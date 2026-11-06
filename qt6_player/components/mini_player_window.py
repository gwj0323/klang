import math
from typing import Optional
from PyQt6.QtCore import (
    Qt,
    QRectF,
    QPointF,
    QPoint,
    QTimer,
    QElapsedTimer,
    QVariantAnimation,
    QEasingCurve,
    pyqtSignal,
)
from PyQt6.QtGui import (
    QPainter,
    QPen,
    QColor,
    QFont,
    QPainterPath,
    QPixmap,
    QLinearGradient,
    QRadialGradient,
    QMouseEvent,
)
from PyQt6.QtWidgets import (
    QWidget,
    QHBoxLayout,
    QVBoxLayout,
    QLabel,
)

from qt6_player.data.tracks import NordicTrack, LyricLine
from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font
from qt6_player.components.transport_bar import TransportIconButton
from qt6_player.components.fluent_widgets import draw_nordic_icon


class MiniSpinningArtwork(QWidget):
    clicked = pyqtSignal()

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.track: Optional[NordicTrack] = None
        self.pixmap: Optional[QPixmap] = None
        self.is_playing: bool = False
        self.angle: float = 0.0
        self.angular_vel: float = 0.0
        self.setFixedSize(76, 68)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setToolTip("点击返回完整主窗口")

        self._clock = QElapsedTimer()
        self._clock.start()
        self._timer = QTimer(self)
        self._timer.setInterval(28)
        self._timer.timeout.connect(self._step_spin)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def set_track(self, track: NordicTrack) -> None:
        self.track = track
        self.pixmap = None
        base_pix = track.get_cover_pixmap() if track is not None else None
        if base_pix is not None and not base_pix.isNull():
            self.pixmap = base_pix.scaled(
                120,
                120,
                Qt.AspectRatioMode.KeepAspectRatioByExpanding,
                Qt.TransformationMode.SmoothTransformation,
            )
        if self.isVisible():
            self.update()

    def set_playing(self, playing: bool) -> None:
        self.is_playing = bool(playing)
        self._clock.restart()
        if self.isVisible() and not self._timer.isActive():
            self._timer.start()

    def showEvent(self, event) -> None:
        super().showEvent(event)
        if self.is_playing and not self._timer.isActive():
            self._clock.restart()
            self._timer.start()

    def hideEvent(self, event) -> None:
        super().hideEvent(event)
        self._timer.stop()

    def _step_spin(self) -> None:
        if not self.isVisible():
            self._timer.stop()
            return
        dt = max(0.002, min(0.06, self._clock.restart() / 1000.0))
        target = 48.0 if self.is_playing else 0.0
        self.angular_vel += (target - self.angular_vel) * (1.0 - math.exp(-3.5 * dt))
        if abs(self.angular_vel) > 0.2:
            self.angle = (self.angle + self.angular_vel * dt) % 360.0
            self.update()
        elif not self.is_playing:
            self.angular_vel = 0.0
            self._timer.stop()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self.clicked.emit()
            event.accept()
            return
        super().mousePressEvent(event)

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)

        # 右侧微露旋转小黑胶
        disc_cx, disc_cy = 46.0, 34.0
        disc_r = 27.0
        painter.save()
        painter.translate(disc_cx, disc_cy)
        painter.rotate(self.angle)
        grad = QRadialGradient(QPointF(0.0, 0.0), disc_r)
        wood_col = QColor(self.track.palette["wood"]) if self.track else QColor("#C9BBAE")
        grad.setColorAt(0.0, wood_col)
        grad.setColorAt(0.28, wood_col)
        grad.setColorAt(0.32, QColor("#26292E"))
        grad.setColorAt(0.75, QColor("#1C1F23"))
        grad.setColorAt(1.0, QColor("#15171A"))
        painter.setPen(QPen(QColor(255, 255, 255, 30), 0.8))
        painter.setBrush(grad)
        painter.drawEllipse(QPointF(0.0, 0.0), disc_r, disc_r)
        painter.setPen(QPen(self.theme.sage_accent, 1.5))
        painter.drawArc(QRectF(-6.0, -6.0, 12.0, 12.0), 20 * 16, 120 * 16)
        painter.restore()

        # 左侧圆角封面
        cov_rect = QRectF(2.0, 4.0, 60.0, 60.0)
        cov_path = QPainterPath()
        cov_path.addRoundedRect(cov_rect, 8.0, 8.0)
        painter.save()
        painter.setClipPath(cov_path)
        if self.pixmap is not None and not self.pixmap.isNull():
            painter.drawPixmap(cov_rect.toRect(), self.pixmap)
        elif self.track is not None:
            g = QLinearGradient(cov_rect.topLeft(), cov_rect.bottomRight())
            g.setColorAt(0.0, QColor(self.track.palette["bg_primary"]))
            g.setColorAt(1.0, QColor(self.track.palette["bg_secondary"]))
            painter.fillRect(cov_rect, g)
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(QColor(self.track.palette["accent"]))
            painter.drawEllipse(cov_rect.center(), 12.0, 12.0)
        else:
            painter.fillRect(cov_rect, self.theme.card_elevated)
        painter.restore()
        painter.setPen(QPen(self.theme.stroke_outer, 1.0))
        painter.setBrush(Qt.BrushStyle.NoBrush)
        painter.drawPath(cov_path)


class MiniPlayerWindow(QWidget):
    playToggled = pyqtSignal()
    prevClicked = pyqtSignal()
    nextClicked = pyqtSignal()
    seekRatioRequested = pyqtSignal(float)
    restoreRequested = pyqtSignal()

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.progress_ratio: float = 0.0
        self._drag_pos: Optional[QPoint] = None

        self.setWindowTitle("Klang Mini")
        self.setFixedSize(368, 108)
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.Window
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.setMouseTracking(True)

        root = QHBoxLayout(self)
        root.setContentsMargins(14, 12, 14, 16)
        root.setSpacing(10)

        self.art_widget = MiniSpinningArtwork(self)
        self.art_widget.clicked.connect(self.restoreRequested.emit)
        root.addWidget(self.art_widget)

        info_col = QVBoxLayout()
        info_col.setContentsMargins(0, 2, 0, 2)
        info_col.setSpacing(2)

        self.lbl_title = QLabel("Klang", self)
        self.lbl_title.setFont(get_nordic_font(13, QFont.Weight.DemiBold))
        self.lbl_artist = QLabel("", self)
        self.lbl_artist.setFont(get_nordic_font(11, QFont.Weight.Medium))
        self.lbl_lyric = QLabel("点击封面返回完整视图", self)
        self.lbl_lyric.setFont(get_nordic_font(11, QFont.Weight.Normal))

        info_col.addWidget(self.lbl_title)
        info_col.addWidget(self.lbl_artist)
        info_col.addWidget(self.lbl_lyric)
        root.addLayout(info_col, 1)

        ctrl_row = QHBoxLayout()
        ctrl_row.setSpacing(4)
        self.btn_prev = TransportIconButton("prev", parent=self)
        self.btn_prev.setFixedSize(28, 28)
        self.btn_play = TransportIconButton("play", is_primary=True, parent=self)
        self.btn_play.setFixedSize(36, 36)
        self.btn_next = TransportIconButton("next", parent=self)
        self.btn_next.setFixedSize(28, 28)

        self.btn_restore = TransportIconButton("mode", parent=self)
        self.btn_restore.setFixedSize(26, 26)
        self.btn_restore.setToolTip("返回主界面 (Ctrl+M)")
        self.btn_restore.paintEvent = self._paint_restore_btn  # type: ignore

        self.btn_prev.clicked.connect(self.prevClicked.emit)
        self.btn_play.clicked.connect(self.playToggled.emit)
        self.btn_next.clicked.connect(self.nextClicked.emit)
        self.btn_restore.clicked.connect(self.restoreRequested.emit)

        ctrl_row.addWidget(self.btn_prev)
        ctrl_row.addWidget(self.btn_play)
        ctrl_row.addWidget(self.btn_next)
        ctrl_row.addWidget(self.btn_restore)
        root.addLayout(ctrl_row)

        self._entry_anim = QVariantAnimation(self)
        self._entry_anim.setDuration(240)
        self._entry_anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._entry_anim.valueChanged.connect(lambda v: self.setWindowOpacity(max(0.01, float(v))))

        self.set_theme(DAYLIGHT_THEME)

    def _paint_restore_btn(self, event) -> None:
        p = QPainter(self.btn_restore)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        r = QRectF(self.btn_restore.rect()).adjusted(1, 1, -1, -1)
        if self.btn_restore.underMouse():
            path = QPainterPath()
            path.addRoundedRect(r, 5.0, 5.0)
            p.fillPath(path, self.theme.subtle_hover)
        draw_nordic_icon(
            p,
            "mini_player",
            r,
            self.theme.sage_accent if self.btn_restore.underMouse() else self.theme.slate_muted,
            stroke_w=1.35,
        )

    def show_animated(self) -> None:
        self.setWindowOpacity(0.01)
        self.show()
        self.raise_()
        self._entry_anim.stop()
        self._entry_anim.setStartValue(0.0)
        self._entry_anim.setEndValue(1.0)
        self._entry_anim.start()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.art_widget.set_theme(theme)
        self.lbl_title.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        self.lbl_artist.setStyleSheet(f"color: {theme.slate_dark.name()}; background: transparent;")
        self.lbl_lyric.setStyleSheet(f"color: {theme.sage_accent.name()}; background: transparent;")
        for b in (self.btn_prev, self.btn_play, self.btn_next, self.btn_restore):
            b.set_theme(theme)
        self.update()

    def set_track(self, track: NordicTrack) -> None:
        self.art_widget.set_track(track)
        fm_t = self.lbl_title.fontMetrics()
        fm_a = self.lbl_artist.fontMetrics()
        self.lbl_title.setText(fm_t.elidedText(track.title, Qt.TextElideMode.ElideRight, 132))
        self.lbl_artist.setText(fm_a.elidedText(track.artist, Qt.TextElideMode.ElideRight, 132))

    def set_playing(self, playing: bool) -> None:
        self.art_widget.set_playing(playing)
        self.btn_play.set_playing_animated(playing)

    def update_lyric(self, line: LyricLine) -> None:
        txt = line.sub if line.is_interlude else line.text
        fm = self.lbl_lyric.fontMetrics()
        self.lbl_lyric.setText(fm.elidedText(txt or "· · ·", Qt.TextElideMode.ElideRight, 136))

    def update_progress(self, current_time: float, duration: float) -> None:
        new_ratio = max(0.0, min(1.0, current_time / max(1.0, duration)))
        if abs(new_ratio - self.progress_ratio) > 0.002:
            self.progress_ratio = new_ratio
            if self.isVisible():
                self.update()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            if event.position().y() >= self.height() - 12.0:
                ratio = max(0.0, min(1.0, (event.position().x() - 14.0) / max(1.0, self.width() - 28.0)))
                self.seekRatioRequested.emit(ratio)
                event.accept()
                return
            self._drag_pos = event.globalPosition().toPoint() - self.frameGeometry().topLeft()
            event.accept()
            return
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if self._drag_pos is not None and (event.buttons() & Qt.MouseButton.LeftButton):
            self.move(event.globalPosition().toPoint() - self._drag_pos)
            event.accept()
            return
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        self._drag_pos = None
        super().mouseReleaseEvent(event)

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        rect = QRectF(self.rect()).adjusted(1.0, 1.0, -1.0, -1.0)
        path = QPainterPath()
        path.addRoundedRect(rect, 14.0, 14.0)

        bg = QColor(self.theme.mist_white)
        bg.setAlpha(246)
        painter.fillPath(path, bg)
        painter.setPen(QPen(self.theme.sage_border, 1.0))
        painter.drawPath(path)

        # 底部内嵌流光进度条
        bar_left = 14.0
        bar_w = self.width() - 28.0
        bar_y = self.height() - 6.0
        bg_bar = QRectF(bar_left, bar_y, bar_w, 3.0)
        tr_col = QColor(self.theme.slate_muted)
        tr_col.setAlpha(45)
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(tr_col)
        painter.drawRoundedRect(bg_bar, 1.5, 1.5)

        if self.progress_ratio > 0.002:
            fill_bar = QRectF(bar_left, bar_y, max(4.0, bar_w * self.progress_ratio), 3.0)
            painter.setBrush(self.theme.sage_accent)
            painter.drawRoundedRect(fill_bar, 1.5, 1.5)
