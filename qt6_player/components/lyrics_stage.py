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
    seekRequested = pyqtSignal(float)
    activeLineChanged = pyqtSignal(object)
    fontSizeChanged = pyqtSignal(int)
    lyricOffsetChanged = pyqtSignal(float)
    posterRequested = pyqtSignal(int)

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
        self._buffer_img: Optional[QImage] = None
        self._last_painted_prog: float = -1.0
        self._idle_ticks: int = 0
        self._springs_active: bool = True
        self._phys_clock = QElapsedTimer()
        self._phys_clock.start()
        self._motion_timer = QTimer(self)
        self._motion_timer.setTimerType(Qt.TimerType.PreciseTimer)
        self._motion_timer.timeout.connect(self._step_spring_physics)
        self._update_refresh_rate()
        self._motion_timer.start()

    def _wake_physics(self, wake_springs: bool = True) -> None:
        self._idle_ticks = 0
        if wake_springs:
            self._springs_active = True
        if not self._motion_timer.isActive():
            self._phys_clock.restart()
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
                new_screen.refreshRateChanged.connect(
                    self._on_refresh_rate_changed,
                    Qt.ConnectionType.UniqueConnection,
                )
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
                        scr.refreshRateChanged.connect(
                            self._on_refresh_rate_changed,
                            Qt.ConnectionType.UniqueConnection,
                        )
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
        self._last_painted_prog = -1.0
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
        self._wake_physics()
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
        self._wake_physics()

    def center_line_smoothly(self, line_index: int, immediate: bool = False, duration_ms: int = 560) -> None:
        if not (0 <= line_index < len(self.visual_states)):
            return
        st = self.visual_states[line_index]
        target_y = st.y_top + st.height / 2.0
        self.scroll_target_y = target_y
        self._wake_physics()

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

        line_changed = found != self.active_index
        if line_changed:
            self.active_index = found
            self._last_painted_prog = -1.0
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
                if source == "seek":
                    self.user_scrolling = False
                    self._user_scroll_timer.stop()
                self.center_line_smoothly(found, immediate=(source == "init"))
        elif source == "seek":
            self.user_scrolling = False
            self._user_scroll_timer.stop()
            self.center_line_smoothly(found, immediate=False)

        self._wake_physics(wake_springs=(line_changed or source != "tick"))
        if source != "tick" or line_changed:
            self.update()

    def _update_target_states(self) -> None:
        self._wake_physics(wake_springs=True)
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
        raw_dt = self._phys_clock.restart() / 1000.0
        fallback_dt = 1.0 / max(30.0, getattr(self, "screen_refresh_rate", 60.0))
        dt = max(0.002, min(0.045, raw_dt if raw_dt > 0 else fallback_dt))
        needs_repaint = False

        if self._springs_active:
            spring_moved = False
            omega_scroll = 9.8
            disp = self.scroll_y - self.scroll_target_y
            if abs(disp) > 0.25 or abs(self.scroll_vel) > 1.2:
                accel = -(omega_scroll * omega_scroll) * disp - 2.0 * omega_scroll * self.scroll_vel
                self.scroll_vel += accel * dt
                self.scroll_y += self.scroll_vel * dt
                spring_moved = True
            else:
                self.scroll_y = self.scroll_target_y
                self.scroll_vel = 0.0

            alpha_lerp = 1.0 - math.exp(-11.5 * dt)
            active_idx = max(0, self.active_index)

            for idx, st in enumerate(self.visual_states):
                row_dist = min(6, abs(idx - active_idx))
                omega_row = max(7.2, 13.5 - row_dist * 0.95)
                cascade_lag = -self.scroll_vel * (0.008 * row_dist)
                row_target_y = st.y_top + cascade_lag

                dy = st.visual_y - row_target_y
                if abs(dy) > 0.25 or abs(st.vel_y) > 1.2:
                    acc_y = -(omega_row * omega_row) * dy - 2.0 * omega_row * st.vel_y
                    st.vel_y += acc_y * dt
                    st.visual_y += st.vel_y * dt
                    spring_moved = True
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
                    abs(ds) > 0.002
                    or abs(do) > 0.006
                    or abs(db) > 0.008
                    or abs(dx) > 0.08
                    or abs(dp) > 0.01
                ):
                    st.current_scale += ds * alpha_lerp
                    st.current_opacity += do * alpha_lerp
                    st.current_bg_alpha += db * alpha_lerp
                    st.current_x_offset += dx * alpha_lerp
                    st.pill_scale += dp * alpha_lerp
                    spring_moved = True
                else:
                    st.current_scale = st.target_scale
                    st.current_opacity = st.target_opacity
                    st.current_bg_alpha = st.target_bg_alpha
                    st.current_x_offset = st.target_x_offset
                    st.pill_scale = target_pill

            if spring_moved:
                needs_repaint = True
            else:
                self._springs_active = False

        # 3. 逐字柔光羽化与间奏动效在歌曲播放区间的平滑渲染
        syllable_only = False
        if self.syllable_mode and 0 <= self.active_index < len(self.lyrics):
            cur_line = self.lyrics[self.active_index]
            eff_t = max(0.0, self.current_time + self.lyric_offset)
            if cur_line.time <= eff_t <= cur_line.end_time + 0.15:
                prog = self._compute_line_progress_ratio(cur_line)
                if abs(prog - self._last_painted_prog) > 0.006:
                    self._last_painted_prog = prog
                    if not needs_repaint:
                        syllable_only = True
                    needs_repaint = True

        if needs_repaint:
            self._idle_ticks = 0
            if self.isVisible():
                if syllable_only and 0 <= self.active_index < len(self.visual_states):
                    st = self.visual_states[self.active_index]
                    sy = int(self.height() / 2.0 + (st.visual_y - self.scroll_y) - 6.0)
                    sh = int(st.height + 12.0)
                    self.update(0, sy, self.width(), sh)
                else:
                    self.update()
        else:
            self._idle_ticks += 1
            if self._idle_ticks > 8:
                self._motion_timer.stop()

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
        if w <= 10 or h <= 10:
            return
        if not self.lyrics:
            painter = QPainter(self)
            painter.setRenderHint(QPainter.RenderHint.Antialiasing)
            painter.setRenderHint(QPainter.RenderHint.TextAntialiasing)
            align_flag = Qt.AlignmentFlag.AlignHCenter if self.align_center else Qt.AlignmentFlag.AlignLeft
            x_pad = 0.0 if self.align_center else 18.0
            title_font = get_nordic_font(self.lyric_font_size, QFont.Weight.Medium)
            sub_font = get_nordic_font(max(12, round(self.lyric_font_size * 0.65)), QFont.Weight.Normal)
            painter.setFont(title_font)
            painter.setPen(self.theme.slate_dark)
            painter.drawText(
                QRectF(x_pad, h / 2.0 - 28.0, w - 36.0, 32.0),
                int(align_flag | Qt.AlignmentFlag.AlignVCenter),
                "暂无播放曲目",
            )
            painter.setFont(sub_font)
            painter.setPen(self.theme.slate_muted)
            painter.drawText(
                QRectF(x_pad, h / 2.0 + 8.0, w - 36.0, 26.0),
                int(align_flag | Qt.AlignmentFlag.AlignVCenter),
                "可通过左侧栏导入音乐文件、文件夹，或直接拖拽音频至此窗口",
            )
            return

        dirty = event.rect()
        dirty_top = float(dirty.top()) - 8.0
        dirty_bottom = float(dirty.bottom()) + 8.0

        main_painter = QPainter(self)
        main_painter.setClipRect(dirty)
        main_painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        main_painter.setRenderHint(QPainter.RenderHint.TextAntialiasing)
        main_painter.setRenderHint(QPainter.RenderHint.VerticalSubpixelPositioning)
        bp = main_painter

        viewport_center_y = h / 2.0
        primary_line_h = round(self.lyric_font_size * 1.68)
        sub_font_size = max(11, round(self.lyric_font_size * 0.65))
        sub_line_h = round(sub_font_size * 1.65)
        pad_v = round(self.lyric_font_size * 0.62)

        active_font = get_nordic_font(self.lyric_font_size + 1, QFont.Weight.DemiBold)
        inactive_font = get_nordic_font(self.lyric_font_size, QFont.Weight.Medium)
        active_fm = QFontMetricsF(active_font)
        inactive_fm = QFontMetricsF(inactive_font)
        sub_font = get_nordic_font(sub_font_size, QFont.Weight.Normal)
        sub_fm = QFontMetricsF(sub_font)

        align_flag = Qt.AlignmentFlag.AlignHCenter if self.align_center else Qt.AlignmentFlag.AlignLeft
        fade_zone = max(24.0, h * 0.20)

        for idx, line in enumerate(self.lyrics):
            st = self.visual_states[idx]
            screen_y = viewport_center_y + (st.visual_y - self.scroll_y)

            if screen_y + st.height < max(-40.0, dirty_top) or screen_y > min(h + 40.0, dirty_bottom):
                continue

            anchor_x = (w / 2.0) if self.align_center else 18.0
            anchor_y = screen_y + st.height / 2.0

            # 顶底 20% 区域平滑边缘羽化衰减（无需全屏离屏 QImage 与 DestinationIn 混合开销）
            edge_t = min(1.0, max(0.0, anchor_y / fade_zone), max(0.0, (h - anchor_y) / fade_zone))
            edge_fade = edge_t * edge_t * (3.0 - 2.0 * edge_t)
            eff_opacity = st.current_opacity * edge_fade
            if eff_opacity <= 0.01:
                continue

            bp.save()
            bp.setOpacity(eff_opacity)

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

            # 保持 1:1 矩阵变换以命中 Qt6 DirectWrite 硬件字形位图缓存，大幅降低 CPU 矢量光栅化开销
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
                primary_font = active_font if is_active else inactive_font
                fm = active_fm if is_active else inactive_fm
                bp.setFont(primary_font)
                primary_rect = QRectF(text_left, screen_y + pad_v, max_text_w, float(primary_line_h))
                elided_main = fm.elidedText(line.text, Qt.TextElideMode.ElideRight, max_text_w)
                text_flags = int(align_flag | Qt.AlignmentFlag.AlignVCenter)

                if is_active and self.syllable_mode:
                    prog = self._compute_line_progress_ratio(line)
                    actual_text_w = max(1.0, min(max_text_w, fm.horizontalAdvance(elided_main)))
                    start_x = (
                        (text_left + (max_text_w - actual_text_w) / 2.0)
                        if self.align_center
                        else text_left
                    )
                    unlit_col = QColor(self.theme.charcoal)
                    unlit_col.setAlphaF(0.52)
                    bp.setPen(unlit_col)
                    bp.drawText(primary_rect, text_flags, elided_main)

                    if prog > 0.005:
                        bp.save()
                        bp.setClipRect(
                            QRectF(start_x, primary_rect.top(), actual_text_w * prog, primary_rect.height()),
                            Qt.ClipOperation.IntersectClip,
                        )
                        bp.setPen(self.theme.charcoal)
                        bp.drawText(primary_rect, text_flags, elided_main)
                        bp.restore()
                else:
                    bp.setPen(self.theme.charcoal)
                    bp.drawText(primary_rect, text_flags, elided_main)

            if line.sub and self.show_translation:
                bp.setFont(sub_font)
                bp.setPen(self.theme.slate_dark if is_active else self.theme.slate_muted)
                sub_rect = QRectF(text_left, screen_y + pad_v + primary_line_h + 2.0, max_text_w, float(sub_line_h))
                elided_sub = sub_fm.elidedText(line.sub, Qt.TextElideMode.ElideRight, max_text_w)
                bp.drawText(
                    sub_rect,
                    int(align_flag | Qt.AlignmentFlag.AlignVCenter),
                    elided_sub,
                )

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
        bp.setOpacity(1.0)

        # 鼠标在歌词区悬浮时，左下角显示微弱快捷微调胶囊 (词距 -0.5s / +0.5s / 对齐切换)
        self._hud_buttons.clear()
        if self.underMouse():
            hud_items = [
                ("-0.5s", "offset_dec"),
                ("+0.5s", "offset_inc"),
                ("居中" if not self.align_center else "居左", "toggle_align"),
                ("歌词卡片", "open_poster"),
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

    def _show_context_menu(self, global_pos, line_idx: int) -> None:
        from PyQt6.QtWidgets import QMenu
        menu = QMenu(self)
        menu.setStyleSheet(
            f"QMenu {{ background: {self.theme.card_elevated.name()}; border: 1px solid {self.theme.stroke_outer.name()}; border-radius: 8px; padding: 4px; color: {self.theme.charcoal.name()}; font-size: 12px; }}"
            f"QMenu::item {{ padding: 6px 16px; border-radius: 4px; }}"
            f"QMenu::item:selected {{ background: {self.theme.oat_surface.name()}; }}"
        )
        act_poster = menu.addAction("生成歌词海报卡片...")
        menu.addSeparator()
        act_align = menu.addAction("切换对齐方式 (居中 / 居左)")
        act_trans = menu.addAction("切换双语翻译显示")
        selected = menu.exec(global_pos)
        if selected == act_poster:
            self.posterRequested.emit(line_idx)
        elif selected == act_align:
            self.set_align_center(not self.align_center)
            self._show_toast("居中对齐" if self.align_center else "左对齐")
        elif selected == act_trans:
            self.set_show_translation(not self.show_translation)

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.RightButton:
            hit = self._hit_test_line(event.position())
            line_idx = hit if hit >= 0 else self.active_index
            self._show_context_menu(event.globalPosition().toPoint(), line_idx)
            event.accept()
            return

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
                    elif act == "open_poster":
                        self.posterRequested.emit(self.active_index)
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
        self._wake_physics()
        event.accept()

    def _resume_auto_center(self) -> None:
        self.user_scrolling = False
        self._user_scroll_timer.stop()
        if self.active_index >= 0:
            self.center_line_smoothly(self.active_index, immediate=False)
        self.update()
