"""
滚动歌词核心组件 (Qt6 Offscreen Alpha-Masked QPainter + Staggered Spring Physics Engine)
功能特性：
1. 连续阻尼弹簧物理引擎 + 逐行错峰级联惯性 (Staggered Spring Cascade)：
   告别生硬整块平移，当前行领航滚动、相邻行带柔和物理惯性依次跟随，中途切行或滚轮滑动均保持连续速度。
2. 当前高亮行 (Active)：微量放大 scale(1.03)，透明度从 0.35 平滑过渡到 1.0，带柔和呼吸偏移。
3. 边缘羽化：使用 QPainter.CompositionMode_DestinationIn 叠加顶底线性渐变遮罩 (复刻 mask-image)。
4. 逐字柔光推进：采用带边缘柔化渐变的字重与透明度过渡，避免生硬切边与俗气 KTV 变色。
5. 字号无级可调：支持 14px ~ 32px 实时调节（顶部 A-/A+ 按钮或按住 Ctrl + 鼠标滚轮）。
6. 译文显示开关、左对齐/居中对齐切换、歌词时间轴偏移微调 (-0.5s / +0.5s)。
7. 点击任意一行歌词立即丝滑跳转到对应时刻。
"""

import math
from dataclasses import dataclass
from typing import List, Optional, Tuple
from PyQt6.QtCore import (
    Qt,
    QTimer,
    QElapsedTimer,
    QRectF,
    QPointF,
    pyqtSignal,
)
from PyQt6.QtGui import (
    QPainter,
    QImage,
    QPen,
    QBrush,
    QColor,
    QFont,
    QFontMetricsF,
    QLinearGradient,
    QPainterPath,
    QMouseEvent,
    QWheelEvent,
    QScreen,
    QGuiApplication,
)
from PyQt6.QtWidgets import QWidget

from qt6_player.data.tracks import LyricLine, format_timecode
from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font


@dataclass
class LineVisualState:
    y_top: float = 0.0
    height: float = 78.0
    # 每行独立弹簧物理位置与速度，实现错峰级联丝滑滚动
    visual_y: float = 0.0
    vel_y: float = 0.0
    current_scale: float = 1.0
    target_scale: float = 1.0
    current_opacity: float = 0.35
    target_opacity: float = 0.35
    current_bg_alpha: float = 0.0
    target_bg_alpha: float = 0.0
    current_x_offset: float = 0.0
    target_x_offset: float = 0.0
    pill_scale: float = 0.0


class LyricsStage(QWidget):
    seekRequested = pyqtSignal(float)        # 点击任意歌词行触发跳转秒数
    activeLineChanged = pyqtSignal(object)   # 当前激活 LyricLine 变更
    fontSizeChanged = pyqtSignal(int)        # 歌词字号变更通知 (14 ~ 32)
    lyricOffsetChanged = pyqtSignal(float)   # 歌词偏移变更通知

    MIN_FONT_SIZE = 14
    MAX_FONT_SIZE = 32

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.setMouseTracking(True)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.lyrics: List[LyricLine] = []
        self.visual_states: List[LineVisualState] = []

        self.active_index: int = -1
        self.hovered_index: int = -1
        self.current_time: float = 0.0
        self.lyric_offset: float = 0.0
        self.syllable_mode: bool = True
        self.show_translation: bool = True
        self.align_center: bool = False
        self.lyric_font_size: int = 20

        # 悬浮微调按钮碰撞区
        self._hud_buttons: List[Tuple[QRectF, str]] = []
        self._toast_text: str = ""
        self._toast_timer = QTimer(self)
        self._toast_timer.setSingleShot(True)
        self._toast_timer.setInterval(1600)
        self._toast_timer.timeout.connect(self._clear_toast)

        # 连续阻尼弹簧居中滚动目标与当前状态
        self.scroll_y: float = 0.0
        self.scroll_target_y: float = 0.0
        self.scroll_vel: float = 0.0

        # 用户手动滚轮浏览防抖恢复
        self.user_scrolling: bool = False
        self._manual_pill_rect = QRectF()
        self._user_scroll_timer = QTimer(self)
        self._user_scroll_timer.setSingleShot(True)
        self._user_scroll_timer.setInterval(2600)
        self._user_scroll_timer.timeout.connect(self._resume_auto_center)

        # 屏幕刷新率动态自适应物理与视觉插值驱动器 (60Hz / 90Hz / 120Hz / 144Hz / 165Hz / 240Hz+)
        self.screen_refresh_rate: float = 60.0
        self._screen_signals_connected: bool = False
        self._phys_clock = QElapsedTimer()
        self._phys_clock.start()
        self._motion_timer = QTimer(self)
        self._motion_timer.setTimerType(Qt.TimerType.PreciseTimer)
        self._motion_timer.timeout.connect(self._step_spring_physics)
        self._update_refresh_rate()
        self._motion_timer.start()

    def _detect_screen_refresh_rate(self) -> float:
        screen: Optional[QScreen] = None
        win = self.window()
        if win is not None:
            wh = win.windowHandle()
            if wh is not None:
                screen = wh.screen()
        if screen is None and win is not None:
            screen = win.screen()
        if screen is None:
            screen = QGuiApplication.primaryScreen()

        if screen is not None:
            hz = screen.refreshRate()
            if hz > 20.0:
                return float(hz)
        return 60.0

    def _update_refresh_rate(self, rate: Optional[float] = None) -> None:
        if rate is None or rate <= 20.0:
            rate = self._detect_screen_refresh_rate()
        self.screen_refresh_rate = float(rate)
        # 根据检测到的屏幕物理刷新率动态设置定时器间隔 (支持 60Hz~240Hz+)
        # 60Hz -> 17ms, 90Hz -> 11ms, 120Hz -> 8ms, 144Hz -> 7ms, 165Hz -> 6ms, 240Hz -> 4ms
        interval_ms = max(4, min(33, int(round(1000.0 / self.screen_refresh_rate))))
        self._motion_timer.setInterval(interval_ms)

    def _on_screen_changed(self, new_screen: Optional[QScreen]) -> None:
        if new_screen is not None:
            try:
                new_screen.refreshRateChanged.connect(self._on_refresh_rate_changed)
            except Exception:
                pass
            self._update_refresh_rate(new_screen.refreshRate())
        else:
            self._update_refresh_rate()

    def _on_refresh_rate_changed(self, new_rate: float) -> None:
        self._update_refresh_rate(new_rate)

    def showEvent(self, event) -> None:
        super().showEvent(event)
        win = self.window()
        if win is not None:
            wh = win.windowHandle()
            if wh is not None and not self._screen_signals_connected:
                wh.screenChanged.connect(self._on_screen_changed)
                self._screen_signals_connected = True
                scr = wh.screen()
                if scr is not None:
                    try:
                        scr.refreshRateChanged.connect(self._on_refresh_rate_changed)
                    except Exception:
                        pass
        self._update_refresh_rate()

    def _show_toast(self, text: str) -> None:
        self._toast_text = text
        self._toast_timer.start()
        self.update()

    def _clear_toast(self) -> None:
        self._toast_text = ""
        self.update()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def set_syllable_mode(self, enabled: bool) -> None:
        self.syllable_mode = bool(enabled)
        self.update()

    def set_show_translation(self, show: bool) -> None:
        if self.show_translation == bool(show):
            return
        self.show_translation = bool(show)
        self._recalculate_layout(snap_immediately=False)
        if self.active_index >= 0 and not self.user_scrolling:
            self.center_line_smoothly(self.active_index, immediate=False)
        self.update()

    def set_align_center(self, center: bool) -> None:
        self.align_center = bool(center)
        self._update_target_states()
        self.update()

    def set_lyric_offset(self, offset_sec: float) -> None:
        self.lyric_offset = round(float(offset_sec), 1)
        self.sync_time(self.current_time, source="seek")
        self.update()

    def adjust_lyric_offset(self, delta: float) -> None:
        if delta == 0.0:
            self.lyric_offset = 0.0
        else:
            self.lyric_offset = round(self.lyric_offset + delta, 1)
        self._show_toast(f"歌词时间偏移: {self.lyric_offset:+.1f}s")
        self.lyricOffsetChanged.emit(self.lyric_offset)
        self.sync_time(self.current_time, source="seek")

    def set_lyric_font_size(self, px_size: int) -> None:
        clamped = max(self.MIN_FONT_SIZE, min(self.MAX_FONT_SIZE, int(px_size)))
        if clamped == self.lyric_font_size:
            return
        self.lyric_font_size = clamped
        self._recalculate_layout(snap_immediately=False)
        if self.active_index >= 0 and not self.user_scrolling:
            self.center_line_smoothly(self.active_index, immediate=False)
        self._show_toast(f"歌词字号: {self.lyric_font_size}px")
        self.fontSizeChanged.emit(self.lyric_font_size)
        self.update()

    def adjust_lyric_font_size(self, delta: int) -> None:
        self.set_lyric_font_size(self.lyric_font_size + delta)

    def load_lyrics(self, lyrics: List[LyricLine], lyric_offset: float = 0.0) -> None:
        self.lyrics = lyrics
        self.lyric_offset = round(float(lyric_offset), 1)
        self.active_index = -1
        self.hovered_index = -1
        self.current_time = 0.0
        self.user_scrolling = False
        self._user_scroll_timer.stop()

        self.visual_states = [LineVisualState() for _ in lyrics]
        self._recalculate_layout(snap_immediately=True)
        self.sync_time(0.0, source="init")
        self.center_line_smoothly(0, immediate=True)
        # 切歌时赋予轻微纵向错峰入场初位移，呈现温润上浮入场感
        for i, st in enumerate(self.visual_states):
            st.visual_y = st.y_top + min(36.0, (i + 1) * 6.5)
            st.vel_y = 0.0
        self._phys_clock.restart()
        self.update()

    def _recalculate_layout(self, snap_immediately: bool = False) -> None:
        if not self.lyrics or len(self.visual_states) != len(self.lyrics):
            return
        primary_line_h = round(self.lyric_font_size * 1.68)
        sub_font_size = max(11, round(self.lyric_font_size * 0.65))
        sub_line_h = round(sub_font_size * 1.65)
        pad_v = round(self.lyric_font_size * 0.62)

        y_cursor = 0.0
        for idx, line in enumerate(self.lyrics):
            has_sub = bool(line.sub and self.show_translation)
            if has_sub:
                row_height = float(pad_v * 2 + primary_line_h + sub_line_h + 4)
            else:
                row_height = float(pad_v * 2 + primary_line_h)
            st = self.visual_states[idx]
            st.y_top = y_cursor
            st.height = row_height
            if snap_immediately:
                st.visual_y = y_cursor
                st.vel_y = 0.0
            y_cursor += row_height + max(8.0, round(self.lyric_font_size * 0.42))

    def center_line_smoothly(self, line_index: int, immediate: bool = False, duration_ms: int = 560) -> None:
        if not (0 <= line_index < len(self.visual_states)):
            return
        st = self.visual_states[line_index]
        target_y = st.y_top + st.height / 2.0
        self.scroll_target_y = target_y

        if immediate or duration_ms <= 0:
            self.scroll_y = target_y
            self.scroll_vel = 0.0
            for s in self.visual_states:
                s.visual_y = s.y_top
                s.vel_y = 0.0
            self.update()

    def sync_time(self, current_time: float, source: str = "tick") -> None:
        self.current_time = current_time
        if not self.lyrics:
            return

        eff_time = max(0.0, current_time + self.lyric_offset)
        found = 0
        for i, line in enumerate(self.lyrics):
            if eff_time >= line.time:
                found = i
            else:
                break

        if found != self.active_index:
            self.active_index = found
            self._update_target_states()
            if source == "init":
                for i, st in enumerate(self.visual_states):
                    st.current_scale = st.target_scale
                    st.current_opacity = st.target_opacity
                    st.current_bg_alpha = st.target_bg_alpha
                    st.current_x_offset = st.target_x_offset
                    st.pill_scale = 1.0 if i == found else 0.0
            self.activeLineChanged.emit(self.lyrics[found])

            if not self.user_scrolling or source in ("seek", "init"):
                self.center_line_smoothly(found, immediate=(source == "init"))

        self.update()

    def _update_target_states(self) -> None:
        for idx, st in enumerate(self.visual_states):
            dist = abs(idx - self.active_index)
            is_active = idx == self.active_index
            is_hover = idx == self.hovered_index

            if is_active:
                st.target_scale = 1.03
                st.target_opacity = 1.0
                st.target_bg_alpha = 1.0
                st.target_x_offset = 0.0 if self.align_center else 4.0
            elif is_hover:
                st.target_scale = 1.01
                st.target_opacity = 0.78
                st.target_bg_alpha = 0.65
                st.target_x_offset = 0.0 if self.align_center else 2.0
            elif dist == 1:
                st.target_scale = 1.0
                st.target_opacity = 0.52
                st.target_bg_alpha = 0.0
                st.target_x_offset = 0.0
            elif dist == 2:
                st.target_scale = 1.0
                st.target_opacity = 0.38
                st.target_bg_alpha = 0.0
                st.target_x_offset = 0.0
            else:
                st.target_scale = 1.0
                st.target_opacity = 0.32
                st.target_bg_alpha = 0.0
                st.target_x_offset = 0.0

    def _step_spring_physics(self) -> None:
        """
        60fps 临界阻尼弹簧求解器 (Critically-Damped Spring + Per-Row Cascade)
        确保无论自动切行、点击跳转还是滚轮滑动，都具有连续的速度与波浪式行间级联感。
        """
        raw_dt = self._phys_clock.restart() / 1000.0
        # 动态自适应屏幕物理刷新率，覆盖 24Hz 到 360Hz 超高刷电竞与高阶 ProMotion 显示屏
        fallback_dt = 1.0 / max(30.0, getattr(self, "screen_refresh_rate", 60.0))
        dt = max(0.002, min(0.045, raw_dt if raw_dt > 0 else fallback_dt))
        needs_repaint = False

        # 1. 全局视口中心位置阻尼弹簧 (omega = 9.8 rad/s, 临界阻尼平滑缓出)
        omega_scroll = 9.8
        disp = self.scroll_y - self.scroll_target_y
        if abs(disp) > 0.08 or abs(self.scroll_vel) > 0.4:
            accel = -(omega_scroll * omega_scroll) * disp - 2.0 * omega_scroll * self.scroll_vel
            self.scroll_vel += accel * dt
            self.scroll_y += self.scroll_vel * dt
            needs_repaint = True
        else:
            self.scroll_y = self.scroll_target_y
            self.scroll_vel = 0.0

        # 2. 每一行的错峰级联弹簧 + 视觉属性平滑过渡
        alpha_lerp = 1.0 - math.exp(-11.5 * dt)
        active_idx = max(0, self.active_index)

        for idx, st in enumerate(self.visual_states):
            # 距离当前激活行越远的行，弹簧固有频率略低，形成细腻的波浪跟随感
            row_dist = min(6, abs(idx - active_idx))
            omega_row = max(7.2, 13.5 - row_dist * 0.95)
            # 当视口正在滚动时，给每行目标位置施加微小的惯性滞后偏移，产生手风琴级联弹性
            cascade_lag = -self.scroll_vel * (0.008 * row_dist)
            row_target_y = st.y_top + cascade_lag

            dy = st.visual_y - row_target_y
            if abs(dy) > 0.06 or abs(st.vel_y) > 0.3:
                acc_y = -(omega_row * omega_row) * dy - 2.0 * omega_row * st.vel_y
                st.vel_y += acc_y * dt
                st.visual_y += st.vel_y * dt
                needs_repaint = True
            else:
                st.visual_y = row_target_y
                st.vel_y = 0.0

            ds = st.target_scale - st.current_scale
            do = st.target_opacity - st.current_opacity
            db = st.target_bg_alpha - st.current_bg_alpha
            dx = st.target_x_offset - st.current_x_offset
            target_pill = 1.0 if idx == self.active_index else 0.0
            dp = target_pill - st.pill_scale

            if (
                abs(ds) > 0.0003
                or abs(do) > 0.001
                or abs(db) > 0.002
                or abs(dx) > 0.02
                or abs(dp) > 0.002
            ):
                st.current_scale += ds * alpha_lerp
                st.current_opacity += do * alpha_lerp
                st.current_bg_alpha += db * alpha_lerp
                st.current_x_offset += dx * alpha_lerp
                st.pill_scale += dp * alpha_lerp
                needs_repaint = True
            else:
                st.current_scale = st.target_scale
                st.current_opacity = st.target_opacity
                st.current_bg_alpha = st.target_bg_alpha
                st.current_x_offset = st.target_x_offset
                st.pill_scale = target_pill

        # 3. 逐字柔光羽化与间奏动效在歌曲播放区间的高刷连贯渲染
        if self.syllable_mode and 0 <= self.active_index < len(self.lyrics):
            cur_line = self.lyrics[self.active_index]
            eff_t = max(0.0, self.current_time + self.lyric_offset)
            if cur_line.time <= eff_t <= cur_line.end_time + 0.15:
                needs_repaint = True

        if needs_repaint:
            self.update()

    def _compute_line_progress_ratio(self, line: LyricLine) -> float:
        eff_time = max(0.0, self.current_time + self.lyric_offset)
        if line.is_interlude or not line.syllables:
            span = max(0.8, line.end_time - line.time)
            return max(0.0, min(1.0, (eff_time - line.time) / span))

        total_chars = sum(max(1, len(s.text)) for s in line.syllables)
        covered_chars = 0.0
        for s in line.syllables:
            char_len = max(1, len(s.text))
            if eff_time >= s.end:
                covered_chars += char_len
            elif eff_time > s.start:
                seg_ratio = (eff_time - s.start) / max(0.06, s.end - s.start)
                covered_chars += char_len * seg_ratio
                break
            else:
                break
        return max(0.0, min(1.0, covered_chars / max(1, total_chars)))

    def paintEvent(self, event) -> None:
        w, h = self.width(), self.height()
        if w <= 10 or h <= 10 or not self.lyrics:
            return

        dpr = self.devicePixelRatioF()
        buffer = QImage(
            int(w * dpr),
            int(h * dpr),
            QImage.Format.Format_ARGB32_Premultiplied,
        )
        buffer.setDevicePixelRatio(dpr)
        buffer.fill(Qt.GlobalColor.transparent)

        bp = QPainter(buffer)
        bp.setRenderHint(QPainter.RenderHint.Antialiasing)
        bp.setRenderHint(QPainter.RenderHint.TextAntialiasing)
        bp.setRenderHint(QPainter.RenderHint.VerticalSubpixelPositioning)

        viewport_center_y = h / 2.0
        primary_line_h = round(self.lyric_font_size * 1.68)
        sub_font_size = max(11, round(self.lyric_font_size * 0.65))
        sub_line_h = round(sub_font_size * 1.65)
        pad_v = round(self.lyric_font_size * 0.62)

        align_flag = Qt.AlignmentFlag.AlignHCenter if self.align_center else Qt.AlignmentFlag.AlignLeft

        for idx, line in enumerate(self.lyrics):
            st = self.visual_states[idx]
            screen_y = viewport_center_y + (st.visual_y - self.scroll_y)

            if screen_y + st.height < -60 or screen_y > h + 60:
                continue

            bp.save()
            bp.setOpacity(st.current_opacity)

            anchor_x = (w / 2.0) if self.align_center else 18.0
            anchor_y = screen_y + st.height / 2.0

            row_rect = QRectF(10.0, screen_y, w - 46.0, st.height)

            # 背景毛玻璃微卡片进行平滑 1.03 缩放
            if st.current_bg_alpha > 0.01:
                bp.save()
                bp.translate(anchor_x, anchor_y)
                bp.scale(st.current_scale, st.current_scale)
                bp.translate(-anchor_x, -anchor_y)

                bg_path = QPainterPath()
                bg_path.addRoundedRect(row_rect, 10.0, 10.0)

                bg_col = QColor(self.theme.sage_soft if idx == self.active_index else self.theme.subtle_hover)
                bg_col.setAlphaF(bg_col.alphaF() * st.current_bg_alpha)
                bp.fillPath(bg_path, bg_col)

                stroke_col = QColor(self.theme.stroke_outer)
                stroke_col.setAlphaF(stroke_col.alphaF() * st.current_bg_alpha)
                bp.setPen(QPen(stroke_col, 1.0))
                bp.setBrush(Qt.BrushStyle.NoBrush)
                bp.drawPath(bg_path)
                bp.restore()

            if st.pill_scale > 0.02 and not self.align_center:
                pill_h = 20.0 * st.pill_scale
                pill_rect = QRectF(14.0, anchor_y - pill_h / 2.0, 3.2, pill_h)
                pill_path = QPainterPath()
                pill_path.addRoundedRect(pill_rect, 1.6, 1.6)
                bp.fillPath(pill_path, self.theme.sage_accent)

            text_left = 28.0 + st.current_x_offset
            max_text_w = max(80.0, w - text_left - 96.0)
            is_active = idx == self.active_index

            # 文字缩放：仅在激活或悬停存在微缩放时启用平滑浮点字号/变换，确保未激活歌词 100% 像素级锐利
            bp.save()
            if abs(st.current_scale - 1.0) > 0.001:
                bp.translate(anchor_x + st.current_x_offset, anchor_y)
                bp.scale(st.current_scale, st.current_scale)
                bp.translate(-(anchor_x + st.current_x_offset), -anchor_y)

            if line.is_interlude:
                ratio = self._compute_line_progress_ratio(line) if is_active else 0.0
                base_x = (w / 2.0 - 16.0) if self.align_center else (text_left + 8.0)
                for d_i in range(3):
                    lit = is_active and ratio >= (d_i + 0.2) / 3.0
                    dot_r = 4.2 if lit else 3.4
                    dot_col = QColor(self.theme.sage_accent)
                    dot_col.setAlphaF(0.95 if lit else 0.35)
                    bp.setPen(Qt.PenStyle.NoPen)
                    bp.setBrush(dot_col)
                    bp.drawEllipse(QPointF(base_x + d_i * 16.0, screen_y + st.height / 2.0), dot_r, dot_r)
            else:
                base_weight = QFont.Weight.DemiBold if is_active else QFont.Weight.Medium
                primary_font = get_nordic_font(self.lyric_font_size, base_weight)
                bp.setFont(primary_font)
                fm = QFontMetricsF(primary_font)
                primary_rect = QRectF(text_left, screen_y + pad_v, max_text_w, float(primary_line_h))
                elided_main = fm.elidedText(line.text, Qt.TextElideMode.ElideRight, max_text_w)

                if is_active and self.syllable_mode:
                    prog = self._compute_line_progress_ratio(line)
                    actual_text_w = max(1.0, min(max_text_w, fm.horizontalAdvance(elided_main)))
                    start_x = (
                        (text_left + (max_text_w - actual_text_w) / 2.0)
                        if self.align_center
                        else text_left
                    )
                    # 使用带 18px 柔化过渡带的水平渐变画笔渲染逐字柔光推进（告别生硬矩形裁切）
                    wipe_grad = QLinearGradient(start_x, 0.0, start_x + actual_text_w, 0.0)
                    lit_col = QColor(self.theme.charcoal)
                    unlit_col = QColor(self.theme.charcoal)
                    unlit_col.setAlphaF(0.54)

                    feather = 0.06
                    p_left = max(0.0, min(1.0, prog - feather * 0.5))
                    p_right = max(0.0, min(1.0, prog + feather * 0.5))
                    wipe_grad.setColorAt(0.0, lit_col)
                    wipe_grad.setColorAt(p_left, lit_col)
                    wipe_grad.setColorAt(p_right, unlit_col)
                    wipe_grad.setColorAt(1.0, unlit_col)

                    bp.setPen(QPen(QBrush(wipe_grad), 1.0))
                    bp.drawText(
                        primary_rect,
                        int(align_flag | Qt.AlignmentFlag.AlignVCenter),
                        elided_main,
                    )
                else:
                    bp.setPen(self.theme.charcoal)
                    bp.drawText(
                        primary_rect,
                        int(align_flag | Qt.AlignmentFlag.AlignVCenter),
                        elided_main,
                    )

            if line.sub and self.show_translation:
                sub_font = get_nordic_font(sub_font_size, QFont.Weight.Normal)
                bp.setFont(sub_font)
                sub_fm = QFontMetricsF(sub_font)
                bp.setPen(self.theme.slate_dark if is_active else self.theme.slate_muted)
                sub_rect = QRectF(text_left, screen_y + pad_v + primary_line_h + 2.0, max_text_w, float(sub_line_h))
                elided_sub = sub_fm.elidedText(line.sub, Qt.TextElideMode.ElideRight, max_text_w)
                bp.drawText(
                    sub_rect,
                    int(align_flag | Qt.AlignmentFlag.AlignVCenter),
                    elided_sub,
                )

            bp.restore()

            if idx == self.hovered_index:
                badge_w, badge_h = 64.0, 24.0
                badge_rect = QRectF(
                    row_rect.right() - badge_w - 14.0,
                    anchor_y - badge_h / 2.0,
                    badge_w,
                    badge_h,
                )
                badge_path = QPainterPath()
                badge_path.addRoundedRect(badge_rect, 12.0, 12.0)
                bp.fillPath(badge_path, self.theme.card_elevated)
                bp.setPen(QPen(self.theme.stroke_outer, 1.0))
                bp.setBrush(Qt.BrushStyle.NoBrush)
                bp.drawPath(badge_path)

                bp.setPen(self.theme.sage_accent)
                bp.setFont(get_nordic_font(11, QFont.Weight.DemiBold, mono=True))
                bp.drawText(
                    badge_rect,
                    int(Qt.AlignmentFlag.AlignCenter),
                    f"▶ {format_timecode(line.time)}",
                )

            bp.restore()

        # 上下边缘线性渐变遮罩 (CompositionMode_DestinationIn)
        bp.setCompositionMode(QPainter.CompositionMode.CompositionMode_DestinationIn)
        edge_mask = QLinearGradient(0.0, 0.0, 0.0, float(h))
        edge_mask.setColorAt(0.00, QColor(0, 0, 0, 0))
        edge_mask.setColorAt(0.08, QColor(0, 0, 0, 48))
        edge_mask.setColorAt(0.20, QColor(0, 0, 0, 255))
        edge_mask.setColorAt(0.80, QColor(0, 0, 0, 255))
        edge_mask.setColorAt(0.92, QColor(0, 0, 0, 48))
        edge_mask.setColorAt(1.00, QColor(0, 0, 0, 0))
        bp.fillRect(0, 0, w, h, edge_mask)
        bp.end()

        main_painter = QPainter(self)
        main_painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        main_painter.drawImage(0, 0, buffer)

        # 鼠标在歌词区悬浮时，左下角显示微弱快捷微调胶囊 (词距 -0.5s / +0.5s / 对齐切换)
        self._hud_buttons.clear()
        if self.underMouse():
            hud_items = [
                ("-0.5s", "offset_dec"),
                ("+0.5s", "offset_inc"),
                ("居中" if not self.align_center else "居左", "toggle_align"),
            ]
            bx = 12.0
            by = h - 34.0
            main_painter.setFont(get_nordic_font(11, QFont.Weight.Medium))
            for text, act in hud_items:
                bw = 46.0
                r = QRectF(bx, by, bw, 24.0)
                p = QPainterPath()
                p.addRoundedRect(r, 6.0, 6.0)
                main_painter.fillPath(p, self.theme.oat_surface)
                main_painter.setPen(QPen(self.theme.stroke_outer, 1.0))
                main_painter.drawPath(p)
                main_painter.setPen(self.theme.slate_dark)
                main_painter.drawText(r, int(Qt.AlignmentFlag.AlignCenter), text)
                self._hud_buttons.append((r, act))
                bx += bw + 6.0

        # 顶部轻提示胶囊
        if self._toast_text:
            tw, th = 176.0, 28.0
            t_rect = QRectF((w - tw) / 2.0, 12.0, tw, th)
            t_path = QPainterPath()
            t_path.addRoundedRect(t_rect, 14.0, 14.0)
            main_painter.fillPath(t_path, self.theme.card_elevated)
            main_painter.setPen(QPen(self.theme.sage_border, 1.0))
            main_painter.drawPath(t_path)
            main_painter.setPen(self.theme.charcoal)
            main_painter.setFont(get_nordic_font(11, QFont.Weight.Medium))
            main_painter.drawText(t_rect, int(Qt.AlignmentFlag.AlignCenter), self._toast_text)

        if self.user_scrolling:
            pill_w, pill_h = 156.0, 30.0
            self._manual_pill_rect = QRectF(w - pill_w - 20.0, h - pill_h - 10.0, pill_w, pill_h)
            pill_path = QPainterPath()
            pill_path.addRoundedRect(self._manual_pill_rect, 15.0, 15.0)
            main_painter.fillPath(pill_path, self.theme.card_elevated)
            main_painter.setPen(QPen(self.theme.sage_border, 1.0))
            main_painter.drawPath(pill_path)

            main_painter.setPen(Qt.PenStyle.NoPen)
            main_painter.setBrush(self.theme.sage_accent)
            main_painter.drawEllipse(
                QPointF(self._manual_pill_rect.left() + 14.0, self._manual_pill_rect.center().y()),
                3.2,
                3.2,
            )

            main_painter.setPen(self.theme.charcoal)
            main_painter.setFont(get_nordic_font(11, QFont.Weight.Medium))
            main_painter.drawText(
                self._manual_pill_rect.adjusted(24.0, 0.0, -10.0, 0.0),
                int(Qt.AlignmentFlag.AlignVCenter | Qt.AlignmentFlag.AlignLeft),
                "回到当前播放歌词",
            )

    def _hit_test_line(self, pos: QPointF) -> int:
        viewport_center_y = self.height() / 2.0
        for idx, st in enumerate(self.visual_states):
            screen_y = viewport_center_y + (st.visual_y - self.scroll_y)
            rect = QRectF(8.0, screen_y, self.width() - 24.0, st.height)
            if rect.contains(pos):
                return idx
        return -1

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        pos = event.position()
        for r, _ in self._hud_buttons:
            if r.contains(pos):
                self.setCursor(Qt.CursorShape.PointingHandCursor)
                return

        if self.user_scrolling and self._manual_pill_rect.contains(pos):
            self.setCursor(Qt.CursorShape.PointingHandCursor)
            return

        hit = self._hit_test_line(pos)
        if hit != self.hovered_index:
            self.hovered_index = hit
            self._update_target_states()
            self.setCursor(
                Qt.CursorShape.PointingHandCursor if hit >= 0 else Qt.CursorShape.ArrowCursor
            )
            self.update()
        super().mouseMoveEvent(event)

    def enterEvent(self, event) -> None:
        self.update()
        super().enterEvent(event)

    def leaveEvent(self, event) -> None:
        self.hovered_index = -1
        self._update_target_states()
        self.update()
        super().leaveEvent(event)

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            pos = event.position()
            for r, act in self._hud_buttons:
                if r.contains(pos):
                    if act == "offset_dec":
                        self.adjust_lyric_offset(-0.5)
                    elif act == "offset_inc":
                        self.adjust_lyric_offset(0.5)
                    elif act == "toggle_align":
                        self.set_align_center(not self.align_center)
                        self._show_toast("居中对齐" if self.align_center else "左对齐")
                    return

            if self.user_scrolling and self._manual_pill_rect.contains(pos):
                self._resume_auto_center()
                return

            hit = self._hit_test_line(pos)
            if 0 <= hit < len(self.lyrics):
                self.user_scrolling = False
                self._user_scroll_timer.stop()
                target_line = self.lyrics[hit]
                seek_target = max(0.0, target_line.time - self.lyric_offset)
                self.seekRequested.emit(seek_target)
                self.center_line_smoothly(hit, immediate=False)
        super().mousePressEvent(event)

    def wheelEvent(self, event: QWheelEvent) -> None:
        if event.modifiers() & Qt.KeyboardModifier.ControlModifier:
            step = 1 if event.angleDelta().y() > 0 else -1
            self.adjust_lyric_font_size(step)
            event.accept()
            return

        if not self.visual_states:
            return
        delta_y = -event.angleDelta().y() * 0.72
        min_y = 0.0
        max_y = self.visual_states[-1].y_top + self.visual_states[-1].height

        self.scroll_target_y = max(min_y, min(max_y, self.scroll_target_y + delta_y))
        self.user_scrolling = True
        self._user_scroll_timer.start()
        event.accept()

    def _resume_auto_center(self) -> None:
        self.user_scrolling = False
        self._user_scroll_timer.stop()
        if self.active_index >= 0:
            self.center_line_smoothly(self.active_index, immediate=False)
        self.update()
