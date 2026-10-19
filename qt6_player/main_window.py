"""
Klang 音乐播放器主窗口 (Qt6)
完整功能集成：
1. 顶底完全对称 12px 圆角无边框窗口 + 四周 8 方向鼠标拖拽缩放大小 (startSystemResize)
2. 本地音乐文件 / 文件夹 / LRC 歌词导入与拖拽导入
3. 在线歌词与专辑封面自动静默刮削 + 手动搜索匹配弹窗
4. 播放模式切换 (列表循环 / 单曲循环 / 随机播放 / 顺序播放)
5. 倍速播放 (0.75x ~ 2.0x)、音效模式、歌词时间轴微调 (-0.5s / +0.5s)、睡眠定时
6. 桌面悬浮歌词窗 (DesktopLyricOverlay)、双语译文开关、左对齐/居中切换、歌词字号无级调节
7. 播放列表搜索过滤、「我喜欢」红心收藏分组、右键单曲管理与状态持久化保存
"""

import random
from pathlib import Path
from typing import List, Optional
from PyQt6.QtCore import Qt, QRectF, QPointF, QVariantAnimation, QEasingCurve
from PyQt6.QtGui import (
    QPainter,
    QPen,
    QPainterPath,
    QRadialGradient,
    QKeyEvent,
    QDragEnterEvent,
    QDropEvent,
    QMouseEvent,
    QCloseEvent,
    QIcon,
)
from PyQt6.QtWidgets import (
    QMainWindow,
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QFileDialog,
)

from qt6_player.data.tracks import NordicTrack, LyricLine, parse_lrc_text
from qt6_player.theme.nordic_palette import DAYLIGHT_THEME, DUSK_THEME, NordicThemeColors
from qt6_player.services.audio_engine import NordicAudioEngine
from qt6_player.services.metadata_service import (
    SUPPORTED_AUDIO_EXTS,
    create_track_from_file,
    scan_folder_for_audio,
)
from qt6_player.services.scraper_service import AutoScrapeWorker, OnlineScraperDialog
from qt6_player.services.storage_service import (
    load_player_state,
    save_player_state,
    restore_tracks_from_state,
)
from qt6_player.components.title_bar import WinUITitleBar
from qt6_player.components.navigation_pane import NavigationPane
from qt6_player.components.vinyl_cover_card import VinylCoverCard
from qt6_player.components.lyrics_stage import LyricsStage
from qt6_player.components.transport_bar import TransportBar
from qt6_player.components.desktop_lyric_overlay import DesktopLyricOverlay
from qt6_player.components.equalizer_dialog import AudioSettingsDialog


class WindowFrameOverlay(QWidget):
    """顶层透明描边覆盖层：确保窗口四角（含顶部与底部左右圆角）拥有完全对称的 1px 内外高光边框"""

    def __init__(self, parent: QWidget):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents, True)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme
        self.update()

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        is_max = self.window().isMaximized()
        radius = 0.0 if is_max else 12.0

        outer_rect = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        outer_path = QPainterPath()
        outer_path.addRoundedRect(outer_rect, radius, radius)
        painter.setPen(QPen(self.theme.stroke_outer, 1.0))
        painter.setBrush(Qt.BrushStyle.NoBrush)
        painter.drawPath(outer_path)

        if radius > 0:
            inner_rect = QRectF(self.rect()).adjusted(1.5, 1.5, -1.5, -1.5)
            inner_path = QPainterPath()
            inner_path.addRoundedRect(inner_rect, radius - 1.0, radius - 1.0)
            painter.setPen(QPen(self.theme.stroke_inner, 1.0))
            painter.drawPath(inner_path)


class NordicWinUI3Window(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Klang")
        icon_path = Path(__file__).resolve().parent / "assets" / "klang_icon.ico"
        if not icon_path.exists():
            icon_path = Path(__file__).resolve().parent / "assets" / "klang_icon.png"
        if icon_path.exists():
            self.setWindowIcon(QIcon(str(icon_path)))

        self.resize(1280, 820)
        self.setMinimumSize(940, 640)
        self.setAcceptDrops(True)
        self.setMouseTracking(True)

        self.setWindowFlags(Qt.WindowType.FramelessWindowHint | Qt.WindowType.Window)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)

        # 读取持久化状态
        self._saved_state = load_player_state()
        self.theme: NordicThemeColors = (
            DUSK_THEME if self._saved_state.get("theme") == "dusk" else DAYLIGHT_THEME
        )
        self.tracks: List[NordicTrack] = restore_tracks_from_state(self._saved_state)
        self.current_track_index: int = 0
        self.play_mode: str = self._saved_state.get("play_mode", "loop_list")

        self.audio = NordicAudioEngine(self)
        self.desktop_lyric = DesktopLyricOverlay()
        self._active_workers: List[AutoScrapeWorker] = []

        self._setup_ui()
        self._bind_signals()
        self._apply_restored_settings()

        init_idx = int(self._saved_state.get("last_track_index", 0))
        if not (0 <= init_idx < len(self.tracks)):
            init_idx = 0
        self.select_track(init_idx, auto_play=False)

    def _setup_ui(self) -> None:
        central = QWidget(self)
        central.setMouseTracking(True)
        self.setCentralWidget(central)

        root_vbox = QVBoxLayout(central)
        root_vbox.setContentsMargins(1, 1, 1, 1)
        root_vbox.setSpacing(0)

        # 1. 顶部栏
        self.title_bar = WinUITitleBar(central)
        root_vbox.addWidget(self.title_bar)

        # 2. 主工作区
        workspace = QWidget(central)
        ws_hbox = QHBoxLayout(workspace)
        ws_hbox.setContentsMargins(0, 0, 0, 0)
        ws_hbox.setSpacing(0)

        self.nav_pane = NavigationPane(self.tracks, workspace)
        ws_hbox.addWidget(self.nav_pane)

        stage_container = QWidget(workspace)
        stage_hbox = QHBoxLayout(stage_container)
        stage_hbox.setContentsMargins(46, 24, 42, 24)
        stage_hbox.setSpacing(48)

        self.cover_card = VinylCoverCard(stage_container)
        stage_hbox.addWidget(self.cover_card, 0, Qt.AlignmentFlag.AlignVCenter)

        self.lyrics_stage = LyricsStage(stage_container)
        stage_hbox.addWidget(self.lyrics_stage, 1)

        ws_hbox.addWidget(stage_container, 1)
        root_vbox.addWidget(workspace, 1)

        # 3. 底部控制栏
        self.transport_bar = TransportBar(central)
        root_vbox.addWidget(self.transport_bar)

        # 4. 顶层对称圆角边框覆盖层
        self.frame_overlay = WindowFrameOverlay(central)
        self.frame_overlay.raise_()

    def resizeEvent(self, event) -> None:
        super().resizeEvent(event)
        if hasattr(self, "frame_overlay") and self.centralWidget():
            self.frame_overlay.setGeometry(self.centralWidget().rect())
            self.frame_overlay.raise_()

    def _apply_restored_settings(self) -> None:
        st = self._saved_state
        vol = float(st.get("volume", 0.72))
        muted = bool(st.get("is_muted", False))
        rate = float(st.get("playback_rate", 1.0))
        font_sz = int(st.get("lyric_font_size", 20))
        show_trans = bool(st.get("show_translation", True))
        syl_mode = bool(st.get("syllable_mode", True))
        align_center = bool(st.get("lyric_align_center", False))
        sidebar_collapsed = bool(st.get("sidebar_collapsed", False))
        sound_profile = str(st.get("sound_profile", "原声自然"))

        self.audio.set_volume(vol)
        if muted:
            self.audio.toggle_mute()
        self.audio.set_playback_rate(rate)
        self.audio.set_sound_profile(sound_profile, trigger_preview=False)

        self.transport_bar.set_play_mode(self.play_mode)
        self.transport_bar.set_playback_rate(rate)
        self.transport_bar.set_sound_profile_badge(sound_profile)
        self.transport_bar.update_volume(self.audio.volume, self.audio.is_muted)

        self.lyrics_stage.set_lyric_font_size(font_sz)
        self.lyrics_stage.set_show_translation(show_trans)
        self.lyrics_stage.set_syllable_mode(syl_mode)
        self.lyrics_stage.set_align_center(align_center)

        self.title_bar.update_font_size_display(font_sz)
        self.title_bar.show_translation = show_trans
        self.title_bar.btn_trans.set_chip_active(show_trans)
        self.title_bar.syllable_mode = syl_mode
        self.title_bar.btn_syllable.set_chip_active(syl_mode)

        self._sidebar_expanded = not sidebar_collapsed
        self._sidebar_anim = QVariantAnimation(self)
        self._sidebar_anim.setDuration(260)
        self._sidebar_anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._sidebar_anim.valueChanged.connect(self._on_sidebar_anim_val)

        init_w = NavigationPane.EXPANDED_WIDTH if self._sidebar_expanded else 0
        self.nav_pane.setMinimumWidth(init_w)
        self.nav_pane.setMaximumWidth(init_w)
        self.nav_pane.setVisible(init_w > 0)
        self.title_bar.set_sidebar_expanded(self._sidebar_expanded)

        self._on_theme_toggled(self.theme.name)

    def _save_current_state(self) -> None:
        settings = {
            "volume": self.audio.volume,
            "is_muted": self.audio.is_muted,
            "play_mode": self.play_mode,
            "playback_rate": self.audio.playback_rate,
            "lyric_font_size": self.lyrics_stage.lyric_font_size,
            "show_translation": self.lyrics_stage.show_translation,
            "syllable_mode": self.lyrics_stage.syllable_mode,
            "lyric_align_center": self.lyrics_stage.align_center,
            "theme": self.theme.name,
            "sidebar_collapsed": not getattr(self, "_sidebar_expanded", True),
            "sound_profile": self.audio.sound_profile,
            "last_track_index": self.current_track_index,
        }
        save_player_state(self.tracks, settings)

    def closeEvent(self, event: QCloseEvent) -> None:
        self.audio.pause()
        self.audio._decoder.stop()
        self.desktop_lyric.close()
        self._save_current_state()
        super().closeEvent(event)

    def _bind_signals(self) -> None:
        # 顶部栏信号
        self.title_bar.sidebarToggleClicked.connect(self._toggle_sidebar)
        self.title_bar.scrapeClicked.connect(self._open_online_scraper_dialog)
        self.title_bar.fontSizeDeltaRequested.connect(self.lyrics_stage.adjust_lyric_font_size)
        self.title_bar.translationToggled.connect(self.lyrics_stage.set_show_translation)
        self.title_bar.syllableModeToggled.connect(self.lyrics_stage.set_syllable_mode)
        self.title_bar.desktopLyricToggled.connect(self._toggle_desktop_lyric)
        self.title_bar.themeToggled.connect(self._on_theme_toggled)

        # 左侧栏信号
        self.nav_pane.trackSelected.connect(lambda idx: self.select_track(idx, auto_play=True))
        self.nav_pane.favoriteChanged.connect(self._on_nav_favorite_changed)
        self.nav_pane.trackActionRequested.connect(self._on_track_context_action)
        self.nav_pane.importFilesRequested.connect(self._on_import_files)
        self.nav_pane.importFolderRequested.connect(self._on_import_folder)
        self.nav_pane.importLrcRequested.connect(self._on_import_lrc_file)

        # 封面点击快捷匹配词图
        self.cover_card.scrapeRequested.connect(self._open_online_scraper_dialog)

        # 歌词舞台信号
        self.lyrics_stage.seekRequested.connect(
            lambda sec: self.audio.seek(sec, auto_play_on_seek=True, source="seek")
        )
        self.lyrics_stage.activeLineChanged.connect(self._on_active_lyric_line_changed)
        self.lyrics_stage.fontSizeChanged.connect(self.title_bar.update_font_size_display)
        self.lyrics_stage.lyricOffsetChanged.connect(self._on_lyric_offset_changed)

        # 底部控制栏信号
        self.transport_bar.playToggled.connect(self.audio.toggle_play)
        self.transport_bar.prevClicked.connect(self._on_prev_track)
        self.transport_bar.nextClicked.connect(self._on_next_track)
        self.transport_bar.playModeCycled.connect(self._on_play_mode_changed)
        self.transport_bar.favoriteToggled.connect(self._on_transport_favorite_toggled)
        self.transport_bar.rateCycled.connect(self._on_rate_cycled)
        self.transport_bar.audioSettingsClicked.connect(self._open_audio_settings_dialog)
        self.transport_bar.coverThumbClicked.connect(self._toggle_sidebar)
        self.transport_bar.seekRatioRequested.connect(
            lambda ratio: self.audio.seek(ratio * self.audio.duration, auto_play_on_seek=False, source="seek")
        )
        self.transport_bar.volumeChanged.connect(self.audio.set_volume)
        self.transport_bar.muteToggled.connect(self.audio.toggle_mute)

        # 桌面歌词信号
        self.desktop_lyric.prevClicked.connect(self._on_prev_track)
        self.desktop_lyric.playToggled.connect(self.audio.toggle_play)
        self.desktop_lyric.nextClicked.connect(self._on_next_track)
        self.desktop_lyric.closedByUser.connect(lambda: self.title_bar.set_desktop_lyric_active(False))

        # 音频引擎信号
        self.audio.trackChanged.connect(self._on_track_changed)
        self.audio.playStateChanged.connect(self._on_play_state_changed)
        self.audio.timeUpdated.connect(self._on_time_updated)
        self.audio.volumeChanged.connect(self.transport_bar.update_volume)
        self.audio.playbackRateChanged.connect(self.transport_bar.set_playback_rate)
        self.audio.soundProfileChanged.connect(
            lambda name, _g, _r: self.transport_bar.set_sound_profile_badge(name)
        )
        self.audio.trackEnded.connect(self._on_track_naturally_ended)

    def _on_sidebar_anim_val(self, val: float) -> None:
        w = int(round(float(val)))
        self.nav_pane.setMinimumWidth(w)
        self.nav_pane.setMaximumWidth(w)
        self.nav_pane.setVisible(w > 2)

    def _toggle_sidebar(self) -> None:
        self._sidebar_expanded = not getattr(self, "_sidebar_expanded", True)
        self.title_bar.set_sidebar_expanded(self._sidebar_expanded)
        self._sidebar_anim.stop()
        cur_w = self.nav_pane.width() if self.nav_pane.isVisible() else 0
        target_w = NavigationPane.EXPANDED_WIDTH if self._sidebar_expanded else 0
        if target_w > 0:
            self.nav_pane.setVisible(True)
        self._sidebar_anim.setStartValue(float(cur_w))
        self._sidebar_anim.setEndValue(float(target_w))
        self._sidebar_anim.start()

    def _toggle_desktop_lyric(self, open_flag: bool) -> None:
        if open_flag:
            self.desktop_lyric.show()
            self.desktop_lyric.raise_()
        else:
            self.desktop_lyric.hide()

    def _on_active_lyric_line_changed(self, line: LyricLine) -> None:
        self.transport_bar.update_lyric_snippet(line)
        self.desktop_lyric.update_lyric(line)

    def _on_lyric_offset_changed(self, new_offset: float) -> None:
        if 0 <= self.current_track_index < len(self.tracks):
            self.tracks[self.current_track_index].lyric_offset = new_offset

    def _on_play_mode_changed(self, mode: str) -> None:
        self.play_mode = mode
        mode_label = TransportBar._MODE_NAMES.get(mode, "列表循环")
        self.lyrics_stage._show_toast(f"播放模式: {mode_label}")

    def _on_rate_cycled(self) -> None:
        r = self.audio.cycle_playback_rate()
        self.lyrics_stage._show_toast(f"播放倍速: {r:g}x")

    def _on_nav_favorite_changed(self, idx: int) -> None:
        if idx == self.current_track_index and 0 <= idx < len(self.tracks):
            self.transport_bar.set_track_info(self.tracks[idx])

    def _on_transport_favorite_toggled(self) -> None:
        if not (0 <= self.current_track_index < len(self.tracks)):
            return
        cur = self.tracks[self.current_track_index]
        cur.is_favorite = not cur.is_favorite
        self.transport_bar.set_track_info(cur)
        self.nav_pane.refresh_item(self.current_track_index)
        self.lyrics_stage._show_toast("已添加到「我喜欢」" if cur.is_favorite else "已取消喜欢")

    def _on_track_context_action(self, index: int, action: str) -> None:
        if not (0 <= index < len(self.tracks)):
            return
        if action == "play":
            self.select_track(index, auto_play=True)
        elif action == "scrape":
            self.select_track(index, auto_play=False)
            self._open_online_scraper_dialog()
        elif action == "import_lrc":
            self.select_track(index, auto_play=False)
            self._on_import_lrc_file()
        elif action == "remove":
            if len(self.tracks) <= 1:
                self.lyrics_stage._show_toast("至少保留一首曲目")
                return
            self.tracks.pop(index)
            if self.current_track_index >= len(self.tracks):
                self.current_track_index = max(0, len(self.tracks) - 1)
            self.nav_pane.rebuild_list(self.tracks, active_index=self.current_track_index)
            self.select_track(self.current_track_index, auto_play=self.audio.is_playing)

    def _open_audio_settings_dialog(self) -> None:
        cur_offset = (
            self.tracks[self.current_track_index].lyric_offset
            if (0 <= self.current_track_index < len(self.tracks))
            else 0.0
        )
        dlg = AudioSettingsDialog(
            current_profile=self.audio.sound_profile,
            current_gains=self.audio.eq_gains_db,
            current_reverb=self.audio.reverb_mix,
            current_rate=self.audio.playback_rate,
            current_offset=cur_offset,
            sleep_remaining_sec=self.audio.sleep_remaining_sec,
            theme=self.theme,
            parent=self,
        )
        dlg.soundProfileChanged.connect(
            lambda prof: (
                self.audio.set_sound_profile(prof, trigger_preview=True),
                self.lyrics_stage._show_toast(f"声学预设: {prof}"),
            )
        )
        dlg.customEqChanged.connect(
            lambda gains, rev, prev: self.audio.set_custom_eq(gains, rev, trigger_preview=prev)
        )
        dlg.playbackRateChanged.connect(self.audio.set_playback_rate)
        dlg.lyricOffsetDeltaRequested.connect(self.lyrics_stage.adjust_lyric_offset)
        dlg.sleepTimerRequested.connect(self.audio.set_sleep_timer)
        dlg.exec()

    # =========================================================
    # 音乐文件 / 文件夹 / LRC 歌词导入与拖拽支持
    # =========================================================
    def _on_import_files(self) -> None:
        files, _ = QFileDialog.getOpenFileNames(
            self,
            "选择要导入的音乐文件",
            "",
            "音频文件 (*.mp3 *.flac *.wav *.m4a *.ogg *.aac *.wma);;所有文件 (*.*)",
        )
        if files:
            self._append_audio_files(files, auto_play_first=True)

    def _on_import_folder(self) -> None:
        folder = QFileDialog.getExistingDirectory(self, "选择音乐文件夹")
        if folder:
            audio_files = scan_folder_for_audio(folder)
            if audio_files:
                self._append_audio_files(audio_files, auto_play_first=True)

    def _on_import_lrc_file(self) -> None:
        if not self.tracks:
            return
        lrc_file, _ = QFileDialog.getOpenFileName(
            self,
            "为当前歌曲导入本地 LRC 歌词",
            "",
            "LRC 歌词文件 (*.lrc *.txt);;所有文件 (*.*)",
        )
        if not lrc_file:
            return
        p = Path(lrc_file)
        for enc in ("utf-8", "utf-8-sig", "gb18030", "utf-16"):
            try:
                content = p.read_text(encoding=enc)
                if content.strip():
                    parsed = parse_lrc_text(content)
                    if parsed:
                        cur = self.tracks[self.current_track_index]
                        cur.lyrics = parsed
                        self.lyrics_stage.load_lyrics(parsed, lyric_offset=cur.lyric_offset)
                        self.lyrics_stage.sync_time(self.audio.current_time, source="seek")
                        self.transport_bar.set_status_message(f"已加载歌词：{p.name}")
                    break
            except Exception:
                continue

    def _append_audio_files(self, file_paths: List[str], auto_play_first: bool = True) -> None:
        existing_paths = {t.file_path for t in self.tracks if t.file_path}
        first_new_index: Optional[int] = None

        for fp in file_paths:
            norm_p = str(Path(fp).resolve())
            if norm_p in existing_paths:
                continue
            new_track = create_track_from_file(norm_p, index_hint=len(self.tracks))
            if first_new_index is None:
                first_new_index = len(self.tracks)
            self.tracks.append(new_track)
            existing_paths.add(norm_p)
            self._trigger_auto_scrape_if_needed(new_track)

        if first_new_index is not None:
            self.nav_pane.rebuild_list(self.tracks, active_index=first_new_index)
            self.select_track(first_new_index, auto_play=auto_play_first)
            self._save_current_state()

    def dragEnterEvent(self, event: QDragEnterEvent) -> None:
        if event.mimeData().hasUrls():
            event.acceptProposedAction()
        else:
            super().dragEnterEvent(event)

    def dropEvent(self, event: QDropEvent) -> None:
        urls = event.mimeData().urls()
        audio_files: List[str] = []
        lrc_files: List[str] = []

        for u in urls:
            local = u.toLocalFile()
            if not local:
                continue
            p = Path(local)
            if p.is_dir():
                audio_files.extend(scan_folder_for_audio(str(p)))
            elif p.is_file():
                suf = p.suffix.lower()
                if suf in SUPPORTED_AUDIO_EXTS:
                    audio_files.append(str(p.resolve()))
                elif suf == ".lrc":
                    lrc_files.append(str(p.resolve()))

        if audio_files:
            self._append_audio_files(audio_files, auto_play_first=True)
        elif lrc_files and self.tracks:
            for enc in ("utf-8", "utf-8-sig", "gb18030", "utf-16"):
                try:
                    txt = Path(lrc_files[0]).read_text(encoding=enc)
                    if txt.strip():
                        parsed = parse_lrc_text(txt)
                        if parsed:
                            cur = self.tracks[self.current_track_index]
                            cur.lyrics = parsed
                            self.lyrics_stage.load_lyrics(parsed, lyric_offset=cur.lyric_offset)
                            self.lyrics_stage.sync_time(self.audio.current_time, source="seek")
                        break
                except Exception:
                    continue
        event.acceptProposedAction()

    # =========================================================
    # 在线歌词与专辑封面刮削
    # =========================================================
    def _trigger_auto_scrape_if_needed(self, track: NordicTrack) -> None:
        has_real_lyrics = len(track.lyrics) > 2
        has_cover = bool(track.cover_bytes or track.cover_path)
        if has_real_lyrics and has_cover:
            return

        worker = AutoScrapeWorker(
            track,
            need_lyrics=(not has_real_lyrics),
            need_cover=(not has_cover),
            parent=self,
        )
        worker.scrapeCompleted.connect(self._on_auto_scrape_completed)
        self._active_workers.append(worker)
        worker.start()

    def _on_auto_scrape_completed(
        self,
        track_id: str,
        lyrics: List[LyricLine],
        cover_bytes: Optional[bytes],
        album_name: str,
    ) -> None:
        for idx, t in enumerate(self.tracks):
            if t.track_id == track_id:
                if lyrics:
                    t.lyrics = lyrics
                if cover_bytes:
                    t.cover_bytes = cover_bytes
                if album_name and not t.album:
                    t.album = album_name

                self.nav_pane.refresh_item(idx)
                if idx == self.current_track_index:
                    self.cover_card.update_track(t)
                    self.transport_bar.set_track_info(t)
                    if lyrics:
                        self.lyrics_stage.load_lyrics(t.lyrics, lyric_offset=t.lyric_offset)
                        self.lyrics_stage.sync_time(self.audio.current_time, source="seek")
                break

    def _open_online_scraper_dialog(self) -> None:
        if not self.tracks:
            return
        cur_track = self.tracks[self.current_track_index]
        dlg = OnlineScraperDialog(cur_track, theme=self.theme, parent=self)
        dlg.matchApplied.connect(
            lambda lyrics, cover_bytes, album_name: self._apply_scraped_to_current(
                lyrics, cover_bytes, album_name
            )
        )
        dlg.exec()

    def _apply_scraped_to_current(
        self,
        lyrics: List[LyricLine],
        cover_bytes: Optional[bytes],
        album_name: str,
    ) -> None:
        cur_track = self.tracks[self.current_track_index]
        if lyrics:
            cur_track.lyrics = lyrics
            self.lyrics_stage.load_lyrics(lyrics, lyric_offset=cur_track.lyric_offset)
            self.lyrics_stage.sync_time(self.audio.current_time, source="seek")
        if cover_bytes:
            cur_track.cover_bytes = cover_bytes
        if album_name:
            cur_track.album = album_name

        self.cover_card.update_track(cur_track)
        self.transport_bar.set_track_info(cur_track)
        self.nav_pane.refresh_item(self.current_track_index)
        self.transport_bar.set_status_message("已更新在线歌词与封面")

    # =========================================================
    # 播放与模式调度
    # =========================================================
    def select_track(self, index: int, auto_play: bool = False) -> None:
        if not self.tracks:
            return
        self.current_track_index = index % len(self.tracks)
        track = self.tracks[self.current_track_index]
        self.nav_pane.set_active_index(self.current_track_index)
        self.audio.load_track(track, auto_play=auto_play)

    def _on_prev_track(self) -> None:
        if not self.tracks:
            return
        if self.play_mode == "shuffle" and len(self.tracks) > 1:
            choices = [i for i in range(len(self.tracks)) if i != self.current_track_index]
            next_idx = random.choice(choices)
        else:
            next_idx = (self.current_track_index - 1) % len(self.tracks)
        self.select_track(next_idx, auto_play=True)

    def _on_next_track(self) -> None:
        if not self.tracks:
            return
        if self.play_mode == "shuffle" and len(self.tracks) > 1:
            choices = [i for i in range(len(self.tracks)) if i != self.current_track_index]
            next_idx = random.choice(choices)
        else:
            next_idx = (self.current_track_index + 1) % len(self.tracks)
        self.select_track(next_idx, auto_play=True)

    def _on_track_naturally_ended(self) -> None:
        if not self.tracks:
            return
        if self.play_mode == "loop_one":
            self.audio.seek(0.0, auto_play_on_seek=True, source="init")
        elif self.play_mode == "sequential":
            if self.current_track_index + 1 < len(self.tracks):
                self.select_track(self.current_track_index + 1, auto_play=True)
        else:
            self._on_next_track()

    def _on_track_changed(self, track: NordicTrack) -> None:
        self.cover_card.update_track(track)
        self.lyrics_stage.load_lyrics(track.lyrics, lyric_offset=track.lyric_offset)
        self.transport_bar.set_track_info(track)
        self.transport_bar.update_progress(0.0, track.duration)
        if track.lyrics:
            self.desktop_lyric.update_lyric(track.lyrics[0])

    def _on_play_state_changed(self, is_playing: bool) -> None:
        self.cover_card.set_playing(is_playing)
        self.transport_bar.set_playing_state(is_playing)

    def _on_time_updated(self, current_time: float, duration: float, source: str) -> None:
        self.transport_bar.update_progress(current_time, duration)
        self.lyrics_stage.sync_time(current_time, source=source)

    def _on_theme_toggled(self, theme_name: str) -> None:
        self.theme = DUSK_THEME if theme_name == "dusk" else DAYLIGHT_THEME
        self.title_bar.set_theme(self.theme)
        self.nav_pane.set_theme(self.theme)
        self.cover_card.set_theme(self.theme)
        self.lyrics_stage.set_theme(self.theme)
        self.transport_bar.set_theme(self.theme)
        self.desktop_lyric.set_theme(self.theme)
        self.frame_overlay.set_theme(self.theme)
        self.update()

    # =========================================================
    # 无边框窗口四周 8 方向鼠标拖拽缩放支持
    # =========================================================
    def _detect_resize_edges(self, pos: QPointF) -> Qt.Edge:
        if self.isMaximized():
            return Qt.Edge(0)
        margin = 6.0
        w, h = float(self.width()), float(self.height())
        edges = Qt.Edge(0)
        if pos.x() <= margin:
            edges |= Qt.Edge.LeftEdge
        elif pos.x() >= w - margin:
            edges |= Qt.Edge.RightEdge
        if pos.y() <= margin:
            edges |= Qt.Edge.TopEdge
        elif pos.y() >= h - margin:
            edges |= Qt.Edge.BottomEdge
        return edges

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            edges = self._detect_resize_edges(event.position())
            if edges != Qt.Edge(0) and self.windowHandle() is not None:
                self.windowHandle().startSystemResize(edges)
                event.accept()
                return
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        edges = self._detect_resize_edges(event.position())
        if edges in (Qt.Edge.LeftEdge | Qt.Edge.TopEdge, Qt.Edge.RightEdge | Qt.Edge.BottomEdge):
            self.setCursor(Qt.CursorShape.SizeFDiagCursor)
        elif edges in (Qt.Edge.RightEdge | Qt.Edge.TopEdge, Qt.Edge.LeftEdge | Qt.Edge.BottomEdge):
            self.setCursor(Qt.CursorShape.SizeBDiagCursor)
        elif edges in (Qt.Edge.LeftEdge, Qt.Edge.RightEdge):
            self.setCursor(Qt.CursorShape.SizeHorCursor)
        elif edges in (Qt.Edge.TopEdge, Qt.Edge.BottomEdge):
            self.setCursor(Qt.CursorShape.SizeVerCursor)
        else:
            self.setCursor(Qt.CursorShape.ArrowCursor)
        super().mouseMoveEvent(event)

    def keyPressEvent(self, event: QKeyEvent) -> None:
        if event.key() == Qt.Key.Key_Space:
            self.audio.toggle_play()
            event.accept()
            return
        if event.modifiers() & Qt.KeyboardModifier.ControlModifier:
            if event.key() in (Qt.Key.Key_Plus, Qt.Key.Key_Equal):
                self.lyrics_stage.adjust_lyric_font_size(2)
                event.accept()
                return
            if event.key() == Qt.Key.Key_Minus:
                self.lyrics_stage.adjust_lyric_font_size(-2)
                event.accept()
                return
            if event.key() == Qt.Key.Key_Left:
                self._on_prev_track()
                event.accept()
                return
            if event.key() == Qt.Key.Key_Right:
                self._on_next_track()
                event.accept()
                return
        super().keyPressEvent(event)

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        rect = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        radius = 0.0 if self.isMaximized() else 12.0
        path = QPainterPath()
        path.addRoundedRect(rect, radius, radius)

        painter.fillPath(path, self.theme.mist_white)

        ambient = QRadialGradient(QPointF(self.width() * 0.28, self.height() * 0.42), 520.0)
        ambient.setColorAt(0.0, self.theme.sage_soft)
        ambient.setColorAt(1.0, Qt.GlobalColor.transparent)
        painter.fillPath(path, ambient)
