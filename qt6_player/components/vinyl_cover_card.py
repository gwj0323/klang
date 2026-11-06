import math
from pathlib import Path
from typing import List, Optional, Tuple
from PyQt6.QtCore import (
    Qt,
    QRectF,
    QPointF,
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
    QRadialGradient,
    QConicalGradient,
    QLinearGradient,
    QPixmap,
    QImage,
    QMouseEvent,
)
from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
)

from qt6_player.data.tracks import NordicTrack
from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font


def extract_ambient_colors(pixmap: Optional[QPixmap], track: NordicTrack) -> Tuple[QColor, QColor]:
    if pixmap is not None and not pixmap.isNull():
        try:
            small = pixmap.scaled(16, 16, Qt.AspectRatioMode.IgnoreAspectRatio, Qt.TransformationMode.FastTransformation)
            img = small.toImage().convertToFormat(QImage.Format.Format_RGB32)
            r1 = g1 = b1 = 0
            r2 = g2 = b2 = 0
            c1_cnt = c2_cnt = 0
            for y in range(16):
                for x in range(16):
                    col = img.pixelColor(x, y)
                    if (x + y) < 16:
                        r1 += col.red()
                        g1 += col.green()
                        b1 += col.blue()
                        c1_cnt += 1
                    else:
                        r2 += col.red()
                        g2 += col.green()
                        b2 += col.blue()
                        c2_cnt += 1
            if c1_cnt > 0 and c2_cnt > 0:
                col1 = QColor(r1 // c1_cnt, g1 // c1_cnt, b1 // c1_cnt)
                col2 = QColor(r2 // c2_cnt, g2 // c2_cnt, b2 // c2_cnt)
                h1, s1, l1, _ = col1.getHslF()
                h2, s2, l2, _ = col2.getHslF()
                col1.setHslF(h1 if h1 >= 0 else 0.35, min(0.42, s1 * 0.72), max(0.35, min(0.72, l1)))
                col2.setHslF(h2 if h2 >= 0 else 0.12, min(0.42, s2 * 0.72), max(0.35, min(0.72, l2)))
                return col1, col2
        except Exception:
            pass

    pal = track.palette or {}
    return QColor(pal.get("accent", "#6E8277")), QColor(pal.get("wood", "#C9BBAE"))


class NordicArtworkCanvas(QWidget):
    clicked = pyqtSignal()

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.track: Optional[NordicTrack] = None
        self.cover_pixmap: Optional[QPixmap] = None
        self.is_playing: bool = False
        self.vinyl_slide: float = 0.0
        self.transition_prog: float = 1.0
        self.vinyl_angle: float = 0.0
        self.angular_vel: float = 0.0  # deg/s，带启停阻尼惯性
        self._cached_disc_pixmap: Optional[QPixmap] = None
        self._cached_card_pixmap: Optional[QPixmap] = None
        self._cached_dpr: float = 0.0

        self.setFixedSize(304, 296)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setToolTip("点击可在线搜索匹配封面与歌词")

        self._slide_anim = QVariantAnimation(self)
        self._slide_anim.setDuration(580)
        slide_curve = QEasingCurve(QEasingCurve.Type.OutBack)
        slide_curve.setOvershoot(1.08)
        self._slide_anim.setEasingCurve(slide_curve)
        self._slide_anim.valueChanged.connect(self._on_slide_val)

        self._switch_anim = QVariantAnimation(self)
        self._switch_anim.setDuration(380)
        sw_curve = QEasingCurve(QEasingCurve.Type.OutBack)
        sw_curve.setOvershoot(1.12)
        self._switch_anim.setEasingCurve(sw_curve)
        self._switch_anim.valueChanged.connect(self._on_switch_val)

        self._spin_clock = QElapsedTimer()
        self._spin_clock.start()
        self._spin_timer = QTimer(self)
        self._spin_timer.setInterval(24)
        self._spin_timer.timeout.connect(self._step_vinyl_spin)

    def _on_slide_val(self, val: float) -> None:
        self.vinyl_slide = float(val)
        if self.isVisible():
            self.update()

    def _on_switch_val(self, val: float) -> None:
        self.transition_prog = float(val)
        if self.isVisible():
            self.update()

    def _step_vinyl_spin(self) -> None:
        dt = max(0.002, min(0.06, self._spin_clock.restart() / 1000.0))
        target_vel = 45.0 if self.is_playing else 0.0
        # 阻尼惯性加速与缓慢停盘
        lerp = 1.0 - math.exp((-3.6 if self.is_playing else -2.6) * dt)
        self.angular_vel += (target_vel - self.angular_vel) * lerp

        if abs(self.angular_vel) > 0.15:
            self.vinyl_angle = (self.vinyl_angle + self.angular_vel * dt) % 360.0
            if self.isVisible():
                if (
                    self._slide_anim.state() != QVariantAnimation.State.Running
                    and self._switch_anim.state() != QVariantAnimation.State.Running
                ):
                    # 仅刷新右侧露出黑胶与唱臂的 40×224 狭长月牙区，避免整块 304×296 封面重绘
                    self.update(264, 36, 40, 224)
                else:
                    self.update()
        elif not self.is_playing:
            self.angular_vel = 0.0
            self._spin_timer.stop()

    def set_playing(self, playing: bool) -> None:
        new_state = bool(playing)
        target_slide = 1.0 if new_state else 0.0
        if self.is_playing == new_state and abs(self.vinyl_slide - target_slide) < 0.01:
            return
        self.is_playing = new_state
        self._slide_anim.stop()
        self._slide_anim.setStartValue(self.vinyl_slide)
        self._slide_anim.setEndValue(target_slide)
        self._slide_anim.start()
        self._spin_clock.restart()
        if not self._spin_timer.isActive():
            self._spin_timer.start()

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
        self.cover_pixmap = self.track.get_cover_pixmap() if self.track is not None else None
        self._rebuild_caches()
        self.update()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self._rebuild_caches()
        self.update()

    def _rebuild_caches(self) -> None:
        palette = (
            self.track.palette
            if self.track is not None
            else {
                "bg_primary": "#E7EAE6",
                "bg_secondary": "#D7DFDA",
                "accent": "#6E8277",
                "wood": "#C9BBAE",
            }
        )

        dpr = max(1.0, self.devicePixelRatioF())
        self._cached_dpr = dpr

        disc_key = (dpr, palette.get("wood", "#C9BBAE"), palette.get("accent", "#6E8277"))
        if self._cached_disc_pixmap is None or getattr(self, "_cached_disc_key", None) != disc_key:
            self._cached_disc_key = disc_key
            disc_size = 212
            disc_radius = 104.0
            disc_pix = QPixmap(int(disc_size * dpr), int(disc_size * dpr))
            disc_pix.setDevicePixelRatio(dpr)
            disc_pix.fill(Qt.GlobalColor.transparent)

            dp = QPainter(disc_pix)
            dp.setRenderHint(QPainter.RenderHint.Antialiasing)
            dp.translate(disc_size / 2.0, disc_size / 2.0)

            disc_grad = QRadialGradient(QPointF(0.0, 0.0), disc_radius)
            disc_grad.setColorAt(0.0, QColor(palette["wood"]))
            disc_grad.setColorAt(0.24, QColor(palette["wood"]))
            disc_grad.setColorAt(0.26, QColor("#2B2E33"))
            disc_grad.setColorAt(0.55, QColor("#1F2227"))
            disc_grad.setColorAt(0.58, QColor("#2E3238"))
            disc_grad.setColorAt(0.82, QColor("#1C1F24"))
            disc_grad.setColorAt(1.0, QColor("#16181C"))

            dp.setPen(QPen(QColor(255, 255, 255, 28), 1.0))
            dp.setBrush(disc_grad)
            dp.drawEllipse(QPointF(0.0, 0.0), disc_radius, disc_radius)

            sheen = QConicalGradient(QPointF(0.0, 0.0), 35.0)
            sheen.setColorAt(0.00, QColor(255, 255, 255, 0))
            sheen.setColorAt(0.12, QColor(255, 255, 255, 24))
            sheen.setColorAt(0.24, QColor(255, 255, 255, 0))
            sheen.setColorAt(0.50, QColor(255, 255, 255, 0))
            sheen.setColorAt(0.62, QColor(255, 255, 255, 20))
            sheen.setColorAt(0.74, QColor(255, 255, 255, 0))
            sheen.setColorAt(1.00, QColor(255, 255, 255, 0))
            dp.setPen(Qt.PenStyle.NoPen)
            dp.setBrush(sheen)
            dp.drawEllipse(QPointF(0.0, 0.0), disc_radius - 2.0, disc_radius - 2.0)

            groove_pen = QPen(QColor(255, 255, 255, 16), 0.7)
            dp.setPen(groove_pen)
            dp.setBrush(Qt.BrushStyle.NoBrush)
            for gr in (42.0, 56.0, 70.0, 84.0, 95.0):
                dp.drawEllipse(QPointF(0.0, 0.0), gr, gr)

            lbl_r = 25.0
            dp.setPen(QPen(QColor(38, 41, 46, 50), 1.0))
            dp.setBrush(QColor(palette["wood"]))
            dp.drawEllipse(QPointF(0.0, 0.0), lbl_r, lbl_r)

            accent_col = QColor(palette.get("accent", "#6E8277"))
            dp.setPen(QPen(accent_col, 2.0, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
            dp.drawArc(QRectF(-17.0, -17.0, 34.0, 34.0), 30 * 16, 110 * 16)
            dp.drawArc(QRectF(-17.0, -17.0, 34.0, 34.0), 210 * 16, 110 * 16)

            dp.setPen(Qt.PenStyle.NoPen)
            dp.setBrush(QColor("#1B1E23"))
            dp.drawEllipse(QPointF(0.0, 0.0), 4.2, 4.2)
            dp.end()
            self._cached_disc_pixmap = disc_pix

        card_pix = QPixmap(int(self.width() * dpr), int(self.height() * dpr))
        card_pix.setDevicePixelRatio(dpr)
        card_pix.fill(Qt.GlobalColor.transparent)

        cp = QPainter(card_pix)
        cp.setRenderHint(QPainter.RenderHint.Antialiasing)
        cp.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)

        card_rect = QRectF(4.0, 8.0, 266.0, 266.0)
        for s_i, (spread, y_off, alpha) in enumerate(((5.0, 8.0, 6), (3.0, 5.0, 10), (1.2, 2.5, 14))):
            sh_rect = card_rect.adjusted(-spread, -spread + y_off, spread, spread + y_off)
            sh_path = QPainterPath()
            sh_path.addRoundedRect(sh_rect, 10.0 + spread, 10.0 + spread)
            cp.fillPath(sh_path, QColor(28, 31, 36, alpha))

        card_path = QPainterPath()
        card_path.addRoundedRect(card_rect, 10.0, 10.0)
        cp.fillPath(card_path, self.theme.card_elevated)
        cp.setPen(QPen(self.theme.stroke_outer, 1.0))
        cp.setBrush(Qt.BrushStyle.NoBrush)
        cp.drawPath(card_path)

        art_rect = card_rect.adjusted(10.0, 10.0, -10.0, -10.0)
        art_path = QPainterPath()
        art_path.addRoundedRect(art_rect, 6.0, 6.0)

        cp.save()
        cp.setClipPath(art_path)
        if self.cover_pixmap is not None and not self.cover_pixmap.isNull():
            scaled = self.cover_pixmap.scaled(
                int(art_rect.width() * dpr),
                int(art_rect.height() * dpr),
                Qt.AspectRatioMode.KeepAspectRatioByExpanding,
                Qt.TransformationMode.SmoothTransformation,
            )
            cp.drawPixmap(art_rect.toRect(), scaled)
        else:
            bg1 = QColor(palette["bg_primary"])
            bg2 = QColor(palette["bg_secondary"])
            accent = QColor(palette["accent"])
            wood = QColor(palette["wood"])

            cp.fillRect(art_rect, bg1)
            wood_c = QColor(wood)
            wood_c.setAlpha(125)
            cp.setPen(Qt.PenStyle.NoPen)
            cp.setBrush(wood_c)
            cp.drawEllipse(QPointF(art_rect.left() + 168, art_rect.top() + 78), 44.0, 44.0)

            wave1 = QPainterPath()
            wave1.moveTo(art_rect.left(), art_rect.top() + 164)
            wave1.quadTo(art_rect.left() + 80, art_rect.top() + 136, art_rect.left() + 150, art_rect.top() + 154)
            wave1.quadTo(art_rect.left() + 210, art_rect.top() + 170, art_rect.right(), art_rect.top() + 148)
            wave1.lineTo(art_rect.right(), art_rect.bottom())
            wave1.lineTo(art_rect.left(), art_rect.bottom())
            wave1.closeSubpath()
            cp.fillPath(wave1, bg2)

            wave2 = QPainterPath()
            wave2.moveTo(art_rect.left(), art_rect.top() + 196)
            wave2.quadTo(art_rect.left() + 100, art_rect.top() + 178, art_rect.right(), art_rect.top() + 190)
            wave2.lineTo(art_rect.right(), art_rect.bottom())
            wave2.lineTo(art_rect.left(), art_rect.bottom())
            wave2.closeSubpath()
            acc_c = QColor(accent)
            acc_c.setAlpha(135)
            cp.fillPath(wave2, acc_c)

        cp.restore()
        cp.setPen(QPen(self.theme.stroke_outer, 1.0))
        cp.setBrush(Qt.BrushStyle.NoBrush)
        cp.drawPath(art_path)
        cp.end()
        self._cached_card_pixmap = card_pix

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self.clicked.emit()
        super().mousePressEvent(event)

    def paintEvent(self, event) -> None:
        if (
            self._cached_disc_pixmap is None
            or self._cached_card_pixmap is None
            or abs(self._cached_dpr - self.devicePixelRatioF()) > 0.01
        ):
            self._rebuild_caches()

        painter = QPainter(self)
        painter.setClipRect(event.rect())
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)

        if self.transition_prog < 0.999:
            painter.setOpacity(max(0.0, min(1.0, 0.35 + 0.65 * self.transition_prog)))
            scale_f = 0.92 + 0.08 * self.transition_prog
            slide_x = (1.0 - self.transition_prog) * -18.0
            cx_all, cy_all = self.width() / 2.0, self.height() / 2.0
            painter.translate(cx_all + slide_x, cy_all)
            painter.scale(scale_f, scale_f)
            painter.translate(-cx_all, -cy_all)

        # 1. 绘制预渲染旋转黑胶唱片位图
        if self._cached_disc_pixmap is not None:
            disc_cx = 166.0 + self.vinyl_slide * 20.0
            disc_cy = self.height() / 2.0
            painter.save()
            painter.translate(disc_cx, disc_cy)
            painter.rotate(self.vinyl_angle)
            painter.drawPixmap(QPointF(-106.0, -106.0), self._cached_disc_pixmap)
            painter.restore()

        # 1.5 右上角极简金属唱臂
        if self.vinyl_slide > 0.02:
            painter.save()
            pivot = QPointF(284.0, 42.0)
            painter.setOpacity(min(1.0, self.vinyl_slide * 1.15))
            painter.translate(pivot)
            arm_angle = -18.0 + self.vinyl_slide * 24.0
            painter.rotate(arm_angle)

            painter.setPen(QPen(self.theme.stroke_outer, 1.0))
            painter.setBrush(self.theme.card_elevated)
            painter.drawEllipse(QPointF(0.0, 0.0), 9.0, 9.0)
            painter.setBrush(self.theme.sage_accent)
            painter.drawEllipse(QPointF(0.0, 0.0), 3.6, 3.6)

            arm_path = QPainterPath()
            arm_path.moveTo(0.0, 4.0)
            arm_path.lineTo(-4.0, 68.0)
            arm_path.lineTo(-14.0, 86.0)
            painter.setPen(
                QPen(
                    self.theme.slate_muted,
                    2.2,
                    Qt.PenStyle.SolidLine,
                    Qt.PenCapStyle.RoundCap,
                    Qt.PenJoinStyle.RoundJoin,
                )
            )
            painter.setBrush(Qt.BrushStyle.NoBrush)
            painter.drawPath(arm_path)

            painter.translate(-14.0, 86.0)
            painter.rotate(28.0)
            head_rect = QRectF(-3.0, -2.0, 6.0, 10.0)
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(self.theme.sage_accent)
            painter.drawRoundedRect(head_rect, 1.8, 1.8)
            painter.restore()

        # 2. 绘制预渲染封面画框位图
        if self._cached_card_pixmap is not None:
            painter.drawPixmap(0, 0, self._cached_card_pixmap)


class NordicSpectrumWidget(QWidget):
    """24 段北欧极简实时音频频谱组件：带弹簧上升、重力回落阻尼与峰值悬浮微点"""

    NUM_BARS = 24

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self._targets: List[float] = [0.0] * self.NUM_BARS
        self._values: List[float] = [0.04] * self.NUM_BARS
        self._peaks: List[float] = [0.04] * self.NUM_BARS
        self._peak_vels: List[float] = [0.0] * self.NUM_BARS
        self._is_playing: bool = False
        self.setFixedHeight(32)

        self._clock = QElapsedTimer()
        self._clock.start()
        self._anim_timer = QTimer(self)
        self._anim_timer.setInterval(25)
        self._anim_timer.timeout.connect(self._step_physics)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def set_playing(self, playing: bool) -> None:
        self._is_playing = bool(playing)
        if not playing:
            self._targets = [0.0] * self.NUM_BARS
        self._clock.restart()
        if not self._anim_timer.isActive():
            self._anim_timer.start()

    def set_spectrum(self, bands: List[float]) -> None:
        if not bands:
            return
        for i in range(min(self.NUM_BARS, len(bands))):
            self._targets[i] = max(0.0, min(1.0, float(bands[i])))
        if not self._anim_timer.isActive():
            self._clock.restart()
            self._anim_timer.start()

    def _step_physics(self) -> None:
        dt = max(0.002, min(0.06, self._clock.restart() / 1000.0))
        active = False
        rise_alpha = 1.0 - math.exp(-22.0 * dt)
        fall_alpha = 1.0 - math.exp(-7.5 * dt)

        for i in range(self.NUM_BARS):
            tgt = self._targets[i] if self._is_playing else 0.04
            cur = self._values[i]
            if tgt > cur:
                cur += (tgt - cur) * rise_alpha
            else:
                cur += (tgt - cur) * fall_alpha
            cur = max(0.04, min(1.0, cur))
            self._values[i] = cur

            # 峰值保持与重力加速下落
            if cur >= self._peaks[i]:
                self._peaks[i] = cur
                self._peak_vels[i] = 0.0
            else:
                self._peak_vels[i] += 1.45 * dt
                self._peaks[i] = max(cur, self._peaks[i] - self._peak_vels[i] * dt)

            if abs(cur - 0.04) > 0.008 or abs(self._peaks[i] - 0.04) > 0.008:
                active = True

        if self.isVisible():
            self.update()
        if not active and not self._is_playing:
            self._anim_timer.stop()

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        w = float(self.width())
        h = float(self.height())
        n = self.NUM_BARS
        bar_w = 3.4
        gap = min(6.2, max(2.8, (w - n * bar_w) / max(1, n - 1)))
        max_h = h - 6.0
        base_y = h - 2.0

        for i in range(n):
            x = i * (bar_w + gap)
            val = self._values[i]
            bh = max(2.8, val * max_h)
            rect = QRectF(x, base_y - bh, bar_w, bh)

            col = QColor(self.theme.sage_accent)
            col.setAlphaF(0.28 + 0.64 * val)
            path = QPainterPath()
            path.addRoundedRect(rect, 1.7, 1.7)
            painter.fillPath(path, col)

            # 悬浮峰值微点
            pk = self._peaks[i]
            if pk > val + 0.04:
                py = max(1.5, base_y - pk * max_h - 3.2)
                dot_col = QColor(self.theme.sage_accent)
                dot_col.setAlphaF(0.45)
                painter.setPen(Qt.PenStyle.NoPen)
                painter.setBrush(dot_col)
                painter.drawEllipse(QPointF(x + bar_w / 2.0, py), 1.4, 1.4)


class AudioQualityBadge(QLabel):
    """音频格式与采样率微章 (例如 FLAC · 44.1kHz)"""

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__("SYNTH · 44.1kHz", parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.setFont(get_nordic_font(10, QFont.Weight.DemiBold, mono=True))
        self.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.setFixedHeight(20)
        self._update_style()

    def set_track_format(self, track: NordicTrack) -> None:
        if track.file_path:
            ext = Path(track.file_path).suffix.upper().lstrip(".")
            if ext in ("FLAC", "WAV", "APE"):
                txt = f"{ext} LOSSLESS · 44.1kHz"
            elif ext:
                txt = f"{ext} · 44.1kHz"
            else:
                txt = "PCM · 44.1kHz"
        else:
            txt = "NORDIC SYNTH · 44.1kHz"
        self.setText(txt)
        fm = self.fontMetrics()
        self.setFixedWidth(fm.horizontalAdvance(txt) + 16)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self._update_style()

    def _update_style(self) -> None:
        t = self.theme
        bg = f"rgba({t.sage_accent.red()}, {t.sage_accent.green()}, {t.sage_accent.blue()}, 0.13)"
        border = f"rgba({t.sage_accent.red()}, {t.sage_accent.green()}, {t.sage_accent.blue()}, 0.30)"
        self.setStyleSheet(
            f"""
            QLabel {{
                background-color: {bg};
                color: {t.sage_accent.name()};
                border: 1px solid {border};
                border-radius: 5px;
                padding: 0px 6px;
            }}
            """
        )


class VinylCoverCard(QWidget):
    scrapeRequested = pyqtSignal()
    ambientColorsChanged = pyqtSignal(QColor, QColor)

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.setFixedWidth(320)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(8, 12, 8, 12)
        layout.setSpacing(6)
        layout.addStretch(1)

        self.artwork = NordicArtworkCanvas(self)
        self.artwork.clicked.connect(self.scrapeRequested.emit)
        layout.addWidget(self.artwork, 0, Qt.AlignmentFlag.AlignLeft)
        layout.addSpacing(12)

        self.title_label = QLabel("", self)
        self.title_label.setFont(get_nordic_font(22, QFont.Weight.DemiBold))
        self.title_label.setWordWrap(True)
        layout.addWidget(self.title_label)

        self.artist_label = QLabel("", self)
        self.artist_label.setFont(get_nordic_font(14, QFont.Weight.Medium))
        self.artist_label.setWordWrap(True)
        layout.addWidget(self.artist_label)

        meta_row = QHBoxLayout()
        meta_row.setContentsMargins(0, 2, 0, 0)
        meta_row.setSpacing(8)
        self.quality_badge = AudioQualityBadge(self)
        meta_row.addWidget(self.quality_badge)

        self.album_label = QLabel("", self)
        self.album_label.setFont(get_nordic_font(12, QFont.Weight.Normal))
        self.album_label.setWordWrap(True)
        meta_row.addWidget(self.album_label, 1)
        layout.addLayout(meta_row)

        layout.addSpacing(6)
        self.spectrum_widget = NordicSpectrumWidget(self)
        layout.addWidget(self.spectrum_widget)

        layout.addStretch(1)
        self.set_theme(DAYLIGHT_THEME)

    def update_track(self, track: NordicTrack) -> None:
        self.artwork.set_track(track)
        self.title_label.setText(track.title)
        self.artist_label.setText(track.artist)
        self.album_label.setText(track.album or "")
        self.album_label.setVisible(bool(track.album))
        self.quality_badge.setVisible(True)
        self.quality_badge.set_track_format(track)
        c1, c2 = extract_ambient_colors(self.artwork.cover_pixmap, track)
        self.ambientColorsChanged.emit(c1, c2)

    def clear_track(self) -> None:
        self.artwork.track = None
        self.artwork.cover_pixmap = None
        self.artwork.set_playing(False)
        self.artwork._rebuild_caches()
        self.artwork.update()
        self.title_label.setText("暂无播放曲目")
        self.artist_label.setText("点击左侧栏按钮导入本地音乐")
        self.album_label.setText("")
        self.album_label.setVisible(False)
        self.quality_badge.setVisible(False)
        self.spectrum_widget.set_playing(False)

    def refresh_cover(self) -> None:
        self.artwork.reload_cover_pixmap()
        if self.artwork.track is not None:
            c1, c2 = extract_ambient_colors(self.artwork.cover_pixmap, self.artwork.track)
            self.ambientColorsChanged.emit(c1, c2)

    def set_playing(self, is_playing: bool) -> None:
        self.artwork.set_playing(is_playing)
        self.spectrum_widget.set_playing(is_playing)

    def set_spectrum(self, bands: List[float]) -> None:
        self.spectrum_widget.set_spectrum(bands)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.artwork.set_theme(theme)
        self.quality_badge.set_theme(theme)
        self.spectrum_widget.set_theme(theme)
        self.title_label.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        self.artist_label.setStyleSheet(f"color: {theme.slate_dark.name()}; background: transparent;")
        self.album_label.setStyleSheet(f"color: {theme.slate_muted.name()}; background: transparent;")
        self.update()
