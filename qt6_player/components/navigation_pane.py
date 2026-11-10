import logging as _logging
import concurrent.futures
import math
import os
import subprocess
import threading
from pathlib import Path
from typing import Any, List, Optional, Dict, Set
from PyQt6.QtCore import (
    Qt,
    pyqtSignal,
    QRectF,
    QPointF,
    QPoint,
    QTimer,
    QVariantAnimation,
    QEasingCurve,
)
from PyQt6.QtGui import (
    QPainter,
    QPen,
    QColor,
    QFont,
    QFontMetrics,
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
    QLabel,
    QLineEdit,
    QCheckBox,
    QFileDialog,
)

from qt6_player.data.tracks import NordicTrack, format_timecode
from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font
from qt6_player.components.fluent_widgets import (
    draw_nordic_icon,
    WinUISegmentedControl,
    WinUISearchBox,
    WinUIFlyoutMenu,
    WinUIModalDialog,
)
from qt6_player.services.netease_service import netease_manager
from qt6_player.components.netease_login_dialog import NeteaseLoginDialog


class TrackMetadataDialog(WinUIModalDialog):
    metadataSaved = pyqtSignal(str, str, str, bool)

    def __init__(
        self,
        track: NordicTrack,
        theme: NordicThemeColors = DAYLIGHT_THEME,
        parent: Optional[QWidget] = None,
    ):
        super().__init__("编辑曲目信息", 420, 336, theme=theme, parent=parent)
        self.track = track
        self._setup_body()

    def _setup_body(self) -> None:
        vbox = self._root_vbox
        t = self.theme
        input_style = f"""
            QLineEdit {{
                background-color: {t.card_elevated.name()};
                color: {t.charcoal.name()};
                border: 1px solid rgba({t.sage_accent.red()}, {t.sage_accent.green()}, {t.sage_accent.blue()}, 0.35);
                border-radius: 6px;
                padding: 6px 10px;
            }}
        """

        def _make_field(label_txt: str, init_val: str) -> QLineEdit:
            row = QVBoxLayout()
            row.setSpacing(4)
            lbl = QLabel(label_txt, self)
            lbl.setFont(get_nordic_font(11, QFont.Weight.Medium))
            lbl.setStyleSheet(f"color: {t.slate_muted.name()}; background: transparent;")
            edit = QLineEdit(init_val, self)
            edit.setFont(get_nordic_font(12))
            edit.setStyleSheet(input_style)
            row.addWidget(lbl)
            row.addWidget(edit)
            vbox.addLayout(row)
            return edit

        self.edit_title = _make_field("歌曲名称", self.track.title)
        self.edit_artist = _make_field("歌手", self.track.artist)
        self.edit_album = _make_field("专辑", self.track.album or "")

        self.cb_write_file = QCheckBox("同时写入音频文件内嵌标签与封面", self)
        has_local_file = bool(self.track.file_path and Path(self.track.file_path).is_file())
        self.cb_write_file.setChecked(has_local_file)
        self.cb_write_file.setEnabled(has_local_file)
        self.cb_write_file.setFont(get_nordic_font(11, QFont.Weight.Medium))
        self.cb_write_file.setStyleSheet(
            f"QCheckBox {{ color: {t.slate_dark.name()}; padding-top: 4px; background: transparent; }} "
            f"QCheckBox::indicator {{ width: 15px; height: 15px; border-radius: 4px; border: 1px solid {t.sage_border.name()}; }} "
            f"QCheckBox::indicator:checked {{ background: {t.sage_accent.name()}; border-color: {t.sage_accent.name()}; }}"
        )
        vbox.addWidget(self.cb_write_file)

        vbox.addStretch(1)
        btn_row = QHBoxLayout()
        btn_row.addStretch(1)

        btn_cancel = QPushButton("取消", self)
        btn_cancel.setFixedSize(76, 30)
        btn_cancel.setCursor(Qt.CursorShape.PointingHandCursor)
        btn_cancel.setFont(get_nordic_font(12, QFont.Weight.Medium))
        btn_cancel.setStyleSheet(
            f"QPushButton {{ background: {t.oat_surface.name()}; color: {t.slate_dark.name()}; border-radius: 6px; border: 1px solid {t.stroke_outer.name()}; }}"
        )
        btn_cancel.clicked.connect(self.reject)

        btn_save = QPushButton("保存", self)
        btn_save.setFixedSize(88, 30)
        btn_save.setCursor(Qt.CursorShape.PointingHandCursor)
        btn_save.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        btn_save.setStyleSheet(
            f"QPushButton {{ background: {t.sage_accent.name()}; color: #FAF9F6; border-radius: 6px; border: none; }}"
        )
        btn_save.clicked.connect(self._on_save)

        btn_row.addWidget(btn_cancel)
        btn_row.addSpacing(8)
        btn_row.addWidget(btn_save)
        vbox.addLayout(btn_row)

    def _on_save(self) -> None:
        new_title = self.edit_title.text().strip() or self.track.title
        new_artist = self.edit_artist.text().strip() or "未知歌手"
        new_album = self.edit_album.text().strip()
        self.metadataSaved.emit(new_title, new_artist, new_album, self.cb_write_file.isChecked())
        self.accept()


class CreatePlaylistDialog(WinUIModalDialog):
    playlistCreated = pyqtSignal(str)

    def __init__(
        self,
        title_text: str = "新建歌单",
        initial_name: str = "",
        theme: NordicThemeColors = DAYLIGHT_THEME,
        parent: Optional[QWidget] = None,
    ):
        super().__init__(title_text, 360, 185, theme=theme, parent=parent)
        t = self.theme
        vbox = self._root_vbox

        lbl = QLabel("歌单名称", self)
        lbl.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl.setStyleSheet(f"color: {t.slate_muted.name()}; background: transparent;")
        vbox.addWidget(lbl)

        self.edit_name = QLineEdit(initial_name, self)
        self.edit_name.setPlaceholderText("例如：常听无损、古典现场…")
        self.edit_name.setFont(get_nordic_font(12))
        self.edit_name.setStyleSheet(
            f"QLineEdit {{ background-color: {t.card_elevated.name()}; color: {t.charcoal.name()}; "
            f"border: 1px solid {t.sage_border.name()}; border-radius: 6px; padding: 6px 10px; }}"
        )
        self.edit_name.returnPressed.connect(self._on_confirm)
        vbox.addWidget(self.edit_name)

        vbox.addStretch(1)
        btn_row = QHBoxLayout()
        btn_row.addStretch(1)

        btn_cancel = QPushButton("取消", self)
        btn_cancel.setFixedSize(72, 30)
        btn_cancel.setCursor(Qt.CursorShape.PointingHandCursor)
        btn_cancel.setFont(get_nordic_font(12, QFont.Weight.Medium))
        btn_cancel.setStyleSheet(
            f"QPushButton {{ background: {t.oat_surface.name()}; color: {t.slate_dark.name()}; border-radius: 6px; border: 1px solid {t.stroke_outer.name()}; }}"
        )
        btn_cancel.clicked.connect(self.reject)

        btn_ok = QPushButton("确定", self)
        btn_ok.setFixedSize(78, 30)
        btn_ok.setCursor(Qt.CursorShape.PointingHandCursor)
        btn_ok.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        btn_ok.setStyleSheet(
            f"QPushButton {{ background: {t.sage_accent.name()}; color: #FAF9F6; border-radius: 6px; border: none; }}"
        )
        btn_ok.clicked.connect(self._on_confirm)

        btn_row.addWidget(btn_cancel)
        btn_row.addSpacing(8)
        btn_row.addWidget(btn_ok)
        vbox.addLayout(btn_row)

    def _on_confirm(self) -> None:
        name = self.edit_name.text().strip()
        if name:
            self.playlistCreated.emit(name)
            self.accept()


class WatchedFoldersDialog(WinUIModalDialog):
    watchedFoldersChanged = pyqtSignal(list)
    rescanRequested = pyqtSignal()

    def __init__(
        self,
        folders: List[str],
        theme: NordicThemeColors = DAYLIGHT_THEME,
        parent: Optional[QWidget] = None,
    ):
        super().__init__("自动监控音乐文件夹", 460, 340, theme=theme, parent=parent)
        self.folders: List[str] = list(folders)
        self._setup_body()

    def _setup_body(self) -> None:
        vbox = self._root_vbox
        t = self.theme

        tip = QLabel("添加常用目录（如 Music 或 Downloads）后，启动时或目录内新增无损音频与 CUE 文件时将自动导入曲库。", self)
        tip.setWordWrap(True)
        tip.setFont(get_nordic_font(11, QFont.Weight.Normal))
        tip.setStyleSheet(f"color: {t.slate_muted.name()}; background: transparent;")
        vbox.addWidget(tip)

        self.scroll = QScrollArea(self)
        self.scroll.setWidgetResizable(True)
        self.scroll.setStyleSheet(
            f"QScrollArea {{ border: 1px solid {t.stroke_outer.name()}; border-radius: 7px; background: {t.card_elevated.name()}; }}"
        )
        self.list_widget = QWidget()
        self.list_widget.setStyleSheet("background: transparent;")
        self.list_vbox = QVBoxLayout(self.list_widget)
        self.list_vbox.setContentsMargins(8, 8, 8, 8)
        self.list_vbox.setSpacing(6)
        self.scroll.setWidget(self.list_widget)
        vbox.addWidget(self.scroll, 1)

        self._rebuild_folder_rows()

        btn_row = QHBoxLayout()
        btn_add = QPushButton("＋ 添加文件夹", self)
        btn_add.setFixedHeight(30)
        btn_add.setCursor(Qt.CursorShape.PointingHandCursor)
        btn_add.setFont(get_nordic_font(11, QFont.Weight.DemiBold))
        btn_add.setStyleSheet(
            f"QPushButton {{ background: {t.oat_surface.name()}; color: {t.charcoal.name()}; border-radius: 6px; border: 1px solid {t.sage_border.name()}; padding: 0 12px; }}"
        )
        btn_add.clicked.connect(self._on_add_folder)
        btn_row.addWidget(btn_add)

        btn_scan = QPushButton("立即扫描", self)
        btn_scan.setFixedHeight(30)
        btn_scan.setCursor(Qt.CursorShape.PointingHandCursor)
        btn_scan.setFont(get_nordic_font(11, QFont.Weight.Medium))
        btn_scan.setStyleSheet(
            f"QPushButton {{ background: {t.oat_surface.name()}; color: {t.slate_dark.name()}; border-radius: 6px; border: 1px solid {t.stroke_outer.name()}; padding: 0 12px; }}"
        )
        btn_scan.clicked.connect(lambda: (self.watchedFoldersChanged.emit(list(self.folders)), self.rescanRequested.emit()))
        btn_row.addWidget(btn_scan)

        btn_row.addStretch(1)

        btn_done = QPushButton("完成", self)
        btn_done.setFixedSize(76, 30)
        btn_done.setCursor(Qt.CursorShape.PointingHandCursor)
        btn_done.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        btn_done.setStyleSheet(
            f"QPushButton {{ background: {t.sage_accent.name()}; color: #FAF9F6; border-radius: 6px; border: none; }}"
        )
        btn_done.clicked.connect(self.accept)
        btn_row.addWidget(btn_done)
        vbox.addLayout(btn_row)

    def _rebuild_folder_rows(self) -> None:
        while self.list_vbox.count():
            item = self.list_vbox.takeAt(0)
            w = item.widget()
            if w is not None:
                w.hide()
                w.setParent(None)
                w.deleteLater()

        t = self.theme
        if not self.folders:
            empty_lbl = QLabel("暂未绑定监控文件夹", self.list_widget)
            empty_lbl.setFont(get_nordic_font(11, QFont.Weight.Normal))
            empty_lbl.setAlignment(Qt.AlignmentFlag.AlignCenter)
            empty_lbl.setStyleSheet(f"color: {t.slate_faint.name()}; padding: 28px;")
            self.list_vbox.addWidget(empty_lbl)
        else:
            for folder_path in self.folders:
                row_w = QWidget(self.list_widget)
                row_w.setStyleSheet(
                    f"background: {t.oat_surface.name()}; border-radius: 5px;"
                )
                row_h = QHBoxLayout(row_w)
                row_h.setContentsMargins(10, 6, 8, 6)
                row_h.setSpacing(8)

                lbl_p = QLabel(folder_path, row_w)
                lbl_p.setFont(get_nordic_font(11, QFont.Weight.Normal))
                lbl_p.setStyleSheet(f"color: {t.charcoal.name()}; background: transparent;")
                row_h.addWidget(lbl_p, 1)

                btn_rm = QPushButton("移除", row_w)
                btn_rm.setFixedSize(46, 22)
                btn_rm.setCursor(Qt.CursorShape.PointingHandCursor)
                btn_rm.setFont(get_nordic_font(10, QFont.Weight.Medium))
                btn_rm.setStyleSheet(
                    f"QPushButton {{ background: transparent; color: {t.slate_muted.name()}; border: 1px solid {t.stroke_outer.name()}; border-radius: 4px; }}"
                    f"QPushButton:hover {{ color: {t.charcoal.name()}; border-color: {t.sage_border.name()}; }}"
                )
                btn_rm.clicked.connect(lambda _=False, fp=folder_path: self._on_remove_folder(fp))
                row_h.addWidget(btn_rm)
                self.list_vbox.addWidget(row_w)
        self.list_vbox.addStretch(1)

    def _on_add_folder(self) -> None:
        folder = QFileDialog.getExistingDirectory(self, "选择要自动监控的音乐文件夹")
        if folder:
            norm = str(Path(folder).resolve())
            if norm not in self.folders:
                self.folders.append(norm)
                self._rebuild_folder_rows()
                self.watchedFoldersChanged.emit(list(self.folders))

    def _on_remove_folder(self, fp: str) -> None:
        if fp in self.folders:
            self.folders.remove(fp)
            self._rebuild_folder_rows()
            self.watchedFoldersChanged.emit(list(self.folders))


class NavGroupHeaderButton(QPushButton):
    """分类视图下的可折叠分组标题栏（如：▾ 王菲 · 3 首）"""

    def __init__(
        self,
        group_name: str,
        count: int,
        expanded: bool = True,
        theme: NordicThemeColors = DAYLIGHT_THEME,
        backdrop_material: str = "mica",
        parent: Optional[QWidget] = None,
    ):
        super().__init__(parent)
        self.group_name = group_name
        self.count = count
        self.expanded = expanded
        self.theme = theme
        self.backdrop_material = str(backdrop_material)
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        self.setFixedHeight(32)
        self.setCursor(Qt.CursorShape.PointingHandCursor)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def set_backdrop_material(self, material: str) -> None:
        self.backdrop_material = str(material)
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        self.update()

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        rect = QRectF(self.rect()).adjusted(2.0, 2.0, -2.0, -2.0)
        path = QPainterPath()
        path.addRoundedRect(rect, 6.0, 6.0)

        is_dusk = self.theme.name == "dusk"
        if self.is_translucent_backdrop:
            bg = QColor(255, 255, 255, 14) if is_dusk else QColor(0, 0, 0, 10)
        else:
            bg = self.theme.oat_surface
        painter.fillPath(path, bg)

        arrow = "▾" if self.expanded else "▸"
        painter.setPen(self.theme.sage_accent)
        painter.setFont(get_nordic_font(11, QFont.Weight.Bold))
        painter.drawText(
            QRectF(10.0, 0.0, 16.0, self.height()),
            int(Qt.AlignmentFlag.AlignCenter),
            arrow,
        )

        painter.setPen(self.theme.charcoal)
        painter.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        avail_w = max(40.0, self.width() - 78.0)
        elided = painter.fontMetrics().elidedText(
            self.group_name, Qt.TextElideMode.ElideRight, int(avail_w)
        )
        painter.drawText(
            QRectF(28.0, 0.0, avail_w, self.height()),
            int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
            elided,
        )

        painter.setPen(self.theme.slate_muted)
        painter.setFont(get_nordic_font(10, QFont.Weight.Medium, mono=True))
        painter.drawText(
            QRectF(self.width() - 52.0, 0.0, 42.0, self.height()),
            int(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter),
            f"{self.count} 首",
        )


class NavTrackItemButton(QPushButton):
    favoriteToggled = pyqtSignal(int)
    contextActionRequested = pyqtSignal(int, str)  # track_index, action_name
    dragReorderRequested = pyqtSignal(int, int)    # from_index, to_index

    def __init__(
        self,
        index: int,
        track: NordicTrack,
        selected: bool = False,
        in_custom_playlist: bool = False,
        parent: Optional[QWidget] = None,
    ):
        super().__init__(parent)
        self.index = index
        self.track = track
        self.is_selected = selected
        self.in_custom_playlist = in_custom_playlist
        self.is_playing = False
        self.eq_phase: float = 0.0
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.backdrop_material: str = "mica"
        self.is_translucent_backdrop: bool = True
        self._import_flyout: Optional[WinUIFlyoutMenu] = None
        self._ctx_menu: Optional[WinUIFlyoutMenu] = None
        self._thumb_pixmap: Optional[QPixmap] = None
        self._hover_alpha: float = 0.0
        self._reveal_progress: float = 1.0
        self._fav_scale: float = 1.0
        self._press_y: Optional[float] = None
        self._dragging_item: bool = False

        self.setFixedHeight(52)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMouseTracking(True)

        self._hover_anim = QVariantAnimation(self)
        self._hover_anim.setDuration(150)
        self._hover_anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._hover_anim.valueChanged.connect(self._on_hover_val)

        self._fav_anim = QVariantAnimation(self)
        self._fav_anim.setDuration(260)
        fav_curve = QEasingCurve(QEasingCurve.Type.OutBack)
        fav_curve.setOvershoot(2.6)
        self._fav_anim.setEasingCurve(fav_curve)
        self._fav_anim.valueChanged.connect(self._on_fav_scale)

        self.refresh_thumbnail()

    def set_backdrop_material(self, material: str) -> None:
        self.backdrop_material = str(material)
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        self.update()

    def _on_hover_val(self, val: float) -> None:
        self._hover_alpha = float(val)
        self.update()

    def _on_fav_scale(self, val: float) -> None:
        self._fav_scale = float(val)
        self.update()

    def trigger_fav_bounce(self) -> None:
        self._fav_anim.stop()
        self._fav_anim.setStartValue(0.58)
        self._fav_anim.setEndValue(1.0)
        self._fav_anim.start()

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

    def refresh_thumbnail(self, force: bool = False) -> None:
        if not force and self._thumb_pixmap is not None and not self._thumb_pixmap.isNull():
            return
        base_pix = self.track.get_cover_pixmap() if self.track is not None else None
        if base_pix is not None and not base_pix.isNull():
            self._thumb_pixmap = base_pix.scaled(
                72,
                72,
                Qt.AspectRatioMode.KeepAspectRatioByExpanding,
                Qt.TransformationMode.SmoothTransformation,
            )
            self.update()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def set_selected(self, selected: bool) -> None:
        if self.is_selected != selected:
            self.is_selected = selected
            self.update()

    def _fav_rect(self) -> QRectF:
        return QRectF(self.width() - 62.0, self.height() / 2.0 - 9.0, 18.0, 18.0)

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            if self._fav_rect().adjusted(-3, -3, 3, 3).contains(event.position()):
                self.track.is_favorite = not self.track.is_favorite
                self.trigger_fav_bounce()
                self.favoriteToggled.emit(self.index)
                self.update()
                event.accept()
                return
            self._press_y = event.position().y()
            self._dragging_item = False
        elif event.button() == Qt.MouseButton.RightButton:
            self._show_context_menu(event.globalPosition().toPoint())
            event.accept()
            return
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if self._press_y is not None and (event.buttons() & Qt.MouseButton.LeftButton):
            dy = event.position().y() - self._press_y
            if abs(dy) >= 34.0:
                step = int(round(dy / 54.0))
                if step != 0:
                    self._dragging_item = True
                    self.dragReorderRequested.emit(self.index, self.index + step)
                    self._press_y = event.position().y()
                    return
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        self._press_y = None
        if self._dragging_item:
            self._dragging_item = False
            event.accept()
            return
        super().mouseReleaseEvent(event)

    def _show_context_menu(self, global_pos: QPoint) -> None:
        fav_label = "取消喜欢" if self.track.is_favorite else "添加到「我喜欢」"
        fav_icon = "heart_filled" if self.track.is_favorite else "heart_outline"
        items = [
            ("play", "music_file", "立即播放"),
            ("play_next", "music_file", "下一首播放"),
            ("fav", fav_icon, fav_label),
            ("add_to_playlist", "plus", "添加到歌单…"),
        ]
        if self.in_custom_playlist:
            items.append(("remove_from_playlist", "close", "从当前歌单移除"))
        items.extend(
            [
                ("scrape", "sparkle", "在线匹配词图"),
                ("import_lrc", "lrc", "加载本地 LRC 歌词"),
                ("edit_tags", "edit", "编辑曲目信息"),
            ]
        )
        if self.track.file_path and Path(self.track.file_path).exists():
            items.append(("write_tags", "sparkle", "写入标签与封面到文件"))
            items.append(("open_dir", "folder", "打开文件所在位置"))
        items.append(("remove", "trash", "从曲库移除"))

        self._ctx_menu = WinUIFlyoutMenu(items, theme=self.theme, backdrop_material=getattr(self, "backdrop_material", "mica"), parent=self.window())
        self._ctx_menu.actionTriggered.connect(self._on_ctx_action)
        self._ctx_menu.popup_animated(global_pos)

    def _on_ctx_action(self, act: str) -> None:
        if act == "fav":
            self.track.is_favorite = not self.track.is_favorite
            self.trigger_fav_bounce()
            self.favoriteToggled.emit(self.index)
            self.update()
        elif act == "open_dir":
            target_path = self.track.cue_path if (self.track.cue_path and Path(self.track.cue_path).exists()) else self.track.file_path
            if target_path:
                try:
                    subprocess.Popen(["explorer", "/select,", os.path.normpath(target_path)])
                except Exception:
                    _logging.getLogger(__name__).debug("ignored exception", exc_info=True)
        else:
            self.contextActionRequested.emit(self.index, act)

    def paintEvent(self, event) -> None:
        dirty = event.rect()
        painter = QPainter(self)
        painter.setClipRect(dirty)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)

        painter.setOpacity(self._reveal_progress)
        slide_y = (1.0 - self._reveal_progress) * 10.0
        painter.translate(0.0, slide_y)

        rect = QRectF(self.rect()).adjusted(1.0, 1.0, -1.0, -1.0)
        path = QPainterPath()
        path.addRoundedRect(rect, 7.0, 7.0)

        is_dusk = self.theme.name == "dusk"
        if self.is_selected:
            if is_dusk:
                sel_bg = QColor(255, 255, 255, 22 if self.is_translucent_backdrop else 32)
                sel_bd = QColor(255, 255, 255, 38 if self.is_translucent_backdrop else 50)
            else:
                sel_bg = QColor(255, 255, 255, 220)
                sel_bd = QColor(0, 0, 0, 30)
            painter.fillPath(path, sel_bg)
            painter.setPen(QPen(sel_bd, 1.0))
            painter.drawPath(path)

            pill_rect = QRectF(4.0, self.height() / 2.0 - 9.0, 3.2, 18.0)
            pill_path = QPainterPath()
            pill_path.addRoundedRect(pill_rect, 1.6, 1.6)
            painter.fillPath(pill_path, self.theme.sage_accent)
        elif self._hover_alpha > 0.01:
            if self.is_translucent_backdrop:
                hov_col = QColor(255, 255, 255, int((18 if is_dusk else 85) * self._hover_alpha))
            else:
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

            if not (self.is_selected and self.is_playing):
                painter.setPen(QColor(self.track.palette["ink"]))
                painter.setFont(get_nordic_font(11, QFont.Weight.DemiBold, mono=True))
                num_val = self.track.cue_track_num if self.track.cue_track_num > 0 else (self.index + 1)
                painter.drawText(swatch_rect, int(Qt.AlignmentFlag.AlignCenter), f"{num_val:02d}")

        # 正在播放曲目：绘制动态跳动三柱小音轨指示器
        if self.is_selected and self.is_playing:
            painter.save()
            painter.setClipPath(swatch_path)
            ov_col = QColor(24, 27, 32, 145) if self._thumb_pixmap is not None else QColor(self.theme.sage_soft)
            painter.fillPath(swatch_path, ov_col)
            bar_color = QColor("#FAF9F6") if self._thumb_pixmap is not None else self.theme.sage_accent
            painter.setPen(Qt.PenStyle.NoPen)
            painter.setBrush(bar_color)
            base_y = swatch_rect.bottom() - 7.0
            cx_s = swatch_rect.center().x()
            phases = (0.0, 2.1, 4.2)
            offsets = (-6.0, 0.0, 6.0)
            for bx_off, ph in zip(offsets, phases):
                ratio = 0.28 + 0.72 * (0.5 + 0.5 * math.sin(self.eq_phase + ph))
                bh = 4.0 + ratio * 13.0
                b_rect = QRectF(cx_s + bx_off - 1.4, base_y - bh, 2.8, bh)
                painter.drawRoundedRect(b_rect, 1.3, 1.3)
            painter.restore()

        if dirty.right() <= 50:
            return

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
        sub_txt = f"{self.track.artist} · {self.track.album}" if self.track.album else self.track.artist
        elided_artist = painter.fontMetrics().elidedText(
            sub_txt, Qt.TextElideMode.ElideRight, int(text_width)
        )
        painter.drawText(
            QRectF(text_left, 27.0, text_width, 17.0),
            int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
            elided_artist,
        )

        # 矢量心形收藏图标
        if self.track.is_favorite or self._hover_alpha > 0.1:
            fav_r = self._fav_rect()
            ic_col = self.theme.sage_accent if self.track.is_favorite else self.theme.slate_faint
            painter.save()
            fc = fav_r.center()
            painter.translate(fc)
            painter.scale(self._fav_scale, self._fav_scale)
            painter.translate(-fc)
            draw_nordic_icon(
                painter,
                "heart_filled" if self.track.is_favorite else "heart_outline",
                fav_r,
                ic_col,
                stroke_w=1.35,
            )
            painter.restore()

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
    音乐导入分割按钮 (Split Button)
    左侧主区域点击：直接添加音频或 CUE 文件
    右侧下拉箭头点击：弹出菜单（添加文件 / 扫描文件夹 / 自动监控目录 / 导入 LRC 歌词）
    """

    importFilesClicked = pyqtSignal()
    menuRequested = pyqtSignal(QPoint)

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self._hover_alpha: float = 0.0
        self.backdrop_material: str = "mica"
        self.is_translucent_backdrop: bool = True
        self.setFixedHeight(34)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setMouseTracking(True)
        self.setToolTip("左侧点击导入音频/CUE 文件，右侧箭头展开文件夹扫描与目录监控菜单")

        self._hover_anim = QVariantAnimation(self)
        self._hover_anim.setDuration(150)
        self._hover_anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._hover_anim.valueChanged.connect(self._on_hover_val)

    def set_backdrop_material(self, material: str) -> None:
        self.backdrop_material = str(material)
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        self.update()

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
                menu_pos = self.mapToGlobal(QPoint(0, -155))
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

        is_dusk = self.theme.name == "dusk"
        if is_dusk:
            if self.is_translucent_backdrop:
                bg = QColor(255, 255, 255, int(18 + 20 * self._hover_alpha))
                border = QColor(255, 255, 255, int(32 + 25 * self._hover_alpha))
                div_col = QColor(255, 255, 255, 20)
            else:
                bg = QColor(self.theme.sage_soft if self._hover_alpha > 0.3 else self.theme.oat_surface)
                border = self.theme.sage_border if self._hover_alpha > 0.3 else self.theme.stroke_outer
                div_col = self.theme.divider
            fg = self.theme.charcoal if self._hover_alpha > 0.2 else self.theme.slate_dark
        else:
            # 浅色模式下：高白度底色配合清晰暗烟描边与高对比度文字
            bg = QColor(255, 255, 255, int(195 + 60 * self._hover_alpha))
            border = (
                self.theme.sage_border
                if self._hover_alpha > 0.4
                else QColor(0, 0, 0, int(28 + 22 * self._hover_alpha))
            )
            div_col = QColor(0, 0, 0, 26)
            fg = QColor("#181B1E") if self._hover_alpha > 0.2 else QColor("#282D34")

        painter.fillPath(path, bg)
        painter.setPen(QPen(border, 1.0))
        painter.drawPath(path)

        split_x = self.width() - 34.0
        painter.setPen(QPen(div_col, 1.0))
        painter.drawLine(QPointF(split_x, 7.0), QPointF(split_x, self.height() - 7.0))

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
            "导入音乐 / CUE / 歌词",
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
    trackReordered = pyqtSignal(int, int)  # from_idx, to_idx
    importFilesRequested = pyqtSignal()
    importFolderRequested = pyqtSignal()
    importLrcRequested = pyqtSignal()
    watchFoldersRequested = pyqtSignal()
    playlistsChanged = pyqtSignal()
    neteaseTrackSelected = pyqtSignal(object, list, int)  # selected_track, playlist_tracks, index_in_playlist
    _thumbnailsRefreshed = pyqtSignal()
    _cloudSearchDone = pyqtSignal(str, list)

    EXPANDED_WIDTH = 276

    _SORT_LABELS = {
        "default": "默认排序",
        "title": "按歌名",
        "artist": "按歌手",
        "album": "按专辑",
        "duration": "按时长",
        "recent": "最近添加",
    }

    _GROUP_LABELS = {
        "artist": "按歌手分组",
        "album": "按专辑分组",
        "folder": "按文件夹分组",
    }

    def __init__(self, tracks: List[NordicTrack], parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.local_tracks: List[NordicTrack] = list(tracks) if tracks else []
        self.netease_tracks: List[NordicTrack] = []
        self.tracks: List[NordicTrack] = self.local_tracks
        self.active_index: int = 0
        self._active_track_id: str = ""
        self.is_playing: bool = False
        self._eq_phase: float = 0.0

        # 视图模式: 0="全部", 1="喜欢", 2="歌单", 3="分类", 4="网易云"
        self.view_mode: int = 0
        self.filter_favorites_only: bool = False
        self.search_keyword: str = ""
        self.sort_mode: str = "default"
        self.group_mode: str = "artist"  # "artist" | "album" | "folder"
        self.collapsed_groups: Set[str] = set()

        # 自定义歌单: {歌单名: [track_id, ...]}
        self.custom_playlists: Dict[str, List[str]] = {}
        self.active_playlist_name: str = ""

        # 网易云音乐状态
        self.netease_tracks: List[NordicTrack] = []
        self._netease_playlist_tracks: List[NordicTrack] = []
        self.netease_sub_mode: str = "daily"  # "daily" | "pl:{playlist_id}"
        self.netease_sub_name: str = "每日推荐"
        self.is_netease_loading: bool = False
        self.is_netease_searching: bool = False
        self._netease_search_keyword: str = ""

        self._netease_search_timer = QTimer(self)
        self._netease_search_timer.setSingleShot(True)
        self._netease_search_timer.setInterval(400)
        self._netease_search_timer.timeout.connect(self._do_netease_cloud_search)

        self._netease_load_timeout_timer = QTimer(self)
        self._netease_load_timeout_timer.setSingleShot(True)
        self._netease_load_timeout_timer.timeout.connect(self._on_netease_load_timeout)

        netease_manager.loginStatusChanged.connect(self._on_netease_login_status_changed)
        netease_manager.playlistsLoaded.connect(self._on_netease_playlists_loaded)
        netease_manager.dailyRecommendLoaded.connect(self._on_daily_recommend_loaded)
        netease_manager.playlistTracksLoaded.connect(self._on_playlist_tracks_loaded)
        self._thumbnailsRefreshed.connect(self._refresh_item_thumbnails)
        self._cloudSearchDone.connect(self._on_cloud_search_done)

        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.backdrop_material: str = "mica"
        self.is_translucent_backdrop: bool = True

        self.setMinimumWidth(self.EXPANDED_WIDTH)
        self.setMaximumWidth(self.EXPANDED_WIDTH)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(12, 12, 12, 14)
        layout.setSpacing(8)

        # 1. 顶部五栏视图切换器 (全部 / 喜欢 / 歌单 / 分类 / 网易云)
        self.segmented_tabs = WinUISegmentedControl(["全部", "喜欢", "歌单", "分类", "网易云"], self)
        self.segmented_tabs.currentIndexChanged.connect(self._on_tab_index_changed)
        layout.addWidget(self.segmented_tabs)

        # 2. 搜索框
        self.search_box = WinUISearchBox("搜索歌名 / 歌手 / 专辑…", self)
        self.search_box.textChanged.connect(self._on_search_changed)
        self.search_box.returnPressed.connect(self._on_search_return_pressed)
        layout.addWidget(self.search_box)

        # 3. 辅助工具栏（排序 / 歌单选择与新建 / 分类切换）
        self.sub_bar = QWidget(self)
        self.sub_bar.setFixedHeight(26)
        sub_hbox = QHBoxLayout(self.sub_bar)
        sub_hbox.setContentsMargins(2, 0, 2, 0)
        sub_hbox.setSpacing(6)

        self.lbl_count = QLabel("共 0 首", self.sub_bar)
        self.lbl_count.setFont(get_nordic_font(11, QFont.Weight.Normal))
        sub_hbox.addWidget(self.lbl_count)

        self.btn_sub_selector = QPushButton("", self.sub_bar)
        self.btn_sub_selector.setFixedHeight(24)
        self.btn_sub_selector.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_sub_selector.setFont(get_nordic_font(11, QFont.Weight.DemiBold))
        self.btn_sub_selector.clicked.connect(self._on_sub_selector_clicked)
        self.btn_sub_selector.hide()
        sub_hbox.addWidget(self.btn_sub_selector)

        sub_hbox.addStretch(1)

        self.btn_new_playlist = QPushButton("＋新建", self.sub_bar)
        self.btn_new_playlist.setFixedHeight(24)
        self.btn_new_playlist.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_new_playlist.setFont(get_nordic_font(11, QFont.Weight.Medium))
        self.btn_new_playlist.clicked.connect(self.prompt_create_playlist)
        self.btn_new_playlist.hide()
        sub_hbox.addWidget(self.btn_new_playlist)

        self.btn_del_playlist = QPushButton("删除", self.sub_bar)
        self.btn_del_playlist.setFixedHeight(24)
        self.btn_del_playlist.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_del_playlist.setFont(get_nordic_font(11, QFont.Weight.Medium))
        self.btn_del_playlist.clicked.connect(self._on_delete_current_playlist)
        self.btn_del_playlist.hide()
        sub_hbox.addWidget(self.btn_del_playlist)

        self.btn_sort = QPushButton("默认排序 ▾", self.sub_bar)
        self.btn_sort.setFixedHeight(24)
        self.btn_sort.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_sort.setFont(get_nordic_font(11, QFont.Weight.Medium))
        self.btn_sort.clicked.connect(self._open_sort_menu)
        sub_hbox.addWidget(self.btn_sort)

        self.btn_netease_logout = QPushButton("退出登录", self.sub_bar)
        self.btn_netease_logout.setFixedHeight(24)
        self.btn_netease_logout.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_netease_logout.setFont(get_nordic_font(11, QFont.Weight.Medium))
        self.btn_netease_logout.clicked.connect(self._on_netease_logout_clicked)
        self.btn_netease_logout.hide()
        sub_hbox.addWidget(self.btn_netease_logout)

        self.btn_netease_login = QPushButton("登录", self.sub_bar)
        self.btn_netease_login.setFixedHeight(24)
        self.btn_netease_login.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_netease_login.setFont(get_nordic_font(11, QFont.Weight.Medium))
        self.btn_netease_login.clicked.connect(self._open_netease_login_dialog)
        self.btn_netease_login.hide()
        sub_hbox.addWidget(self.btn_netease_login)

        layout.addWidget(self.sub_bar)

        # 4. 可滚动曲目容器
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
        self.group_buttons: List[NavGroupHeaderButton] = []

        # 5. 底部导入分割按钮
        self.btn_import_split = UnifiedImportSplitButton(self)
        self.btn_import_split.importFilesClicked.connect(self.importFilesRequested.emit)
        self.btn_import_split.menuRequested.connect(self._open_import_flyout)
        layout.addWidget(self.btn_import_split)

        self._list_reveal_anim = QVariantAnimation(self)
        self._list_reveal_anim.setDuration(280)
        self._list_reveal_anim.setEasingCurve(QEasingCurve.Type.OutQuint)
        self._list_reveal_anim.valueChanged.connect(self._on_list_reveal_val)

        self._eq_timer = QTimer(self)
        self._eq_timer.setInterval(55)
        self._eq_timer.timeout.connect(self._step_eq_bars)

        self.rebuild_list(tracks, active_index=0)
        self.set_theme(DAYLIGHT_THEME)

    def set_custom_playlists(self, playlists: Dict[str, List[str]]) -> None:
        self.custom_playlists = {str(k): list(v) for k, v in (playlists or {}).items() if str(k).strip()}
        if self.active_playlist_name not in self.custom_playlists:
            self.active_playlist_name = next(iter(self.custom_playlists.keys()), "")
        self._update_sub_bar_ui()
        if self.view_mode == 2:
            self.rebuild_list(self.local_tracks, active_index=self.active_index)

    def prompt_create_playlist(self, initial_track_id: Optional[str] = None) -> None:
        dlg = CreatePlaylistDialog("新建歌单", "", theme=self.theme, parent=self.window())

        def _on_created(name: str) -> None:
            if name not in self.custom_playlists:
                self.custom_playlists[name] = []
            if initial_track_id and initial_track_id not in self.custom_playlists[name]:
                self.custom_playlists[name].append(initial_track_id)
            self.active_playlist_name = name
            self._update_sub_bar_ui()
            if self.view_mode == 2:
                self.rebuild_list(self.local_tracks, active_index=self.active_index)
            self.playlistsChanged.emit()

        dlg.playlistCreated.connect(_on_created)
        dlg.finished.connect(dlg.deleteLater)
        dlg.exec()

    def add_track_to_playlist(self, playlist_name: str, track_id: str) -> bool:
        if not playlist_name:
            return False
        if playlist_name not in self.custom_playlists:
            self.custom_playlists[playlist_name] = []
        if track_id not in self.custom_playlists[playlist_name]:
            self.custom_playlists[playlist_name].append(track_id)
            if self.view_mode == 2 and self.active_playlist_name == playlist_name:
                self.rebuild_list(self.local_tracks, active_index=self.active_index)
            self.playlistsChanged.emit()
            return True
        return False

    def remove_track_from_active_playlist(self, track_id: str) -> bool:
        if self.active_playlist_name and self.active_playlist_name in self.custom_playlists:
            plist = self.custom_playlists[self.active_playlist_name]
            if track_id in plist:
                plist.remove(track_id)
                self.rebuild_list(self.local_tracks, active_index=self.active_index)
                self.playlistsChanged.emit()
                return True
        return False

    def _on_delete_current_playlist(self) -> None:
        if self.active_playlist_name and self.active_playlist_name in self.custom_playlists:
            del self.custom_playlists[self.active_playlist_name]
            self.active_playlist_name = next(iter(self.custom_playlists.keys()), "")
            self._update_sub_bar_ui()
            self.rebuild_list(self.local_tracks, active_index=self.active_index)
            self.playlistsChanged.emit()

    def _on_sub_selector_clicked(self) -> None:
        pos = self.btn_sub_selector.mapToGlobal(QPoint(0, self.btn_sub_selector.height() + 4))
        if self.view_mode == 2:
            if not self.custom_playlists:
                self.prompt_create_playlist()
                return
            items = [(f"pl:{name}", "music_file", f"{name} ({len(ids)})") for name, ids in self.custom_playlists.items()]
            menu = WinUIFlyoutMenu(
                items,
                theme=self.theme,
                backdrop_material=getattr(self, "backdrop_material", "mica"),
                searchable=(len(items) > 10),
                search_placeholder="搜索本地歌单...",
                max_height=420,
                parent=self.window(),
            )
            menu.actionTriggered.connect(self._on_playlist_menu_selected)
            menu.popup_animated(pos, anchor_widget=self.btn_sub_selector)
        elif self.view_mode == 3:
            items = [
                ("grp:artist", "sparkle", "按歌手分组"),
                ("grp:album", "music_file", "按专辑分组"),
                ("grp:folder", "folder", "按文件夹分组"),
            ]
            menu = WinUIFlyoutMenu(items, theme=self.theme, backdrop_material=getattr(self, "backdrop_material", "mica"), searchable=False, parent=self.window())
            menu.actionTriggered.connect(self._on_group_menu_selected)
            menu.popup_animated(pos, anchor_widget=self.btn_sub_selector)
        elif self.view_mode == 4:
            items = [
                ("ne:daily", "sparkle", "每日推荐"),
            ]
            if netease_manager.user_playlists:
                liked_pls = [pl for pl in netease_manager.user_playlists if pl.get("is_liked")]
                created_pls = [pl for pl in netease_manager.user_playlists if not pl.get("is_liked") and not pl.get("subscribed")]
                sub_pls = [pl for pl in netease_manager.user_playlists if pl.get("subscribed")]

                for pl in liked_pls:
                    cnt = pl.get("track_count", 0)
                    items.append((f"ne_pl:{pl['id']}", "heart", f"{pl.get('name', '我喜欢的音乐')} ({cnt})"))

                if created_pls:
                    items.append(("header:created", "", f"创建的歌单 ({len(created_pls)})"))
                    for pl in created_pls:
                        cnt = pl.get("track_count", 0)
                        items.append((f"ne_pl:{pl['id']}", "music_file", f"{pl.get('name', '歌单')} ({cnt})"))

                if sub_pls:
                    items.append(("header:sub", "", f"收藏的歌单 ({len(sub_pls)})"))
                    for pl in sub_pls:
                        cnt = pl.get("track_count", 0)
                        items.append((f"ne_pl:{pl['id']}", "folder", f"{pl.get('name', '歌单')} ({cnt})"))

            items.append(("separator", "", ""))
            cur_q_lbl = netease_manager.QUALITY_LABELS.get(netease_manager.preferred_quality, "无损 FLAC")
            items.append(("ne:quality", "equalizer", f"音质选择 (当前: {cur_q_lbl}) ▾"))
            items.append(("ne:refresh", "sparkle", "刷新歌单列表"))
            nickname = netease_manager.user_profile.get("nickname", "已登录账号")
            items.append(("ne:logout", "cross", f"退出登录 ({nickname})"))

            menu = WinUIFlyoutMenu(
                items,
                theme=self.theme,
                backdrop_material=getattr(self, "backdrop_material", "mica"),
                searchable=(len(items) > 10),
                search_placeholder="搜索歌单名称...",
                max_height=420,
                parent=self.window(),
            )
            menu.actionTriggered.connect(self._on_netease_menu_selected)
            menu.popup_animated(pos, anchor_widget=self.btn_sub_selector)

    def _open_netease_quality_menu(self) -> None:
        pos = self.btn_sub_selector.mapToGlobal(QPoint(0, self.btn_sub_selector.height() + 4))
        cur_q = netease_manager.preferred_quality
        items = []
        for q_k, q_lbl in netease_manager.QUALITY_LABELS.items():
            icon = "sparkle" if q_k in ("lossless", "hires", "jymaster") else "music_file"
            prefix = "✓ " if q_k == cur_q else ""
            items.append((f"set_q:{q_k}", icon, f"{prefix}{q_lbl}"))
        menu = WinUIFlyoutMenu(items, theme=self.theme, backdrop_material=getattr(self, "backdrop_material", "mica"), parent=self.window())

        def _on_q(act: str):
            if act.startswith("set_q:"):
                nk = act[6:]
                netease_manager.set_preferred_quality(nk)
                self._update_sub_bar_ui()
                win = self.window()
                if hasattr(win, "lyrics_stage"):
                    win.lyrics_stage._show_toast(f"网易云播放音质已设为：{netease_manager.QUALITY_LABELS.get(nk)}")

        menu.actionTriggered.connect(_on_q)
        menu.popup_animated(pos, anchor_widget=self.btn_sub_selector)

    def _on_netease_menu_selected(self, act: str) -> None:
        if self.is_netease_searching:
            self.is_netease_searching = False
            self._netease_search_keyword = ""
            if hasattr(self, "search_box"):
                self.search_box.clear()
        if act == "ne:daily":
            self.netease_sub_mode = "daily"
            self.netease_sub_name = "每日推荐"
            self._update_sub_bar_ui()
            self._load_current_netease_playlist()
        elif act.startswith("ne_pl:"):
            pl_id = int(act[6:])
            self.netease_sub_mode = f"pl:{pl_id}"
            pl_name = "云歌单"
            for pl in netease_manager.user_playlists:
                if str(pl.get("id")) == str(pl_id):
                    pl_name = pl.get("name", "云歌单")
                    break
            self.netease_sub_name = pl_name
            self._update_sub_bar_ui()
            self._load_current_netease_playlist()
        elif act == "ne:quality":
            self._open_netease_quality_menu()
        elif act == "ne:refresh":
            netease_manager.fetch_user_playlists()
            self._load_current_netease_playlist()
        elif act == "ne:logout":
            netease_manager.logout()

    def _on_playlist_menu_selected(self, act: str) -> None:
        if act.startswith("pl:"):
            self.active_playlist_name = act[3:]
            self._update_sub_bar_ui()
            self.rebuild_list(self.local_tracks, active_index=self.active_index)

    def _on_group_menu_selected(self, act: str) -> None:
        if act.startswith("grp:"):
            self.group_mode = act[4:]
            self.collapsed_groups.clear()
            self._update_sub_bar_ui()
            self.rebuild_list(self.local_tracks, active_index=self.active_index)

    def _open_sort_menu(self) -> None:
        pos = self.btn_sort.mapToGlobal(QPoint(0, self.btn_sort.height() + 4))
        items = [
            ("default", "music_file", "默认顺序"),
            ("title", "edit", "按歌名 (A-Z)"),
            ("artist", "sparkle", "按歌手 (A-Z)"),
            ("album", "music_file", "按专辑 (A-Z)"),
            ("duration", "clock", "按时长"),
            ("recent", "plus", "按最近添加"),
        ]
        menu = WinUIFlyoutMenu(items, theme=self.theme, backdrop_material=getattr(self, "backdrop_material", "mica"), parent=self.window())
        menu.actionTriggered.connect(self._on_sort_selected)
        menu.popup_animated(pos, anchor_widget=self.btn_sort)

    def _on_sort_selected(self, mode: str) -> None:
        if mode in self._SORT_LABELS:
            self.sort_mode = mode
            self._update_sub_bar_ui()
            self.rebuild_list(active_index=self.active_index)
            self.set_active_index(self.active_index, track_id=self._active_track_id)
            self._trigger_list_transition()

    def _update_sub_bar_ui(self) -> None:
        t = self.theme
        getattr(self, "is_translucent_backdrop", True)
        is_dusk = t.name == "dusk"
        if is_dusk:
            bg_val = "rgba(255, 255, 255, 0.08)"
            bd_val = "rgba(255, 255, 255, 0.14)"
            hov_bg = "rgba(255, 255, 255, 0.15)"
            pill_style = (
                f"QPushButton {{ background: {bg_val}; color: {t.charcoal.name()}; "
                f"border: 1px solid {bd_val}; border-radius: 5px; padding: 0 8px; }}"
                f"QPushButton:hover {{ border-color: {t.sage_border.name()}; background: {hov_bg}; }}"
            )
            btn_sort_color = t.slate_muted.name()
            count_color = t.slate_muted.name()
        else:
            pill_style = (
                "QPushButton { background: rgba(255, 255, 255, 0.88); color: #22262B; "
                "border: 1px solid rgba(0, 0, 0, 0.14); border-radius: 5px; padding: 0 8px; font-weight: 500; }"
                f"QPushButton:hover {{ border-color: {t.sage_border.name()}; background: #FFFFFF; }}"
            )
            btn_sort_color = "#3A4048"
            count_color = "#424850"

        muted_btn_style = (
            f"QPushButton {{ background: transparent; color: {btn_sort_color}; "
            f"border: none; padding: 0 4px; font-weight: 500; }}"
            f"QPushButton:hover {{ color: {t.charcoal.name()}; }}"
        )
        self.lbl_count.setStyleSheet(f"color: {count_color}; background: transparent; font-weight: 500;")
        self.btn_sub_selector.setStyleSheet(pill_style)
        self.btn_new_playlist.setStyleSheet(pill_style)
        self.btn_del_playlist.setStyleSheet(muted_btn_style)
        self.btn_sort.setStyleSheet(muted_btn_style)
        self.btn_sort.setText(f"{self._SORT_LABELS.get(self.sort_mode, '默认排序')} ▾")
        if hasattr(self, "btn_netease_logout"):
            self.btn_netease_logout.setStyleSheet(muted_btn_style)
        if hasattr(self, "btn_netease_login"):
            self.btn_netease_login.setStyleSheet(
                f"QPushButton {{ background: {self.theme.sage_accent.name()}; color: #FFFFFF; "
                f"border: none; border-radius: 4px; padding: 0 10px; font-weight: 600; font-size: 11px; }} "
                f"QPushButton:hover {{ background: {self.theme.sage_border.name()}; }}"
            )

        if self.view_mode in (0, 1):
            self.lbl_count.show()
            self.btn_sub_selector.hide()
            self.btn_new_playlist.hide()
            self.btn_del_playlist.hide()
            self.btn_sort.show()
            if hasattr(self, "btn_netease_logout"):
                self.btn_netease_logout.hide()
            if hasattr(self, "btn_netease_login"):
                self.btn_netease_login.hide()
        elif self.view_mode == 2:
            self.lbl_count.hide()
            self.btn_sub_selector.show()
            pl_label = self.active_playlist_name if self.active_playlist_name else "未建歌单"
            metrics = QFontMetrics(self.btn_sub_selector.font())
            self.btn_sub_selector.setText(metrics.elidedText(f"{pl_label} ▾", Qt.TextElideMode.ElideRight, 136))
            self.btn_sub_selector.setToolTip(f"当前本地歌单：{pl_label}")
            self.btn_new_playlist.show()
            self.btn_del_playlist.setVisible(bool(self.active_playlist_name))
            self.btn_sort.hide()
            if hasattr(self, "btn_netease_logout"):
                self.btn_netease_logout.hide()
            if hasattr(self, "btn_netease_login"):
                self.btn_netease_login.hide()
        elif self.view_mode == 3:
            self.lbl_count.hide()
            self.btn_sub_selector.show()
            grp_name = self._GROUP_LABELS.get(self.group_mode, "按歌手分组")
            metrics = QFontMetrics(self.btn_sub_selector.font())
            self.btn_sub_selector.setText(metrics.elidedText(f"{grp_name} ▾", Qt.TextElideMode.ElideRight, 136))
            self.btn_sub_selector.setToolTip(f"当前分组：{grp_name}")
            self.btn_new_playlist.hide()
            self.btn_del_playlist.hide()
            self.btn_sort.show()
            if hasattr(self, "btn_netease_logout"):
                self.btn_netease_logout.hide()
            if hasattr(self, "btn_netease_login"):
                self.btn_netease_login.hide()
        elif self.view_mode == 4:
            if not netease_manager.is_logged_in:
                self.lbl_count.setText("未登录网易云")
                self.lbl_count.show()
                self.btn_sub_selector.hide()
                self.btn_new_playlist.hide()
                self.btn_del_playlist.hide()
                self.btn_sort.hide()
                if hasattr(self, "btn_netease_logout"):
                    self.btn_netease_logout.hide()
                if hasattr(self, "btn_netease_login"):
                    self.btn_netease_login.show()
            else:
                self.lbl_count.hide()
                self.btn_sub_selector.show()
                metrics = QFontMetrics(self.btn_sub_selector.font())
                self.btn_sub_selector.setText(metrics.elidedText(f"{self.netease_sub_name} ▾", Qt.TextElideMode.ElideRight, 136))
                self.btn_sub_selector.setToolTip(f"当前云歌单：{self.netease_sub_name}")
                self.btn_new_playlist.hide()
                self.btn_del_playlist.hide()
                self.btn_sort.show()
                if hasattr(self, "btn_netease_login"):
                    self.btn_netease_login.hide()
                if hasattr(self, "btn_netease_logout"):
                    nickname = netease_manager.user_profile.get("nickname") or "账号"
                    self.btn_netease_logout.setToolTip(f"退出网易云账号: {nickname}")
                    self.btn_netease_logout.show()

    def _step_eq_bars(self) -> None:
        if not self.isVisible():
            return
        self._eq_phase = (self._eq_phase + 0.38) % (math.pi * 2.0)
        for btn in self.item_buttons:
            is_match = (btn.track.track_id == self._active_track_id) if self._active_track_id else (btn.index == self.active_index)
            if is_match:
                btn.eq_phase = self._eq_phase
                if btn.isVisible():
                    btn.update(11, int(btn.height() / 2 - 18), 36, 36)

    def set_playing_state(self, is_playing: bool) -> None:
        self.is_playing = bool(is_playing)
        for btn in self.item_buttons:
            is_match = (btn.track.track_id == self._active_track_id) if self._active_track_id else (btn.index == self.active_index)
            new_playing = self.is_playing and is_match
            if btn.is_playing != new_playing:
                btn.is_playing = new_playing
                btn.update()
        if self.is_playing:
            if not self._eq_timer.isActive():
                self._eq_timer.start()
        else:
            self._eq_timer.stop()

    def _open_import_flyout(self, global_pos: QPoint) -> None:
        items = [
            ("add_files", "music_file", "添加音频或 CUE 分轨 (.flac/.mp3/.cue)"),
            ("add_folder", "folder", "扫描并导入音乐文件夹"),
            ("watch_folders", "folder", "自动监控音乐文件夹…"),
            ("add_lrc", "lrc", "为当前歌曲导入 LRC 歌词"),
        ]
        self._import_flyout = WinUIFlyoutMenu(items, theme=self.theme, backdrop_material=getattr(self, "backdrop_material", "mica"), parent=self.window())
        self._import_flyout.actionTriggered.connect(self._on_import_flyout_action)
        self._import_flyout.popup_animated(global_pos)

    def _on_import_flyout_action(self, act: str) -> None:
        if act == "add_files":
            self.importFilesRequested.emit()
        elif act == "add_folder":
            self.importFolderRequested.emit()
        elif act == "watch_folders":
            self.watchFoldersRequested.emit()
        elif act == "add_lrc":
            self.importLrcRequested.emit()

    def _on_list_reveal_val(self, val: float) -> None:
        p = float(val)
        visible_btns = [b for b in self.item_buttons if b.isVisible()]
        total = len(visible_btns)
        if total == 0:
            return
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
        self.view_mode = idx
        self.filter_favorites_only = (idx == 1)
        self._update_sub_bar_ui()
        if hasattr(self, "search_box"):
            if idx == 4:
                self.search_box.set_placeholder("搜索网易云在线歌曲 / 歌手…")
            else:
                self.search_box.set_placeholder("搜索歌名 / 歌手 / 专辑…")
        if idx == 4:
            if not netease_manager.is_logged_in:
                self.rebuild_list([], active_index=-1)
            else:
                cur_kw = self.search_box.text().strip() if hasattr(self, "search_box") else ""
                if cur_kw:
                    self._netease_search_timer.start(400)
                elif not self.netease_tracks and not self.is_netease_loading:
                    self._load_current_netease_playlist()
                else:
                    self.rebuild_list(self.netease_tracks, active_index=self.active_index)
        else:
            self.rebuild_list(self.local_tracks, active_index=self.active_index)
        self._trigger_list_transition()

    def _load_current_netease_playlist(self) -> None:
        if not netease_manager.is_logged_in:
            return
        self.is_netease_loading = True
        self.rebuild_list([], active_index=-1)
        if hasattr(self, "_netease_load_timeout_timer"):
            self._netease_load_timeout_timer.start(12000)
        if self.netease_sub_mode == "daily":
            netease_manager.fetch_daily_recommend_async()
        elif self.netease_sub_mode.startswith("pl:"):
            pl_id = int(self.netease_sub_mode[3:])
            netease_manager.fetch_playlist_tracks_async(pl_id)

    def _on_netease_load_timeout(self) -> None:
        if self.is_netease_loading:
            self.is_netease_loading = False
            if self.view_mode == 4 and not self.is_netease_searching:
                self.rebuild_list(self.netease_tracks, active_index=self.active_index)
            win = self.window()
            if hasattr(win, "lyrics_stage") and hasattr(win.lyrics_stage, "_show_toast"):
                win.lyrics_stage._show_toast(f"同步「{self.netease_sub_name}」超时，请点击刷新重试")

    def _on_daily_recommend_loaded(self, tracks: List[NordicTrack]) -> None:
        if hasattr(self, "_netease_load_timeout_timer"):
            self._netease_load_timeout_timer.stop()
        if self.netease_sub_mode == "daily":
            self.is_netease_loading = False
            self.netease_tracks = list(tracks)
            self._netease_playlist_tracks = list(tracks)
            if self.view_mode == 4 and not self.is_netease_searching:
                self.rebuild_list(self.netease_tracks, active_index=self.active_index)
            self._prefetch_thumbnails_for_tracks(tracks)

    def _on_playlist_tracks_loaded(self, pl_id: Any, tracks: List[NordicTrack]) -> None:
        if hasattr(self, "_netease_load_timeout_timer"):
            self._netease_load_timeout_timer.stop()
        # 兼容 64 位大整数与字符串格式歌单 ID
        if str(self.netease_sub_mode) == f"pl:{pl_id}":
            self.is_netease_loading = False
            self.netease_tracks = list(tracks)
            self._netease_playlist_tracks = list(tracks)
            if self.view_mode == 4 and not self.is_netease_searching:
                self.rebuild_list(self.netease_tracks, active_index=self.active_index)
            self._prefetch_thumbnails_for_tracks(tracks)

    def _on_netease_login_status_changed(self, is_logged_in: bool, profile: dict) -> None:
        if self.view_mode == 4:
            self._update_sub_bar_ui()
            if is_logged_in:
                self._load_current_netease_playlist()
            else:
                self.netease_tracks.clear()
                self.rebuild_list([], active_index=-1)

    def _on_netease_playlists_loaded(self, playlists: list) -> None:
        if self.view_mode == 4:
            self._update_sub_bar_ui()

    def _on_netease_logout_clicked(self) -> None:
        netease_manager.logout()
        win = self.window()
        if hasattr(win, "lyrics_stage") and hasattr(win.lyrics_stage, "_show_toast"):
            win.lyrics_stage._show_toast("已退出网易云账号")

    def _open_netease_login_dialog(self) -> None:
        dlg = NeteaseLoginDialog(theme=self.theme, parent=self.window())
        dlg.finished.connect(dlg.deleteLater)
        dlg.exec()

    def _prefetch_thumbnails_for_tracks(self, tracks: List[NordicTrack]) -> None:
        def _worker():
            target_tracks = [
                t for t in tracks[:100]
                if not (t.cover_path and not t.cover_path.startswith("http") and Path(t.cover_path).exists())
            ]
            if not target_tracks:
                return

            def _fetch_one(t: NordicTrack) -> bool:
                try:
                    url = getattr(t, "_cover_url", None) or (t.cover_path if t.cover_path and t.cover_path.startswith("http") else None)
                    if url:
                        song_id = getattr(t, "_netease_song_id", None) or t.track_id.replace("netease_", "")
                        p = netease_manager.cache_cover_image(song_id, url)
                        if p:
                            t.cover_path = p
                            t._cached_cover_pix = None
                            t._cover_cache_loaded = False
                            return True
                except Exception:
                    _logging.getLogger(__name__).debug("ignored exception", exc_info=True)
                return False

            with concurrent.futures.ThreadPoolExecutor(max_workers=6) as executor:
                futures = [executor.submit(_fetch_one, t) for t in target_tracks]
                done_count = 0
                for fut in concurrent.futures.as_completed(futures):
                    try:
                        if fut.result():
                            done_count += 1
                            if done_count % 4 == 0 or done_count == len(target_tracks):
                                self._thumbnailsRefreshed.emit()
                    except Exception:
                        _logging.getLogger(__name__).debug("ignored exception", exc_info=True)
        threading.Thread(target=_worker, daemon=True).start()

    def _refresh_item_thumbnails(self) -> None:
        for btn in self.item_buttons:
            btn.refresh_thumbnail()

    def _on_netease_item_clicked(self, index: int, track: NordicTrack) -> None:
        self.set_active_index(index)
        self.neteaseTrackSelected.emit(track, list(self.netease_tracks), index)

    def _on_search_changed(self, text: str) -> None:
        kw = text.strip()
        if self.view_mode == 4:
            if not kw:
                if self._netease_search_timer.isActive():
                    self._netease_search_timer.stop()
                if self.is_netease_searching or self._netease_search_keyword:
                    self.is_netease_searching = False
                    self._netease_search_keyword = ""
                    self.netease_tracks = list(self._netease_playlist_tracks)
                    self.rebuild_list(self.netease_tracks, active_index=self.active_index)
                    self._apply_visibility_filter()
                    self._trigger_list_transition()
            else:
                self._netease_search_timer.start(400)
        else:
            self.search_keyword = kw.lower()
            self._apply_visibility_filter()
            self._trigger_list_transition()

    def _on_search_return_pressed(self) -> None:
        if self.view_mode == 4:
            if self._netease_search_timer.isActive():
                self._netease_search_timer.stop()
            self._do_netease_cloud_search()

    def _do_netease_cloud_search(self) -> None:
        kw = self.search_box.text().strip()
        if not kw or self.view_mode != 4:
            return
        self.is_netease_searching = True
        self._netease_search_keyword = kw
        self.is_netease_loading = True
        self.lbl_count.setText(f"正在搜索「{kw}」…")
        self.rebuild_list([], active_index=-1)

        def _worker():
            try:
                results = netease_manager.search_tracks(kw, limit=50)
            except Exception:
                results = []
            self._cloudSearchDone.emit(kw, results)

        threading.Thread(target=_worker, daemon=True).start()

    def _on_cloud_search_done(self, kw: str, results: list) -> None:
        cur_kw = self.search_box.text().strip()
        if cur_kw != kw or self.view_mode != 4:
            return
        self.is_netease_loading = False
        self.netease_tracks = list(results)
        self.rebuild_list(self.netease_tracks, active_index=-1)
        self.lbl_count.setText(f"共找到 {len(results)} 首在线歌曲")
        self._trigger_list_transition()
        self._prefetch_thumbnails_for_tracks(results)

    def _matches_search(self, t: NordicTrack) -> bool:
        if not self.search_keyword:
            return True
        kw = self.search_keyword
        return (
            kw in t.title.lower()
            or kw in t.artist.lower()
            or kw in (t.album or "").lower()
        )

    def _apply_visibility_filter(self) -> None:
        visible_count = 0
        for btn in self.item_buttons:
            t = btn.track
            match_fav = (not self.filter_favorites_only) or t.is_favorite
            match_kw = True if self.view_mode == 4 else self._matches_search(t)
            group_key = getattr(btn, "_group_key", None)
            match_grp = (group_key is None) or (group_key not in self.collapsed_groups)
            vis = match_fav and match_kw and match_grp
            btn.setVisible(vis)
            if vis:
                visible_count += 1
        if self.view_mode in (0, 1):
            self.lbl_count.setText(f"共 {visible_count} 首")
        elif self.view_mode == 4:
            if not netease_manager.is_logged_in:
                self.lbl_count.setText("未登录网易云")
            elif self.is_netease_searching:
                self.lbl_count.setText(f"共找到 {len(self.netease_tracks)} 首在线歌曲")
            else:
                self.lbl_count.setText(f"共 {len(self.netease_tracks)} 首")

    def _sorted_indexed_tracks(self, pairs: List[tuple]) -> List[tuple]:
        if self.sort_mode == "title":
            return sorted(pairs, key=lambda x: (x[1].title or "").lower())
        if self.sort_mode == "artist":
            return sorted(pairs, key=lambda x: ((x[1].artist or "").lower(), (x[1].title or "").lower()))
        if self.sort_mode == "album":
            return sorted(pairs, key=lambda x: ((x[1].album or "").lower(), x[1].cue_track_num, (x[1].title or "").lower()))
        if self.sort_mode == "duration":
            return sorted(pairs, key=lambda x: float(x[1].duration or 0.0), reverse=True)
        if self.sort_mode == "recent":
            return sorted(pairs, key=lambda x: float(getattr(x[1], "added_timestamp", 0.0) or 0.0), reverse=True)
        return pairs

    def _group_key_for_track(self, t: NordicTrack) -> str:
        if self.group_mode == "album":
            return (t.album or "").strip() or "未知专辑"
        if self.group_mode == "folder":
            if t.file_path:
                return Path(t.file_path).parent.name or "根目录"
            return "内置曲目"
        return (t.artist or "").strip() or "未知歌手"

    def _toggle_group_collapsed(self, group_name: str) -> None:
        if group_name in self.collapsed_groups:
            self.collapsed_groups.remove(group_name)
        else:
            self.collapsed_groups.add(group_name)
        for gbtn in self.group_buttons:
            if gbtn.group_name == group_name:
                gbtn.expanded = (group_name not in self.collapsed_groups)
                gbtn.update()
        self._apply_visibility_filter()

    def rebuild_list(self, tracks: Optional[List[NordicTrack]] = None, active_index: Optional[int] = None) -> None:
        if tracks is not None:
            if self.view_mode != 4:
                self.local_tracks = list(tracks)
            else:
                self.netease_tracks = list(tracks)

        current_tracks = self.netease_tracks if self.view_mode == 4 else self.local_tracks
        self.tracks = current_tracks
        if active_index is not None:
            self.active_index = active_index

        while self.list_vbox.count():
            item = self.list_vbox.takeAt(0)
            w = item.widget()
            if w is not None:
                w.hide()
                w.setParent(None)
                w.deleteLater()

        self.item_buttons.clear()
        self.group_buttons.clear()

        if self.view_mode == 2:
            # 自定义歌单视图 (严格仅使用本地曲库 self.local_tracks)
            if not self.custom_playlists:
                empty_lbl = QLabel("暂无自定义歌单\n点击右上角「＋新建」创建歌单", self.list_container)
                empty_lbl.setAlignment(Qt.AlignmentFlag.AlignCenter)
                empty_lbl.setFont(get_nordic_font(11, QFont.Weight.Normal))
                empty_lbl.setStyleSheet(f"color: {self.theme.slate_muted.name()}; padding: 36px 12px;")
                self.list_vbox.addWidget(empty_lbl)
            else:
                if self.active_playlist_name not in self.custom_playlists:
                    self.active_playlist_name = next(iter(self.custom_playlists.keys()), "")
                local_ids = {x.track_id for x in self.local_tracks}
                all_candidates = list(self.local_tracks) + [t for t in self.netease_tracks if t.track_id not in local_ids]
                id_map = {t.track_id: (i, t) for i, t in enumerate(all_candidates)}
                pl_ids = self.custom_playlists.get(self.active_playlist_name, [])
                pairs = [id_map[tid] for tid in pl_ids if tid in id_map]
                if not pairs:
                    empty_lbl = QLabel("歌单内暂无曲目\n可在曲库右键「添加到歌单」，或直接点击左下角「+ 导入音乐」/ 拖拽歌曲至此", self.list_container)
                    empty_lbl.setAlignment(Qt.AlignmentFlag.AlignCenter)
                    empty_lbl.setFont(get_nordic_font(11, QFont.Weight.Normal))
                    empty_lbl.setStyleSheet(f"color: {self.theme.slate_muted.name()}; padding: 36px 12px;")
                    self.list_vbox.addWidget(empty_lbl)
                else:
                    for idx, track in pairs:
                        self._add_track_button(idx, track, in_custom_playlist=True)

        elif self.view_mode == 3:
            # 分类折叠视图 (按歌手 / 专辑 / 文件夹，严格仅使用本地曲库 self.local_tracks)
            groups: Dict[str, List[tuple]] = {}
            for idx, track in enumerate(self.local_tracks):
                g_key = self._group_key_for_track(track)
                groups.setdefault(g_key, []).append((idx, track))

            for g_name in sorted(groups.keys(), key=lambda s: s.lower()):
                g_pairs = self._sorted_indexed_tracks(groups[g_name])
                is_exp = (g_name not in self.collapsed_groups)
                g_btn = NavGroupHeaderButton(
                    g_name,
                    len(g_pairs),
                    expanded=is_exp,
                    theme=self.theme,
                    backdrop_material=getattr(self, "backdrop_material", "mica"),
                    parent=self.list_container,
                )
                g_btn.clicked.connect(lambda _=False, gn=g_name: self._toggle_group_collapsed(gn))
                self.list_vbox.addWidget(g_btn)
                self.group_buttons.append(g_btn)

                for idx, track in g_pairs:
                    btn = self._add_track_button(idx, track, in_custom_playlist=False)
                    btn._group_key = g_name

        elif self.view_mode == 4:
            # 网易云音乐视图
            if not self.is_netease_searching and not netease_manager.is_logged_in:
                login_widget = QWidget(self.list_container)
                login_widget.setStyleSheet("background: transparent;")
                lw_layout = QVBoxLayout(login_widget)
                lw_layout.setContentsMargins(16, 48, 16, 24)
                lw_layout.setSpacing(12)
                lw_layout.setAlignment(Qt.AlignmentFlag.AlignCenter)

                title_lbl = QLabel("网易云音乐", login_widget)
                title_lbl.setAlignment(Qt.AlignmentFlag.AlignCenter)
                title_lbl.setFont(get_nordic_font(15, QFont.Weight.DemiBold))
                title_lbl.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
                lw_layout.addWidget(title_lbl)

                desc_lbl = QLabel("登录网易云音乐账号\n同步收藏歌单与每日推荐", login_widget)
                desc_lbl.setAlignment(Qt.AlignmentFlag.AlignCenter)
                desc_lbl.setFont(get_nordic_font(11, QFont.Weight.Normal))
                desc_lbl.setStyleSheet(f"color: {self.theme.slate_muted.name()}; line-height: 1.4; background: transparent;")
                lw_layout.addWidget(desc_lbl)

                lw_layout.addSpacing(6)

                btn_login = QPushButton("扫码登录", login_widget)
                btn_login.setFixedSize(110, 32)
                btn_login.setCursor(Qt.CursorShape.PointingHandCursor)
                btn_login.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
                btn_login.setStyleSheet(
                    f"QPushButton {{ background: {self.theme.sage_accent.name()}; color: #FAF9F6; "
                    f"border-radius: 6px; border: none; }} "
                    f"QPushButton:hover {{ background: {self.theme.sage_border.name()}; }}"
                )
                btn_login.clicked.connect(self._open_netease_login_dialog)
                lw_layout.addWidget(btn_login, 0, Qt.AlignmentFlag.AlignCenter)

                self.list_vbox.addWidget(login_widget)
            elif self.is_netease_loading:
                msg = f"正在网易云搜索「{self._netease_search_keyword}」…" if self.is_netease_searching else f"正在同步「{self.netease_sub_name}」…"
                loading_lbl = QLabel(msg, self.list_container)
                loading_lbl.setAlignment(Qt.AlignmentFlag.AlignCenter)
                loading_lbl.setFont(get_nordic_font(11, QFont.Weight.Normal))
                loading_lbl.setStyleSheet(f"color: {self.theme.slate_muted.name()}; padding: 48px 12px;")
                self.list_vbox.addWidget(loading_lbl)
            else:
                if not self.netease_tracks:
                    msg = f"未找到与「{self._netease_search_keyword}」相关的歌曲" if self.is_netease_searching else f"「{self.netease_sub_name}」暂无歌曲"
                    empty_lbl = QLabel(msg, self.list_container)
                    empty_lbl.setAlignment(Qt.AlignmentFlag.AlignCenter)
                    empty_lbl.setFont(get_nordic_font(11, QFont.Weight.Normal))
                    empty_lbl.setStyleSheet(f"color: {self.theme.slate_muted.name()}; padding: 48px 12px;")
                    self.list_vbox.addWidget(empty_lbl)
                else:
                    pairs = list(enumerate(self.netease_tracks))
                    sorted_pairs = self._sorted_indexed_tracks(pairs)
                    for idx, track in sorted_pairs:
                        self._add_track_button(idx, track, in_custom_playlist=False, is_netease=True)

        else:
            # 全部 (0) 或 喜欢 (1) 视图 (严格仅使用本地曲库 self.local_tracks)
            all_pairs = list(enumerate(self.local_tracks))
            sorted_pairs = self._sorted_indexed_tracks(all_pairs)
            for idx, track in sorted_pairs:
                self._add_track_button(idx, track, in_custom_playlist=False)

        self.list_vbox.addStretch(1)
        self._update_sub_bar_ui()
        self._apply_visibility_filter()

    def _add_track_button(
        self,
        idx: int,
        track: NordicTrack,
        in_custom_playlist: bool = False,
        is_netease: bool = False,
    ) -> NavTrackItemButton:
        is_sel = (track.track_id == self._active_track_id) if self._active_track_id else (idx == self.active_index and ((self.view_mode == 4) == is_netease))
        btn = NavTrackItemButton(
            idx,
            track,
            selected=is_sel,
            in_custom_playlist=in_custom_playlist,
            parent=self.list_container,
        )
        btn.is_playing = self.is_playing and is_sel
        btn.set_theme(self.theme)
        btn.set_backdrop_material(getattr(self, "backdrop_material", "mica"))
        if is_netease:
            btn.clicked.connect(lambda checked=False, i=idx, t=track: self._on_netease_item_clicked(i, t))
        else:
            btn.clicked.connect(lambda checked=False, i=idx: self._on_item_clicked(i))
        btn.favoriteToggled.connect(self._on_item_fav_toggled)
        btn.contextActionRequested.connect(self.trackActionRequested.emit)
        btn.dragReorderRequested.connect(self._on_drag_reorder)
        self.list_vbox.addWidget(btn)
        self.item_buttons.append(btn)
        return btn

    def _on_drag_reorder(self, from_idx: int, to_idx: int) -> None:
        if self.view_mode != 0 or self.sort_mode != "default" or self.search_keyword:
            return
        clamped_to = max(0, min(len(self.tracks) - 1, to_idx))
        if clamped_to != from_idx:
            self.trackReordered.emit(from_idx, clamped_to)

    def _on_item_fav_toggled(self, index: int) -> None:
        self._apply_visibility_filter()
        self.favoriteChanged.emit(index)

    def refresh_item(self, index: int) -> None:
        for btn in self.item_buttons:
            if btn.index == index:
                btn.refresh_thumbnail()
                btn.update()
        # 批量刮削时每首歌完成都会调用；合并成一次过滤，避免 O(n²)
        self._schedule_visibility_filter()

    def _schedule_visibility_filter(self) -> None:
        timer = getattr(self, "_vis_filter_timer", None)
        if timer is None:
            timer = QTimer(self)
            timer.setSingleShot(True)
            timer.setInterval(60)
            timer.timeout.connect(self._apply_visibility_filter)
            self._vis_filter_timer = timer
        timer.start()

    def _on_item_clicked(self, index: int) -> None:
        self.set_active_index(index)
        self.trackSelected.emit(index)

    def set_active_index(self, index: int, track_id: Optional[str] = None) -> None:
        self.active_index = index
        if track_id:
            self._active_track_id = str(track_id)
        for btn in self.item_buttons:
            new_sel = (btn.track.track_id == self._active_track_id) if self._active_track_id else (btn.index == index)
            new_play = self.is_playing and new_sel
            if btn.is_selected != new_sel or btn.is_playing != new_play:
                btn.is_selected = new_sel
                btn.is_playing = new_play
                btn.update()

    def set_backdrop_material(self, material: str) -> None:
        self.backdrop_material = str(material)
        self.is_translucent_backdrop = self.backdrop_material in ("mica", "acrylic")
        if hasattr(self, "segmented_tabs"):
            self.segmented_tabs.set_backdrop_material(material)
        if hasattr(self, "search_box"):
            self.search_box.set_backdrop_material(material)
        if hasattr(self, "btn_import_split"):
            self.btn_import_split.set_backdrop_material(material)
        if hasattr(self, "_import_flyout") and self._import_flyout:
            self._import_flyout.set_backdrop_material(material)
        self._update_sub_bar_ui()
        for gbtn in getattr(self, "group_buttons", []):
            if hasattr(gbtn, "set_backdrop_material"):
                gbtn.set_backdrop_material(material)
        for btn in self.item_buttons:
            if hasattr(btn, "set_backdrop_material"):
                btn.set_backdrop_material(material)
        self.update()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.segmented_tabs.set_theme(theme)
        self.search_box.set_theme(theme)
        self.btn_import_split.set_theme(theme)
        if hasattr(self, "_import_flyout") and self._import_flyout:
            self._import_flyout.set_theme(theme)
        self._update_sub_bar_ui()
        for gbtn in self.group_buttons:
            gbtn.set_theme(theme)
        for btn in self.item_buttons:
            btn.set_theme(theme)
        if self.view_mode in (2, 4) and not self.item_buttons:
            self.rebuild_list(self.netease_tracks if self.view_mode == 4 else self.local_tracks, active_index=self.active_index)
        self.update()

    def paintEvent(self, event) -> None:
        if self.width() <= 2:
            return
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setPen(QPen(self.theme.divider, 1.0))
        painter.drawLine(self.width() - 1, 0, self.width() - 1, self.height())
