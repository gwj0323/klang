"""
Qt6 连续流式音频播放与无爆音实时 DSP 音效引擎
(Continuous QIODevice Pull-Stream + Stateful Scipy SOS Biquad Cascade + Warmed-Up Crossfade + Auto-Headroom Anti-Clipping)

核心防爆音设计：
1. 零中断连续硬件流 (ContinuousDspStreamDevice)：
   全程使用单一 QAudioSink 连续拉取音频流，切换音效预设或高频拖动均衡器/混响滑块时绝不调用 stop()/start()，彻底杜绝硬件缓冲中断引起的咔哒爆音。
2. 状态保持型双二阶滤波器 + 预热双滤波器余弦交叉淡化 (Warmed-Up Dual-SOS Crossfade)：
   基于 scipy.signal.sosfilt 保持块间精确 IIR 延迟状态 (zi)；当用户调节频段增益时，新滤波器先在历史输入样本上预热状态，再与旧滤波器输出进行 2048 样本 (约 46ms) 的余弦平滑交叉淡化，实现零阶跃瞬态。
3. 自动动态余量前置衰减 (Auto-Headroom Pre-Gain) + 柔膝母带限幅：
   针对高响度母带音频（如 0 dBFS 流行曲目），根据正向 EQ 增益与混响深度自动计算前置衰减并在块内逐样本平滑插值，结合温润的高频阻尼环状混响缓冲，彻底消除低频过载削波失真。
"""

import math
from pathlib import Path
from typing import Optional, List, Dict, Tuple
import numpy as np
from scipy.signal import sosfilt

from PyQt6.QtCore import (
    QObject,
    QTimer,
    QElapsedTimer,
    pyqtSignal,
    QIODevice,
    QUrl,
)
from PyQt6.QtMultimedia import (
    QAudioFormat,
    QAudioSink,
    QAudioDecoder,
    QMediaDevices,
    QMediaPlayer,
    QAudioOutput,
)

from qt6_player.data.tracks import NordicTrack


EQ_BAND_FREQS: List[float] = [60.0, 230.0, 910.0, 3600.0, 12000.0]
EQ_BAND_LABELS: List[str] = ["60Hz", "230Hz", "910Hz", "3.6kHz", "12kHz"]

SOUND_PROFILES: Dict[str, Tuple[List[float], float]] = {
    # profile_name: ([60Hz, 230Hz, 910Hz, 3.6kHz, 12kHz] in dB, reverb_mix 0..1)
    "原声自然": ([0.0, 0.0, 0.0, 0.0, 0.0], 0.03),
    "北欧厅堂": ([3.0, 1.5, -1.0, 2.5, 4.5], 0.42),
    "温暖人声": ([-2.0, 2.5, 5.5, 3.0, 1.0], 0.14),
    "深沉低音": ([7.5, 4.5, -1.5, 1.0, 2.0], 0.10),
    "清澈器乐": ([-2.0, -1.0, 1.5, 5.0, 7.0], 0.24),
}


class NordicDspProcessor:
    """
    无爆音连续状态 5 段双二阶滤波器 (SOS Cascade) + 环状立体声阻尼厅堂混响处理器
    """

    HIST_LEN = 512
    REV_BUF_LEN = 32768  # 2^15 立体声样本 (~743ms @ 44.1kHz)
    REV_MASK = 32767

    def __init__(self, sample_rate: int = 44100):
        self.sample_rate = sample_rate
        self.gains_db: List[float] = [0.0, 0.0, 0.0, 0.0, 0.0]
        self.reverb_mix: float = 0.03

        self._cur_pre_gain: float = 0.82
        self._target_pre_gain: float = 0.82
        self._cur_rev_mix: float = 0.03
        self._target_rev_mix: float = 0.03

        self._sos: np.ndarray = self._build_sos(self.gains_db)
        self._pending_sos: Optional[np.ndarray] = None
        # 5 个二阶节 × 2 声道 × 2 状态变量 -> sosfilt 要求 zi 形状为 (n_sections, 2_channels, 2_states) 的转置或 (n_sections, 2, 2)
        # 注意 scipy.signal.sosfilt 对输入 shape (2, N) 且 axis=-1 时，zi 的形状为 (n_sections, 2, 2)
        self._zi: np.ndarray = np.zeros((5, 2, 2), dtype=np.float64)
        self._input_hist: np.ndarray = np.zeros((2, self.HIST_LEN), dtype=np.float64)

        # 连续环状立体声混响延迟线与高频阻尼状态
        self._rev_ring: np.ndarray = np.zeros((2, self.REV_BUF_LEN), dtype=np.float64)
        self._rev_write_pos: int = 0
        self._rev_lp_state: np.ndarray = np.zeros(2, dtype=np.float64)

    def reset_state(self) -> None:
        self._zi.fill(0.0)
        self._input_hist.fill(0.0)
        self._rev_ring.fill(0.0)
        self._rev_write_pos = 0
        self._rev_lp_state.fill(0.0)
        self._cur_pre_gain = self._target_pre_gain
        self._cur_rev_mix = self._target_rev_mix
        if self._pending_sos is not None:
            self._sos = self._pending_sos
            self._pending_sos = None

    def set_parameters(self, gains_db: List[float], reverb_mix: float) -> None:
        clamped_gains = [max(-12.0, min(12.0, float(g))) for g in (gains_db + [0.0] * 5)[:5]]
        clamped_rev = max(0.0, min(1.0, float(reverb_mix)))

        self.gains_db = clamped_gains
        self.reverb_mix = clamped_rev
        self._target_rev_mix = clamped_rev

        # 计算自动防削波前置余量衰减 (Auto-Headroom Pre-Gain)：正向增益越高，自动预留越充足的防削波余量
        max_boost = max(0.0, max(clamped_gains))
        pos_sum = sum(max(0.0, g) for g in clamped_gains)
        atten_db = -(max_boost * 0.85 + max(0.0, pos_sum - max_boost) * 0.22 + clamped_rev * 2.2 + 1.6)
        self._target_pre_gain = float(10.0 ** (atten_db / 20.0))

        new_sos = self._build_sos(clamped_gains)
        if not np.allclose(new_sos, self._sos, atol=1e-5):
            self._pending_sos = new_sos

    def _build_sos(self, gains_db: List[float]) -> np.ndarray:
        fs = float(self.sample_rate)
        sos_list = []
        for idx, (f0, db) in enumerate(zip(EQ_BAND_FREQS, gains_db)):
            fc = min(f0, fs * 0.44)
            if abs(db) < 0.05:
                sos_list.append([1.0, 0.0, 0.0, 1.0, 0.0, 0.0])
                continue

            A = 10.0 ** (db / 40.0)
            w0 = 2.0 * math.pi * fc / fs
            cos_w0 = math.cos(w0)
            sin_w0 = math.sin(w0)

            if idx == 0:
                # 低架滤波器 (Low-Shelf, S=0.75 温润斜率)
                alpha = sin_w0 / 2.0 * math.sqrt((A + 1.0 / A) * (1.0 / 0.75 - 1.0) + 2.0)
                two_sqrt_a_alpha = 2.0 * math.sqrt(A) * alpha
                b0 = A * ((A + 1.0) - (A - 1.0) * cos_w0 + two_sqrt_a_alpha)
                b1 = 2.0 * A * ((A - 1.0) - (A + 1.0) * cos_w0)
                b2 = A * ((A + 1.0) - (A - 1.0) * cos_w0 - two_sqrt_a_alpha)
                a0 = (A + 1.0) + (A - 1.0) * cos_w0 + two_sqrt_a_alpha
                a1 = -2.0 * ((A - 1.0) + (A + 1.0) * cos_w0)
                a2 = (A + 1.0) + (A - 1.0) * cos_w0 - two_sqrt_a_alpha
            elif idx == 4:
                # 高架滤波器 (High-Shelf)
                alpha = sin_w0 / 2.0 * math.sqrt((A + 1.0 / A) * (1.0 / 0.75 - 1.0) + 2.0)
                two_sqrt_a_alpha = 2.0 * math.sqrt(A) * alpha
                b0 = A * ((A + 1.0) + (A - 1.0) * cos_w0 + two_sqrt_a_alpha)
                b1 = -2.0 * A * ((A - 1.0) + (A + 1.0) * cos_w0)
                b2 = A * ((A + 1.0) + (A - 1.0) * cos_w0 - two_sqrt_a_alpha)
                a0 = (A + 1.0) - (A - 1.0) * cos_w0 + two_sqrt_a_alpha
                a1 = 2.0 * ((A - 1.0) - (A + 1.0) * cos_w0)
                a2 = (A + 1.0) - (A - 1.0) * cos_w0 - two_sqrt_a_alpha
            else:
                # 参量峰值滤波器 (Peaking EQ, Q=1.05 宽广自然音乐曲线)
                Q = 1.05
                alpha = sin_w0 / (2.0 * Q)
                b0 = 1.0 + alpha * A
                b1 = -2.0 * cos_w0
                b2 = 1.0 - alpha * A
                a0 = 1.0 + alpha / A
                a1 = -2.0 * cos_w0
                a2 = 1.0 - alpha / A

            sos_list.append([b0 / a0, b1 / a0, b2 / a0, 1.0, a1 / a0, a2 / a0])

        return np.array(sos_list, dtype=np.float64)

    def process_block(self, stereo_in: np.ndarray) -> np.ndarray:
        """
        处理形状为 (2, N) 的浮点立体声音频块，返回 (2, N) 的无爆音滤波输出
        """
        n = stereo_in.shape[1]
        if n == 0:
            return stereo_in

        # 1. 前置防削波增益逐样本线性斜坡插值（彻底消除增益突变台阶）
        if abs(self._cur_pre_gain - self._target_pre_gain) > 1e-4:
            gain_ramp = np.linspace(self._cur_pre_gain, self._target_pre_gain, n, dtype=np.float64)
            self._cur_pre_gain = self._target_pre_gain
            x = stereo_in * gain_ramp[np.newaxis, :]
        else:
            x = stereo_in * self._cur_pre_gain

        # 2. 状态保持 5 段双二阶滤波（若参数刚发生变更，则执行历史预热 + 双滤波器余弦交叉淡化）
        pending = self._pending_sos
        if pending is not None:
            self._pending_sos = None
            out_old, self._zi = sosfilt(self._sos, x, axis=-1, zi=self._zi)
            _, zi_warm = sosfilt(pending, self._input_hist, axis=-1, zi=np.zeros((5, 2, 2), dtype=np.float64))
            out_new, zi_new = sosfilt(pending, x, axis=-1, zi=zi_warm)

            fade_in = (0.5 - 0.5 * np.cos(np.linspace(0.0, math.pi, n, dtype=np.float64)))[np.newaxis, :]
            eq_out = out_old * (1.0 - fade_in) + out_new * fade_in
            self._sos = pending
            self._zi = zi_new
        else:
            eq_out, self._zi = sosfilt(self._sos, x, axis=-1, zi=self._zi)

        # 更新 512 样本历史缓存供下一次参数调整预热使用
        if n >= self.HIST_LEN:
            self._input_hist[:] = x[:, -self.HIST_LEN :]
        else:
            self._input_hist[:, :-n] = self._input_hist[:, n:]
            self._input_hist[:, -n:] = x

        # 3. 连续环状缓冲立体声厅堂混响（跨音频块不断尾，带高频柔化阻尼）
        idx_vec = (self._rev_write_pos + np.arange(n, dtype=np.int32)) & self.REV_MASK
        # 先将当前滤波干声写入环状延迟线（带极温和的早期反射交叉反馈）
        fb_read_idx = (idx_vec - 3925) & self.REV_MASK
        self._rev_ring[0, idx_vec] = eq_out[0] + self._rev_ring[1, fb_read_idx] * 0.14
        self._rev_ring[1, idx_vec] = eq_out[1] + self._rev_ring[0, fb_read_idx] * 0.14
        self._rev_write_pos = (self._rev_write_pos + n) & self.REV_MASK

        if self._cur_rev_mix > 0.005 or self._target_rev_mix > 0.005:
            if abs(self._cur_rev_mix - self._target_rev_mix) > 1e-4:
                rev_ramp = np.linspace(self._cur_rev_mix, self._target_rev_mix, n, dtype=np.float64)[np.newaxis, :]
                self._cur_rev_mix = self._target_rev_mix
            else:
                rev_ramp = self._cur_rev_mix

            wet_l = np.zeros(n, dtype=np.float64)
            wet_r = np.zeros(n, dtype=np.float64)
            taps = (
                (1279, 0.36, False),
                (2337, 0.28, True),
                (3925, 0.22, False),
                (6042, 0.16, True),
                (9305, 0.11, False),
                (13980, 0.07, True),
            )
            for delay_samples, tap_gain, cross in taps:
                r_idx = (idx_vec - delay_samples) & self.REV_MASK
                if cross:
                    wet_l += self._rev_ring[1, r_idx] * tap_gain
                    wet_r += self._rev_ring[0, r_idx] * tap_gain
                else:
                    wet_l += self._rev_ring[0, r_idx] * tap_gain
                    wet_r += self._rev_ring[1, r_idx] * tap_gain

            # 简单一阶低通柔化混响高频毛刺（模拟木质音乐厅温暖反射）
            wet_stack = np.vstack((wet_l, wet_r))
            wet_smooth = wet_stack * 0.65
            wet_smooth[:, 1:] += wet_stack[:, :-1] * 0.35

            out = eq_out * (1.0 - rev_ramp * 0.24) + wet_smooth * (rev_ramp * 0.68)
        else:
            out = eq_out

        # 4. 透明柔膝防削波母带限幅器（0.80 以下完全线性，0.80~0.98 平滑渐近，绝不产生硬削波爆音）
        abs_out = np.abs(out)
        over_mask = abs_out > 0.80
        if np.any(over_mask):
            sign = np.sign(out[over_mask])
            compressed = 0.80 + 0.17 * np.tanh((abs_out[over_mask] - 0.80) / 0.17)
            out[over_mask] = sign * compressed

        return out


class ContinuousDspStreamDevice(QIODevice):
    """
    自定义连续拉流 QIODevice：
    供 QAudioSink 持续拉取经过 5 段均衡器与混响处理的立体声 PCM 数据，
    无需在调音或切预设时调用 stop()/start()，实现 100% 零爆音平滑过渡。
    """

    BLOCK_FRAMES = 2048  # 每次 DSP 运算块大小 (约 46.4ms @ 44.1kHz)

    def __init__(self, engine: "NordicAudioEngine"):
        super().__init__(engine)
        self.engine = engine
        self._out_fifo = bytearray()
        self._last_tail_frame = np.zeros((2, 1), dtype=np.float64)
        self._crossfade_on_next_block: bool = False

    def start_stream(self) -> None:
        self._out_fifo.clear()
        if not self.isOpen():
            self.open(QIODevice.OpenModeFlag.ReadOnly)

    def request_smooth_transition(self) -> None:
        """在跳转进度 (seek) 时清空未消费 FIFO 并标记对下一块做 8ms 抗咔哒交叉淡化"""
        self._out_fifo.clear()
        self._crossfade_on_next_block = True

    def bytesAvailable(self) -> int:
        # 始终向 QAudioSink 报告充足可用字节以保持连续拉流
        return max(len(self._out_fifo), 16384) + super().bytesAvailable()

    def isSequential(self) -> bool:
        return True

    def readData(self, maxlen: int) -> bytes:
        if maxlen <= 0:
            return b""
        while len(self._out_fifo) < maxlen:
            chunk_bytes = self._render_next_dsp_block()
            if not chunk_bytes:
                break
            self._out_fifo.extend(chunk_bytes)

        take = min(maxlen, len(self._out_fifo))
        if take <= 0:
            return bytes(maxlen)
        data = bytes(self._out_fifo[:take])
        del self._out_fifo[:take]
        return data

    def _render_next_dsp_block(self) -> bytes:
        eng = self.engine
        n_frames = self.BLOCK_FRAMES

        if not eng.is_playing and eng._preview_frames_remaining <= 0:
            return bytes(n_frames * 2 * eng._channels)

        raw_buf = eng._decoded_raw_bytes
        total_avail_frames = len(raw_buf) // 4

        if total_avail_frames <= 64:
            return bytes(n_frames * 2 * eng._channels)

        rate = eng.playback_rate
        cur_frame = eng._frame_cursor

        if cur_frame >= total_avail_frames:
            if eng._decoder_finished:
                eng._notify_eof_from_stream()
            return bytes(n_frames * 2 * eng._channels)

        # 从已解码立体声缓冲中读取 n_frames 帧（支持 0.75x ~ 2.0x 倍速线性插值采样）
        if abs(rate - 1.0) < 0.01:
            end_frame = min(total_avail_frames, cur_frame + n_frames)
            actual = end_frame - cur_frame
            byte_slice = raw_buf[cur_frame * 4 : end_frame * 4]
            arr = np.frombuffer(byte_slice, dtype=np.int16).astype(np.float64) / 32768.0
            arr = arr.reshape(-1, 2).T  # shape (2, actual)
            if actual < n_frames:
                pad = np.zeros((2, n_frames - actual), dtype=np.float64)
                stereo_block = np.hstack((arr, pad))
            else:
                stereo_block = arr
            eng._frame_cursor = end_frame
        else:
            step = rate
            float_indices = cur_frame + np.arange(n_frames, dtype=np.float64) * step
            valid_mask = float_indices < (total_avail_frames - 1)
            clamped_idx = np.clip(float_indices, 0, max(0, total_avail_frames - 2))
            idx0 = clamped_idx.astype(np.int32)
            frac = clamped_idx - idx0

            min_i = int(idx0[0])
            max_i = min(total_avail_frames, int(idx0[-1]) + 2)
            sub_raw = raw_buf[min_i * 4 : max_i * 4]
            sub_arr = (np.frombuffer(sub_raw, dtype=np.int16).astype(np.float64) / 32768.0).reshape(-1, 2)
            rel0 = np.clip(idx0 - min_i, 0, max(0, len(sub_arr) - 2))
            s0 = sub_arr[rel0]
            s1 = sub_arr[rel0 + 1]
            interp = (s0 + (s1 - s0) * frac[:, np.newaxis]) * valid_mask[:, np.newaxis]
            stereo_block = interp.T.copy()
            eng._frame_cursor = min(total_avail_frames, int(cur_frame + n_frames * step))

        if eng._preview_frames_remaining > 0:
            eng._preview_frames_remaining = max(0, eng._preview_frames_remaining - n_frames)

        # 通过无爆音 5 段 Biquad EQ + 环状立体声混响处理器
        proc_stereo = eng.dsp.process_block(stereo_block)

        # 若刚发生跳转 (seek)，对前 256 样本 (约 5.8ms) 施加平滑斜坡防咔哒过渡
        if self._crossfade_on_next_block:
            self._crossfade_on_next_block = False
            cf_len = min(256, n_frames)
            ramp = np.linspace(0.0, 1.0, cf_len, dtype=np.float64)[np.newaxis, :]
            proc_stereo[:, :cf_len] = self._last_tail_frame * (1.0 - ramp) + proc_stereo[:, :cf_len] * ramp

        self._last_tail_frame = proc_stereo[:, -1:].copy()

        if eng._channels == 2:
            interleaved = np.empty(n_frames * 2, dtype=np.int16)
            interleaved[0::2] = np.clip(proc_stereo[0] * 32767.0, -32767.0, 32767.0).astype(np.int16)
            interleaved[1::2] = np.clip(proc_stereo[1] * 32767.0, -32767.0, 32767.0).astype(np.int16)
            return interleaved.tobytes()
        else:
            mono = (proc_stereo[0] + proc_stereo[1]) * 0.5
            pcm_mono = np.clip(mono * 32767.0, -32767.0, 32767.0).astype(np.int16)
            return pcm_mono.tobytes()


class NordicAudioEngine(QObject):
    trackChanged = pyqtSignal(object)                   # NordicTrack
    playStateChanged = pyqtSignal(bool)                 # is_playing
    timeUpdated = pyqtSignal(float, float, str)         # current_time, duration, source
    volumeChanged = pyqtSignal(float, bool)             # volume (0..1), is_muted
    playbackRateChanged = pyqtSignal(float)             # rate (0.75 ~ 2.0)
    soundProfileChanged = pyqtSignal(str, list, float)  # profile_name, gains_db(5), reverb_mix
    sleepTimerUpdated = pyqtSignal(int)                 # remaining_seconds (0 = off)
    trackEnded = pyqtSignal()

    def __init__(self, parent: Optional[QObject] = None):
        super().__init__(parent)
        self.current_track: Optional[NordicTrack] = None
        self.current_time: float = 0.0
        self.duration: float = 76.0
        self.is_playing: bool = False
        self.is_muted: bool = False
        self.volume: float = 0.72
        self.playback_rate: float = 1.0

        self.sound_profile: str = "原声自然"
        self.eq_gains_db: List[float] = [0.0, 0.0, 0.0, 0.0, 0.0]
        self.reverb_mix: float = 0.03

        self._sample_rate = 44100
        self._channels = 2
        self.dsp = NordicDspProcessor(sample_rate=self._sample_rate)

        self._using_local_file: bool = False
        self._decoded_raw_bytes = bytearray()
        self._decoder_finished: bool = False
        self._decoder_failed: bool = False
        self._frame_cursor: int = 0
        self._preview_frames_remaining: int = 0
        self._eof_triggered: bool = False

        # 睡眠定时器
        self.sleep_remaining_sec: int = 0
        self._sleep_timer = QTimer(self)
        self._sleep_timer.setInterval(1000)
        self._sleep_timer.timeout.connect(self._on_sleep_tick)

        # 1. 备用 QMediaPlayer（仅当某个冷门封装格式 QAudioDecoder 解码失败时作为后备）
        self._media_player = QMediaPlayer(self)
        self._audio_output = QAudioOutput(self)
        self._audio_output.setVolume(0.0)
        self._media_player.setAudioOutput(self._audio_output)
        self._media_player.durationChanged.connect(self._on_media_duration_changed)

        # 2. 本地音频 44.1kHz 立体声高速解码器
        self._decoder = QAudioDecoder(self)
        dec_fmt = QAudioFormat()
        dec_fmt.setSampleRate(self._sample_rate)
        dec_fmt.setChannelCount(2)
        dec_fmt.setSampleFormat(QAudioFormat.SampleFormat.Int16)
        self._decoder.setAudioFormat(dec_fmt)
        self._decoder.bufferReady.connect(self._on_decoder_buffer_ready)
        self._decoder.finished.connect(self._on_decoder_finished)
        self._decoder.error.connect(self._on_decoder_error)

        # 3. 60fps 进度同步定时器
        self._tick_timer = QTimer(self)
        self._tick_timer.setInterval(16)
        self._tick_timer.timeout.connect(self._on_tick)

        # 4. 连续无爆音 QAudioSink + ContinuousDspStreamDevice
        self._audio_sink: Optional[QAudioSink] = None
        self._stream_device = ContinuousDspStreamDevice(self)
        self._init_continuous_sink()

    def _init_continuous_sink(self) -> None:
        try:
            device = QMediaDevices.defaultAudioOutput()
            if device.isNull():
                return
            fmt = QAudioFormat()
            fmt.setSampleRate(self._sample_rate)
            fmt.setChannelCount(2)
            fmt.setSampleFormat(QAudioFormat.SampleFormat.Int16)
            if device.isFormatSupported(fmt):
                self._channels = 2
            else:
                fmt.setChannelCount(1)
                self._channels = 1

            self._audio_sink = QAudioSink(device, fmt, self)
            # 设置 32KB 充裕硬件环形缓冲，彻底防止系统调度抖动引起欠载爆音
            self._audio_sink.setBufferSize(32768)
            self._audio_sink.setVolume(self.volume)
            self._stream_device.start_stream()
            self._audio_sink.start(self._stream_device)
        except Exception:
            self._audio_sink = None

    def _ensure_sink_running(self) -> None:
        if self._audio_sink is None:
            return
        if self._audio_sink.state() in (
            self._audio_sink.state().StoppedState,
            self._audio_sink.state().IdleState,
        ):
            self._stream_device.start_stream()
            self._audio_sink.start(self._stream_device)

    def _on_decoder_buffer_ready(self) -> None:
        try:
            while self._decoder.bufferAvailable():
                buf = self._decoder.read()
                cnt = buf.byteCount()
                if cnt > 0:
                    ptr = buf.constData()
                    self._decoded_raw_bytes.extend(bytes(ptr[:cnt]))
        except Exception:
            pass

    def _on_decoder_finished(self) -> None:
        self._decoder_finished = True
        total_frames = len(self._decoded_raw_bytes) // 4
        if total_frames > 0:
            self.duration = total_frames / float(self._sample_rate)
            if self.current_track is not None:
                self.current_track.duration = self.duration
            self.timeUpdated.emit(self.current_time, self.duration, "init")

    def _on_decoder_error(self, err) -> None:
        if len(self._decoded_raw_bytes) == 0 and self.current_track and self.current_track.file_path:
            self._decoder_failed = True
            url = QUrl.fromLocalFile(self.current_track.file_path)
            self._media_player.setSource(url)
            eff = 0.0 if self.is_muted else self.volume
            self._audio_output.setVolume(eff)
            if self.is_playing:
                self._media_player.play()

    def _on_media_duration_changed(self, duration_ms: int) -> None:
        if self._using_local_file and duration_ms > 0 and not self._decoder_finished:
            self.duration = duration_ms / 1000.0
            if self.current_track is not None:
                self.current_track.duration = self.duration
            self.timeUpdated.emit(self.current_time, self.duration, "init")

    def _build_synth_track_pcm(self, track: NordicTrack) -> bytearray:
        """为内置示例曲目预先生成平滑立体声泛音和弦流，使其同样通过连续 DSP 管线播放"""
        dur = max(10.0, float(track.duration))
        chords = track.chords or [[261.63, 329.63, 392.00, 493.88]]
        chord_span = 4.6
        n_steps = int(math.ceil(dur / chord_span))
        step_samples = int(self._sample_rate * chord_span)
        t = np.linspace(0.0, chord_span, step_samples, endpoint=False, dtype=np.float32)

        out_bytes = bytearray()
        for s_i in range(n_steps):
            freqs = chords[s_i % len(chords)]
            sig_l = np.zeros_like(t)
            sig_r = np.zeros_like(t)
            for idx, freq in enumerate(freqs):
                stagger = idx * 0.10
                t_sh = np.maximum(0.0, t - stagger)
                gate = (t >= stagger).astype(np.float32)
                attack = np.minimum(1.0, t_sh / 0.16)
                decay = np.exp(-t_sh * 0.92)
                # 尾端 120ms 余弦平滑收尾，保证和弦切换零咔哒声
                fade_tail = np.clip((chord_span - t) / 0.12, 0.0, 1.0)
                env = attack * decay * gate * fade_tail

                sub_w = 0.42 * np.sin(2.0 * np.pi * (freq * 0.5) * t_sh)
                fund = np.sin(2.0 * np.pi * freq * t_sh)
                oct_w = 0.28 * np.sin(2.0 * np.pi * (freq * 2.0) * t_sh) * np.exp(-t_sh * 1.5)
                air_w = 0.18 * np.sin(2.0 * np.pi * (freq * 3.0) * t_sh) * np.exp(-t_sh * 2.1)

                voice = env * (sub_w + fund + oct_w + air_w)
                w_gain = max(0.06, 0.20 - idx * 0.025)
                pan = 0.35 + 0.30 * (idx / max(1, len(freqs) - 1))
                sig_l += w_gain * (1.0 - pan * 0.45) * voice
                sig_r += w_gain * (0.55 + pan * 0.45) * voice

            interleaved = np.empty(step_samples * 2, dtype=np.int16)
            interleaved[0::2] = np.clip(sig_l * 24000.0, -32000, 32000).astype(np.int16)
            interleaved[1::2] = np.clip(sig_r * 24000.0, -32000, 32000).astype(np.int16)
            out_bytes.extend(interleaved.tobytes())

        max_bytes = int(dur * self._sample_rate) * 4
        return out_bytes[:max_bytes]

    def load_track(self, track: NordicTrack, auto_play: bool = False) -> None:
        was_playing = self.is_playing
        self.pause()
        self.current_track = track
        self.duration = max(1.0, track.duration)
        self.current_time = 0.0
        self._frame_cursor = 0
        self._preview_frames_remaining = 0
        self._eof_triggered = False
        self._decoder_finished = False
        self._decoder_failed = False

        self._decoder.stop()
        self._decoded_raw_bytes.clear()
        self.dsp.reset_state()
        self._stream_device.request_smooth_transition()

        if track.file_path and Path(track.file_path).is_file():
            self._using_local_file = True
            url = QUrl.fromLocalFile(track.file_path)
            self._audio_output.setVolume(0.0)
            self._decoder.setSource(url)
            self._decoder.start()
        else:
            self._using_local_file = False
            self._media_player.setSource(QUrl())
            self._decoded_raw_bytes = self._build_synth_track_pcm(track)
            self._decoder_finished = True

        self.trackChanged.emit(track)
        self.timeUpdated.emit(0.0, self.duration, "init")

        if auto_play or was_playing:
            self.play()

    def play(self) -> None:
        if self.is_playing or self.current_track is None:
            return
        if self.current_time >= self.duration - 0.15:
            self.current_time = 0.0
            self._frame_cursor = 0
            self._eof_triggered = False

        self._preview_frames_remaining = 0
        self.is_playing = True
        self._ensure_sink_running()
        if self._using_local_file and self._decoder_failed:
            self._media_player.play()
        self._tick_timer.start()
        self.playStateChanged.emit(True)

    def pause(self) -> None:
        if not self.is_playing:
            return
        self.is_playing = False
        self._preview_frames_remaining = 0
        self._tick_timer.stop()
        if self._using_local_file and self._decoder_failed:
            self._media_player.pause()
        self.playStateChanged.emit(False)

    def toggle_play(self) -> None:
        if self.is_playing:
            self.pause()
        else:
            self.play()

    def seek(self, target_seconds: float, auto_play_on_seek: bool = True, source: str = "seek") -> None:
        clamped = max(0.0, min(self.duration, float(target_seconds)))
        self.current_time = clamped
        self._eof_triggered = False
        self._frame_cursor = max(0, int(clamped * self._sample_rate))
        self._stream_device.request_smooth_transition()

        if self._using_local_file and self._decoder_failed:
            self._media_player.setPosition(int(clamped * 1000))

        if auto_play_on_seek and not self.is_playing:
            self.play()

        self.timeUpdated.emit(self.current_time, self.duration, source)

    def set_volume(self, volume: float) -> None:
        self.volume = max(0.0, min(1.0, float(volume)))
        if self.volume > 0.01 and self.is_muted:
            self.is_muted = False
        eff = 0.0 if self.is_muted else self.volume
        if self._audio_sink is not None:
            self._audio_sink.setVolume(eff)
        if self._decoder_failed:
            self._audio_output.setVolume(eff)
        self.volumeChanged.emit(self.volume, self.is_muted)

    def toggle_mute(self) -> None:
        self.is_muted = not self.is_muted
        eff = 0.0 if self.is_muted else self.volume
        if self._audio_sink is not None:
            self._audio_sink.setVolume(eff)
        if self._decoder_failed:
            self._audio_output.setVolume(eff)
        self.volumeChanged.emit(self.volume, self.is_muted)

    def set_playback_rate(self, rate: float) -> None:
        self.playback_rate = max(0.5, min(2.0, float(rate)))
        if self._decoder_failed:
            self._media_player.setPlaybackRate(self.playback_rate)
        self.playbackRateChanged.emit(self.playback_rate)

    def cycle_playback_rate(self) -> float:
        rates = [0.75, 1.0, 1.25, 1.5, 2.0]
        try:
            idx = rates.index(self.playback_rate)
            next_r = rates[(idx + 1) % len(rates)]
        except ValueError:
            next_r = 1.0
        self.set_playback_rate(next_r)
        return next_r

    def trigger_dsp_live_refresh(self, play_preview_if_paused: bool = False) -> None:
        """
        由于已采用 ContinuousDspStreamDevice 连续拉流，正在播放时修改 DSP 参数会自动在下一个 46ms 块内平滑交叉淡化，
        完全不需要中断或重启音频流！仅当处于暂停状态且用户主动点击「试听当前音效」时，开放 1.5 秒平滑预览窗。
        """
        if not self.is_playing and play_preview_if_paused:
            self._ensure_sink_running()
            self._preview_frames_remaining = int(self._sample_rate * 1.5)

    def set_sound_profile(self, profile: str, trigger_preview: bool = False) -> None:
        self.sound_profile = profile
        if profile in SOUND_PROFILES:
            gains, rev = SOUND_PROFILES[profile]
            self.eq_gains_db = list(gains)
            self.reverb_mix = rev
        self.dsp.set_parameters(self.eq_gains_db, self.reverb_mix)
        self.soundProfileChanged.emit(self.sound_profile, list(self.eq_gains_db), self.reverb_mix)
        if trigger_preview and not self.is_playing:
            self.trigger_dsp_live_refresh(play_preview_if_paused=True)

    def set_custom_eq(self, gains_db: List[float], reverb_mix: float, trigger_preview: bool = False) -> None:
        self.sound_profile = "自定义"
        self.eq_gains_db = [max(-12.0, min(12.0, float(g))) for g in (gains_db + [0.0] * 5)[:5]]
        self.reverb_mix = max(0.0, min(1.0, float(reverb_mix)))
        self.dsp.set_parameters(self.eq_gains_db, self.reverb_mix)
        self.soundProfileChanged.emit(self.sound_profile, list(self.eq_gains_db), self.reverb_mix)
        if trigger_preview and not self.is_playing:
            self.trigger_dsp_live_refresh(play_preview_if_paused=True)

    def set_sleep_timer(self, minutes: int) -> None:
        if minutes <= 0:
            self.sleep_remaining_sec = 0
            self._sleep_timer.stop()
        else:
            self.sleep_remaining_sec = minutes * 60
            self._sleep_timer.start()
        self.sleepTimerUpdated.emit(self.sleep_remaining_sec)

    def _on_sleep_tick(self) -> None:
        if self.sleep_remaining_sec > 0:
            self.sleep_remaining_sec -= 1
            self.sleepTimerUpdated.emit(self.sleep_remaining_sec)
            if self.sleep_remaining_sec <= 0:
                self._sleep_timer.stop()
                self.pause()

    def _notify_eof_from_stream(self) -> None:
        if not self._eof_triggered and self.is_playing:
            self._eof_triggered = True
            QTimer.singleShot(0, self._handle_eof_on_main_thread)

    def _handle_eof_on_main_thread(self) -> None:
        if self.is_playing:
            self.pause()
            self.trackEnded.emit()

    def _on_tick(self) -> None:
        if not self.is_playing or self.current_track is None:
            return

        if self._using_local_file and self._decoder_failed:
            pos_ms = self._media_player.position()
            dur_ms = self._media_player.duration()
            if dur_ms > 0:
                self.duration = dur_ms / 1000.0
            self.current_time = pos_ms / 1000.0
            self.timeUpdated.emit(self.current_time, self.duration, "tick")
            return

        self.current_time = min(self.duration, self._frame_cursor / float(self._sample_rate))
        self.timeUpdated.emit(self.current_time, self.duration, "tick")
