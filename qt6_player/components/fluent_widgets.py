"""
Nordic WinUI 3 自定义矢量图标与现代 Fluent 交互组件库 (Qt6)
彻底替代传统 Qt 原生老旧控件 (QComboBox / QCheckBox / QDialog 原生边框 / 字符图标 ☰ ♥ ♡ ✕)，
所有组件均具备 60fps 平滑过渡动效、1px 漫反射微描边与矢量几何图标。
"""

import math
from typing import List, Optional, Tuple, Callable
from PyQt6.QtCore import (
    Qt,
    QRectF,
    QPointF,
    QPoint,
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


def draw_nordic_icon(
    painter: QPainter,
    kind: str,
    rect: QRectF,
    color: QColor,
    stroke_w: float = 1.4,
) -> None:
    """使用 QPainterPath 绘制高精度抗锯齿北欧极简矢量图标"""
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

    elif kind == "close":
        painter.drawLine(QPointF(cx - 4.2, cy - 4.2), QPointF(cx + 4.2, cy + 4.2))
        painter.drawLine(QPointF(cx + 4.2, cy - 4.2), QPointF(cx - 4.2, cy + 4.2))

    elif kind == "check":
        path = QPainterPath()
        path.moveTo(cx - 4.2, cy + 0.2)
        path.lineTo(cx - 1.2, cy + 3.2)
        path.lineTo(cx + 4.5, cy - 2.8)
        painter.drawPath(path)

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
        self.setFixedHeight(32)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMouseTracking(True)

        self._anim = QVariantAnimation(self)
        self._anim.setDuration(280)
        seg_curve = QEasingCurve(QEasingCurve.Type.OutBack)
        seg_curve.setOvershoot(1.16)
        self._anim.setEasingCurve(seg_curve)
        self._anim.valueChanged.connect(self._on_anim_val)

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

    def set_current_index(self, index: int, animate: bool = True) -> None:
        if not (0 <= index < len(self.items)):
            return
        changed = index != self.current_index
        self.current_index = index
        self._anim.stop()
        if animate:
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

    def paintEvent(self, event) -> None:
        if not self.items:
            return
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        outer_rect = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        outer_path = QPainterPath()
        outer_path.addRoundedRect(outer_rect, 7.0, 7.0)
        painter.fillPath(outer_path, self.theme.oat_surface)
        painter.setPen(QPen(self.theme.stroke_outer, 1.0))
        painter.drawPath(outer_path)

        n = len(self.items)
        pad = 3.0
        usable_w = self.width() - pad * 2.0
        seg_w = usable_w / n

        # 滑动胶囊选中背景
        pill_x = pad + self.pill_pos * seg_w
        pill_rect = QRectF(pill_x, pad, seg_w, self.height() - pad * 2.0)
        pill_path = QPainterPath()
        pill_path.addRoundedRect(pill_rect, 5.0, 5.0)
        painter.fillPath(pill_path, self.theme.card_elevated)
        painter.setPen(QPen(self.theme.sage_border, 1.0))
        painter.drawPath(pill_path)

        # 绘制底部鼠尾草绿短横线指示条
        bar_w = min(20.0, seg_w * 0.32)
        bar_rect = QRectF(pill_rect.center().x() - bar_w / 2.0, pill_rect.bottom() - 2.5, bar_w, 2.0)
        bar_path = QPainterPath()
        bar_path.addRoundedRect(bar_rect, 1.0, 1.0)
        painter.fillPath(bar_path, self.theme.sage_accent)

        # 绘制各分段文本
        for i, label in enumerate(self.items):
            item_rect = QRectF(pad + i * seg_w, 0.0, seg_w, self.height())
            is_cur = i == self.current_index
            painter.setFont(
                get_nordic_font(12, QFont.Weight.DemiBold if is_cur else QFont.Weight.Medium)
            )
            if is_cur:
                painter.setPen(self.theme.charcoal)
            elif i == self.hovered_index:
                painter.setPen(self.theme.slate_dark)
            else:
                painter.setPen(self.theme.slate_muted)
            painter.drawText(item_rect, int(Qt.AlignmentFlag.AlignCenter), label)


class WinUISearchBox(QWidget):
    """自定义绘制的 WinUI 3 搜索输入框：带矢量放大镜、焦点平滑高亮底边与矢量清除按钮"""

    textChanged = pyqtSignal(str)
    returnPressed = pyqtSignal()

    def __init__(self, placeholder: str = "搜索…", parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self._focus_progress: float = 0.0
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
        placeholder_col = theme.slate_faint.name()
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

        painter.fillPath(path, self.theme.card_elevated)
        border_col = (
            self.theme.sage_border if self._focus_progress > 0.3 else self.theme.stroke_outer
        )
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
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setFixedHeight(30)
        self.setFont(get_nordic_font(12, QFont.Weight.Medium))
        fm = self.fontMetrics()
        self.setFixedWidth(fm.horizontalAdvance(text) + 36)

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

        if self._checked:
            bg = self.theme.sage_soft
            border = self.theme.sage_border
            fg = self.theme.charcoal
        elif self.underMouse():
            bg = self.theme.subtle_hover
            border = self.theme.stroke_outer
            fg = self.theme.charcoal
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
        parent: Optional[QWidget] = None,
    ):
        super().__init__(parent)
        self.items = items
        self.theme = theme
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
        w = 216
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

        bg = QColor(self.theme.mist_white)
        bg.setAlpha(248)
        painter.fillPath(path, bg)
        painter.setPen(QPen(self.theme.sage_border, 1.0))
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
        parent: Optional[QWidget] = None,
    ):
        super().__init__(parent)
        self.theme = theme
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

        painter.fillPath(path, self.theme.mist_white)
        painter.setPen(QPen(self.theme.sage_border, 1.0))
        painter.drawPath(path)

        inner_rect = rect.adjusted(1.0, 1.0, -1.0, -1.0)
        inner_path = QPainterPath()
        inner_path.addRoundedRect(inner_rect, 11.0, 11.0)
        painter.setPen(QPen(self.theme.stroke_inner, 1.0))
        painter.drawPath(inner_path)

        # 顶栏下方的极细分割线
        painter.setPen(QPen(self.theme.divider, 1.0))
        painter.drawLine(22, 48, self.width() - 22, 48)
