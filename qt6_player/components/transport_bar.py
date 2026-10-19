"""
底部播放控制栏组件 (Qt6)
特性：
1. 底部左右 12px 圆角裁切与描边，与窗口顶部圆角完全对称。
2. 完整播放控制：播放模式切换 (列表循环/单曲循环/随机播放/顺序播放)、上一曲、播放/暂停、下一曲、倍速切换 (0.75x~2.0x)。
3. 左侧迷你专辑封面、歌名/歌词实时显示、红心收藏按钮。
4. 右侧音效/定时设置按钮、静音切换、双环音量滑块与百分比显示。
"""

import math
from typing import Optional
from PyQt6.QtCore import Qt, QTimer, QRectF, QPointF, pyqtSignal
from PyQt6.QtGui import (
    QPainter,
    QPen,
    QColor,
    QFont,
    QPainterPath,
    QMouseEvent,
    QPixmap,
    QLinearGradient,
)
from PyQt6.QtWidgets import QWidget, QHBoxLayout, QVBoxLayout, QLabel, QPushButton

from qt6_player.data.tracks import LyricLine, NordicTrack, format_timecode
from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font
from qt6_player.components.fluent_widgets import draw_nordic_icon



class WinUISlider(QWidget):
    valueChangedByUser = pyqtSignal(float)

    def __init__(self, initial_ratio: float = 0.0, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.ratio: float = max(0.0, min(1.0, initial_ratio))
        self.is_dragging: bool = False
        self.setFixedHeight(22)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMouseTracking(True)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def set_ratio(self, ratio: float) -> None:
        if not self.is_dragging:
            self.ratio = max(0.0, min(1.0, float(ratio)))
            self.update()

    def _update_from_pos(self, x: float) -> None:
        margin = 8.0
        usable_w = max(1.0, self.width() - margin * 2.0)
        self.ratio = max(0.0, min(1.0, (x - margin) / usable_w))
        self.valueChangedByUser.emit(self.ratio)
        self.update()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self.is_dragging = True
            self._update_from_pos(event.position().x())
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if self.is_dragging:
            self._update_from_pos(event.position().x())
        else:
            self.update()
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton and self.is_dragging:
            self.is_dragging = False
            self._update_from_pos(event.position().x())
        super().mouseReleaseEvent(event)

    def leaveEvent(self, event) -> None:
        self.update()
        super().leaveEvent(event)

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        margin = 8.0
        cy = self.height() / 2.0
        usable_w = max(1.0, self.width() - margin * 2.0)
        thumb_x = margin + usable_w * self.ratio

        track_rect = QRectF(margin, cy - 2.0, usable_w, 4.0)
        track_path = QPainterPath()
        track_path.addRoundedRect(track_rect, 2.0, 2.0)
        track_bg = QColor(self.theme.slate_muted)
        track_bg.setAlpha(46)
        painter.fillPath(track_path, track_bg)

        if self.ratio > 0.001:
            fill_rect = QRectF(margin, cy - 2.0, max(4.0, thumb_x - margin), 4.0)
            fill_path = QPainterPath()
            fill_path.addRoundedRect(fill_rect, 2.0, 2.0)
            painter.fillPath(fill_path, self.theme.sage_accent)

        outer_r = 8.0
        painter.setPen(QPen(self.theme.stroke_outer, 1.0))
        painter.setBrush(self.theme.card_elevated)
        painter.drawEllipse(QPointF(thumb_x, cy), outer_r, outer_r)

        if self.is_dragging:
            inner_r = 3.4
        elif self.underMouse():
            inner_r = 4.8
        else:
            inner_r = 4.0

        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(self.theme.sage_accent)
        painter.drawEllipse(QPointF(thumb_x, cy), inner_r, inner_r)


class MiniCoverThumb(QPushButton):
    """左下角迷你专辑封面缩略图"""

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.track: Optional[NordicTrack] = None
        self.pixmap: Optional[QPixmap] = None
        self.setFixedSize(44, 44)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setToolTip("点击切换侧边栏展开/收起")

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def set_track(self, track: NordicTrack) -> None:
        self.track = track
        self.pixmap = None
        pix = QPixmap()
        if track.cover_bytes and pix.loadFromData(track.cover_bytes):
            self.pixmap = pix
        elif track.cover_path and pix.load(track.cover_path):
            self.pixmap = pix
        self.update()

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)

        rect = QRectF(self.rect()).adjusted(1.0, 1.0, -1.0, -1.0)
        path = QPainterPath()
        path.addRoundedRect(rect, 7.0, 7.0)

        painter.save()
        painter.setClipPath(path)
        if self.pixmap is not None and not self.pixmap.isNull():
            painter.drawPixmap(rect.toRect(), self.pixmap)
        elif self.track is not None:
            grad = QLinearGradient(rect.topLeft(), rect.bottomRight())
            grad.setColorAt(0.0, QColor(self.track.palette["bg_primary"]))
            grad.setColorAt(1.0, QColor(self.track.palette["bg_secondary"]))
            painter.fillRect(rect, grad)
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(QColor(self.track.palette["accent"]))
            painter.drawEllipse(rect.center(), 10.0, 10.0)
        else:
            painter.fillRect(rect, self.theme.card_elevated)
        painter.restore()

        painter.setPen(QPen(self.theme.stroke_outer, 1.0))
        painter.drawPath(path)


class TransportIconButton(QPushButton):
    def __init__(self, kind: str, is_primary: bool = False, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.kind = kind  # "mode" | "prev" | "play" | "next" | "mute" | "fav" | "rate" | "eq"
        self.is_primary = is_primary
        self.is_playing = False
        self.is_muted = False
        self.is_favorite = False
        self.play_mode = "loop_list"  # "loop_list" | "loop_one" | "shuffle" | "sequential"
        self.badge_text = "1.0x"
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.setCursor(Qt.CursorShape.PointingHandCursor)

        if is_primary:
            self.setFixedSize(40, 40)
        elif kind == "eq":
            self.setFixedSize(58, 28)
        elif kind == "rate":
            self.setFixedSize(44, 28)
        else:
            self.setFixedSize(32, 32)
        self.is_eq_active = False

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        cx, cy = self.width() / 2.0, self.height() / 2.0

        if self.is_primary:
            bg = QColor(self.theme.sage_accent)
            if self.underMouse():
                bg = bg.lighter(108)
            painter.setPen(QPen(self.theme.sage_border, 1.0))
            painter.setBrush(bg)
            painter.drawEllipse(QPointF(cx, cy), 19.0, 19.0)
            fg = QColor("#FAF9F6")
        else:
            if self.underMouse() or self.kind in ("rate", "eq"):
                path = QPainterPath()
                path.addRoundedRect(QRectF(self.rect()).adjusted(1, 1, -1, -1), 6.0, 6.0)
                if self.kind == "eq" and self.is_eq_active:
                    fill = self.theme.sage_soft
                    border = self.theme.sage_border
                else:
                    fill = self.theme.subtle_hover if self.underMouse() else self.theme.oat_surface
                    border = self.theme.stroke_outer
                painter.fillPath(path, fill)
                painter.setPen(QPen(border, 1.0))
                painter.drawPath(path)
            fg = self.theme.charcoal if (self.underMouse() or self.is_eq_active) else self.theme.slate_dark

        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(fg)

        if self.kind == "play":
            if self.is_playing:
                painter.drawRoundedRect(QRectF(cx - 5.0, cy - 6.0, 3.2, 12.0), 1.0, 1.0)
                painter.drawRoundedRect(QRectF(cx + 1.8, cy - 6.0, 3.2, 12.0), 1.0, 1.0)
            else:
                tri = QPainterPath()
                tri.moveTo(cx - 3.5, cy - 6.2)
                tri.lineTo(cx + 6.5, cy)
                tri.lineTo(cx - 3.5, cy + 6.2)
                tri.closeSubpath()
                painter.drawPath(tri)
        elif self.kind == "prev":
            painter.drawRoundedRect(QRectF(cx - 6.0, cy - 5.5, 2.0, 11.0), 0.8, 0.8)
            tri = QPainterPath()
            tri.moveTo(cx + 5.5, cy - 5.5)
            tri.lineTo(cx - 3.0, cy)
            tri.lineTo(cx + 5.5, cy + 5.5)
            tri.closeSubpath()
            painter.drawPath(tri)
        elif self.kind == "next":
            painter.drawRoundedRect(QRectF(cx + 4.0, cy - 5.5, 2.0, 11.0), 0.8, 0.8)
            tri = QPainterPath()
            tri.moveTo(cx - 5.5, cy - 5.5)
            tri.lineTo(cx + 3.0, cy)
            tri.lineTo(cx - 5.5, cy + 5.5)
            tri.closeSubpath()
            painter.drawPath(tri)
        elif self.kind == "mode":
            pen = QPen(fg, 1.35)
            painter.setPen(pen)
            painter.setBrush(Qt.BrushStyle.NoBrush)
            if self.play_mode in ("loop_list", "loop_one"):
                painter.drawRoundedRect(QRectF(cx - 6.5, cy - 4.5, 13.0, 9.0), 3.0, 3.0)
                if self.play_mode == "loop_one":
                    painter.setFont(get_nordic_font(8, QFont.Weight.Bold, mono=True))
                    painter.drawText(self.rect(), int(Qt.AlignmentFlag.AlignCenter), "1")
            elif self.play_mode == "shuffle":
                painter.drawLine(int(cx - 6), int(cy - 4), int(cx + 6), int(cy + 4))
                painter.drawLine(int(cx - 6), int(cy + 4), int(cx + 6), int(cy - 4))
            else:  # sequential
                painter.drawLine(int(cx - 6), int(cy - 3), int(cx + 6), int(cy - 3))
                painter.drawLine(int(cx - 6), int(cy + 3), int(cx + 6), int(cy + 3))
        elif self.kind == "fav":
            ic_col = self.theme.sage_accent if self.is_favorite else self.theme.slate_muted
            draw_nordic_icon(
                painter,
                "heart_filled" if self.is_favorite else "heart_outline",
                QRectF(0, 0, self.width(), self.height()),
                ic_col,
                stroke_w=1.4,
            )
        elif self.kind == "rate":
            painter.setFont(get_nordic_font(11, QFont.Weight.Medium, mono=True))
            painter.setPen(fg)
            painter.drawText(self.rect(), int(Qt.AlignmentFlag.AlignCenter), self.badge_text)
        elif self.kind == "eq":
            ic_col = self.theme.sage_accent if self.is_eq_active else fg
            draw_nordic_icon(
                painter,
                "eq_sliders",
                QRectF(5.0, cy - 7.0, 14.0, 14.0),
                ic_col,
                stroke_w=1.25,
            )
            painter.setFont(get_nordic_font(11, QFont.Weight.Medium))
            painter.setPen(fg)
            painter.drawText(
                QRectF(21.0, 0, self.width() - 23.0, self.height()),
                int(Qt.AlignmentFlag.AlignCenter),
                self.badge_text,
            )

        elif self.kind == "mute":
            spk = QPainterPath()
            spk.moveTo(cx - 6.0, cy - 2.5)
            spk.lineTo(cx - 3.0, cy - 2.5)
            spk.lineTo(cx + 1.0, cy - 6.0)
            spk.lineTo(cx + 1.0, cy + 6.0)
            spk.lineTo(cx - 3.0, cy + 2.5)
            spk.lineTo(cx - 6.0, cy + 2.5)
            spk.closeSubpath()
            painter.drawPath(spk)

            pen = QPen(fg, 1.4)
            painter.setPen(pen)
            painter.setBrush(Qt.BrushStyle.NoBrush)
            if self.is_muted:
                painter.drawLine(int(cx + 3), int(cy - 3), int(cx + 7), int(cy + 3))
                painter.drawLine(int(cx + 7), int(cy - 3), int(cx + 3), int(cy + 3))
            else:
                painter.drawArc(QRectF(cx + 1.0, cy - 4.0, 5.0, 8.0), -60 * 16, 120 * 16)


class TransportBar(QWidget):
    playToggled = pyqtSignal()
    prevClicked = pyqtSignal()
    nextClicked = pyqtSignal()
    playModeCycled = pyqtSignal(str)
    favoriteToggled = pyqtSignal()
    rateCycled = pyqtSignal()
    audioSettingsClicked = pyqtSignal()
    coverThumbClicked = pyqtSignal()
    seekRatioRequested = pyqtSignal(float)
    volumeChanged = pyqtSignal(float)
    muteToggled = pyqtSignal()

    _MODE_ORDER = ["loop_list", "loop_one", "shuffle", "sequential"]
    _MODE_NAMES = {
        "loop_list": "列表循环",
        "loop_one": "单曲循环",
        "shuffle": "随机播放",
        "sequential": "顺序播放",
    }

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.setFixedHeight(90)

        root_layout = QHBoxLayout(self)
        root_layout.setContentsMargins(20, 10, 20, 10)
        root_layout.setSpacing(18)

        # 1. 左侧：迷你封面 + 曲名/实时歌词 + 红心收藏
        left_box = QWidget(self)
        left_box.setFixedWidth(268)
        left_layout = QHBoxLayout(left_box)
        left_layout.setContentsMargins(0, 0, 0, 0)
        left_layout.setSpacing(10)

        self.cover_thumb = MiniCoverThumb(left_box)
        self.cover_thumb.clicked.connect(self.coverThumbClicked.emit)
        left_layout.addWidget(self.cover_thumb)

        snippet_col = QVBoxLayout()
        snippet_col.setContentsMargins(0, 0, 0, 0)
        snippet_col.setSpacing(2)

        title_fav_row = QHBoxLayout()
        title_fav_row.setContentsMargins(0, 0, 0, 0)
        title_fav_row.setSpacing(4)
        self.status_caption = QLabel("未播放", left_box)
        self.status_caption.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        self.btn_fav = TransportIconButton("fav", parent=left_box)
        self.btn_fav.setFixedSize(24, 24)
        self.btn_fav.setToolTip("收藏到「我喜欢」")
        self.btn_fav.clicked.connect(self.favoriteToggled.emit)
        title_fav_row.addWidget(self.status_caption, 1)
        title_fav_row.addWidget(self.btn_fav)

        self.snippet_label = QLabel("", left_box)
        self.snippet_label.setFont(get_nordic_font(11, QFont.Weight.Normal))
        snippet_col.addLayout(title_fav_row)
        snippet_col.addWidget(self.snippet_label)
        left_layout.addLayout(snippet_col, 1)

        root_layout.addWidget(left_box)

        # 2. 中央：播放模式 + 上一曲 + 播放/暂停 + 下一曲 + 倍速 + 进度条
        center_col = QVBoxLayout()
        center_col.setContentsMargins(0, 0, 0, 0)
        center_col.setSpacing(6)

        btns_row = QHBoxLayout()
        btns_row.setSpacing(12)
        btns_row.addStretch(1)

        self.btn_mode = TransportIconButton("mode", parent=self)
        self.btn_mode.setToolTip("播放模式：列表循环")
        self.btn_mode.clicked.connect(self._cycle_play_mode)

        self.btn_prev = TransportIconButton("prev", parent=self)
        self.btn_prev.setToolTip("上一首 (Ctrl + Left)")
        self.btn_play = TransportIconButton("play", is_primary=True, parent=self)
        self.btn_play.setToolTip("播放 / 暂停 (Space)")
        self.btn_next = TransportIconButton("next", parent=self)
        self.btn_next.setToolTip("下一首 (Ctrl + Right)")

        self.btn_rate = TransportIconButton("rate", parent=self)
        self.btn_rate.badge_text = "1.0x"
        self.btn_rate.setToolTip("点击切换播放倍速 (0.75x ~ 2.0x)")
        self.btn_rate.clicked.connect(self.rateCycled.emit)

        self.btn_prev.clicked.connect(self.prevClicked.emit)
        self.btn_play.clicked.connect(self.playToggled.emit)
        self.btn_next.clicked.connect(self.nextClicked.emit)

        btns_row.addWidget(self.btn_mode)
        btns_row.addWidget(self.btn_prev)
        btns_row.addWidget(self.btn_play)
        btns_row.addWidget(self.btn_next)
        btns_row.addWidget(self.btn_rate)
        btns_row.addStretch(1)
        center_col.addLayout(btns_row)

        timeline_row = QHBoxLayout()
        timeline_row.setSpacing(10)
        self.lbl_cur_time = QLabel("00:00", self)
        self.lbl_cur_time.setFont(get_nordic_font(11, QFont.Weight.Normal, mono=True))
        self.lbl_cur_time.setFixedWidth(40)
        self.lbl_cur_time.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)

        self.timeline_slider = WinUISlider(0.0, self)
        self.timeline_slider.valueChangedByUser.connect(self.seekRatioRequested.emit)

        self.lbl_dur_time = QLabel("00:00", self)
        self.lbl_dur_time.setFont(get_nordic_font(11, QFont.Weight.Normal, mono=True))
        self.lbl_dur_time.setFixedWidth(40)
        self.lbl_dur_time.setAlignment(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter)

        timeline_row.addWidget(self.lbl_cur_time)
        timeline_row.addWidget(self.timeline_slider, 1)
        timeline_row.addWidget(self.lbl_dur_time)
        center_col.addLayout(timeline_row)

        root_layout.addLayout(center_col, 1)

        # 3. 右侧：音效/定时按钮 + 静音 + 音量滑块 + 百分比
        right_box = QWidget(self)
        right_box.setFixedWidth(238)
        right_layout = QHBoxLayout(right_box)
        right_layout.setContentsMargins(0, 0, 0, 0)
        right_layout.setSpacing(6)
        right_layout.addStretch(1)

        self.btn_eq = TransportIconButton("eq", parent=right_box)
        self.btn_eq.badge_text = "音效"
        self.btn_eq.setToolTip("打开音效模式、歌词偏移与睡眠定时设置")
        self.btn_eq.clicked.connect(self.audioSettingsClicked.emit)
        right_layout.addWidget(self.btn_eq)

        self.btn_mute = TransportIconButton("mute", parent=right_box)
        self.btn_mute.setToolTip("静音切换")
        self.btn_mute.clicked.connect(self.muteToggled.emit)
        right_layout.addWidget(self.btn_mute)

        self.volume_slider = WinUISlider(0.72, right_box)
        self.volume_slider.setFixedWidth(94)
        self.volume_slider.valueChangedByUser.connect(self.volumeChanged.emit)
        right_layout.addWidget(self.volume_slider)

        self.lbl_vol_pct = QLabel("72%", right_box)
        self.lbl_vol_pct.setFixedWidth(32)
        self.lbl_vol_pct.setFont(get_nordic_font(11, QFont.Weight.Normal, mono=True))
        self.lbl_vol_pct.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        right_layout.addWidget(self.lbl_vol_pct)

        root_layout.addWidget(right_box)
        self.set_theme(DAYLIGHT_THEME)

    def _cycle_play_mode(self) -> None:
        cur = self.btn_mode.play_mode
        idx = (self._MODE_ORDER.index(cur) + 1) % len(self._MODE_ORDER) if cur in self._MODE_ORDER else 0
        next_mode = self._MODE_ORDER[idx]
        self.set_play_mode(next_mode)
        self.playModeCycled.emit(next_mode)

    def set_play_mode(self, mode: str) -> None:
        if mode not in self._MODE_NAMES:
            mode = "loop_list"
        self.btn_mode.play_mode = mode
        self.btn_mode.setToolTip(f"播放模式：{self._MODE_NAMES[mode]}")
        self.btn_mode.update()

    def set_playback_rate(self, rate: float) -> None:
        self.btn_rate.badge_text = f"{rate:g}x"
        self.btn_rate.update()

    def set_sound_profile_badge(self, profile: str) -> None:
        self.btn_eq.is_eq_active = profile != "原声自然"
        self.btn_eq.setToolTip(f"当前音效：{profile}（点击打开均衡器与定时设置）")
        self.btn_eq.update()


    def set_track_info(self, track: NordicTrack) -> None:
        self.cover_thumb.set_track(track)
        self.btn_fav.is_favorite = track.is_favorite
        self.btn_fav.update()
        fm = self.status_caption.fontMetrics()
        self.status_caption.setText(
            fm.elidedText(f"{track.title}", Qt.TextElideMode.ElideRight, 175)
        )
        self.snippet_label.setText(track.artist)

    def set_status_message(self, msg: str) -> None:
        fm = self.snippet_label.fontMetrics()
        self.snippet_label.setText(fm.elidedText(msg, Qt.TextElideMode.ElideRight, 195))

    def set_playing_state(self, is_playing: bool) -> None:
        self.btn_play.is_playing = is_playing
        self.btn_play.update()

    def update_progress(self, current_time: float, duration: float) -> None:
        safe_dur = max(1.0, duration)
        self.lbl_cur_time.setText(format_timecode(current_time))
        self.lbl_dur_time.setText(format_timecode(safe_dur))
        self.timeline_slider.set_ratio(current_time / safe_dur)

    def update_volume(self, volume: float, is_muted: bool) -> None:
        self.btn_mute.is_muted = is_muted
        self.btn_mute.update()
        eff = 0.0 if is_muted else volume
        self.volume_slider.set_ratio(eff)
        self.lbl_vol_pct.setText(f"{int(round(eff * 100))}%")

    def update_lyric_snippet(self, line: LyricLine) -> None:
        text = line.sub if line.is_interlude else line.text
        fm = self.snippet_label.fontMetrics()
        self.snippet_label.setText(fm.elidedText(text, Qt.TextElideMode.ElideRight, 195))

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.cover_thumb.set_theme(theme)
        self.status_caption.setStyleSheet(f"color: {theme.charcoal.name()}; background: transparent;")
        self.snippet_label.setStyleSheet(f"color: {theme.slate_muted.name()}; background: transparent;")
        self.lbl_cur_time.setStyleSheet(f"color: {theme.slate_muted.name()}; background: transparent;")
        self.lbl_dur_time.setStyleSheet(f"color: {theme.slate_muted.name()}; background: transparent;")
        self.lbl_vol_pct.setStyleSheet(f"color: {theme.slate_muted.name()}; background: transparent;")
        for b in (
            self.btn_fav,
            self.btn_mode,
            self.btn_prev,
            self.btn_play,
            self.btn_next,
            self.btn_rate,
            self.btn_eq,
            self.btn_mute,
        ):
            b.set_theme(theme)
        self.timeline_slider.set_theme(theme)
        self.volume_slider.set_theme(theme)
        self.update()

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        painter.save()
        painter.setCompositionMode(QPainter.CompositionMode.CompositionMode_Clear)
        painter.fillRect(self.rect(), Qt.GlobalColor.transparent)
        painter.restore()

        w, h = float(self.width()), float(self.height())
        is_max = self.window().isMaximized()
        r = 0.0 if is_max else 11.0

        # 构造顶部直角、底部左右圆角的路径，使底部圆角与窗口顶部完全对称
        path = QPainterPath()
        if r > 0.0:
            path.moveTo(0.0, 0.0)
            path.lineTo(w, 0.0)
            path.lineTo(w, h - r)
            path.arcTo(QRectF(w - 2 * r, h - 2 * r, 2 * r, 2 * r), 0.0, -90.0)
            path.lineTo(r, h)
            path.arcTo(QRectF(0.0, h - 2 * r, 2 * r, 2 * r), -90.0, -90.0)
            path.closeSubpath()
        else:
            path.addRect(QRectF(0.0, 0.0, w, h))

        painter.save()
        painter.setClipPath(path)
        painter.fillPath(path, self.theme.oat_surface)

        # 顶部分割线与内侧微高光
        painter.setPen(QPen(self.theme.stroke_outer, 1.0))
        painter.drawLine(0, 0, int(w), 0)
        painter.setPen(QPen(self.theme.stroke_inner, 1.0))
        painter.drawLine(0, 1, int(w), 1)
        painter.restore()
