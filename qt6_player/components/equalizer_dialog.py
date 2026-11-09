from typing import List, Optional, Tuple
import re
import threading
import urllib.request
import json
from pathlib import Path
from PyQt6.QtCore import (
    Qt,
    QRectF,
    QPointF,
    QPoint,
    QSize,
    QVariantAnimation,
    QEasingCurve,
    pyqtSignal,
    QUrl,
)
from PyQt6.QtGui import (
    QPainter,
    QPen,
    QColor,
    QFont,
    QPainterPath,
    QLinearGradient,
    QMouseEvent,
    QPixmap,
    QDesktopServices,
)
from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
    QGraphicsOpacityEffect,
    QStackedLayout,
    QScrollArea,
    QFrame,
)

from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font
from qt6_player.components.fluent_widgets import (
    WinUIModalDialog,
    WinUISegmentedControl,
    WinUIToggleSwitch,
    WinUIFlyoutMenu,
)
from qt6_player.components.title_bar import WinUIChipButton
from qt6_player.components.transport_bar import WinUISlider
from qt6_player.services.audio_engine import SOUND_PROFILES, EQ_BAND_LABELS
from qt6_player.services.headphone_eq import HEADPHONE_PROFILES, HeadphoneProfile


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

        self.setFixedHeight(144)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMouseTracking(True)

        self._anim = QVariantAnimation(self)
        self._anim.setDuration(280)
        self._anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._anim.valueChanged.connect(self._on_anim_val)

    def sizeHint(self) -> QSize:
        return QSize(400, 144)

    def minimumSizeHint(self) -> QSize:
        return QSize(280, 144)

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
        top_y = 24.0
        bot_y = self.height() - 28.0
        ratio = (12.0 - max(-12.0, min(12.0, db))) / 24.0
        return top_y + ratio * (bot_y - top_y)

    def _y_to_gain(self, y: float) -> float:
        top_y = 24.0
        bot_y = self.height() - 28.0
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

        top_y = 24.0
        bot_y = self.height() - 28.0
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
                QRectF(bx - 24.0, self.height() - 22.0, 48.0, 16.0),
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
    outputDeviceChanged = pyqtSignal(str)
    crossfadeChanged = pyqtSignal(float)
    replayGainToggled = pyqtSignal(bool)
    vocalBalanceChanged = pyqtSignal(float)
    turntablePhysicsToggled = pyqtSignal(bool)
    vinylWarmthChanged = pyqtSignal(float)
    headphoneProfileChanged = pyqtSignal(str)
    neteaseQualityChanged = pyqtSignal(str)

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

    # 软件更新信号
    updateCheckResult = pyqtSignal(bool, str, str)  # has_update, latest_tag, release_url
    updateCheckFailed = pyqtSignal(str)              # error_msg

    _PRESET_NAMES = ["默认原声", "流行人声", "低音增强", "清澈高音", "现场空间"]
    _RATE_VALUES = [0.75, 1.0, 1.25, 1.5, 2.0]
    _SLEEP_MINS = [0, 15, 30, 60]
    _CROSSFADE_VALUES = [0.0, 1.0, 2.0, 3.0, 5.0]
    _CROSSFADE_LABELS = ["无缝 (0s)", "1 秒", "2 秒", "3 秒", "5 秒"]

    _OUTPUT_MODES = ["binaural", "surround_51", "surround_71"]
    _OUTPUT_LABELS = ["耳机双耳", "5.1 环绕", "7.1 环绕"]

    _NETEASE_QUALITIES = ["standard", "higher", "lossless", "hires", "jymaster"]
    _NETEASE_LABELS = ["标准 128k", "极高 320k", "无损 FLAC", "Hi-Res", "超清母带"]

    _CACHE_LIMIT_VALUES = [500, 1024, 2048, 5120, 0]
    _CACHE_LIMIT_LABELS = ["500 MB", "1 GB", "2 GB", "5 GB", "不限"]

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
        selected_device_id: str = "default",
        available_devices: Optional[List[Tuple[str, str]]] = None,
        crossfade_sec: float = 0.0,
        replay_gain_enabled: bool = False,
        vocal_balance: float = 0.0,
        turntable_physics: bool = True,
        vinyl_warmth: float = 0.0,
        headphone_profile_id: str = "bypass",
        # 新增偏好参数
        default_tab: int = 0,
        current_theme: str = "daylight",
        current_font_size: int = 20,
        show_translation: bool = True,
        syllable_mode: bool = True,
        immersive_fps: int = 60,
        vinyl_mode: bool = False,
        backdrop_material: str = "mica",
        current_netease_quality: str = "lossless",
        theme: NordicThemeColors = DAYLIGHT_THEME,
        parent: Optional[QWidget] = None,
    ):
        super().__init__("设置", 560, 680, theme=theme, backdrop_material=backdrop_material, parent=parent)
        self.backdrop_material = backdrop_material
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        self.current_offset = current_offset
        self.current_reverb = current_reverb
        self.current_dolby_mode = dolby_mode
        self.current_spatial_width = spatial_width
        self.current_spatial_height = spatial_height
        self.current_output_mode = audio_output_mode
        self.actual_channels = actual_channels
        self.output_device_name = output_device_name
        self.selected_device_id = selected_device_id or "default"
        self.available_devices: List[Tuple[str, str]] = list(available_devices) if available_devices else [("default", f"系统默认 ({output_device_name or '默认设备'})")]
        self.crossfade_sec = float(crossfade_sec)
        self.replay_gain_enabled = bool(replay_gain_enabled)
        self.vocal_balance = max(-1.0, min(1.0, float(vocal_balance)))
        self.turntable_physics = bool(turntable_physics)
        self.vinyl_warmth = max(0.0, min(1.0, float(vinyl_warmth)))
        self.headphone_profile_id = headphone_profile_id or "bypass"
        self.current_font_size = current_font_size
        self._device_flyout: Optional[WinUIFlyoutMenu] = None
        self._headphone_flyout: Optional[WinUIFlyoutMenu] = None

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
            current_netease_quality,
        )

    def _apply_scroll_style(self, scroll: QScrollArea) -> None:
        scroll.setStyleSheet(f"""
            QScrollArea {{
                background: transparent;
                border: none;
            }}
            QScrollArea > QWidget > QWidget {{
                background: transparent;
            }}
            QScrollBar:vertical {{
                border: none;
                background: transparent;
                width: 6px;
                margin: 0px 1px 0px 0px;
            }}
            QScrollBar::handle:vertical {{
                background: rgba(140, 150, 145, 0.35);
                min-height: 28px;
                border-radius: 3px;
            }}
            QScrollBar::handle:vertical:hover {{
                background: rgba(140, 150, 145, 0.65);
            }}
            QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {{
                height: 0px;
                border: none;
                background: transparent;
            }}
            QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical {{
                background: transparent;
            }}
        """)

    def _wrap_tab_in_scroll(self, content_widget: QWidget) -> QScrollArea:
        content_widget.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        content_widget.setStyleSheet("background: transparent;")

        scroll = QScrollArea(self.tab_container)
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        scroll.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)
        scroll.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        if scroll.viewport():
            scroll.viewport().setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
            scroll.viewport().setStyleSheet("background: transparent;")

        self._apply_scroll_style(scroll)
        scroll.setWidget(content_widget)
        return scroll

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
        current_netease_quality: str = "lossless",
    ) -> None:
        vbox = self._root_vbox
        vbox.setSpacing(14)

        # 顶部四栏分类切换：将音效、播放输出、界面外观与关于拆分为独立标签页
        self.category_tabs = WinUISegmentedControl(["音效调节", "播放与输出", "外观与歌词", "关于"], self)
        self.category_tabs.set_theme(self.theme)
        vbox.addWidget(self.category_tabs)

        self.tab_container = QWidget(self)
        self.stacked_layout = QStackedLayout(self.tab_container)
        self.stacked_layout.setContentsMargins(0, 0, 0, 0)

        # ----------------- Tab 0: 音效调节 -----------------
        self.tab_audio = QWidget(self.tab_container)
        audio_vbox = QVBoxLayout(self.tab_audio)
        audio_vbox.setContentsMargins(4, 4, 10, 16)
        audio_vbox.setSpacing(12)

        # 0. 耳机硬件声学校准 (AutoEq 哈曼曲线目标补偿)
        hp_vbox = QVBoxLayout()
        hp_vbox.setSpacing(6)

        hp_header = QHBoxLayout()
        lbl_hp = QLabel("耳机声学校准 (AutoEq)", self.tab_audio)
        lbl_hp.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_hp.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        hp_header.addWidget(lbl_hp)

        self.lbl_hp_badge = QLabel("HARMAN TARGET", self.tab_audio)
        self.lbl_hp_badge.setFont(get_nordic_font(9, QFont.Weight.Bold, mono=True))
        self._update_hp_badge_state(self.headphone_profile_id != "bypass")
        hp_header.addWidget(self.lbl_hp_badge)
        hp_header.addStretch(1)
        hp_vbox.addLayout(hp_header)

        self.btn_headphone_profile = WinUIChipButton(
            self._current_headphone_button_label(),
            active=(self.headphone_profile_id != "bypass"),
            icon_kind="speaker",
            parent=self.tab_audio,
        )
        self.btn_headphone_profile.set_theme(self.theme)
        self.btn_headphone_profile.clicked.connect(self._on_open_headphone_flyout)
        hp_vbox.addWidget(self.btn_headphone_profile)

        self.lbl_hp_desc = QLabel(self.tab_audio)
        self.lbl_hp_desc.setFont(get_nordic_font(10, QFont.Weight.Normal))
        self.lbl_hp_desc.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        self.lbl_hp_desc.setWordWrap(True)
        self._update_hp_desc(self.headphone_profile_id)
        hp_vbox.addWidget(self.lbl_hp_desc)
        audio_vbox.addLayout(hp_vbox)

        audio_vbox.addSpacing(2)

        # 1. 均衡器预设
        header_eq = QHBoxLayout()
        lbl_eq = QLabel("均衡器预设", self.tab_audio)
        lbl_eq.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_eq.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        header_eq.addWidget(lbl_eq)
        header_eq.addStretch(1)

        self.btn_preview = WinUIChipButton("试听", active=True, icon_kind="speaker", parent=self.tab_audio)
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
        audio_vbox.addSpacing(6)

        # 3. 混响强度
        rev_row = QHBoxLayout()
        rev_row.setSpacing(10)
        lbl_rev = QLabel("混响强度", self.tab_audio)
        lbl_rev.setFixedWidth(72)
        lbl_rev.setFont(get_nordic_font(12, QFont.Weight.Medium))
        lbl_rev.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        rev_row.addWidget(lbl_rev)

        self.reverb_slider = WinUISlider(max(0.0, min(1.0, current_reverb)), self.tab_audio)
        self.reverb_slider.set_theme(self.theme)
        self.reverb_slider.valueChangedByUser.connect(self._on_reverb_changed)
        rev_row.addWidget(self.reverb_slider, 1)

        self.lbl_rev_pct = QLabel(f"{int(round(current_reverb * 100))}%", self.tab_audio)
        self.lbl_rev_pct.setFixedWidth(42)
        self.lbl_rev_pct.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        self.lbl_rev_pct.setFont(get_nordic_font(11, QFont.Weight.Medium, mono=True))
        self.lbl_rev_pct.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        rev_row.addWidget(self.lbl_rev_pct)
        audio_vbox.addLayout(rev_row)
        audio_vbox.addSpacing(6)

        # 3.5 人声 ⟷ 伴奏无极天平 (向左消除人声保留伴奏 / 向右突出人声削弱伴奏)
        vocal_box = QVBoxLayout()
        vocal_box.setSpacing(4)
        vocal_row = QHBoxLayout()
        vocal_row.setSpacing(10)
        lbl_vocal = QLabel("人声天平", self.tab_audio)
        lbl_vocal.setFixedWidth(72)
        lbl_vocal.setFont(get_nordic_font(12, QFont.Weight.Medium))
        lbl_vocal.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        vocal_row.addWidget(lbl_vocal)

        init_vb_ratio = max(0.0, min(1.0, (self.vocal_balance + 1.0) * 0.5))
        self.vocal_balance_slider = WinUISlider(init_vb_ratio, self.tab_audio)
        self.vocal_balance_slider.set_theme(self.theme)
        self.vocal_balance_slider.valueChangedByUser.connect(self._on_vocal_balance_slider_changed)
        vocal_row.addWidget(self.vocal_balance_slider, 1)

        self.lbl_vocal_balance = QLabel(self._format_vocal_balance_text(self.vocal_balance), self.tab_audio)
        self.lbl_vocal_balance.setFixedWidth(74)
        self.lbl_vocal_balance.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        self.lbl_vocal_balance.setFont(get_nordic_font(10, QFont.Weight.DemiBold, mono=True))
        self.lbl_vocal_balance.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        vocal_row.addWidget(self.lbl_vocal_balance)

        self.btn_vocal_reset = WinUIChipButton("归零", fixed_w=44, parent=self.tab_audio)
        self.btn_vocal_reset.set_theme(self.theme)
        self.btn_vocal_reset.clicked.connect(self._on_reset_vocal_balance)
        vocal_row.addWidget(self.btn_vocal_reset)
        vocal_box.addLayout(vocal_row)

        lbl_vocal_hint = QLabel("向左隐去人声保留伴奏 · 向右突出人声削弱伴奏", self.tab_audio)
        lbl_vocal_hint.setFont(get_nordic_font(10, QFont.Weight.Normal))
        lbl_vocal_hint.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent; padding-left: 72px;")
        vocal_box.addWidget(lbl_vocal_hint)
        audio_vbox.addLayout(vocal_box)
        audio_vbox.addSpacing(6)

        # 4. 空间音频
        dolby_box = QVBoxLayout()
        dolby_box.setSpacing(10)

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
        sc_vbox.setSpacing(10)

        mode_row = QHBoxLayout()
        lbl_mode = QLabel("音效模式", self.spatial_controls_widget)
        lbl_mode.setFixedWidth(68)
        lbl_mode.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_mode.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        mode_row.addWidget(lbl_mode)

        self.dolby_mode_tabs = WinUISegmentedControl(["杜比全景声", "空间立体声"], self.spatial_controls_widget)
        self.dolby_mode_tabs.set_theme(self.theme)
        init_mode_idx = 1 if dolby_mode == "spatial_stereo" else 0
        self.dolby_mode_tabs.set_current_index(init_mode_idx, animate=False)
        self.dolby_mode_tabs.currentIndexChanged.connect(self._on_dolby_sub_mode_changed)
        mode_row.addWidget(self.dolby_mode_tabs, 1)
        sc_vbox.addLayout(mode_row)

        width_row = QHBoxLayout()
        lbl_w = QLabel("声场宽度", self.spatial_controls_widget)
        lbl_w.setFixedWidth(68)
        lbl_w.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_w.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        width_row.addWidget(lbl_w)

        init_w_ratio = max(0.0, min(1.0, (spatial_width - 0.8) / 1.0))
        self.spatial_width_slider = WinUISlider(init_w_ratio, self.spatial_controls_widget)
        self.spatial_width_slider.set_theme(self.theme)
        self.spatial_width_slider.valueChangedByUser.connect(self._on_spatial_slider_changed)
        width_row.addWidget(self.spatial_width_slider, 1)

        self.lbl_width_val = QLabel(f"{int(round(spatial_width * 100))}%", self.spatial_controls_widget)
        self.lbl_width_val.setFixedWidth(42)
        self.lbl_width_val.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        self.lbl_width_val.setFont(get_nordic_font(11, QFont.Weight.Medium, mono=True))
        self.lbl_width_val.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        width_row.addWidget(self.lbl_width_val)
        sc_vbox.addLayout(width_row)

        height_row = QHBoxLayout()
        lbl_h = QLabel("空间高度", self.spatial_controls_widget)
        lbl_h.setFixedWidth(68)
        lbl_h.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_h.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        height_row.addWidget(lbl_h)

        self.spatial_height_slider = WinUISlider(max(0.0, min(1.0, spatial_height)), self.spatial_controls_widget)
        self.spatial_height_slider.set_theme(self.theme)
        self.spatial_height_slider.valueChangedByUser.connect(self._on_spatial_slider_changed)
        height_row.addWidget(self.spatial_height_slider, 1)

        self.lbl_height_val = QLabel(f"{int(round(spatial_height * 100))}%", self.spatial_controls_widget)
        self.lbl_height_val.setFixedWidth(42)
        self.lbl_height_val.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        self.lbl_height_val.setFont(get_nordic_font(11, QFont.Weight.Medium, mono=True))
        self.lbl_height_val.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        height_row.addWidget(self.lbl_height_val)
        sc_vbox.addLayout(height_row)

        out_mode_row = QHBoxLayout()
        lbl_out = QLabel("输出声道", self.spatial_controls_widget)
        lbl_out.setFixedWidth(68)
        lbl_out.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_out.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        out_mode_row.addWidget(lbl_out)

        self.output_tabs = WinUISegmentedControl(self._OUTPUT_LABELS, self.spatial_controls_widget)
        self.output_tabs.set_theme(self.theme)
        cur_out_idx = self._OUTPUT_MODES.index(audio_output_mode) if audio_output_mode in self._OUTPUT_MODES else 0
        self.output_tabs.set_current_index(cur_out_idx, animate=False)
        self.output_tabs.currentIndexChanged.connect(self._on_output_tab_changed)
        out_mode_row.addWidget(self.output_tabs, 1)
        sc_vbox.addLayout(out_mode_row)

        dolby_box.addWidget(self.spatial_controls_widget)
        audio_vbox.addLayout(dolby_box)
        audio_vbox.addStretch(1)

        self._update_badge_state(is_dolby_on)
        self.spatial_controls_widget.setEnabled(is_dolby_on)
        self.spatial_controls_opacity.setOpacity(1.0 if is_dolby_on else 0.58)

        self.scroll_audio = self._wrap_tab_in_scroll(self.tab_audio)
        self.stacked_layout.addWidget(self.scroll_audio)

        # ----------------- Tab 1: 播放与输出 -----------------
        self.tab_playback = QWidget(self.tab_container)
        play_vbox = QVBoxLayout(self.tab_playback)
        play_vbox.setContentsMargins(4, 6, 10, 16)
        play_vbox.setSpacing(16)

        # 1. 输出设备选择
        dev_box = QVBoxLayout()
        dev_box.setSpacing(6)
        lbl_dev = QLabel("音频输出设备", self.tab_playback)
        lbl_dev.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_dev.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        dev_box.addWidget(lbl_dev)

        self.btn_output_device = WinUIChipButton(
            self._current_device_button_label(),
            active=False,
            icon_kind="speaker",
            parent=self.tab_playback,
        )
        self.btn_output_device.set_theme(self.theme)
        self.btn_output_device.clicked.connect(self._on_open_device_flyout)
        dev_box.addWidget(self.btn_output_device)

        self.lbl_output_tip = QLabel(self.tab_playback)
        self.lbl_output_tip.setFont(get_nordic_font(10, QFont.Weight.Normal))
        self.lbl_output_tip.setWordWrap(True)
        dev_box.addWidget(self.lbl_output_tip)
        play_vbox.addLayout(dev_box)
        self._update_output_tip(audio_output_mode, actual_channels, output_device_name)

        # 2. 切歌过渡 (Gapless / Crossfade)
        xfade_box = QVBoxLayout()
        xfade_box.setSpacing(6)
        lbl_xfade = QLabel("切歌淡入淡出", self.tab_playback)
        lbl_xfade.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_xfade.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        xfade_box.addWidget(lbl_xfade)

        self.crossfade_tabs = WinUISegmentedControl(self._CROSSFADE_LABELS, self.tab_playback)
        self.crossfade_tabs.set_theme(self.theme)
        best_xf_idx = min(
            range(len(self._CROSSFADE_VALUES)),
            key=lambda i: abs(self.crossfade_sec - self._CROSSFADE_VALUES[i]),
        )
        self.crossfade_tabs.set_current_index(best_xf_idx, animate=False)
        self.crossfade_tabs.currentIndexChanged.connect(
            lambda idx: self.crossfadeChanged.emit(self._CROSSFADE_VALUES[idx])
        )
        xfade_box.addWidget(self.crossfade_tabs)
        play_vbox.addLayout(xfade_box)

        # 2.5 网易云音乐在线播放音质 (NetEase VIP Audio Quality)
        ne_box = QVBoxLayout()
        ne_box.setSpacing(6)
        ne_header = QHBoxLayout()
        lbl_ne = QLabel("网易云播放音质", self.tab_playback)
        lbl_ne.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_ne.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        ne_header.addWidget(lbl_ne)

        lbl_vip = QLabel("VIP HIGH-RES", self.tab_playback)
        lbl_vip.setFont(get_nordic_font(9, QFont.Weight.Bold, mono=True))
        lbl_vip.setStyleSheet(
            f"background: {self.theme.sage_accent.name()}; color: #FFFFFF; "
            "padding: 1px 6px; border-radius: 4px; letter-spacing: 0.5px;"
        )
        ne_header.addWidget(lbl_vip)
        ne_header.addStretch(1)

        from qt6_player.services.netease_service import netease_manager
        self.lbl_ne_account = QLabel(self.tab_playback)
        self.lbl_ne_account.setFont(get_nordic_font(10, QFont.Weight.Normal))
        self.lbl_ne_account.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        ne_header.addWidget(self.lbl_ne_account)

        self.btn_ne_account_action = WinUIChipButton("退出登录", fixed_w=78, parent=self.tab_playback)
        self.btn_ne_account_action.set_theme(self.theme)
        ne_header.addWidget(self.btn_ne_account_action)

        def _update_ne_account_ui():
            if netease_manager.is_logged_in:
                nick = netease_manager.user_profile.get("nickname") or "网易云用户"
                self.lbl_ne_account.setText(f"账号: {nick}")
                self.btn_ne_account_action.setText("退出登录")
            else:
                self.lbl_ne_account.setText("未登录账号")
                self.btn_ne_account_action.setText("扫码登录")

        def _on_ne_account_action():
            if netease_manager.is_logged_in:
                netease_manager.logout()
                _update_ne_account_ui()
                self.lbl_cache_size.setText(f"已占用: {netease_manager.get_cache_size_str()}")
            else:
                from qt6_player.components.netease_login_dialog import NeteaseLoginDialog
                dlg = NeteaseLoginDialog(theme=self.theme, parent=self.window())
                dlg.exec()
                _update_ne_account_ui()
                self.lbl_cache_size.setText(f"已占用: {netease_manager.get_cache_size_str()}")

        self.btn_ne_account_action.clicked.connect(_on_ne_account_action)
        _update_ne_account_ui()
        netease_manager.loginStatusChanged.connect(lambda _in, _prof: _update_ne_account_ui())

        ne_box.addLayout(ne_header)

        self.netease_quality_tabs = WinUISegmentedControl(self._NETEASE_LABELS, self.tab_playback)
        self.netease_quality_tabs.set_theme(self.theme)
        init_q_idx = self._NETEASE_QUALITIES.index(current_netease_quality) if current_netease_quality in self._NETEASE_QUALITIES else 2
        self.netease_quality_tabs.set_current_index(init_q_idx, animate=False)
        self.netease_quality_tabs.currentIndexChanged.connect(
            lambda idx: self.neteaseQualityChanged.emit(self._NETEASE_QUALITIES[idx])
        )
        ne_box.addWidget(self.netease_quality_tabs)

        lbl_ne_desc = QLabel("支持最高 24-bit/192kHz 无损母带流传输与离线缓存（需 VIP 账号权限）", self.tab_playback)
        lbl_ne_desc.setFont(get_nordic_font(10, QFont.Weight.Normal))
        lbl_ne_desc.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        ne_box.addWidget(lbl_ne_desc)
        play_vbox.addLayout(ne_box)

        # 2.6 网易云本地缓存管理 (Cache Quota & Cleanup)
        cache_box = QVBoxLayout()
        cache_box.setSpacing(6)

        cache_header = QHBoxLayout()
        self.lbl_cache_title = QLabel("网易云本地缓存", self.tab_playback)
        self.lbl_cache_title.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.lbl_cache_title.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        cache_header.addWidget(self.lbl_cache_title)

        from qt6_player.services.netease_service import netease_manager
        cur_limit = getattr(netease_manager, "cache_limit_mb", 2048)
        self.lbl_cache_quota_info = QLabel("缓存配额上限", self.tab_playback)
        self.lbl_cache_quota_info.setFont(get_nordic_font(10, QFont.Weight.Normal))
        self.lbl_cache_quota_info.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        cache_header.addWidget(self.lbl_cache_quota_info)
        cache_header.addStretch(1)
        cache_box.addLayout(cache_header)

        self.cache_limit_tabs = WinUISegmentedControl(self._CACHE_LIMIT_LABELS, self.tab_playback)
        self.cache_limit_tabs.set_theme(self.theme)
        if cur_limit in self._CACHE_LIMIT_VALUES:
            init_c_idx = self._CACHE_LIMIT_VALUES.index(cur_limit)
        else:
            init_c_idx = 2  # default 2GB
        self.cache_limit_tabs.set_current_index(init_c_idx, animate=False)
        cache_box.addWidget(self.cache_limit_tabs)

        cache_action_row = QHBoxLayout()
        cache_action_row.setSpacing(10)
        self.lbl_cache_size = QLabel(f"已占用: {netease_manager.get_cache_size_str()}", self.tab_playback)
        self.lbl_cache_size.setFont(get_nordic_font(10, QFont.Weight.Normal))
        self.lbl_cache_size.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        cache_action_row.addWidget(self.lbl_cache_size, 1)

        self.btn_clear_cache = WinUIChipButton("清空缓存", fixed_w=78, parent=self.tab_playback)
        self.btn_clear_cache.set_theme(self.theme)
        cache_action_row.addWidget(self.btn_clear_cache)
        cache_box.addLayout(cache_action_row)

        def _on_cache_limit_changed(idx: int):
            limit_val = self._CACHE_LIMIT_VALUES[idx]
            netease_manager.set_cache_limit_mb(limit_val)
            self.lbl_cache_size.setText(f"已占用: {netease_manager.get_cache_size_str()}")

        def _on_clear_cache_clicked():
            cnt, freed = netease_manager.clear_cache()
            self.lbl_cache_size.setText(f"已占用: {netease_manager.get_cache_size_str()}")
            mb_freed = freed / (1024 * 1024)
            self.lbl_cache_quota_info.setText(f"已清空 {cnt} 首曲目 (释放 {mb_freed:.1f} MB)")

        self.cache_limit_tabs.currentIndexChanged.connect(_on_cache_limit_changed)
        self.btn_clear_cache.clicked.connect(_on_clear_cache_clicked)

        play_vbox.addLayout(cache_box)

        # 3. 音量平衡 (ReplayGain)
        rg_row = QHBoxLayout()
        rg_text_col = QVBoxLayout()
        rg_text_col.setSpacing(2)
        lbl_rg = QLabel("音量平衡 (ReplayGain)", self.tab_playback)
        lbl_rg.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_rg.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        rg_text_col.addWidget(lbl_rg)

        lbl_rg_desc = QLabel("自动平衡不同歌曲的音量差异，避免切歌时忽大忽小", self.tab_playback)
        lbl_rg_desc.setFont(get_nordic_font(10, QFont.Weight.Normal))
        lbl_rg_desc.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        rg_text_col.addWidget(lbl_rg_desc)
        rg_row.addLayout(rg_text_col, 1)

        self.switch_replay_gain = WinUIToggleSwitch(
            checked=self.replay_gain_enabled,
            text_on="开启",
            text_off="关闭",
            parent=self.tab_playback,
        )
        self.switch_replay_gain.set_theme(self.theme)
        self.switch_replay_gain.toggled.connect(self.replayGainToggled.emit)
        rg_row.addWidget(self.switch_replay_gain)
        play_vbox.addLayout(rg_row)

        # 3.5 黑胶物理起停与模拟味 (Turntable Physics & Analog Warmth)
        tt_box = QVBoxLayout()
        tt_box.setSpacing(8)
        tt_header = QHBoxLayout()
        tt_text_col = QVBoxLayout()
        tt_text_col.setSpacing(2)
        lbl_tt = QLabel("黑胶物理断电起停", self.tab_playback)
        lbl_tt.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_tt.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        tt_text_col.addWidget(lbl_tt)

        lbl_tt_desc = QLabel("模拟唱盘断电降速与启动加速效果，避免声音生硬切断", self.tab_playback)
        lbl_tt_desc.setFont(get_nordic_font(10, QFont.Weight.Normal))
        lbl_tt_desc.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        tt_text_col.addWidget(lbl_tt_desc)
        tt_header.addLayout(tt_text_col, 1)

        self.switch_turntable = WinUIToggleSwitch(
            checked=self.turntable_physics,
            text_on="开启",
            text_off="关闭",
            parent=self.tab_playback,
        )
        self.switch_turntable.set_theme(self.theme)
        self.switch_turntable.toggled.connect(self.turntablePhysicsToggled.emit)
        tt_header.addWidget(self.switch_turntable)
        tt_box.addLayout(tt_header)

        warmth_row = QHBoxLayout()
        lbl_warm = QLabel("黑胶模拟质感 (微底噪与电子管温暖度)", self.tab_playback)
        lbl_warm.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_warm.setStyleSheet(f"color: {self.theme.slate_dark.name()}; background: transparent;")
        warmth_row.addWidget(lbl_warm)

        self.vinyl_warmth_slider = WinUISlider(self.vinyl_warmth, self.tab_playback)
        self.vinyl_warmth_slider.set_theme(self.theme)
        self.vinyl_warmth_slider.valueChangedByUser.connect(self._on_vinyl_warmth_changed)
        warmth_row.addWidget(self.vinyl_warmth_slider, 1)

        self.lbl_warmth_pct = QLabel(
            "关闭" if self.vinyl_warmth < 0.02 else f"{int(round(self.vinyl_warmth * 100))}%",
            self.tab_playback,
        )
        self.lbl_warmth_pct.setFixedWidth(44)
        self.lbl_warmth_pct.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        self.lbl_warmth_pct.setFont(get_nordic_font(11, QFont.Weight.Medium, mono=True))
        self.lbl_warmth_pct.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        warmth_row.addWidget(self.lbl_warmth_pct)
        tt_box.addLayout(warmth_row)
        play_vbox.addLayout(tt_box)

        # 4. 播放倍速
        rate_box = QVBoxLayout()
        rate_box.setSpacing(6)
        lbl_rate = QLabel("播放速度", self.tab_playback)
        lbl_rate.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_rate.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        rate_box.addWidget(lbl_rate)

        self.rate_tabs = WinUISegmentedControl(["0.75x", "1.0x", "1.25x", "1.5x", "2.0x"], self.tab_playback)
        self.rate_tabs.set_theme(self.theme)
        for i, r in enumerate(self._RATE_VALUES):
            if abs(r - current_rate) < 0.05:
                self.rate_tabs.set_current_index(i, animate=False)
                break
        self.rate_tabs.currentIndexChanged.connect(
            lambda idx: self.playbackRateChanged.emit(self._RATE_VALUES[idx])
        )
        rate_box.addWidget(self.rate_tabs)
        play_vbox.addLayout(rate_box)

        # 5. 定时关闭
        sleep_box = QVBoxLayout()
        sleep_box.setSpacing(6)
        lbl_sleep = QLabel("定时停止播放", self.tab_playback)
        lbl_sleep.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_sleep.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        sleep_box.addWidget(lbl_sleep)

        self.sleep_tabs = WinUISegmentedControl(["关闭", "15 分钟", "30 分钟", "60 分钟"], self.tab_playback)
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
        sleep_box.addWidget(self.sleep_tabs)
        play_vbox.addLayout(sleep_box)

        # 6. 歌词时间微调
        offset_row = QHBoxLayout()
        self.lbl_offset = QLabel(f"歌词时间偏移 ({self.current_offset:+.1f}s)", self.tab_playback)
        self.lbl_offset.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.lbl_offset.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        offset_row.addWidget(self.lbl_offset)
        offset_row.addStretch(1)

        for label, delta in (("-0.5s", -0.5), ("重置", 0.0), ("+0.5s", 0.5)):
            btn = WinUIChipButton(label, parent=self.tab_playback)
            btn.set_theme(self.theme)
            btn.clicked.connect(lambda checked=False, d=delta: self._on_offset_btn(d))
            offset_row.addWidget(btn)
        play_vbox.addLayout(offset_row)
        play_vbox.addStretch(1)

        self.scroll_playback = self._wrap_tab_in_scroll(self.tab_playback)
        self.stacked_layout.addWidget(self.scroll_playback)

        # ----------------- Tab 2: 外观与歌词 -----------------
        self.tab_ui = QWidget(self.tab_container)
        ui_vbox = QVBoxLayout(self.tab_ui)
        ui_vbox.setContentsMargins(4, 6, 10, 16)
        ui_vbox.setSpacing(15)

        # 1. 外观主题
        theme_box = QVBoxLayout()
        theme_box.setSpacing(6)
        lbl_th = QLabel("颜色主题", self.tab_ui)
        lbl_th.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_th.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        theme_box.addWidget(lbl_th)

        self.theme_tabs = WinUISegmentedControl(["浅色", "深色"], self.tab_ui)
        self.theme_tabs.set_theme(self.theme)
        self.theme_tabs.set_current_index(1 if current_theme == "dusk" else 0, animate=False)
        self.theme_tabs.currentIndexChanged.connect(
            lambda idx: self.themeChanged.emit("dusk" if idx == 1 else "daylight")
        )
        theme_box.addWidget(self.theme_tabs)
        ui_vbox.addLayout(theme_box)

        # 2. 窗口背景效果
        mat_box = QVBoxLayout()
        mat_box.setSpacing(6)
        lbl_mat = QLabel("窗口背景效果 (Windows 11)", self.tab_ui)
        lbl_mat.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_mat.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        mat_box.addWidget(lbl_mat)

        self.backdrop_tabs = WinUISegmentedControl(["纯色", "云母 (Mica)", "亚克力 (Acrylic)"], self.tab_ui)
        self.backdrop_tabs.set_theme(self.theme)
        mat_idx = 0 if backdrop_material == "solid" else (2 if backdrop_material == "acrylic" else 1)
        self.backdrop_tabs.set_current_index(mat_idx, animate=False)

        def _on_mat_changed(idx: int):
            mat_mode = ["solid", "mica", "acrylic"][idx]
            self.set_backdrop_material(mat_mode)
            self.backdropMaterialChanged.emit(mat_mode)

        self.backdrop_tabs.currentIndexChanged.connect(_on_mat_changed)
        mat_box.addWidget(self.backdrop_tabs)
        ui_vbox.addLayout(mat_box)

        # 3. 歌词设置
        lyrics_box = QVBoxLayout()
        lyrics_box.setSpacing(8)

        lbl_lyr = QLabel("歌词显示", self.tab_ui)
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
        lbl_tr = QLabel("显示歌词翻译", self.tab_ui)
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
        lbl_sy = QLabel("逐字高亮动画", self.tab_ui)
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

        # 4. 全屏模式
        imm_box = QVBoxLayout()
        imm_box.setSpacing(8)

        lbl_imm = QLabel("全屏模式", self.tab_ui)
        lbl_imm.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_imm.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        imm_box.addWidget(lbl_imm)

        fps_row = QHBoxLayout()
        lbl_fps = QLabel("动画帧率", self.tab_ui)
        lbl_fps.setFixedWidth(68)
        lbl_fps.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_fps.setStyleSheet(f"color: {self.theme.slate_dark.name()}; background: transparent;")
        fps_row.addWidget(lbl_fps)

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
        lbl_cv.setFixedWidth(68)
        lbl_cv.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_cv.setStyleSheet(f"color: {self.theme.slate_dark.name()}; background: transparent;")
        cover_row.addWidget(lbl_cv)

        self.cover_mode_tabs = WinUISegmentedControl(["方形封面", "黑胶唱片", "黑胶唱机"], self.tab_ui)
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

        # 5. 在线歌词与封面
        scrape_box = QVBoxLayout()
        scrape_box.setSpacing(6)

        lbl_sc = QLabel("歌词与封面匹配", self.tab_ui)
        lbl_sc.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_sc.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        scrape_box.addWidget(lbl_sc)

        sc_row = QHBoxLayout()
        lbl_sc_desc = QLabel("在线搜索当前歌曲的歌词和专辑封面", self.tab_ui)
        lbl_sc_desc.setFont(get_nordic_font(11, QFont.Weight.Normal))
        lbl_sc_desc.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        sc_row.addWidget(lbl_sc_desc)
        sc_row.addStretch(1)

        self.btn_scrape_action = WinUIChipButton("搜索词图", icon_kind="search", parent=self.tab_ui)
        self.btn_scrape_action.set_theme(self.theme)
        self.btn_scrape_action.clicked.connect(self._on_scrape_clicked)
        sc_row.addWidget(self.btn_scrape_action)
        scrape_box.addLayout(sc_row)

        ui_vbox.addLayout(scrape_box)
        ui_vbox.addStretch(1)

        self.scroll_ui = self._wrap_tab_in_scroll(self.tab_ui)
        self.stacked_layout.addWidget(self.scroll_ui)

        # ----------------- Tab 3: 关于 -----------------
        self._latest_release_url: str = ""
        self.tab_about = QWidget(self.tab_container)
        about_vbox = QVBoxLayout(self.tab_about)
        about_vbox.setContentsMargins(6, 12, 12, 18)
        about_vbox.setSpacing(16)

        # 头部应用概览：图标、名称、版本胶囊与定位简述
        app_header = QHBoxLayout()
        app_header.setSpacing(14)

        icon_path = Path(__file__).resolve().parent.parent / "assets" / "klang_icon.png"
        self.lbl_about_icon = QLabel(self.tab_about)
        if icon_path.exists():
            pm = QPixmap(str(icon_path)).scaled(
                52, 52, Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation
            )
            self.lbl_about_icon.setPixmap(pm)
        self.lbl_about_icon.setFixedSize(52, 52)
        self.lbl_about_icon.setStyleSheet("background: transparent;")
        app_header.addWidget(self.lbl_about_icon)

        app_title_box = QVBoxLayout()
        app_title_box.setSpacing(4)
        app_title_box.setAlignment(Qt.AlignmentFlag.AlignVCenter)

        title_row = QHBoxLayout()
        title_row.setSpacing(8)
        title_row.setAlignment(Qt.AlignmentFlag.AlignLeft)

        self.lbl_about_app_name = QLabel("Klang", self.tab_about)
        self.lbl_about_app_name.setFont(get_nordic_font(20, QFont.Weight.Bold))
        self.lbl_about_app_name.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        title_row.addWidget(self.lbl_about_app_name)

        self.lbl_about_version_badge = QLabel("v1.4.0", self.tab_about)
        self.lbl_about_version_badge.setFont(get_nordic_font(10, QFont.Weight.DemiBold, mono=True))
        self.lbl_about_version_badge.setFixedHeight(20)
        self.lbl_about_version_badge.setContentsMargins(6, 2, 6, 2)
        self.lbl_about_version_badge.setStyleSheet(
            f"color: {self.theme.charcoal.name()}; background: rgba({self.theme.sage_soft.red()}, {self.theme.sage_soft.green()}, {self.theme.sage_soft.blue()}, 0.22); border-radius: 4px;"
        )
        title_row.addWidget(self.lbl_about_version_badge)
        app_title_box.addLayout(title_row)

        self.lbl_about_subtitle = QLabel("Windows 本地与网易云无损音乐播放器", self.tab_about)
        self.lbl_about_subtitle.setFont(get_nordic_font(10, QFont.Weight.Normal))
        self.lbl_about_subtitle.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        app_title_box.addWidget(self.lbl_about_subtitle)

        app_header.addLayout(app_title_box, 1)
        about_vbox.addLayout(app_header)

        # 分割线 1
        self.div_about_1 = QFrame(self.tab_about)
        self.div_about_1.setFrameShape(QFrame.Shape.HLine)
        self.div_about_1.setFrameShadow(QFrame.Shadow.Plain)
        self.div_about_1.setFixedHeight(1)
        self.div_about_1.setStyleSheet(
            f"background-color: rgba({self.theme.divider.red()}, {self.theme.divider.green()}, {self.theme.divider.blue()}, {self.theme.divider.alphaF():.3f}); border: none;"
        )
        about_vbox.addWidget(self.div_about_1)

        # 1. 项目信息
        proj_box = QVBoxLayout()
        proj_box.setSpacing(10)

        self.lbl_about_sec_proj = QLabel("项目信息", self.tab_about)
        self.lbl_about_sec_proj.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.lbl_about_sec_proj.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        proj_box.addWidget(self.lbl_about_sec_proj)

        # 开发者
        author_row = QHBoxLayout()
        self.lbl_author_title = QLabel("开发者", self.tab_about)
        self.lbl_author_title.setFont(get_nordic_font(11, QFont.Weight.Medium))
        self.lbl_author_title.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        author_row.addWidget(self.lbl_author_title)
        author_row.addStretch(1)

        self.lbl_author_val = QLabel("gwj0323 (Wendgy)", self.tab_about)
        self.lbl_author_val.setFont(get_nordic_font(11, QFont.Weight.Normal, mono=True))
        self.lbl_author_val.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        author_row.addWidget(self.lbl_author_val)
        proj_box.addLayout(author_row)

        # 开源协议
        lic_row = QHBoxLayout()
        self.lbl_lic_title = QLabel("开源协议", self.tab_about)
        self.lbl_lic_title.setFont(get_nordic_font(11, QFont.Weight.Medium))
        self.lbl_lic_title.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        lic_row.addWidget(self.lbl_lic_title)
        lic_row.addStretch(1)

        self.lbl_lic_val = QLabel("GNU General Public License v3.0 (GPL-3.0)", self.tab_about)
        self.lbl_lic_val.setFont(get_nordic_font(10, QFont.Weight.Normal))
        self.lbl_lic_val.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        lic_row.addWidget(self.lbl_lic_val)
        proj_box.addLayout(lic_row)

        # 代码仓库
        repo_row = QHBoxLayout()
        repo_text_col = QVBoxLayout()
        repo_text_col.setSpacing(2)
        self.lbl_repo_title = QLabel("代码仓库", self.tab_about)
        self.lbl_repo_title.setFont(get_nordic_font(11, QFont.Weight.Medium))
        self.lbl_repo_title.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        repo_text_col.addWidget(self.lbl_repo_title)

        self.lbl_repo_desc = QLabel("github.com/gwj0323/klang", self.tab_about)
        self.lbl_repo_desc.setFont(get_nordic_font(10, QFont.Weight.Normal, mono=True))
        self.lbl_repo_desc.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        repo_text_col.addWidget(self.lbl_repo_desc)
        repo_row.addLayout(repo_text_col, 1)

        self.btn_open_github = WinUIChipButton("访问 GitHub", fixed_w=100, parent=self.tab_about)
        self.btn_open_github.set_theme(self.theme)
        self.btn_open_github.clicked.connect(lambda: QDesktopServices.openUrl(QUrl("https://github.com/gwj0323/klang")))
        repo_row.addWidget(self.btn_open_github)
        proj_box.addLayout(repo_row)

        about_vbox.addLayout(proj_box)

        # 分割线 2
        self.div_about_2 = QFrame(self.tab_about)
        self.div_about_2.setFrameShape(QFrame.Shape.HLine)
        self.div_about_2.setFrameShadow(QFrame.Shadow.Plain)
        self.div_about_2.setFixedHeight(1)
        self.div_about_2.setStyleSheet(
            f"background-color: rgba({self.theme.divider.red()}, {self.theme.divider.green()}, {self.theme.divider.blue()}, {self.theme.divider.alphaF():.3f}); border: none;"
        )
        about_vbox.addWidget(self.div_about_2)

        # 2. 版本与检查更新
        update_box = QVBoxLayout()
        update_box.setSpacing(10)

        self.lbl_about_sec_update = QLabel("软件更新", self.tab_about)
        self.lbl_about_sec_update.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.lbl_about_sec_update.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        update_box.addWidget(self.lbl_about_sec_update)

        update_row = QHBoxLayout()
        update_text_col = QVBoxLayout()
        update_text_col.setSpacing(2)

        self.lbl_cur_ver = QLabel("当前版本: v1.4.0", self.tab_about)
        self.lbl_cur_ver.setFont(get_nordic_font(11, QFont.Weight.Medium))
        self.lbl_cur_ver.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        update_text_col.addWidget(self.lbl_cur_ver)

        self.lbl_update_status = QLabel("点击检查更新获取 GitHub 最新发布", self.tab_about)
        self.lbl_update_status.setFont(get_nordic_font(10, QFont.Weight.Normal))
        self.lbl_update_status.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        update_text_col.addWidget(self.lbl_update_status)
        update_row.addLayout(update_text_col, 1)

        btn_group_row = QHBoxLayout()
        btn_group_row.setSpacing(6)

        self.btn_view_releases = WinUIChipButton("发布日志", fixed_w=78, parent=self.tab_about)
        self.btn_view_releases.set_theme(self.theme)
        self.btn_view_releases.clicked.connect(lambda: QDesktopServices.openUrl(QUrl("https://github.com/gwj0323/klang/releases")))
        btn_group_row.addWidget(self.btn_view_releases)

        self.btn_download_update = WinUIChipButton("前往下载", fixed_w=78, parent=self.tab_about)
        self.btn_download_update.set_theme(self.theme)
        self.btn_download_update.setVisible(False)
        self.btn_download_update.clicked.connect(self._on_download_update_clicked)
        btn_group_row.addWidget(self.btn_download_update)

        self.btn_check_update = WinUIChipButton("检查更新", fixed_w=78, parent=self.tab_about)
        self.btn_check_update.set_theme(self.theme)
        self.btn_check_update.clicked.connect(self._on_check_update_clicked)
        btn_group_row.addWidget(self.btn_check_update)

        update_row.addLayout(btn_group_row)
        update_box.addLayout(update_row)

        about_vbox.addLayout(update_box)

        self.updateCheckResult.connect(self._on_update_check_result)
        self.updateCheckFailed.connect(self._on_update_check_failed)

        about_vbox.addStretch(1)

        self.lbl_about_footer = QLabel("Copyright © 2026 gwj0323. All rights reserved.", self.tab_about)
        self.lbl_about_footer.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.lbl_about_footer.setFont(get_nordic_font(10, QFont.Weight.Normal))
        self.lbl_about_footer.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        about_vbox.addWidget(self.lbl_about_footer)

        self.scroll_about = self._wrap_tab_in_scroll(self.tab_about)
        self.stacked_layout.addWidget(self.scroll_about)

        vbox.addWidget(self.tab_container, 1)

        # 切换到默认 Tab
        safe_tab = max(0, min(3, int(default_tab)))
        self.category_tabs.set_current_index(safe_tab, animate=False)
        self.stacked_layout.setCurrentIndex(safe_tab)
        self.category_tabs.currentIndexChanged.connect(self._on_category_changed)

    def _on_category_changed(self, idx: int) -> None:
        self.stacked_layout.setCurrentIndex(idx)

    def _adjust_font_size(self, delta: int) -> None:
        self.current_font_size = max(12, min(36, self.current_font_size + delta))
        self.lbl_fs_val.setText(f"{self.current_font_size}px")
        self.fontSizeDeltaRequested.emit(delta)

    def _on_scrape_clicked(self) -> None:
        self.scrapeRequested.emit()

    def _on_check_update_clicked(self) -> None:
        self.btn_check_update.setEnabled(False)
        self.lbl_update_status.setText("正在连接 GitHub 检查最新版本…")
        self.lbl_update_status.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")

        def _fetch():
            try:
                req = urllib.request.Request(
                    "https://api.github.com/repos/gwj0323/klang/releases/latest",
                    headers={"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Klang/1.4.0"}
                )
                with urllib.request.urlopen(req, timeout=5) as resp:
                    raw = resp.read().decode("utf-8")
                    data = json.loads(raw)
                    tag_name = data.get("tag_name", "").strip()
                    html_url = data.get("html_url", "https://github.com/gwj0323/klang/releases")

                    def _parse_tag(t: str) -> List[int]:
                        clean = re.sub(r"[^0-9.]", "", t)
                        parts = [int(p) for p in clean.split(".") if p.isdigit()]
                        return parts if parts else [0]

                    curr_parts = _parse_tag("1.4.0")
                    latest_parts = _parse_tag(tag_name)
                    has_update = latest_parts > curr_parts
                    self.updateCheckResult.emit(has_update, tag_name, html_url)
            except Exception as e:
                self.updateCheckFailed.emit(str(e))

        t = threading.Thread(target=_fetch, daemon=True)
        t.start()

    def _on_update_check_result(self, has_update: bool, tag_name: str, release_url: str) -> None:
        self.btn_check_update.setEnabled(True)
        self._latest_release_url = release_url
        if has_update:
            self.lbl_update_status.setText(f"发现新版本 {tag_name}，可前往 GitHub 下载")
            self.lbl_update_status.setStyleSheet(f"color: {self.theme.sage_accent.name()}; background: transparent;")
            self.btn_download_update.setVisible(True)
        else:
            self.lbl_update_status.setText(f"当前已是最新版本 ({tag_name or 'v1.4.0'})")
            self.lbl_update_status.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
            self.btn_download_update.setVisible(False)

    def _on_update_check_failed(self, error_msg: str) -> None:
        self.btn_check_update.setEnabled(True)
        self.lbl_update_status.setText("检查更新失败，请确认网络连接或访问 Releases 页面")
        self.lbl_update_status.setStyleSheet(f"color: #D32F2F; background: transparent;")

    def _on_download_update_clicked(self) -> None:
        target = self._latest_release_url or "https://github.com/gwj0323/klang/releases"
        QDesktopServices.openUrl(QUrl(target))

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

    def _format_vocal_balance_text(self, val: float) -> str:
        if abs(val) < 0.04:
            return "均衡 0%"
        pct = int(round(abs(val) * 100))
        if val < 0:
            return f"伴奏 -{pct}%"
        return f"人声 +{pct}%"

    def _on_vocal_balance_slider_changed(self, ratio: float) -> None:
        val = round((ratio - 0.5) * 2.0, 2)
        if abs(val) < 0.04:
            val = 0.0
        self.vocal_balance = val
        self.lbl_vocal_balance.setText(self._format_vocal_balance_text(val))
        self.vocalBalanceChanged.emit(val)

    def _on_reset_vocal_balance(self) -> None:
        self.vocal_balance = 0.0
        self.vocal_balance_slider.set_ratio(0.5)
        self.lbl_vocal_balance.setText("均衡 0%")
        self.vocalBalanceChanged.emit(0.0)

    def _on_vinyl_warmth_changed(self, ratio: float) -> None:
        val = round(max(0.0, min(1.0, ratio)), 2)
        if val < 0.02:
            val = 0.0
        self.vinyl_warmth = val
        self.lbl_warmth_pct.setText("关闭" if val < 0.02 else f"{int(round(val * 100))}%")
        self.vinylWarmthChanged.emit(val)

    def _on_offset_btn(self, delta: float) -> None:
        if delta == 0.0:
            self.current_offset = 0.0
        else:
            self.current_offset = round(self.current_offset + delta, 1)
        self.lbl_offset.setText(f"歌词时间偏移 ({self.current_offset:+.1f}s)")
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
            if self.theme.name == "dusk":
                self.lbl_atmos_badge.setStyleSheet(
                    f"background: rgba(150, 150, 150, 0.12); color: {self.theme.slate_muted.name()}; "
                    "border: 1px solid rgba(150, 150, 150, 0.25); border-radius: 4px; padding: 1px 6px;"
                )
            else:
                self.lbl_atmos_badge.setStyleSheet(
                    "background: rgba(0, 0, 0, 0.06); color: #4A5058; "
                    "border: 1px solid rgba(0, 0, 0, 0.14); border-radius: 4px; padding: 1px 6px;"
                )

    def _on_spatial_slider_changed(self, _val: float) -> None:
        width = round(0.8 + self.spatial_width_slider.ratio * 1.0, 2)
        height = round(self.spatial_height_slider.ratio, 2)
        self.current_spatial_width = width
        self.current_spatial_height = height
        self.lbl_width_val.setText(f"{int(round(width * 100))}%")
        self.lbl_height_val.setText(f"{int(round(height * 100))}%")
        self.spatialParamsChanged.emit(width, height)

    def _current_device_button_label(self) -> str:
        for dev_id, desc in self.available_devices:
            if dev_id == self.selected_device_id:
                return f"{desc}  ▾"
        clean_dev = self._clean_device_name(self.output_device_name) or "系统默认设备"
        return f"系统默认 ({clean_dev})  ▾"

    def _on_open_device_flyout(self) -> None:
        items: List[Tuple[str, str, str]] = []
        for dev_id, desc in self.available_devices:
            icon_kind = "check" if dev_id == self.selected_device_id else "speaker"
            items.append((dev_id, icon_kind, desc))
        if not items:
            items.append(("default", "check", "系统默认输出设备"))

        self._device_flyout = WinUIFlyoutMenu(items, theme=self.theme, backdrop_material=getattr(self, "backdrop_material", "mica"), parent=self)
        self._device_flyout.actionTriggered.connect(self._on_device_selected)
        global_pos = self.btn_output_device.mapToGlobal(QPoint(0, self.btn_output_device.height() + 4))
        self._device_flyout.popup_animated(global_pos)

    def _on_device_selected(self, dev_id: str) -> None:
        self.selected_device_id = dev_id
        self.btn_output_device.setText(self._current_device_button_label())
        self.outputDeviceChanged.emit(dev_id)

    def update_output_devices(
        self,
        available_devices: List[Tuple[str, str]],
        selected_device_id: str,
        output_device_name: str,
    ) -> None:
        self.available_devices = list(available_devices)
        self.selected_device_id = selected_device_id
        self.output_device_name = output_device_name
        self.btn_output_device.setText(self._current_device_button_label())
        self._update_output_tip(self.current_output_mode, self.actual_channels, self.output_device_name)

    def _on_output_tab_changed(self, idx: int) -> None:
        if 0 <= idx < len(self._OUTPUT_MODES):
            mode = self._OUTPUT_MODES[idx]
            self.current_output_mode = mode
            self.outputModeChanged.emit(mode)
            self._update_output_tip(mode, self.actual_channels, self.output_device_name)

    def _update_output_tip(self, mode: str, actual_channels: int, dev_name: str) -> None:
        clean_dev = self._clean_device_name(dev_name)
        dev_tag = f"当前输出：{clean_dev} · " if clean_dev else ""
        hotplug_note = "拔出耳机或外置声卡时自动暂停播放"

        if actual_channels > 2 and self.switch_dolby.isChecked():
            self.lbl_output_tip.setText(f"{dev_tag}{actual_channels} 声道输出 · {hotplug_note}")
            self.lbl_output_tip.setStyleSheet("color: #5B9E83; background: transparent; padding-top: 2px;")
        else:
            self.lbl_output_tip.setText(f"{dev_tag}{hotplug_note}")
            self.lbl_output_tip.setStyleSheet(
                f"color: {self.theme.slate_muted.name()}; background: transparent; padding-top: 2px;"
            )

    def _current_headphone_button_label(self) -> str:
        prof = HEADPHONE_PROFILES.get(self.headphone_profile_id)
        name = prof.name if prof else "关闭 / 原声监听 (Bypass)"
        return f"{name}  ▾"

    def _update_hp_desc(self, profile_id: str) -> None:
        prof = HEADPHONE_PROFILES.get(profile_id)
        if prof and profile_id != "bypass":
            self.lbl_hp_desc.setText(f"已匹配哈曼目标曲线：{prof.description}")
        else:
            self.lbl_hp_desc.setText("未开启硬件校准，音频直通输出。")

    def _update_hp_badge_state(self, is_active: bool) -> None:
        if is_active:
            self.lbl_hp_badge.setText("HARMAN TARGET")
            self.lbl_hp_badge.setStyleSheet(
                "background: rgba(110, 130, 119, 0.16); color: #5B9E83; "
                "border: 1px solid rgba(110, 130, 119, 0.35); border-radius: 4px; padding: 1px 6px;"
            )
        else:
            self.lbl_hp_badge.setText("已关闭")
            if self.theme.name == "dusk":
                self.lbl_hp_badge.setStyleSheet(
                    f"background: rgba(150, 150, 150, 0.12); color: {self.theme.slate_muted.name()}; "
                    "border: 1px solid rgba(150, 150, 150, 0.25); border-radius: 4px; padding: 1px 6px;"
                )
            else:
                self.lbl_hp_badge.setStyleSheet(
                    "background: rgba(0, 0, 0, 0.06); color: #4A5058; "
                    "border: 1px solid rgba(0, 0, 0, 0.14); border-radius: 4px; padding: 1px 6px;"
                )

    def _on_open_headphone_flyout(self) -> None:
        items: List[Tuple[str, str, str]] = []
        for pid, p in HEADPHONE_PROFILES.items():
            icon_kind = "check" if pid == self.headphone_profile_id else "speaker"
            label = f"{p.name} ({p.category})" if p.category != "直通" else p.name
            items.append((pid, icon_kind, label))

        self._headphone_flyout = WinUIFlyoutMenu(items, theme=self.theme, backdrop_material=getattr(self, "backdrop_material", "mica"), parent=self)
        self._headphone_flyout.actionTriggered.connect(self._on_headphone_selected)
        global_pos = self.btn_headphone_profile.mapToGlobal(QPoint(0, self.btn_headphone_profile.height() + 4))
        self._headphone_flyout.popup_animated(global_pos)

    def _on_headphone_selected(self, profile_id: str) -> None:
        self.headphone_profile_id = profile_id
        self.btn_headphone_profile.setText(self._current_headphone_button_label())
        self.btn_headphone_profile.set_active(profile_id != "bypass")
        self._update_hp_badge_state(profile_id != "bypass")
        self._update_hp_desc(profile_id)
        self.headphoneProfileChanged.emit(profile_id)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.title_label.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        self.category_tabs.set_theme(theme)

        # Tab 0
        self.btn_headphone_profile.set_theme(theme)
        self.btn_preview.set_theme(theme)
        self.preset_tabs.set_theme(theme)
        self.eq_rack.set_theme(theme)
        self.reverb_slider.set_theme(theme)
        self.vocal_balance_slider.set_theme(theme)
        self.btn_vocal_reset.set_theme(theme)
        self.switch_dolby.set_theme(theme)
        self.dolby_mode_tabs.set_theme(theme)
        self.spatial_width_slider.set_theme(theme)
        self.spatial_height_slider.set_theme(theme)
        self.output_tabs.set_theme(theme)
        self._update_hp_badge_state(self.headphone_profile_id != "bypass")
        self._update_hp_desc(self.headphone_profile_id)

        # Tab 1
        self.btn_output_device.set_theme(theme)
        self.crossfade_tabs.set_theme(theme)
        if hasattr(self, "netease_quality_tabs"):
            self.netease_quality_tabs.set_theme(theme)
        if hasattr(self, "btn_ne_account_action"):
            self.btn_ne_account_action.set_theme(theme)
        if hasattr(self, "cache_limit_tabs"):
            self.cache_limit_tabs.set_theme(theme)
        if hasattr(self, "btn_clear_cache"):
            self.btn_clear_cache.set_theme(theme)
        if hasattr(self, "lbl_cache_title"):
            self.lbl_cache_title.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        self.switch_replay_gain.set_theme(theme)
        self.switch_turntable.set_theme(theme)
        self.vinyl_warmth_slider.set_theme(theme)
        self.rate_tabs.set_theme(theme)
        self.sleep_tabs.set_theme(theme)

        # Tab 2
        self.theme_tabs.set_theme(theme)
        self.backdrop_tabs.set_theme(theme)
        self.switch_trans.set_theme(theme)
        self.switch_syl.set_theme(theme)
        self.fps_tabs.set_theme(theme)
        self.cover_mode_tabs.set_theme(theme)
        self.btn_scrape_action.set_theme(theme)

        # Tab 3 (关于)
        if hasattr(self, "lbl_about_app_name"):
            self.lbl_about_app_name.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        if hasattr(self, "lbl_about_version_badge"):
            self.lbl_about_version_badge.setStyleSheet(
                f"color: {theme.charcoal.name()}; background: rgba({theme.sage_soft.red()}, {theme.sage_soft.green()}, {theme.sage_soft.blue()}, 0.22); border-radius: 4px;"
            )
        if hasattr(self, "lbl_about_subtitle"):
            self.lbl_about_subtitle.setStyleSheet(f"color: {theme.slate_muted.name()}; background: transparent;")
        if hasattr(self, "lbl_about_sec_proj"):
            self.lbl_about_sec_proj.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        if hasattr(self, "lbl_author_title"):
            self.lbl_author_title.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        if hasattr(self, "lbl_author_val"):
            self.lbl_author_val.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        if hasattr(self, "lbl_lic_title"):
            self.lbl_lic_title.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        if hasattr(self, "lbl_lic_val"):
            self.lbl_lic_val.setStyleSheet(f"color: {theme.slate_muted.name()}; background: transparent;")
        if hasattr(self, "lbl_repo_title"):
            self.lbl_repo_title.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        if hasattr(self, "lbl_repo_desc"):
            self.lbl_repo_desc.setStyleSheet(f"color: {theme.slate_muted.name()}; background: transparent;")
        if hasattr(self, "lbl_about_sec_update"):
            self.lbl_about_sec_update.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        if hasattr(self, "lbl_cur_ver"):
            self.lbl_cur_ver.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        if hasattr(self, "lbl_update_status"):
            self.lbl_update_status.setStyleSheet(f"color: {theme.slate_muted.name()}; background: transparent;")
        if hasattr(self, "lbl_about_footer"):
            self.lbl_about_footer.setStyleSheet(f"color: {theme.slate_muted.name()}; background: transparent;")
        if hasattr(self, "btn_open_github"):
            self.btn_open_github.set_theme(theme)
        if hasattr(self, "btn_view_releases"):
            self.btn_view_releases.set_theme(theme)
        if hasattr(self, "btn_check_update"):
            self.btn_check_update.set_theme(theme)
        if hasattr(self, "btn_download_update"):
            self.btn_download_update.set_theme(theme)
        if hasattr(self, "div_about_1"):
            self.div_about_1.setStyleSheet(
                f"background-color: rgba({theme.divider.red()}, {theme.divider.green()}, {theme.divider.blue()}, {theme.divider.alphaF():.3f}); border: none;"
            )
        if hasattr(self, "div_about_2"):
            self.div_about_2.setStyleSheet(
                f"background-color: rgba({theme.divider.red()}, {theme.divider.green()}, {theme.divider.blue()}, {theme.divider.alphaF():.3f}); border: none;"
            )

        # Scrollbars
        if hasattr(self, "scroll_audio"):
            self._apply_scroll_style(self.scroll_audio)
        if hasattr(self, "scroll_playback"):
            self._apply_scroll_style(self.scroll_playback)
        if hasattr(self, "scroll_ui"):
            self._apply_scroll_style(self.scroll_ui)
        if hasattr(self, "scroll_about"):
            self._apply_scroll_style(self.scroll_about)

        if hasattr(self, "_device_flyout") and self._device_flyout:
            self._device_flyout.set_theme(theme)
        if hasattr(self, "_headphone_flyout") and self._headphone_flyout:
            self._headphone_flyout.set_theme(theme)

        self.update()

    def set_backdrop_material(self, material: str) -> None:
        self.backdrop_material = str(material)
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        super().set_backdrop_material(material)

        # Propagate to segmented controls
        for tab in (
            getattr(self, "category_tabs", None),
            getattr(self, "preset_tabs", None),
            getattr(self, "dolby_mode_tabs", None),
            getattr(self, "output_tabs", None),
            getattr(self, "crossfade_tabs", None),
            getattr(self, "netease_quality_tabs", None),
            getattr(self, "rate_tabs", None),
            getattr(self, "sleep_tabs", None),
            getattr(self, "theme_tabs", None),
            getattr(self, "backdrop_tabs", None),
            getattr(self, "fps_tabs", None),
            getattr(self, "cover_mode_tabs", None),
        ):
            if tab and hasattr(tab, "set_backdrop_material"):
                tab.set_backdrop_material(material)

        # Propagate to chip buttons
        for btn in (
            getattr(self, "btn_headphone_profile", None),
            getattr(self, "btn_preview", None),
            getattr(self, "btn_output_device", None),
            getattr(self, "btn_vocal_reset", None),
            getattr(self, "btn_scrape_action", None),
            getattr(self, "btn_ne_account_action", None),
            getattr(self, "btn_clear_cache", None),
            getattr(self, "btn_open_github", None),
            getattr(self, "btn_view_releases", None),
            getattr(self, "btn_check_update", None),
            getattr(self, "btn_download_update", None),
        ):
            if btn and hasattr(btn, "set_backdrop_material"):
                btn.set_backdrop_material(material)

        # Propagate to flyout menus
        for fly in (
            getattr(self, "_device_flyout", None),
            getattr(self, "_headphone_flyout", None),
        ):
            if fly and hasattr(fly, "set_backdrop_material"):
                fly.set_backdrop_material(material)

        # Update scroll areas
        for scroll in (
            getattr(self, "scroll_audio", None),
            getattr(self, "scroll_playback", None),
            getattr(self, "scroll_ui", None),
            getattr(self, "scroll_about", None),
        ):
            if scroll:
                self._apply_scroll_style(scroll)

        self.update()
