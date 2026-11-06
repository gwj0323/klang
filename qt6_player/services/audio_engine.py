import math
from pathlib import Path
from typing import Optional, List, Dict, Tuple
import numpy as np
from scipy.signal import sosfilt, butter

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
    "原声自然": ([0.0, 0.0, 0.0, 0.0, 0.0], 0.03),
    "北欧厅堂": ([3.0, 1.5, -1.0, 2.5, 4.5], 0.42),
    "温暖人声": ([-2.0, 2.5, 5.5, 3.0, 1.0], 0.14),
    "深沉低音": ([7.5, 4.5, -1.5, 1.0, 2.0], 0.10),
    "清澈器乐": ([-2.0, -1.0, 1.5, 5.0, 7.0], 0.24),
}


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
            self.mode = mode
            if mode == "off":
                self._target_wet_gain = 0.0
            elif mode == "spatial_stereo":
                self._target_wet_gain = 0.65
            else:  # "dolby_atmos"
                self._target_wet_gain = 1.0

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

        if abs(self._cur_wet_gain - self._target_wet_gain) > 1e-4:
            wet_ramp = np.linspace(self._cur_wet_gain, self._target_wet_gain, n, dtype=np.float64)
            self._cur_wet_gain = self._target_wet_gain
        else:
            wet_ramp = self._cur_wet_gain

        if self._cur_wet_gain <= 0.001 and self._target_wet_gain <= 0.001:
            return stereo_in

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
        shadow_l = np.empty(n, dtype=np.float64)
        shadow_r = np.empty(n, dtype=np.float64)
        prev_l = self._head_shadow_l
        prev_r = self._head_shadow_r
        for i in range(n):
            prev_l += alpha_shadow * (m_cross[i] - prev_l)
            prev_r += alpha_shadow * (m_cross[i] - prev_r)
            shadow_l[i] = prev_l
            shadow_r[i] = prev_r
        self._head_shadow_l = prev_l
        self._head_shadow_r = prev_r

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

        self._sos: np.ndarray = self._build_sos(self.gains_db)
        self._pending_sos: Optional[np.ndarray] = None
        self._zi: np.ndarray = np.zeros((5, 2, 2), dtype=np.float64)
        self._input_hist: np.ndarray = np.zeros((2, self.HIST_LEN), dtype=np.float64)

        self._rev_ring: np.ndarray = np.zeros((2, self.REV_BUF_LEN), dtype=np.float64)
        self._rev_write_pos: int = 0
        self._rev_lp_state: np.ndarray = np.zeros(2, dtype=np.float64)
        self.spatializer = DolbyAtmosSpatializer(self.sample_rate)

    def reset_state(self) -> None:
        self._zi.fill(0.0)
        self._input_hist.fill(0.0)
        self._rev_ring.fill(0.0)
        self._rev_write_pos = 0
        self._rev_lp_state.fill(0.0)
        self._cur_pre_gain = self._target_pre_gain
        self._cur_rev_mix = self._target_rev_mix
        self.spatializer.reset_state()
        if self._pending_sos is not None:
            self._sos = self._pending_sos
            self._pending_sos = None

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

        if abs(self._cur_pre_gain - self._target_pre_gain) > 1e-4:
            gain_ramp = np.linspace(self._cur_pre_gain, self._target_pre_gain, n, dtype=np.float64)
            self._cur_pre_gain = self._target_pre_gain
            x = stereo_in * gain_ramp[np.newaxis, :]
        else:
            x = stereo_in * self._cur_pre_gain

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
    SPECTRUM_BANDS = 24

    def __init__(self, engine: "NordicAudioEngine"):
        super().__init__(engine)
        self.engine = engine
        self._out_fifo = bytearray()
        self._last_tail_frame = np.zeros((2, 1), dtype=np.float64)
        self._crossfade_on_next_block: bool = False
        self._env_gain: float = 0.0
        self._hann_win = np.hanning(self.BLOCK_FRAMES)
        self._cos_fade_in = (
            0.5 - 0.5 * np.cos(np.linspace(0.0, math.pi, self.BLOCK_FRAMES, dtype=np.float64))
        )[np.newaxis, :]
        self._cos_fade_out = 1.0 - self._cos_fade_in
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

    def render_smooth_fadeout_block(self) -> Optional[bytes]:
        eng = self.engine
        n_frames = self.BLOCK_FRAMES
        if self._env_gain <= 0.001 and float(np.max(np.abs(self._last_tail_frame))) <= 1e-4:
            self._env_gain = 0.0
            self._last_tail_frame.fill(0.0)
            return None

        raw_buf = eng._decoded_raw_bytes
        total_avail_frames = len(raw_buf) // 4
        cur_frame = eng._frame_cursor

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
            proc_stereo = proc_stereo * (self._env_gain * self._cos_fade_out)
        else:
            proc_stereo = self._last_tail_frame * self._cos_fade_out

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

        if cur_frame >= total_avail_frames:
            if eng._decoder_finished:
                eng._notify_eof_from_stream()
            if float(np.max(np.abs(self._last_tail_frame))) > 1e-4:
                decay = self._last_tail_frame * self._cos_fade_out
                self._last_tail_frame.fill(0.0)
                self._env_gain = 0.0
                return self._pack_pcm_bytes(decay, n_frames, eng._channels)
            return self._get_silence_bytes(eng._channels)

        advance_cursor = eng.is_playing or eng._preview_frames_remaining > 0

        if abs(rate - 1.0) < 0.01:
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
            if advance_cursor:
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
                eng._frame_cursor = min(total_avail_frames, int(cur_frame + n_frames * step))

        if eng._preview_frames_remaining > 0:
            eng._preview_frames_remaining = max(0, eng._preview_frames_remaining - n_frames)

        proc_stereo = eng.dsp.process_block(stereo_block, target_channels=eng._channels)

        target_env = 1.0 if (eng.is_playing or eng._preview_frames_remaining > 0) else 0.0
        if abs(target_env - self._env_gain) > 0.001:
            env_curve = self._env_gain + (target_env - self._env_gain) * self._cos_fade_in
            proc_stereo = proc_stereo * env_curve
            self._env_gain = target_env
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
    outputModeChanged = pyqtSignal(str, int, str)  # mode, actual_channels, device_name
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

        self.sound_profile: str = "原声自然"
        self.eq_gains_db: List[float] = [0.0, 0.0, 0.0, 0.0, 0.0]
        self.reverb_mix: float = 0.03
        self.current_spectrum_bands: List[float] = [0.0] * 24

        self._sample_rate = 44100
        self._channels = 2
        self.output_channel_mode: str = "binaural"  # "binaural" (2.0) | "surround_51" (5.1) | "surround_71" (7.1)
        self.output_channels_actual: int = 2
        self.output_device_name: str = ""
        self.dsp = NordicDspProcessor(sample_rate=self._sample_rate)

        self._using_local_file: bool = False
        self._decoded_raw_bytes = bytearray()
        self._crossfade_tail_pcm: Optional[bytearray] = None
        self._decoder_started: bool = False
        self._decoder_finished: bool = False
        self._decoder_failed: bool = False
        self._frame_cursor: int = 0
        self._preview_frames_remaining: int = 0
        self._eof_triggered: bool = False

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

        self._tick_timer = QTimer(self)
        self._tick_timer.setInterval(16)
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
            pass
        finally:
            self._ignoring_decoder_signals = False

    def _init_continuous_sink(self) -> None:
        try:
            device = QMediaDevices.defaultAudioOutput()
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
            self._audio_sink.setBufferSize(16384 * buf_multiplier)
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
        elif st in (st.StoppedState, st.IdleState) or self._push_io is None:
            self._stream_device.start_stream()
            self._push_io = self._audio_sink.start()

    def _flush_fadeout_to_sink(self) -> None:
        if self._audio_sink is None or self._push_io is None:
            self._stream_device._env_gain = 0.0
            self._stream_device._last_tail_frame.fill(0.0)
            return
        fade_bytes = self._stream_device.render_smooth_fadeout_block()
        if fade_bytes and self._audio_sink.bytesFree() >= len(fade_bytes):
            self._push_io.write(fade_bytes)

    def _feed_audio_sink(self) -> None:
        if self._audio_sink is None or self._push_io is None:
            return
        block_bytes = ContinuousDspStreamDevice.BLOCK_FRAMES * 2 * self._channels
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

        while self._audio_sink.bytesFree() >= block_bytes:
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
            pass

    def _on_decoder_buffer_ready(self) -> None:
        if self._ignoring_decoder_signals:
            return
        self._drain_decoder_buffers(max_buffers=100000)
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
            if decoded_dur >= 10.0 or self.duration < 10.0:
                self.duration = decoded_dur
                if self.current_track is not None:
                    self.current_track.duration = self.duration
                self.timeUpdated.emit(self.current_time, self.duration, "init")

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
        if self._using_local_file and duration_ms > 0 and not self._decoder_finished:
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

    def load_track(self, track: NordicTrack, auto_play: bool = False) -> None:
        was_playing = self.is_playing
        should_play = bool(auto_play or was_playing)

        if was_playing:
            self._flush_fadeout_to_sink()
            if self._using_local_file and self._decoder_failed:
                self._media_player.stop()
        if not should_play and was_playing:
            self.pause()

        self._stop_decoder_safely()
        self._decoded_raw_bytes.clear()
        self.current_track = track
        self.duration = max(1.0, track.duration)
        self.current_time = 0.0
        self._frame_cursor = 0
        self._preview_frames_remaining = 0
        self._eof_triggered = False
        self._decoder_started = False
        self._decoder_finished = False
        self._decoder_failed = False
        self._initial_sink_fed = False
        self._crossfade_tail_pcm = None

        self.dsp.reset_state()
        self._stream_device._out_fifo.clear()
        self._stream_device._env_gain = 0.0
        self._stream_device._last_tail_frame.fill(0.0)
        self._stream_device._crossfade_on_next_block = False

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
            self._frame_cursor = 0
            self._eof_triggered = False

        self._pause_drain_ticks = 0
        self._preview_frames_remaining = 0
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
        self._flush_fadeout_to_sink()
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

    def seek(self, target_seconds: float, auto_play_on_seek: bool = True, source: str = "seek") -> None:
        clamped = max(0.0, min(self.duration, float(target_seconds)))
        if self.is_playing:
            self._flush_fadeout_to_sink()
        self.current_time = clamped
        self._eof_triggered = False
        self._frame_cursor = max(0, int(clamped * self._sample_rate))
        self.dsp.reset_state()
        self._stream_device._out_fifo.clear()
        self._stream_device._env_gain = 0.0
        self._stream_device._last_tail_frame.fill(0.0)

        if self._using_local_file and self._decoder_failed:
            self._media_player.setPosition(int(clamped * 1000))
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
        """平滑重建音频输出槽 (支持动态切换输出声道数)"""
        was_playing = self.is_playing
        if self._audio_sink is not None:
            try:
                self._audio_sink.stop()
            except Exception:
                pass
            self._audio_sink = None
            self._push_io = None
        self._stream_device._out_fifo.clear()
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
            self.current_time = min(self.duration, self._frame_cursor / float(self._sample_rate))
            self.timeUpdated.emit(self.current_time, self.duration, "tick")
            self.spectrumUpdated.emit(self.current_spectrum_bands)
