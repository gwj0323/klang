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
    QDateTime,
    QEvent,
    pyqtSignal,
)
from PyQt6.QtGui import (
    QShowEvent,
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
    QResizeEvent,
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
from qt6_player.components.fluid_shader import FluidMeshShaderWidget


def extract_aurora_palette(pixmap: Optional[QPixmap], track: Optional[NordicTrack]) -> Tuple[QColor, QColor, QColor, QColor, QColor]:
    """从封面中提取用于 Apple Music 全景流体极光的高动态 5 阶调色盘：主极光、副极光、暖阳流金、深海极光、午夜黑曜底色"""
    if pixmap is not None and not pixmap.isNull():
        try:
            small = pixmap.scaled(24, 24, Qt.AspectRatioMode.IgnoreAspectRatio, Qt.TransformationMode.FastTransformation)
            img = small.toImage().convertToFormat(QImage.Format.Format_RGB32)
            c1_r = c1_g = c1_b = c1_cnt = 0
            c2_r = c2_g = c2_b = c2_cnt = 0
            c3_r = c3_g = c3_b = c3_cnt = 0
            c4_r = c4_g = c4_b = c4_cnt = 0
            all_r = all_g = all_b = all_cnt = 0
            for y in range(24):
                for x in range(24):
                    c = img.pixelColor(x, y)
                    r, g, b = c.red(), c.green(), c.blue()
                    all_r += r; all_g += g; all_b += b; all_cnt += 1
                    if x < 12 and y < 12:
                        c1_r += r; c1_g += g; c1_b += b; c1_cnt += 1
                    elif x >= 12 and y < 12:
                        c2_r += r; c2_g += g; c2_b += b; c2_cnt += 1
                    elif x < 12 and y >= 12:
                        c3_r += r; c3_g += g; c3_b += b; c3_cnt += 1
                    else:
                        c4_r += r; c4_g += g; c4_b += b; c4_cnt += 1

            if c1_cnt > 0 and c2_cnt > 0 and c3_cnt > 0 and c4_cnt > 0:
                raw_c1 = QColor(c1_r // c1_cnt, c1_g // c1_cnt, c1_b // c1_cnt)
                raw_c2 = QColor(c2_r // c2_cnt, c2_g // c2_cnt, c2_b // c2_cnt)
                raw_c3 = QColor(c3_r // c3_cnt, c3_g // c3_cnt, c3_b // c3_cnt)
                raw_c4 = QColor(c4_r // c4_cnt, c4_g // c4_cnt, c4_b // c4_cnt)

                # 全局主色调与饱和度分析
                dom_color = QColor(all_r // all_cnt, all_g // all_cnt, all_b // all_cnt)
                h_dom, s_dom, l_dom, _ = dom_color.getHslF()

                h1, s1, l1, _ = raw_c1.getHslF()
                h2, s2, l2, _ = raw_c2.getHslF()
                h3, s3, l3, _ = raw_c3.getHslF()
                h4, s4, l4, _ = raw_c4.getHslF()

                hue_diff = max(abs(h1 - h2), abs(h1 - h3), abs(h1 - h4), abs(h2 - h3), abs(h2 - h4), abs(h3 - h4))

                if hue_diff < 0.15 or s_dom > 0.40:
                    # 单主色调主导的专辑封面（如纯红、纯橙、高饱和单色封面）：
                    # 采用 Apple Music 风格色彩扩展体系，构建高动态珠宝色系（主极光红、暖金、雅玫、深夜幽蓝），拒绝混浊成泥
                    col1 = QColor()
                    col1.setHslF(max(0.0, h_dom), min(0.95, max(0.70, s_dom * 1.35)), 0.50)

                    col2 = QColor()
                    col2.setHslF((h_dom + 0.08) % 1.0, min(0.92, max(0.65, s_dom * 1.25)), 0.54)

                    col3 = QColor()
                    col3.setHslF((h_dom - 0.07 + 1.0) % 1.0, min(0.88, max(0.60, s_dom * 1.20)), 0.46)

                    col4 = QColor()
                    col4.setHslF((h_dom + 0.55) % 1.0, 0.40, 0.28)
                else:
                    # 封面本身色彩丰富，各象限保留自身色相并提升通透度与饱和度
                    col1 = QColor()
                    col1.setHslF(max(0.0, h1), min(0.92, max(0.50, s1 * 1.45)), min(0.64, max(0.42, l1 * 1.20)))
                    col2 = QColor()
                    col2.setHslF(max(0.0, h2), min(0.88, max(0.45, s2 * 1.35)), min(0.58, max(0.38, l2 * 1.15)))
                    col3 = QColor()
                    col3.setHslF(max(0.0, h3), min(0.85, max(0.40, s3 * 1.30)), min(0.68, max(0.44, l3 * 1.25)))
                    col4 = QColor()
                    col4.setHslF(max(0.0, h4), min(0.80, max(0.38, s4 * 1.25)), min(0.52, max(0.32, l4 * 1.10)))

                # 纯净午夜黑曜岩底色（纯正深黑，保证暗部沉稳清澈）
                c_base = QColor(8, 10, 16)
                return col1, col2, col3, col4, c_base
        except Exception:
            pass

    pal = (track.palette if track else None) or {}
    c1 = QColor(pal.get("accent", "#3D7A63"))
    c2 = QColor(pal.get("wood", "#7D6B5C"))
    c3 = QColor(pal.get("highlight", "#BFA78A"))
    c4 = QColor("#223348")
    c_base = QColor(8, 10, 16)
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
    seekRatioScrubbing = pyqtSignal(float)
    modeCycled = pyqtSignal()
    volumeChanged = pyqtSignal(float)

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.track: Optional[NordicTrack] = None
        self.cover_pixmap: Optional[QPixmap] = None
        self.is_playing: bool = False
        self.vinyl_mode: str = "pure"  # "none" | "pure" (纯净黑胶无唱臂) | "arm" (极简现代唱臂)
        self.vinyl_angle: float = 0.0
        self.angular_vel: float = 0.0
        self.slide_prog: float = 0.0
        self.audio_energy: float = 0.0
        self.accent_color: QColor = QColor("#5B9E83")
        self._cached_disc_key: Optional[str] = None
        self._cached_disc_pixmap: Optional[QPixmap] = None
        self._cached_sleeve_key: Optional[str] = None
        self._cached_sleeve_pixmap: Optional[QPixmap] = None

        # 预缓存字体与度量，避免 120 FPS paintEvent 中重复构造 QFont
        self._font_badge = get_nordic_font(8, QFont.Weight.Bold, mono=True)
        self._fm_badge = QFontMetricsF(self._font_badge)
        self._font_title = get_nordic_font(22, QFont.Weight.Bold)
        self._fm_title = QFontMetricsF(self._font_title)
        self._font_sub = get_nordic_font(14, QFont.Weight.Medium)
        self._fm_sub = QFontMetricsF(self._font_sub)
        self._font_time = get_nordic_font(11, QFont.Weight.Medium, mono=True)
        self._font_loop1 = get_nordic_font(8, QFont.Weight.Bold)

        # 现代极简线性唱臂系统 (仅在 vinyl_mode == "arm" 时生效)
        self.tonearm_angle: float = -16.0        # 当前旋转角 (deg): -16° 为停靠外侧, 0°~+8° 为音轨
        self.tonearm_target_angle: float = -16.0
        self.tonearm_lift: float = 1.0           # 抬升: 1.0 为悬空抬起, 0.0 为落针搭在唱片上
        self.tonearm_target_lift: float = 1.0

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

    @property
    def show_vinyl(self) -> bool:
        return self.vinyl_mode in ("pure", "arm", "vinyl_pure", "vinyl_arm")

    @property
    def show_tonearm(self) -> bool:
        return self.vinyl_mode in ("arm", "vinyl_arm")

    def set_vinyl_mode(self, mode) -> None:
        if isinstance(mode, bool):
            self.vinyl_mode = "pure" if mode else "none"
        elif mode in ("none", "pure", "arm"):
            self.vinyl_mode = mode
        elif mode == "vinyl_pure":
            self.vinyl_mode = "pure"
        elif mode == "vinyl_arm":
            self.vinyl_mode = "arm"
        else:
            self.vinyl_mode = "pure" if bool(mode) else "none"
        self.update()

    def set_track(self, track: Optional[NordicTrack]) -> None:
        self.track = track
        self.cover_pixmap = None
        self._cached_disc_pixmap = None
        self._cached_disc_key = None
        self._cached_sleeve_pixmap = None
        self._cached_sleeve_key = None
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
        self.duration = max(1.0, dur)
        cur_time = max(0.0, cur_time)
        if self.is_dragging_progress:
            return
        if not self.is_playing or abs(cur_time - self.current_time) > 0.12:
            self.current_time = cur_time
            if not self.is_playing and not self.is_dragging_progress:
                self.update()
        else:
            # 播放中通过微阻尼与音频时钟对齐，避免帧间阶跃与错相重绘
            self.current_time += (cur_time - self.current_time) * 0.25

    def step_physics(self, dt: float) -> None:
        dt = max(0.001, min(0.05, dt))
        if self.is_playing and not self.is_dragging_progress:
            self.current_time = min(self.duration, self.current_time + dt)
        # 1. 黑胶盘旋转角速度平滑推进与惯性衰减
        target_vel = 36.0 if (self.is_playing and self.show_vinyl) else 0.0
        self.angular_vel += (target_vel - self.angular_vel) * min(1.0, dt * 3.6)
        if abs(self.angular_vel) > 0.05:
            self.vinyl_angle = (self.vinyl_angle + self.angular_vel * dt) % 360.0

        # 2. 伴生黑胶滑出露出阻尼 (开启黑胶模式时露出，关闭时收回)
        target_slide = 1.0 if self.show_vinyl else 0.0
        slide_rate = min(1.0, 1.0 - math.exp(-6.5 * dt))
        self.slide_prog += (target_slide - self.slide_prog) * slide_rate

        # 3. 极简现代线性唱臂落针物理系统 (仅在 show_tonearm 开启时运作)
        if self.show_tonearm and self.show_vinyl and self.slide_prog > 0.20:
            if self.is_playing:
                # 播放时：根据播放进度在右侧暴露音轨上微移 (0° ~ 8°)
                progress = max(0.0, min(1.0, self.current_time / max(1.0, self.duration)))
                self.tonearm_target_angle = 0.0 + progress * 8.0

                # 摆到位后轻柔落针
                if abs(self.tonearm_angle - self.tonearm_target_angle) < 5.0:
                    self.tonearm_target_lift = 0.0
                else:
                    self.tonearm_target_lift = 1.0
            else:
                # 暂停时：抬起并旋回外侧停靠位 (-16°)
                self.tonearm_target_lift = 1.0
                self.tonearm_target_angle = -16.0
        else:
            self.tonearm_target_lift = 1.0
            self.tonearm_target_angle = -16.0

        # 唱臂平滑阻尼
        arm_rate = min(1.0, dt * 4.2)
        self.tonearm_angle += (self.tonearm_target_angle - self.tonearm_angle) * arm_rate

        lift_speed = 3.6 if self.tonearm_target_lift == 0.0 else 6.5
        lift_rate = min(1.0, dt * lift_speed)
        self.tonearm_lift += (self.tonearm_target_lift - self.tonearm_lift) * lift_rate

    def _draw_tonearm(self, painter: QPainter, pivot_x: float, pivot_y: float, r_disc: float) -> None:
        """
        绘制现代极简 (Braun / B&O 风格) 纤薄线性唱臂与落针动效。
        特点：
        - 位于唱片右上方外侧，落针在暴露的右侧音轨 (2 点钟方向)，绝不遮挡唱片中心封面与护封；
        - 去除一切多余拟物杂质 (无怪异黑孔眼、无笨重配重砣、无支架叉)；
        - 极细 1.6px 钛金直臂杆，精微平齐枢轴，极简黑色微型唱头与翡翠绿指示线；
        - 播放时温柔平缓落针，暂停时轻盈抬起。
        """
        painter.save()
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)

        arm_len = r_disc * 0.60
        cur_angle = self.tonearm_angle
        lift_prog = self.tonearm_lift

        # 1. 动态接触柔和阴影 (随抬针高度产生真实景深)
        sh_dx = 3.0 + 7.0 * lift_prog
        sh_dy = 3.0 + 8.0 * lift_prog
        sh_alpha = int(round(35 * (1.0 - 0.45 * lift_prog)))

        painter.save()
        painter.translate(pivot_x + sh_dx, pivot_y + sh_dy)
        painter.rotate(cur_angle)
        painter.setPen(QPen(QColor(0, 0, 0, sh_alpha), 2.4, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        painter.drawLine(QPointF(0, 0), QPointF(0, arm_len))
        painter.fillRect(QRectF(-3.0, arm_len, 6.0, 13.0), QColor(0, 0, 0, sh_alpha))
        painter.restore()

        # 2. 唱臂主体旋转系统 (围绕 pivot 旋转)
        painter.save()
        painter.translate(pivot_x, pivot_y)
        painter.rotate(cur_angle)

        # A. 极简纤细钛金直臂杆 (1.6px)
        arm_col = QColor(220, 226, 234)
        arm_pen = QPen(arm_col, 1.6, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap)
        painter.setPen(arm_pen)
        painter.drawLine(QPointF(0, 0), QPointF(0, arm_len))

        # 后端精微平齐短杆
        rear_pen = QPen(QColor(130, 138, 148), 2.2, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap)
        painter.setPen(rear_pen)
        painter.drawLine(QPointF(0, 0), QPointF(0, -6.5))

        # B. 极简现代微型唱头 (Cartridge)
        cart_w, cart_h = 5.6, 13.0
        cart_rect = QRectF(-cart_w / 2.0, arm_len - 1.0, cart_w, cart_h)
        cart_path = QPainterPath()
        cart_path.addRoundedRect(cart_rect, 1.5, 1.5)
        painter.fillPath(cart_path, QColor(22, 24, 27))
        painter.setPen(QPen(QColor(60, 66, 74), 0.8))
        painter.drawPath(cart_path)

        # 标志性翡翠绿 / 强调色微细指示条
        stripe_rect = QRectF(-cart_w / 2.0 + 0.8, arm_len + 4.0, cart_w - 1.6, 1.6)
        painter.fillRect(stripe_rect, self.accent_color)

        # 针尖微点 (Diamond Stylus Tip)
        stylus_tip = QPointF(0.0, arm_len + cart_h)
        painter.setPen(QPen(QColor(230, 235, 240), 1.0))
        painter.drawPoint(stylus_tip)

        # 落针着地微光
        if lift_prog < 0.10 and self.is_playing:
            glow = QRadialGradient(stylus_tip, 3.5)
            glow.setColorAt(0.0, QColor(255, 255, 255, 220))
            glow.setColorAt(0.5, QColor(self.accent_color.red(), self.accent_color.green(), self.accent_color.blue(), 120))
            glow.setColorAt(1.0, Qt.GlobalColor.transparent)
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(glow)
            painter.drawEllipse(stylus_tip, 3.5, 3.5)

        painter.restore()

        # 3. 平齐现代极简轴承底座 (Flush Minimalist Gimbal Pivot)
        base_r = 7.0
        base_grad = QRadialGradient(QPointF(pivot_x - 1.5, pivot_y - 1.5), base_r * 1.2)
        base_grad.setColorAt(0.0, QColor(64, 70, 78))
        base_grad.setColorAt(0.7, QColor(32, 36, 40))
        base_grad.setColorAt(1.0, QColor(18, 20, 23))

        painter.setPen(QPen(QColor(70, 78, 88), 1.0))
        painter.setBrush(base_grad)
        painter.drawEllipse(QPointF(pivot_x, pivot_y), base_r, base_r)

        # 中心车削精铝同心圆点
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(QColor(220, 226, 232))
        painter.drawEllipse(QPointF(pivot_x, pivot_y), 2.0, 2.0)

        painter.restore()

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

        # 1. 拟真黑曜石黑胶盘体 (带微弱径向漫反射深度)
        disc_path = QPainterPath()
        disc_path.addEllipse(QPointF(cx, cy), r_outer, r_outer)

        disc_grad = QRadialGradient(QPointF(cx, cy), r_outer)
        disc_grad.setColorAt(0.0, QColor(24, 26, 30))
        disc_grad.setColorAt(0.35, QColor(16, 17, 20))
        disc_grad.setColorAt(0.85, QColor(10, 11, 13))
        disc_grad.setColorAt(1.0, QColor(5, 6, 7))
        p.fillPath(disc_path, disc_grad)

        # 2. 外缘圆润精抛光倒角 (Lead-in rim)
        p.setPen(QPen(QColor(255, 255, 255, 28), 1.2))
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawPath(disc_path)

        p.setPen(QPen(QColor(0, 0, 0, 120), 1.0))
        p.drawEllipse(QPointF(cx, cy), r_outer - 1.5, r_outer - 1.5)

        # 3. 极细腻多音轨微刻纹 (Micro-Groove Acoustic Bands)
        r_label = r_outer * 0.36
        r_groove_start = r_label + (r_outer - r_label) * 0.08
        r_groove_end = r_outer * 0.96
        total_groove_width = r_groove_end - r_groove_start

        track_splits = [0.0, 0.26, 0.52, 0.78, 1.0]
        for t_idx in range(len(track_splits) - 1):
            band_r_start = r_groove_start + total_groove_width * track_splits[t_idx]
            band_r_end = r_groove_start + total_groove_width * track_splits[t_idx + 1] - 3.0

            lines_in_band = 7
            for li in range(lines_in_band):
                sub_r = band_r_start + (band_r_end - band_r_start) * (li / float(lines_in_band - 1))
                alpha = 10 if (li % 2 == 0) else 6
                p.setPen(QPen(QColor(255, 255, 255, alpha), 0.7))
                p.drawEllipse(QPointF(cx, cy), sub_r, sub_r)

            gap_r = band_r_end + 1.5
            p.setPen(QPen(QColor(0, 0, 0, 90), 1.2))
            p.drawEllipse(QPointF(cx, cy), gap_r, gap_r)

        # 4. 唱片中央圆形贴纸与内圈微凹倒角
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
            inner_shadow.setColorAt(0.72, Qt.GlobalColor.transparent)
            inner_shadow.setColorAt(0.96, QColor(0, 0, 0, 110))
            inner_shadow.setColorAt(1.0, QColor(0, 0, 0, 180))
            p.fillPath(label_path, inner_shadow)
        else:
            p.fillPath(label_path, QColor(36, 42, 48))
        p.restore()

        p.setPen(QPen(QColor(0, 0, 0, 140), 1.2))
        p.setBrush(Qt.BrushStyle.NoBrush)
        p.drawPath(label_path)
        p.setPen(QPen(QColor(255, 255, 255, 24), 0.8))
        p.drawEllipse(QPointF(cx, cy), r_label + 1.0, r_label + 1.0)

        # 5. 金属中心主轴孔 (精车镀铬黄铜轴芯)
        r_hole = r_outer * 0.050
        spindle_grad = QRadialGradient(QPointF(cx - 1.0, cy - 1.0), r_hole * 1.4)
        spindle_grad.setColorAt(0.0, QColor(255, 248, 220))
        spindle_grad.setColorAt(0.6, QColor(190, 175, 140))
        spindle_grad.setColorAt(1.0, QColor(120, 105, 75))
        p.setPen(QPen(spindle_grad, 1.2))
        p.setBrush(QColor(6, 7, 8))
        p.drawEllipse(QPointF(cx, cy), r_hole, r_hole)

        p.end()
        return pix

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.RightButton:
            from PyQt6.QtWidgets import QMenu
            menu = QMenu(self)
            menu.setStyleSheet(
                "QMenu { background: rgba(28, 32, 36, 0.96); border: 1px solid rgba(255, 255, 255, 0.22); "
                "border-radius: 8px; padding: 4px; color: #FFFFFF; font-size: 13px; }"
                "QMenu::item { padding: 6px 16px; border-radius: 4px; }"
                "QMenu::item:selected { background: rgba(255, 255, 255, 0.18); }"
            )
            act_poster = menu.addAction("生成歌词海报卡片...")
            selected = menu.exec(event.globalPosition().toPoint())
            if selected == act_poster:
                p_win = self.window()
                if hasattr(p_win, "posterRequested"):
                    p_win.posterRequested.emit()
            event.accept()
            return

        if event.button() == Qt.MouseButton.LeftButton:
            pos = event.position()
            # 1. 进度条点击与拖拽 (拖拽过程仅平滑刷新界面与预览，松开鼠标时才提交 seek，彻底解决卡顿与破音)
            if self._rect_slider.adjusted(-8.0, -8.0, 8.0, 8.0).contains(pos):
                self.is_dragging_progress = True
                try:
                    self.grabMouse()
                except Exception:
                    pass
                ratio = max(0.0, min(1.0, (pos.x() - self._rect_slider.left()) / max(1.0, self._rect_slider.width())))
                self._scrub_ratio = ratio
                self.seekRatioScrubbing.emit(ratio)
                self.update()
                event.accept()
                return

            # 2. 音量滑条点击与拖拽
            if self._rect_vol_track.adjusted(-8.0, -8.0, 8.0, 8.0).contains(pos):
                self.is_dragging_volume = True
                try:
                    self.grabMouse()
                except Exception:
                    pass
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
            self.seekRatioScrubbing.emit(ratio)
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
            try:
                self.releaseMouse()
            except Exception:
                pass
            self.seekRatioRequested.emit(self._scrub_ratio)
            self.update()
            event.accept()
            return
        if self.is_dragging_volume:
            self.is_dragging_volume = False
            try:
                self.releaseMouse()
            except Exception:
                pass
            self.update()
            event.accept()
            return
        super().mouseReleaseEvent(event)

    def hideEvent(self, event) -> None:
        if self.is_dragging_progress or self.is_dragging_volume:
            self.is_dragging_progress = False
            self.is_dragging_volume = False
            try:
                self.releaseMouse()
            except Exception:
                pass
        super().hideEvent(event)

    def _render_sleeve_card(self, sleeve_sz: int) -> QPixmap:
        """预渲染带 3 层弥散阴影与圆角玻璃高光的专辑封面卡片，避免每帧重复裁剪与重绘"""
        pad = 36
        total_w = sleeve_sz + pad * 2
        total_h = sleeve_sz + pad * 2
        dpr = max(1.0, self.devicePixelRatioF())
        pix = QPixmap(int(total_w * dpr), int(total_h * dpr))
        pix.setDevicePixelRatio(dpr)
        pix.fill(Qt.GlobalColor.transparent)

        sp = QPainter(pix)
        sp.setRenderHint(QPainter.RenderHint.Antialiasing)
        sp.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)

        card_rect = QRectF(float(pad), float(pad), float(sleeve_sz), float(sleeve_sz))
        s_radius = 22.0

        # 现代柔和连续景深阴影 (多层极低透明度漫反射，消除硬边缘暗阶与伪线)
        for i in range(16):
            t_sh = (i + 1) / 16.0
            spread = 1.5 + t_sh * 22.0
            alpha = int(round(18.0 * math.exp(-2.5 * t_sh * t_sh)))
            if alpha > 0:
                sh_rect = card_rect.adjusted(-spread * 0.35, spread * 0.5, spread * 0.35, spread * 1.1)
                sh_path = QPainterPath()
                sh_path.addRoundedRect(sh_rect, s_radius + spread * 0.4, s_radius + spread * 0.4)
                sp.fillPath(sh_path, QColor(0, 0, 0, alpha))

        jacket_path = QPainterPath()
        jacket_path.addRoundedRect(card_rect, s_radius, s_radius)
        sp.save()
        sp.setClipPath(jacket_path)
        if self.cover_pixmap is not None and not self.cover_pixmap.isNull():
            sp.drawPixmap(card_rect.toRect(), self.cover_pixmap)
        elif self.track is not None:
            grad = QLinearGradient(card_rect.topLeft(), card_rect.bottomRight())
            grad.setColorAt(0.0, QColor(self.track.palette["bg_primary"]))
            grad.setColorAt(1.0, QColor(self.track.palette["bg_secondary"]))
            sp.fillRect(card_rect, grad)
        else:
            sp.fillRect(card_rect, QColor(36, 42, 50))

        glass_grad = QLinearGradient(card_rect.topLeft(), card_rect.bottomRight())
        glass_grad.setColorAt(0.0, QColor(255, 255, 255, 45))
        glass_grad.setColorAt(0.35, QColor(255, 255, 255, 8))
        glass_grad.setColorAt(0.38, Qt.GlobalColor.transparent)
        glass_grad.setColorAt(1.0, Qt.GlobalColor.transparent)
        sp.fillRect(card_rect, glass_grad)
        sp.restore()

        sp.setPen(QPen(QColor(255, 255, 255, 38), 1.0))
        sp.setBrush(Qt.BrushStyle.NoBrush)
        sp.drawPath(jacket_path)
        sp.end()
        return pix

    def paintEvent(self, event) -> None:
        w, h = self.width(), self.height()
        if w < 100 or h < 100:
            return

        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)
        p.setRenderHint(QPainter.RenderHint.TextAntialiasing)

        # 1. 计算自适应几何排版 (整体下移平稳居中)
        ctrl_h = 216.0  # 下方控件区总高度 (加大呼吸空间)
        max_sleeve = min(390.0, min(w * 0.74, h - ctrl_h - 80.0))
        sleeve_sz = int(max(240.0, max_sleeve))
        disc_sz = int(sleeve_sz * 0.94)

        if self.show_vinyl:
            center_x = w * 0.40
            slide_offset = sleeve_sz * (0.08 + 0.34 * self.slide_prog)
        else:
            center_x = w * 0.48
            slide_offset = 0.0

        total_content_h = sleeve_sz + ctrl_h
        # 整体下移至屏幕视觉黄金中心，避免上方过于拥挤
        sleeve_top = max(40.0, (h - total_content_h) * 0.52 + 36.0)
        sleeve_left = center_x - sleeve_sz / 2.0
        center_y = sleeve_top + sleeve_sz / 2.0

        # B. 若开启黑胶模式，绘制微露出的伴生黑胶唱片
        disc_cx = center_x + slide_offset
        disc_cy = center_y
        if self.show_vinyl and (self.slide_prog > 0.01 or self.is_playing):
            disc_key = f"{self.track.track_id if self.track else 'none'}_{disc_sz}_{id(self.cover_pixmap)}"
            if self._cached_disc_key != disc_key or self._cached_disc_pixmap is None:
                self._cached_disc_pixmap = self._render_vinyl_disc(disc_sz)
                self._cached_disc_key = disc_key

            if self._cached_disc_pixmap is not None:
                p.save()
                p.setOpacity(min(1.0, 0.4 + 0.6 * self.slide_prog))
                p.translate(disc_cx, disc_cy)
                p.rotate(self.vinyl_angle)
                p.drawPixmap(int(-disc_sz / 2.0), int(-disc_sz / 2.0), self._cached_disc_pixmap)
                p.restore()

        # C. 绘制前景核心现代专辑封面卡片 (使用预缓存纹理，单次 Blit 零裁剪开销)
        sleeve_rect = QRectF(sleeve_left, sleeve_top, float(sleeve_sz), float(sleeve_sz))
        sleeve_key = f"{self.track.track_id if self.track else 'none'}_{sleeve_sz}_{id(self.cover_pixmap)}"
        if self._cached_sleeve_key != sleeve_key or self._cached_sleeve_pixmap is None:
            self._cached_sleeve_pixmap = self._render_sleeve_card(sleeve_sz)
            self._cached_sleeve_key = sleeve_key
        if self._cached_sleeve_pixmap is not None:
            p.drawPixmap(int(round(sleeve_left - 36.0)), int(round(sleeve_top - 36.0)), self._cached_sleeve_pixmap)

        # 若开启现代黑胶模式且包含唱臂，在暴露的右侧音轨上方绘制现代极简线性唱臂
        if self.show_vinyl and self.show_tonearm and (self.slide_prog > 0.05 or self.is_playing):
            pivot_x = disc_cx + (disc_sz / 2.0) * 0.68
            pivot_y = disc_cy - (disc_sz / 2.0) * 0.78
            self._draw_tonearm(p, pivot_x, pivot_y, disc_sz / 2.0)

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
            elif actual_ch == 8:
                atmos_txt = "DOLBY ATMOS 7.1"
            elif self.track and getattr(self.track, "is_hi_res", False):
                atmos_txt = f"HI-RES · ATMOS"
            else:
                atmos_txt = "DOLBY ATMOS"
        elif self.track:
            ext = (getattr(self.track, "audio_format", "") or "").upper()
            sr_txt = getattr(self.track, "sample_rate_str", "44.1kHz")
            if getattr(self.track, "is_hi_res", False):
                atmos_txt = f"HI-RES · {sr_txt}"
            elif ext in ("FLAC", "WAV", "APE", "ALAC", "AIFF"):
                atmos_txt = f"LOSSLESS · {sr_txt}"
            elif ext in ("MP3", "AAC", "M4A", "OGG"):
                atmos_txt = f"{ext} · {sr_txt}"
            else:
                atmos_txt = f"PCM · {sr_txt}"
        else:
            atmos_txt = "LOSSLESS"

        b_font = self._font_badge
        fm_badge = self._fm_badge
        badge_w = max(66.0, fm_badge.horizontalAdvance(atmos_txt) + 16.0)
        title_max_w = sleeve_sz - badge_w - 10.0

        title_str = self.track.title if self.track else "Klang"
        artist_str = self.track.artist if self.track else "北欧极简极光视界"
        album_str = self.track.album if (self.track and self.track.album) else ""

        # 歌曲名 (22px SF Pro Bold)
        p.setFont(self._font_title)
        elided_title = self._fm_title.elidedText(title_str, Qt.TextElideMode.ElideRight, title_max_w)
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
            p.setFont(b_font)
            p.setPen(QColor(255, 255, 255, 240))
            p.drawText(badge_rect, int(Qt.AlignmentFlag.AlignCenter), atmos_txt)
        else:
            p.fillPath(badge_path, QColor(255, 255, 255, 20))
            p.setPen(QPen(QColor(255, 255, 255, 45), 1.0))
            p.drawPath(badge_path)
            p.setFont(b_font)
            p.setPen(QColor(255, 255, 255, 210))
            p.drawText(badge_rect, int(Qt.AlignmentFlag.AlignCenter), atmos_txt)

        # 歌手与专辑副标题 (14px SF Pro Medium)
        sub_str = f"{artist_str}  ·  {album_str}" if album_str else artist_str
        p.setFont(self._font_sub)
        elided_sub = self._fm_sub.elidedText(sub_str, Qt.TextElideMode.ElideRight, sleeve_sz)
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

        # 进度条下方时间戳
        time_y = sl_y + 8.0
        p.setFont(self._font_time)
        p.setPen(QColor(255, 255, 255, 175))
        disp_time = (self._scrub_ratio * self.duration) if self.is_dragging_progress else self.current_time
        left_time_str = format_timecode(disp_time)
        rem_sec = max(0.0, self.duration - disp_time)
        right_time_str = f"-{format_timecode(rem_sec)}"
        p.drawText(QRectF(sleeve_left, time_y, 80.0, 16.0), int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter), left_time_str)
        p.drawText(QRectF(sleeve_rect.right() - 80.0, time_y, 80.0, 16.0), int(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter), right_time_str)

        # 3. 核心播放控制行 (同图片二横向排版)
        btn_cy = sl_y + 48.0
        gap = sleeve_sz * 0.20

        # A. 模式 / 随机或情绪漫游播放 (左一)
        mode_x = center_x - gap * 2.0
        self._rect_btn_mode = QRectF(mode_x - 18.0, btn_cy - 18.0, 36.0, 36.0)
        is_mode_hov = (self._hover_element == "mode")
        is_shuffle_or_flow = (self.play_mode in ("shuffle", "smart_flow"))
        mode_col = QColor(self.accent_color if is_shuffle_or_flow else QColor(255, 255, 255, 230 if is_mode_hov else 165))
        p.setPen(QPen(mode_col, 1.8, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin))
        p.setBrush(Qt.BrushStyle.NoBrush)
        if self.play_mode == "smart_flow":
            flow_path = QPainterPath()
            flow_path.moveTo(mode_x - 8.0, btn_cy + 1.5)
            flow_path.cubicTo(mode_x - 4.0, btn_cy - 7.0, mode_x - 0.5, btn_cy + 7.0, mode_x + 3.5, btn_cy - 2.0)
            flow_path.quadTo(mode_x + 6.0, btn_cy - 5.5, mode_x + 8.0, btn_cy - 1.0)
            p.drawPath(flow_path)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(mode_col)
            p.drawEllipse(QPointF(mode_x + 6.5, btn_cy - 5.5), 1.6, 1.6)
            p.drawEllipse(QPointF(mode_x - 6.0, btn_cy - 5.0), 1.3, 1.3)
        else:
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
        is_loop = (self.play_mode in ("loop_list", "loop_one", "loop_single"))
        is_single = (self.play_mode in ("loop_one", "loop_single"))
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
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.setMouseTracking(True)
        self.lyrics: List[LyricLine] = []
        self.active_index: int = -1
        self.smooth_focus_idx: float = 0.0
        self.smooth_focus_vel: float = 0.0
        self.current_time: float = 0.0
        self.lyric_offset: float = 0.0
        self.is_playing: bool = False
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

        # 预缓存无网格吸附 (PreferNoHinting) 字体与浮点基线，彻底消除缩放/滚动时的 1px 阶梯抖动
        self._main_font = get_nordic_font(38, QFont.Weight.Bold)
        self._main_font.setHintingPreference(QFont.HintingPreference.PreferNoHinting)
        self._main_font.setStyleStrategy(
            QFont.StyleStrategy.PreferAntialias | QFont.StyleStrategy.PreferQuality
        )
        self._sub_font = get_nordic_font(18, QFont.Weight.Medium)
        self._sub_font.setHintingPreference(QFont.HintingPreference.PreferNoHinting)
        self._sub_font.setStyleStrategy(
            QFont.StyleStrategy.PreferAntialias | QFont.StyleStrategy.PreferQuality
        )
        self._empty_font = get_nordic_font(26, QFont.Weight.Medium)
        self._fm_main = QFontMetricsF(self._main_font)
        self._fm_sub = QFontMetricsF(self._sub_font)
        self._roma_font = get_nordic_font(15, QFont.Weight.Medium)
        self._roma_font.setHintingPreference(QFont.HintingPreference.PreferNoHinting)
        self._fm_roma = QFontMetricsF(self._roma_font)
        self.show_romaji: bool = True
        self._main_baseline_y: float = (48.0 + self._fm_main.ascent() - self._fm_main.descent()) * 0.5
        self._sub_baseline_y: float = 50.0 + (28.0 + self._fm_sub.ascent() - self._fm_sub.descent()) * 0.5
        self._cached_layout_w: int = -1
        self._elided_main_cache: List[str] = []
        self._adv_main_cache: List[float] = []
        self._elided_sub_cache: List[str] = []
        self._elided_roma_cache: List[str] = []

    def set_lyrics(self, lyrics: List[LyricLine], offset: float = 0.0) -> None:
        self.lyrics = list(lyrics)
        self.lyric_offset = offset
        self.active_index = -1
        self.smooth_focus_idx = 0.0
        self.smooth_focus_vel = 0.0
        self._recompute_layout()
        self.scroll_y = 0.0
        self.scroll_target_y = 0.0
        self.scroll_vel = 0.0
        self.update()

    def _update_active_index_for_time(self, eff_time: float) -> bool:
        new_idx = -1
        for idx, line in enumerate(self.lyrics):
            if eff_time >= line.time:
                new_idx = idx
            else:
                break
        if new_idx != self.active_index:
            prev_idx = self.active_index
            self.active_index = new_idx
            if prev_idx == -1 and 0 <= new_idx < len(self._line_y_tops):
                # 首次切歌定位时直接对齐起始行，避免从第 0 行生硬大跨度滚下来
                self.smooth_focus_idx = float(new_idx)
                self.smooth_focus_vel = 0.0
                target_y = self._line_y_tops[new_idx] + self._line_heights[new_idx] * 0.5
                self.scroll_y = target_y
                self.scroll_target_y = target_y
                self.scroll_vel = 0.0
            return True
        return False

    def sync_time(self, cur_time: float) -> None:
        cur_time = max(0.0, cur_time)
        if not self.is_playing or abs(cur_time - self.current_time) > 0.12:
            self.current_time = cur_time
            eff_time = max(0.0, self.current_time + self.lyric_offset)
            self._update_active_index_for_time(eff_time)
            if not self.is_playing and 0 <= self.active_index < len(self._line_y_tops) and not self.user_scrolling:
                self.scroll_target_y = self._line_y_tops[self.active_index] + self._line_heights[self.active_index] * 0.5
            if not self.is_playing:
                self.update()
        else:
            # 播放中通过微阻尼平滑消除时钟抖动，由 120 FPS step_physics 统一推进与重绘
            self.current_time += (cur_time - self.current_time) * 0.25
            eff_time = max(0.0, self.current_time + self.lyric_offset)
            self._update_active_index_for_time(eff_time)

    def step_physics(self, dt: float) -> None:
        if not self.lyrics or len(self._line_y_tops) != len(self.lyrics):
            return

        dt = max(0.001, min(0.05, dt))

        # 0. 120 FPS 连续帧间时钟推进（消除卡拉OK逐字扫掠在相邻帧卡顿冻结）
        if self.is_playing:
            self.current_time += dt
            eff_time = max(0.0, self.current_time + self.lyric_offset)
            self._update_active_index_for_time(eff_time)

        # 1. 解析解临界阻尼弹簧光标跟随 (Analytical Critically-Damped Focus Follower)
        target_focus = float(self.active_index) if self.active_index >= 0 else 0.0
        omega_f = 13.5
        exp_f = math.exp(-omega_f * dt)
        diff_f = self.smooth_focus_idx - target_focus
        temp_f = self.smooth_focus_vel + omega_f * diff_f
        self.smooth_focus_idx = target_focus + (diff_f + temp_f * dt) * exp_f
        self.smooth_focus_vel = (self.smooth_focus_vel - omega_f * temp_f * dt) * exp_f

        # 2. 直接锁定当前激活行的真实几何中心（去除双重级联 Hermite 导致的跨行停顿感）
        if not self.user_scrolling and 0 <= self.active_index < len(self._line_y_tops):
            self.scroll_target_y = self._line_y_tops[self.active_index] + self._line_heights[self.active_index] * 0.5

        # 3. 解析解临界阻尼弹簧滚动物理 (Exact Analytical Critically-Damped Spring, 零过冲/零停顿)
        omega_s = 11.5
        exp_s = math.exp(-omega_s * dt)
        diff_y = self.scroll_y - self.scroll_target_y
        temp_y = self.scroll_vel + omega_s * diff_y
        self.scroll_y = self.scroll_target_y + (diff_y + temp_y * dt) * exp_s
        self.scroll_vel = (self.scroll_vel - omega_s * temp_y * dt) * exp_s

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

    def set_show_romaji(self, show: bool) -> None:
        if getattr(self, "show_romaji", True) == bool(show):
            return
        self.show_romaji = bool(show)
        self._recompute_layout()
        self.update()

    def set_syllable_mode(self, enabled: bool) -> None:
        self.syllable_mode = bool(enabled)
        self.update()

    def _recompute_layout(self) -> None:
        self._line_heights = []
        self._line_y_tops = []
        self._cached_layout_w = -1
        cur_y = 0.0
        for line in self.lyrics:
            h = 62.0
            if getattr(line, "roma", "") and getattr(self, "show_romaji", True) and not line.is_interlude:
                h += 24.0
            if line.sub and self.show_translation:
                h += 30.0
            self._line_y_tops.append(cur_y)
            self._line_heights.append(h)
            cur_y += h + 32.0

    def _ensure_text_metrics_cache(self, max_text_w: float) -> None:
        w_key = int(round(max_text_w))
        if self._cached_layout_w == w_key and len(self._elided_main_cache) == len(self.lyrics):
            return
        self._cached_layout_w = w_key
        self._elided_main_cache = []
        self._adv_main_cache = []
        self._elided_sub_cache = []
        self._elided_roma_cache = []
        fm_main = self._fm_main
        fm_sub = self._fm_sub
        fm_roma = getattr(self, "_fm_roma", QFontMetricsF(self._sub_font))
        for line in self.lyrics:
            elided_m = fm_main.elidedText(line.text, Qt.TextElideMode.ElideRight, max_text_w)
            adv_m = min(max_text_w, fm_main.horizontalAdvance(elided_m))
            self._elided_main_cache.append(elided_m)
            self._adv_main_cache.append(adv_m)
            if getattr(line, "roma", ""):
                roma_str = str(line.roma).strip()
                self._elided_roma_cache.append(
                    fm_roma.elidedText(roma_str, Qt.TextElideMode.ElideRight, max_text_w) if roma_str else ""
                )
            else:
                self._elided_roma_cache.append("")
            if line.sub:
                sub_str = str(line.sub).strip()
                self._elided_sub_cache.append(
                    fm_sub.elidedText(sub_str, Qt.TextElideMode.ElideRight, max_text_w) if sub_str else ""
                )
            else:
                self._elided_sub_cache.append("")

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
            viewport_center = self.height() * 0.48
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
            p.setFont(self._empty_font)
            p.setPen(QColor(255, 255, 255, 120))
            p.drawText(self.rect(), int(Qt.AlignmentFlag.AlignCenter), "纯音乐，请静心聆听")
            return

        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setRenderHint(QPainter.RenderHint.TextAntialiasing)
        p.setRenderHint(QPainter.RenderHint.VerticalSubpixelPositioning)

        # 歌词直接画在模糊封面背景上，不加任何遮罩层；可读性由着色器统一压暗保证。

        viewport_center = h * 0.48
        pad_left = 36.0
        max_text_w = max(100.0, w - pad_left - 48.0)
        fade_zone = max(80.0, h * 0.22)

        self._ensure_text_metrics_cache(max_text_w)
        main_font = self._main_font
        sub_font = self._sub_font
        main_text_h = 48.0
        main_base_pt = QPointF(0.0, self._main_baseline_y)
        main_glow_pt = QPointF(0.0, self._main_baseline_y + 1.0)
        sub_base_pt = QPointF(0.0, self._sub_baseline_y)

        for idx, line in enumerate(self.lyrics):
            top_y = self._line_y_tops[idx]
            line_h = self._line_heights[idx]
            screen_y = viewport_center + (top_y - self.scroll_y)

            if screen_y + line_h < -40.0 or screen_y > h + 40.0:
                continue

            # 边缘羽化渐隐
            edge_factor = min(
                1.0,
                max(0.0, (h - (screen_y + line_h * 0.5)) / fade_zone),
                max(0.0, (screen_y + line_h * 0.5) / fade_zone),
            )
            edge_curve = edge_factor * edge_factor * (3.0 - 2.0 * edge_factor)

            # 连续非线性距离
            d = abs(float(idx) - self.smooth_focus_idx)
            focus_raw = max(0.0, 1.0 - d)
            focus_eased = focus_raw * focus_raw * (3.0 - 2.0 * focus_raw)

            # 字号：当前行 1.0，其余 0.80
            line_scale = 0.80 + 0.20 * focus_eased

            # 透明度：当前行不透明，相邻行约 40%，越远越淡
            if d < 1.0:
                base_alpha = 0.40 + 0.60 * focus_eased
            else:
                base_alpha = max(0.16, 0.40 * math.pow(0.80, max(0.0, d - 1.0)))

            final_alpha = base_alpha * edge_curve
            if final_alpha <= 0.02:
                continue

            # Apple 风格水平浮动阶梯 (获得焦点的行向右轻微推开 10px)
            drift_x = 10.0 * focus_eased

            p.save()
            p.setOpacity(final_alpha)
            p.translate(pad_left + drift_x, screen_y)

            has_roma = bool(getattr(line, "roma", "") and getattr(self, "show_romaji", True) and not line.is_interlude)
            y_shift = 22.0 if has_roma else 0.0

            if has_roma:
                elided_roma = self._elided_roma_cache[idx]
                if elided_roma:
                    p.setFont(self._roma_font)
                    roma_alpha = int(round(225 * focus_eased + 90 * (1.0 - focus_eased)))
                    # 微阴影增强立体分离感
                    p.setPen(QColor(0, 0, 0, int(round(120 * focus_eased + 50 * (1.0 - focus_eased)))))
                    p.drawText(QPointF(0.0, 19.2), elided_roma)
                    p.setPen(QColor(255, 255, 255, roma_alpha))
                    p.drawText(QPointF(0.0, 18.0), elided_roma)

            cur_main_base = QPointF(0.0, self._main_baseline_y + y_shift)
            cur_main_glow = QPointF(0.0, self._main_baseline_y + y_shift + 1.0)
            cur_sub_base = QPointF(0.0, self._sub_baseline_y + y_shift)

            # 缩放歌词本体
            p.save()
            p.scale(line_scale, line_scale)

            p.setFont(main_font)
            elided_str = self._elided_main_cache[idx]

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

                    adv_w = self._adv_main_cache[idx]

                    # 1. 绘制暗部未唱到的半透明歌词底色 (使用浮点 QPointF 基线，实现 DirectWrite 亚像素定位)
                    p.setPen(QColor(255, 255, 255, 105))
                    p.drawText(cur_main_base, elided_str)

                    # 2. 液态流光亮部
                    if prog > 0.002:
                        fill_w = adv_w * prog
                        p.save()
                        clip_rect = QRectF(-4.0, -10.0 + y_shift, fill_w + 4.0, main_text_h + 20.0)
                        p.setClipRect(clip_rect, Qt.ClipOperation.IntersectClip)

                        # 底层热点柔光光晕 (Bloom Glow)
                        glow_col = QColor(self.accent_color)
                        glow_col.setAlpha(int(round(90 * focus_eased)))
                        p.setPen(glow_col)
                        p.drawText(cur_main_glow, elided_str)

                        # 表层纯白流光高亮
                        p.setPen(QColor(255, 255, 255, 255))
                        p.drawText(cur_main_base, elided_str)
                        p.restore()
                else:
                    p.setPen(QColor(255, 255, 255, 255))
                    p.drawText(cur_main_base, elided_str)

            p.restore()

            # 绘制翻译译文行 (浮点亚像素基线定位)
            if self.show_translation:
                elided_sub = self._elided_sub_cache[idx]
                if elided_sub:
                    p.setFont(sub_font)
                    sub_alpha = int(round(195 * focus_eased + 90 * (1.0 - focus_eased)))
                    p.setPen(QColor(0, 0, 0, int(round(120 * focus_eased + 50 * (1.0 - focus_eased)))))
                    p.drawText(cur_sub_base + QPointF(0.0, 1.4), elided_sub)
                    p.setPen(QColor(255, 255, 255, sub_alpha))
                    p.drawText(cur_sub_base, elided_sub)

            p.restore()


class ImmersiveControlDock(QWidget):
    """悬浮毛玻璃胶囊控制台 (Apple Island Dock)"""

    playToggled = pyqtSignal()
    prevClicked = pyqtSignal()
    nextClicked = pyqtSignal()
    seekRatioRequested = pyqtSignal(float)
    seekRatioScrubbing = pyqtSignal(float)
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
            self.seekRatioScrubbing.emit(ratio)
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
            self.seekRatioScrubbing.emit(ratio)
            self.update()
            event.accept()
            return
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        if self.is_dragging_progress:
            self.is_dragging_progress = False
            self.seekRatioRequested.emit(self._scrub_ratio)
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
    seekRatioScrubbing = pyqtSignal(float)
    seekTimeRequested = pyqtSignal(float)
    modeCycled = pyqtSignal()
    volumeRequested = pyqtSignal(float)
    translationToggled = pyqtSignal(bool)
    syllableToggled = pyqtSignal(bool)
    translationFetchRequested = pyqtSignal(object)
    fpsChanged = pyqtSignal(int)
    settingsRequested = pyqtSignal()
    posterRequested = pyqtSignal()

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(None)
        self.setWindowTitle("Klang — 极光沉浸视界")
        self.setWindowFlags(Qt.WindowType.Window | Qt.WindowType.FramelessWindowHint)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, False)
        self.setObjectName("ImmersiveFullscreenWindow")
        self.setStyleSheet("#ImmersiveFullscreenWindow { background: #080B10; border: none; }")
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
        self._aurora_pixmap: Optional[QPixmap] = None
        self._last_aurora_ms: int = -1000
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

        # 动画与歌词刷新率控制 (默认 120 FPS 原生高刷)
        self.target_fps: int = 120
        # 帧间隔用高精度时钟（QElapsedTimer 仅毫秒整数，8/9ms 交替会造成滚动微抖）
        self._last_frame_t: float = time.perf_counter()

        # 120 FPS 模式且 GPU 背景可用时，由 GPU 的 frameSwapped（垂直同步）驱动帧循环；
        # 定时器仅作兜底看门狗 / 60 FPS 模式 / CPU 降级模式的驱动源
        self._render_timer = QTimer(self)
        self._render_timer.setTimerType(Qt.TimerType.PreciseTimer)
        self._render_timer.setInterval(7)
        self._render_timer.timeout.connect(self._on_render_timer)

        self._setup_ui()
        self.fluid_shader = FluidMeshShaderWidget(self)
        self.fluid_shader.setGeometry(self.rect())
        self.fluid_shader.lower()
        self.fluid_shader.frameSwapped.connect(self._on_frame_swapped)

    def _setup_ui(self) -> None:
        root_vbox = QVBoxLayout(self)
        root_vbox.setContentsMargins(0, 74, 0, 30)
        root_vbox.setSpacing(0)

        # 1. 顶部悬浮工具条 (绝对坐标悬浮于中央舞台上方，绝不因隐藏重新排版引起画面抖动上移)
        self.top_bar_widget = QWidget(self)
        self.top_bar_widget.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.top_bar_widget.setFixedHeight(48)
        self.top_bar_widget.setGeometry(0, 26, max(800, self.width()), 48)
        top_hbox = QHBoxLayout(self.top_bar_widget)
        top_hbox.setContentsMargins(40, 0, 40, 0)
        top_hbox.setSpacing(12)

        self.lbl_title_badge = QLabel("KLANG", self.top_bar_widget)
        self.lbl_title_badge.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.lbl_title_badge.setStyleSheet(
            "background: rgba(255, 255, 255, 0.08); "
            "border: 1px solid rgba(255, 255, 255, 0.14); "
            "border-radius: 14px; "
            "padding: 4px 12px; "
            "color: rgba(255, 255, 255, 0.88); "
            "letter-spacing: 0.8px;"
        )
        top_hbox.addWidget(self.lbl_title_badge)

        top_hbox.addStretch(1)

        pill_btn_base = (
            "QPushButton { "
            "background: rgba(255, 255, 255, 0.08); "
            "border: 1px solid rgba(255, 255, 255, 0.16); "
            "border-radius: 15px; "
            "color: rgba(255, 255, 255, 0.90); "
            "font-weight: 600; font-size: 13px; "
            "font-family: 'SF Pro Display', 'PingFang SC', sans-serif; "
            "} "
            "QPushButton:hover { "
            "background: rgba(255, 255, 255, 0.18); "
            "border-color: rgba(255, 255, 255, 0.30); "
            "color: #FFFFFF; "
            "} "
            "QPushButton:pressed { "
            "background: rgba(255, 255, 255, 0.26); "
            "border-color: rgba(255, 255, 255, 0.40); "
            "}"
        )

        # 译文开关
        self.btn_trans = QPushButton("译", self.top_bar_widget)
        self.btn_trans.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.btn_trans.setFixedSize(36, 30)
        self.btn_trans.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_trans.setToolTip("显示 / 隐藏歌词翻译")
        self.btn_trans.setStyleSheet(pill_btn_base)
        self.btn_trans.clicked.connect(self._on_toggle_translation)
        top_hbox.addWidget(self.btn_trans)

        # 假名注音 / 罗马音开关
        self.btn_roma = QPushButton("音", self.top_bar_widget)
        self.btn_roma.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.btn_roma.setFixedSize(36, 30)
        self.btn_roma.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_roma.setToolTip("显示 / 隐藏假名注音与罗马音")
        self.btn_roma.setStyleSheet(pill_btn_base)
        self.btn_roma.clicked.connect(self._on_toggle_romaji)
        top_hbox.addWidget(self.btn_roma)

        # 设置入口按钮 (统一配置帧率、封面、动效与音频)
        self.btn_settings = QPushButton("设置", self.top_bar_widget)
        self.btn_settings.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.btn_settings.setFixedHeight(30)
        self.btn_settings.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_settings.setToolTip("设置 (音效、歌词、刷新率等)")
        self.btn_settings.setStyleSheet(
            pill_btn_base.replace("QPushButton {", "QPushButton { padding: 0 14px;")
        )
        self.btn_settings.clicked.connect(self.settingsRequested.emit)
        top_hbox.addWidget(self.btn_settings)

        # 保留历史内部组件句柄以防外部方法调用报错
        self.btn_syllable = QPushButton("逐字")
        self.btn_vinyl_mode = QPushButton("纯净封面")
        self.btn_fps = QPushButton(f"{self.target_fps} FPS")
        for _w in (self.btn_syllable, self.btn_vinyl_mode, self.btn_fps):
            _w.hide()

        # 歌词海报卡片按钮
        self.btn_poster = QPushButton("歌词卡片", self.top_bar_widget)
        self.btn_poster.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.btn_poster.setFixedHeight(30)
        self.btn_poster.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_poster.setToolTip("生成歌词卡片")
        self.btn_poster.setStyleSheet(
            pill_btn_base.replace("QPushButton {", "QPushButton { padding: 0 14px;")
        )
        self.btn_poster.clicked.connect(self.posterRequested.emit)
        top_hbox.addWidget(self.btn_poster)

        # 退出全屏按钮
        self.btn_exit = QPushButton("退出全屏 (Esc)", self.top_bar_widget)
        self.btn_exit.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.btn_exit.setFixedHeight(30)
        self.btn_exit.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_exit.setToolTip("退出全屏 (Esc)")
        self.btn_exit.setStyleSheet(
            pill_btn_base.replace("QPushButton {", "QPushButton { padding: 0 16px;")
        )
        self.btn_exit.clicked.connect(self.exit_immersive)
        top_hbox.addWidget(self.btn_exit)

        # 2. 中间舞台左右分栏 (左黑胶唱机与封面，右大字滚动流光歌词)
        self.stage_split = QWidget(self)
        self.stage_split.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        split_hbox = QHBoxLayout(self.stage_split)
        split_hbox.setContentsMargins(0, 0, 40, 0)
        split_hbox.setSpacing(36)

        self.artwork_view = ImmersiveArtworkView(self.stage_split)
        self.artwork_view.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.artwork_view.playToggled.connect(self.playToggled.emit)
        self.artwork_view.prevClicked.connect(self.prevRequested.emit)
        self.artwork_view.nextClicked.connect(self.nextRequested.emit)
        self.artwork_view.seekRatioRequested.connect(self.seekRatioRequested.emit)
        self.artwork_view.seekRatioScrubbing.connect(self.seekRatioScrubbing.emit)
        self.artwork_view.modeCycled.connect(self.modeCycled.emit)
        self.artwork_view.volumeChanged.connect(self.volumeRequested.emit)
        split_hbox.addWidget(self.artwork_view, 45)

        self.lyrics_view = ImmersiveLyricsView(self.stage_split)
        self.lyrics_view.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.lyrics_view.seekRequested.connect(self.seekTimeRequested.emit)
        split_hbox.addWidget(self.lyrics_view, 55)

        root_vbox.addWidget(self.stage_split, 1)

        self.top_bar_widget.raise_()
        self._top_opacity_eff = QGraphicsOpacityEffect(self.top_bar_widget)
        self.top_bar_widget.setGraphicsEffect(self._top_opacity_eff)

        self._attach_mouse_tracking_and_filter()

    def _attach_mouse_tracking_and_filter(self) -> None:
        targets = [self, getattr(self, "top_bar_widget", None), getattr(self, "stage_split", None), getattr(self, "artwork_view", None), getattr(self, "lyrics_view", None)]
        for w in targets:
            if w is not None:
                try:
                    w.setMouseTracking(True)
                    w.removeEventFilter(self)
                    w.installEventFilter(self)
                    for child in w.findChildren(QWidget):
                        child.setMouseTracking(True)
                        child.removeEventFilter(self)
                        child.installEventFilter(self)
                except Exception:
                    pass

    def _wake_hud(self) -> None:
        if self._hud_target_opacity < 0.05 or self._hud_opacity < 0.05:
            self.unsetCursor()
        self._hud_target_opacity = 1.0
        self._idle_timer.start(3000)

    def eventFilter(self, watched, event: QEvent) -> bool:
        if event.type() in (
            QEvent.Type.MouseMove,
            QEvent.Type.MouseButtonPress,
            QEvent.Type.MouseButtonRelease,
            QEvent.Type.Wheel,
            QEvent.Type.HoverMove,
        ):
            self._wake_hud()
        return super().eventFilter(watched, event)

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

    def _on_toggle_romaji(self) -> None:
        self.show_romaji = not getattr(self, "show_romaji", True)
        self.btn_roma.setStyleSheet(
            f"QPushButton {{ background: {'rgba(255, 255, 255, 0.32)' if self.show_romaji else 'rgba(255, 255, 255, 0.10)'}; "
            "border: 1px solid rgba(255, 255, 255, 0.25); border-radius: 7px; color: #FFFFFF; font-weight: 600; font-size: 13px; }"
        )
        self.lyrics_view.set_show_romaji(self.show_romaji)

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
        cur_mode = getattr(self.artwork_view, "vinyl_mode", "none")
        mode_cycle = ["none", "pure", "arm"]
        next_idx = (mode_cycle.index(cur_mode) + 1) % len(mode_cycle) if cur_mode in mode_cycle else 1
        new_mode = mode_cycle[next_idx]
        self.artwork_view.set_vinyl_mode(new_mode)

        labels = {"none": "方形封面", "pure": "黑胶唱片", "arm": "黑胶唱机"}
        label = labels.get(new_mode, "方形封面")
        self.btn_vinyl_mode.setText(label)
        is_active = (new_mode != "none")
        self.btn_vinyl_mode.setStyleSheet(
            f"QPushButton {{ background: {'rgba(255, 255, 255, 0.32)' if is_active else 'rgba(255, 255, 255, 0.12)'}; "
            "border: 1px solid rgba(255, 255, 255, 0.25); border-radius: 7px; color: #FFFFFF; font-weight: 600; font-size: 13px; padding: 0 10px; }"
            "QPushButton:hover { background: rgba(255, 255, 255, 0.36); }"
        )

    def _on_toggle_fps(self) -> None:
        new_fps = 120 if self.target_fps == 60 else 60
        self.set_target_fps(new_fps)
        self.fpsChanged.emit(new_fps)

    def set_target_fps(self, fps: int) -> None:
        self.target_fps = 120 if fps >= 100 else 60
        interval = 7 if self.target_fps == 120 else 15
        self._render_timer.setInterval(interval)
        if hasattr(self, "fluid_shader"):
            self.fluid_shader.set_fps(self.target_fps)
        self._update_fps_btn_style()

    def _update_fps_btn_style(self) -> None:
        if not hasattr(self, "btn_fps"):
            return
        self.btn_fps.setText(f"{self.target_fps} FPS")
        if self.target_fps == 120:
            self.btn_fps.setToolTip("刷新率：120 FPS（点击切换至 60 FPS）")
            self.btn_fps.setStyleSheet(
                "QPushButton { background: rgba(91, 158, 131, 0.35); border: 1px solid rgba(126, 224, 182, 0.65); "
                "border-radius: 7px; color: #7EE0B6; font-weight: 700; font-size: 13px; padding: 0 10px; font-family: 'SF Mono', 'Consolas', monospace; }"
                "QPushButton:hover { background: rgba(91, 158, 131, 0.50); }"
            )
        else:
            self.btn_fps.setToolTip("刷新率：60 FPS（点击切换至 120 FPS）")
            self.btn_fps.setStyleSheet(
                "QPushButton { background: rgba(255, 255, 255, 0.12); border: 1px solid rgba(255, 255, 255, 0.22); "
                "border-radius: 7px; color: rgba(255, 255, 255, 0.88); font-weight: 600; font-size: 13px; padding: 0 10px; font-family: 'SF Mono', 'Consolas', monospace; }"
                "QPushButton:hover { background: rgba(255, 255, 255, 0.25); }"
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
        self.top_bar_widget.setGeometry(0, 26, self.width(), 48)
        self.top_bar_widget.raise_()

        self._hud_opacity = 1.0
        self._wake_hud()
        self._attach_mouse_tracking_and_filter()
        self.top_bar_widget.setVisible(True)
        self._top_opacity_eff.setOpacity(1.0)
        self._top_opacity_eff.setEnabled(False)
        self.artwork_view.controls_opacity = 1.0

        self._last_frame_t = time.perf_counter()
        if hasattr(self, "fluid_shader"):
            self.fluid_shader.setGeometry(self.rect())
            self.fluid_shader.lower()
            if not self.fluid_shader._init_failed:
                self.fluid_shader.show()
                self.fluid_shader.start_animation()
        if not self._render_timer.isActive():
            self._render_timer.start()

    def set_dolby_atmos_active(self, active: bool, actual_channels: int = 2) -> None:
        self.artwork_view.dolby_atmos_active = bool(active)
        self.artwork_view.actual_channels = int(actual_channels)
        self.artwork_view.update()

    def exit_immersive(self) -> None:
        if hasattr(self, "fluid_shader"):
            self.fluid_shader.stop_animation()
        self._render_timer.stop()
        self._idle_timer.stop()
        self.unsetCursor()
        self.hide()
        self.closed.emit()

    def update_track(self, track: Optional[NordicTrack]) -> None:
        self.current_track = track
        self._last_aurora_ms = -1000
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
            if hasattr(self, "fluid_shader"):
                self.fluid_shader.set_palette(c1, c2, c3, c4, c_base)
                self.fluid_shader.set_cover(track.get_cover_pixmap())
        else:
            self.lyrics_view.set_lyrics([], offset=0.0)

    def update_play_state(self, is_playing: bool) -> None:
        self.is_playing = is_playing
        self.artwork_view.set_playing_state(is_playing)
        self.lyrics_view.is_playing = is_playing

    def update_time(self, current_time: float, duration: float) -> None:
        self.artwork_view.update_time(current_time, duration)
        self.lyrics_view.sync_time(current_time)

    def update_spectrum(self, bands: List[float]) -> None:
        if bands:
            # 提取 20Hz~120Hz Sub-Bass 超重低音 / 底鼓频段 (前 4 频段)
            low_energy = sum(bands[:4]) / 4.0
            # 非对称声学包络检波：快起 (Attack 0.48 瞬时捕捉鼓点下潜) 慢释 (Decay 0.09 呼吸式平稳回弹)
            if low_energy > self._audio_energy:
                self._audio_energy += (low_energy - self._audio_energy) * 0.48
            else:
                self._audio_energy += (low_energy - self._audio_energy) * 0.09
            self.artwork_view.audio_energy = self._audio_energy
            if hasattr(self, "fluid_shader"):
                self.fluid_shader.set_energy(self._audio_energy)

    def update_play_mode(self, mode: str) -> None:
        self.play_mode = mode
        self.artwork_view.set_play_mode(mode)

    def update_volume(self, vol: float) -> None:
        self.volume = vol
        self.artwork_view.set_volume(vol)

    def set_volume(self, vol: float) -> None:
        self.update_volume(vol)

    def _on_idle_timeout(self) -> None:
        if not (self.artwork_view.is_dragging_progress or self.artwork_view.is_dragging_volume):
            self._hud_target_opacity = 0.0
            self.setCursor(Qt.CursorShape.BlankCursor)

    def resizeEvent(self, event: QResizeEvent) -> None:
        super().resizeEvent(event)
        if hasattr(self, "fluid_shader"):
            self.fluid_shader.setGeometry(self.rect())
            self.fluid_shader.lower()
        if hasattr(self, "top_bar_widget"):
            self.top_bar_widget.setGeometry(0, 26, self.width(), 48)
            self.top_bar_widget.raise_()

    def showEvent(self, event: QShowEvent) -> None:
        super().showEvent(event)
        if hasattr(self, "fluid_shader"):
            self.fluid_shader.setGeometry(self.rect())
            self.fluid_shader.lower()
            self.fluid_shader.start_animation()

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        self._wake_hud()
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
            target = max(0.0, self.artwork_view.current_time - 5.0)
            self.seekTimeRequested.emit(target)
            event.accept()
            return
        if key == Qt.Key.Key_Right:
            target = min(self.artwork_view.duration, self.artwork_view.current_time + 5.0)
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

    def _vsync_driven(self) -> bool:
        return (
            self.target_fps == 120
            and hasattr(self, "fluid_shader")
            and self.fluid_shader.gpu_active
        )

    def _on_frame_swapped(self) -> None:
        """GPU 一帧交换完成（垂直同步节拍）→ 立即推进下一帧，形成与显示器刷新对齐的帧循环。"""
        if not self._render_timer.isActive() or not self._vsync_driven():
            return
        since = time.perf_counter() - self._last_frame_t
        if since < 0.004:
            # 驱动未真正阻塞垂直同步时防止空转烧 GPU：最多约 250Hz
            QTimer.singleShot(max(1, int((0.004 - since) * 1000)), self._on_frame_swapped)
            return
        self._on_animation_frame()

    def _on_render_timer(self) -> None:
        # 垂直同步驱动正常工作时（最近一帧很新）定时器不重复推帧，仅在帧循环停摆时兜底
        if self._vsync_driven() and (time.perf_counter() - self._last_frame_t) < 0.03:
            return
        self._on_animation_frame()

    def _on_animation_frame(self) -> None:
        now_t = time.perf_counter()
        dt = max(0.001, min(0.05, now_t - self._last_frame_t))
        self._last_frame_t = now_t

        # 色彩渐变插值平滑推进 (无级指数阻尼，与帧率解耦)
        col_rate = min(1.0, 1.0 - math.exp(-3.5 * dt))
        self._col1_cur = self._interp_color(self._col1_cur, self._col1_target, col_rate)
        self._col2_cur = self._interp_color(self._col2_cur, self._col2_target, col_rate)
        self._col3_cur = self._interp_color(self._col3_cur, self._col3_target, col_rate)
        self._col4_cur = self._interp_color(self._col4_cur, self._col4_target, col_rate)
        self._col_base_cur = self._interp_color(self._col_base_cur, self._col_base_target, col_rate)

        # 漂浮星芒柔光微粒物理移动
        for m in self._motes:
            m.x = (m.x + m.speed_x * dt) % 1.0
            m.y = (m.y + m.speed_y * dt) % 1.0

        # 唱片与歌词物理弹簧动力学推进 (120 FPS 原生高刷新率无抖动平滑推进)
        self.artwork_view.step_physics(dt)
        self.lyrics_view.step_physics(dt)

        # 悬浮 HUD 与封面下置控制栏透明度平滑过渡 (非过渡期停用 QGraphicsOpacityEffect 以消除离屏 FBO 开销)
        if abs(self._hud_target_opacity - self._hud_opacity) > 0.005:
            hud_rate = min(1.0, 1.0 - math.exp(-9.0 * dt))
            self._hud_opacity += (self._hud_target_opacity - self._hud_opacity) * hud_rate
            if self._hud_opacity <= 0.01:
                self._hud_opacity = 0.0
                self.top_bar_widget.setVisible(False)
                self._top_opacity_eff.setEnabled(False)
            elif self._hud_opacity >= 0.99:
                self._hud_opacity = 1.0
                if not self.top_bar_widget.isVisible():
                    self.top_bar_widget.setVisible(True)
                self._top_opacity_eff.setEnabled(False)
            else:
                if not self.top_bar_widget.isVisible():
                    self.top_bar_widget.setVisible(True)
                if not self._top_opacity_eff.isEnabled():
                    self._top_opacity_eff.setEnabled(True)
                self._top_opacity_eff.setOpacity(self._hud_opacity)
        self.artwork_view.controls_opacity = self._hud_opacity

        # 极光慢速等离子流背景限频 ~30Hz 更新（当 GPU Fluid 着色器未激活时作为纯 CPU 降级保底）
        has_gpu = hasattr(self, "fluid_shader") and self.fluid_shader.gpu_active
        if not has_gpu:
            now_ms = self._clock.elapsed()
            if self._aurora_pixmap is None or (now_ms - self._last_aurora_ms) >= 32:
                self._last_aurora_ms = now_ms
                self._render_aurora_buffer(240, 135)

        # GPU 背景与主窗口在同一帧内一起标脏，合成一次、交换一次
        if hasattr(self, "fluid_shader"):
            self.fluid_shader.advance(dt)
        self.update()

    def _render_aurora_buffer(self, bw: int, bh: int) -> None:
        """5 核流动等离子体极光流体动力学渲染（含预烘焙午夜天鹅绒暗角，转存 GPU QPixmap）"""
        if self._aurora_buffer is None or self._aurora_buffer.width() != bw or self._aurora_buffer.height() != bh:
            self._aurora_buffer = QImage(bw, bh, QImage.Format.Format_RGB32)

        p = QPainter(self._aurora_buffer)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.fillRect(0, 0, bw, bh, self._col_base_cur)

        t = self._clock.elapsed() / 1000.0
        # Sub-Bass 柔光呼吸 (低频能量联动)
        energy_boost = self._audio_energy * 0.36
        core_alpha_boost = int(round(35 * self._audio_energy))

        # 1号极光核 (左上方有机流动)
        c1 = QColor(self._col1_cur)
        c1.setAlpha(min(255, 185 + core_alpha_boost))
        cx1 = bw * (0.26 + 0.16 * math.sin(t * 0.23))
        cy1 = bh * (0.30 + 0.14 * math.cos(t * 0.19))
        r1 = min(bw, bh) * (0.68 + 0.08 * math.sin(t * 0.31) + energy_boost)
        g1 = QRadialGradient(QPointF(cx1, cy1), r1)
        g1.setColorAt(0.0, c1)
        g1.setColorAt(0.65, QColor(c1.red(), c1.green(), c1.blue(), min(255, 60 + core_alpha_boost // 2)))
        g1.setColorAt(1.0, Qt.GlobalColor.transparent)
        p.fillRect(0, 0, bw, bh, g1)

        # 2号极光核 (右下方弥散)
        c2 = QColor(self._col2_cur)
        c2.setAlpha(min(255, 170 + core_alpha_boost))
        cx2 = bw * (0.78 + 0.14 * math.cos(t * 0.21))
        cy2 = bh * (0.68 + 0.15 * math.sin(t * 0.27))
        r2 = min(bw, bh) * (0.62 + 0.06 * math.cos(t * 0.25) + energy_boost * 0.88)
        g2 = QRadialGradient(QPointF(cx2, cy2), r2)
        g2.setColorAt(0.0, c2)
        g2.setColorAt(0.60, QColor(c2.red(), c2.green(), c2.blue(), min(255, 50 + core_alpha_boost // 2)))
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

        # 5. 预烘焙午夜天鹅绒深色暗角 (避免每帧在 1080p/4K 全屏画布上实时光栅化径向渐变)
        vignette = QRadialGradient(QPointF(bw * 0.5, bh * 0.5), max(bw, bh) * 0.75)
        vignette.setColorAt(0.0, QColor(8, 12, 16, 70))
        vignette.setColorAt(0.60, QColor(8, 12, 16, 120))
        vignette.setColorAt(1.0, QColor(5, 7, 10, 195))
        p.fillRect(0, 0, bw, bh, vignette)

        p.end()
        self._aurora_pixmap = QPixmap.fromImage(self._aurora_buffer)

    def paintEvent(self, event) -> None:
        w, h = self.width(), self.height()
        if w < 10 or h < 10:
            return

        has_gpu = hasattr(self, "fluid_shader") and self.fluid_shader.gpu_active
        if has_gpu:
            return

        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)

        # 1. 绘制预缓存极光+暗角纹理 (仅当 GPU Fluid 着色器未激活时作为纯 CPU 降级保底)
        if self._aurora_pixmap is None:
            self._render_aurora_buffer(240, 135)
        if self._aurora_pixmap is not None:
            p.drawPixmap(self.rect(), self._aurora_pixmap)

        # 1.5 绘制封面背后无界 Ambilight 动态氛围溢彩 (全屏画布渲染，彻底消除子控件边界裁切生硬分割线)
        if hasattr(self, "artwork_view"):
            av = self.artwork_view
            av_w = av.width()
            av_h = av.height()
            if av_w >= 100 and av_h >= 100:
                ctrl_h = 216.0
                max_sleeve = min(390.0, min(av_w * 0.74, av_h - ctrl_h - 80.0))
                sleeve_sz = int(max(240.0, max_sleeve))
                center_x_local = av_w * 0.40 if av.show_vinyl else av_w * 0.48
                total_content_h = sleeve_sz + ctrl_h
                sleeve_top_local = max(40.0, (av_h - total_content_h) * 0.52 + 36.0)
                center_y_local = sleeve_top_local + sleeve_sz / 2.0

                pos = av.mapTo(self, QPoint(0, 0))
                g_cx = float(pos.x()) + center_x_local
                g_cy = float(pos.y()) + center_y_local

                amb_radius = sleeve_sz * (0.86 + 0.28 * self._audio_energy)
                amb_glow = QRadialGradient(QPointF(g_cx, g_cy), amb_radius)
                c_glow = QColor(av.accent_color)
                c_glow.setAlpha(int(round(85 + 55 * self._audio_energy)))
                amb_glow.setColorAt(0.0, c_glow)
                amb_glow.setColorAt(0.50, QColor(c_glow.red(), c_glow.green(), c_glow.blue(), int(round(24 + 18 * self._audio_energy))))
                amb_glow.setColorAt(1.0, Qt.GlobalColor.transparent)
                p.setPen(Qt.PenStyle.NoPen)
                p.setBrush(amb_glow)
                p.drawEllipse(QPointF(g_cx, g_cy), amb_radius, amb_radius)

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
