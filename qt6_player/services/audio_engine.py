import logging as _logging
import math
import sys
from pathlib import Path
from typing import Optional, List, Dict, Tuple
import numpy as np
from scipy.signal import sosfilt, butter, lfilter, lfiltic

from qt6_player.services.headphone_eq import HEADPHONE_PROFILES, build_headphone_sos

from PyQt6.QtCore import (
    Qt,
    QObject,
    QTimer,
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
    "默认原声": ([0.0, 0.0, 0.0, 0.0, 0.0], 0.03),
    "流行人声": ([-2.0, 2.5, 5.5, 3.0, 1.0], 0.14),
    "低音增强": ([7.5, 4.5, -1.5, 1.0, 2.0], 0.10),
    "清澈高音": ([-2.0, -1.0, 1.5, 5.0, 7.0], 0.24),
    "现场空间": ([3.0, 1.5, -1.0, 2.5, 4.5], 0.42),
}
# 兼容旧预设名称
SOUND_PROFILES["原声自然"] = SOUND_PROFILES["默认原声"]
SOUND_PROFILES["北欧厅堂"] = SOUND_PROFILES["现场空间"]
SOUND_PROFILES["温暖人声"] = SOUND_PROFILES["流行人声"]
SOUND_PROFILES["深沉低音"] = SOUND_PROFILES["低音增强"]
SOUND_PROFILES["清澈器乐"] = SOUND_PROFILES["清澈高音"]


def compute_pcm_acoustic_dna(
    raw_bytes: bytearray,
    sample_rate: int = 44100,
    start_frame: int = 0,
    end_frame: int = 0,
) -> Optional[Dict[str, float]]:
    """
    完全离线提取曲目的 4 维声学 DNA 指纹：
    - bpm: 节拍速度 (68 ~ 168 BPM，基于包络瞬态自相关分析)
    - energy: 能量密度 (0.0 ~ 1.0)
    - brightness: 高频明亮度 (0.0 ~ 1.0)
    - warmth: 中低频温暖度 (0.0 ~ 1.0)
    耗时 < 3ms，用于「离线声学 DNA 情绪漫游」无缝智能选曲。
    """
    total_frames = len(raw_bytes) // 4
    if total_frames < sample_rate * 4:
        return None

    s_f = max(0, start_frame)
    e_f = min(total_frames, end_frame) if end_frame > s_f else total_frames
    span = e_f - s_f
    if span < sample_rate * 3:
        return None

    # 截取曲目黄金中段 12 秒（避开前奏静音）
    window_frames = min(span, sample_rate * 12)
    mid_start = s_f + max(0, (span - window_frames) // 3)
    mid_end = mid_start + window_frames

    try:
        pcm = np.frombuffer(raw_bytes[mid_start * 4 : mid_end * 4], dtype=np.int16).astype(np.float32) / 32768.0
        mono = (pcm[0::2] + pcm[1::2]) * 0.5
        if len(mono) < 4096:
            return None

        # 1. 能量密度 (RMS Energy)
        rms = float(np.sqrt(np.mean(mono * mono)))
        energy = max(0.05, min(0.98, (rms / 0.24) ** 0.65))

        # 2. 频谱明亮度与中低频温暖度 (取 16384 样本做快速 FFT 频段能量比)
        fft_len = min(len(mono), 16384)
        spec = np.abs(np.fft.rfft(mono[:fft_len] * np.hanning(fft_len)))
        freqs = np.fft.rfftfreq(fft_len, d=1.0 / float(sample_rate))
        total_spec_e = float(np.sum(spec)) + 1e-6
        low_warm_e = float(np.sum(spec[(freqs >= 80.0) & (freqs <= 750.0)]))
        high_bright_e = float(np.sum(spec[freqs >= 2200.0]))

        warmth = max(0.05, min(0.98, (low_warm_e / total_spec_e) * 1.45))
        brightness = max(0.05, min(0.98, (high_bright_e / total_spec_e) * 2.35))

        # 3. 节拍速度 BPM (以 512 帧步长计算一阶瞬态能量包络并做 68~168 BPM 自相关峰值检测)
        hop = 512
        env_fps = float(sample_rate) / float(hop)
        n_hops = len(mono) // hop
        bpm = 108.0
        if n_hops >= 64:
            env = np.sqrt(np.mean(mono[: n_hops * hop].reshape(n_hops, hop) ** 2, axis=1))
            onset = np.maximum(0.0, np.diff(env, prepend=env[0]))
            onset = onset - np.mean(onset)
            norm = float(np.sum(onset * onset))
            if norm > 1e-7:
                ac = np.correlate(onset, onset, mode="full")[len(onset) - 1 :]
                min_lag = max(4, int(round(env_fps * 60.0 / 168.0)))
                max_lag = min(len(ac) - 1, int(round(env_fps * 60.0 / 68.0)))
                if max_lag > min_lag:
                    best_lag = min_lag + int(np.argmax(ac[min_lag : max_lag + 1]))
                    bpm = round(float(env_fps * 60.0 / max(1, best_lag)), 1)

        return {
            "bpm": float(max(68.0, min(168.0, bpm))),
            "energy": round(float(energy), 3),
            "brightness": round(float(brightness), 3),
            "warmth": round(float(warmth), 3),
        }
    except Exception:
        return None


class DolbyAtmosSpatializer:
    """
    Dolby Atmos & Spatial Audio 杜比全景声与空间音频双耳渲染引擎
    
    声学特性：
    1. M/S (Mid-Side) 声学解耦与中心人声前置声像重构 (Frontal Pinna Elevation & Crossfeed)
    2. 360° 环绕空间拓展与全通无损相位去相关网络 (Schroeder Allpass Decorrelation)
    3. 天空声道穹顶高度感知陷波滤波器 (8.6kHz Concha Elevation Notch & Air Absorption)
    4. 空间双耳耳廓时间差 (ITD: ~0.25ms) 与频率依存头影效应 (ILD)
    5. 杜比动态声场扩展度 (Spatial Width: 0.8 ~ 1.8) 与高度感知控制 (Spatial Height: 0.0 ~ 1.0)
    """

    MODES = ("off", "spatial_stereo", "dolby_atmos")
    RING_LEN = 8192
    RING_MASK = 8191

    def __init__(self, sample_rate: int = 44100):
        self.sample_rate = sample_rate
        self.mode: str = "dolby_atmos"
        self.spatial_width: float = 1.35
        self.spatial_height: float = 0.65

        self._delay_ring: np.ndarray = np.zeros((3, self.RING_LEN), dtype=np.float64)
        self._write_pos: int = 0
        self._head_shadow_l: float = 0.0
        self._head_shadow_r: float = 0.0

        self._cur_wet_gain: float = 1.0
        self._target_wet_gain: float = 1.0

        self._build_elevation_filters()
        self._build_lfe_filters()

    def reset_state(self) -> None:
        self._delay_ring.fill(0.0)
        self._write_pos = 0
        self._head_shadow_l = 0.0
        self._head_shadow_r = 0.0
        self._elev_zi_l.fill(0.0)
        self._elev_zi_r.fill(0.0)
        self._lfe_zi.fill(0.0)

    def set_mode(self, mode: str) -> None:
        if mode in self.MODES:
            prev_mode = self.mode
            self.mode = mode
            if mode == "off":
                self._target_wet_gain = 0.0
            elif mode == "spatial_stereo":
                self._target_wet_gain = 0.65
            else:  # "dolby_atmos"
                self._target_wet_gain = 1.0
            if prev_mode != mode:
                self._delay_ring.fill(0.0)
                self._head_shadow_l = 0.0
                self._head_shadow_r = 0.0

    def set_parameters(self, width: float, height: float) -> None:
        self.spatial_width = max(0.8, min(2.0, float(width)))
        self.spatial_height = max(0.0, min(1.0, float(height)))

    def _build_elevation_filters(self) -> None:
        fs = float(self.sample_rate)
        f0 = min(8600.0, fs * 0.45)
        Q = 2.2
        w0 = 2.0 * math.pi * f0 / fs
        alpha = math.sin(w0) / (2.0 * Q)
        b0 = 1.0
        b1 = -2.0 * math.cos(w0)
        b2 = 1.0
        a0 = 1.0 + alpha
        a1 = -2.0 * math.cos(w0)
        a2 = 1.0 - alpha
        self._elev_sos = np.array([[b0 / a0, b1 / a0, b2 / a0, 1.0, a1 / a0, a2 / a0]], dtype=np.float64)
        self._elev_zi_l = np.zeros((1, 2), dtype=np.float64)
        self._elev_zi_r = np.zeros((1, 2), dtype=np.float64)

    def _build_lfe_filters(self) -> None:
        fs = float(self.sample_rate)
        fc = min(120.0, fs * 0.45)
        self._lfe_sos = butter(4, fc / (fs * 0.5), btype='low', output='sos')
        self._lfe_zi = np.zeros((self._lfe_sos.shape[0], 2), dtype=np.float64)

    def process_block(self, stereo_in: np.ndarray, target_channels: int = 2) -> np.ndarray:
        n = stereo_in.shape[1]
        if n == 0:
            return stereo_in

        fading = abs(self._cur_wet_gain - self._target_wet_gain) > 1e-4
        if fading:
            wet_ramp = np.linspace(self._cur_wet_gain, self._target_wet_gain, n, dtype=np.float64)
            self._cur_wet_gain = self._target_wet_gain
        else:
            wet_ramp = self._cur_wet_gain

        # 正在淡出（刚切到 off）的这一块仍要走完整渲染，否则湿声会被瞬间切断产生爆音
        if not fading and self._cur_wet_gain <= 0.001 and self._target_wet_gain <= 0.001:
            # 空间声场关闭：多声道输出仍需返回 target_channels 行（前左/前右直通，其余补零）
            if target_channels <= stereo_in.shape[0]:
                return stereo_in
            padded = np.zeros((target_channels, n), dtype=stereo_in.dtype)
            padded[: stereo_in.shape[0]] = stereo_in
            return padded

        L = stereo_in[0]
        R = stereo_in[1]

        # 1. M/S (Mid-Side) 声场分解
        mid = (L + R) * 0.5
        side = (L - R) * 0.5

        # 2. 写入主延迟环形缓冲
        idx_vec = (self._write_pos + np.arange(n, dtype=np.int32)) & self.RING_MASK
        self._delay_ring[0, idx_vec] = mid
        self._delay_ring[1, idx_vec] = side

        # 3. 侧向环绕全通相位漫射 (Schroeder Allpass Decorrelation)
        tap1 = int(round(self.sample_rate * 0.0015))  # 1.5ms
        tap2 = int(round(self.sample_rate * 0.0036))  # 3.6ms
        tap3 = int(round(self.sample_rate * 0.0072))  # 7.2ms

        s_delayed_l = self._delay_ring[1, (idx_vec - tap1) & self.RING_MASK]
        s_delayed_r = self._delay_ring[1, (idx_vec - tap2) & self.RING_MASK]

        # 4. 前置人声声学交叉馈送 (ITD ~0.25ms + 头影衰减 ILD)
        itd_samples = max(1, int(round(self.sample_rate * 0.00025)))  # 0.25ms
        m_cross = self._delay_ring[0, (idx_vec - itd_samples) & self.RING_MASK]
        alpha_shadow = 0.35
        # 一阶低通 y[i] = y[i-1] + a*(x[i]-y[i-1])，用 lfilter 向量化（带状态延续），替代逐采样 Python 循环
        shadow_b = np.array([alpha_shadow], dtype=np.float64)
        shadow_a = np.array([1.0, -(1.0 - alpha_shadow)], dtype=np.float64)
        shadow_l, _ = lfilter(shadow_b, shadow_a, m_cross, zi=lfiltic(shadow_b, shadow_a, [self._head_shadow_l]))
        shadow_r, _ = lfilter(shadow_b, shadow_a, m_cross, zi=lfiltic(shadow_b, shadow_a, [self._head_shadow_r]))
        self._head_shadow_l = float(shadow_l[-1])
        self._head_shadow_r = float(shadow_r[-1])

        # 5. 杜比天空声道高度感知渲染 (Height Elevation Objects)
        if self.mode == "dolby_atmos" and self.spatial_height > 0.05:
            height_gain = self.spatial_height * 0.28
            tap_h_l = (idx_vec - tap3) & self.RING_MASK
            tap_h_r = (idx_vec - (tap3 + tap1)) & self.RING_MASK
            h_raw_l = self._delay_ring[1, tap_h_l] - self._delay_ring[0, tap_h_l] * 0.15
            h_raw_r = -self._delay_ring[1, tap_h_r] - self._delay_ring[0, tap_h_r] * 0.15
            
            h_l, self._elev_zi_l = sosfilt(self._elev_sos, h_raw_l, zi=self._elev_zi_l)
            h_r, self._elev_zi_r = sosfilt(self._elev_sos, h_raw_r, zi=self._elev_zi_r)
        else:
            h_l = np.zeros(n, dtype=np.float64)
            h_r = np.zeros(n, dtype=np.float64)
            height_gain = 0.0

        # 6. 声场多维重建 (支持 2.0 双耳全景声、5.1 环绕声与 7.1 全景声离散声道)
        width = self.spatial_width
        surround_gain = 0.38 if self.mode == "dolby_atmos" else 0.22

        if target_channels == 6:
            # === 杜比 5.1 离散声道渲染矩阵 (FL, FR, FC, LFE, SL, SR) ===
            lfe, self._lfe_zi = sosfilt(self._lfe_sos, mid, zi=self._lfe_zi)
            lfe = lfe * 1.20
            fl = mid * 0.707 + side * width
            fr = mid * 0.707 - side * width
            fc = mid * 1.0  # 人声中置
            sl = s_delayed_r * surround_gain + h_l * height_gain
            sr = -s_delayed_l * surround_gain + h_r * height_gain
            norm_factor = 0.88
            atmos_51 = np.vstack((fl, fr, fc, lfe, sl, sr)) * norm_factor
            self._write_pos = (self._write_pos + n) & self.RING_MASK
            w_gain = wet_ramp[np.newaxis, :] if isinstance(wet_ramp, np.ndarray) else wet_ramp
            dry_51 = np.vstack((L, R, np.zeros(n), np.zeros(n), np.zeros(n), np.zeros(n)))
            return dry_51 * (1.0 - w_gain) + atmos_51 * w_gain

        elif target_channels == 8:
            # === 杜比 7.1 离散声道渲染矩阵 (FL, FR, FC, LFE, SL, SR, RL, RR) ===
            lfe, self._lfe_zi = sosfilt(self._lfe_sos, mid, zi=self._lfe_zi)
            lfe = lfe * 1.20
            fl = mid * 0.707 + side * width
            fr = mid * 0.707 - side * width
            fc = mid * 1.0
            sl = s_delayed_r * surround_gain
            sr = -s_delayed_l * surround_gain
            tap_rear1 = int(round(self.sample_rate * 0.0125))  # 12.5ms 后置环绕
            tap_rear2 = int(round(self.sample_rate * 0.0175))  # 17.5ms 后置环绕
            s_rear_l = self._delay_ring[1, (idx_vec - tap_rear1) & self.RING_MASK]
            s_rear_r = self._delay_ring[1, (idx_vec - tap_rear2) & self.RING_MASK]
            rl = s_rear_r * surround_gain + h_l * height_gain
            rr = -s_rear_l * surround_gain + h_r * height_gain
            norm_factor = 0.88
            atmos_71 = np.vstack((fl, fr, fc, lfe, sl, sr, rl, rr)) * norm_factor
            self._write_pos = (self._write_pos + n) & self.RING_MASK
            w_gain = wet_ramp[np.newaxis, :] if isinstance(wet_ramp, np.ndarray) else wet_ramp
            dry_71 = np.vstack((L, R, np.zeros(n), np.zeros(n), np.zeros(n), np.zeros(n), np.zeros(n), np.zeros(n)))
            return dry_71 * (1.0 - w_gain) + atmos_71 * w_gain

        else:
            # === 耳机与立体声音响双耳 HRTF 3D 渲染 (2.0 声道) ===
            out_l = (
                mid * 0.80
                + shadow_r * 0.20
                + side * width
                + s_delayed_r * surround_gain
                + h_l * height_gain
            )
            out_r = (
                mid * 0.80
                + shadow_l * 0.20
                - side * width
                - s_delayed_l * surround_gain
                + h_r * height_gain
            )

            self._write_pos = (self._write_pos + n) & self.RING_MASK
            atmos_stereo = np.vstack((out_l, out_r))
            norm_factor = 0.90
            final_out = stereo_in * (1.0 - wet_ramp) + (atmos_stereo * norm_factor) * wet_ramp
            return final_out


class NordicDspProcessor:
    HIST_LEN = 512
    REV_BUF_LEN = 32768
    REV_MASK = 32767

    def __init__(self, sample_rate: int = 44100):
        self.sample_rate = sample_rate
        self.gains_db: List[float] = [0.0, 0.0, 0.0, 0.0, 0.0]
        self.reverb_mix: float = 0.03

        self._cur_pre_gain: float = 0.82
        self._target_pre_gain: float = 0.82
        self._cur_rev_mix: float = 0.03
        self._target_rev_mix: float = 0.03
        self.replay_gain_enabled: bool = False
        self._cur_rg_gain: float = 1.0
        self._target_rg_gain: float = 1.0

        # 人声 ⟷ 伴奏 无极天平 (-1.0 = 纯享伴奏, 0.0 = 原声均衡, +1.0 = 人声清唱聚焦)
        self.vocal_balance: float = 0.0
        self._cur_vocal_bal: float = 0.0
        self._target_vocal_bal: float = 0.0
        nyq = self.sample_rate * 0.5
        self._vocal_lp_sos = butter(2, max(60.0, 145.0) / nyq, btype="lowpass", output="sos")
        self._vocal_hp_sos = butter(2, min(8200.0, nyq * 0.88) / nyq, btype="highpass", output="sos")
        self._vocal_lp_zi = np.zeros((self._vocal_lp_sos.shape[0], 2), dtype=np.float64)
        self._vocal_hp_zi = np.zeros((self._vocal_hp_sos.shape[0], 2), dtype=np.float64)

        # 黑胶模拟温暖味 (0.0 ~ 1.0: 电子管偶次谐波饱和 + 细腻黑胶接触静噪)
        self.vinyl_warmth: float = 0.0
        self._cur_vinyl_warmth: float = 0.0
        self._target_vinyl_warmth: float = 0.0
        self._crackle_lp_state: np.ndarray = np.zeros(2, dtype=np.float64)
        self._rng = np.random.default_rng(42)

        self._sos: np.ndarray = self._build_sos(self.gains_db)
        self._pending_sos: Optional[np.ndarray] = None
        self._zi: np.ndarray = np.zeros((5, 2, 2), dtype=np.float64)
        self._input_hist: np.ndarray = np.zeros((2, self.HIST_LEN), dtype=np.float64)

        self._rev_ring: np.ndarray = np.zeros((2, self.REV_BUF_LEN), dtype=np.float64)
        self._rev_write_pos: int = 0
        self._rev_lp_state: np.ndarray = np.zeros(2, dtype=np.float64)
        self.spatializer = DolbyAtmosSpatializer(self.sample_rate)

        # 耳机硬件声学校准 (AutoEq 哈曼曲线目标补偿)
        self.headphone_profile_id: str = "bypass"
        self._headphone_sos: Optional[np.ndarray] = None
        self._headphone_zi: Optional[np.ndarray] = None
        self._headphone_preamp: float = 1.0
        self._cur_hp_wet: float = 0.0
        self._target_hp_wet: float = 0.0

    def reset_state(self) -> None:
        self._zi.fill(0.0)
        self._vocal_lp_zi.fill(0.0)
        self._vocal_hp_zi.fill(0.0)
        self._crackle_lp_state.fill(0.0)
        self._input_hist.fill(0.0)
        self._rev_ring.fill(0.0)
        self._rev_write_pos = 0
        self._rev_lp_state.fill(0.0)
        if self._headphone_zi is not None:
            self._headphone_zi.fill(0.0)
        self._cur_pre_gain = self._target_pre_gain
        self._cur_rev_mix = self._target_rev_mix
        self._cur_rg_gain = self._target_rg_gain
        self._cur_vocal_bal = self._target_vocal_bal
        self._cur_vinyl_warmth = self._target_vinyl_warmth
        self._cur_hp_wet = self._target_hp_wet
        self.spatializer.reset_state()
        if self._pending_sos is not None:
            self._sos = self._pending_sos
            self._pending_sos = None

    def set_headphone_profile(self, profile_id: str) -> None:
        """设置 AutoEq 耳机硬件声学校准档案"""
        self.headphone_profile_id = profile_id
        prof = HEADPHONE_PROFILES.get(profile_id)
        if prof is None or profile_id == "bypass":
            self._target_hp_wet = 0.0
            return

        sos, preamp = build_headphone_sos(prof, self.sample_rate)
        if sos is not None:
            self._headphone_sos = sos
            self._headphone_preamp = preamp
            self._headphone_zi = np.zeros((sos.shape[0], 2, 2), dtype=np.float64)
            self._target_hp_wet = 1.0
        else:
            self._target_hp_wet = 0.0

    def update_sample_rate(self, new_sr: int) -> None:
        if self.sample_rate == new_sr:
            return
        self.sample_rate = new_sr
        nyq = self.sample_rate * 0.5
        self._vocal_lp_sos = butter(2, max(60.0, 145.0) / nyq, btype="lowpass", output="sos")
        self._vocal_hp_sos = butter(2, min(8200.0, nyq * 0.88) / nyq, btype="highpass", output="sos")
        self._sos = self._build_sos(self.gains_db)
        self.spatializer = DolbyAtmosSpatializer(self.sample_rate)
        if self.headphone_profile_id != "bypass":
            self.set_headphone_profile(self.headphone_profile_id)
        self.reset_state()

    def set_vocal_balance(self, balance: float) -> None:
        """设置人声 ⟷ 伴奏无极天平: -1.0 (纯享伴奏) ~ 0.0 (原声) ~ +1.0 (人声聚焦)"""
        clamped = max(-1.0, min(1.0, float(balance)))
        self.vocal_balance = clamped
        self._target_vocal_bal = clamped

    def set_vinyl_warmth(self, warmth: float) -> None:
        """设置黑胶模拟温暖味: 0.0 (关闭) ~ 1.0 (电子管偶次谐波与细腻黑胶静噪)"""
        clamped = max(0.0, min(1.0, float(warmth)))
        self.vinyl_warmth = clamped
        self._target_vinyl_warmth = clamped

    def set_replay_gain(self, enabled: bool, rg_db: Optional[float], peak: Optional[float] = None) -> None:
        self.replay_gain_enabled = bool(enabled)
        if not self.replay_gain_enabled or rg_db is None:
            self._target_rg_gain = 1.0
            return
        clamped_db = max(-14.0, min(10.0, float(rg_db)))
        lin_gain = float(10.0 ** (clamped_db / 20.0))
        if peak is not None and peak > 0.01:
            # 防止 ReplayGain 正增益导致数字削波
            lin_gain = min(lin_gain, 0.96 / float(peak))
        self._target_rg_gain = max(0.20, min(3.16, lin_gain))

    def set_parameters(self, gains_db: List[float], reverb_mix: float) -> None:
        clamped_gains = [max(-12.0, min(12.0, float(g))) for g in (gains_db + [0.0] * 5)[:5]]
        clamped_rev = max(0.0, min(1.0, float(reverb_mix)))

        self.gains_db = clamped_gains
        self.reverb_mix = clamped_rev
        self._target_rev_mix = clamped_rev

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
                alpha = sin_w0 / 2.0 * math.sqrt((A + 1.0 / A) * (1.0 / 0.75 - 1.0) + 2.0)
                two_sqrt_a_alpha = 2.0 * math.sqrt(A) * alpha
                b0 = A * ((A + 1.0) - (A - 1.0) * cos_w0 + two_sqrt_a_alpha)
                b1 = 2.0 * A * ((A - 1.0) - (A + 1.0) * cos_w0)
                b2 = A * ((A + 1.0) - (A - 1.0) * cos_w0 - two_sqrt_a_alpha)
                a0 = (A + 1.0) + (A - 1.0) * cos_w0 + two_sqrt_a_alpha
                a1 = -2.0 * ((A - 1.0) + (A + 1.0) * cos_w0)
                a2 = (A + 1.0) + (A - 1.0) * cos_w0 - two_sqrt_a_alpha
            elif idx == 4:
                alpha = sin_w0 / 2.0 * math.sqrt((A + 1.0 / A) * (1.0 / 0.75 - 1.0) + 2.0)
                two_sqrt_a_alpha = 2.0 * math.sqrt(A) * alpha
                b0 = A * ((A + 1.0) + (A - 1.0) * cos_w0 + two_sqrt_a_alpha)
                b1 = -2.0 * A * ((A - 1.0) + (A + 1.0) * cos_w0)
                b2 = A * ((A + 1.0) + (A - 1.0) * cos_w0 - two_sqrt_a_alpha)
                a0 = (A + 1.0) - (A - 1.0) * cos_w0 + two_sqrt_a_alpha
                a1 = 2.0 * ((A - 1.0) - (A + 1.0) * cos_w0)
                a2 = (A + 1.0) - (A - 1.0) * cos_w0 - two_sqrt_a_alpha
            else:
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

    def process_block(self, stereo_in: np.ndarray, target_channels: int = 2) -> np.ndarray:
        n = stereo_in.shape[1]
        if n == 0:
            return stereo_in

        # ReplayGain (响度均衡 / 自动音量归一化) 前端增益平滑过渡
        if abs(self._cur_rg_gain - self._target_rg_gain) > 1e-4:
            rg_ramp = np.linspace(self._cur_rg_gain, self._target_rg_gain, n, dtype=np.float64)[np.newaxis, :]
            self._cur_rg_gain = self._target_rg_gain
            stereo_in = stereo_in * rg_ramp
        elif abs(self._cur_rg_gain - 1.0) > 1e-4:
            stereo_in = stereo_in * self._cur_rg_gain

        # 人声 ⟷ 伴奏 无极天平 (Mid/Side 频域掩蔽分离，完整保留中置低频底鼓与极高频空气感)
        if abs(self._cur_vocal_bal) > 1e-3 or abs(self._target_vocal_bal) > 1e-3:
            if abs(self._cur_vocal_bal - self._target_vocal_bal) > 1e-4:
                v_bal = np.linspace(self._cur_vocal_bal, self._target_vocal_bal, n, dtype=np.float64)
                self._cur_vocal_bal = self._target_vocal_bal
            else:
                v_bal = self._cur_vocal_bal

            mid = (stereo_in[0] + stereo_in[1]) * 0.5
            side = (stereo_in[0] - stereo_in[1]) * 0.5
            mid_bass, self._vocal_lp_zi = sosfilt(self._vocal_lp_sos, mid, zi=self._vocal_lp_zi)
            mid_air, self._vocal_hp_zi = sosfilt(self._vocal_hp_sos, mid, zi=self._vocal_hp_zi)
            mid_bass_air = mid_bass + mid_air
            mid_vocal = mid - mid_bass_air

            # 负值 (< 0): 实时隐去中置人声，保留底鼓/贝斯与立体声伴奏；正值 (> 0): 压暗伴奏、提亮人声细节
            neg_k = np.maximum(0.0, -v_bal)
            pos_k = np.maximum(0.0, v_bal)
            vocal_gain = (1.0 - 0.96 * neg_k) + 0.54 * pos_k
            bass_air_gain = 1.0 - 0.36 * pos_k
            side_gain = (1.0 + 0.26 * neg_k) * (1.0 - 0.68 * pos_k)

            new_mid = mid_vocal * vocal_gain + mid_bass_air * bass_air_gain
            new_side = side * side_gain
            stereo_in = np.vstack((new_mid + new_side, new_mid - new_side))
        else:
            # 保持滤波器状态连续，确保随时拖动滑块零爆音
            mid = (stereo_in[0] + stereo_in[1]) * 0.5
            _, self._vocal_lp_zi = sosfilt(self._vocal_lp_sos, mid, zi=self._vocal_lp_zi)
            _, self._vocal_hp_zi = sosfilt(self._vocal_hp_sos, mid, zi=self._vocal_hp_zi)

        # 黑胶模拟温暖味 (电子管偶次谐波微饱和 + 细腻黑胶接触底噪)
        if self._cur_vinyl_warmth > 1e-3 or self._target_vinyl_warmth > 1e-3:
            if abs(self._cur_vinyl_warmth - self._target_vinyl_warmth) > 1e-4:
                w_mix = np.linspace(self._cur_vinyl_warmth, self._target_vinyl_warmth, n, dtype=np.float64)[np.newaxis, :]
                w_scalar = float(self._target_vinyl_warmth)
                self._cur_vinyl_warmth = self._target_vinyl_warmth
            else:
                w_mix = self._cur_vinyl_warmth
                w_scalar = float(self._cur_vinyl_warmth)

            drive = 1.0 + 0.42 * w_scalar
            bias = 0.045 * w_scalar
            tube_out = (np.tanh(stereo_in * drive + bias) - math.tanh(bias)) / math.tanh(drive)
            stereo_in = stereo_in * (1.0 - 0.55 * w_mix) + tube_out * (0.55 * w_mix)

            # 合成极其克制的真实黑胶唱针接触微尘脉冲与温暖底噪
            dust_prob = 0.0009 * w_scalar
            impulses = (self._rng.random((2, n)) < dust_prob).astype(np.float64)
            imp_amp = (self._rng.uniform(-1.0, 1.0, size=(2, n)) * impulses) * (0.011 * w_scalar)
            surface_hiss = self._rng.normal(0.0, 0.00028 * w_scalar, size=(2, n))
            raw_vinyl_noise = imp_amp + surface_hiss
            # 简单一阶唱头谐振柔化，消除数字毛刺感
            vinyl_filtered = raw_vinyl_noise * 0.55
            vinyl_filtered[:, 1:] += raw_vinyl_noise[:, :-1] * 0.45
            stereo_in = stereo_in + vinyl_filtered

        if abs(self._cur_pre_gain - self._target_pre_gain) > 1e-4:
            gain_ramp = np.linspace(self._cur_pre_gain, self._target_pre_gain, n, dtype=np.float64)
            self._cur_pre_gain = self._target_pre_gain
            x = stereo_in * gain_ramp[np.newaxis, :]
        else:
            x = stereo_in * self._cur_pre_gain

        # 耳机硬件声学校准 (AutoEq 参量双二阶滤波级联)
        if self._headphone_sos is not None and (self._cur_hp_wet > 1e-4 or self._target_hp_wet > 1e-4):
            calibrated, self._headphone_zi = sosfilt(
                self._headphone_sos,
                x * self._headphone_preamp,
                axis=-1,
                zi=self._headphone_zi,
            )
            if abs(self._cur_hp_wet - self._target_hp_wet) > 1e-4:
                ramp = np.linspace(self._cur_hp_wet, self._target_hp_wet, n, dtype=np.float64)[np.newaxis, :]
                self._cur_hp_wet = self._target_hp_wet
                x = x * (1.0 - ramp) + calibrated * ramp
            elif self._cur_hp_wet > 0.001:
                x = x * (1.0 - self._cur_hp_wet) + calibrated * self._cur_hp_wet

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

        if n >= self.HIST_LEN:
            self._input_hist[:] = x[:, -self.HIST_LEN :]
        else:
            self._input_hist[:, :-n] = self._input_hist[:, n:]
            self._input_hist[:, -n:] = x

        # 杜比全景声与空间音频渲染 (支持 2.0 双耳、5.1 环绕与 7.1 全景声输出)
        spatial_out = self.spatializer.process_block(eq_out, target_channels=target_channels)

        idx_vec = (self._rev_write_pos + np.arange(n, dtype=np.int32)) & self.REV_MASK
        fb_read_idx = (idx_vec - 3925) & self.REV_MASK
        self._rev_ring[0, idx_vec] = spatial_out[0] + self._rev_ring[1, fb_read_idx] * 0.14
        self._rev_ring[1, idx_vec] = spatial_out[1] + self._rev_ring[0, fb_read_idx] * 0.14
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

            wet_stack = np.vstack((wet_l, wet_r))
            wet_smooth = wet_stack * 0.65
            wet_smooth[:, 1:] += wet_stack[:, :-1] * 0.35

            if target_channels == 6:
                out = spatial_out.copy()
                out[0] = spatial_out[0] * (1.0 - rev_ramp * 0.15) + wet_smooth[0] * (rev_ramp * 0.40)
                out[1] = spatial_out[1] * (1.0 - rev_ramp * 0.15) + wet_smooth[1] * (rev_ramp * 0.40)
                out[4] = spatial_out[4] + wet_smooth[0] * (rev_ramp * 0.55)
                out[5] = spatial_out[5] + wet_smooth[1] * (rev_ramp * 0.55)
            elif target_channels == 8:
                out = spatial_out.copy()
                out[0] = spatial_out[0] * (1.0 - rev_ramp * 0.15) + wet_smooth[0] * (rev_ramp * 0.35)
                out[1] = spatial_out[1] * (1.0 - rev_ramp * 0.15) + wet_smooth[1] * (rev_ramp * 0.35)
                out[4] = spatial_out[4] + wet_smooth[0] * (rev_ramp * 0.45)
                out[5] = spatial_out[5] + wet_smooth[1] * (rev_ramp * 0.45)
                out[6] = spatial_out[6] + wet_smooth[0] * (rev_ramp * 0.45)
                out[7] = spatial_out[7] + wet_smooth[1] * (rev_ramp * 0.45)
            else:
                out = spatial_out * (1.0 - rev_ramp * 0.24) + wet_smooth * (rev_ramp * 0.68)
        else:
            out = spatial_out

        abs_out = np.abs(out)
        over_mask = abs_out > 0.80
        if np.any(over_mask):
            sign = np.sign(out[over_mask])
            compressed = 0.80 + 0.17 * np.tanh((abs_out[over_mask] - 0.80) / 0.17)
            out[over_mask] = sign * compressed

        return out


class ContinuousDspStreamDevice(QIODevice):
    BLOCK_FRAMES = 1024
    FADEOUT_FRAMES = 3584  # ~81.3ms @ 44.1kHz，消除切歌/暂停时的数字截断爆音 (Click/Pop)
    SPECTRUM_BANDS = 24

    def __init__(self, engine: "NordicAudioEngine"):
        super().__init__(engine)
        self.engine = engine
        self._out_fifo = bytearray()
        self._last_tail_frame = np.zeros((2, 1), dtype=np.float64)
        self._crossfade_on_next_block: bool = False
        self._env_gain: float = 0.0
        self._tt_speed: float = 1.0  # 黑胶唱盘实时转速倍率 (0.22x -> 1.0x 柔顺加速曲线)
        self._hann_win = np.hanning(self.BLOCK_FRAMES)
        self._cos_fade_in = (
            0.5 - 0.5 * np.cos(np.linspace(0.0, math.pi, self.BLOCK_FRAMES, dtype=np.float64))
        )[np.newaxis, :]
        self._cos_fade_out = 1.0 - self._cos_fade_in
        self._cos_fade_out_80ms = (
            0.5 + 0.5 * np.cos(np.linspace(0.0, math.pi, self.FADEOUT_FRAMES, dtype=np.float64))
        )[np.newaxis, :]
        self._silence_stereo = bytes(self.BLOCK_FRAMES * 4)
        self._silence_mono = bytes(self.BLOCK_FRAMES * 2)
        self._fft_edges = np.unique(
            np.geomspace(2, 360, num=self.SPECTRUM_BANDS + 1).astype(np.int32)
        )

    def start_stream(self) -> None:
        self._out_fifo.clear()
        if not self.isOpen():
            self.open(QIODevice.OpenModeFlag.ReadOnly)

    def request_smooth_transition(self) -> None:
        self._out_fifo.clear()
        self._crossfade_on_next_block = True

    def render_smooth_fadeout_block(self, is_pause_brake: bool = False) -> Optional[bytes]:
        """渲染 80ms 余弦微淡出块，确保暂停/停止时音频平滑淡出，杜绝截断爆音"""
        eng = self.engine
        if self._env_gain <= 0.001 and float(np.max(np.abs(self._last_tail_frame))) <= 1e-4:
            self._env_gain = 0.0
            self._last_tail_frame.fill(0.0)
            return None

        raw_buf = eng._decoded_raw_bytes
        total_avail_frames = len(raw_buf) // 4
        cur_frame = eng._frame_cursor

        n_frames = self.FADEOUT_FRAMES
        if cur_frame + 16 < total_avail_frames:
            end_frame = min(total_avail_frames, cur_frame + n_frames)
            actual = end_frame - cur_frame
            byte_slice = raw_buf[cur_frame * 4 : end_frame * 4]
            arr = np.frombuffer(byte_slice, dtype=np.int16).astype(np.float64) / 32768.0
            arr = arr.reshape(-1, 2).T
            if actual < n_frames:
                pad = np.zeros((2, n_frames - actual), dtype=np.float64)
                stereo_block = np.hstack((arr, pad))
            else:
                stereo_block = arr
            eng._frame_cursor = end_frame
            proc_stereo = eng.dsp.process_block(stereo_block, target_channels=eng._channels)
            proc_stereo = proc_stereo * (self._env_gain * self._cos_fade_out_80ms)
        else:
            proc_stereo = self._last_tail_frame * self._cos_fade_out_80ms

        self._env_gain = 0.0
        self._last_tail_frame.fill(0.0)
        self._crossfade_on_next_block = False
        return self._pack_pcm_bytes(proc_stereo, n_frames, eng._channels)

    def _reset_tail_frame(self) -> None:
        self._last_tail_frame = np.zeros((self.engine._channels, 1), dtype=np.float64)

    def _get_silence_bytes(self, channels: int) -> bytes:
        return bytes(self.BLOCK_FRAMES * 2 * channels)

    def _pack_pcm_bytes(self, proc_audio: np.ndarray, n_frames: int, channels: int) -> bytes:
        if channels == 8:
            interleaved = np.empty(n_frames * 8, dtype=np.int16)
            for ch in range(8):
                interleaved[ch::8] = np.clip(proc_audio[ch] * 32767.0, -32767.0, 32767.0).astype(np.int16)
            return interleaved.tobytes()
        elif channels == 6:
            interleaved = np.empty(n_frames * 6, dtype=np.int16)
            for ch in range(6):
                interleaved[ch::6] = np.clip(proc_audio[ch] * 32767.0, -32767.0, 32767.0).astype(np.int16)
            return interleaved.tobytes()
        elif channels == 2:
            interleaved = np.empty(n_frames * 2, dtype=np.int16)
            interleaved[0::2] = np.clip(proc_audio[0] * 32767.0, -32767.0, 32767.0).astype(np.int16)
            interleaved[1::2] = np.clip(proc_audio[1] * 32767.0, -32767.0, 32767.0).astype(np.int16)
            return interleaved.tobytes()
        mono = (proc_audio[0] + proc_audio[1]) * 0.5
        pcm_mono = np.clip(mono * 32767.0, -32767.0, 32767.0).astype(np.int16)
        return pcm_mono.tobytes()

    def _compute_spectrum_24(self, stereo_block: np.ndarray) -> List[float]:
        mono = (stereo_block[0] + stereo_block[1]) * 0.5
        if len(mono) < self.BLOCK_FRAMES:
            padded = np.zeros(self.BLOCK_FRAMES, dtype=np.float64)
            padded[: len(mono)] = mono
            mono = padded
        else:
            mono = mono[: self.BLOCK_FRAMES]

        mag = np.abs(np.fft.rfft(mono * self._hann_win)) / (self.BLOCK_FRAMES * 0.25)
        bands: List[float] = []
        edges = self._fft_edges
        for i in range(self.SPECTRUM_BANDS):
            lo = int(edges[min(i, len(edges) - 2)])
            hi = max(lo + 1, int(edges[min(i + 1, len(edges) - 1)]))
            slice_mag = mag[lo:hi]
            val = float(np.mean(slice_mag)) if len(slice_mag) > 0 else 0.0
            tilt = 1.0 + (i / float(self.SPECTRUM_BANDS)) * 2.4
            norm_v = min(1.0, math.pow(max(0.0, val * tilt * 6.5), 0.62))
            bands.append(round(norm_v, 4))
        return bands

    def bytesAvailable(self) -> int:
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

        if not eng.is_playing and eng._preview_frames_remaining <= 0 and self._env_gain <= 0.001:
            if float(np.max(np.abs(self._last_tail_frame))) > 1e-4:
                decay = self._last_tail_frame * self._cos_fade_out
                self._last_tail_frame.fill(0.0)
                return self._pack_pcm_bytes(decay, n_frames, eng._channels)
            return self._get_silence_bytes(eng._channels)

        raw_buf = eng._decoded_raw_bytes
        total_avail_frames = len(raw_buf) // 4

        if total_avail_frames <= 64:
            if float(np.max(np.abs(self._last_tail_frame))) > 1e-4:
                decay = self._last_tail_frame * self._cos_fade_out
                self._last_tail_frame.fill(0.0)
                self._env_gain = 0.0
                return self._pack_pcm_bytes(decay, n_frames, eng._channels)
            return self._get_silence_bytes(eng._channels)

        rate = eng.playback_rate
        cur_frame = eng._frame_cursor
        target_end_frame = (
            min(total_avail_frames, eng._track_end_frame)
            if (eng._track_end_frame > 0 and total_avail_frames >= eng._track_end_frame)
            else total_avail_frames
        )
        subtrack_end_ready = (eng._track_end_frame > 0 and total_avail_frames >= eng._track_end_frame) or eng._decoder_finished

        # 检查是否到达交叉淡入淡出 (Crossfade) 触发点
        if (
            eng.crossfade_sec > 0.05
            and subtrack_end_ready
            and not eng._eof_triggered
            and eng.is_playing
        ):
            xfade_frames = int(eng.crossfade_sec * eng._sample_rate)
            span_frames = target_end_frame - eng._track_start_frame
            if span_frames > xfade_frames * 2 and cur_frame >= target_end_frame - xfade_frames:
                rem_frames = target_end_frame - cur_frame
                if rem_frames > 0:
                    eng._crossfade_tail_pcm = bytearray(raw_buf[cur_frame * 4 : target_end_frame * 4])
                    eng._crossfade_tail_cursor = 0
                    eng._crossfade_tail_total_frames = rem_frames
                eng._notify_eof_from_stream()

        if cur_frame >= target_end_frame:
            if subtrack_end_ready:
                eng._notify_eof_from_stream()
            if float(np.max(np.abs(self._last_tail_frame))) > 1e-4:
                decay = self._last_tail_frame * self._cos_fade_out
                self._last_tail_frame.fill(0.0)
                self._env_gain = 0.0
                return self._pack_pcm_bytes(decay, n_frames, eng._channels)
            return self._get_silence_bytes(eng._channels)

        advance_cursor = eng.is_playing or eng._preview_frames_remaining > 0
        self._tt_speed = 1.0

        if abs(rate - 1.0) < 0.01:
            end_frame = min(target_end_frame, cur_frame + n_frames)
            actual = end_frame - cur_frame
            byte_slice = raw_buf[cur_frame * 4 : end_frame * 4]
            byte_slice = byte_slice[: (len(byte_slice) // 4) * 4]
            if len(byte_slice) < 4 or actual <= 0:
                stereo_block = np.zeros((2, n_frames), dtype=np.float64)
            else:
                arr = np.frombuffer(byte_slice, dtype=np.int16).astype(np.float64) / 32768.0
                if len(arr) % 2 != 0:
                    arr = arr[: (len(arr) // 2) * 2]
                arr = arr.reshape(-1, 2).T
                if actual < n_frames:
                    pad = np.zeros((2, n_frames - actual), dtype=np.float64)
                    stereo_block = np.hstack((arr, pad))
                else:
                    stereo_block = arr
            if advance_cursor:
                eng._frame_cursor = end_frame
        else:
            step = rate
            float_indices = cur_frame + np.arange(n_frames, dtype=np.float64) * step
            next_cursor = min(target_end_frame, int(cur_frame + n_frames * step))

            valid_mask = float_indices < (target_end_frame - 1)
            clamped_idx = np.clip(float_indices, 0, max(0, target_end_frame - 2))
            idx0 = clamped_idx.astype(np.int32)
            frac = clamped_idx - idx0

            min_i = int(idx0[0])
            max_i = min(target_end_frame, int(idx0[-1]) + 2)
            sub_raw = raw_buf[min_i * 4 : max_i * 4]
            sub_raw = sub_raw[: (len(sub_raw) // 4) * 4]
            if len(sub_raw) < 4:
                stereo_block = np.zeros((2, n_frames), dtype=np.float64)
            else:
                sub_arr = (np.frombuffer(sub_raw, dtype=np.int16).astype(np.float64) / 32768.0)
                if len(sub_arr) % 2 != 0:
                    sub_arr = sub_arr[: (len(sub_arr) // 2) * 2]
                sub_arr = sub_arr.reshape(-1, 2)
                if len(sub_arr) == 0:
                    stereo_block = np.zeros((2, n_frames), dtype=np.float64)
                elif len(sub_arr) == 1:
                    s0 = np.repeat(sub_arr[0:1], n_frames, axis=0)
                    interp = s0 * valid_mask[:, np.newaxis]
                    stereo_block = interp.T.copy()
                else:
                    rel0 = np.clip(idx0 - min_i, 0, len(sub_arr) - 2)
                    s0 = sub_arr[rel0]
                    s1 = sub_arr[rel0 + 1]
                    interp = (s0 + (s1 - s0) * frac[:, np.newaxis]) * valid_mask[:, np.newaxis]
                    stereo_block = interp.T.copy()
            if advance_cursor:
                eng._frame_cursor = next_cursor

        # 若存在上一曲的 Crossfade 尾部缓冲，执行平滑余弦交叉混音
        if (
            eng._crossfade_tail_pcm is not None
            and eng._crossfade_tail_total_frames > 0
            and eng._crossfade_tail_cursor < eng._crossfade_tail_total_frames
        ):
            t_cur = eng._crossfade_tail_cursor
            t_tot = eng._crossfade_tail_total_frames
            t_end = min(t_tot, t_cur + n_frames)
            t_actual = t_end - t_cur
            t_bytes = eng._crossfade_tail_pcm[t_cur * 4 : t_end * 4]
            t_arr = (np.frombuffer(t_bytes, dtype=np.int16).astype(np.float64) / 32768.0).reshape(-1, 2).T
            if t_actual < n_frames:
                tail_stereo = np.hstack((t_arr, np.zeros((2, n_frames - t_actual), dtype=np.float64)))
            else:
                tail_stereo = t_arr
            progress = np.clip(
                (t_cur + np.arange(n_frames, dtype=np.float64)) / float(max(1, t_tot)),
                0.0,
                1.0,
            )
            w_out = (0.5 + 0.5 * np.cos(math.pi * progress))[np.newaxis, :]
            w_in = (0.5 - 0.5 * np.cos(math.pi * progress))[np.newaxis, :]
            stereo_block = tail_stereo * w_out + stereo_block * w_in
            if advance_cursor:
                eng._crossfade_tail_cursor = t_end
                if eng._crossfade_tail_cursor >= t_tot:
                    eng._crossfade_tail_pcm = None
                    eng._crossfade_tail_total_frames = 0

        if eng._preview_frames_remaining > 0:
            eng._preview_frames_remaining = max(0, eng._preview_frames_remaining - n_frames)

        proc_stereo = eng.dsp.process_block(stereo_block, target_channels=eng._channels)

        # ~80ms 微淡入包络 (每 1024 帧块推进 0.28，约 3.5 个块 = 81ms 完成平滑淡入)
        target_env = 1.0 if (eng.is_playing or eng._preview_frames_remaining > 0) else 0.0
        if abs(target_env - self._env_gain) > 0.001:
            if target_env > self._env_gain:
                next_env = min(target_env, self._env_gain + 0.28)
            else:
                next_env = max(target_env, self._env_gain - 0.28)
            env_curve = self._env_gain + (next_env - self._env_gain) * self._cos_fade_in
            proc_stereo = proc_stereo * env_curve
            self._env_gain = next_env
        elif target_env < 0.001:
            proc_stereo = proc_stereo * 0.0

        if self._crossfade_on_next_block:
            self._crossfade_on_next_block = False
            proc_stereo = self._last_tail_frame * self._cos_fade_out + proc_stereo * self._cos_fade_in

        self._last_tail_frame = proc_stereo[:, -1:].copy()
        eng.current_spectrum_bands = self._compute_spectrum_24(proc_stereo)
        return self._pack_pcm_bytes(proc_stereo, n_frames, eng._channels)


class NordicAudioEngine(QObject):
    trackChanged = pyqtSignal(object)
    playStateChanged = pyqtSignal(bool)
    timeUpdated = pyqtSignal(float, float, str)
    volumeChanged = pyqtSignal(float, bool)
    playbackRateChanged = pyqtSignal(float)
    soundProfileChanged = pyqtSignal(str, list, float)
    dolbyAtmosChanged = pyqtSignal(str, float, float)  # mode, width, height
    vocalBalanceChanged = pyqtSignal(float)
    vinylWarmthChanged = pyqtSignal(float)
    turntablePhysicsChanged = pyqtSignal(bool)
    headphoneProfileChanged = pyqtSignal(str)
    acousticDnaReady = pyqtSignal(object)
    outputModeChanged = pyqtSignal(str, int, str)  # mode, actual_channels, device_name
    outputDevicesUpdated = pyqtSignal(list, str)  # available_devices, selected_device_id
    deviceHotplugNotification = pyqtSignal(str)
    requestNextTrackPreload = pyqtSignal()
    sleepTimerUpdated = pyqtSignal(int)
    spectrumUpdated = pyqtSignal(list)
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

        self.sound_profile: str = "默认原声"
        self.eq_gains_db: List[float] = [0.0, 0.0, 0.0, 0.0, 0.0]
        self.reverb_mix: float = 0.03
        self.current_spectrum_bands: List[float] = [0.0] * 24

        # 无缝播放 / 交叉淡入淡出 (0.0 = Gapless 无缝切换, 1.0~5.0s = Crossfade)
        self.crossfade_sec: float = 0.0
        # ReplayGain 响度均衡开关
        self.replay_gain_enabled: bool = False
        self._rms_rg_computed: bool = False
        self._rms_rg_provisional_done: bool = False
        self._dna_computed: bool = False

        # Klang 听感控制
        self.vocal_balance: float = 0.0
        self.turntable_physics_enabled: bool = False
        self.vinyl_warmth: float = 0.0
        self.headphone_profile_id: str = "bypass"

        self._sample_rate = 44100
        self._channels = 2
        self.output_channel_mode: str = "binaural"  # "binaural" (2.0) | "surround_51" (5.1) | "surround_71" (7.1)
        self.output_channels_actual: int = 2
        self.selected_device_id: str = "default"
        self.output_device_name: str = ""
        self.dsp = NordicDspProcessor(sample_rate=self._sample_rate)

        self._using_local_file: bool = False
        self._decoded_raw_bytes = bytearray()
        self._decoded_source_path: str = ""
        self._track_start_frame: int = 0
        self._track_end_frame: int = 0
        self._crossfade_tail_pcm: Optional[bytearray] = None
        self._crossfade_tail_cursor: int = 0
        self._crossfade_tail_total_frames: int = 0
        self._decoder_started: bool = False
        self._decoder_finished: bool = False
        self._decoder_failed: bool = False
        self._frame_cursor: int = 0
        self._preview_frames_remaining: int = 0
        self._eof_triggered: bool = False

        # 下一首曲目预解码缓冲队列 (Pre-buffering)
        self._pre_decoder = QAudioDecoder(self)
        self._pre_decoded_bytes = bytearray()
        self._pre_decoded_track_id: Optional[str] = None
        self._pre_decoder_finished: bool = False
        self._ignoring_pre_decoder_signals: bool = False

        self.sleep_remaining_sec: int = 0
        self._sleep_timer = QTimer(self)
        self._sleep_timer.setInterval(1000)
        self._sleep_timer.timeout.connect(self._on_sleep_tick)

        self._media_player = QMediaPlayer(self)
        self._audio_output = QAudioOutput(self)
        self._audio_output.setVolume(0.0)
        self._media_player.setAudioOutput(self._audio_output)
        self._media_player.durationChanged.connect(self._on_media_duration_changed)
        self._media_player.mediaStatusChanged.connect(self._on_media_status_changed)

        self._decoder = QAudioDecoder(self)
        dec_fmt = QAudioFormat()
        dec_fmt.setSampleRate(self._sample_rate)
        dec_fmt.setChannelCount(2)
        dec_fmt.setSampleFormat(QAudioFormat.SampleFormat.Int16)
        self._decoder.setAudioFormat(dec_fmt)
        self._decoder.bufferReady.connect(self._on_decoder_buffer_ready)
        self._decoder.durationChanged.connect(self._on_media_duration_changed)
        self._decoder.finished.connect(self._on_decoder_finished)
        self._decoder.error.connect(self._on_decoder_error)
        self._ignoring_decoder_signals: bool = False
        self._initial_sink_fed: bool = False

        self._pre_decoder.setAudioFormat(dec_fmt)
        self._pre_decoder.bufferReady.connect(self._on_pre_decoder_buffer_ready)
        self._pre_decoder.finished.connect(self._on_pre_decoder_finished)

        # 监听音频设备热插拔 (如拔出耳机/小尾巴 DAC 自动暂停播放)
        self._media_devices = QMediaDevices(self)
        self._cached_output_devices: List[Tuple[str, str]] = []
        self._known_device_ids = set()
        self._refresh_output_devices_cache()
        _init_def = QMediaDevices.defaultAudioOutput()
        self._last_default_device_id: str = (
            bytes(_init_def.id()).decode("utf-8", errors="ignore") if not _init_def.isNull() else "default"
        )
        self._media_devices.audioOutputsChanged.connect(self._on_audio_outputs_changed)

        # 提升 Windows 进程与音频调度优先级，锁定 1ms 高精度多媒体计时器，杜绝窗口切换与后台节流时的爆音断流
        if sys.platform == "win32":
            try:
                import ctypes
                from ctypes import wintypes
                winmm = ctypes.WinDLL("winmm")
                winmm.timeBeginPeriod(1)
                kernel32 = ctypes.WinDLL("kernel32")
                kernel32.GetCurrentProcess.restype = wintypes.HANDLE
                kernel32.SetPriorityClass.argtypes = [wintypes.HANDLE, wintypes.DWORD]
                kernel32.SetPriorityClass.restype = wintypes.BOOL
                # 0x00008000 = ABOVE_NORMAL_PRIORITY_CLASS
                kernel32.SetPriorityClass(kernel32.GetCurrentProcess(), 0x00008000)
            except Exception:
                _logging.getLogger(__name__).debug("ignored exception", exc_info=True)

        self._tick_timer = QTimer(self)
        self._tick_timer.setTimerType(Qt.TimerType.PreciseTimer)
        self._tick_timer.setInterval(10)
        self._tick_timer.timeout.connect(self._on_tick)
        self._pause_drain_ticks: int = 0

        self._audio_sink: Optional[QAudioSink] = None
        self._push_io: Optional[QIODevice] = None
        self._stream_device = ContinuousDspStreamDevice(self)
        self._stream_device._out_fifo.clear()
        self._init_continuous_sink()

    def _stop_decoder_safely(self) -> None:
        self._ignoring_decoder_signals = True
        try:
            self._decoder.stop()
        except Exception:
            _logging.getLogger(__name__).debug("ignored exception", exc_info=True)
        finally:
            self._ignoring_decoder_signals = False

    def _stop_pre_decoder_safely(self) -> None:
        self._ignoring_pre_decoder_signals = True
        try:
            self._pre_decoder.stop()
        except Exception:
            _logging.getLogger(__name__).debug("ignored exception", exc_info=True)
        finally:
            self._ignoring_pre_decoder_signals = False

    def preload_next_track(self, track: Optional[NordicTrack]) -> None:
        """在后台预解码下一首曲目到内存缓冲队列，实现 0ms 无缝切歌与平滑 Crossfade"""
        if track is None:
            return
        if self._pre_decoded_track_id == track.track_id and (
            self._pre_decoder_finished or len(self._pre_decoded_bytes) > 0
        ):
            return
        # 若下一首为同一整轨 CUE 文件的另一分轨，直接复用当前解码缓冲，无需重复解码
        if (
            track.file_path
            and self._decoded_source_path == track.file_path
            and (self._decoder_finished or len(self._decoded_raw_bytes) > 0)
        ):
            return
        self._stop_pre_decoder_safely()
        self._pre_decoded_bytes.clear()
        self._pre_decoded_track_id = track.track_id
        self._pre_decoder_finished = False

        if track.file_path and Path(track.file_path).is_file():
            try:
                self._pre_decoder.setSource(QUrl.fromLocalFile(track.file_path))
                self._pre_decoder.start()
            except Exception:
                self._pre_decoded_track_id = None
        else:
            self._pre_decoded_bytes = self._build_synth_track_pcm(track)
            self._pre_decoder_finished = True

    def _drain_pre_decoder_buffers(self, max_buffers: int = 100000) -> None:
        try:
            read_cnt = 0
            while read_cnt < max_buffers and self._pre_decoder.bufferAvailable():
                buf = self._pre_decoder.read()
                cnt = buf.byteCount()
                if cnt > 0:
                    self._pre_decoded_bytes.extend(buf.constData().asstring(cnt))
                read_cnt += 1
        except Exception:
            _logging.getLogger(__name__).debug("ignored exception", exc_info=True)

    def _on_pre_decoder_buffer_ready(self) -> None:
        if self._ignoring_pre_decoder_signals:
            return
        self._drain_pre_decoder_buffers(max_buffers=6)

    def _on_pre_decoder_finished(self) -> None:
        if self._ignoring_pre_decoder_signals:
            return
        self._drain_pre_decoder_buffers(max_buffers=100000)
        self._pre_decoder_finished = True

    def set_vocal_balance(self, val: float) -> None:
        """设置人声 ⟷ 伴奏无极天平 (-1.0 纯享伴奏 ~ 0.0 原声均衡 ~ +1.0 清唱聚焦)"""
        self.vocal_balance = max(-1.0, min(1.0, float(val)))
        self.dsp.set_vocal_balance(self.vocal_balance)
        self.vocalBalanceChanged.emit(self.vocal_balance)

    def set_vinyl_warmth(self, val: float) -> None:
        """设置电子管偶次谐波温暖度与黑胶微底噪 (0.0 关闭 ~ 1.0 浓郁模拟味)"""
        self.vinyl_warmth = max(0.0, min(1.0, float(val)))
        self.dsp.set_vinyl_warmth(self.vinyl_warmth)
        self.vinylWarmthChanged.emit(self.vinyl_warmth)

    def set_turntable_physics_enabled(self, enabled: bool) -> None:
        self.turntable_physics_enabled = False
        self._stream_device._tt_speed = 1.0

    def _update_track_acoustic_dna(self) -> None:
        """离线提取当前曲目的声学 DNA 特征向量 (BPM / 能量密度 / 频谱明暗度 / 温暖度)"""
        track = self.current_track
        if track is None or self._dna_computed:
            return
        if track.acoustic_dna and isinstance(track.acoustic_dna, dict) and "bpm" in track.acoustic_dna:
            self._dna_computed = True
            return
        total_bytes = len(self._decoded_raw_bytes)
        start_byte = self._track_start_frame * 4
        end_byte = self._track_end_frame * 4 if self._track_end_frame > self._track_start_frame else total_bytes
        avail_span = min(total_bytes, end_byte) - start_byte
        if avail_span >= self._sample_rate * 4 * 12 or (self._decoder_finished and avail_span >= self._sample_rate * 4 * 3):
            # 直接传整块缓冲区 + 帧范围，函数内部只会切出 12 秒窗口；不再先复制整首（CUE 整轨可达数百 MB）
            dna = compute_pcm_acoustic_dna(
                self._decoded_raw_bytes,
                self._sample_rate,
                start_frame=self._track_start_frame,
                end_frame=min(total_bytes, end_byte) // 4,
            )
            if dna:
                track.acoustic_dna = dna
                self._dna_computed = True
                self.acousticDnaReady.emit(track)

    def _refresh_output_devices_cache(self) -> None:
        default_dev = QMediaDevices.defaultAudioOutput()
        default_desc = default_dev.description() if not default_dev.isNull() else "无可用设备"
        result: List[Tuple[str, str]] = [("default", f"系统默认 ({default_desc})")]
        seen_ids = set()
        for dev in QMediaDevices.audioOutputs():
            if dev.isNull():
                continue
            dev_id = bytes(dev.id()).decode("utf-8", errors="ignore")
            if dev_id and dev_id not in seen_ids:
                seen_ids.add(dev_id)
                result.append((dev_id, dev.description()))
        self._cached_output_devices = result
        self._known_device_ids = seen_ids

    def get_available_output_devices(self) -> List[Tuple[str, str]]:
        """返回缓存的音频输出设备列表，避免主线程频繁查询 COM 导致卡顿"""
        if not self._cached_output_devices:
            self._refresh_output_devices_cache()
        return list(self._cached_output_devices)

    def set_output_device(self, device_id: str) -> None:
        """切换音频输出设备"""
        target_id = device_id or "default"
        if self.selected_device_id == target_id and self._audio_sink is not None:
            return
        self.selected_device_id = target_id
        self.reconfigure_sink()
        self.outputDevicesUpdated.emit(self.get_available_output_devices(), self.selected_device_id)

    def _on_audio_outputs_changed(self) -> None:
        """监听音频设备热插拔事件：当耳机或外置声卡拔出时自动暂停播放"""
        old_ids = set(self._known_device_ids)
        self._refresh_output_devices_cache()
        new_ids = self._known_device_ids
        removed_ids = old_ids - new_ids
        added_ids = new_ids - old_ids

        # 检查系统默认音频设备是否实际发生变更
        default_dev = QMediaDevices.defaultAudioOutput()
        cur_default_id = bytes(default_dev.id()).decode("utf-8", errors="ignore") if not default_dev.isNull() else "default"
        default_changed = (self.selected_device_id == "default" and getattr(self, "_last_default_device_id", None) != cur_default_id)
        self._last_default_device_id = cur_default_id

        # 若设备 ID 列表与默认设备均未发生实质变动（如调节系统音量/静音引发的伪事件），直接忽略，避免误重构 Sink 产生杂音/重音
        if not removed_ids and not added_ids and not default_changed:
            return

        if removed_ids and self.is_playing:
            if self.selected_device_id == "default" or self.selected_device_id in removed_ids:
                self.pause()
                self.deviceHotplugNotification.emit("音频设备已断开，已暂停播放")

        if self.selected_device_id != "default" and self.selected_device_id not in new_ids:
            self.selected_device_id = "default"

        self.reconfigure_sink()
        self.outputDevicesUpdated.emit(self.get_available_output_devices(), self.selected_device_id)
        if added_ids and not removed_ids:
            self.deviceHotplugNotification.emit(f"已切换输出设备：{self.output_device_name}")

    def set_crossfade_sec(self, sec: float) -> None:
        """设置切歌过渡时长: 0.0 (无缝播放 Gapless) 或 1.0~5.0 秒 (平滑交叉淡入淡出 Crossfade)"""
        self.crossfade_sec = max(0.0, min(6.0, float(sec)))

    def set_replay_gain_enabled(self, enabled: bool) -> None:
        """开启或关闭 ReplayGain 响度均衡"""
        self.replay_gain_enabled = bool(enabled)
        self._update_track_replay_gain()

    def _update_track_replay_gain(self) -> None:
        track = self.current_track
        if not self.replay_gain_enabled or track is None:
            self.dsp.set_replay_gain(False, None, None)
            return

        if track.replay_gain_db is not None:
            self.dsp.set_replay_gain(True, track.replay_gain_db, track.replay_peak)
            return

        # 当音频标签内未写入 REPLAYGAIN_TRACK_GAIN 时，基于已解码 PCM 的 RMS 能量动态估算响度 (目标 -16.5 dBFS)。
        # 两阶段：解码到 3 秒先给一个保守的临时增益（范围 -6~+4 dB，不写回曲目、不锁定），
        # 解码满 25 秒后再算最终增益并锁定。CUE 分轨从该轨的起始偏移开始量，而不是整轨文件的开头。
        sr_bytes = self._sample_rate * 4
        offset_bytes = int(max(0.0, float(getattr(track, "start_time", 0.0) or 0.0)) * self._sample_rate) * 4
        avail = len(self._decoded_raw_bytes) - offset_bytes
        window = sr_bytes * 25
        if avail >= sr_bytes * 2:
            is_final = avail >= window
            if not is_final and self._rms_rg_provisional_done:
                return
            sample_slice = bytes(self._decoded_raw_bytes[offset_bytes : offset_bytes + min(avail, window)])
            sample_slice = sample_slice[: (len(sample_slice) // 4) * 4]
            pcm = np.frombuffer(sample_slice, dtype=np.int16).astype(np.float32) / 32768.0
            abs_pcm = np.abs(pcm)
            active = pcm[abs_pcm > 0.008]
            if len(active) > 2048:
                rms = float(np.sqrt(np.mean(active * active)))
                peak = float(np.max(abs_pcm))
                if rms > 1e-4:
                    rms_db = 20.0 * math.log10(rms)
                    lo, hi = (-10.0, 7.5) if is_final else (-6.0, 4.0)
                    est_gain_db = max(lo, min(hi, -16.5 - rms_db))
                    if is_final:
                        track.replay_gain_db = round(est_gain_db, 2)
                        track.replay_peak = round(peak, 4)
                        self._rms_rg_computed = True
                        self.dsp.set_replay_gain(True, track.replay_gain_db, track.replay_peak)
                    else:
                        self._rms_rg_provisional_done = True
                        self.dsp.set_replay_gain(True, round(est_gain_db, 2), round(peak, 4))
                    return

        self.dsp.set_replay_gain(True, 0.0, None)

    def _init_continuous_sink(self) -> None:
        try:
            device = QMediaDevices.defaultAudioOutput()
            if self.selected_device_id and self.selected_device_id != "default":
                for dev in QMediaDevices.audioOutputs():
                    dev_id = bytes(dev.id()).decode("utf-8", errors="ignore")
                    if dev_id == self.selected_device_id:
                        device = dev
                        break
            if device.isNull():
                self.output_device_name = "未检测到音频设备"
                return
            self.output_device_name = device.description()

            target_ch = 2
            if self.output_channel_mode == "surround_51":
                target_ch = 6
            elif self.output_channel_mode == "surround_71":
                target_ch = 8

            fmt = QAudioFormat()
            fmt.setSampleRate(self._sample_rate)
            fmt.setSampleFormat(QAudioFormat.SampleFormat.Int16)
            fmt.setChannelCount(target_ch)

            if device.isFormatSupported(fmt):
                self._channels = target_ch
                self.output_channels_actual = target_ch
            else:
                fmt.setChannelCount(2)
                if device.isFormatSupported(fmt):
                    self._channels = 2
                    self.output_channels_actual = 2
                else:
                    fmt.setChannelCount(1)
                    self._channels = 1
                    self.output_channels_actual = 1

            self._push_io = None
            self._audio_sink = QAudioSink(device, fmt, self)
            buf_multiplier = max(1, self._channels // 2)
            # 256KB 硬件缓冲 (~1.48s @ 44.1kHz)，彻底杜绝窗口切换、Alt+Tab、DWM 渲染或后台节流时的爆音断流
            self._audio_sink.setBufferSize(262144 * buf_multiplier)
            self._audio_sink.setVolume(self.volume)
            self._stream_device._reset_tail_frame()
            self._stream_device.start_stream()
            self.outputModeChanged.emit(self.output_channel_mode, self.output_channels_actual, self.output_device_name)
        except Exception:
            self._audio_sink = None
            self._push_io = None

    def _ensure_decoder_started(self) -> None:
        if (
            self._using_local_file
            and not self._decoder_started
            and not self._decoder_finished
            and not self._decoder_failed
        ):
            self._decoder_started = True
            self._decoder.start()

    def _ensure_sink_running(self) -> None:
        self._ensure_decoder_started()
        if self._audio_sink is None:
            return
        st = self._audio_sink.state()
        if st == st.SuspendedState:
            self._audio_sink.resume()
        elif self._push_io is None or st == st.StoppedState:
            # 仅在初次启动或已停止时调用 start()；在 Push 写入模式下，IdleState 表示缓冲短暂排空，切勿重复 start()，直接写入即可自动恢复 ActiveState
            self._stream_device.start_stream()
            self._push_io = self._audio_sink.start()

    def _flush_fadeout_to_sink(self, is_pause_brake: bool = False) -> None:
        if self._audio_sink is None or self._push_io is None:
            self._stream_device._env_gain = 0.0
            self._stream_device._last_tail_frame.fill(0.0)
            return
        fade_bytes = self._stream_device.render_smooth_fadeout_block(is_pause_brake=is_pause_brake)
        if fade_bytes and self._audio_sink.bytesFree() >= len(fade_bytes):
            self._push_io.write(fade_bytes)

    def _feed_audio_sink(self) -> None:
        if self._audio_sink is None or self._push_io is None:
            return
        block_bytes = ContinuousDspStreamDevice.BLOCK_FRAMES * 2 * self._channels
        buf_multiplier = max(1, self._channels // 2)
        fadeout_reserve = 16384 * buf_multiplier
        waiting_for_initial_decode = (
            self._using_local_file
            and not self._decoder_finished
            and not self._decoder_failed
            and ((len(self._decoded_raw_bytes) // 4) - self._frame_cursor) < ContinuousDspStreamDevice.BLOCK_FRAMES * 2
        )
        if waiting_for_initial_decode:
            buf_sz = self._audio_sink.bufferSize()
            if buf_sz > 0 and self._audio_sink.bytesFree() >= buf_sz - block_bytes:
                chunk = self._stream_device._render_next_dsp_block()
                if chunk:
                    self._push_io.write(chunk)
            return

        # 维持 ~1.4s 硬件环形缓冲彻底杜绝窗口切换与后台抢占断音，同时预留 16KB 空间供微淡出块即时写入
        while self._audio_sink.bytesFree() >= fadeout_reserve + block_bytes:
            chunk = self._stream_device._render_next_dsp_block()
            if not chunk:
                break
            self._push_io.write(chunk)

    def _drain_decoder_buffers(self, max_buffers: int = 100000) -> None:
        try:
            read_cnt = 0
            while read_cnt < max_buffers and self._decoder.bufferAvailable():
                buf = self._decoder.read()
                cnt = buf.byteCount()
                if cnt > 0:
                    self._decoded_raw_bytes.extend(buf.constData().asstring(cnt))
                read_cnt += 1
        except Exception:
            _logging.getLogger(__name__).debug("ignored exception", exc_info=True)

    def _on_decoder_buffer_ready(self) -> None:
        if self._ignoring_decoder_signals:
            return
        self._drain_decoder_buffers(max_buffers=12)
        if (
            self.replay_gain_enabled
            and not self._rms_rg_computed
            and self.current_track is not None
            and self.current_track.replay_gain_db is None
            and len(self._decoded_raw_bytes)
            >= (int(max(0.0, float(getattr(self.current_track, "start_time", 0.0) or 0.0)) * self._sample_rate) + self._sample_rate * 3) * 4
            and (
                not self._rms_rg_provisional_done
                or len(self._decoded_raw_bytes)
                >= (int(max(0.0, float(getattr(self.current_track, "start_time", 0.0) or 0.0)) * self._sample_rate) + self._sample_rate * 25) * 4
            )
        ):
            self._update_track_replay_gain()
        if not self._dna_computed and len(self._decoded_raw_bytes) >= self._sample_rate * 4 * 14:
            self._update_track_acoustic_dna()
        if (
            self.is_playing
            and not self._initial_sink_fed
            and (len(self._decoded_raw_bytes) // 4) - self._frame_cursor >= ContinuousDspStreamDevice.BLOCK_FRAMES * 2
        ):
            self._initial_sink_fed = True
            self._feed_audio_sink()

    def _on_decoder_finished(self) -> None:
        if self._ignoring_decoder_signals:
            return
        self._drain_decoder_buffers(max_buffers=100000)
        self._decoder_finished = True
        total_frames = len(self._decoded_raw_bytes) // 4
        if total_frames > 0:
            decoded_dur = total_frames / float(self._sample_rate)
            if self._track_start_frame == 0 and self._track_end_frame == 0:
                if decoded_dur >= 10.0 or self.duration < 10.0:
                    self.duration = decoded_dur
                    if self.current_track is not None:
                        self.current_track.duration = self.duration
                    self.timeUpdated.emit(self.current_time, self.duration, "init")
            elif self._track_end_frame == 0 and self._track_start_frame > 0:
                start_sec = self._track_start_frame / float(self._sample_rate)
                if decoded_dur > start_sec + 1.0:
                    self.duration = decoded_dur - start_sec
                    if self.current_track is not None:
                        self.current_track.duration = self.duration
                    self.timeUpdated.emit(self.current_time, self.duration, "init")
        if self.replay_gain_enabled and not self._rms_rg_computed:
            self._update_track_replay_gain()
        if not self._dna_computed:
            self._update_track_acoustic_dna()
        # 当前曲目解码完成后，自动通知主窗口在后台预解码下一首曲目 (Pre-buffering)
        QTimer.singleShot(40, self.requestNextTrackPreload.emit)

    def _on_decoder_error(self, err) -> None:
        if self._ignoring_decoder_signals:
            return
        self._drain_decoder_buffers(max_buffers=100000)
        decoded_dur = (len(self._decoded_raw_bytes) // 4) / float(self._sample_rate)
        if decoded_dur >= max(15.0, self.duration * 0.5):
            QTimer.singleShot(
                60,
                lambda: self._on_decoder_finished()
                if (not self._decoder_finished and not self._ignoring_decoder_signals)
                else None,
            )
            return
        if self.current_track and self.current_track.file_path:
            self._decoder_failed = True
            url = QUrl.fromLocalFile(self.current_track.file_path)
            self._media_player.setSource(url)
            eff = 0.0 if self.is_muted else self.volume
            self._audio_output.setVolume(eff)
            if self.is_playing:
                self._media_player.play()

    def _on_media_duration_changed(self, duration_ms: int) -> None:
        if (
            self._using_local_file
            and duration_ms > 0
            and not self._decoder_finished
            and self._track_start_frame == 0
            and self._track_end_frame == 0
        ):
            self.duration = duration_ms / 1000.0
            if self.current_track is not None:
                self.current_track.duration = self.duration
            self.timeUpdated.emit(self.current_time, self.duration, "init")

    def _on_media_status_changed(self, status: QMediaPlayer.MediaStatus) -> None:
        if (
            self._using_local_file
            and self._decoder_failed
            and status == QMediaPlayer.MediaStatus.EndOfMedia
            and not self._eof_triggered
        ):
            self._eof_triggered = True
            self._handle_eof_on_main_thread()

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

    def load_track(self, track: NordicTrack, auto_play: bool = False, is_auto_transition: bool = False) -> None:
        was_playing = self.is_playing
        should_play = bool(auto_play or was_playing)

        if was_playing and not is_auto_transition:
            self._flush_fadeout_to_sink()
            if self._using_local_file and self._decoder_failed:
                self._media_player.stop()
        if not should_play and was_playing:
            self.pause()

        start_sec = max(0.0, float(getattr(track, "start_time", 0.0) or 0.0))
        end_sec = max(0.0, float(getattr(track, "end_time", 0.0) or 0.0))
        self._track_start_frame = int(start_sec * self._sample_rate)
        self._track_end_frame = int(end_sec * self._sample_rate) if end_sec > start_sec else 0

        # 若切换的是同一整轨音频文件的不同 CUE 分轨且当前已解码完成，直接复用内存 PCM，实现 0ms 分轨切换
        same_completed_file = bool(
            track.file_path
            and self._decoded_source_path == track.file_path
            and self._decoder_finished
            and len(self._decoded_raw_bytes) > 0
        )
        if not same_completed_file:
            self._stop_decoder_safely()
            self._decoded_raw_bytes.clear()
            self._decoded_source_path = str(track.file_path or "")

        self.current_track = track
        if end_sec > start_sec:
            self.duration = max(1.0, end_sec - start_sec)
        else:
            self.duration = max(1.0, track.duration)
        self.current_time = 0.0
        self._frame_cursor = self._track_start_frame
        self._preview_frames_remaining = 0
        self._eof_triggered = False
        if not same_completed_file:
            self._decoder_started = False
            self._decoder_finished = False
            self._decoder_failed = False
            self._initial_sink_fed = False
        else:
            self._initial_sink_fed = True
        self._rms_rg_computed = False
        self._rms_rg_provisional_done = False
        self._dna_computed = False

        if not is_auto_transition:
            self._crossfade_tail_pcm = None
            self._crossfade_tail_cursor = 0
            self._crossfade_tail_total_frames = 0
            self.dsp.reset_state()
            self._stream_device._out_fifo.clear()
            self._stream_device._env_gain = 0.0
            self._stream_device._tt_speed = 1.0
            self._stream_device._last_tail_frame.fill(0.0)
            self._stream_device._crossfade_on_next_block = False
            # 彻底重置 QAudioSink 硬件环形缓冲区，排空上一首残留的 PCM 音频，杜绝切歌泄漏重放与爆音
            if self._audio_sink is not None:
                try:
                    curr_vol = self.volume if not self.is_muted else 0.0
                    self._audio_sink.setVolume(0.0)
                    self._audio_sink.reset()
                    self._push_io = self._audio_sink.start()
                    self._audio_sink.setVolume(curr_vol)
                except Exception:
                    _logging.getLogger(__name__).debug("ignored exception", exc_info=True)
        else:
            # 自动连续切歌 (Gapless / Crossfade)：保持音频管线连续，不截断输出缓冲
            self._stream_device._env_gain = 1.0
            self._stream_device._tt_speed = 1.0
            if self._crossfade_tail_pcm is None:
                self._stream_device._crossfade_on_next_block = True

        # 优先命中预解码缓冲队列 (Pre-buffering) 或同一整轨缓存，实现 0ms 无缝加载
        used_prebuffer = same_completed_file
        if (
            not used_prebuffer
            and self._pre_decoded_track_id == track.track_id
            and self._pre_decoder_finished
            and len(self._pre_decoded_bytes) > 0
        ):
            self._decoded_raw_bytes = bytearray(self._pre_decoded_bytes)
            self._decoded_source_path = str(track.file_path or "")
            self._pre_decoded_bytes.clear()
            self._pre_decoded_track_id = None
            self._pre_decoder_finished = False
            self._using_local_file = bool(track.file_path and Path(track.file_path).is_file())
            self._decoder_started = True
            self._decoder_finished = True
            self._initial_sink_fed = True
            total_frames = len(self._decoded_raw_bytes) // 4
            if total_frames > 0 and self._track_start_frame == 0 and self._track_end_frame == 0:
                decoded_dur = total_frames / float(self._sample_rate)
                if decoded_dur >= 10.0 or self.duration < 10.0:
                    self.duration = decoded_dur
                    track.duration = self.duration
            used_prebuffer = True
            QTimer.singleShot(40, self.requestNextTrackPreload.emit)

        if not used_prebuffer:
            if track.file_path and Path(track.file_path).is_file():
                self._using_local_file = True
                url = QUrl.fromLocalFile(track.file_path)
                self._audio_output.setVolume(0.0)
                self._decoder.setSource(url)
                if should_play:
                    self._decoder_started = True
                    self._decoder.start()
            else:
                self._using_local_file = False
                self._media_player.setSource(QUrl())
                self._decoded_raw_bytes = self._build_synth_track_pcm(track)
                self._decoder_started = True
                self._decoder_finished = True
                QTimer.singleShot(40, self.requestNextTrackPreload.emit)

        self._update_track_replay_gain()
        self._update_track_acoustic_dna()
        self.trackChanged.emit(track)
        self.timeUpdated.emit(0.0, self.duration, "init")

        if should_play:
            if not self.is_playing:
                self.play()
            else:
                self._pause_drain_ticks = 0
                self._ensure_sink_running()
                self._feed_audio_sink()
                if not self._tick_timer.isActive():
                    self._tick_timer.start()

    def play(self) -> None:
        if self.is_playing or self.current_track is None:
            return
        if self.current_time >= self.duration - 0.15:
            self.current_time = 0.0
            self._frame_cursor = self._track_start_frame
            self._eof_triggered = False

        self._pause_drain_ticks = 0
        self._preview_frames_remaining = 0
        self._stream_device._tt_speed = 1.0
        self.is_playing = True
        self._ensure_sink_running()
        self._feed_audio_sink()
        if self._using_local_file and self._decoder_failed:
            self._media_player.play()
        self._tick_timer.start()
        self.playStateChanged.emit(True)

    def pause(self) -> None:
        if not self.is_playing:
            return
        self.is_playing = False
        self._preview_frames_remaining = 0
        self._crossfade_tail_pcm = None
        self._crossfade_tail_cursor = 0
        self._crossfade_tail_total_frames = 0
        self._flush_fadeout_to_sink(is_pause_brake=False)
        self._pause_drain_ticks = 4
        if not self._tick_timer.isActive():
            self._tick_timer.start()
        if self._using_local_file and self._decoder_failed:
            self._media_player.pause()
        self.current_spectrum_bands = [0.0] * 24
        self.spectrumUpdated.emit(self.current_spectrum_bands)
        self.playStateChanged.emit(False)

    def toggle_play(self) -> None:
        if self.is_playing:
            self.pause()
        else:
            self.play()

    def flush_hardware_sink(self) -> None:
        """立即重置并排空 QAudioSink 硬件环形缓冲区及内部流队列，彻底清除残留音频"""
        self.dsp.reset_state()
        self._stream_device._out_fifo.clear()
        self._stream_device._env_gain = 0.0
        self._stream_device._last_tail_frame.fill(0.0)
        self._crossfade_tail_pcm = None
        self._crossfade_tail_cursor = 0
        self._crossfade_tail_total_frames = 0
        if self._audio_sink is not None:
            try:
                curr_vol = self.volume if not self.is_muted else 0.0
                self._audio_sink.setVolume(0.0)
                self._audio_sink.reset()
                self._push_io = self._audio_sink.start()
                self._audio_sink.setVolume(curr_vol)
            except Exception:
                _logging.getLogger(__name__).debug("ignored exception", exc_info=True)

    def seek(self, target_seconds: float, auto_play_on_seek: bool = True, source: str = "seek") -> None:
        max_sec = max(0.0, self.duration - 0.05) if self.duration > 0 else 0.0
        clamped = max(0.0, min(max_sec, float(target_seconds)))
        if self.is_playing:
            self._flush_fadeout_to_sink()
        self.current_time = clamped
        self._eof_triggered = False
        self._crossfade_tail_pcm = None
        self._crossfade_tail_cursor = 0
        self._crossfade_tail_total_frames = 0
        self._frame_cursor = self._track_start_frame + max(0, int(clamped * self._sample_rate))
        self.dsp.reset_state()
        self._stream_device._out_fifo.clear()
        self._stream_device._env_gain = 0.0
        self._stream_device._last_tail_frame.fill(0.0)

        # 立即重置 QAudioSink 硬件缓冲，清空旧位置音频，确保 0ms 定位即播且不爆音
        if self._audio_sink is not None:
            try:
                curr_vol = self.volume if not self.is_muted else 0.0
                self._audio_sink.setVolume(0.0)
                self._audio_sink.reset()
                self._push_io = self._audio_sink.start()
                self._audio_sink.setVolume(curr_vol)
            except Exception:
                _logging.getLogger(__name__).debug("ignored exception", exc_info=True)

        if self._using_local_file and self._decoder_failed:
            abs_sec = (self._track_start_frame / float(self._sample_rate)) + clamped
            self._media_player.setPosition(int(abs_sec * 1000))
        elif self._using_local_file and not self._decoder_finished:
            self._drain_decoder_buffers(max_buffers=16)

        if auto_play_on_seek and not self.is_playing:
            self.play()
        elif self.is_playing:
            self._feed_audio_sink()

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
        正在播放时修改 DSP 参数会自动在下一个 46ms 块内平滑交叉淡化，
        完全不需要中断或重启音频流！仅当处于暂停状态且用户主动点击「试听当前音效」时，开放 1.5 秒平滑预览窗。
        """
        if not self.is_playing and play_preview_if_paused:
            self._ensure_sink_running()
            self._preview_frames_remaining = int(self._sample_rate * 1.5)
            self._feed_audio_sink()
            if not self._tick_timer.isActive():
                self._tick_timer.start()

    def set_output_channel_mode(self, mode: str) -> None:
        """设置全景声输出声道模式: 'binaural' (2.0 双耳全景声) | 'surround_51' (5.1 环绕) | 'surround_71' (7.1 全景声)"""
        valid_modes = ("binaural", "surround_51", "surround_71")
        if mode not in valid_modes:
            return
        if self.output_channel_mode == mode and self._audio_sink is not None:
            return
        self.output_channel_mode = mode
        self.reconfigure_sink()

    def reconfigure_sink(self) -> None:
        """平滑重建音频输出槽 (支持动态切换输出声道数，彻底释放旧槽防止重音叠加)"""
        was_playing = self.is_playing
        if self._audio_sink is not None:
            try:
                self._audio_sink.setVolume(0.0)
                self._audio_sink.stop()
                self._audio_sink.reset()
                self._audio_sink.setParent(None)
                self._audio_sink.deleteLater()
            except Exception:
                _logging.getLogger(__name__).debug("ignored exception", exc_info=True)
            self._audio_sink = None
            self._push_io = None
        self._stream_device._out_fifo.clear()
        self._stream_device._env_gain = 0.0
        self._stream_device._last_tail_frame.fill(0.0)
        self.dsp.reset_state()
        if self.duration > 0:
            self._frame_cursor = self._track_start_frame + max(0, int(self.current_time * self._sample_rate))
        self._init_continuous_sink()
        if was_playing:
            self._ensure_sink_running()
            self._feed_audio_sink()

    def set_dolby_atmos_mode(self, mode: str) -> None:
        """设置杜比全景声工作模式: 'off' | 'spatial_stereo' | 'dolby_atmos'"""
        self.dsp.spatializer.set_mode(mode)
        self.dolbyAtmosChanged.emit(mode, self.dsp.spatializer.spatial_width, self.dsp.spatializer.spatial_height)

    def set_spatial_parameters(self, width: float, height: float) -> None:
        """设置空间音频声场宽度 (0.8~2.0) 与垂直穹顶高度感知 (0.0~1.0)"""
        self.dsp.spatializer.set_parameters(width, height)
        self.dolbyAtmosChanged.emit(self.dsp.spatializer.mode, self.dsp.spatializer.spatial_width, self.dsp.spatializer.spatial_height)

    @property
    def dolby_atmos_mode(self) -> str:
        return self.dsp.spatializer.mode

    @property
    def spatial_width(self) -> float:
        return self.dsp.spatializer.spatial_width

    @property
    def spatial_height(self) -> float:
        return self.dsp.spatializer.spatial_height

    @property
    def is_dolby_atmos_active(self) -> bool:
        return self.dsp.spatializer.mode != "off"

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

    def set_headphone_profile(self, profile_id: str, trigger_preview: bool = False) -> None:
        self.headphone_profile_id = profile_id
        self.dsp.set_headphone_profile(profile_id)
        self.headphoneProfileChanged.emit(profile_id)
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
            self.trackEnded.emit()
            if self._eof_triggered and self.is_playing:
                self.pause()

    def _on_tick(self) -> None:
        if self.current_track is None:
            return
        if not self.is_playing and self._preview_frames_remaining <= 0:
            if self._pause_drain_ticks > 0:
                self._pause_drain_ticks -= 1
                return
            if self._audio_sink is not None:
                self._audio_sink.suspend()
            self._tick_timer.stop()
            return

        if self._using_local_file and self._decoder_failed:
            pos_ms = self._media_player.position()
            dur_ms = self._media_player.duration()
            if dur_ms > 0:
                self.duration = dur_ms / 1000.0
            self.current_time = pos_ms / 1000.0
            self.timeUpdated.emit(self.current_time, self.duration, "tick")
            return

        if self._using_local_file and not self._decoder_finished and not self._decoder_failed:
            self._drain_decoder_buffers(max_buffers=3)

        self._feed_audio_sink()
        if self.is_playing:
            rel_frames = max(0, self._frame_cursor - self._track_start_frame)
            queued_bytes = max(0, self._audio_sink.bufferSize() - self._audio_sink.bytesFree()) if self._audio_sink else 0
            latency_sec = queued_bytes / float(self._sample_rate * 2 * self._channels)
            self.current_time = max(0.0, min(self.duration, (rel_frames / float(self._sample_rate)) - latency_sec))
            self.timeUpdated.emit(self.current_time, self.duration, "tick")
            self.spectrumUpdated.emit(self.current_spectrum_bands)
