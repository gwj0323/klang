from pathlib import Path
from typing import Optional
from PyQt6.QtCore import Qt, pyqtSignal, QPoint, QPointF, QRectF, QVariantAnimation, QEasingCurve, QTimer
from PyQt6.QtGui import QPainter, QPen, QColor, QFont, QMouseEvent, QPainterPath, QPixmap
from PyQt6.QtWidgets import QWidget, QHBoxLayout, QPushButton, QLabel

from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font
from qt6_player.components.fluent_widgets import draw_nordic_icon


class WinUIChipButton(QPushButton):
    def __init__(
        self,
        text: str = "",
        active: bool = False,
        show_dot: bool = False,
        icon_kind: str = "",
        fixed_w: Optional[int] = None,
        parent: Optional[QWidget] = None,
    ):
        super().__init__(text, parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.is_chip_active: bool = active
        self.show_dot: bool = show_dot
        self.icon_kind: str = icon_kind
        self._fixed_w = fixed_w
        self._hover_alpha: float = 0.0

        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setFixedHeight(28)
        self.setFont(get_nordic_font(12, QFont.Weight.Medium))

        self._hover_anim = QVariantAnimation(self)
        self._hover_anim.setDuration(150)
        self._hover_anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._hover_anim.valueChanged.connect(self._on_hover_val)

        self._update_width()

    def _on_hover_val(self, val: float) -> None:
        self._hover_alpha = float(val)
        self.update()

    def enterEvent(self, event) -> None:
        self._hover_anim.stop()
        self._hover_anim.setStartValue(self._hover_alpha)
        self._hover_anim.setEndValue(1.0)
        self._hover_anim.start()
        super().enterEvent(event)

    def leaveEvent(self, event) -> None:
        self._hover_anim.stop()
        self._hover_anim.setStartValue(self._hover_alpha)
        self._hover_anim.setEndValue(0.0)
        self._hover_anim.start()
        super().leaveEvent(event)

    def _update_width(self) -> None:
        if self._fixed_w is not None:
            self.setFixedWidth(self._fixed_w)
            return
        fm = self.fontMetrics()
        extra = 20
        if self.show_dot:
            extra += 10
        if self.icon_kind:
            extra += 18 if self.text() else 12
        self.setFixedWidth(fm.horizontalAdvance(self.text()) + extra)

    def setText(self, text: str) -> None:
        super().setText(text)
        self._update_width()
        self.update()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def set_chip_active(self, active: bool) -> None:
        self.is_chip_active = active
        self.update()

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        rect = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        path = QPainterPath()
        path.addRoundedRect(rect, 6.0, 6.0)

        if self.is_chip_active:
            bg = self.theme.sage_soft
            border = self.theme.sage_border
            text_color = self.theme.charcoal
        else:
            bg = QColor(self.theme.oat_surface)
            if self._hover_alpha > 0.01:
                hov = self.theme.card_elevated
                r = int(bg.red() + (hov.red() - bg.red()) * self._hover_alpha)
                g = int(bg.green() + (hov.green() - bg.green()) * self._hover_alpha)
                b = int(bg.blue() + (hov.blue() - bg.blue()) * self._hover_alpha)
                a = int(bg.alpha() + (hov.alpha() - bg.alpha()) * self._hover_alpha)
                bg = QColor(r, g, b, a)
            border = self.theme.stroke_outer
            text_color = self.theme.charcoal if self._hover_alpha > 0.4 else self.theme.slate_dark

        painter.fillPath(path, bg)
        painter.setPen(QPen(border, 1.0))
        painter.drawPath(path)

        painter.setFont(self.font())
        left_x = 9.0

        if self.icon_kind:
            ic_col = self.theme.sage_accent if self.is_chip_active else text_color
            if not self.text():
                draw_nordic_icon(painter, self.icon_kind, QRectF(0, 0, self.width(), self.height()), ic_col, 1.35)
                return
            draw_nordic_icon(
                painter,
                self.icon_kind,
                QRectF(7.0, self.height() / 2.0 - 7.5, 15.0, 15.0),
                ic_col,
                1.35,
            )
            left_x = 24.5
        elif self.show_dot:
            dot_color = self.theme.sage_accent if self.is_chip_active else self.theme.slate_faint
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(dot_color)
            painter.drawEllipse(QRectF(9.0, self.height() / 2.0 - 3.0, 6.0, 6.0))
            left_x = 19.0

        painter.setPen(text_color)
        if self.icon_kind or self.show_dot:
            text_rect = QRectF(left_x, 0, self.width() - left_x - 6.0, self.height())
            painter.drawText(text_rect, int(Qt.AlignmentFlag.AlignVCenter | Qt.AlignmentFlag.AlignLeft), self.text())
        else:
            painter.drawText(self.rect(), int(Qt.AlignmentFlag.AlignCenter), self.text())


class WindowCaptionButton(QPushButton):
    def __init__(self, kind: str, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.kind = kind
        self.is_maximized: bool = False
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.setFixedSize(40, 30)
        if kind == "min":
            self.setToolTip("最小化")
        elif kind == "max":
            self.setToolTip("最大化")
        elif kind == "close":
            self.setToolTip("关闭")

    def set_maximized_state(self, is_max: bool) -> None:
        if self.is_maximized != is_max:
            self.is_maximized = is_max
            if self.kind == "max":
                self.setToolTip("向下还原" if is_max else "最大化")
            self.update()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        if self.underMouse():
            path = QPainterPath()
            path.addRoundedRect(QRectF(self.rect()).adjusted(2, 2, -2, -2), 5.0, 5.0)
            if self.kind == "close":
                painter.fillPath(path, QColor(186, 74, 70, 215))
                icon_color = QColor("#FAF9F6")
            else:
                painter.fillPath(path, self.theme.subtle_hover)
                icon_color = self.theme.charcoal
        else:
            icon_color = self.theme.slate_muted

        painter.setPen(QPen(icon_color, 1.2, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        cx, cy = self.width() / 2.0, self.height() / 2.0

        if self.kind == "min":
            painter.drawLine(QPointF(cx - 5.0, cy), QPointF(cx + 5.0, cy))
        elif self.kind == "max":
            painter.setBrush(Qt.BrushStyle.NoBrush)
            if self.is_maximized:
                # 还原双矩形图标 (WinUI 3 还原标准微缩图形)
                painter.drawRoundedRect(QRectF(cx - 5.5, cy - 3.5, 8.0, 8.0), 1.2, 1.2)
                p_back = QPainterPath()
                p_back.moveTo(cx - 3.0, cy - 5.5)
                p_back.lineTo(cx + 4.5, cy - 5.5)
                p_back.lineTo(cx + 4.5, cy + 2.0)
                painter.drawPath(p_back)
            else:
                painter.drawRoundedRect(QRectF(cx - 4.5, cy - 4.5, 9.0, 9.0), 1.4, 1.4)
        elif self.kind == "close":
            painter.drawLine(QPointF(cx - 4.5, cy - 4.5), QPointF(cx + 4.5, cy + 4.5))
            painter.drawLine(QPointF(cx + 4.5, cy - 4.5), QPointF(cx - 4.5, cy + 4.5))


class WinUITitleBar(QWidget):
    sidebarToggleClicked = pyqtSignal()
    scrapeClicked = pyqtSignal()
    fontSizeDeltaRequested = pyqtSignal(int)
    translationToggled = pyqtSignal(bool)
    syllableModeToggled = pyqtSignal(bool)
    desktopLyricToggled = pyqtSignal(bool)
    miniPlayerClicked = pyqtSignal()
    immersiveClicked = pyqtSignal()
    themeToggled = pyqtSignal(str)
    settingsClicked = pyqtSignal()

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.syllable_mode: bool = True
        self.show_translation: bool = True
        self.desktop_lyric_open: bool = False
        self._drag_pos: Optional[QPoint] = None
        self.setFixedHeight(46)

        layout = QHBoxLayout(self)
        layout.setContentsMargins(12, 0, 8, 0)
        layout.setSpacing(6)

        # 左侧：收起/展开播放列表矢量图标按钮 + 品牌名
        self.btn_sidebar = WinUIChipButton("", active=True, icon_kind="sidebar", fixed_w=32, parent=self)
        self.btn_sidebar.setToolTip("展开 / 收起音乐库")
        self.btn_sidebar.clicked.connect(self.sidebarToggleClicked.emit)
        layout.addWidget(self.btn_sidebar)

        self.brand_icon = QLabel(self)
        icon_path = Path(__file__).resolve().parent.parent / "assets" / "klang_icon.png"
        if icon_path.exists():
            pix = QPixmap(str(icon_path)).scaled(
                20, 20,
                Qt.AspectRatioMode.KeepAspectRatio,
                Qt.TransformationMode.SmoothTransformation,
            )
            self.brand_icon.setPixmap(pix)
            self.brand_icon.setFixedSize(20, 20)
            self.brand_icon.setContentsMargins(4, 0, 0, 0)
            layout.addWidget(self.brand_icon)

        self.brand_label = QLabel("Klang", self)
        self.brand_label.setFont(get_nordic_font(13, QFont.Weight.DemiBold))
        self.brand_label.setContentsMargins(2, 0, 0, 0)
        layout.addWidget(self.brand_label)

        layout.addStretch(1)

        # 右侧精炼工具组：桌面词、迷你窗、全屏沉浸、偏好设置
        self.btn_desktop_lyric = WinUIChipButton("桌面词", active=False, icon_kind="desktop_lyric", parent=self)
        self.btn_desktop_lyric.setToolTip("桌面歌词 (Ctrl+D)")
        self.btn_desktop_lyric.clicked.connect(self._on_toggle_desktop_lyric)
        layout.addWidget(self.btn_desktop_lyric)

        self.btn_mini = WinUIChipButton("迷你窗", active=False, icon_kind="mini_player", parent=self)
        self.btn_mini.setToolTip("迷你窗口 (Ctrl+M)")
        self.btn_mini.clicked.connect(self.miniPlayerClicked.emit)
        layout.addWidget(self.btn_mini)

        self.btn_immersive = WinUIChipButton("沉浸", active=False, icon_kind="fullscreen", parent=self)
        self.btn_immersive.setToolTip("全屏沉浸 (F11)")
        self.btn_immersive.clicked.connect(self.immersiveClicked.emit)
        layout.addWidget(self.btn_immersive)

        self.btn_settings = WinUIChipButton("设置", active=False, icon_kind="settings", parent=self)
        self.btn_settings.setToolTip("设置")
        self.btn_settings.clicked.connect(self.settingsClicked.emit)
        layout.addWidget(self.btn_settings)

        # 保留历史组件引用以保持接口兼容（不在顶栏堆叠显示）
        self.btn_scrape = WinUIChipButton("词图")
        self.btn_scrape.clicked.connect(self.scrapeClicked.emit)
        self.btn_font_dec = WinUIChipButton("A-")
        self.lbl_font_size = QLabel("20px")
        self.btn_font_inc = WinUIChipButton("A+")
        self.btn_trans = WinUIChipButton("译")
        self.btn_syllable = WinUIChipButton("逐字")
        self.btn_theme = WinUIChipButton("浅色")
        for _w in (self.btn_scrape, self.btn_font_dec, self.lbl_font_size, self.btn_font_inc, self.btn_trans, self.btn_syllable, self.btn_theme):
            _w.hide()

        layout.addSpacing(2)

        self.btn_min = WindowCaptionButton("min", self)
        self.btn_max = WindowCaptionButton("max", self)
        self.btn_close = WindowCaptionButton("close", self)

        self.btn_min.clicked.connect(lambda: self.window().showMinimized())
        self.btn_max.clicked.connect(self._toggle_maximize)
        self.btn_close.clicked.connect(lambda: self.window().close())

        layout.addWidget(self.btn_min)
        layout.addWidget(self.btn_max)
        layout.addWidget(self.btn_close)

        self.set_theme(DAYLIGHT_THEME)

    def set_sidebar_expanded(self, expanded: bool) -> None:
        self.btn_sidebar.set_chip_active(expanded)

    def update_font_size_display(self, px_size: int) -> None:
        self.lbl_font_size.setText(f"{px_size}px")

    def set_desktop_lyric_active(self, active: bool) -> None:
        self.desktop_lyric_open = active
        self.btn_desktop_lyric.set_chip_active(active)

    def set_window_maximized(self, is_max: bool) -> None:
        self.btn_max.set_maximized_state(is_max)

    def _toggle_maximize(self) -> None:
        win = self.window()
        if win.isMaximized():
            win.showNormal()
        else:
            win.showMaximized()
        if hasattr(win, "_handle_window_state_change"):
            win._handle_window_state_change()
            QTimer.singleShot(0, win._handle_window_state_change)
            QTimer.singleShot(40, win._handle_window_state_change)
            QTimer.singleShot(120, win._handle_window_state_change)

    def set_translation_state(self, show: bool) -> None:
        self.show_translation = bool(show)
        self.btn_trans.set_chip_active(self.show_translation)

    def set_syllable_state(self, enabled: bool) -> None:
        self.syllable_mode = bool(enabled)
        self.btn_syllable.set_chip_active(self.syllable_mode)

    def _on_toggle_translation(self) -> None:
        self.show_translation = not self.show_translation
        self.btn_trans.set_chip_active(self.show_translation)
        self.translationToggled.emit(self.show_translation)

    def _on_toggle_syllable(self) -> None:
        self.syllable_mode = not self.syllable_mode
        self.btn_syllable.set_chip_active(self.syllable_mode)
        self.syllableModeToggled.emit(self.syllable_mode)

    def _on_toggle_desktop_lyric(self) -> None:
        self.desktop_lyric_open = not self.desktop_lyric_open
        self.btn_desktop_lyric.set_chip_active(self.desktop_lyric_open)
        self.desktopLyricToggled.emit(self.desktop_lyric_open)

    def _on_toggle_theme(self) -> None:
        next_name = "dusk" if self.theme.name == "daylight" else "daylight"
        self.btn_theme.set_chip_active(next_name == "dusk")
        self.btn_theme.icon_kind = "sun" if next_name == "daylight" else "moon"
        self.btn_theme.setText("浅色" if next_name == "daylight" else "深色")
        self.themeToggled.emit(next_name)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.brand_label.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        for btn in (
            self.btn_sidebar,
            self.btn_desktop_lyric,
            self.btn_mini,
            self.btn_immersive,
            self.btn_settings,
        ):
            btn.set_theme(theme)
        self.btn_min.set_theme(theme)
        self.btn_max.set_theme(theme)
        self.btn_close.set_theme(theme)
        self.update()

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setPen(QPen(self.theme.divider, 1.0))
        painter.drawLine(0, self.height() - 1, self.width(), self.height() - 1)

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            winHandle = self.window().windowHandle()
            if winHandle is not None and hasattr(winHandle, "startSystemMove"):
                winHandle.startSystemMove()
                return
            self._drag_pos = event.globalPosition().toPoint() - self.window().frameGeometry().topLeft()
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if self._drag_pos is not None and event.buttons() & Qt.MouseButton.LeftButton:
            if not self.window().isMaximized():
                self.window().move(event.globalPosition().toPoint() - self._drag_pos)
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        self._drag_pos = None
        super().mouseReleaseEvent(event)

    def mouseDoubleClickEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self._toggle_maximize()
        super().mouseDoubleClickEvent(event)
