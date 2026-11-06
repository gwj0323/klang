from typing import List, Optional
import re
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
    QGraphicsOpacityEffect,
    QStackedLayout,
)

from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font
from qt6_player.components.fluent_widgets import (
    WinUIModalDialog,
    WinUISegmentedControl,
    WinUIToggleSwitch,
)
from qt6_player.components.title_bar import WinUIChipButton
from qt6_player.components.transport_bar import WinUISlider
from qt6_player.services.audio_engine import SOUND_PROFILES, EQ_BAND_LABELS


class EqualizerRackWidget(QWidget):
    gainsChangedByUser = pyqtSignal(list, bool)

    def __init__(self, initial_gains: List[float], parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.gains: List[float] = list((initial_gains + [0.0] * 5)[:5])
        self._start_gains: List[float] = list(self.gains)
        self._target_gains: List[float] = list(self.gains)
        self._spectrum_bars: List[float] = [0.0] * 24
        self.dragging_band: int = -1
        self.hovered_band: int = -1

        self.setFixedHeight(150)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMouseTracking(True)

        self._anim = QVariantAnimation(self)
        self._anim.setDuration(280)
        self._anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._anim.valueChanged.connect(self._on_anim_val)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def set_spectrum(self, bands: List[float]) -> None:
        if not bands:
            return
        for i in range(min(len(self._spectrum_bars), len(bands))):
            self._spectrum_bars[i] = self._spectrum_bars[i] * 0.45 + float(bands[i]) * 0.55
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

        n_spec = len(self._spectrum_bars)
        if n_spec > 0:
            spec_left = 24.0
            spec_w = max(10.0, self.width() - 48.0)
            bw = 5.0
            gap = (spec_w - n_spec * bw) / max(1, n_spec - 1)
            max_sh = (bot_y - top_y) * 0.85
            spec_col = QColor(self.theme.sage_accent)
            spec_col.setAlpha(28)
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(spec_col)
            for si, sv in enumerate(self._spectrum_bars):
                if sv > 0.02:
                    sh = sv * max_sh
                    sx = spec_left + si * (bw + gap)
                    painter.drawRoundedRect(QRectF(sx, bot_y - sh, bw, sh), 2.0, 2.0)

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

        for i in range(5):
            bx = self._band_x(i)
            by = pts[i].y()

            track_col = QColor(self.theme.slate_muted)
            track_col.setAlpha(42)
            painter.setPen(QPen(track_col, 3.0, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
            painter.drawLine(QPointF(bx, top_y), QPointF(bx, bot_y))

            painter.setPen(QPen(self.theme.sage_accent, 3.0, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
            painter.drawLine(QPointF(bx, mid_y), QPointF(bx, by))

            outer_r = 7.5 if (i == self.dragging_band or i == self.hovered_band) else 6.5
            painter.setPen(QPen(self.theme.sage_border, 1.2))
            painter.setBrush(self.theme.mist_white)
            painter.drawEllipse(QPointF(bx, by), outer_r, outer_r)

            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(self.theme.sage_accent)
            painter.drawEllipse(QPointF(bx, by), 3.2, 3.2)

            painter.setPen(self.theme.charcoal if abs(self.gains[i]) >= 0.2 else self.theme.slate_faint)
            painter.setFont(get_nordic_font(10, QFont.Weight.DemiBold, mono=True))
            painter.drawText(
                QRectF(bx - 24.0, 4.0, 48.0, 18.0),
                int(Qt.AlignmentFlag.AlignCenter),
                f"{self.gains[i]:+.1f}dB",
            )

            painter.setPen(self.theme.slate_muted)
            painter.setFont(get_nordic_font(10, QFont.Weight.Medium, mono=True))
            painter.drawText(
                QRectF(bx - 24.0, self.height() - 24.0, 48.0, 18.0),
                int(Qt.AlignmentFlag.AlignCenter),
                EQ_BAND_LABELS[i],
            )


class AudioSettingsDialog(WinUIModalDialog):
    # 音频设置信号
    soundProfileChanged = pyqtSignal(str)
    customEqChanged = pyqtSignal(list, float, bool)  # gains_db, reverb_mix, preview_flag
    playbackRateChanged = pyqtSignal(float)
    lyricOffsetDeltaRequested = pyqtSignal(float)
    sleepTimerRequested = pyqtSignal(int)
    dolbyAtmosModeChanged = pyqtSignal(str)
    spatialParamsChanged = pyqtSignal(float, float)
    outputModeChanged = pyqtSignal(str)

    # 界面与偏好设置信号
    themeChanged = pyqtSignal(str)
    fontSizeDeltaRequested = pyqtSignal(int)
    translationToggled = pyqtSignal(bool)
    syllableModeToggled = pyqtSignal(bool)
    immersiveFpsChanged = pyqtSignal(int)
    vinylModeToggled = pyqtSignal(bool)
    vinylModeChanged = pyqtSignal(str)
    backdropMaterialChanged = pyqtSignal(str)
    scrapeRequested = pyqtSignal()

    _PRESET_NAMES = ["默认原声", "流行人声", "低音增强", "清澈高音", "现场空间"]
    _RATE_VALUES = [0.75, 1.0, 1.25, 1.5, 2.0]
    _SLEEP_MINS = [0, 15, 30, 60]

    _OUTPUT_MODES = ["binaural", "surround_51", "surround_71"]
    _OUTPUT_LABELS = ["双耳渲染 (耳机)", "5.1 环绕声", "7.1 环绕声"]

    def __init__(
        self,
        current_profile: str = "默认原声",
        current_gains: Optional[List[float]] = None,
        current_reverb: float = 0.03,
        current_rate: float = 1.0,
        current_offset: float = 0.0,
        sleep_remaining_sec: int = 0,
        dolby_mode: str = "dolby_atmos",
        spatial_width: float = 1.35,
        spatial_height: float = 0.65,
        audio_output_mode: str = "binaural",
        actual_channels: int = 2,
        output_device_name: str = "",
        # 新增偏好参数
        default_tab: int = 0,
        current_theme: str = "daylight",
        current_font_size: int = 20,
        show_translation: bool = True,
        syllable_mode: bool = True,
        immersive_fps: int = 60,
        vinyl_mode: bool = False,
        backdrop_material: str = "mica",
        theme: NordicThemeColors = DAYLIGHT_THEME,
        parent: Optional[QWidget] = None,
    ):
        super().__init__("设置", 560, 670, theme=theme, parent=parent)
        self.current_offset = current_offset
        self.current_reverb = current_reverb
        self.current_dolby_mode = dolby_mode
        self.current_spatial_width = spatial_width
        self.current_spatial_height = spatial_height
        self.current_output_mode = audio_output_mode
        self.actual_channels = actual_channels
        self.output_device_name = output_device_name
        self.current_font_size = current_font_size

        init_gains = list(current_gains) if current_gains else list(SOUND_PROFILES.get(current_profile, ([0.0] * 5, 0.03))[0])
        self._setup_unified_body(
            current_profile,
            init_gains,
            current_reverb,
            current_rate,
            sleep_remaining_sec,
            dolby_mode,
            spatial_width,
            spatial_height,
            audio_output_mode,
            actual_channels,
            output_device_name,
            default_tab,
            current_theme,
            current_font_size,
            show_translation,
            syllable_mode,
            immersive_fps,
            vinyl_mode,
            backdrop_material,
        )

    def _clean_device_name(self, name: str) -> str:
        if not name:
            return ""
        clean = re.sub(r"\s*-\d+\s*\(.*?\)", "", name)
        clean = re.sub(r"\s*\(.*?\)", "", clean)
        return clean.strip() or name.strip()

    def _setup_unified_body(
        self,
        current_profile: str,
        init_gains: List[float],
        current_reverb: float,
        current_rate: float,
        sleep_remaining_sec: int,
        dolby_mode: str,
        spatial_width: float,
        spatial_height: float,
        audio_output_mode: str,
        actual_channels: int,
        output_device_name: str,
        default_tab: int,
        current_theme: str,
        current_font_size: int,
        show_translation: bool,
        syllable_mode: bool,
        immersive_fps: int,
        vinyl_mode: bool,
        backdrop_material: str = "mica",
    ) -> None:
        vbox = self._root_vbox
        vbox.setSpacing(10)

        # 顶部设置大类导航分段胶囊
        self.category_tabs = WinUISegmentedControl(["声音与音效", "界面与显示"], self)
        self.category_tabs.set_theme(self.theme)
        vbox.addWidget(self.category_tabs)

        self.tab_container = QWidget(self)
        self.stacked_layout = QStackedLayout(self.tab_container)
        self.stacked_layout.setContentsMargins(0, 0, 0, 0)

        # ----------------- Tab 0: 音效与播放 -----------------
        self.tab_audio = QWidget(self.tab_container)
        audio_vbox = QVBoxLayout(self.tab_audio)
        audio_vbox.setContentsMargins(0, 0, 0, 0)
        audio_vbox.setSpacing(6)

        # 1. 音效预设
        header_eq = QHBoxLayout()
        lbl_eq = QLabel("音效预设", self.tab_audio)
        lbl_eq.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_eq.setStyleSheet(f"color: {self.theme.slate_dark.name()}; background: transparent;")
        header_eq.addWidget(lbl_eq)
        header_eq.addStretch(1)

        self.btn_preview = WinUIChipButton("试听效果", active=True, icon_kind="speaker", parent=self.tab_audio)
        self.btn_preview.set_theme(self.theme)
        self.btn_preview.clicked.connect(
            lambda: self.customEqChanged.emit(list(self.eq_rack.gains), self.reverb_slider.ratio, True)
        )
        header_eq.addWidget(self.btn_preview)
        audio_vbox.addLayout(header_eq)

        self.preset_tabs = WinUISegmentedControl(self._PRESET_NAMES, self.tab_audio)
        self.preset_tabs.set_theme(self.theme)
        if current_profile in self._PRESET_NAMES:
            self.preset_tabs.set_current_index(self._PRESET_NAMES.index(current_profile), animate=False)
        else:
            legacy_map = {"原声自然": "默认原声", "北欧厅堂": "现场空间", "温暖人声": "流行人声", "深沉低音": "低音增强", "清澈器乐": "清澈高音"}
            mapped = legacy_map.get(current_profile)
            if mapped in self._PRESET_NAMES:
                self.preset_tabs.set_current_index(self._PRESET_NAMES.index(mapped), animate=False)
            else:
                self.preset_tabs.clear_selection()
        self.preset_tabs.currentIndexChanged.connect(self._on_preset_tab_changed)
        audio_vbox.addWidget(self.preset_tabs)

        # 2. 5 段均衡器推子
        self.eq_rack = EqualizerRackWidget(init_gains, self.tab_audio)
        self.eq_rack.set_theme(self.theme)
        self.eq_rack.gainsChangedByUser.connect(self._on_eq_rack_dragged)
        audio_vbox.addWidget(self.eq_rack)

        # 3. 空间音频与杜比全景声
        dolby_box = QVBoxLayout()
        dolby_box.setSpacing(6)

        dolby_header = QHBoxLayout()
        lbl_dolby_title = QLabel("空间音频", self.tab_audio)
        lbl_dolby_title.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_dolby_title.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        dolby_header.addWidget(lbl_dolby_title)

        self.lbl_atmos_badge = QLabel("DOLBY ATMOS", self.tab_audio)
        self.lbl_atmos_badge.setFont(get_nordic_font(9, QFont.Weight.Bold, mono=True))
        dolby_header.addWidget(self.lbl_atmos_badge)
        dolby_header.addStretch(1)

        is_dolby_on = (dolby_mode != "off")
        self.switch_dolby = WinUIToggleSwitch(checked=is_dolby_on, text_on="开启", text_off="关闭", parent=self.tab_audio)
        self.switch_dolby.set_theme(self.theme)
        self.switch_dolby.toggled.connect(self._on_dolby_switch_toggled)
        dolby_header.addWidget(self.switch_dolby)
        dolby_box.addLayout(dolby_header)

        self.spatial_controls_widget = QWidget(self.tab_audio)
        self.spatial_controls_opacity = QGraphicsOpacityEffect(self.spatial_controls_widget)
        self.spatial_controls_widget.setGraphicsEffect(self.spatial_controls_opacity)

        sc_vbox = QVBoxLayout(self.spatial_controls_widget)
        sc_vbox.setContentsMargins(0, 0, 0, 0)
        sc_vbox.setSpacing(6)

        mode_row = QHBoxLayout()
        lbl_mode = QLabel("效果模式", self.tab_audio)
        lbl_mode.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_mode.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        mode_row.addWidget(lbl_mode)
        mode_row.addSpacing(6)

        self.dolby_mode_tabs = WinUISegmentedControl(["杜比全景声", "空间立体声"], self.spatial_controls_widget)
        self.dolby_mode_tabs.set_theme(self.theme)
        init_mode_idx = 1 if dolby_mode == "spatial_stereo" else 0
        self.dolby_mode_tabs.set_current_index(init_mode_idx, animate=False)
        self.dolby_mode_tabs.currentIndexChanged.connect(self._on_dolby_sub_mode_changed)
        mode_row.addWidget(self.dolby_mode_tabs, 1)
        sc_vbox.addLayout(mode_row)

        params_row = QHBoxLayout()
        params_row.setSpacing(10)

        lbl_w = QLabel("声场宽度", self.tab_audio)
        lbl_w.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_w.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        params_row.addWidget(lbl_w)

        init_w_ratio = max(0.0, min(1.0, (spatial_width - 0.8) / 1.0))
        self.spatial_width_slider = WinUISlider(init_w_ratio, self.spatial_controls_widget)
        self.spatial_width_slider.set_theme(self.theme)
        self.spatial_width_slider.valueChangedByUser.connect(self._on_spatial_slider_changed)
        params_row.addWidget(self.spatial_width_slider, 1)

        self.lbl_width_val = QLabel(f"{int(round(spatial_width * 100))}%", self.tab_audio)
        self.lbl_width_val.setFixedWidth(38)
        self.lbl_width_val.setFont(get_nordic_font(11, QFont.Weight.Medium, mono=True))
        self.lbl_width_val.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        params_row.addWidget(self.lbl_width_val)

        params_row.addSpacing(8)

        lbl_h = QLabel("空间高度", self.tab_audio)
        lbl_h.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_h.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        params_row.addWidget(lbl_h)

        self.spatial_height_slider = WinUISlider(max(0.0, min(1.0, spatial_height)), self.spatial_controls_widget)
        self.spatial_height_slider.set_theme(self.theme)
        self.spatial_height_slider.valueChangedByUser.connect(self._on_spatial_slider_changed)
        params_row.addWidget(self.spatial_height_slider, 1)

        self.lbl_height_val = QLabel(f"{int(round(spatial_height * 100))}%", self.tab_audio)
        self.lbl_height_val.setFixedWidth(38)
        self.lbl_height_val.setFont(get_nordic_font(11, QFont.Weight.Medium, mono=True))
        self.lbl_height_val.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        params_row.addWidget(self.lbl_height_val)

        sc_vbox.addLayout(params_row)

        out_mode_row = QHBoxLayout()
        lbl_out = QLabel("输出模式", self.tab_audio)
        lbl_out.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_out.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        out_mode_row.addWidget(lbl_out)
        out_mode_row.addSpacing(6)

        self.output_tabs = WinUISegmentedControl(self._OUTPUT_LABELS, self.spatial_controls_widget)
        self.output_tabs.set_theme(self.theme)
        cur_out_idx = self._OUTPUT_MODES.index(audio_output_mode) if audio_output_mode in self._OUTPUT_MODES else 0
        self.output_tabs.set_current_index(cur_out_idx, animate=False)
        self.output_tabs.currentIndexChanged.connect(self._on_output_tab_changed)
        out_mode_row.addWidget(self.output_tabs, 1)
        sc_vbox.addLayout(out_mode_row)

        dolby_box.addWidget(self.spatial_controls_widget)

        self.lbl_output_tip = QLabel(self.tab_audio)
        self.lbl_output_tip.setFont(get_nordic_font(10, QFont.Weight.Normal))
        self.lbl_output_tip.setWordWrap(True)
        dolby_box.addWidget(self.lbl_output_tip)

        audio_vbox.addLayout(dolby_box)

        self._update_badge_state(is_dolby_on)
        self.spatial_controls_widget.setEnabled(is_dolby_on)
        self.spatial_controls_opacity.setOpacity(1.0 if is_dolby_on else 0.42)
        self._update_output_tip(audio_output_mode, actual_channels, output_device_name)

        # 4. 空间混响
        rev_row = QHBoxLayout()
        lbl_rev = QLabel("空间混响", self.tab_audio)
        lbl_rev.setFont(get_nordic_font(12, QFont.Weight.Medium))
        lbl_rev.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        rev_row.addWidget(lbl_rev)
        rev_row.addSpacing(16)

        self.reverb_slider = WinUISlider(max(0.0, min(1.0, current_reverb)), self.tab_audio)
        self.reverb_slider.set_theme(self.theme)
        self.reverb_slider.valueChangedByUser.connect(self._on_reverb_changed)
        rev_row.addWidget(self.reverb_slider, 1)

        self.lbl_rev_pct = QLabel(f"{int(round(current_reverb * 100))}%", self.tab_audio)
        self.lbl_rev_pct.setFixedWidth(38)
        self.lbl_rev_pct.setFont(get_nordic_font(11, QFont.Weight.Medium, mono=True))
        self.lbl_rev_pct.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        rev_row.addWidget(self.lbl_rev_pct)
        audio_vbox.addLayout(rev_row)

        # 5. 播放倍速
        rate_row = QHBoxLayout()
        lbl_rate = QLabel("播放倍速", self.tab_audio)
        lbl_rate.setFont(get_nordic_font(12, QFont.Weight.Medium))
        lbl_rate.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        self.rate_tabs = WinUISegmentedControl(["0.75x", "1.0x", "1.25x", "1.5x", "2.0x"], self.tab_audio)
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
        audio_vbox.addLayout(rate_row)

        # 6. 歌词微调 & 定时关闭
        bottom_row = QHBoxLayout()
        self.lbl_offset = QLabel(f"歌词微调 ({self.current_offset:+.1f}s)", self.tab_audio)
        self.lbl_offset.setFont(get_nordic_font(12, QFont.Weight.Medium))
        self.lbl_offset.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        bottom_row.addWidget(self.lbl_offset)
        bottom_row.addSpacing(6)

        for label, delta in (("-0.5s", -0.5), ("重置", 0.0), ("+0.5s", 0.5)):
            btn = WinUIChipButton(label, parent=self.tab_audio)
            btn.set_theme(self.theme)
            btn.clicked.connect(lambda checked=False, d=delta: self._on_offset_btn(d))
            bottom_row.addWidget(btn)

        bottom_row.addStretch(1)

        lbl_sleep = QLabel("定时关闭", self.tab_audio)
        lbl_sleep.setFont(get_nordic_font(12, QFont.Weight.Medium))
        lbl_sleep.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        bottom_row.addWidget(lbl_sleep)

        self.sleep_tabs = WinUISegmentedControl(["关闭", "15分", "30分", "60分"], self.tab_audio)
        self.sleep_tabs.setFixedWidth(186)
        self.sleep_tabs.set_theme(self.theme)
        if sleep_remaining_sec > 0:
            rem_m = sleep_remaining_sec / 60.0
            best_i = min(
                range(1, len(self._SLEEP_MINS)),
                key=lambda i: abs(rem_m - self._SLEEP_MINS[i]),
            )
            self.sleep_tabs.set_current_index(best_i, animate=False)
        self.sleep_tabs.currentIndexChanged.connect(
            lambda idx: self.sleepTimerRequested.emit(self._SLEEP_MINS[idx])
        )
        bottom_row.addWidget(self.sleep_tabs)
        audio_vbox.addLayout(bottom_row)

        self.stacked_layout.addWidget(self.tab_audio)

        # ----------------- Tab 1: 界面与视效 -----------------
        self.tab_ui = QWidget(self.tab_container)
        ui_vbox = QVBoxLayout(self.tab_ui)
        ui_vbox.setContentsMargins(0, 4, 0, 0)
        ui_vbox.setSpacing(14)

        # 1. 外观主题
        theme_box = QVBoxLayout()
        theme_box.setSpacing(6)
        lbl_th = QLabel("外观主题", self.tab_ui)
        lbl_th.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_th.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        theme_box.addWidget(lbl_th)

        self.theme_tabs = WinUISegmentedControl(["浅色模式", "深色模式"], self.tab_ui)
        self.theme_tabs.set_theme(self.theme)
        self.theme_tabs.set_current_index(1 if current_theme == "dusk" else 0, animate=False)
        self.theme_tabs.currentIndexChanged.connect(
            lambda idx: self.themeChanged.emit("dusk" if idx == 1 else "daylight")
        )
        theme_box.addWidget(self.theme_tabs)
        ui_vbox.addLayout(theme_box)

        # 窗口材质 (Windows 11 Mica / Acrylic / Solid)
        mat_box = QVBoxLayout()
        mat_box.setSpacing(6)
        lbl_mat = QLabel("窗口材质 (Windows 11)", self.tab_ui)
        lbl_mat.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_mat.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        mat_box.addWidget(lbl_mat)

        self.backdrop_tabs = WinUISegmentedControl(["标准纯色", "Win11 云母 (Mica)", "亚克力 (Acrylic)"], self.tab_ui)
        self.backdrop_tabs.set_theme(self.theme)
        mat_idx = 0 if backdrop_material == "solid" else (2 if backdrop_material == "acrylic" else 1)
        self.backdrop_tabs.set_current_index(mat_idx, animate=False)
        self.backdrop_tabs.currentIndexChanged.connect(
            lambda idx: self.backdropMaterialChanged.emit(["solid", "mica", "acrylic"][idx])
        )
        mat_box.addWidget(self.backdrop_tabs)

        lbl_mat_tip = QLabel("保留北欧原色温润质感的同时，透现系统桌面壁纸毛玻璃与云母光影", self.tab_ui)
        lbl_mat_tip.setFont(get_nordic_font(10, QFont.Weight.Normal))
        lbl_mat_tip.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        mat_box.addWidget(lbl_mat_tip)
        ui_vbox.addLayout(mat_box)

        # 2. 歌词显示设置 (字号、双语翻译、逐字光效)
        lyrics_box = QVBoxLayout()
        lyrics_box.setSpacing(8)

        lbl_lyr = QLabel("歌词设置", self.tab_ui)
        lbl_lyr.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_lyr.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        lyrics_box.addWidget(lbl_lyr)

        font_row = QHBoxLayout()
        lbl_fs = QLabel("歌词字号", self.tab_ui)
        lbl_fs.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_fs.setStyleSheet(f"color: {self.theme.slate_dark.name()}; background: transparent;")
        font_row.addWidget(lbl_fs)
        font_row.addStretch(1)

        btn_f_dec = WinUIChipButton("A -", fixed_w=36, parent=self.tab_ui)
        btn_f_dec.set_theme(self.theme)
        btn_f_dec.clicked.connect(lambda: self._adjust_font_size(-2))
        font_row.addWidget(btn_f_dec)

        self.lbl_fs_val = QLabel(f"{current_font_size}px", self.tab_ui)
        self.lbl_fs_val.setFixedWidth(44)
        self.lbl_fs_val.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.lbl_fs_val.setFont(get_nordic_font(11, QFont.Weight.DemiBold, mono=True))
        self.lbl_fs_val.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        font_row.addWidget(self.lbl_fs_val)

        btn_f_inc = WinUIChipButton("A +", fixed_w=36, parent=self.tab_ui)
        btn_f_inc.set_theme(self.theme)
        btn_f_inc.clicked.connect(lambda: self._adjust_font_size(2))
        font_row.addWidget(btn_f_inc)
        lyrics_box.addLayout(font_row)

        trans_row = QHBoxLayout()
        lbl_tr = QLabel("双语翻译", self.tab_ui)
        lbl_tr.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_tr.setStyleSheet(f"color: {self.theme.slate_dark.name()}; background: transparent;")
        trans_row.addWidget(lbl_tr)
        trans_row.addStretch(1)
        self.switch_trans = WinUIToggleSwitch(checked=show_translation, text_on="开启", text_off="关闭", parent=self.tab_ui)
        self.switch_trans.set_theme(self.theme)
        self.switch_trans.toggled.connect(self.translationToggled.emit)
        trans_row.addWidget(self.switch_trans)
        lyrics_box.addLayout(trans_row)

        syl_row = QHBoxLayout()
        lbl_sy = QLabel("逐字动效", self.tab_ui)
        lbl_sy.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_sy.setStyleSheet(f"color: {self.theme.slate_dark.name()}; background: transparent;")
        syl_row.addWidget(lbl_sy)
        syl_row.addStretch(1)
        self.switch_syl = WinUIToggleSwitch(checked=syllable_mode, text_on="开启", text_off="关闭", parent=self.tab_ui)
        self.switch_syl.set_theme(self.theme)
        self.switch_syl.toggled.connect(self.syllableModeToggled.emit)
        syl_row.addWidget(self.switch_syl)
        lyrics_box.addLayout(syl_row)

        ui_vbox.addLayout(lyrics_box)

        # 3. 沉浸全屏视界设置
        imm_box = QVBoxLayout()
        imm_box.setSpacing(8)

        lbl_imm = QLabel("全屏沉浸", self.tab_ui)
        lbl_imm.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_imm.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        imm_box.addWidget(lbl_imm)

        fps_row = QHBoxLayout()
        lbl_fps = QLabel("刷新率", self.tab_ui)
        lbl_fps.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_fps.setStyleSheet(f"color: {self.theme.slate_dark.name()}; background: transparent;")
        fps_row.addWidget(lbl_fps)
        fps_row.addSpacing(12)

        self.fps_tabs = WinUISegmentedControl(["60 FPS", "120 FPS"], self.tab_ui)
        self.fps_tabs.set_theme(self.theme)
        self.fps_tabs.set_current_index(1 if immersive_fps >= 100 else 0, animate=False)
        self.fps_tabs.currentIndexChanged.connect(
            lambda idx: self.immersiveFpsChanged.emit(120 if idx == 1 else 60)
        )
        fps_row.addWidget(self.fps_tabs, 1)
        imm_box.addLayout(fps_row)

        cover_row = QHBoxLayout()
        lbl_cv = QLabel("封面样式", self.tab_ui)
        lbl_cv.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_cv.setStyleSheet(f"color: {self.theme.slate_dark.name()}; background: transparent;")
        cover_row.addWidget(lbl_cv)
        cover_row.addSpacing(12)

        self.cover_mode_tabs = WinUISegmentedControl(["现代封面", "纯净黑胶", "极简唱机"], self.tab_ui)
        self.cover_mode_tabs.set_theme(self.theme)
        if vinyl_mode in ("arm", "vinyl_arm", 2):
            cur_cv_idx = 2
        elif vinyl_mode in ("pure", "vinyl_pure", True, 1):
            cur_cv_idx = 1
        else:
            cur_cv_idx = 0
        self.cover_mode_tabs.set_current_index(cur_cv_idx, animate=False)
        self.cover_mode_tabs.currentIndexChanged.connect(
            lambda idx: (
                self.vinylModeChanged.emit(["none", "pure", "arm"][idx]),
                self.vinylModeToggled.emit(idx > 0),
            )
        )
        cover_row.addWidget(self.cover_mode_tabs, 1)
        imm_box.addLayout(cover_row)

        ui_vbox.addLayout(imm_box)

        # 4. 在线词图刮削
        scrape_box = QVBoxLayout()
        scrape_box.setSpacing(6)

        lbl_sc = QLabel("歌词与封面", self.tab_ui)
        lbl_sc.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_sc.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        scrape_box.addWidget(lbl_sc)

        sc_row = QHBoxLayout()
        lbl_sc_desc = QLabel("联网匹配当前歌曲的封面与双语歌词", self.tab_ui)
        lbl_sc_desc.setFont(get_nordic_font(11, QFont.Weight.Normal))
        lbl_sc_desc.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        sc_row.addWidget(lbl_sc_desc)
        sc_row.addStretch(1)

        self.btn_scrape_action = WinUIChipButton("在线匹配", icon_kind="search", parent=self.tab_ui)
        self.btn_scrape_action.set_theme(self.theme)
        self.btn_scrape_action.clicked.connect(self._on_scrape_clicked)
        sc_row.addWidget(self.btn_scrape_action)
        scrape_box.addLayout(sc_row)

        ui_vbox.addLayout(scrape_box)
        ui_vbox.addStretch(1)

        self.stacked_layout.addWidget(self.tab_ui)

        vbox.addWidget(self.tab_container, 1)

        # 切换到默认 Tab
        self.category_tabs.set_current_index(default_tab, animate=False)
        self.stacked_layout.setCurrentIndex(default_tab)
        self.category_tabs.currentIndexChanged.connect(self._on_category_changed)

    def _on_category_changed(self, idx: int) -> None:
        self.stacked_layout.setCurrentIndex(idx)

    def _adjust_font_size(self, delta: int) -> None:
        self.current_font_size = max(12, min(36, self.current_font_size + delta))
        self.lbl_fs_val.setText(f"{self.current_font_size}px")
        self.fontSizeDeltaRequested.emit(delta)

    def _on_scrape_clicked(self) -> None:
        self.scrapeRequested.emit()

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
        self.preset_tabs.clear_selection()
        self.customEqChanged.emit(gains, self.reverb_slider.ratio, is_release)

    def update_spectrum(self, bands: List[float]) -> None:
        self.eq_rack.set_spectrum(bands)

    def _on_reverb_changed(self, ratio: float) -> None:
        self.lbl_rev_pct.setText(f"{int(round(ratio * 100))}%")
        self.customEqChanged.emit(list(self.eq_rack.gains), ratio, False)

    def _on_offset_btn(self, delta: float) -> None:
        if delta == 0.0:
            self.current_offset = 0.0
        else:
            self.current_offset = round(self.current_offset + delta, 1)
        self.lbl_offset.setText(f"歌词微调 ({self.current_offset:+.1f}s)")
        self.lyricOffsetDeltaRequested.emit(delta)

    def _on_dolby_switch_toggled(self, checked: bool) -> None:
        self.spatial_controls_widget.setEnabled(checked)
        self.spatial_controls_opacity.setOpacity(1.0 if checked else 0.42)
        if not checked:
            self.current_dolby_mode = "off"
            self._update_badge_state(False)
            self.dolbyAtmosModeChanged.emit("off")
        else:
            mode = "spatial_stereo" if self.dolby_mode_tabs.current_index == 1 else "dolby_atmos"
            self.current_dolby_mode = mode
            self._update_badge_state(True)
            self.dolbyAtmosModeChanged.emit(mode)
        self._update_output_tip(self.current_output_mode, self.actual_channels, self.output_device_name)

    def _on_dolby_sub_mode_changed(self, idx: int) -> None:
        if not self.switch_dolby.isChecked():
            return
        mode = "spatial_stereo" if idx == 1 else "dolby_atmos"
        self.current_dolby_mode = mode
        self._update_badge_state(True)
        self.dolbyAtmosModeChanged.emit(mode)

    def _update_badge_state(self, is_active: bool) -> None:
        if is_active:
            txt = "DOLBY ATMOS" if self.current_dolby_mode == "dolby_atmos" else "SPATIAL"
            self.lbl_atmos_badge.setText(txt)
            self.lbl_atmos_badge.setStyleSheet(
                "background: rgba(110, 130, 119, 0.16); color: #5B9E83; "
                "border: 1px solid rgba(110, 130, 119, 0.35); border-radius: 4px; padding: 1px 6px;"
            )
        else:
            self.lbl_atmos_badge.setText("已关闭")
            self.lbl_atmos_badge.setStyleSheet(
                f"background: rgba(150, 150, 150, 0.12); color: {self.theme.slate_muted.name()}; "
                "border: 1px solid rgba(150, 150, 150, 0.25); border-radius: 4px; padding: 1px 6px;"
            )

    def _on_spatial_slider_changed(self, _val: float) -> None:
        width = round(0.8 + self.spatial_width_slider.ratio * 1.0, 2)
        height = round(self.spatial_height_slider.ratio, 2)
        self.current_spatial_width = width
        self.current_spatial_height = height
        self.lbl_width_val.setText(f"{int(round(width * 100))}%")
        self.lbl_height_val.setText(f"{int(round(height * 100))}%")
        self.spatialParamsChanged.emit(width, height)

    def _on_output_tab_changed(self, idx: int) -> None:
        if 0 <= idx < len(self._OUTPUT_MODES):
            mode = self._OUTPUT_MODES[idx]
            self.current_output_mode = mode
            self.outputModeChanged.emit(mode)
            self._update_output_tip(mode, self.actual_channels, self.output_device_name)

    def _update_output_tip(self, mode: str, actual_channels: int, dev_name: str) -> None:
        clean_dev = self._clean_device_name(dev_name)
        dev_tag = f"[{clean_dev}] " if clean_dev else ""

        if not self.switch_dolby.isChecked():
            self.lbl_output_tip.setText(f"{dev_tag}空间音频已关闭，当前输出立体声直通")
            self.lbl_output_tip.setStyleSheet(
                f"color: {self.theme.slate_muted.name()}; background: transparent; padding-top: 2px;"
            )
            return

        if actual_channels > 2:
            self.lbl_output_tip.setText(
                f"{dev_tag}已激活物理 {actual_channels} 声道直通输出"
            )
            self.lbl_output_tip.setStyleSheet("color: #5B9E83; background: transparent; padding-top: 2px;")
        elif mode != "binaural":
            self.lbl_output_tip.setText(
                f"{dev_tag}当前设备为双声道，已通过双耳算法模拟环绕空间感"
            )
            self.lbl_output_tip.setStyleSheet(
                f"color: {self.theme.slate_muted.name()}; background: transparent; padding-top: 2px;"
            )
        else:
            self.lbl_output_tip.setText(
                f"{dev_tag}已开启双耳空间音频渲染 (推荐佩戴耳机使用)"
            )
            self.lbl_output_tip.setStyleSheet(
                f"color: {self.theme.slate_muted.name()}; background: transparent; padding-top: 2px;"
            )
