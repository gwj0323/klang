"""
音效均衡器、厅堂混响、倍速播放、歌词时间偏移与睡眠定时面板 (WinUI 3 Custom Frameless Modal)
彻底移除传统 QComboBox 与原生 QDialog 边框，提供：
1. 五大北欧音效预设分段切换 + 动态平滑过渡的 5 段参量均衡器推子 (60Hz / 230Hz / 910Hz / 3.6kHz / 12kHz) 与频响曲线
2. 北欧厅堂混响空间感无级滑块 + 实时音色试听按钮
3. 现代分段胶囊式倍速切换、歌词时间轴微调与睡眠定时控制
"""

from typing import List, Optional
from PyQt6.QtCore import Qt, QRectF, QPointF, QVariantAnimation, QEasingCurve, pyqtSignal
from PyQt6.QtGui import (
    QPainter,
    QPen,
    QColor,
    QFont,
    QPainterPath,
    QLinearGradient,
    QMouseEvent,
)
from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
)

from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font
from qt6_player.components.fluent_widgets import (
    WinUIModalDialog,
    WinUISegmentedControl,
)
from qt6_player.components.title_bar import WinUIChipButton
from qt6_player.components.transport_bar import WinUISlider
from qt6_player.services.audio_engine import SOUND_PROFILES, EQ_BAND_LABELS


class EqualizerRackWidget(QWidget):
    """
    5 段交互式均衡器推子与实时贝塞尔频响曲线画布 (-12dB ~ +12dB)
    支持预设切换时的丝滑曲线动画与鼠标直接拖拽任意频段推子。
    """

    gainsChangedByUser = pyqtSignal(list, bool)  # gains_db(5), is_release

    def __init__(self, initial_gains: List[float], parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.gains: List[float] = list((initial_gains + [0.0] * 5)[:5])
        self._start_gains: List[float] = list(self.gains)
        self._target_gains: List[float] = list(self.gains)
        self.dragging_band: int = -1
        self.hovered_band: int = -1

        self.setFixedHeight(156)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMouseTracking(True)

        self._anim = QVariantAnimation(self)
        self._anim.setDuration(280)
        self._anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._anim.valueChanged.connect(self._on_anim_val)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def animate_to_gains(self, target_gains: List[float]) -> None:
        self._start_gains = list(self.gains)
        self._target_gains = [max(-12.0, min(12.0, float(g))) for g in (target_gains + [0.0] * 5)[:5]]
        self._anim.stop()
        self._anim.setStartValue(0.0)
        self._anim.setEndValue(1.0)
        self._anim.start()

    def _on_anim_val(self, val: float) -> None:
        t = float(val)
        for i in range(5):
            self.gains[i] = self._start_gains[i] + (self._target_gains[i] - self._start_gains[i]) * t
        self.update()

    def _band_x(self, index: int) -> float:
        pad_x = 46.0
        usable_w = max(10.0, self.width() - pad_x * 2.0)
        return pad_x + usable_w * (index / 4.0)

    def _gain_to_y(self, db: float) -> float:
        top_y = 26.0
        bot_y = self.height() - 32.0
        ratio = (12.0 - max(-12.0, min(12.0, db))) / 24.0
        return top_y + ratio * (bot_y - top_y)

    def _y_to_gain(self, y: float) -> float:
        top_y = 26.0
        bot_y = self.height() - 32.0
        ratio = max(0.0, min(1.0, (y - top_y) / max(1.0, bot_y - top_y)))
        return round(12.0 - ratio * 24.0, 1)

    def _nearest_band(self, x: float) -> int:
        best_i = 0
        best_d = 1e9
        for i in range(5):
            d = abs(x - self._band_x(i))
            if d < best_d:
                best_d = d
                best_i = i
        return best_i if best_d <= 36.0 else -1

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            b = self._nearest_band(event.position().x())
            if b >= 0:
                self._anim.stop()
                self.dragging_band = b
                self.gains[b] = self._y_to_gain(event.position().y())
                self.gainsChangedByUser.emit(list(self.gains), False)
                self.update()
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if self.dragging_band >= 0:
            self.gains[self.dragging_band] = self._y_to_gain(event.position().y())
            self.gainsChangedByUser.emit(list(self.gains), False)
            self.update()
        else:
            b = self._nearest_band(event.position().x())
            if b != self.hovered_band:
                self.hovered_band = b
                self.update()
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton and self.dragging_band >= 0:
            self.gains[self.dragging_band] = self._y_to_gain(event.position().y())
            self.dragging_band = -1
            self.gainsChangedByUser.emit(list(self.gains), True)
            self.update()
        super().mouseReleaseEvent(event)

    def leaveEvent(self, event) -> None:
        self.hovered_band = -1
        self.update()
        super().leaveEvent(event)

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        rect = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        card_path = QPainterPath()
        card_path.addRoundedRect(rect, 9.0, 9.0)
        painter.fillPath(card_path, self.theme.card_elevated)
        painter.setPen(QPen(self.theme.stroke_outer, 1.0))
        painter.drawPath(card_path)

        top_y = 26.0
        bot_y = self.height() - 32.0
        mid_y = (top_y + bot_y) / 2.0

        # 0dB 基准虚线
        dash_pen = QPen(self.theme.divider, 1.0, Qt.PenStyle.DashLine)
        painter.setPen(dash_pen)
        painter.drawLine(QPointF(16.0, mid_y), QPointF(self.width() - 16.0, mid_y))

        # 绘制平滑频响曲线与底部柔光面积图
        pts = [QPointF(self._band_x(i), self._gain_to_y(self.gains[i])) for i in range(5)]
        curve = QPainterPath()
        curve.moveTo(pts[0])
        for i in range(4):
            c1 = QPointF((pts[i].x() + pts[i + 1].x()) * 0.5, pts[i].y())
            c2 = QPointF((pts[i].x() + pts[i + 1].x()) * 0.5, pts[i + 1].y())
            curve.cubicTo(c1, c2, pts[i + 1])

        area = QPainterPath(curve)
        area.lineTo(pts[-1].x(), bot_y)
        area.lineTo(pts[0].x(), bot_y)
        area.closeSubpath()

        grad = QLinearGradient(0.0, top_y, 0.0, bot_y)
        top_fill = QColor(self.theme.sage_accent)
        top_fill.setAlpha(52)
        bot_fill = QColor(self.theme.sage_accent)
        bot_fill.setAlpha(4)
        grad.setColorAt(0.0, top_fill)
        grad.setColorAt(1.0, bot_fill)
        painter.fillPath(area, grad)

        painter.setPen(QPen(self.theme.sage_accent, 1.8))
        painter.setBrush(Qt.BrushStyle.NoBrush)
        painter.drawPath(curve)

        # 绘制 5 条竖直滑槽、推子圆环、顶部 dB 读数与底部频段标签
        for i in range(5):
            bx = self._band_x(i)
            by = pts[i].y()

            # 滑轨槽
            track_col = QColor(self.theme.slate_muted)
            track_col.setAlpha(42)
            painter.setPen(QPen(track_col, 3.0, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
            painter.drawLine(QPointF(bx, top_y), QPointF(bx, bot_y))

            # 从 0dB 到当前推子的高亮柱
            painter.setPen(QPen(self.theme.sage_accent, 3.0, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
            painter.drawLine(QPointF(bx, mid_y), QPointF(bx, by))

            # 双环推子手柄
            outer_r = 7.5 if (i == self.dragging_band or i == self.hovered_band) else 6.5
            painter.setPen(QPen(self.theme.sage_border, 1.2))
            painter.setBrush(self.theme.mist_white)
            painter.drawEllipse(QPointF(bx, by), outer_r, outer_r)

            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(self.theme.sage_accent)
            painter.drawEllipse(QPointF(bx, by), 3.2, 3.2)

            # 顶部 dB 数值
            painter.setPen(self.theme.charcoal if abs(self.gains[i]) >= 0.2 else self.theme.slate_faint)
            painter.setFont(get_nordic_font(10, QFont.Weight.DemiBold, mono=True))
            painter.drawText(
                QRectF(bx - 24.0, 4.0, 48.0, 18.0),
                int(Qt.AlignmentFlag.AlignCenter),
                f"{self.gains[i]:+.1f}dB",
            )

            # 底部频率刻度
            painter.setPen(self.theme.slate_muted)
            painter.setFont(get_nordic_font(10, QFont.Weight.Medium, mono=True))
            painter.drawText(
                QRectF(bx - 24.0, self.height() - 24.0, 48.0, 18.0),
                int(Qt.AlignmentFlag.AlignCenter),
                EQ_BAND_LABELS[i],
            )


class AudioSettingsDialog(WinUIModalDialog):
    soundProfileChanged = pyqtSignal(str)
    customEqChanged = pyqtSignal(list, float, bool)  # gains_db, reverb_mix, preview_flag
    playbackRateChanged = pyqtSignal(float)
    lyricOffsetDeltaRequested = pyqtSignal(float)    # e.g. -0.5, 0.0 (reset), +0.5
    sleepTimerRequested = pyqtSignal(int)            # minutes (0 = off)

    _PRESET_NAMES = ["原声自然", "北欧厅堂", "温暖人声", "深沉低音", "清澈器乐"]
    _RATE_VALUES = [0.75, 1.0, 1.25, 1.5, 2.0]
    _SLEEP_MINS = [0, 15, 30, 60]

    def __init__(
        self,
        current_profile: str = "原声自然",
        current_gains: Optional[List[float]] = None,
        current_reverb: float = 0.06,
        current_rate: float = 1.0,
        current_offset: float = 0.0,
        sleep_remaining_sec: int = 0,
        theme: NordicThemeColors = DAYLIGHT_THEME,
        parent: Optional[QWidget] = None,
    ):
        super().__init__("声学均衡器与播放设置", 560, 456, theme=theme, parent=parent)
        self.current_offset = current_offset
        self.current_reverb = current_reverb
        init_gains = list(current_gains) if current_gains else list(SOUND_PROFILES.get(current_profile, ([0.0] * 5, 0.06))[0])
        self._setup_body(current_profile, init_gains, current_reverb, current_rate, sleep_remaining_sec)

    def _setup_body(
        self,
        current_profile: str,
        init_gains: List[float],
        current_reverb: float,
        current_rate: float,
        sleep_remaining_sec: int,
    ) -> None:
        vbox = self._root_vbox

        # 1. 五大音效预设分段胶囊
        header_eq = QHBoxLayout()
        lbl_eq = QLabel("声学预设", self)
        lbl_eq.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_eq.setStyleSheet(f"color: {self.theme.slate_dark.name()}; background: transparent;")
        header_eq.addWidget(lbl_eq)
        header_eq.addStretch(1)

        self.btn_preview = WinUIChipButton("试听当前音效", active=True, icon_kind="sparkle", parent=self)
        self.btn_preview.set_theme(self.theme)
        self.btn_preview.clicked.connect(
            lambda: self.customEqChanged.emit(list(self.eq_rack.gains), self.reverb_slider.ratio, True)
        )
        header_eq.addWidget(self.btn_preview)
        vbox.addLayout(header_eq)

        self.preset_tabs = WinUISegmentedControl(self._PRESET_NAMES, self)
        self.preset_tabs.set_theme(self.theme)
        if current_profile in self._PRESET_NAMES:
            self.preset_tabs.set_current_index(self._PRESET_NAMES.index(current_profile), animate=False)
        self.preset_tabs.currentIndexChanged.connect(self._on_preset_tab_changed)
        vbox.addWidget(self.preset_tabs)

        # 2. 5 段交互式参量均衡器推子架
        self.eq_rack = EqualizerRackWidget(init_gains, self)
        self.eq_rack.set_theme(self.theme)
        self.eq_rack.gainsChangedByUser.connect(self._on_eq_rack_dragged)
        vbox.addWidget(self.eq_rack)

        # 3. 北欧厅堂空间混响滑块
        rev_row = QHBoxLayout()
        lbl_rev = QLabel("厅堂空间混响", self)
        lbl_rev.setFont(get_nordic_font(12, QFont.Weight.Medium))
        lbl_rev.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        self.reverb_slider = WinUISlider(current_reverb, self)
        self.reverb_slider.set_theme(self.theme)
        self.lbl_rev_pct = QLabel(f"{int(round(current_reverb * 100))}%", self)
        self.lbl_rev_pct.setFixedWidth(38)
        self.lbl_rev_pct.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        self.lbl_rev_pct.setFont(get_nordic_font(11, QFont.Weight.Medium, mono=True))
        self.lbl_rev_pct.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        self.reverb_slider.valueChangedByUser.connect(self._on_reverb_changed)

        rev_row.addWidget(lbl_rev)
        rev_row.addSpacing(12)
        rev_row.addWidget(self.reverb_slider, 1)
        rev_row.addWidget(self.lbl_rev_pct)
        vbox.addLayout(rev_row)

        # 4. 播放倍速分段切换
        rate_row = QHBoxLayout()
        lbl_rate = QLabel("播放倍速", self)
        lbl_rate.setFont(get_nordic_font(12, QFont.Weight.Medium))
        lbl_rate.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        self.rate_tabs = WinUISegmentedControl(["0.75x", "1.0x", "1.25x", "1.5x", "2.0x"], self)
        self.rate_tabs.set_theme(self.theme)
        for i, r in enumerate(self._RATE_VALUES):
            if abs(r - current_rate) < 0.05:
                self.rate_tabs.set_current_index(i, animate=False)
                break
        self.rate_tabs.currentIndexChanged.connect(
            lambda idx: self.playbackRateChanged.emit(self._RATE_VALUES[idx])
        )
        rate_row.addWidget(lbl_rate)
        rate_row.addSpacing(16)
        rate_row.addWidget(self.rate_tabs, 1)
        vbox.addLayout(rate_row)

        # 5. 歌词时间轴微调 & 睡眠定时
        bottom_row = QHBoxLayout()
        self.lbl_offset = QLabel(f"歌词偏移 ({self.current_offset:+.1f}s)", self)
        self.lbl_offset.setFont(get_nordic_font(12, QFont.Weight.Medium))
        self.lbl_offset.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        bottom_row.addWidget(self.lbl_offset)
        bottom_row.addSpacing(6)

        for label, delta in (("-0.5s", -0.5), ("重置", 0.0), ("+0.5s", 0.5)):
            btn = WinUIChipButton(label, parent=self)
            btn.set_theme(self.theme)
            btn.clicked.connect(lambda checked=False, d=delta: self._on_offset_btn(d))
            bottom_row.addWidget(btn)

        bottom_row.addStretch(1)

        lbl_sleep = QLabel("定时", self)
        lbl_sleep.setFont(get_nordic_font(12, QFont.Weight.Medium))
        lbl_sleep.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        bottom_row.addWidget(lbl_sleep)

        self.sleep_tabs = WinUISegmentedControl(["关闭", "15分", "30分", "60分"], self)
        self.sleep_tabs.setFixedWidth(186)
        self.sleep_tabs.set_theme(self.theme)
        if sleep_remaining_sec > 0:
            rem_m = sleep_remaining_sec // 60
            for i, m in enumerate(self._SLEEP_MINS):
                if m > 0 and abs(rem_m - m) <= 10:
                    self.sleep_tabs.set_current_index(i, animate=False)
                    break
        self.sleep_tabs.currentIndexChanged.connect(
            lambda idx: self.sleepTimerRequested.emit(self._SLEEP_MINS[idx])
        )
        bottom_row.addWidget(self.sleep_tabs)
        vbox.addLayout(bottom_row)

    def _on_preset_tab_changed(self, idx: int) -> None:
        if not (0 <= idx < len(self._PRESET_NAMES)):
            return
        name = self._PRESET_NAMES[idx]
        gains, rev = SOUND_PROFILES[name]
        self.eq_rack.animate_to_gains(gains)
        self.reverb_slider.set_ratio(rev)
        self.lbl_rev_pct.setText(f"{int(round(rev * 100))}%")
        self.soundProfileChanged.emit(name)

    def _on_eq_rack_dragged(self, gains: List[float], is_release: bool) -> None:
        self.customEqChanged.emit(gains, self.reverb_slider.ratio, is_release)

    def _on_reverb_changed(self, ratio: float) -> None:
        self.lbl_rev_pct.setText(f"{int(round(ratio * 100))}%")
        self.customEqChanged.emit(list(self.eq_rack.gains), ratio, False)

    def _on_offset_btn(self, delta: float) -> None:
        if delta == 0.0:
            self.current_offset = 0.0
        else:
            self.current_offset = round(self.current_offset + delta, 1)
        self.lbl_offset.setText(f"歌词偏移 ({self.current_offset:+.1f}s)")
        self.lyricOffsetDeltaRequested.emit(delta)
