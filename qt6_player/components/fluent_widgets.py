import math
from typing import List, Optional, Tuple, Callable
from PyQt6.QtCore import (
    Qt,
    QRectF,
    QPointF,
    QPoint,
    QSize,
    QVariantAnimation,
    QEasingCurve,
    pyqtSignal,
    QEvent,
)
from PyQt6.QtGui import (
    QPainter,
    QPen,
    QColor,
    QFont,
    QFontDatabase,
    QPainterPath,
    QMouseEvent,
    QKeyEvent,
)
from PyQt6.QtWidgets import (
    QWidget,
    QDialog,
    QPushButton,
    QLineEdit,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
    QGraphicsDropShadowEffect,
    QGraphicsOpacityEffect,
)

from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font



_FLUENT_ICON_FONT: Optional[str] = None


def get_fluent_icon_font_family() -> Optional[str]:
    global _FLUENT_ICON_FONT
    if _FLUENT_ICON_FONT is None:
        try:
            fams = QFontDatabase.families()
            if "Segoe Fluent Icons" in fams:
                _FLUENT_ICON_FONT = "Segoe Fluent Icons"
            elif "Segoe MDL2 Assets" in fams:
                _FLUENT_ICON_FONT = "Segoe MDL2 Assets"
            else:
                _FLUENT_ICON_FONT = ""
        except Exception:
            _FLUENT_ICON_FONT = ""
    return _FLUENT_ICON_FONT or None

def draw_nordic_icon(
    painter: QPainter,
    kind: str,
    rect: QRectF,
    color: QColor,
    stroke_w: float = 1.4,
) -> None:
    painter.save()
    painter.setRenderHint(QPainter.RenderHint.Antialiasing)
    cx, cy = rect.center().x(), rect.center().y()
    pen = QPen(color, stroke_w, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin)
    painter.setPen(pen)
    painter.setBrush(Qt.BrushStyle.NoBrush)

    if kind == "sidebar":
        # 圆角侧边栏分栏图标 (WinUI NavigationView 风格)
        box = QRectF(cx - 7.0, cy - 5.5, 14.0, 11.0)
        painter.drawRoundedRect(box, 2.2, 2.2)
        painter.drawLine(QPointF(cx - 2.2, cy - 5.5), QPointF(cx - 2.2, cy + 5.5))

    elif kind in ("heart_outline", "heart_filled"):
        path = QPainterPath()
        # 优雅几何心形贝塞尔曲线
        s = min(rect.width(), rect.height()) * 0.34
        path.moveTo(cx, cy + s * 0.88)
        path.cubicTo(cx - s * 1.35, cy + s * 0.10, cx - s * 1.15, cy - s * 0.95, cx - s * 0.52, cy - s * 0.95)
        path.cubicTo(cx - s * 0.18, cy - s * 0.95, cx, cy - s * 0.62, cx, cy - s * 0.42)
        path.cubicTo(cx, cy - s * 0.62, cx + s * 0.18, cy - s * 0.95, cx + s * 0.52, cy - s * 0.95)
        path.cubicTo(cx + s * 1.15, cy - s * 0.95, cx + s * 1.35, cy + s * 0.10, cx, cy + s * 0.88)
        path.closeSubpath()
        if kind == "heart_filled":
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(color)
            painter.drawPath(path)
        else:
            painter.drawPath(path)

    elif kind == "search":
        painter.drawEllipse(QPointF(cx - 1.2, cy - 1.2), 4.6, 4.6)
        painter.drawLine(QPointF(cx + 2.2, cy + 2.2), QPointF(cx + 5.8, cy + 5.8))

    elif kind == "plus":
        painter.drawLine(QPointF(cx - 5.0, cy), QPointF(cx + 5.0, cy))
        painter.drawLine(QPointF(cx, cy - 5.0), QPointF(cx, cy + 5.0))

    elif kind == "folder":
        path = QPainterPath()
        path.moveTo(cx - 6.5, cy - 4.5)
        path.lineTo(cx - 2.0, cy - 4.5)
        path.lineTo(cx - 0.5, cy - 2.8)
        path.lineTo(cx + 6.5, cy - 2.8)
        path.lineTo(cx + 6.5, cy + 4.8)
        path.lineTo(cx - 6.5, cy + 4.8)
        path.closeSubpath()
        painter.drawPath(path)

    elif kind == "music_file":
        painter.drawLine(QPointF(cx - 1.5, cy + 3.5), QPointF(cx - 1.5, cy - 5.0))
        painter.drawLine(QPointF(cx - 1.5, cy - 5.0), QPointF(cx + 4.5, cy - 3.5))
        painter.drawLine(QPointF(cx + 4.5, cy - 3.5), QPointF(cx + 4.5, cy + 2.0))
        painter.setBrush(color)
        painter.drawEllipse(QPointF(cx - 3.5, cy + 3.8), 2.0, 1.5)
        painter.drawEllipse(QPointF(cx + 2.5, cy + 2.3), 2.0, 1.5)

    elif kind == "lrc":
        box = QRectF(cx - 5.5, cy - 6.0, 11.0, 12.0)
        painter.drawRoundedRect(box, 2.0, 2.0)
        painter.drawLine(QPointF(cx - 3.0, cy - 2.2), QPointF(cx + 3.0, cy - 2.2))
        painter.drawLine(QPointF(cx - 3.0, cy + 0.8), QPointF(cx + 3.0, cy + 0.8))
        painter.drawLine(QPointF(cx - 3.0, cy + 3.6), QPointF(cx + 1.0, cy + 3.6))

    elif kind == "sparkle":
        # 四芒星词图匹配图标
        path = QPainterPath()
        r_out = 6.0
        r_in = 1.8
        for i in range(8):
            angle = i * math.pi / 4.0 - math.pi / 2.0
            r = r_out if i % 2 == 0 else r_in
            px = cx + math.cos(angle) * r
            py = cy + math.sin(angle) * r
            if i == 0:
                path.moveTo(px, py)
            else:
                path.lineTo(px, py)
        path.closeSubpath()
        painter.drawPath(path)

    elif kind in ("speaker", "volume", "sound"):
        path = QPainterPath()
        path.moveTo(cx - 5.0, cy - 2.5)
        path.lineTo(cx - 2.5, cy - 2.5)
        path.lineTo(cx + 1.2, cy - 5.2)
        path.lineTo(cx + 1.2, cy + 5.2)
        path.lineTo(cx - 2.5, cy + 2.5)
        path.lineTo(cx - 5.0, cy + 2.5)
        path.closeSubpath()
        painter.fillPath(path, color)
        painter.setPen(QPen(color, 1.3, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        painter.drawArc(QRectF(cx + 2.5, cy - 3.8, 3.8, 7.6), -45 * 16, 90 * 16)

    elif kind == "close":
        painter.drawLine(QPointF(cx - 4.2, cy - 4.2), QPointF(cx + 4.2, cy + 4.2))
        painter.drawLine(QPointF(cx + 4.2, cy - 4.2), QPointF(cx - 4.2, cy + 4.2))

    elif kind == "check":
        path = QPainterPath()
        path.moveTo(cx - 4.2, cy + 0.2)
        path.lineTo(cx - 1.2, cy + 3.2)
        path.lineTo(cx + 4.5, cy - 2.8)
        painter.drawPath(path)

    elif kind in ("settings", "gear"):
        fluent_fam = get_fluent_icon_font_family()
        if fluent_fam:
            painter.setFont(QFont(fluent_fam, 10))
            painter.setPen(color)
            painter.drawText(rect, int(Qt.AlignmentFlag.AlignCenter), "\uE713")
        else:
            path = QPainterPath()
            n = 6
            r_out = 5.8
            r_in = 4.2
            half_tooth = math.radians(12.0)
            for i in range(n):
                ang = i * (2 * math.pi / n) - math.pi / 2
                t_start = ang - half_tooth
                t_end = ang + half_tooth
                g_start = ang + half_tooth + math.radians(4.0)
                g_end = ang + (2 * math.pi / n) - half_tooth - math.radians(4.0)
                p_t1 = QPointF(cx + r_out * math.cos(t_start), cy + r_out * math.sin(t_start))
                p_t2 = QPointF(cx + r_out * math.cos(t_end), cy + r_out * math.sin(t_end))
                p_g1 = QPointF(cx + r_in * math.cos(g_start), cy + r_in * math.sin(g_start))
                p_g2 = QPointF(cx + r_in * math.cos(g_end), cy + r_in * math.sin(g_end))
                if i == 0:
                    path.moveTo(p_t1)
                else:
                    path.lineTo(p_t1)
                path.lineTo(p_t2)
                path.lineTo(p_g1)
                path.lineTo(p_g2)
            path.closeSubpath()
            painter.drawPath(path)
            painter.drawEllipse(QPointF(cx, cy), 1.9, 1.9)

    elif kind == "eq_sliders":
        # 三推子均衡器图标
        for idx, offset_x in enumerate((-4.5, 0.0, 4.5)):
            x = cx + offset_x
            painter.drawLine(QPointF(x, cy - 5.5), QPointF(x, cy + 5.5))
            knob_y = cy + (-2.2 if idx == 0 else (2.0 if idx == 1 else -1.0))
            painter.setBrush(color)
            painter.drawEllipse(QPointF(x, knob_y), 1.7, 1.7)

    elif kind == "desktop_lyric":
        box = QRectF(cx - 6.5, cy - 4.5, 13.0, 9.0)
        painter.drawRoundedRect(box, 2.0, 2.0)
        painter.drawLine(QPointF(cx - 3.8, cy - 1.0), QPointF(cx + 3.8, cy - 1.0))
        painter.drawLine(QPointF(cx - 2.4, cy + 1.8), QPointF(cx + 2.4, cy + 1.8))

    elif kind == "sun":
        painter.drawEllipse(QPointF(cx, cy), 3.2, 3.2)
        for i in range(8):
            ang = i * math.pi / 4.0
            painter.drawLine(
                QPointF(cx + math.cos(ang) * 4.8, cy + math.sin(ang) * 4.8),
                QPointF(cx + math.cos(ang) * 6.4, cy + math.sin(ang) * 6.4),
            )

    elif kind == "moon":
        path = QPainterPath()
        path.addEllipse(QPointF(cx, cy), 5.2, 5.2)
        cut = QPainterPath()
        cut.addEllipse(QPointF(cx + 2.6, cy - 2.0), 4.6, 4.6)
        crescent = path.subtracted(cut)
        painter.drawPath(crescent)

    elif kind == "trash":
        painter.drawLine(QPointF(cx - 5.5, cy - 3.5), QPointF(cx + 5.5, cy - 3.5))
        painter.drawRoundedRect(QRectF(cx - 4.2, cy - 3.5, 8.4, 8.8), 1.2, 1.2)
        painter.drawLine(QPointF(cx - 2.0, cy - 5.2), QPointF(cx + 2.0, cy - 5.2))

    elif kind == "chevron_down":
        path = QPainterPath()
        path.moveTo(cx - 3.8, cy - 1.5)
        path.lineTo(cx, cy + 2.2)
        path.lineTo(cx + 3.8, cy - 1.5)
        painter.drawPath(path)

    elif kind == "edit":
        path = QPainterPath()
        path.moveTo(cx - 4.8, cy + 4.8)
        path.lineTo(cx - 4.0, cy + 1.6)
        path.lineTo(cx + 2.4, cy - 4.8)
        path.lineTo(cx + 4.8, cy - 2.4)
        path.lineTo(cx - 1.6, cy + 4.0)
        path.closeSubpath()
        painter.drawPath(path)

    elif kind == "mini_player":
        outer = QRectF(cx - 6.5, cy - 4.8, 13.0, 9.6)
        painter.drawRoundedRect(outer, 2.0, 2.0)
        inner = QRectF(cx + 0.2, cy - 0.2, 5.2, 4.0)
        painter.setBrush(color)
        painter.drawRoundedRect(inner, 1.0, 1.0)

    elif kind == "fullscreen":
        s = 5.0
        painter.drawLine(QPointF(cx - s, cy - s + 3.0), QPointF(cx - s, cy - s))
        painter.drawLine(QPointF(cx - s, cy - s), QPointF(cx - s + 3.0, cy - s))
        painter.drawLine(QPointF(cx + s - 3.0, cy - s), QPointF(cx + s, cy - s))
        painter.drawLine(QPointF(cx + s, cy - s), QPointF(cx + s, cy - s + 3.0))
        painter.drawLine(QPointF(cx - s, cy + s - 3.0), QPointF(cx - s, cy + s))
        painter.drawLine(QPointF(cx - s, cy + s), QPointF(cx - s + 3.0, cy + s))
        painter.drawLine(QPointF(cx + s - 3.0, cy + s), QPointF(cx + s, cy + s))
        painter.drawLine(QPointF(cx + s, cy + s), QPointF(cx + s, cy + s - 3.0))

    elif kind == "exit_fullscreen":
        s = 5.0
        painter.drawLine(QPointF(cx - s, cy - 2.0), QPointF(cx - s + 3.0, cy - 2.0))
        painter.drawLine(QPointF(cx - s + 3.0, cy - 2.0), QPointF(cx - s + 3.0, cy - s))
        painter.drawLine(QPointF(cx + s, cy - 2.0), QPointF(cx + s - 3.0, cy - 2.0))
        painter.drawLine(QPointF(cx + s - 3.0, cy - 2.0), QPointF(cx + s - 3.0, cy - s))
        painter.drawLine(QPointF(cx - s, cy + 2.0), QPointF(cx - s + 3.0, cy + 2.0))
        painter.drawLine(QPointF(cx - s + 3.0, cy + 2.0), QPointF(cx - s + 3.0, cy + s))
        painter.drawLine(QPointF(cx + s, cy + 2.0), QPointF(cx + s - 3.0, cy + 2.0))
        painter.drawLine(QPointF(cx + s - 3.0, cy + 2.0), QPointF(cx + s - 3.0, cy + s))

    painter.restore()


class WinUISegmentedControl(QWidget):
    """带有丝滑滑动胶囊指示器 (Sliding Pill Indicator) 的分段切换组件"""

    currentIndexChanged = pyqtSignal(int)

    def __init__(self, items: List[str], parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.items = list(items)
        self.current_index: int = 0
        self.hovered_index: int = -1
        self.pill_pos: float = 0.0
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.backdrop_material: str = "mica"
        self.is_translucent_backdrop: bool = True
        self.setFixedHeight(32)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMouseTracking(True)

        self._anim = QVariantAnimation(self)
        self._anim.setDuration(280)
        seg_curve = QEasingCurve(QEasingCurve.Type.OutBack)
        seg_curve.setOvershoot(1.16)
        self._anim.setEasingCurve(seg_curve)
        self._anim.valueChanged.connect(self._on_anim_val)

    def set_backdrop_material(self, material: str) -> None:
        self.backdrop_material = str(material)
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        self.update()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def set_backdrop_material(self, material: str) -> None:
        self.backdrop_material = str(material)
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        self.update()

    def _on_anim_val(self, val: float) -> None:
        self.pill_pos = float(val)
        self.update()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def set_item_text(self, index: int, text: str) -> None:
        if 0 <= index < len(self.items):
            self.items[index] = text
            self.update()

    def clear_selection(self) -> None:
        self._anim.stop()
        self.current_index = -1
        self.update()

    def set_current_index(self, index: int, animate: bool = True) -> None:
        if not (0 <= index < len(self.items)):
            return
        was_unselected = self.current_index < 0
        changed = index != self.current_index
        self.current_index = index
        self._anim.stop()
        if animate and not was_unselected:
            self._anim.setStartValue(self.pill_pos)
            self._anim.setEndValue(float(index))
            self._anim.start()
        else:
            self.pill_pos = float(index)
            self.update()
        if changed:
            self.currentIndexChanged.emit(index)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if not self.items:
            return
        seg_w = self.width() / len(self.items)
        idx = max(0, min(len(self.items) - 1, int(event.position().x() // max(1.0, seg_w))))
        if idx != self.hovered_index:
            self.hovered_index = idx
            self.update()
        super().mouseMoveEvent(event)

    def leaveEvent(self, event) -> None:
        self.hovered_index = -1
        self.update()
        super().leaveEvent(event)

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton and self.items:
            seg_w = self.width() / len(self.items)
            idx = max(0, min(len(self.items) - 1, int(event.position().x() // max(1.0, seg_w))))
            self.set_current_index(idx, animate=True)
        super().mousePressEvent(event)

    def sizeHint(self) -> QSize:
        if not self.items:
            return QSize(80, 32)
        fm = self.fontMetrics()
        w = sum(fm.horizontalAdvance(item) + 24 for item in self.items) + 12
        return QSize(max(80, w), 32)

    def minimumSizeHint(self) -> QSize:
        return self.sizeHint()

    def paintEvent(self, event) -> None:
        if not self.items:
            return
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        is_dusk = self.theme.name == "dusk"
        is_enabled = self.isEnabled()
        outer_rect = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        outer_path = QPainterPath()
        outer_path.addRoundedRect(outer_rect, 7.0, 7.0)

        if is_dusk:
            if self.is_translucent_backdrop:
                track_fill = QColor(255, 255, 255, 14)
                track_border = QColor(255, 255, 255, 25)
            else:
                track_fill = self.theme.oat_surface
                track_border = self.theme.stroke_outer
        else:
            # 浅色模式下：采用微凹陷槽位质感与暗烟色外描边，彻底解决在白色/云母底色上边界发虚不可读问题
            track_fill = QColor(0, 0, 0, 14 if is_enabled else 10)
            track_border = QColor(0, 0, 0, 28 if is_enabled else 18)

        painter.fillPath(outer_path, track_fill)
        painter.setPen(QPen(track_border, 1.0))
        painter.drawPath(outer_path)

        n = len(self.items)
        pad = 3.0
        usable_w = self.width() - pad * 2.0
        seg_w = usable_w / n

        # 悬浮未选中的段落微光提示
        if is_enabled and 0 <= self.hovered_index < n and self.hovered_index != self.current_index:
            hov_x = pad + self.hovered_index * seg_w
            hov_rect = QRectF(hov_x, pad, seg_w, self.height() - pad * 2.0)
            hov_path = QPainterPath()
            hov_path.addRoundedRect(hov_rect, 5.0, 5.0)
            if is_dusk:
                hov_fill = (
                    QColor(255, 255, 255, 20)
                    if self.is_translucent_backdrop
                    else self.theme.subtle_hover
                )
            else:
                hov_fill = QColor(0, 0, 0, 12)
            painter.fillPath(hov_path, hov_fill)

        # 滑动胶囊选中背景
        if self.current_index >= 0:
            pill_x = pad + self.pill_pos * seg_w
            pill_rect = QRectF(pill_x, pad, seg_w, self.height() - pad * 2.0)
            pill_path = QPainterPath()
            pill_path.addRoundedRect(pill_rect, 5.0, 5.0)

            if is_dusk:
                if self.is_translucent_backdrop:
                    pill_fill = QColor(255, 255, 255, 38)
                    pill_border = QColor(255, 255, 255, 60)
                else:
                    pill_fill = self.theme.card_elevated
                    pill_border = self.theme.sage_border
            else:
                # 浅色模式下：带有细腻漫反射下沉投影与清晰边界的实体白底胶囊
                shadow_rect = pill_rect.translated(0.0, 0.8)
                shadow_path = QPainterPath()
                shadow_path.addRoundedRect(shadow_rect, 5.0, 5.0)
                painter.fillPath(shadow_path, QColor(0, 0, 0, 14 if is_enabled else 8))

                pill_fill = QColor(255, 255, 255, 250 if is_enabled else 210)
                pill_border = QColor(0, 0, 0, 32 if is_enabled else 20)

            painter.fillPath(pill_path, pill_fill)
            painter.setPen(QPen(pill_border, 1.0))
            painter.drawPath(pill_path)

            # 绘制底部鼠尾草绿短横线指示条
            bar_w = min(20.0, seg_w * 0.32)
            bar_rect = QRectF(pill_rect.center().x() - bar_w / 2.0, pill_rect.bottom() - 2.5, bar_w, 2.0)
            bar_path = QPainterPath()
            bar_path.addRoundedRect(bar_rect, 1.0, 1.0)
            bar_color = self.theme.sage_accent if is_enabled else self.theme.slate_faint
            painter.fillPath(bar_path, bar_color)

        # 绘制各分段文本
        for i, label in enumerate(self.items):
            item_rect = QRectF(pad + i * seg_w, 0.0, seg_w, self.height())
            is_cur = i == self.current_index
            painter.setFont(
                get_nordic_font(12, QFont.Weight.DemiBold if is_cur else QFont.Weight.Medium)
            )
            if not is_enabled:
                text_col = self.theme.slate_dark if is_cur else self.theme.slate_faint
            elif is_dusk:
                if is_cur:
                    text_col = self.theme.charcoal
                elif i == self.hovered_index:
                    text_col = self.theme.slate_dark
                else:
                    text_col = self.theme.slate_muted
            else:
                # 浅色模式下：未选中选项使用高对比度深石板灰，绝不发灰泛白
                if is_cur:
                    text_col = QColor("#14171A")
                elif i == self.hovered_index:
                    text_col = QColor("#181B1E")
                else:
                    text_col = QColor("#3A4048")
            painter.setPen(text_col)
            painter.drawText(item_rect, int(Qt.AlignmentFlag.AlignCenter), label)


class WinUIToggleSwitch(QWidget):
    """WinUI 3 风格平滑滑动开关"""
    toggled = pyqtSignal(bool)

    def __init__(
        self,
        checked: bool = False,
        text_on: str = "已开启",
        text_off: str = "已关闭",
        parent: Optional[QWidget] = None,
    ):
        super().__init__(parent)
        self._checked: bool = bool(checked)
        self.text_on = text_on
        self.text_off = text_off
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self._thumb_pos: float = 1.0 if self._checked else 0.0
        self._is_hovered: bool = False

        self.setFixedHeight(24)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMouseTracking(True)

        self._anim = QVariantAnimation(self)
        self._anim.setDuration(180)
        self._anim.setEasingCurve(QEasingCurve.Type.InOutCubic)
        self._anim.valueChanged.connect(self._on_anim_val)

    def _on_anim_val(self, val: float) -> None:
        self._thumb_pos = float(val)
        self.update()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def isChecked(self) -> bool:
        return self._checked

    def setChecked(self, checked: bool, animate: bool = True) -> None:
        checked = bool(checked)
        if self._checked == checked and not self._anim.state():
            return
        self._checked = checked
        self._anim.stop()
        if animate:
            self._anim.setStartValue(self._thumb_pos)
            self._anim.setEndValue(1.0 if self._checked else 0.0)
            self._anim.start()
        else:
            self._thumb_pos = 1.0 if self._checked else 0.0
            self.update()
        self.toggled.emit(self._checked)

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton and self.isEnabled():
            self.setChecked(not self._checked, animate=True)
        super().mousePressEvent(event)

    def enterEvent(self, event) -> None:
        self._is_hovered = True
        self.update()
        super().enterEvent(event)

    def leaveEvent(self, event) -> None:
        self._is_hovered = False
        self.update()
        super().leaveEvent(event)

    def sizeHint(self) -> QSize:
        label = self.text_on if self._checked else self.text_off
        fm = self.fontMetrics()
        w = 44 + (fm.horizontalAdvance(label) + 8 if label else 0)
        return QSize(w, 24)

    def minimumSizeHint(self) -> QSize:
        return self.sizeHint()

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        track_w = 38.0
        track_h = 20.0
        track_y = (self.height() - track_h) / 2.0
        track_rect = QRectF(1.0, track_y, track_w, track_h)

        track_path = QPainterPath()
        track_path.addRoundedRect(track_rect, 10.0, 10.0)

        t = self._thumb_pos
        is_dusk = self.theme.name == "dusk"
        if self.isEnabled():
            if t > 0.01:
                accent = QColor(self.theme.sage_accent)
                fill_col = QColor(accent)
                fill_col.setAlpha(int(220 * t + 35 * (1.0 - t)))
                painter.fillPath(track_path, fill_col)
                border_col = QColor(accent)
                border_col.setAlpha(int(255 * t + 70 * (1.0 - t)))
                painter.setPen(QPen(border_col, 1.2))
            else:
                if is_dusk:
                    painter.fillPath(track_path, self.theme.oat_surface)
                    painter.setPen(QPen(self.theme.stroke_outer if not self._is_hovered else self.theme.slate_muted, 1.2))
                else:
                    painter.fillPath(track_path, QColor(0, 0, 0, 14 if not self._is_hovered else 22))
                    painter.setPen(QPen(QColor(0, 0, 0, 42 if not self._is_hovered else 65), 1.2))
        else:
            painter.fillPath(track_path, self.theme.oat_surface)
            painter.setPen(QPen(self.theme.stroke_inner, 1.0))

        painter.drawPath(track_path)

        thumb_d = 14.0
        thumb_r = thumb_d / 2.0
        min_cx = 1.0 + 3.0 + thumb_r
        max_cx = 1.0 + track_w - 3.0 - thumb_r
        cx = min_cx + t * (max_cx - min_cx)
        cy = track_y + track_h / 2.0

        thumb_rect = QRectF(cx - thumb_r, cy - thumb_r, thumb_d, thumb_d)
        thumb_path = QPainterPath()
        thumb_path.addEllipse(thumb_rect)

        if self.isEnabled():
            if t > 0.5:
                painter.fillPath(thumb_path, self.theme.mist_white if is_dusk else QColor("#FFFFFF"))
            else:
                painter.fillPath(
                    thumb_path,
                    self.theme.charcoal if self._is_hovered else (self.theme.slate_dark if is_dusk else QColor("#5A626E"))
                )
        else:
            painter.fillPath(thumb_path, self.theme.slate_faint)

        label = self.text_on if self._checked else self.text_off
        if label:
            painter.setFont(get_nordic_font(11, QFont.Weight.Medium))
            if is_dusk:
                text_col = self.theme.charcoal if self._checked else self.theme.slate_muted
            else:
                text_col = QColor("#181B1E") if self._checked else QColor("#4A5058")
            painter.setPen(text_col)
            text_rect = QRectF(track_w + 8.0, 0.0, self.width() - track_w - 8.0, self.height())
            painter.drawText(text_rect, int(Qt.AlignmentFlag.AlignVCenter | Qt.AlignmentFlag.AlignLeft), label)


class WinUISearchBox(QWidget):
    """自定义绘制的 WinUI 3 搜索输入框：带矢量放大镜、焦点平滑高亮底边与矢量清除按钮"""

    textChanged = pyqtSignal(str)
    returnPressed = pyqtSignal()

    def __init__(self, placeholder: str = "搜索…", parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self._focus_progress: float = 0.0
        self.backdrop_material: str = "mica"
        self.is_translucent_backdrop: bool = True
        self.setFixedHeight(32)

        self._focus_anim = QVariantAnimation(self)
        self._focus_anim.setDuration(200)
        self._focus_anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._focus_anim.valueChanged.connect(self._on_focus_anim)

        layout = QHBoxLayout(self)
        layout.setContentsMargins(28, 1, 26, 1)
        layout.setSpacing(0)

        self.edit = QLineEdit(self)
        self.edit.setPlaceholderText(placeholder)
        self.edit.setFrame(False)
        self.edit.setFont(get_nordic_font(12))
        self.edit.textChanged.connect(self._on_text_changed)
        self.edit.returnPressed.connect(self.returnPressed.emit)
        self.edit.installEventFilter(self)
        layout.addWidget(self.edit)

    def set_placeholder(self, text: str) -> None:
        self.edit.setPlaceholderText(str(text))

    def clear(self) -> None:
        self.edit.clear()

    def set_backdrop_material(self, material: str) -> None:
        self.backdrop_material = str(material)
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        self.update()

    def _on_focus_anim(self, val: float) -> None:
        self._focus_progress = float(val)
        self.update()

    def _on_text_changed(self, txt: str) -> None:
        self.textChanged.emit(txt)
        self.update()

    def text(self) -> str:
        return self.edit.text()

    def setText(self, txt: str) -> None:
        self.edit.setText(txt)

    def eventFilter(self, obj, event: QEvent) -> bool:
        if obj == self.edit:
            if event.type() == QEvent.Type.FocusIn:
                self._focus_anim.stop()
                self._focus_anim.setStartValue(self._focus_progress)
                self._focus_anim.setEndValue(1.0)
                self._focus_anim.start()
            elif event.type() == QEvent.Type.FocusOut:
                self._focus_anim.stop()
                self._focus_anim.setStartValue(self._focus_progress)
                self._focus_anim.setEndValue(0.0)
                self._focus_anim.start()
        return super().eventFilter(obj, event)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        fg = theme.charcoal.name()
        placeholder_col = theme.slate_faint.name() if theme.name == "dusk" else "#68707B"
        self.edit.setStyleSheet(
            f"""
            QLineEdit {{
                background: transparent;
                color: {fg};
                border: none;
                selection-background-color: {theme.sage_accent.name()};
                selection-color: #FAF9F6;
            }}
            """
        )
        pal = self.edit.palette()
        pal.setColor(pal.ColorRole.PlaceholderText, QColor(placeholder_col))
        self.edit.setPalette(pal)
        self.update()

    def _clear_rect(self) -> QRectF:
        return QRectF(self.width() - 24.0, self.height() / 2.0 - 9.0, 18.0, 18.0)

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton and self.edit.text():
            if self._clear_rect().contains(event.position()):
                self.edit.clear()
                event.accept()
                return
        self.edit.setFocus()
        super().mousePressEvent(event)

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        rect = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        path = QPainterPath()
        path.addRoundedRect(rect, 6.5, 6.5)

        is_dusk = self.theme.name == "dusk"
        if is_dusk:
            if self.is_translucent_backdrop:
                bg = QColor(255, 255, 255, int(16 + 22 * self._focus_progress))
                border_col = self.theme.sage_border if self._focus_progress > 0.3 else QColor(255, 255, 255, 30)
            else:
                bg = self.theme.card_elevated
                border_col = (
                    self.theme.sage_border if self._focus_progress > 0.3 else self.theme.stroke_outer
                )
        else:
            # 浅色模式下：高白度底色配合清晰暗烟色描边与聚焦鼠尾草绿动效
            bg = QColor(255, 255, 255, int(195 + 60 * self._focus_progress))
            border_col = self.theme.sage_accent if self._focus_progress > 0.3 else QColor(0, 0, 0, 28)

        painter.fillPath(path, bg)
        painter.setPen(QPen(border_col, 1.0))
        painter.drawPath(path)

        # 底部 WinUI 3 焦点鼠尾草绿动效指示线
        if self._focus_progress > 0.01:
            line_w = (self.width() - 14.0) * self._focus_progress
            cx = self.width() / 2.0
            accent_rect = QRectF(cx - line_w / 2.0, self.height() - 2.0, line_w, 1.8)
            accent_path = QPainterPath()
            accent_path.addRoundedRect(accent_rect, 0.9, 0.9)
            painter.fillPath(accent_path, self.theme.sage_accent)

        # 左侧搜索图标
        icon_col = self.theme.sage_accent if self._focus_progress > 0.3 else self.theme.slate_muted
        draw_nordic_icon(
            painter,
            "search",
            QRectF(7.0, self.height() / 2.0 - 8.0, 16.0, 16.0),
            icon_col,
            stroke_w=1.35,
        )

        # 右侧清除按钮
        if self.edit.text():
            draw_nordic_icon(
                painter,
                "close",
                self._clear_rect(),
                self.theme.slate_muted,
                stroke_w=1.2,
            )


class WinUICheckChip(QPushButton):
    """现代 WinUI 3 矢量勾选胶囊开关（替代老旧的 QCheckBox）"""

    toggledByUser = pyqtSignal(bool)

    def __init__(self, text: str, checked: bool = True, parent: Optional[QWidget] = None):
        super().__init__(text, parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self._checked = checked
        self.backdrop_material: str = "mica"
        self.is_translucent_backdrop: bool = True
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setFixedHeight(30)
        self.setFont(get_nordic_font(12, QFont.Weight.Medium))
        fm = self.fontMetrics()
        self.setFixedWidth(fm.horizontalAdvance(text) + 36)

    def set_backdrop_material(self, material: str) -> None:
        self.backdrop_material = str(material)
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        self.update()

    def isChecked(self) -> bool:
        return self._checked

    def setChecked(self, checked: bool) -> None:
        self._checked = bool(checked)
        self.update()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self._checked = not self._checked
            self.toggledByUser.emit(self._checked)
            self.update()
            event.accept()
            return
        super().mousePressEvent(event)

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        rect = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        path = QPainterPath()
        path.addRoundedRect(rect, 6.0, 6.0)

        is_dusk = self.theme.name == "dusk"
        if self._checked:
            bg = QColor(140, 161, 150, 60) if is_dusk else QColor(110, 130, 119, 52)
            border = QColor(140, 161, 150, 130) if is_dusk else QColor(110, 130, 119, 120)
            fg = self.theme.charcoal
        elif self.underMouse():
            if self.is_translucent_backdrop:
                bg = QColor(255, 255, 255, 36 if is_dusk else 180)
                border = QColor(255, 255, 255, 55 if is_dusk else 220)
            else:
                bg = self.theme.subtle_hover
                border = self.theme.stroke_outer
            fg = self.theme.charcoal
        else:
            if self.is_translucent_backdrop:
                bg = QColor(255, 255, 255, 18 if is_dusk else 115)
                border = QColor(255, 255, 255, 32 if is_dusk else 150)
            else:
                bg = self.theme.oat_surface
                border = self.theme.stroke_outer
            fg = self.theme.slate_muted

        painter.fillPath(path, bg)
        painter.setPen(QPen(border, 1.0))
        painter.drawPath(path)

        # 左侧圆角勾选小框
        chk_rect = QRectF(9.0, self.height() / 2.0 - 7.0, 14.0, 14.0)
        chk_path = QPainterPath()
        chk_path.addRoundedRect(chk_rect, 3.5, 3.5)
        if self._checked:
            painter.fillPath(chk_path, self.theme.sage_accent)
            draw_nordic_icon(painter, "check", chk_rect, QColor("#FAF9F6"), stroke_w=1.5)
        else:
            painter.setPen(QPen(self.theme.slate_faint, 1.1))
            painter.drawPath(chk_path)

        painter.setPen(fg)
        painter.setFont(self.font())
        painter.drawText(
            QRectF(27.0, 0.0, self.width() - 32.0, self.height()),
            int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
            self.text(),
        )


class WinUIFlyoutMenu(QWidget):
    """
    带平滑弹出动效 (Fade + Slide) 与矢量图标的 WinUI 3 悬浮菜单
    替代生硬的原生 QMenu。
    """

    actionTriggered = pyqtSignal(str)

    def __init__(
        self,
        items: List[Tuple[str, str, str]],  # [(action_id, icon_kind, label), ...]
        theme: NordicThemeColors = DAYLIGHT_THEME,
        backdrop_material: str = "mica",
        parent: Optional[QWidget] = None,
    ):
        super().__init__(parent)
        self.items = items
        self.theme = theme
        self.backdrop_material = str(backdrop_material)
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        self.hovered_idx: int = -1
        self._anim_progress: float = 0.0

        self.setWindowFlags(
            Qt.WindowType.Popup
            | Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.NoDropShadowWindowHint
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.setMouseTracking(True)
        self.setCursor(Qt.CursorShape.PointingHandCursor)

        row_h = 34
        from PyQt6.QtGui import QFontMetrics
        metrics = QFontMetrics(get_nordic_font(12, QFont.Weight.Medium))
        max_txt_w = max((metrics.horizontalAdvance(lbl) for _, _, lbl in items), default=150)
        w = max(216, min(380, max_txt_w + 54))
        h = len(items) * row_h + 12
        self.setFixedSize(w, h)

        self._anim = QVariantAnimation(self)
        self._anim.setDuration(220)
        flyout_curve = QEasingCurve(QEasingCurve.Type.OutBack)
        flyout_curve.setOvershoot(1.22)
        self._anim.setEasingCurve(flyout_curve)
        self._anim.valueChanged.connect(self._on_anim_val)

    def _on_anim_val(self, val: float) -> None:
        self._anim_progress = float(val)
        self.setWindowOpacity(max(0.01, self._anim_progress))
        if hasattr(self, "_target_pos"):
            offset_y = int((1.0 - self._anim_progress) * -8.0)
            self.move(self._target_pos.x(), self._target_pos.y() + offset_y)
        self.update()

    def popup_animated(self, global_pos: QPoint) -> None:
        self._target_pos = global_pos
        self._anim_progress = 0.0
        self.setWindowOpacity(0.01)
        self.move(global_pos.x(), global_pos.y() - 8)
        self.show()
        self._anim.stop()
        self._anim.setStartValue(0.0)
        self._anim.setEndValue(1.0)
        self._anim.start()

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        y = event.position().y() - 6.0
        idx = int(y // 34.0)
        if not (0 <= idx < len(self.items)):
            idx = -1
        if idx != self.hovered_idx:
            self.hovered_idx = idx
            self.update()
        super().mouseMoveEvent(event)

    def leaveEvent(self, event) -> None:
        self.hovered_idx = -1
        self.update()
        super().leaveEvent(event)

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton and 0 <= self.hovered_idx < len(self.items):
            act_id = self.items[self.hovered_idx][0]
            self.close()
            self.actionTriggered.emit(act_id)
            return
        super().mousePressEvent(event)

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        rect = QRectF(self.rect()).adjusted(1.0, 1.0, -1.0, -1.0)
        path = QPainterPath()
        path.addRoundedRect(rect, 9.0, 9.0)

        is_dusk = self.theme.name == "dusk"
        bg = QColor(self.theme.mist_white)
        if self.backdrop_material == "acrylic":
            bg.setAlpha(208 if is_dusk else 220)
        elif self.backdrop_material == "mica":
            bg.setAlpha(234 if is_dusk else 244)
        else:
            bg.setAlpha(255)

        painter.fillPath(path, bg)
        border_col = self.theme.sage_border
        if self.backdrop_material == "acrylic":
            border_col = QColor(255, 255, 255, 45) if is_dusk else QColor(0, 0, 0, 30)
        painter.setPen(QPen(border_col, 1.0))
        painter.drawPath(path)

        row_h = 34.0
        for i, (act_id, icon_kind, label) in enumerate(self.items):
            r = QRectF(6.0, 6.0 + i * row_h, self.width() - 12.0, row_h)
            is_hov = i == self.hovered_idx
            if is_hov:
                hp = QPainterPath()
                hp.addRoundedRect(r, 6.0, 6.0)
                painter.fillPath(hp, self.theme.sage_soft)

            ic_col = self.theme.sage_accent if is_hov else self.theme.slate_dark
            draw_nordic_icon(
                painter,
                icon_kind,
                QRectF(r.left() + 8.0, r.center().y() - 8.0, 16.0, 16.0),
                ic_col,
                stroke_w=1.35,
            )

            painter.setPen(self.theme.charcoal if is_hov else self.theme.slate_dark)
            painter.setFont(get_nordic_font(12, QFont.Weight.Medium if is_hov else QFont.Weight.Normal))
            painter.drawText(
                QRectF(r.left() + 32.0, r.top(), r.width() - 38.0, r.height()),
                int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
                label,
            )


class WinUIModalDialog(QDialog):
    """
    无边框圆角 WinUI 3 模态浮层基类
    具备：12px 对称圆角、1px 漫反射高光描边、顶部拖拽栏、矢量关闭按钮与平滑渐入动效。
    """

    def __init__(
        self,
        title: str,
        width: int,
        height: int,
        theme: NordicThemeColors = DAYLIGHT_THEME,
        backdrop_material: str = "mica",
        parent: Optional[QWidget] = None,
    ):
        super().__init__(parent)
        self.theme = theme
        self.backdrop_material = str(backdrop_material)
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        self._drag_pos: Optional[QPoint] = None
        self.setWindowTitle(title)
        self.setFixedSize(width, height)
        self.setWindowFlags(Qt.WindowType.Dialog | Qt.WindowType.FramelessWindowHint)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)

        self._root_vbox = QVBoxLayout(self)
        self._root_vbox.setContentsMargins(22, 16, 22, 20)
        self._root_vbox.setSpacing(12)

        # 顶部标题与关闭按钮栏
        header_row = QHBoxLayout()
        header_row.setContentsMargins(0, 0, 0, 4)
        self.title_label = QLabel(title, self)
        self.title_label.setFont(get_nordic_font(15, QFont.Weight.DemiBold))
        self.title_label.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        header_row.addWidget(self.title_label)
        header_row.addStretch(1)

        self.btn_close_modal = QPushButton(self)
        self.btn_close_modal.setFixedSize(28, 28)
        self.btn_close_modal.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_close_modal.clicked.connect(self.reject)
        self.btn_close_modal.paintEvent = self._paint_close_btn  # type: ignore
        header_row.addWidget(self.btn_close_modal)

        self._root_vbox.addLayout(header_row)

        self._entry_anim = QVariantAnimation(self)
        self._entry_anim.setDuration(260)
        modal_curve = QEasingCurve(QEasingCurve.Type.OutBack)
        modal_curve.setOvershoot(1.10)
        self._entry_anim.setEasingCurve(modal_curve)
        self._entry_anim.valueChanged.connect(self._on_entry_anim)

    def set_backdrop_material(self, material: str) -> None:
        self.backdrop_material = str(material)
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        self.update()

    def _paint_close_btn(self, event) -> None:
        p = QPainter(self.btn_close_modal)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        r = QRectF(self.btn_close_modal.rect()).adjusted(1, 1, -1, -1)
        if self.btn_close_modal.underMouse():
            path = QPainterPath()
            path.addRoundedRect(r, 6.0, 6.0)
            p.fillPath(path, self.theme.subtle_hover)
        draw_nordic_icon(
            p,
            "close",
            r,
            self.theme.charcoal if self.btn_close_modal.underMouse() else self.theme.slate_muted,
            stroke_w=1.35,
        )

    def _on_entry_anim(self, val: float) -> None:
        v = float(val)
        self.setWindowOpacity(max(0.01, v))
        if hasattr(self, "_base_pos"):
            dy = int((1.0 - v) * 10.0)
            self.move(self._base_pos.x(), self._base_pos.y() + dy)

    def showEvent(self, event) -> None:
        super().showEvent(event)
        if self.parentWidget():
            pw = self.parentWidget().window()
            pg = pw.frameGeometry()
            cx = pg.center().x() - self.width() // 2
            cy = pg.center().y() - self.height() // 2
            self._base_pos = QPoint(cx, cy)
            self.move(cx, cy + 10)
        else:
            self._base_pos = self.pos()
        self.setWindowOpacity(0.01)
        self._entry_anim.stop()
        self._entry_anim.setStartValue(0.0)
        self._entry_anim.setEndValue(1.0)
        self._entry_anim.start()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton and event.position().y() <= 48.0:
            self._drag_pos = event.globalPosition().toPoint() - self.frameGeometry().topLeft()
            event.accept()
            return
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if self._drag_pos is not None and event.buttons() & Qt.MouseButton.LeftButton:
            self.move(event.globalPosition().toPoint() - self._drag_pos)
            self._base_pos = self.pos()
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
        path.addRoundedRect(rect, 12.0, 12.0)

        is_dusk = self.theme.name == "dusk"
        bg = QColor(self.theme.mist_white)
        if self.backdrop_material == "acrylic":
            bg.setAlpha(208 if is_dusk else 220)
        elif self.backdrop_material == "mica":
            bg.setAlpha(232 if is_dusk else 242)
        else:
            bg.setAlpha(255)

        painter.fillPath(path, bg)
        border_col = self.theme.sage_border
        if self.backdrop_material == "acrylic":
            border_col = QColor(255, 255, 255, 45) if is_dusk else QColor(0, 0, 0, 30)
        painter.setPen(QPen(border_col, 1.0))
        painter.drawPath(path)

        inner_rect = rect.adjusted(1.0, 1.0, -1.0, -1.0)
        inner_path = QPainterPath()
        inner_path.addRoundedRect(inner_rect, 11.0, 11.0)
        painter.setPen(QPen(self.theme.stroke_inner, 1.0))
        painter.drawPath(inner_path)

        # 顶栏下方的极细分割线
        painter.setPen(QPen(self.theme.divider, 1.0))
        painter.drawLine(22, 48, self.width() - 22, 48)
