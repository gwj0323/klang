import math
import random
import time
from dataclasses import dataclass
from typing import List, Optional, Tuple
from PyQt6.QtCore import (
    Qt,
    QRectF,
    QPointF,
    QPoint,
    QTimer,
    QElapsedTimer,
    pyqtSignal,
)
from PyQt6.QtGui import (
    QPainter,
    QPen,
    QBrush,
    QColor,
    QFont,
    QFontMetricsF,
    QPainterPath,
    QPixmap,
    QImage,
    QLinearGradient,
    QRadialGradient,
    QConicalGradient,
    QMouseEvent,
    QWheelEvent,
    QKeyEvent,
    QScreen,
)
from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QGraphicsOpacityEffect,
)

from qt6_player.data.tracks import NordicTrack, LyricLine, format_timecode
from qt6_player.theme.nordic_palette import get_nordic_font


def extract_aurora_palette(pixmap: Optional[QPixmap], track: Optional[NordicTrack]) -> Tuple[QColor, QColor, QColor, QColor, QColor]:
    """从封面中提取用于全景流体极光的高动态 5 阶调色盘：主极光、副极光、暖阳流金、深海极光、午夜丝绒底色"""
    if pixmap is not None and not pixmap.isNull():
        try:
            small = pixmap.scaled(20, 20, Qt.AspectRatioMode.IgnoreAspectRatio, Qt.TransformationMode.FastTransformation)
            img = small.toImage().convertToFormat(QImage.Format.Format_RGB32)
            c1_r = c1_g = c1_b = c1_cnt = 0
            c2_r = c2_g = c2_b = c2_cnt = 0
            c3_r = c3_g = c3_b = c3_cnt = 0
            c4_r = c4_g = c4_b = c4_cnt = 0
            for y in range(20):
                for x in range(20):
                    c = img.pixelColor(x, y)
                    if x < 10 and y < 10:
                        c1_r += c.red(); c1_g += c.green(); c1_b += c.blue(); c1_cnt += 1
                    elif x >= 10 and y < 10:
                        c2_r += c.red(); c2_g += c.green(); c2_b += c.blue(); c2_cnt += 1
                    elif x < 10 and y >= 10:
                        c3_r += c.red(); c3_g += c.green(); c3_b += c.blue(); c3_cnt += 1
                    else:
                        c4_r += c.red(); c4_g += c.green(); c4_b += c.blue(); c4_cnt += 1

            if c1_cnt > 0 and c2_cnt > 0 and c3_cnt > 0 and c4_cnt > 0:
                col1 = QColor(c1_r // c1_cnt, c1_g // c1_cnt, c1_b // c1_cnt)
                col2 = QColor(c2_r // c2_cnt, c2_g // c2_cnt, c2_b // c2_cnt)
                col3 = QColor(c3_r // c3_cnt, c3_g // c3_cnt, c3_b // c3_cnt)
                col4 = QColor(c4_r // c4_cnt, c4_g // c4_cnt, c4_b // c4_cnt)

                # 增强色彩的通透度与饱和度
                h1, s1, l1, _ = col1.getHslF()
                h2, s2, l2, _ = col2.getHslF()
                h3, s3, l3, _ = col3.getHslF()
                h4, s4, l4, _ = col4.getHslF()

                col1.setHslF(max(0.0, h1), min(0.92, max(0.50, s1 * 1.45)), min(0.64, max(0.40, l1 * 1.20)))
                col2.setHslF(max(0.0, h2), min(0.88, max(0.45, s2 * 1.35)), min(0.58, max(0.35, l2 * 1.15)))
                col3.setHslF(max(0.0, h3), min(0.85, max(0.40, s3 * 1.30)), min(0.68, max(0.42, l3 * 1.25)))
                col4.setHslF(max(0.0, h4), min(0.80, max(0.38, s4 * 1.25)), min(0.52, max(0.30, l4 * 1.10)))

                # 纯净午夜深底（带微弱主色调）
                c_base = QColor()
                c_base.setHslF(max(0.0, h1), min(0.24, s1 * 0.35), 0.065)
                return col1, col2, col3, col4, c_base
        except Exception:
            pass

    pal = (track.palette if track else None) or {}
    c1 = QColor(pal.get("accent", "#3D7A63"))
    c2 = QColor(pal.get("wood", "#7D6B5C"))
    c3 = QColor(pal.get("highlight", "#BFA78A"))
    c4 = QColor("#304A5E")
    c_base = QColor("#0A0D12")
    return c1, c2, c3, c4, c_base


@dataclass
class FloatingMote:
    x: float
    y: float
    r: float
    base_alpha: float
    speed_x: float
    speed_y: float
    phase: float


class ImmersiveArtworkView(QWidget):
    """
    左半侧巨幅现代专辑封面与 Apple Music 风格下置控制区
    特性：
    - 22px 苹果平滑圆角封面，多层立体弥散投影与细腻玻璃高光
    - 背后大范围超柔 Ambilight 环境溢彩氛围光（伴随音频能量实时呼吸）
    - 可选「伴生黑胶微显」模式（仅精致露出 14%），或「纯净现代封面」模式
    - 封面正下方一体化集成：
      1. 纯白 SF Pro Display 粗体曲名 + 柔白副标题 + 音频规格胶囊
      2. 极简全宽进度条 (微悬浮加粗、白光填充、光晕滑块) + 实时已播/剩余时间码
      3. 极简现代播放控制行 (随机/顺序、上一首、无界播放/暂停、下一首、单曲/列表循环)
      4. 极简音量滑动调节行 (静音/低音量扬声器、音量滑轨、高音量扬声器)
      5. 鼠标闲置 3 秒联动柔和淡出隐藏，移动瞬间苏醒
    """

    playToggled = pyqtSignal()
    prevClicked = pyqtSignal()
    nextClicked = pyqtSignal()
    seekRatioRequested = pyqtSignal(float)
    modeCycled = pyqtSignal()
    volumeChanged = pyqtSignal(float)

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.track: Optional[NordicTrack] = None
        self.cover_pixmap: Optional[QPixmap] = None
        self.is_playing: bool = False
        self.show_vinyl: bool = False  # 默认纯净现代封面模式
        self.vinyl_angle: float = 0.0
        self.angular_vel: float = 0.0
        self.slide_prog: float = 0.0
        self.audio_energy: float = 0.0
        self.accent_color: QColor = QColor("#5B9E83")
        self._cached_disc_key: Optional[str] = None
        self._cached_disc_pixmap: Optional[QPixmap] = None

        # 播放状态与交互
        self.current_time: float = 0.0
        self.duration: float = 1.0
        self.play_mode: str = "loop_list"
        self.volume: float = 0.72
        self.controls_opacity: float = 1.0
        self.is_dragging_progress: bool = False
        self.is_dragging_volume: bool = False
        self._scrub_ratio: float = 0.0
        self._last_nonzero_volume: float = 0.72

        # 几何区域缓存 (用于鼠标精准命中测试)
        self._rect_slider = QRectF()
        self._rect_btn_play = QRectF()
        self._rect_btn_prev = QRectF()
        self._rect_btn_next = QRectF()
        self._rect_btn_mode = QRectF()
        self._rect_btn_repeat = QRectF()
        self._rect_vol_mute = QRectF()
        self._rect_vol_track = QRectF()
        self._rect_vol_max = QRectF()
        self._hover_element: Optional[str] = None

        self.setMouseTracking(True)

    def set_vinyl_mode(self, enabled: bool) -> None:
        self.show_vinyl = enabled
        self.update()

    def set_track(self, track: Optional[NordicTrack]) -> None:
        self.track = track
        self.cover_pixmap = None
        self._cached_disc_pixmap = None
        self._cached_disc_key = None
        if track is not None:
            self.cover_pixmap = track.get_cover_pixmap()
            pal = track.palette or {}
            self.accent_color = QColor(pal.get("accent", "#5B9E83"))
        self.update()

    def set_playing_state(self, is_playing: bool) -> None:
        self.is_playing = is_playing
        self.update()

    def set_play_mode(self, mode: str) -> None:
        self.play_mode = mode
        self.update()

    def set_volume(self, vol: float) -> None:
        self.volume = max(0.0, min(1.0, vol))
        if self.volume > 0.05:
            self._last_nonzero_volume = self.volume
        self.update()

    def update_time(self, cur_time: float, dur: float) -> None:
        self.current_time = max(0.0, cur_time)
        self.duration = max(1.0, dur)
        if not self.is_dragging_progress:
            self.update()

    def step_physics(self, dt: float) -> None:
        dt = max(0.001, min(0.05, dt))
        # 旋转角速度平滑推进
        target_vel = 36.0 if (self.is_playing and self.show_vinyl) else 0.0
        self.angular_vel += (target_vel - self.angular_vel) * min(1.0, dt * 3.6)
        if abs(self.angular_vel) > 0.05:
            self.vinyl_angle = (self.vinyl_angle + self.angular_vel * dt) % 360.0

        # 非线性滑出阻尼 (开启黑胶时轻微滑出露出约 14%)
        target_slide = 1.0 if (self.is_playing and self.show_vinyl) else 0.0
        slide_rate = min(1.0, 1.0 - math.exp(-6.5 * dt))
        self.slide_prog += (target_slide - self.slide_prog) * slide_rate

    def _render_vinyl_disc(self, diameter: int) -> QPixmap:
        dpr = max(1.0, self.devicePixelRatioF())
        pix = QPixmap(int(diameter * dpr), int(diameter * dpr))
        pix.setDevicePixelRatio(dpr)
        pix.fill(Qt.GlobalColor.transparent)

        p = QPainter(pix)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)

        cx, cy = diameter / 2.0, diameter / 2.0
        r_outer = diameter / 2.0 - 2.0

        # PVC 黑胶盘体
        disc_path = QPainterPath()
        disc_path.addEllipse(QPointF(cx, cy), r_outer, r_outer)
        p.fillPath(disc_path, QColor(14, 15, 18))

        # 外圈微弱金属光泽
        p.setPen(QPen(QColor(255, 255, 255, 26), 1.0))
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawPath(disc_path)

        # 细腻同心圆音轨微刻纹
        n_grooves = 18
        r_min = r_outer * 0.42
        for i in range(n_grooves):
            t_r = r_min + (r_outer - r_min) * (i / float(n_grooves - 1))
            g_pen = QPen(QColor(255, 255, 255, 12 if i % 2 == 0 else 6), 0.8)
            p.setPen(g_pen)
            p.drawEllipse(QPointF(cx, cy), t_r, t_r)

        # 唱片中央圆形贴纸
        r_label = r_outer * 0.36
        label_path = QPainterPath()
        label_path.addEllipse(QPointF(cx, cy), r_label, r_label)
        p.save()
        p.setClipPath(label_path)
        if self.cover_pixmap is not None and not self.cover_pixmap.isNull():
            p.drawPixmap(
                QRectF(cx - r_label, cy - r_label, r_label * 2.0, r_label * 2.0).toRect(),
                self.cover_pixmap,
            )
            inner_shadow = QRadialGradient(QPointF(cx, cy), r_label)
            inner_shadow.setColorAt(0.68, Qt.GlobalColor.transparent)
            inner_shadow.setColorAt(1.0, QColor(0, 0, 0, 160))
            p.fillPath(label_path, inner_shadow)
        else:
            p.fillPath(label_path, QColor(42, 50, 58))
        p.restore()

        # 金属中心主轴孔
        r_hole = r_outer * 0.052
        p.setPen(QPen(QColor(220, 200, 160), 1.2))
        p.setBrush(QColor(8, 10, 12))
        p.drawEllipse(QPointF(cx, cy), r_hole, r_hole)

        p.end()
        return pix

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            pos = event.position()
            # 1. 进度条点击与拖拽
            if self._rect_slider.adjusted(-8.0, -8.0, 8.0, 8.0).contains(pos):
                self.is_dragging_progress = True
                ratio = max(0.0, min(1.0, (pos.x() - self._rect_slider.left()) / max(1.0, self._rect_slider.width())))
                self._scrub_ratio = ratio
                self.seekRatioRequested.emit(ratio)
                self.update()
                event.accept()
                return

            # 2. 音量滑条点击与拖拽
            if self._rect_vol_track.adjusted(-8.0, -8.0, 8.0, 8.0).contains(pos):
                self.is_dragging_volume = True
                vol = max(0.0, min(1.0, (pos.x() - self._rect_vol_track.left()) / max(1.0, self._rect_vol_track.width())))
                self.set_volume(vol)
                self.volumeChanged.emit(vol)
                self.update()
                event.accept()
                return

            # 3. 播放 / 暂停按钮
            if self._rect_btn_play.contains(pos):
                self.playToggled.emit()
                event.accept()
                return

            # 4. 上一首按钮
            if self._rect_btn_prev.contains(pos):
                self.prevClicked.emit()
                event.accept()
                return

            # 5. 下一首按钮
            if self._rect_btn_next.contains(pos):
                self.nextClicked.emit()
                event.accept()
                return

            # 6. 播放模式切换 (左随机或右循环均可触发)
            if self._rect_btn_mode.contains(pos) or self._rect_btn_repeat.contains(pos):
                self.modeCycled.emit()
                event.accept()
                return

            # 7. 静音切换
            if self._rect_vol_mute.contains(pos):
                new_v = 0.0 if self.volume > 0.02 else (self._last_nonzero_volume or 0.72)
                self.set_volume(new_v)
                self.volumeChanged.emit(new_v)
                event.accept()
                return

            # 8. 最大音量按钮
            if self._rect_vol_max.contains(pos):
                self.set_volume(1.0)
                self.volumeChanged.emit(1.0)
                event.accept()
                return

        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        pos = event.position()
        if self.is_dragging_progress:
            ratio = max(0.0, min(1.0, (pos.x() - self._rect_slider.left()) / max(1.0, self._rect_slider.width())))
            self._scrub_ratio = ratio
            self.seekRatioRequested.emit(ratio)
            self.update()
            event.accept()
            return

        if self.is_dragging_volume:
            vol = max(0.0, min(1.0, (pos.x() - self._rect_vol_track.left()) / max(1.0, self._rect_vol_track.width())))
            self.set_volume(vol)
            self.volumeChanged.emit(vol)
            self.update()
            event.accept()
            return

        # 悬停测试
        prev_hover = self._hover_element
        if self._rect_slider.adjusted(-8.0, -8.0, 8.0, 8.0).contains(pos):
            self._hover_element = "slider"
        elif self._rect_btn_play.contains(pos):
            self._hover_element = "play"
        elif self._rect_btn_prev.contains(pos):
            self._hover_element = "prev"
        elif self._rect_btn_next.contains(pos):
            self._hover_element = "next"
        elif self._rect_btn_mode.contains(pos):
            self._hover_element = "mode"
        elif self._rect_btn_repeat.contains(pos):
            self._hover_element = "repeat"
        elif self._rect_vol_mute.contains(pos):
            self._hover_element = "mute"
        elif self._rect_vol_track.adjusted(-8.0, -8.0, 8.0, 8.0).contains(pos):
            self._hover_element = "vol_track"
        elif self._rect_vol_max.contains(pos):
            self._hover_element = "vol_max"
        else:
            self._hover_element = None

        if self._hover_element != prev_hover:
            if self._hover_element is not None:
                self.setCursor(Qt.CursorShape.PointingHandCursor)
            else:
                self.unsetCursor()
            self.update()

        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        if self.is_dragging_progress:
            self.is_dragging_progress = False
            self.update()
            event.accept()
            return
        if self.is_dragging_volume:
            self.is_dragging_volume = False
            self.update()
            event.accept()
            return
        super().mouseReleaseEvent(event)

    def paintEvent(self, event) -> None:
        w, h = self.width(), self.height()
        if w < 100 or h < 100:
            return

        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)
        p.setRenderHint(QPainter.RenderHint.TextAntialiasing)

        # 1. 计算自适应几何排版 (整体下移平稳居中)
        ctrl_h = 216.0 # 下方控件区总高度 (加大呼吸空间)
        max_sleeve = min(390.0, min(w * 0.74, h - ctrl_h - 80.0))
        sleeve_sz = int(max(240.0, max_sleeve))
        disc_sz = int(sleeve_sz * 0.94)

        if self.show_vinyl:
            center_x = w * 0.45
            slide_offset = sleeve_sz * (0.04 + 0.12 * self.slide_prog)
        else:
            center_x = w * 0.48
            slide_offset = 0.0

        total_content_h = sleeve_sz + ctrl_h
        # 整体下移至屏幕视觉黄金中心，避免上方过于拥挤
        sleeve_top = max(40.0, (h - total_content_h) * 0.52 + 36.0)
        sleeve_left = center_x - sleeve_sz / 2.0
        center_y = sleeve_top + sleeve_sz / 2.0

        # A. 绘制封面背后超大 Ambilight 环境溢彩氛围光
        amb_radius = sleeve_sz * (0.88 + 0.16 * self.audio_energy)
        amb_glow = QRadialGradient(QPointF(center_x, center_y), amb_radius)
        c_glow = QColor(self.accent_color)
        c_glow.setAlpha(int(round(90 + 35 * self.audio_energy)))
        amb_glow.setColorAt(0.0, c_glow)
        amb_glow.setColorAt(0.50, QColor(c_glow.red(), c_glow.green(), c_glow.blue(), 24))
        amb_glow.setColorAt(1.0, Qt.GlobalColor.transparent)
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(amb_glow)
        p.drawEllipse(QPointF(center_x, center_y), amb_radius, amb_radius)

        # B. 若开启黑胶模式，绘制微露出的伴生黑胶唱片
        if self.show_vinyl and (self.slide_prog > 0.01 or self.is_playing):
            disc_cx = center_x + slide_offset
            disc_cy = center_y

            disc_key = f"{self.track.track_id if self.track else 'none'}_{disc_sz}"
            if self._cached_disc_key != disc_key or self._cached_disc_pixmap is None:
                self._cached_disc_pixmap = self._render_vinyl_disc(disc_sz)
                self._cached_disc_key = disc_key

            if self._cached_disc_pixmap is not None:
                p.save()
                p.setOpacity(min(1.0, 0.4 + 0.6 * self.slide_prog))
                p.translate(disc_cx, disc_cy)
                p.rotate(self.vinyl_angle)
                p.drawPixmap(int(-disc_sz / 2.0), int(-disc_sz / 2.0), self._cached_disc_pixmap)

                sheen = QConicalGradient(QPointF(0, 0), 45.0)
                sheen.setColorAt(0.0, QColor(255, 255, 255, 45))
                sheen.setColorAt(0.18, Qt.GlobalColor.transparent)
                sheen.setColorAt(0.50, QColor(255, 255, 255, 45))
                sheen.setColorAt(0.68, Qt.GlobalColor.transparent)
                sheen.setColorAt(1.0, QColor(255, 255, 255, 45))

                sheen_path = QPainterPath()
                sheen_path.addEllipse(QPointF(0, 0), disc_sz / 2.0 - 2.0, disc_sz / 2.0 - 2.0)
                hole_cut = QPainterPath()
                hole_cut.addEllipse(QPointF(0, 0), disc_sz * 0.18, disc_sz * 0.18)
                p.fillPath(sheen_path.subtracted(hole_cut), sheen)
                p.restore()

        # C. 绘制前景核心现代专辑封面卡片
        sleeve_rect = QRectF(sleeve_left, sleeve_top, float(sleeve_sz), float(sleeve_sz))
        s_radius = 22.0

        for pad_w, pad_h, alpha in ((28.0, 36.0, 32), (16.0, 20.0, 60), (6.0, 8.0, 95)):
            sh_rect = sleeve_rect.adjusted(-pad_w * 0.5, pad_h * 0.6, pad_w * 0.5, pad_h * 1.4)
            sh_path = QPainterPath()
            sh_path.addRoundedRect(sh_rect, s_radius + 4.0, s_radius + 4.0)
            p.fillPath(sh_path, QColor(0, 0, 0, alpha))

        jacket_path = QPainterPath()
        jacket_path.addRoundedRect(sleeve_rect, s_radius, s_radius)
        p.save()
        p.setClipPath(jacket_path)
        if self.cover_pixmap is not None and not self.cover_pixmap.isNull():
            p.drawPixmap(sleeve_rect.toRect(), self.cover_pixmap)
        elif self.track is not None:
            grad = QLinearGradient(sleeve_rect.topLeft(), sleeve_rect.bottomRight())
            grad.setColorAt(0.0, QColor(self.track.palette["bg_primary"]))
            grad.setColorAt(1.0, QColor(self.track.palette["bg_secondary"]))
            p.fillRect(sleeve_rect, grad)
        else:
            p.fillRect(sleeve_rect, QColor(36, 42, 50))

        # 缎面高光与细描边
        glass_grad = QLinearGradient(sleeve_rect.topLeft(), sleeve_rect.bottomRight())
        glass_grad.setColorAt(0.0, QColor(255, 255, 255, 45))
        glass_grad.setColorAt(0.35, QColor(255, 255, 255, 8))
        glass_grad.setColorAt(0.38, Qt.GlobalColor.transparent)
        glass_grad.setColorAt(1.0, Qt.GlobalColor.transparent)
        p.fillRect(sleeve_rect, glass_grad)
        p.restore()

        p.setPen(QPen(QColor(255, 255, 255, 38), 1.0))
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawPath(jacket_path)

        # =========================================================================
        # D. 封面正下方 Apple Music 风格一体化控制栏 (联动 controls_opacity 渐隐)
        # =========================================================================
        ctrl_alpha = max(0.0, min(1.0, self.controls_opacity))
        if ctrl_alpha < 0.02:
            return

        p.save()
        p.setOpacity(ctrl_alpha)

        # 1. 曲名与艺术家 (对齐专辑封面宽度，增加下移呼吸距离)
        info_top = sleeve_rect.bottom() + 26.0
        is_atmos = bool(getattr(self, "dolby_atmos_active", True))
        actual_ch = int(getattr(self, "actual_channels", 2))
        if is_atmos:
            if actual_ch == 6:
                atmos_txt = "DOLBY ATMOS 5.1"
                badge_w = 118.0
            elif actual_ch == 8:
                atmos_txt = "DOLBY ATMOS 7.1"
                badge_w = 118.0
            else:
                atmos_txt = "DOLBY ATMOS"
                badge_w = 98.0
        else:
            atmos_txt = "LOSSLESS"
            badge_w = 66.0
        title_max_w = sleeve_sz - badge_w - 10.0

        title_str = self.track.title if self.track else "Klang"
        artist_str = self.track.artist if self.track else "北欧极简极光视界"
        album_str = self.track.album if (self.track and self.track.album) else ""

        # 歌曲名 (22px SF Pro Bold)
        title_font = get_nordic_font(22, QFont.Weight.Bold)
        p.setFont(title_font)
        fm_title = QFontMetricsF(title_font)
        elided_title = fm_title.elidedText(title_str, Qt.TextElideMode.ElideRight, title_max_w)
        p.setPen(QColor(255, 255, 255, 255))
        p.drawText(QRectF(sleeve_left, info_top, title_max_w, 28.0), int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter), elided_title)

        # 右侧 Apple 风格胶囊徽标 (如 LOSSLESS / DOLBY ATMOS)
        badge_rect = QRectF(sleeve_rect.right() - badge_w, info_top + 4.0, badge_w, 20.0)
        badge_path = QPainterPath()
        badge_path.addRoundedRect(badge_rect, 5.0, 5.0)

        if is_atmos:
            p.fillPath(badge_path, QColor(255, 255, 255, 30))
            p.setPen(QPen(QColor(255, 255, 255, 75), 1.0))
            p.drawPath(badge_path)
            p.setFont(get_nordic_font(8, QFont.Weight.Bold, mono=True))
            p.setPen(QColor(255, 255, 255, 240))
            p.drawText(badge_rect, int(Qt.AlignmentFlag.AlignCenter), atmos_txt)
        else:
            p.fillPath(badge_path, QColor(255, 255, 255, 20))
            p.setPen(QPen(QColor(255, 255, 255, 45), 1.0))
            p.drawPath(badge_path)
            p.setFont(get_nordic_font(9, QFont.Weight.Bold, mono=True))
            p.setPen(QColor(255, 255, 255, 210))
            p.drawText(badge_rect, int(Qt.AlignmentFlag.AlignCenter), "LOSSLESS")

        # 歌手与专辑副标题 (14px SF Pro Medium)
        sub_str = f"{artist_str}  ·  {album_str}" if album_str else artist_str
        sub_font = get_nordic_font(14, QFont.Weight.Medium)
        p.setFont(sub_font)
        fm_sub = QFontMetricsF(sub_font)
        elided_sub = fm_sub.elidedText(sub_str, Qt.TextElideMode.ElideRight, sleeve_sz)
        p.setPen(QColor(255, 255, 255, 175))
        p.drawText(QRectF(sleeve_left, info_top + 26.0, sleeve_sz, 22.0), int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter), elided_sub)

        # 2. 进度条与实时/剩余时间戳
        sl_y = info_top + 64.0
        is_sl_hov = (self._hover_element == "slider" or self.is_dragging_progress)
        bar_h = 5.0 if is_sl_hov else 3.5
        self._rect_slider = QRectF(sleeve_left, sl_y - bar_h / 2.0, sleeve_sz, bar_h)

        # 进度底轨
        p_track = QPainterPath()
        p_track.addRoundedRect(self._rect_slider, bar_h / 2.0, bar_h / 2.0)
        p.fillPath(p_track, QColor(255, 255, 255, 60))

        # 进度高亮白条
        ratio = self._scrub_ratio if self.is_dragging_progress else max(0.0, min(1.0, self.current_time / self.duration))
        fill_w = sleeve_sz * ratio
        if fill_w > 1.0:
            p_fill = QPainterPath()
            p_fill.addRoundedRect(QRectF(sleeve_left, sl_y - bar_h / 2.0, fill_w, bar_h), bar_h / 2.0, bar_h / 2.0)
            p.fillPath(p_fill, QColor(255, 255, 255, 250))

            # 滑块小圆点 (悬停时放大呈现光晕)
            th_cx = sleeve_left + fill_w
            if is_sl_hov:
                p.setPen(Qt.PenStyle.NoPen)
                p.setBrush(QColor(255, 255, 255, 50))
                p.drawEllipse(QPointF(th_cx, sl_y), 9.0, 9.0)
                p.setBrush(QColor(255, 255, 255, 255))
                p.drawEllipse(QPointF(th_cx, sl_y), 5.5, 5.5)

        # 进度条下方时间戳 (左已播时间，右负值倒计时剩余时间，同图片二)
        time_y = sl_y + 8.0
        p.setFont(get_nordic_font(11, QFont.Weight.Medium, mono=True))
        p.setPen(QColor(255, 255, 255, 175))
        left_time_str = format_timecode(self.current_time)
        rem_sec = max(0.0, self.duration - self.current_time)
        right_time_str = f"-{format_timecode(rem_sec)}"
        p.drawText(QRectF(sleeve_left, time_y, 80.0, 16.0), int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter), left_time_str)
        p.drawText(QRectF(sleeve_rect.right() - 80.0, time_y, 80.0, 16.0), int(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter), right_time_str)

        # 3. 核心播放控制行 (同图片二横向排版)
        btn_cy = sl_y + 48.0
        gap = sleeve_sz * 0.20

        # A. 模式 / 随机播放 (左一)
        mode_x = center_x - gap * 2.0
        self._rect_btn_mode = QRectF(mode_x - 18.0, btn_cy - 18.0, 36.0, 36.0)
        is_mode_hov = (self._hover_element == "mode")
        is_shuffle = (self.play_mode == "shuffle")
        p.setPen(QPen(QColor(self.accent_color if is_shuffle else QColor(255, 255, 255, 230 if is_mode_hov else 165)), 1.8, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        p.setBrush(Qt.BrushStyle.NoBrush)
        # 交叉随机箭头
        p.drawLine(QPointF(mode_x - 7.0, btn_cy - 5.0), QPointF(mode_x - 2.0, btn_cy - 5.0))
        p.drawLine(QPointF(mode_x - 2.0, btn_cy - 5.0), QPointF(mode_x + 3.0, btn_cy + 5.0))
        p.drawLine(QPointF(mode_x + 3.0, btn_cy + 5.0), QPointF(mode_x + 7.0, btn_cy + 5.0))
        p.drawLine(QPointF(mode_x - 7.0, btn_cy + 5.0), QPointF(mode_x - 2.0, btn_cy + 5.0))
        p.drawLine(QPointF(mode_x - 2.0, btn_cy + 5.0), QPointF(mode_x + 3.0, btn_cy - 5.0))
        p.drawLine(QPointF(mode_x + 3.0, btn_cy - 5.0), QPointF(mode_x + 7.0, btn_cy - 5.0))
        # 箭头头部
        p.drawLine(QPointF(mode_x + 4.0, btn_cy - 8.0), QPointF(mode_x + 7.0, btn_cy - 5.0))
        p.drawLine(QPointF(mode_x + 4.0, btn_cy - 2.0), QPointF(mode_x + 7.0, btn_cy - 5.0))
        p.drawLine(QPointF(mode_x + 4.0, btn_cy + 2.0), QPointF(mode_x + 7.0, btn_cy + 5.0))
        p.drawLine(QPointF(mode_x + 4.0, btn_cy + 8.0), QPointF(mode_x + 7.0, btn_cy + 5.0))

        # B. 上一首按钮 (左二)
        prev_x = center_x - gap
        self._rect_btn_prev = QRectF(prev_x - 20.0, btn_cy - 20.0, 40.0, 40.0)
        is_prev_hov = (self._hover_element == "prev")
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(255, 255, 255, 255 if is_prev_hov else 210))
        p.drawRoundedRect(QRectF(prev_x - 9.0, btn_cy - 8.0, 2.6, 16.0), 1.0, 1.0)
        tri_prev = QPainterPath()
        tri_prev.moveTo(prev_x + 7.0, btn_cy - 8.0)
        tri_prev.lineTo(prev_x - 5.0, btn_cy)
        tri_prev.lineTo(prev_x + 7.0, btn_cy + 8.0)
        tri_prev.closeSubpath()
        p.drawPath(tri_prev)

        # C. 播放 / 暂停按钮 (中央核心大键，同图片二干净纯白无圈)
        self._rect_btn_play = QRectF(center_x - 24.0, btn_cy - 24.0, 48.0, 48.0)
        is_play_hov = (self._hover_element == "play")
        if is_play_hov:
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(255, 255, 255, 30))
            p.drawEllipse(QPointF(center_x, btn_cy), 24.0, 24.0)

        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(255, 255, 255, 255))
        if self.is_playing:
            bar_w = 4.6
            bar_h = 18.0
            p.drawRoundedRect(QRectF(center_x - 7.0, btn_cy - bar_h / 2.0, bar_w, bar_h), 1.6, 1.6)
            p.drawRoundedRect(QRectF(center_x + 2.4, btn_cy - bar_h / 2.0, bar_w, bar_h), 1.6, 1.6)
        else:
            tri = QPainterPath()
            tri.moveTo(center_x - 6.5, btn_cy - 10.5)
            tri.lineTo(center_x + 9.5, btn_cy)
            tri.lineTo(center_x - 6.5, btn_cy + 10.5)
            tri.closeSubpath()
            p.drawPath(tri)

        # D. 下一首按钮 (右二)
        next_x = center_x + gap
        self._rect_btn_next = QRectF(next_x - 20.0, btn_cy - 20.0, 40.0, 40.0)
        is_next_hov = (self._hover_element == "next")
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(255, 255, 255, 255 if is_next_hov else 210))
        p.drawRoundedRect(QRectF(next_x + 6.4, btn_cy - 8.0, 2.6, 16.0), 1.0, 1.0)
        tri_next = QPainterPath()
        tri_next.moveTo(next_x - 7.0, btn_cy - 8.0)
        tri_next.lineTo(next_x + 5.0, btn_cy)
        tri_next.lineTo(next_x - 7.0, btn_cy + 8.0)
        tri_next.closeSubpath()
        p.drawPath(tri_next)

        # E. 循环模式按钮 (右一，支持列表循环/单曲循环)
        rep_x = center_x + gap * 2.0
        self._rect_btn_repeat = QRectF(rep_x - 18.0, btn_cy - 18.0, 36.0, 36.0)
        is_rep_hov = (self._hover_element == "repeat")
        is_loop = (self.play_mode in ("loop_list", "loop_single"))
        is_single = (self.play_mode == "loop_single")
        p.setPen(QPen(QColor(self.accent_color if is_loop else QColor(255, 255, 255, 230 if is_rep_hov else 165)), 1.8, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        p.setBrush(Qt.BrushStyle.NoBrush)
        # 闭环圆弧箭头
        rep_rect = QRectF(rep_x - 7.5, btn_cy - 6.5, 15.0, 13.0)
        p.drawArc(rep_rect, 45 * 16, 270 * 16)
        p.drawLine(QPointF(rep_x + 7.5, btn_cy - 3.0), QPointF(rep_x + 7.5, btn_cy))
        p.drawLine(QPointF(rep_x + 4.5, btn_cy - 3.0), QPointF(rep_x + 7.5, btn_cy))
        p.drawLine(QPointF(rep_x + 10.5, btn_cy - 3.0), QPointF(rep_x + 7.5, btn_cy))
        if is_single:
            p.setFont(get_nordic_font(8, QFont.Weight.Bold))
            p.setPen(QColor(self.accent_color))
            p.drawText(rep_rect, int(Qt.AlignmentFlag.AlignCenter), "1")

        # 4. 底部微型音量控制栏 (同图片二)
        vol_y = btn_cy + 42.0
        vol_pad = 12.0
        vol_track_w = sleeve_sz - vol_pad * 2.0 - 52.0
        vol_track_left = sleeve_left + vol_pad + 26.0

        # 静音/小喇叭 (左)
        self._rect_vol_mute = QRectF(sleeve_left + vol_pad - 6.0, vol_y - 10.0, 22.0, 20.0)
        p.setPen(QPen(QColor(255, 255, 255, 220 if self._hover_element == "mute" else 160), 1.4, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        p.setBrush(Qt.BrushStyle.NoBrush)
        sp_x = sleeve_left + vol_pad + 4.0
        # 小喇叭身体
        sp_path = QPainterPath()
        sp_path.moveTo(sp_x - 3.5, vol_y - 3.0)
        sp_path.lineTo(sp_x - 1.0, vol_y - 3.0)
        sp_path.lineTo(sp_x + 3.0, vol_y - 6.0)
        sp_path.lineTo(sp_x + 3.0, vol_y + 6.0)
        sp_path.lineTo(sp_x - 1.0, vol_y + 3.0)
        sp_path.lineTo(sp_x - 3.5, vol_y + 3.0)
        sp_path.closeSubpath()
        p.setBrush(QColor(255, 255, 255, 180))
        p.drawPath(sp_path)
        if self.volume > 0.05:
            # 1道微弱音波
            p.setBrush(Qt.BrushStyle.NoBrush)
            p.drawArc(QRectF(sp_x + 1.0, vol_y - 5.0, 6.0, 10.0), -60 * 16, 120 * 16)
        else:
            # 静音斜线
            p.drawLine(QPointF(sp_x - 4.0, vol_y + 5.0), QPointF(sp_x + 6.0, vol_y - 5.0))

        # 大喇叭 (右)
        self._rect_vol_max = QRectF(sleeve_rect.right() - vol_pad - 16.0, vol_y - 10.0, 22.0, 20.0)
        sp_rx = sleeve_rect.right() - vol_pad - 8.0
        sp_rpath = QPainterPath()
        sp_rpath.moveTo(sp_rx - 4.5, vol_y - 3.0)
        sp_rpath.lineTo(sp_rx - 2.0, vol_y - 3.0)
        sp_rpath.lineTo(sp_rx + 2.0, vol_y - 6.0)
        sp_rpath.lineTo(sp_rx + 2.0, vol_y + 6.0)
        sp_rpath.lineTo(sp_rx - 2.0, vol_y + 3.0)
        sp_rpath.lineTo(sp_rx - 4.5, vol_y + 3.0)
        sp_rpath.closeSubpath()
        p.setBrush(QColor(255, 255, 255, 180))
        p.drawPath(sp_rpath)
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawArc(QRectF(sp_rx, vol_y - 5.0, 6.0, 10.0), -60 * 16, 120 * 16)
        p.drawArc(QRectF(sp_rx + 2.0, vol_y - 7.5, 9.0, 15.0), -60 * 16, 120 * 16)

        # 音量滑轨
        is_vol_hov = (self._hover_element in ("vol_track", "mute", "vol_max") or self.is_dragging_volume)
        v_bar_h = 4.5 if is_vol_hov else 3.0
        self._rect_vol_track = QRectF(vol_track_left, vol_y - v_bar_h / 2.0, vol_track_w, v_bar_h)
        p_vbg = QPainterPath()
        p_vbg.addRoundedRect(self._rect_vol_track, v_bar_h / 2.0, v_bar_h / 2.0)
        p.fillPath(p_vbg, QColor(255, 255, 255, 50))

        v_fill_w = vol_track_w * max(0.0, min(1.0, self.volume))
        if v_fill_w > 1.0:
            p_vfg = QPainterPath()
            p_vfg.addRoundedRect(QRectF(vol_track_left, vol_y - v_bar_h / 2.0, v_fill_w, v_bar_h), v_bar_h / 2.0, v_bar_h / 2.0)
            p.fillPath(p_vfg, QColor(255, 255, 255, 230))

            v_cx = vol_track_left + v_fill_w
            if is_vol_hov:
                p.setPen(Qt.PenStyle.NoPen)
                p.setBrush(QColor(255, 255, 255, 45))
                p.drawEllipse(QPointF(v_cx, vol_y), 7.5, 7.5)
                p.setBrush(QColor(255, 255, 255, 255))
                p.drawEllipse(QPointF(v_cx, vol_y), 4.5, 4.5)

        p.restore()


class ImmersiveLyricsView(QWidget):
    """
    右半侧巨幅动态流光卡拉OK滚动歌词舞台 (Apple Music 标杆级视效)
    特性：
    - 非线性连续浮动光标 (Continuous Smooth Focus Cursor)，彻底消除阶梯突变
    - 苹果流体谐振弹簧滚动 (Critically Damped Harmonic Spring)
    - 连续三次 Hermite S-Curve 无级变焦缩放 (Scale 0.75 ~ 1.0)
    - 领先边缘液态渐进光波脉冲与柔光 Bloom 弥散
    - 前后行非线性景深平滑衰减
    """

    seekRequested = pyqtSignal(float)

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.setMouseTracking(True)
        self.lyrics: List[LyricLine] = []
        self.active_index: int = -1
        self.smooth_focus_idx: float = 0.0
        self.current_time: float = 0.0
        self.lyric_offset: float = 0.0
        self.syllable_mode: bool = True
        self.show_translation: bool = True
        self.accent_color: QColor = QColor("#5B9E83")

        self.scroll_y: float = 0.0
        self.scroll_target_y: float = 0.0
        self.scroll_vel: float = 0.0
        self.user_scrolling: bool = False
        self._user_scroll_timer = QTimer(self)
        self._user_scroll_timer.setSingleShot(True)
        self._user_scroll_timer.setInterval(2800)
        self._user_scroll_timer.timeout.connect(self._resume_auto_center)

        self._line_heights: List[float] = []
        self._line_y_tops: List[float] = []

    def set_lyrics(self, lyrics: List[LyricLine], offset: float = 0.0) -> None:
        self.lyrics = list(lyrics)
        self.lyric_offset = offset
        self.active_index = -1
        self.smooth_focus_idx = 0.0
        self._recompute_layout()
        self.scroll_y = 0.0
        self.scroll_target_y = 0.0
        self.scroll_vel = 0.0
        self.update()

    def sync_time(self, cur_time: float) -> None:
        self.current_time = cur_time
        eff_time = max(0.0, cur_time + self.lyric_offset)
        new_idx = -1
        for idx, line in enumerate(self.lyrics):
            if eff_time >= line.time:
                new_idx = idx
            else:
                break
        if new_idx != self.active_index:
            self.active_index = new_idx
        self.update()

    def step_physics(self, dt: float) -> None:
        if not self.lyrics or len(self._line_y_tops) != len(self.lyrics):
            return

        dt = max(0.001, min(0.05, dt))

        # 1. 非线性平滑光标跟随 (Smooth Focus Index Follower)
        target_focus = float(self.active_index) if self.active_index >= 0 else 0.0
        focus_rate = min(1.0, 1.0 - math.exp(-8.5 * dt))
        self.smooth_focus_idx += (target_focus - self.smooth_focus_idx) * focus_rate

        # 2. 目标滚动位置平滑计算 (根据平滑光标连续三次 Hermite 插值)
        if not self.user_scrolling and 0 <= self.active_index < len(self._line_y_tops):
            base_i = int(self.smooth_focus_idx)
            frac = max(0.0, min(1.0, self.smooth_focus_idx - base_i))
            frac_eased = frac * frac * (3.0 - 2.0 * frac)
            if 0 <= base_i < len(self._line_y_tops):
                y0 = self._line_y_tops[base_i] + self._line_heights[base_i] * 0.5
                if base_i + 1 < len(self._line_y_tops):
                    y1 = self._line_y_tops[base_i + 1] + self._line_heights[base_i + 1] * 0.5
                    target_y = y0 + (y1 - y0) * frac_eased
                else:
                    target_y = y0
                self.scroll_target_y = target_y

        # 3. 苹果流体谐振弹簧滚动物理 (Critically Damped Harmonic Spring)
        diff = self.scroll_target_y - self.scroll_y
        k_spring = 56.0
        damping = 15.2
        spring_accel = k_spring * diff - damping * self.scroll_vel
        self.scroll_vel += spring_accel * dt
        self.scroll_y += self.scroll_vel * dt

    def _resume_auto_center(self) -> None:
        self.user_scrolling = False
        if 0 <= self.active_index < len(self._line_y_tops):
            self.scroll_target_y = self._line_y_tops[self.active_index] + self._line_heights[self.active_index] * 0.5
        self.update()

    def set_show_translation(self, show: bool) -> None:
        if self.show_translation == bool(show):
            return
        self.show_translation = bool(show)
        self._recompute_layout()
        self.update()

    def set_syllable_mode(self, enabled: bool) -> None:
        self.syllable_mode = bool(enabled)
        self.update()

    def _recompute_layout(self) -> None:
        self._line_heights = []
        self._line_y_tops = []
        cur_y = 0.0
        for line in self.lyrics:
            h = 92.0 if (line.sub and self.show_translation) else 62.0
            self._line_y_tops.append(cur_y)
            self._line_heights.append(h)
            cur_y += h + 22.0

    def wheelEvent(self, event: QWheelEvent) -> None:
        self.user_scrolling = True
        self._user_scroll_timer.start()
        delta = -event.angleDelta().y() * 0.90
        max_scroll = self._line_y_tops[-1] if self._line_y_tops else 0.0
        self.scroll_target_y = max(-100.0, min(max_scroll + 200.0, self.scroll_target_y + delta))
        event.accept()
        self.update()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            viewport_center = self.height() * 0.45
            click_y = event.position().y()
            for idx, top_y in enumerate(self._line_y_tops):
                h = self._line_heights[idx]
                screen_y = viewport_center + (top_y - self.scroll_y)
                if screen_y <= click_y <= screen_y + h:
                    target_time = self.lyrics[idx].time
                    self.seekRequested.emit(target_time)
                    event.accept()
                    return
        super().mousePressEvent(event)

    def paintEvent(self, event) -> None:
        w, h = self.width(), self.height()
        if not self.lyrics or len(self._line_y_tops) != len(self.lyrics):
            p = QPainter(self)
            p.setRenderHint(QPainter.RenderHint.Antialiasing)
            p.setFont(get_nordic_font(26, QFont.Weight.Medium))
            p.setPen(QColor(255, 255, 255, 120))
            p.drawText(self.rect(), int(Qt.AlignmentFlag.AlignCenter), "纯音乐，请静心聆听")
            return

        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setRenderHint(QPainter.RenderHint.TextAntialiasing)
        p.setRenderHint(QPainter.RenderHint.VerticalSubpixelPositioning)

        viewport_center = h * 0.48
        pad_left = 36.0
        max_text_w = max(100.0, w - pad_left - 48.0)
        fade_zone = max(80.0, h * 0.22)

        main_font = get_nordic_font(38, QFont.Weight.Bold)
        sub_font = get_nordic_font(18, QFont.Weight.Medium)
        fm_main = QFontMetricsF(main_font)
        fm_sub = QFontMetricsF(sub_font)
        main_text_h = 48.0

        for idx, line in enumerate(self.lyrics):
            top_y = self._line_y_tops[idx]
            line_h = self._line_heights[idx]
            screen_y = viewport_center + (top_y - self.scroll_y)

            if screen_y + line_h < -40.0 or screen_y > h + 40.0:
                continue

            # 边缘羽化渐隐
            edge_factor = min(1.0, max(0.0, (h - (screen_y + line_h * 0.5)) / fade_zone), max(0.0, (screen_y + line_h * 0.5) / fade_zone))
            edge_curve = edge_factor * edge_factor * (3.0 - 2.0 * edge_factor)

            # 连续非线性距离
            d = abs(float(idx) - self.smooth_focus_idx)
            focus_raw = max(0.0, 1.0 - d)
            focus_eased = focus_raw * focus_raw * (3.0 - 2.0 * focus_raw)

            # 非线性字号缩放 (0.75 ~ 1.0 连续平滑无级变焦)
            line_scale = 0.75 + 0.25 * focus_eased

            # 非线性透明度过渡 (景深衰减)
            if d < 1.0:
                base_alpha = 0.36 + 0.64 * focus_eased
            else:
                base_alpha = max(0.10, 0.36 * math.pow(0.56, max(0.0, d - 1.0)))

            final_alpha = base_alpha * edge_curve
            if final_alpha <= 0.02:
                continue

            # Apple 风格水平浮动阶梯 (获得焦点的行向右轻微推开 10px)
            drift_x = 10.0 * focus_eased

            p.save()
            p.setOpacity(final_alpha)
            p.translate(pad_left + drift_x, screen_y)

            # 缩放歌词本体
            p.save()
            p.scale(line_scale, line_scale)

            p.setFont(main_font)
            text_rect = QRectF(0, 0, max_text_w / line_scale, main_text_h)
            elided_str = fm_main.elidedText(line.text, Qt.TextElideMode.ElideRight, max_text_w / line_scale)

            # 间奏跳动指示器
            if line.is_interlude:
                p.setBrush(QColor(255, 255, 255, int(round(240 * focus_eased + 80 * (1.0 - focus_eased)))))
                p.setPen(Qt.PenStyle.NoPen)
                for dot_i in range(3):
                    dot_r = 5.0 * line_scale
                    p.drawEllipse(QPointF(dot_i * 20.0 + 10.0, main_text_h * 0.5), dot_r, dot_r)
            else:
                is_current_line = (idx == self.active_index)
                if is_current_line and self.syllable_mode and focus_eased > 0.05:
                    eff_time = max(0.0, self.current_time + self.lyric_offset)
                    if eff_time < line.time:
                        raw_prog = 0.0
                    else:
                        line_span = max(0.6, (line.end_time - line.time) if line.end_time > line.time else 4.0)
                        raw_prog = min(1.0, max(0.0, (eff_time - line.time) / line_span))
                    prog = math.sin(raw_prog * math.pi * 0.5)

                    adv_w = min(max_text_w / line_scale, fm_main.horizontalAdvance(elided_str))

                    # 1. 绘制暗部未唱到的半透明歌词底色
                    p.setPen(QColor(255, 255, 255, 105))
                    p.drawText(text_rect, int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter), elided_str)

                    # 2. 液态流光亮部 (带边缘柔光 Feathered Gradient)
                    if prog > 0.002:
                        fill_w = adv_w * prog
                        p.save()
                        clip_rect = QRectF(-4.0, -10.0, fill_w + 4.0, main_text_h + 20.0)
                        p.setClipRect(clip_rect, Qt.ClipOperation.IntersectClip)

                        # 底层热点柔光光晕 (Bloom Glow)
                        glow_col = QColor(self.accent_color)
                        glow_col.setAlpha(int(round(90 * focus_eased)))
                        p.setPen(glow_col)
                        p.drawText(text_rect.adjusted(-1.0, -1.0, 1.0, 1.0), int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter), elided_str)

                        # 表层纯白流光高亮
                        p.setPen(QColor(255, 255, 255, 255))
                        p.drawText(text_rect, int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter), elided_str)

                        # 纯白渐进流光高亮 (去除了多余的矩形白色扫掠光标，视觉纯净平滑)

                        p.restore()
                else:
                    p_alpha = int(round(255 * focus_eased + 130 * (1.0 - focus_eased)))
                    p.setPen(QColor(255, 255, 255, p_alpha))
                    p.drawText(text_rect, int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter), elided_str)

            p.restore()

            # 绘制翻译译文行
            if line.sub and self.show_translation:
                sub_text = str(line.sub).strip()
                if sub_text:
                    sub_rect = QRectF(0, main_text_h + 2.0, max_text_w, 28.0)
                    elided_sub = fm_sub.elidedText(sub_text, Qt.TextElideMode.ElideRight, max_text_w)
                    p.setFont(sub_font)
                    sub_alpha = int(round(195 * focus_eased + 90 * (1.0 - focus_eased)))
                    p.setPen(QColor(255, 255, 255, sub_alpha))
                    p.drawText(sub_rect, int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter), elided_sub)

            p.restore()


class ImmersiveControlDock(QWidget):
    """悬浮毛玻璃胶囊控制台 (Apple Island Dock)"""

    playToggled = pyqtSignal()
    prevClicked = pyqtSignal()
    nextClicked = pyqtSignal()
    seekRatioRequested = pyqtSignal(float)
    modeCycled = pyqtSignal()
    volumeChanged = pyqtSignal(float)

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.setFixedHeight(76)
        self.is_playing: bool = False
        self.play_mode: str = "loop_list"
        self.current_time: float = 0.0
        self.duration: float = 1.0
        self.volume: float = 0.72
        self.is_dragging_progress: bool = False
        self._scrub_ratio: float = 0.0
        self.setMouseTracking(True)

    def update_state(self, cur: float, dur: float, is_playing: bool, mode: str, vol: float) -> None:
        self.current_time = cur
        self.duration = max(1.0, dur)
        self.is_playing = is_playing
        self.play_mode = mode
        self.volume = vol
        if not self.is_dragging_progress:
            self.update()

    def _get_slider_rect(self) -> QRectF:
        cx = self.width() / 2.0
        sw = max(260.0, min(440.0, self.width() * 0.46))
        return QRectF(cx - sw / 2.0, 16.0, sw, 18.0)

    def mousePressEvent(self, event: QMouseEvent) -> None:
        pos = event.position()
        sl_rect = self._get_slider_rect()
        if sl_rect.contains(pos):
            self.is_dragging_progress = True
            ratio = max(0.0, min(1.0, (pos.x() - sl_rect.left()) / sl_rect.width()))
            self._scrub_ratio = ratio
            self.seekRatioRequested.emit(ratio)
            self.update()
            event.accept()
            return

        cx = self.width() / 2.0
        cy = 50.0

        if (pos.x() - (cx - 52.0)) ** 2 + (pos.y() - cy) ** 2 <= 16.0 ** 2:
            self.prevClicked.emit()
            event.accept()
            return
        if (pos.x() - cx) ** 2 + (pos.y() - cy) ** 2 <= 22.0 ** 2:
            self.playToggled.emit()
            event.accept()
            return
        if (pos.x() - (cx + 52.0)) ** 2 + (pos.y() - cy) ** 2 <= 16.0 ** 2:
            self.nextClicked.emit()
            event.accept()
            return
        if (pos.x() - (cx - 110.0)) ** 2 + (pos.y() - cy) ** 2 <= 14.0 ** 2:
            self.modeCycled.emit()
            event.accept()
            return
        if (pos.x() - (cx + 110.0)) ** 2 + (pos.y() - cy) ** 2 <= 14.0 ** 2:
            new_v = 0.0 if self.volume > 0.05 else 0.72
            self.volumeChanged.emit(new_v)
            event.accept()
            return

        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if self.is_dragging_progress:
            sl_rect = self._get_slider_rect()
            ratio = max(0.0, min(1.0, (event.position().x() - sl_rect.left()) / sl_rect.width()))
            self._scrub_ratio = ratio
            self.seekRatioRequested.emit(ratio)
            self.update()
            event.accept()
            return
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        if self.is_dragging_progress:
            self.is_dragging_progress = False
            self.update()
            event.accept()
            return
        super().mouseReleaseEvent(event)

    def paintEvent(self, event) -> None:
        w, h = self.width(), self.height()
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)

        dock_rect = QRectF(1.0, 1.0, w - 2.0, h - 2.0)
        radius = h / 2.0
        dock_path = QPainterPath()
        dock_path.addRoundedRect(dock_rect, radius, radius)

        # 外发光阴影
        sh_path = QPainterPath()
        sh_path.addRoundedRect(dock_rect.adjusted(-2.0, 4.0, 2.0, 8.0), radius + 2.0, radius + 2.0)
        p.fillPath(sh_path, QColor(0, 0, 0, 85))

        # 深邃黑曜石半透毛玻璃底
        p.fillPath(dock_path, QColor(16, 20, 26, 225))

        # 镜面高光外细描边 (上亮下暗)
        stroke_grad = QLinearGradient(0, 0, 0, h)
        stroke_grad.setColorAt(0.0, QColor(255, 255, 255, 60))
        stroke_grad.setColorAt(1.0, QColor(255, 255, 255, 18))
        p.setPen(QPen(QBrush(stroke_grad), 1.0))
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawPath(dock_path)

        # 进度滑条区域
        sl_rect = self._get_slider_rect()
        bar_y = sl_rect.center().y()
        bar_h = 4.0
        bg_bar = QRectF(sl_rect.left(), bar_y - bar_h / 2.0, sl_rect.width(), bar_h)
        p_bg = QPainterPath()
        p_bg.addRoundedRect(bg_bar, 2.0, 2.0)
        p.fillPath(p_bg, QColor(255, 255, 255, 45))

        ratio = self._scrub_ratio if self.is_dragging_progress else max(0.0, min(1.0, self.current_time / self.duration))
        if ratio > 0.001:
            fill_w = sl_rect.width() * ratio
            fg_bar = QRectF(sl_rect.left(), bar_y - bar_h / 2.0, fill_w, bar_h)
            p_fg = QPainterPath()
            p_fg.addRoundedRect(fg_bar, 2.0, 2.0)
            p.fillPath(p_fg, QColor(255, 255, 255, 235))

            # 滑块小圆点带光晕
            th_cx = sl_rect.left() + fill_w
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(255, 255, 255, 40))
            p.drawEllipse(QPointF(th_cx, bar_y), 8.0, 8.0)
            p.setBrush(QColor(255, 255, 255, 255))
            p.drawEllipse(QPointF(th_cx, bar_y), 4.8, 4.8)

        # 左右时间戳
        p.setFont(get_nordic_font(10, QFont.Weight.Medium, mono=True))
        p.setPen(QColor(255, 255, 255, 170))
        time_left_str = format_timecode(self.current_time)
        time_right_str = format_timecode(self.duration)
        p.drawText(QRectF(sl_rect.left() - 58.0, bar_y - 9.0, 50.0, 18.0), int(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter), time_left_str)
        p.drawText(QRectF(sl_rect.right() + 8.0, bar_y - 9.0, 50.0, 18.0), int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter), time_right_str)

        # 控制按钮
        cx = w / 2.0
        cy = 51.0

        # 1. 播放/暂停大圆盘按钮 (42px)
        p_btn_rect = QRectF(cx - 21.0, cy - 21.0, 42.0, 42.0)
        p_path = QPainterPath()
        p_path.addEllipse(p_btn_rect)
        p.fillPath(p_path, QColor(255, 255, 255, 240))

        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(16, 20, 26))
        if self.is_playing:
            bar_w = 3.8
            bar_h = 14.0
            p.drawRoundedRect(QRectF(cx - 6.0, cy - bar_h / 2.0, bar_w, bar_h), 1.2, 1.2)
            p.drawRoundedRect(QRectF(cx + 2.2, cy - bar_h / 2.0, bar_w, bar_h), 1.2, 1.2)
        else:
            tri = QPainterPath()
            tri.moveTo(cx - 4.0, cy - 8.0)
            tri.lineTo(cx + 7.0, cy)
            tri.lineTo(cx - 4.0, cy + 8.0)
            tri.closeSubpath()
            p.drawPath(tri)

        # 2. 上一首按钮
        prev_x = cx - 52.0
        p.setPen(QPen(QColor(255, 255, 255, 220), 1.8, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawLine(QPointF(prev_x - 6.0, cy - 6.0), QPointF(prev_x - 6.0, cy + 6.0))
        tri_prev = QPainterPath()
        tri_prev.moveTo(prev_x + 5.0, cy - 6.0)
        tri_prev.lineTo(prev_x - 4.5, cy)
        tri_prev.lineTo(prev_x + 5.0, cy + 6.0)
        tri_prev.closeSubpath()
        p.setBrush(QColor(255, 255, 255, 220))
        p.drawPath(tri_prev)

        # 3. 下一首按钮
        next_x = cx + 52.0
        p.setPen(QPen(QColor(255, 255, 255, 220), 1.8, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawLine(QPointF(next_x + 6.0, cy - 6.0), QPointF(next_x + 6.0, cy + 6.0))
        tri_next = QPainterPath()
        tri_next.moveTo(next_x - 5.0, cy - 6.0)
        tri_next.lineTo(next_x + 4.5, cy)
        tri_next.lineTo(next_x - 5.0, cy + 6.0)
        tri_next.closeSubpath()
        p.setBrush(QColor(255, 255, 255, 220))
        p.drawPath(tri_next)

        # 4. 循环模式小图标
        mode_x = cx - 110.0
        p.setPen(QPen(QColor(255, 255, 255, 185), 1.4, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawRoundedRect(QRectF(mode_x - 7.0, cy - 5.0, 14.0, 10.0), 2.4, 2.4)
        if self.play_mode == "loop_one":
            p.setFont(get_nordic_font(8, QFont.Weight.Bold))
            p.setPen(QColor(255, 255, 255, 240))
            p.drawText(QRectF(mode_x - 5, cy - 5, 10, 10), int(Qt.AlignmentFlag.AlignCenter), "1")

        # 5. 音量状态小喇叭
        vol_x = cx + 110.0
        p.setPen(QPen(QColor(255, 255, 255, 185), 1.4, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        p.setBrush(QColor(255, 255, 255, 185))
        spk = QPainterPath()
        spk.moveTo(vol_x - 6.0, cy - 3.0)
        spk.lineTo(vol_x - 2.5, cy - 3.0)
        spk.lineTo(vol_x + 2.0, cy - 6.5)
        spk.lineTo(vol_x + 2.0, cy + 6.5)
        spk.lineTo(vol_x - 2.5, cy + 3.0)
        spk.lineTo(vol_x - 6.0, cy + 3.0)
        spk.closeSubpath()
        p.drawPath(spk)
        if self.volume > 0.05:
            p.setBrush(Qt.BrushStyle.NoBrush)
            p.drawArc(QRectF(vol_x + 1.0, cy - 5.0, 6.0, 10.0), -60 * 16, 120 * 16)


class ImmersiveFullscreenWindow(QWidget):
    """
    Apple Music 风格「全屏沉浸流光模式」视界 (Aurora Immersion 2.0)
    特性：
    1. 动态弥散流体极光：5 核流动渐变等离子体，随音乐声学能量跳动呼吸
    2. 星空浮游微粒：36 颗柔光粒子随极光慢速漫游
    3. 巨幅黑胶唱机：机械唱臂入轨动画、同心圆刻纹与 Ambilight 氛围溢彩
    4. 逐字流光歌词：42px 聚光灯居中排版、平滑弹簧动效与双语译文
    5. 3 秒静止智能隐藏光标与悬浮控制台
    """

    closed = pyqtSignal()
    playToggled = pyqtSignal()
    prevRequested = pyqtSignal()
    nextRequested = pyqtSignal()
    seekRatioRequested = pyqtSignal(float)
    seekTimeRequested = pyqtSignal(float)
    modeCycled = pyqtSignal()
    volumeRequested = pyqtSignal(float)
    translationToggled = pyqtSignal(bool)
    syllableToggled = pyqtSignal(bool)
    translationFetchRequested = pyqtSignal(object)

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(None)
        self.setWindowTitle("Klang — 极光沉浸视界")
        self.setWindowFlags(Qt.WindowType.Window | Qt.WindowType.FramelessWindowHint)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, False)
        self.setMouseTracking(True)

        self.setFont(get_nordic_font(14, QFont.Weight.Normal))
        self.current_track: Optional[NordicTrack] = None
        self.is_playing: bool = False
        self.play_mode: str = "loop_list"
        self.volume: float = 0.72
        self.syllable_mode: bool = True
        self.show_translation: bool = True

        # 5 阶动态极光流光色彩与色彩平滑过渡插值
        self._col1_cur: QColor = QColor("#3D7A63")
        self._col1_target: QColor = QColor("#3D7A63")
        self._col2_cur: QColor = QColor("#7D6B5C")
        self._col2_target: QColor = QColor("#7D6B5C")
        self._col3_cur: QColor = QColor("#BFA78A")
        self._col3_target: QColor = QColor("#BFA78A")
        self._col4_cur: QColor = QColor("#304A5E")
        self._col4_target: QColor = QColor("#304A5E")
        self._col_base_cur: QColor = QColor("#0A0D12")
        self._col_base_target: QColor = QColor("#0A0D12")

        self._audio_energy: float = 0.0
        self._aurora_buffer: Optional[QImage] = None
        self._clock = QElapsedTimer()
        self._clock.start()

        # 36 颗漂浮星芒柔光微粒 (Atmospheric Dust Motes)
        self._motes: List[FloatingMote] = []
        for _ in range(36):
            self._motes.append(
                FloatingMote(
                    x=random.uniform(0.0, 1.0),
                    y=random.uniform(0.0, 1.0),
                    r=random.uniform(1.6, 3.8),
                    base_alpha=random.uniform(0.20, 0.65),
                    speed_x=random.uniform(-0.012, 0.012),
                    speed_y=random.uniform(-0.020, -0.005),
                    phase=random.uniform(0.0, math.pi * 2.0),
                )
            )

        # 鼠标空闲 3 秒自动隐藏控制栏与光标
        self._hud_opacity: float = 1.0
        self._hud_target_opacity: float = 1.0
        self._idle_timer = QTimer(self)
        self._idle_timer.setSingleShot(True)
        self._idle_timer.setInterval(3000)
        self._idle_timer.timeout.connect(self._on_idle_timeout)

        # 屏幕刷新率驱动动画定时器 (60fps)
        self._render_timer = QTimer(self)
        self._render_timer.setInterval(16)
        self._render_timer.timeout.connect(self._on_animation_frame)

        self._setup_ui()

    def _setup_ui(self) -> None:
        root_vbox = QVBoxLayout(self)
        root_vbox.setContentsMargins(40, 26, 40, 30)
        root_vbox.setSpacing(0)

        # 1. 顶部悬浮工具条
        self.top_bar_widget = QWidget(self)
        self.top_bar_widget.setFixedHeight(48)
        top_hbox = QHBoxLayout(self.top_bar_widget)
        top_hbox.setContentsMargins(0, 0, 0, 0)
        top_hbox.setSpacing(12)

        self.lbl_title_badge = QLabel("✦  KLANG  ·  AURORA IMMERSION", self.top_bar_widget)
        self.lbl_title_badge.setFont(get_nordic_font(12, QFont.Weight.DemiBold, mono=True))
        self.lbl_title_badge.setStyleSheet("color: rgba(255, 255, 255, 0.76); padding-left: 6px; letter-spacing: 1px;")
        top_hbox.addWidget(self.lbl_title_badge)

        top_hbox.addStretch(1)

        # 译文开关
        self.btn_trans = QPushButton("译", self.top_bar_widget)
        self.btn_trans.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.btn_trans.setFixedSize(36, 30)
        self.btn_trans.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_trans.setToolTip("显示 / 隐藏歌词翻译")
        self.btn_trans.setStyleSheet(
            "QPushButton { background: rgba(255, 255, 255, 0.12); border: 1px solid rgba(255, 255, 255, 0.22); "
            "border-radius: 7px; color: #FFFFFF; font-weight: 600; font-size: 13px; font-family: 'SF Pro Display', 'PingFang SC', sans-serif; }"
            "QPushButton:hover { background: rgba(255, 255, 255, 0.25); }"
        )
        self.btn_trans.clicked.connect(self._on_toggle_translation)
        top_hbox.addWidget(self.btn_trans)

        # 逐字开关
        self.btn_syllable = QPushButton("逐字", self.top_bar_widget)
        self.btn_syllable.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.btn_syllable.setFixedSize(50, 30)
        self.btn_syllable.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_syllable.setToolTip("开启 / 关闭逐字卡拉OK光效")
        self.btn_syllable.setStyleSheet(
            "QPushButton { background: rgba(255, 255, 255, 0.12); border: 1px solid rgba(255, 255, 255, 0.22); "
            "border-radius: 7px; color: #FFFFFF; font-weight: 600; font-size: 13px; font-family: 'SF Pro Display', 'PingFang SC', sans-serif; }"
            "QPushButton:hover { background: rgba(255, 255, 255, 0.25); }"
        )
        self.btn_syllable.clicked.connect(self._on_toggle_syllable)
        top_hbox.addWidget(self.btn_syllable)

        # 封面样式切换 (纯净封面 / 伴生黑胶)
        self.btn_vinyl_mode = QPushButton("纯净封面", self.top_bar_widget)
        self.btn_vinyl_mode.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.btn_vinyl_mode.setFixedHeight(30)
        self.btn_vinyl_mode.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_vinyl_mode.setToolTip("切换封面模式：现代纯净大封面 / 伴生黑胶微显")
        self.btn_vinyl_mode.setStyleSheet(
            "QPushButton { background: rgba(255, 255, 255, 0.12); border: 1px solid rgba(255, 255, 255, 0.22); "
            "border-radius: 7px; color: #FFFFFF; font-weight: 600; font-size: 13px; padding: 0 10px; font-family: 'SF Pro Display', 'PingFang SC', sans-serif; }"
            "QPushButton:hover { background: rgba(255, 255, 255, 0.25); }"
        )
        self.btn_vinyl_mode.clicked.connect(self._on_toggle_cover_mode)
        top_hbox.addWidget(self.btn_vinyl_mode)

        # 退出全屏按钮
        self.btn_exit = QPushButton("退出全屏 (Esc)", self.top_bar_widget)
        self.btn_exit.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.btn_exit.setFixedHeight(30)
        self.btn_exit.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_exit.setToolTip("退出全屏沉浸视界 (Esc / F11)")
        self.btn_exit.setStyleSheet(
            "QPushButton { background: rgba(255, 255, 255, 0.16); border: 1px solid rgba(255, 255, 255, 0.28); "
            "border-radius: 7px; color: #FFFFFF; font-weight: 600; font-size: 13px; padding: 0 14px; font-family: 'SF Pro Display', 'PingFang SC', sans-serif; }"
            "QPushButton:hover { background: rgba(255, 255, 255, 0.32); }"
        )
        self.btn_exit.clicked.connect(self.exit_immersive)
        top_hbox.addWidget(self.btn_exit)

        root_vbox.addWidget(self.top_bar_widget)

        # 2. 中间舞台左右分栏 (左黑胶唱机与封面，右大字滚动流光歌词)
        stage_split = QWidget(self)
        split_hbox = QHBoxLayout(stage_split)
        split_hbox.setContentsMargins(0, 0, 0, 0)
        split_hbox.setSpacing(36)

        self.artwork_view = ImmersiveArtworkView(stage_split)
        self.artwork_view.playToggled.connect(self.playToggled.emit)
        self.artwork_view.prevClicked.connect(self.prevRequested.emit)
        self.artwork_view.nextClicked.connect(self.nextRequested.emit)
        self.artwork_view.seekRatioRequested.connect(self.seekRatioRequested.emit)
        self.artwork_view.modeCycled.connect(self.modeCycled.emit)
        self.artwork_view.volumeChanged.connect(self.volumeRequested.emit)
        split_hbox.addWidget(self.artwork_view, 45)

        self.lyrics_view = ImmersiveLyricsView(stage_split)
        self.lyrics_view.seekRequested.connect(self.seekTimeRequested.emit)
        split_hbox.addWidget(self.lyrics_view, 55)

        root_vbox.addWidget(stage_split, 1)

        self._top_opacity_eff = QGraphicsOpacityEffect(self.top_bar_widget)
        self.top_bar_widget.setGraphicsEffect(self._top_opacity_eff)

    def set_show_translation(self, show: bool) -> None:
        self.show_translation = bool(show)
        self.btn_trans.setStyleSheet(
            f"QPushButton {{ background: {'rgba(255, 255, 255, 0.32)' if self.show_translation else 'rgba(255, 255, 255, 0.10)'}; "
            "border: 1px solid rgba(255, 255, 255, 0.25); border-radius: 7px; color: #FFFFFF; font-weight: 600; font-size: 13px; }"
        )
        self.lyrics_view.set_show_translation(show)

    def set_syllable_mode(self, enabled: bool) -> None:
        self.syllable_mode = bool(enabled)
        self.btn_syllable.setStyleSheet(
            f"QPushButton {{ background: {'rgba(255, 255, 255, 0.32)' if self.syllable_mode else 'rgba(255, 255, 255, 0.10)'}; "
            "border: 1px solid rgba(255, 255, 255, 0.25); border-radius: 7px; color: #FFFFFF; font-weight: 600; font-size: 13px; }"
        )
        self.lyrics_view.set_syllable_mode(enabled)

    def _on_toggle_translation(self) -> None:
        self.show_translation = not self.show_translation
        self.btn_trans.setStyleSheet(
            f"QPushButton {{ background: {'rgba(255, 255, 255, 0.32)' if self.show_translation else 'rgba(255, 255, 255, 0.10)'}; "
            "border: 1px solid rgba(255, 255, 255, 0.25); border-radius: 7px; color: #FFFFFF; font-weight: 600; font-size: 13px; }"
        )
        self.lyrics_view.show_translation = self.show_translation
        self.lyrics_view._recompute_layout()
        self.lyrics_view.update()
        self.translationToggled.emit(self.show_translation)
        if self.show_translation and self.current_track and self.current_track.lyrics:
            if not any(l.sub for l in self.current_track.lyrics):
                self.translationFetchRequested.emit(self.current_track)

    def _on_toggle_syllable(self) -> None:
        self.syllable_mode = not self.syllable_mode
        self.btn_syllable.setStyleSheet(
            f"QPushButton {{ background: {'rgba(255, 255, 255, 0.32)' if self.syllable_mode else 'rgba(255, 255, 255, 0.10)'}; "
            "border: 1px solid rgba(255, 255, 255, 0.25); border-radius: 7px; color: #FFFFFF; font-weight: 600; font-size: 13px; }"
        )
        self.lyrics_view.syllable_mode = self.syllable_mode
        self.lyrics_view.update()
        self.syllableToggled.emit(self.syllable_mode)

    def _on_toggle_cover_mode(self) -> None:
        new_state = not self.artwork_view.show_vinyl
        self.artwork_view.set_vinyl_mode(new_state)
        label = "伴生黑胶" if new_state else "纯净封面"
        self.btn_vinyl_mode.setText(label)
        self.btn_vinyl_mode.setStyleSheet(
            f"QPushButton {{ background: {'rgba(255, 255, 255, 0.32)' if new_state else 'rgba(255, 255, 255, 0.12)'}; "
            "border: 1px solid rgba(255, 255, 255, 0.25); border-radius: 7px; color: #FFFFFF; font-weight: 600; font-size: 13px; padding: 0 10px; }"
            "QPushButton:hover {{ background: rgba(255, 255, 255, 0.36); }}"
        )

    def enter_immersive(
        self,
        current_track: Optional[NordicTrack],
        is_playing: bool,
        current_time: float,
        duration: float,
        play_mode: str,
        volume: float,
        target_screen: Optional[QScreen] = None,
        show_translation: bool = True,
        syllable_mode: bool = True,
        dolby_atmos_active: bool = True,
    ) -> None:
        if target_screen is not None:
            self.setScreen(target_screen)
            geo = target_screen.geometry()
            self.move(geo.topLeft())
            self.resize(geo.size())

        self.set_show_translation(show_translation)
        self.set_syllable_mode(syllable_mode)
        self.set_dolby_atmos_active(dolby_atmos_active)
        self.update_track(current_track)
        self.update_play_state(is_playing)
        self.update_time(current_time, duration)
        self.update_play_mode(play_mode)
        self.update_volume(volume)

        self.showFullScreen()
        self.raise_()
        self.activateWindow()

        self._hud_opacity = 1.0
        self._hud_target_opacity = 1.0
        self._top_opacity_eff.setOpacity(1.0)
        self.artwork_view.controls_opacity = 1.0
        self.unsetCursor()
        self._idle_timer.start(3000)

        if not self._render_timer.isActive():
            self._render_timer.start()

    def set_dolby_atmos_active(self, active: bool, actual_channels: int = 2) -> None:
        self.artwork_view.dolby_atmos_active = bool(active)
        self.artwork_view.actual_channels = int(actual_channels)
        self.artwork_view.update()

    def exit_immersive(self) -> None:
        self._render_timer.stop()
        self._idle_timer.stop()
        self.unsetCursor()
        self.hide()
        self.closed.emit()

    def update_track(self, track: Optional[NordicTrack]) -> None:
        self.current_track = track
        self.artwork_view.set_track(track)
        if track:
            pal = track.palette or {}
            self.lyrics_view.accent_color = QColor(pal.get("accent", "#5B9E83"))
            self.lyrics_view.set_lyrics(track.lyrics, offset=track.lyric_offset)
            c1, c2, c3, c4, c_base = extract_aurora_palette(track.get_cover_pixmap(), track)
            self._col1_target = c1
            self._col2_target = c2
            self._col3_target = c3
            self._col4_target = c4
            self._col_base_target = c_base
        else:
            self.lyrics_view.set_lyrics([], offset=0.0)

    def update_play_state(self, is_playing: bool) -> None:
        self.is_playing = is_playing
        self.artwork_view.set_playing_state(is_playing)

    def update_time(self, current_time: float, duration: float) -> None:
        self.artwork_view.update_time(current_time, duration)
        self.lyrics_view.sync_time(current_time)

    def update_spectrum(self, bands: List[float]) -> None:
        if bands:
            low_energy = sum(bands[:4]) / 4.0
            self._audio_energy += (low_energy - self._audio_energy) * 0.22
            self.artwork_view.audio_energy = self._audio_energy

    def update_play_mode(self, mode: str) -> None:
        self.play_mode = mode
        self.artwork_view.set_play_mode(mode)

    def update_volume(self, vol: float) -> None:
        self.volume = vol
        self.artwork_view.set_volume(vol)

    def _on_idle_timeout(self) -> None:
        if not (self.artwork_view.is_dragging_progress or self.artwork_view.is_dragging_volume):
            self._hud_target_opacity = 0.0
            self.setCursor(Qt.CursorShape.BlankCursor)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if self._hud_target_opacity < 0.05:
            self.unsetCursor()
        self._hud_target_opacity = 1.0
        self._idle_timer.start(3000)
        super().mouseMoveEvent(event)

    def keyPressEvent(self, event: QKeyEvent) -> None:
        key = event.key()
        if key in (Qt.Key.Key_Escape, Qt.Key.Key_F11):
            self.exit_immersive()
            event.accept()
            return
        if key == Qt.Key.Key_Space:
            self.playToggled.emit()
            event.accept()
            return
        if key == Qt.Key.Key_Left:
            target = max(0.0, self.control_dock.current_time - 5.0)
            self.seekTimeRequested.emit(target)
            event.accept()
            return
        if key == Qt.Key.Key_Right:
            target = min(self.control_dock.duration, self.control_dock.current_time + 5.0)
            self.seekTimeRequested.emit(target)
            event.accept()
            return
        if key == Qt.Key.Key_Up:
            self.volumeRequested.emit(min(1.0, self.volume + 0.05))
            event.accept()
            return
        if key == Qt.Key.Key_Down:
            self.volumeRequested.emit(max(0.0, self.volume - 0.05))
            event.accept()
            return
        super().keyPressEvent(event)

    def _interp_color(self, c_cur: QColor, c_tar: QColor, factor: float) -> QColor:
        r = int(c_cur.red() + (c_tar.red() - c_cur.red()) * factor)
        g = int(c_cur.green() + (c_tar.green() - c_cur.green()) * factor)
        b = int(c_cur.blue() + (c_tar.blue() - c_cur.blue()) * factor)
        return QColor(r, g, b)

    def _on_animation_frame(self) -> None:
        dt = 0.016
        # 色彩渐变插值平滑推进
        self._col1_cur = self._interp_color(self._col1_cur, self._col1_target, 0.05)
        self._col2_cur = self._interp_color(self._col2_cur, self._col2_target, 0.05)
        self._col3_cur = self._interp_color(self._col3_cur, self._col3_target, 0.05)
        self._col4_cur = self._interp_color(self._col4_cur, self._col4_target, 0.05)
        self._col_base_cur = self._interp_color(self._col_base_cur, self._col_base_target, 0.05)

        # 漂浮星芒柔光微粒物理移动
        for m in self._motes:
            m.x = (m.x + m.speed_x * dt) % 1.0
            m.y = (m.y + m.speed_y * dt) % 1.0

        # 唱片与歌词阻尼物理推进 (60 FPS 顺滑刷新)
        self.artwork_view.step_physics(dt)
        self.lyrics_view.step_physics(dt)
        self.artwork_view.update()
        self.lyrics_view.update()

        # 悬浮 HUD 与封面下置控制栏透明度平滑过渡
        if abs(self._hud_target_opacity - self._hud_opacity) > 0.01:
            self._hud_opacity += (self._hud_target_opacity - self._hud_opacity) * 0.14
            self._top_opacity_eff.setOpacity(self._hud_opacity)
        self.artwork_view.controls_opacity = self._hud_opacity

        self.update()

    def _render_aurora_buffer(self, bw: int, bh: int) -> None:
        """5 核流动等离子体极光流体动力学渲染"""
        if self._aurora_buffer is None or self._aurora_buffer.width() != bw or self._aurora_buffer.height() != bh:
            self._aurora_buffer = QImage(bw, bh, QImage.Format.Format_RGB32)

        p = QPainter(self._aurora_buffer)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.fillRect(0, 0, bw, bh, self._col_base_cur)

        t = self._clock.elapsed() / 1000.0
        energy_boost = self._audio_energy * 0.22

        # 1号极光核 (左上方有机流动)
        c1 = QColor(self._col1_cur)
        c1.setAlpha(185)
        cx1 = bw * (0.26 + 0.16 * math.sin(t * 0.23))
        cy1 = bh * (0.30 + 0.14 * math.cos(t * 0.19))
        r1 = min(bw, bh) * (0.68 + 0.08 * math.sin(t * 0.31) + energy_boost)
        g1 = QRadialGradient(QPointF(cx1, cy1), r1)
        g1.setColorAt(0.0, c1)
        g1.setColorAt(0.65, QColor(c1.red(), c1.green(), c1.blue(), 60))
        g1.setColorAt(1.0, Qt.GlobalColor.transparent)
        p.fillRect(0, 0, bw, bh, g1)

        # 2号极光核 (右下方弥散)
        c2 = QColor(self._col2_cur)
        c2.setAlpha(170)
        cx2 = bw * (0.78 + 0.14 * math.cos(t * 0.21))
        cy2 = bh * (0.68 + 0.15 * math.sin(t * 0.27))
        r2 = min(bw, bh) * (0.62 + 0.06 * math.cos(t * 0.25) + energy_boost)
        g2 = QRadialGradient(QPointF(cx2, cy2), r2)
        g2.setColorAt(0.0, c2)
        g2.setColorAt(0.60, QColor(c2.red(), c2.green(), c2.blue(), 50))
        g2.setColorAt(1.0, Qt.GlobalColor.transparent)
        p.fillRect(0, 0, bw, bh, g2)

        # 3号极光核 (左侧封面Ambilight背景呼应核)
        c3 = QColor(self._col3_cur)
        c3.setAlpha(155)
        cx3 = bw * (0.38 + 0.15 * math.sin(t * 0.17 + 1.2))
        cy3 = bh * (0.55 + 0.12 * math.cos(t * 0.29 + 0.7))
        r3 = min(bw, bh) * (0.54 + 0.05 * math.sin(t * 0.38))
        g3 = QRadialGradient(QPointF(cx3, cy3), r3)
        g3.setColorAt(0.0, c3)
        g3.setColorAt(0.55, QColor(c3.red(), c3.green(), c3.blue(), 35))
        g3.setColorAt(1.0, Qt.GlobalColor.transparent)
        p.fillRect(0, 0, bw, bh, g3)

        # 4号极光核 (歌词侧深色互补冷调光核)
        c4 = QColor(self._col4_cur)
        c4.setAlpha(145)
        cx4 = bw * (0.65 + 0.18 * math.cos(t * 0.15 + 2.0))
        cy4 = bh * (0.35 + 0.16 * math.sin(t * 0.22 + 1.5))
        r4 = min(bw, bh) * (0.50 + 0.06 * math.cos(t * 0.33))
        g4 = QRadialGradient(QPointF(cx4, cy4), r4)
        g4.setColorAt(0.0, c4)
        g4.setColorAt(0.60, QColor(c4.red(), c4.green(), c4.blue(), 30))
        g4.setColorAt(1.0, Qt.GlobalColor.transparent)
        p.fillRect(0, 0, bw, bh, g4)

        p.end()

    def paintEvent(self, event) -> None:
        w, h = self.width(), self.height()
        if w < 10 or h < 10:
            return

        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)

        # 1. 绘制极光超平滑低开销模糊缓冲 (480x270 硬件平滑插值放大)
        buf_w = 480
        buf_h = 270
        self._render_aurora_buffer(buf_w, buf_h)
        if self._aurora_buffer is not None:
            p.drawImage(self.rect(), self._aurora_buffer)

        # 2. 绘制漂浮星芒柔光微粒 (Atmospheric Dust Particles)
        t = self._clock.elapsed() / 1000.0
        p.setPen(Qt.PenStyle.NoPen)
        for m in self._motes:
            px = m.x * w
            py = m.y * h
            pulse = 0.5 + 0.5 * math.sin(t * 1.8 + m.phase)
            alpha = int(round(m.base_alpha * pulse * 255))
            if alpha > 15:
                p.setBrush(QColor(255, 255, 255, alpha))
                p.drawEllipse(QPointF(px, py), m.r, m.r)

        # 3. Apple Music 式午夜天鹅绒深色微暗降滤镜 (确保前景大字歌词最高对比度与柔光感)
        vignette = QRadialGradient(QPointF(w * 0.5, h * 0.5), max(w, h) * 0.75)
        vignette.setColorAt(0.0, QColor(8, 12, 16, 70))
        vignette.setColorAt(0.60, QColor(8, 12, 16, 120))
        vignette.setColorAt(1.0, QColor(5, 7, 10, 195))
        p.fillRect(self.rect(), vignette)
