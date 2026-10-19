"""
左侧播放列表与音乐库管理面板 (Qt6)
支持：
1. 全部音乐 / 我喜欢 分组切换（带滑动胶囊指示器 + 列表切换平滑缓动过渡）
2. 自定义 WinUI 3 矢量搜索过滤框 (歌名 / 歌手 / 专辑)
3. 单曲矢量红心收藏切换、平滑弹出右键菜单 (WinUIFlyoutMenu)
4. 底部单一下拉/弹出式「导入音乐」入口（彻底消除重复与杂乱按钮）
"""

import os
import subprocess
from pathlib import Path
from typing import List, Optional
from PyQt6.QtCore import (
    Qt,
    pyqtSignal,
    QRectF,
    QPointF,
    QPoint,
    QVariantAnimation,
    QEasingCurve,
)
from PyQt6.QtGui import (
    QPainter,
    QPen,
    QColor,
    QFont,
    QPainterPath,
    QLinearGradient,
    QPixmap,
    QMouseEvent,
)
from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QPushButton,
    QScrollArea,
    QFrame,
)

from qt6_player.data.tracks import NordicTrack, format_timecode
from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font
from qt6_player.components.fluent_widgets import (
    draw_nordic_icon,
    WinUISegmentedControl,
    WinUISearchBox,
    WinUIFlyoutMenu,
)


class NavTrackItemButton(QPushButton):
    favoriteToggled = pyqtSignal(int)
    contextActionRequested = pyqtSignal(int, str)  # track_index, action_name

    def __init__(
        self,
        index: int,
        track: NordicTrack,
        selected: bool = False,
        parent: Optional[QWidget] = None,
    ):
        super().__init__(parent)
        self.index = index
        self.track = track
        self.is_selected = selected
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self._thumb_pixmap: Optional[QPixmap] = None
        self._hover_alpha: float = 0.0
        self._reveal_progress: float = 1.0

        self.setFixedHeight(52)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMouseTracking(True)

        self._hover_anim = QVariantAnimation(self)
        self._hover_anim.setDuration(150)
        self._hover_anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._hover_anim.valueChanged.connect(self._on_hover_val)

        self.refresh_thumbnail()

    def _on_hover_val(self, val: float) -> None:
        self._hover_alpha = float(val)
        self.update()

    def set_reveal_progress(self, prog: float) -> None:
        self._reveal_progress = max(0.0, min(1.0, float(prog)))
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

    def refresh_thumbnail(self) -> None:
        self._thumb_pixmap = None
        pix = QPixmap()
        if self.track.cover_bytes and pix.loadFromData(self.track.cover_bytes):
            self._thumb_pixmap = pix
        elif self.track.cover_path and pix.load(self.track.cover_path):
            self._thumb_pixmap = pix
        self.update()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def set_selected(self, selected: bool) -> None:
        self.is_selected = selected
        self.update()

    def _fav_rect(self) -> QRectF:
        return QRectF(self.width() - 62.0, self.height() / 2.0 - 9.0, 18.0, 18.0)

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            if self._fav_rect().adjusted(-3, -3, 3, 3).contains(event.position()):
                self.track.is_favorite = not self.track.is_favorite
                self.favoriteToggled.emit(self.index)
                self.update()
                event.accept()
                return
        elif event.button() == Qt.MouseButton.RightButton:
            self._show_context_menu(event.globalPosition().toPoint())
            event.accept()
            return
        super().mousePressEvent(event)

    def _show_context_menu(self, global_pos: QPoint) -> None:
        fav_label = "取消喜欢收藏" if self.track.is_favorite else "添加到「我喜欢」"
        fav_icon = "heart_filled" if self.track.is_favorite else "heart_outline"
        items = [
            ("play", "music_file", "播放此曲目"),
            ("fav", fav_icon, fav_label),
            ("scrape", "sparkle", "在线匹配歌词与封面"),
            ("import_lrc", "lrc", "加载本地 LRC 歌词"),
        ]
        if self.track.file_path and Path(self.track.file_path).exists():
            items.append(("open_dir", "folder", "在文件夹中显示"))
        items.append(("remove", "trash", "从列表中移除"))

        self._ctx_menu = WinUIFlyoutMenu(items, theme=self.theme, parent=self.window())
        self._ctx_menu.actionTriggered.connect(self._on_ctx_action)
        self._ctx_menu.popup_animated(global_pos)

    def _on_ctx_action(self, act: str) -> None:
        if act == "fav":
            self.track.is_favorite = not self.track.is_favorite
            self.favoriteToggled.emit(self.index)
            self.update()
        elif act == "open_dir":
            if self.track.file_path:
                try:
                    subprocess.Popen(["explorer", "/select,", os.path.normpath(self.track.file_path)])
                except Exception:
                    pass
        else:
            self.contextActionRequested.emit(self.index, act)

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)

        # Tab 切换时的丝滑纵向位移与淡入动效
        painter.setOpacity(self._reveal_progress)
        slide_y = (1.0 - self._reveal_progress) * 10.0
        painter.translate(0.0, slide_y)

        rect = QRectF(self.rect()).adjusted(1.0, 1.0, -1.0, -1.0)
        path = QPainterPath()
        path.addRoundedRect(rect, 7.0, 7.0)

        if self.is_selected:
            painter.fillPath(path, self.theme.oat_surface)
            painter.setPen(QPen(self.theme.stroke_outer, 1.0))
            painter.drawPath(path)

            pill_rect = QRectF(4.0, self.height() / 2.0 - 9.0, 3.2, 18.0)
            pill_path = QPainterPath()
            pill_path.addRoundedRect(pill_rect, 1.6, 1.6)
            painter.fillPath(pill_path, self.theme.sage_accent)
        elif self._hover_alpha > 0.01:
            hov_col = QColor(self.theme.subtle_hover)
            hov_col.setAlphaF(hov_col.alphaF() * self._hover_alpha)
            painter.fillPath(path, hov_col)

        # 左侧缩略封面或色卡
        swatch_rect = QRectF(13.0, self.height() / 2.0 - 16.0, 32.0, 32.0)
        swatch_path = QPainterPath()
        swatch_path.addRoundedRect(swatch_rect, 5.0, 5.0)

        if self._thumb_pixmap is not None and not self._thumb_pixmap.isNull():
            painter.save()
            painter.setClipPath(swatch_path)
            painter.drawPixmap(swatch_rect.toRect(), self._thumb_pixmap)
            painter.restore()
            painter.setPen(QPen(self.theme.stroke_outer, 1.0))
            painter.drawPath(swatch_path)
        else:
            grad = QLinearGradient(swatch_rect.topLeft(), swatch_rect.bottomRight())
            grad.setColorAt(0.0, QColor(self.track.palette["bg_primary"]))
            grad.setColorAt(1.0, QColor(self.track.palette["bg_secondary"]))
            painter.fillPath(swatch_path, grad)
            painter.setPen(QPen(self.theme.stroke_outer, 1.0))
            painter.drawPath(swatch_path)

            painter.setPen(QColor(self.track.palette["ink"]))
            painter.setFont(get_nordic_font(11, QFont.Weight.DemiBold, mono=True))
            painter.drawText(swatch_rect, int(Qt.AlignmentFlag.AlignCenter), f"{self.index + 1:02d}")

        # 标题与歌手
        text_left = 53.0
        text_width = max(40.0, self.width() - text_left - 66.0)

        painter.setPen(self.theme.charcoal if self.is_selected else self.theme.slate_dark)
        title_font = get_nordic_font(13, QFont.Weight.DemiBold if self.is_selected else QFont.Weight.Medium)
        painter.setFont(title_font)
        elided_title = painter.fontMetrics().elidedText(
            self.track.title, Qt.TextElideMode.ElideRight, int(text_width)
        )
        painter.drawText(
            QRectF(text_left, 8.0, text_width, 19.0),
            int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
            elided_title,
        )

        painter.setPen(self.theme.slate_muted)
        artist_font = get_nordic_font(11, QFont.Weight.Normal)
        painter.setFont(artist_font)
        elided_artist = painter.fontMetrics().elidedText(
            self.track.artist, Qt.TextElideMode.ElideRight, int(text_width)
        )
        painter.drawText(
            QRectF(text_left, 27.0, text_width, 17.0),
            int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
            elided_artist,
        )

        # 矢量心形收藏图标（已收藏或鼠标移入时显示）
        if self.track.is_favorite or self._hover_alpha > 0.1:
            fav_r = self._fav_rect()
            ic_col = self.theme.sage_accent if self.track.is_favorite else self.theme.slate_faint
            draw_nordic_icon(
                painter,
                "heart_filled" if self.track.is_favorite else "heart_outline",
                fav_r,
                ic_col,
                stroke_w=1.35,
            )

        # 右侧时长
        painter.setPen(self.theme.slate_faint)
        painter.setFont(get_nordic_font(11, QFont.Weight.Normal, mono=True))
        painter.drawText(
            QRectF(self.width() - 40.0, 0.0, 34.0, self.height()),
            int(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter),
            format_timecode(self.track.duration),
        )


class UnifiedImportSplitButton(QPushButton):
    """
    单一整洁的 WinUI 3 音乐导入分割按钮 (Split Button)
    左侧主区域点击：直接添加本地音频文件
    右侧下拉箭头点击：弹出平滑动画菜单（添加音乐文件 / 扫描音乐文件夹 / 导入本地 LRC 歌词）
    彻底解决「导入音乐按钮重复」与底部按钮堆砌问题。
    """

    importFilesClicked = pyqtSignal()
    menuRequested = pyqtSignal(QPoint)

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self._hover_alpha: float = 0.0
        self.setFixedHeight(34)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMouseTracking(True)
        self.setToolTip("左侧点击直接导入音乐文件，右侧箭头展开导入文件夹 / LRC 歌词菜单")

        self._hover_anim = QVariantAnimation(self)
        self._hover_anim.setDuration(150)
        self._hover_anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._hover_anim.valueChanged.connect(self._on_hover_val)

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

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            if event.position().x() >= self.width() - 36.0:
                menu_pos = self.mapToGlobal(QPoint(0, -120))
                self.menuRequested.emit(menu_pos)
            else:
                self.importFilesClicked.emit()
            event.accept()
            return
        super().mousePressEvent(event)

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        rect = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        path = QPainterPath()
        path.addRoundedRect(rect, 7.0, 7.0)

        bg = QColor(self.theme.sage_soft if self._hover_alpha > 0.3 else self.theme.oat_surface)
        border = self.theme.sage_border if self._hover_alpha > 0.3 else self.theme.stroke_outer
        painter.fillPath(path, bg)
        painter.setPen(QPen(border, 1.0))
        painter.drawPath(path)

        # 右侧分割线与下拉箭头
        split_x = self.width() - 34.0
        painter.setPen(QPen(self.theme.divider, 1.0))
        painter.drawLine(QPointF(split_x, 7.0), QPointF(split_x, self.height() - 7.0))

        fg = self.theme.charcoal if self._hover_alpha > 0.2 else self.theme.slate_dark
        draw_nordic_icon(
            painter,
            "plus",
            QRectF(16.0, self.height() / 2.0 - 7.0, 14.0, 14.0),
            self.theme.sage_accent,
            stroke_w=1.5,
        )

        painter.setPen(fg)
        painter.setFont(get_nordic_font(12, QFont.Weight.Medium))
        painter.drawText(
            QRectF(36.0, 0.0, split_x - 36.0, self.height()),
            int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
            "导入本地音乐与歌词",
        )

        draw_nordic_icon(
            painter,
            "chevron_down",
            QRectF(split_x, 0.0, 34.0, self.height()),
            fg,
            stroke_w=1.35,
        )


class NavigationPane(QWidget):
    trackSelected = pyqtSignal(int)
    favoriteChanged = pyqtSignal(int)
    trackActionRequested = pyqtSignal(int, str)
    importFilesRequested = pyqtSignal()
    importFolderRequested = pyqtSignal()
    importLrcRequested = pyqtSignal()

    EXPANDED_WIDTH = 252

    def __init__(self, tracks: List[NordicTrack], parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.tracks = tracks
        self.active_index: int = 0
        self.filter_favorites_only: bool = False
        self.search_keyword: str = ""
        self.theme: NordicThemeColors = DAYLIGHT_THEME

        self.setMinimumWidth(self.EXPANDED_WIDTH)
        self.setMaximumWidth(self.EXPANDED_WIDTH)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(12, 12, 12, 14)
        layout.setSpacing(8)

        # 1. 顶部滑动胶囊分段切换器 (全部 / 喜欢)
        self.segmented_tabs = WinUISegmentedControl(["全部 (0)", "喜欢 (0)"], self)
        self.segmented_tabs.currentIndexChanged.connect(self._on_tab_index_changed)
        layout.addWidget(self.segmented_tabs)

        # 2. 自定义矢量搜索框
        self.search_box = WinUISearchBox("搜索歌名 / 歌手 / 专辑…", self)
        self.search_box.textChanged.connect(self._on_search_changed)
        layout.addWidget(self.search_box)

        # 3. 可滚动曲目容器
        self.scroll_area = QScrollArea(self)
        self.scroll_area.setWidgetResizable(True)
        self.scroll_area.setFrameShape(QFrame.Shape.NoFrame)
        self.scroll_area.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.scroll_area.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)
        self.scroll_area.setStyleSheet(
            """
            QScrollArea { background: transparent; border: none; }
            QScrollBar:vertical {
                background: transparent;
                width: 5px;
                margin: 2px 0;
            }
            QScrollBar::handle:vertical {
                background: rgba(118, 125, 134, 0.28);
                border-radius: 2px;
                min-height: 24px;
            }
            QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {
                height: 0px;
            }
            """
        )

        self.list_container = QWidget(self.scroll_area)
        self.list_container.setStyleSheet("background: transparent;")
        self.list_vbox = QVBoxLayout(self.list_container)
        self.list_vbox.setContentsMargins(0, 0, 0, 0)
        self.list_vbox.setSpacing(4)
        self.scroll_area.setWidget(self.list_container)
        layout.addWidget(self.scroll_area, 1)

        self.item_buttons: List[NavTrackItemButton] = []

        # 4. 底部单一整合式导入按钮 (Split Button + Animated Flyout)
        self.btn_import_split = UnifiedImportSplitButton(self)
        self.btn_import_split.importFilesClicked.connect(self.importFilesRequested.emit)
        self.btn_import_split.menuRequested.connect(self._open_import_flyout)
        layout.addWidget(self.btn_import_split)

        # 列表切换非线性动效驱动器 (错峰级联四次减速)
        self._list_reveal_anim = QVariantAnimation(self)
        self._list_reveal_anim.setDuration(320)
        self._list_reveal_anim.setEasingCurve(QEasingCurve.Type.OutQuint)
        self._list_reveal_anim.valueChanged.connect(self._on_list_reveal_val)

        self.rebuild_list(tracks, active_index=0)
        self.set_theme(DAYLIGHT_THEME)

    def _open_import_flyout(self, global_pos: QPoint) -> None:
        items = [
            ("add_files", "music_file", "添加音频文件 (.mp3/.flac/.wav)"),
            ("add_folder", "folder", "扫描并导入音乐文件夹"),
            ("add_lrc", "lrc", "为当前歌曲导入 LRC 歌词"),
        ]
        self._import_flyout = WinUIFlyoutMenu(items, theme=self.theme, parent=self.window())
        self._import_flyout.actionTriggered.connect(self._on_import_flyout_action)
        self._import_flyout.popup_animated(global_pos)

    def _on_import_flyout_action(self, act: str) -> None:
        if act == "add_files":
            self.importFilesRequested.emit()
        elif act == "add_folder":
            self.importFolderRequested.emit()
        elif act == "add_lrc":
            self.importLrcRequested.emit()

    def _on_list_reveal_val(self, val: float) -> None:
        p = float(val)
        visible_btns = [b for b in self.item_buttons if b.isVisible()]
        total = len(visible_btns)
        if total == 0:
            return
        # 非线性级联错峰入场 (Staggered Non-linear Cascade)
        for i, btn in enumerate(visible_btns):
            stagger = (min(i, 10) / 10.0) * 0.36
            sub_p = max(0.0, min(1.0, (p - stagger) / max(0.01, 1.0 - stagger)))
            eased = 1.0 - (1.0 - sub_p) ** 4
            btn.set_reveal_progress(eased)

    def _trigger_list_transition(self) -> None:
        self._list_reveal_anim.stop()
        self._list_reveal_anim.setStartValue(0.0)
        self._list_reveal_anim.setEndValue(1.0)
        self._list_reveal_anim.start()

    def _on_tab_index_changed(self, idx: int) -> None:
        self.filter_favorites_only = idx == 1
        self._apply_visibility_filter()
        self._trigger_list_transition()

    def _on_search_changed(self, text: str) -> None:
        self.search_keyword = text.strip().lower()
        self._apply_visibility_filter()
        self._trigger_list_transition()

    def _apply_visibility_filter(self) -> None:
        fav_count = sum(1 for t in self.tracks if t.is_favorite)
        self.segmented_tabs.set_item_text(0, f"全部 ({len(self.tracks)})")
        self.segmented_tabs.set_item_text(1, f"喜欢 ({fav_count})")

        for btn in self.item_buttons:
            t = btn.track
            match_fav = (not self.filter_favorites_only) or t.is_favorite
            match_kw = (
                not self.search_keyword
                or self.search_keyword in t.title.lower()
                or self.search_keyword in t.artist.lower()
                or self.search_keyword in (t.album or "").lower()
            )
            btn.setVisible(match_fav and match_kw)

    def rebuild_list(self, tracks: List[NordicTrack], active_index: int = 0) -> None:
        self.tracks = tracks
        self.active_index = active_index

        while self.list_vbox.count():
            item = self.list_vbox.takeAt(0)
            w = item.widget()
            if w is not None:
                w.deleteLater()

        self.item_buttons.clear()
        for idx, track in enumerate(tracks):
            btn = NavTrackItemButton(idx, track, selected=(idx == active_index), parent=self.list_container)
            btn.set_theme(self.theme)
            btn.clicked.connect(lambda checked=False, i=idx: self._on_item_clicked(i))
            btn.favoriteToggled.connect(self._on_item_fav_toggled)
            btn.contextActionRequested.connect(self.trackActionRequested.emit)
            self.list_vbox.addWidget(btn)
            self.item_buttons.append(btn)

        self.list_vbox.addStretch(1)
        self._apply_visibility_filter()

    def _on_item_fav_toggled(self, index: int) -> None:
        self._apply_visibility_filter()
        self.favoriteChanged.emit(index)

    def refresh_item(self, index: int) -> None:
        if 0 <= index < len(self.item_buttons):
            self.item_buttons[index].refresh_thumbnail()
            self.item_buttons[index].update()
        self._apply_visibility_filter()

    def _on_item_clicked(self, index: int) -> None:
        self.set_active_index(index)
        self.trackSelected.emit(index)

    def set_active_index(self, index: int) -> None:
        self.active_index = index
        for i, btn in enumerate(self.item_buttons):
            btn.set_selected(i == index)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.segmented_tabs.set_theme(theme)
        self.search_box.set_theme(theme)
        self.btn_import_split.set_theme(theme)
        for btn in self.item_buttons:
            btn.set_theme(theme)
        self.update()

    def paintEvent(self, event) -> None:
        if self.width() <= 2:
            return
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setPen(QPen(self.theme.divider, 1.0))
        painter.drawLine(self.width() - 1, 0, self.width() - 1, self.height())
