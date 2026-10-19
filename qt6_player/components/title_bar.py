"""
顶部标题栏与快捷工具栏组件 (Qt6)
包含：侧边栏折叠开关 (带矢量分栏图标)、在线词图、歌词字号调节 (A-/A+)、译文开关、逐字开关、桌面歌词开关、浅色/深色主题切换与窗口控制键。
已移除与左侧导航栏重复的「导入音乐」按钮，统一收拢至左侧音乐库导航栏。
"""

from pathlib import Path
from typing import Optional
from PyQt6.QtCore import Qt, pyqtSignal, QPoint, QPointF, QRectF, QVariantAnimation, QEasingCurve
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
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.setFixedSize(40, 30)

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
    themeToggled = pyqtSignal(str)

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
        self.btn_sidebar.setToolTip("收起 / 展开左侧音乐库 (纯享歌词视图)")
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

        # 工具按钮组（已移除重复的「导入音乐」，保留在线词图匹配）
        self.btn_scrape = WinUIChipButton("在线词图", icon_kind="sparkle", parent=self)
        self.btn_scrape.setToolTip("在线搜索并刮削当前歌曲的双语滚动歌词与高清专辑封面")
        self.btn_scrape.clicked.connect(self.scrapeClicked.emit)
        layout.addWidget(self.btn_scrape)

        layout.addSpacing(2)

        # 歌词字号调节
        self.btn_font_dec = WinUIChipButton("A-", fixed_w=30, parent=self)
        self.btn_font_dec.setToolTip("减小歌词字号 (Ctrl + 滚轮)")
        self.btn_font_dec.clicked.connect(lambda: self.fontSizeDeltaRequested.emit(-2))
        layout.addWidget(self.btn_font_dec)

        self.lbl_font_size = QLabel("20px", self)
        self.lbl_font_size.setFixedWidth(34)
        self.lbl_font_size.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.lbl_font_size.setFont(get_nordic_font(11, QFont.Weight.Medium, mono=True))
        layout.addWidget(self.lbl_font_size)

        self.btn_font_inc = WinUIChipButton("A+", fixed_w=30, parent=self)
        self.btn_font_inc.setToolTip("增大歌词字号 (Ctrl + 滚轮)")
        self.btn_font_inc.clicked.connect(lambda: self.fontSizeDeltaRequested.emit(2))
        layout.addWidget(self.btn_font_inc)

        layout.addSpacing(2)

        # 译文开关、逐字开关、桌面歌词开关
        self.btn_trans = WinUIChipButton("译", active=True, fixed_w=30, parent=self)
        self.btn_trans.setToolTip("显示 / 隐藏双语翻译歌词")
        self.btn_trans.clicked.connect(self._on_toggle_translation)
        layout.addWidget(self.btn_trans)

        self.btn_syllable = WinUIChipButton("逐字", active=True, show_dot=True, parent=self)
        self.btn_syllable.setToolTip("开启 / 关闭逐字柔光推进动效")
        self.btn_syllable.clicked.connect(self._on_toggle_syllable)
        layout.addWidget(self.btn_syllable)

        self.btn_desktop_lyric = WinUIChipButton("桌面词", active=False, icon_kind="desktop_lyric", parent=self)
        self.btn_desktop_lyric.setToolTip("打开 / 关闭置顶桌面悬浮歌词窗")
        self.btn_desktop_lyric.clicked.connect(self._on_toggle_desktop_lyric)
        layout.addWidget(self.btn_desktop_lyric)

        self.btn_theme = WinUIChipButton("浅色", active=False, icon_kind="sun", parent=self)
        self.btn_theme.setToolTip("切换北欧晨雾浅色 / 峡湾极夜深色主题")
        self.btn_theme.clicked.connect(self._on_toggle_theme)
        layout.addWidget(self.btn_theme)

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

    def _toggle_maximize(self) -> None:
        win = self.window()
        if win.isMaximized():
            win.showNormal()
        else:
            win.showMaximized()

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
        self.lbl_font_size.setStyleSheet(f"color: {theme.slate_muted.name()}; background: transparent;")
        self.btn_theme.set_chip_active(theme.name == "dusk")
        self.btn_theme.icon_kind = "sun" if theme.name == "daylight" else "moon"
        self.btn_theme.setText("浅色" if theme.name == "daylight" else "深色")
        for btn in (
            self.btn_sidebar,
            self.btn_scrape,
            self.btn_font_dec,
            self.btn_font_inc,
            self.btn_trans,
            self.btn_syllable,
            self.btn_desktop_lyric,
            self.btn_theme,
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
