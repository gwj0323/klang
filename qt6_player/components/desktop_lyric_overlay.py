"""
桌面歌词悬浮窗组件 (Desktop Floating Lyrics OSD)
支持置顶显示、拖拽移动、双行双语歌词渲染、悬浮快捷控制栏 (上一曲/播放暂停/下一曲/字号调节/关闭)。
"""

from typing import Optional
from PyQt6.QtCore import Qt, QPoint, QRectF, pyqtSignal
from PyQt6.QtGui import QPainter, QPen, QColor, QFont, QPainterPath, QMouseEvent
from PyQt6.QtWidgets import QWidget, QHBoxLayout, QVBoxLayout, QPushButton, QLabel

from qt6_player.data.tracks import LyricLine
from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font


class DesktopLyricOverlay(QWidget):
    prevClicked = pyqtSignal()
    playToggled = pyqtSignal()
    nextClicked = pyqtSignal()
    closedByUser = pyqtSignal()
    positionChanged = pyqtSignal(int, int)
    fontSizeChanged = pyqtSignal(int)

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.setWindowFlags(
            Qt.WindowType.Tool
            | Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.setMouseTracking(True)
        self.resize(740, 108)

        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.primary_text: str = "Klang 桌面歌词"
        self.sub_text: str = ""
        self.font_size: int = 22
        self._drag_pos: Optional[QPoint] = None

        self._setup_ui()

    def _setup_ui(self) -> None:
        root_vbox = QVBoxLayout(self)
        root_vbox.setContentsMargins(16, 8, 16, 10)
        root_vbox.setSpacing(2)

        # 顶部悬浮控制条（仅鼠标移入时显现）
        self.toolbar_widget = QWidget(self)
        self.toolbar_widget.setFixedHeight(24)
        tb_hbox = QHBoxLayout(self.toolbar_widget)
        tb_hbox.setContentsMargins(0, 0, 0, 0)
        tb_hbox.setSpacing(8)
        tb_hbox.addStretch(1)

        for label, handler in (
            ("上一曲", self.prevClicked.emit),
            ("播放/暂停", self.playToggled.emit),
            ("下一曲", self.nextClicked.emit),
            ("A-", lambda: self._change_font(-2)),
            ("A+", lambda: self._change_font(2)),
            ("关闭", self._on_close),
        ):
            btn = QPushButton(label, self.toolbar_widget)
            btn.setCursor(Qt.CursorShape.PointingHandCursor)
            btn.setFixedHeight(22)
            btn.setFont(get_nordic_font(11, QFont.Weight.Medium))
            btn.setStyleSheet(
                """
                QPushButton {
                    background: rgba(38, 41, 46, 0.55);
                    color: #FAF9F6;
                    border: 1px solid rgba(255, 255, 255, 0.18);
                    border-radius: 4px;
                    padding: 0 8px;
                }
                QPushButton:hover {
                    background: rgba(110, 130, 119, 0.85);
                }
                """
            )
            btn.clicked.connect(handler)
            tb_hbox.addWidget(btn)

        tb_hbox.addStretch(1)
        self.toolbar_widget.setVisible(False)
        root_vbox.addWidget(self.toolbar_widget)
        root_vbox.addStretch(1)

    def _change_font(self, delta: int) -> None:
        self.font_size = max(16, min(32, self.font_size + delta))
        self.fontSizeChanged.emit(self.font_size)
        self.update()

    def _on_close(self) -> None:
        self.hide()
        self.closedByUser.emit()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def update_lyric(self, line: Optional[LyricLine]) -> None:
        if line is None:
            return
        self.primary_text = line.sub if (line.is_interlude and line.sub) else line.text
        self.sub_text = "" if line.is_interlude else (line.sub or "")
        self.update()

    def enterEvent(self, event) -> None:
        self.toolbar_widget.setVisible(True)
        self.update()
        super().enterEvent(event)

    def leaveEvent(self, event) -> None:
        self.toolbar_widget.setVisible(False)
        self.update()
        super().leaveEvent(event)

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self._drag_pos = event.globalPosition().toPoint() - self.frameGeometry().topLeft()
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if self._drag_pos is not None and event.buttons() & Qt.MouseButton.LeftButton:
            self.move(event.globalPosition().toPoint() - self._drag_pos)
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        had_drag = self._drag_pos is not None
        self._drag_pos = None
        if had_drag:
            self.positionChanged.emit(self.x(), self.y())
        super().mouseReleaseEvent(event)

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setRenderHint(QPainter.RenderHint.TextAntialiasing)

        rect = QRectF(self.rect()).adjusted(1.0, 1.0, -1.0, -1.0)
        path = QPainterPath()
        path.addRoundedRect(rect, 12.0, 12.0)

        # 悬浮时显示温润半透磨砂底衬，平时保持极轻薄半透背景以确保桌面上清晰可读
        bg = QColor(30, 34, 39, 185 if self.underMouse() else 125)
        painter.fillPath(path, bg)
        if self.underMouse():
            painter.setPen(QPen(QColor(255, 255, 255, 45), 1.0))
            painter.drawPath(path)

        top_y = 34.0 if self.toolbar_widget.isVisible() else 16.0
        painter.setFont(get_nordic_font(self.font_size, QFont.Weight.DemiBold))
        painter.setPen(QColor("#F6F5F1"))
        painter.drawText(
            QRectF(16.0, top_y, self.width() - 32.0, 34.0),
            int(Qt.AlignmentFlag.AlignCenter),
            self.primary_text,
        )

        if self.sub_text:
            painter.setFont(get_nordic_font(max(12, int(self.font_size * 0.62)), QFont.Weight.Normal))
            painter.setPen(QColor("#B8C4BE"))
            painter.drawText(
                QRectF(16.0, top_y + 34.0, self.width() - 32.0, 24.0),
                int(Qt.AlignmentFlag.AlignCenter),
                self.sub_text,
            )
