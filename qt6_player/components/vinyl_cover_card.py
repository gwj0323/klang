"""
专辑封面与曲目信息组件 (Qt6)
支持渲染真实专辑封面图片 (内嵌封面 / 本地图片 / 在线刮削封面) 以及无封面时的北欧几何默认封面。
"""

from typing import Optional
from PyQt6.QtCore import Qt, QRectF, QPointF, QVariantAnimation, QEasingCurve, pyqtSignal
from PyQt6.QtGui import QPainter, QPen, QColor, QFont, QPainterPath, QRadialGradient, QPixmap, QMouseEvent
from PyQt6.QtWidgets import QWidget, QVBoxLayout, QLabel, QGraphicsDropShadowEffect

from qt6_player.data.tracks import NordicTrack
from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font


class NordicArtworkCanvas(QWidget):
    """专辑封面画布：支持真实封面渲染 + 右侧微露黑胶唱片动效"""

    clicked = pyqtSignal()

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.track: Optional[NordicTrack] = None
        self.cover_pixmap: Optional[QPixmap] = None
        self.vinyl_slide: float = 0.0
        self.transition_prog: float = 1.0

        self.setFixedSize(296, 296)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setToolTip("点击可在线搜索匹配封面与歌词")

        shadow = QGraphicsDropShadowEffect(self)
        shadow.setBlurRadius(36)
        shadow.setOffset(0, 14)
        shadow.setColor(QColor(38, 41, 46, 28))
        self.setGraphicsEffect(shadow)

        self._slide_anim = QVariantAnimation(self)
        self._slide_anim.setDuration(550)
        self._slide_anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._slide_anim.valueChanged.connect(self._on_slide_val)

        self._switch_anim = QVariantAnimation(self)
        self._switch_anim.setDuration(290)
        self._switch_anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._switch_anim.valueChanged.connect(self._on_switch_val)

    def _on_slide_val(self, val: float) -> None:
        self.vinyl_slide = float(val)
        self.update()

    def _on_switch_val(self, val: float) -> None:
        self.transition_prog = float(val)
        self.update()

    def set_playing(self, playing: bool) -> None:
        self._slide_anim.stop()
        self._slide_anim.setStartValue(self.vinyl_slide)
        self._slide_anim.setEndValue(1.0 if playing else 0.0)
        self._slide_anim.start()

    def set_track(self, track: NordicTrack) -> None:
        is_change = self.track is not None and self.track.track_id != track.track_id
        self.track = track
        self.reload_cover_pixmap()
        if is_change:
            self._switch_anim.stop()
            self._switch_anim.setStartValue(0.0)
            self._switch_anim.setEndValue(1.0)
            self._switch_anim.start()
        else:
            self.transition_prog = 1.0

    def reload_cover_pixmap(self) -> None:
        self.cover_pixmap = None
        if self.track is None:
            self.update()
            return
        pix = QPixmap()
        if self.track.cover_bytes and pix.loadFromData(self.track.cover_bytes):
            self.cover_pixmap = pix
        elif self.track.cover_path and pix.load(self.track.cover_path):
            self.cover_pixmap = pix
        self.update()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self.clicked.emit()
        super().mousePressEvent(event)

    def paintEvent(self, event) -> None:
        if self.track is None:
            return
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)

        painter.setOpacity(0.45 + 0.55 * self.transition_prog)
        scale_f = 0.96 + 0.04 * self.transition_prog
        cx_all, cy_all = self.width() / 2.0, self.height() / 2.0
        painter.translate(cx_all, cy_all)
        painter.scale(scale_f, scale_f)
        painter.translate(-cx_all, -cy_all)


        # 1. 右侧微露黑胶唱片
        disc_radius = 104.0
        disc_cx = 166.0 + self.vinyl_slide * 14.0
        disc_cy = self.height() / 2.0
        disc_grad = QRadialGradient(QPointF(disc_cx, disc_cy), disc_radius)
        disc_grad.setColorAt(0.0, QColor(self.track.palette["wood"]))
        disc_grad.setColorAt(0.18, QColor(self.track.palette["wood"]))
        disc_grad.setColorAt(0.20, QColor("#2B2E33"))
        disc_grad.setColorAt(0.55, QColor("#22252A"))
        disc_grad.setColorAt(0.58, QColor("#2E3238"))
        disc_grad.setColorAt(1.0, QColor("#1E2125"))

        painter.setPen(QPen(QColor(255, 255, 255, 24), 1.0))
        painter.setBrush(disc_grad)
        painter.drawEllipse(QPointF(disc_cx, disc_cy), disc_radius, disc_radius)

        # 2. 封面画框外衬
        card_rect = QRectF(4.0, 8.0, 268.0, 268.0)
        card_path = QPainterPath()
        card_path.addRoundedRect(card_rect, 10.0, 10.0)
        painter.fillPath(card_path, self.theme.card_elevated)
        painter.setPen(QPen(self.theme.stroke_outer, 1.0))
        painter.setBrush(Qt.BrushStyle.NoBrush)
        painter.drawPath(card_path)

        # 3. 内部封面图像区域
        art_rect = card_rect.adjusted(10.0, 10.0, -10.0, -10.0)
        art_path = QPainterPath()
        art_path.addRoundedRect(art_rect, 6.0, 6.0)

        painter.save()
        painter.setClipPath(art_path)

        if self.cover_pixmap is not None and not self.cover_pixmap.isNull():
            scaled = self.cover_pixmap.scaled(
                int(art_rect.width() * self.devicePixelRatioF()),
                int(art_rect.height() * self.devicePixelRatioF()),
                Qt.AspectRatioMode.KeepAspectRatioByExpanding,
                Qt.TransformationMode.SmoothTransformation,
            )
            painter.drawPixmap(art_rect.toRect(), scaled)
        else:
            # 无封面图片时的极简几何纯图形底衬（无多余文字）
            bg1 = QColor(self.track.palette["bg_primary"])
            bg2 = QColor(self.track.palette["bg_secondary"])
            accent = QColor(self.track.palette["accent"])
            wood = QColor(self.track.palette["wood"])

            painter.fillRect(art_rect, bg1)
            wood_c = QColor(wood)
            wood_c.setAlpha(125)
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(wood_c)
            painter.drawEllipse(QPointF(art_rect.left() + 168, art_rect.top() + 78), 44.0, 44.0)

            wave1 = QPainterPath()
            wave1.moveTo(art_rect.left(), art_rect.top() + 164)
            wave1.quadTo(art_rect.left() + 80, art_rect.top() + 136, art_rect.left() + 150, art_rect.top() + 154)
            wave1.quadTo(art_rect.left() + 210, art_rect.top() + 170, art_rect.right(), art_rect.top() + 148)
            wave1.lineTo(art_rect.right(), art_rect.bottom())
            wave1.lineTo(art_rect.left(), art_rect.bottom())
            wave1.closeSubpath()
            painter.fillPath(wave1, bg2)

            wave2 = QPainterPath()
            wave2.moveTo(art_rect.left(), art_rect.top() + 196)
            wave2.quadTo(art_rect.left() + 100, art_rect.top() + 178, art_rect.right(), art_rect.top() + 190)
            wave2.lineTo(art_rect.right(), art_rect.bottom())
            wave2.lineTo(art_rect.left(), art_rect.bottom())
            wave2.closeSubpath()
            acc_c = QColor(accent)
            acc_c.setAlpha(135)
            painter.fillPath(wave2, acc_c)

        painter.restore()
        painter.setPen(QPen(self.theme.stroke_outer, 1.0))
        painter.setBrush(Qt.BrushStyle.NoBrush)
        painter.drawPath(art_path)


class VinylCoverCard(QWidget):
    scrapeRequested = pyqtSignal()

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.setFixedWidth(316)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(8, 12, 8, 12)
        layout.setSpacing(6)
        layout.addStretch(1)

        self.artwork = NordicArtworkCanvas(self)
        self.artwork.clicked.connect(self.scrapeRequested.emit)
        layout.addWidget(self.artwork, 0, Qt.AlignmentFlag.AlignLeft)
        layout.addSpacing(14)

        self.title_label = QLabel("", self)
        self.title_label.setFont(get_nordic_font(22, QFont.Weight.DemiBold))
        self.title_label.setWordWrap(True)
        layout.addWidget(self.title_label)

        self.artist_label = QLabel("", self)
        self.artist_label.setFont(get_nordic_font(14, QFont.Weight.Medium))
        self.artist_label.setWordWrap(True)
        layout.addWidget(self.artist_label)

        self.album_label = QLabel("", self)
        self.album_label.setFont(get_nordic_font(12, QFont.Weight.Normal))
        self.album_label.setWordWrap(True)
        layout.addWidget(self.album_label)

        layout.addStretch(1)
        self.set_theme(DAYLIGHT_THEME)

    def update_track(self, track: NordicTrack) -> None:
        self.artwork.set_track(track)
        self.title_label.setText(track.title)
        self.artist_label.setText(track.artist)
        self.album_label.setText(track.album or "")
        self.album_label.setVisible(bool(track.album))

    def refresh_cover(self) -> None:
        self.artwork.reload_cover_pixmap()

    def set_playing(self, is_playing: bool) -> None:
        self.artwork.set_playing(is_playing)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.artwork.set_theme(theme)
        self.title_label.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        self.artist_label.setStyleSheet(f"color: {theme.slate_dark.name()}; background: transparent;")
        self.album_label.setStyleSheet(f"color: {theme.slate_muted.name()}; background: transparent;")
        self.update()
