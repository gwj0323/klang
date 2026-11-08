import random
import threading
from pathlib import Path
from typing import List, Optional, Dict
from PyQt6.QtCore import (
    QThread,
    pyqtSignal,
    Qt,
    QRectF,
    QPointF,
    QVariantAnimation,
    QEasingCurve,
    QTimer,
    QEvent,
    QFileSystemWatcher,
)
from PyQt6.QtGui import (
    QFont,
    QPainter,
    QPen,
    QColor,
    QPainterPath,
    QRadialGradient,
    QKeyEvent,
    QDragEnterEvent,
    QDropEvent,
    QMouseEvent,
    QCloseEvent,
    QShowEvent,
    QMoveEvent,
    QIcon,
    QPixmap,
)
from PyQt6.QtWidgets import (
    QMainWindow,
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QFileDialog,
    QSystemTrayIcon,
)

from qt6_player.data.tracks import NordicTrack, LyricLine, parse_lrc_text
from qt6_player.theme.nordic_palette import DAYLIGHT_THEME, DUSK_THEME, NordicThemeColors
from qt6_player.services.audio_engine import NordicAudioEngine
from qt6_player.services.metadata_service import (
    SUPPORTED_AUDIO_EXTS,
    SUPPORTED_IMPORT_EXTS,
    create_track_from_file,
    parse_cue_file,
    scan_folder_for_audio,
    write_metadata_to_audio_file,
)
from qt6_player.services.scraper_service import AutoScrapeWorker, OnlineScraperDialog
from qt6_player.services.storage_service import (
    load_player_state,
    save_player_state,
    restore_tracks_from_state,
)
from qt6_player.components.title_bar import WinUITitleBar
from qt6_player.components.navigation_pane import (
    NavigationPane,
    TrackMetadataDialog,
    WatchedFoldersDialog,
)
from qt6_player.components.vinyl_cover_card import VinylCoverCard
from qt6_player.components.lyrics_stage import LyricsStage
from qt6_player.components.transport_bar import TransportBar
from qt6_player.components.desktop_lyric_overlay import DesktopLyricOverlay
from qt6_player.services.headphone_eq import HEADPHONE_PROFILES
from qt6_player.components.equalizer_dialog import AudioSettingsDialog
from qt6_player.components.lyric_poster_dialog import LyricPosterDialog
from qt6_player.components.mini_player_window import MiniPlayerWindow
from qt6_player.components.immersive_window import ImmersiveFullscreenWindow
from qt6_player.components.fluent_widgets import WinUIFlyoutMenu
from qt6_player.services.netease_service import netease_manager
from qframelesswindow import AcrylicWindow
from qt6_player.services.window_effects import (
    apply_window_system_theme_and_corners,
    apply_window_backdrop,
    suppress_native_caption_buttons,
)
from qt6_player.services.media_control_service import WindowsMediaControlService


def _lerp_color(c_from: QColor, c_to: QColor, t: float) -> QColor:
    p = max(0.0, min(1.0, float(t)))
    return QColor(
        int(round(c_from.red() + (c_to.red() - c_from.red()) * p)),
        int(round(c_from.green() + (c_to.green() - c_from.green()) * p)),
        int(round(c_from.blue() + (c_to.blue() - c_from.blue()) * p)),
        int(round(c_from.alpha() + (c_to.alpha() - c_from.alpha()) * p)),
    )


class WindowFrameOverlay(QWidget):
    def __init__(self, parent: QWidget):
        super().__init__(parent)
        self.theme: NordicThemeColors = DAYLIGHT_THEME
        self.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents, True)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.hide()

    def set_theme(self, theme: NordicThemeColors) -> None:
        self.theme = theme

    def paintEvent(self, event) -> None:
        # 已由 Windows 11 DWM 原生硬件圆角边框接管，不再叠加软件伪边框避免双圆角重影
        return


class AutoTranslationWorker(QThread):
    translationReady = pyqtSignal(str, str)  # track_id, tlyric

    def __init__(self, track: NordicTrack, parent=None):
        super().__init__(parent)
        self.track_id = track.track_id
        self.title = track.title
        self.artist = track.artist

    def run(self) -> None:
        try:
            from qt6_player.services.scraper_service import fetch_netease_translation_only
            tlyric = fetch_netease_translation_only(self.title, self.artist)
            self.translationReady.emit(self.track_id, tlyric or "")
        except Exception:
            self.translationReady.emit(self.track_id, "")


class FolderWatchScanWorker(QThread):
    scanFinished = pyqtSignal(list)

    def __init__(self, folders: List[str], parent=None):
        super().__init__(parent)
        self.folders = list(folders)

    def run(self) -> None:
        found: List[str] = []
        for f in self.folders:
            if f and Path(f).is_dir():
                found.extend(scan_folder_for_audio(f))
        self.scanFinished.emit(found)


class NordicWinUI3Window(AcrylicWindow):
    _async_callback_signal = pyqtSignal(object)

    def __init__(self):
        super().__init__()
        self._async_callback_signal.connect(self._run_async_callback)
        self._central_widget: Optional[QWidget] = None
        self._central_layout = QVBoxLayout(self)
        self._central_layout.setContentsMargins(0, 0, 0, 0)
        self._central_layout.setSpacing(0)
        if hasattr(self, "titleBar"):
            self.titleBar.hide()
            self.titleBar.setFixedSize(0, 0)
        suppress_native_caption_buttons(self.winId())
        self.setStyleSheet("NordicWinUI3Window { background: transparent; }")

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

        # 注入 Apple Sans (SF Pro) 全局字体体系
        from qt6_player.theme.nordic_palette import apply_apple_sans_globally, get_nordic_font
        apply_apple_sans_globally()
        self.setFont(get_nordic_font(13, QFont.Weight.Normal))

        # 读取持久化状态
        self._initializing: bool = True
        self._saved_state = load_player_state()
        self.theme: NordicThemeColors = (
            DUSK_THEME if self._saved_state.get("theme") == "dusk" else DAYLIGHT_THEME
        )
        self.tracks: List[NordicTrack] = restore_tracks_from_state(self._saved_state)
        self.local_tracks: List[NordicTrack] = list(self.tracks)
        self._netease_load_seq: int = 0
        init_idx = int(self._saved_state.get("last_track_index", 0))
        if not (0 <= init_idx < len(self.tracks)):
            init_idx = 0
        self.current_track_index: int = init_idx
        self.play_mode: str = self._saved_state.get("play_mode", "loop_list")
        self._flow_history: List[str] = []
        self.dolby_atmos_mode: str = str(self._saved_state.get("dolby_atmos_mode", "dolby_atmos"))
        self.spatial_width: float = float(self._saved_state.get("spatial_width", 1.35))
        self.spatial_height: float = float(self._saved_state.get("spatial_height", 0.65))
        self.backdrop_material: str = str(self._saved_state.get("backdrop_material", "mica"))

        self.audio = NordicAudioEngine(self)
        self.audio.set_dolby_atmos_mode(self.dolby_atmos_mode)
        self.audio.set_spatial_parameters(self.spatial_width, self.spatial_height)
        self.desktop_lyric = DesktopLyricOverlay()
        self.mini_player = MiniPlayerWindow()
        self.immersive_window = ImmersiveFullscreenWindow(self)
        saved_vm = self._saved_state.get("vinyl_mode", "pure")
        self.immersive_window.artwork_view.set_vinyl_mode(saved_vm)
        self._active_eq_dialog: Optional[AudioSettingsDialog] = None
        self._active_workers: List[AutoScrapeWorker] = []
        self._tray_menu: Optional[WinUIFlyoutMenu] = None

        # 环境流光色彩与日夜平滑插值过渡状态
        self._bg_col_start: QColor = QColor(self.theme.mist_white)
        self._bg_col_target: QColor = QColor(self.theme.mist_white)
        self._bg_col_cur: QColor = QColor(self.theme.mist_white)
        self._amb1_start: QColor = QColor("#6E8277")
        self._amb1_target: QColor = QColor("#6E8277")
        self._amb1_cur: QColor = QColor("#6E8277")
        self._amb2_start: QColor = QColor("#C9BBAE")
        self._amb2_target: QColor = QColor("#C9BBAE")
        self._amb2_cur: QColor = QColor("#C9BBAE")
        self._audio_energy: float = 0.0
        self._cached_bg_pixmap: Optional[QPixmap] = None
        self._bg_dirty: bool = True

        self._ambient_anim = QVariantAnimation(self)
        self._ambient_anim.setDuration(760)
        self._ambient_anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._ambient_anim.valueChanged.connect(self._on_ambient_anim_val)

        self._theme_anim = QVariantAnimation(self)
        self._theme_anim.setDuration(340)
        self._theme_anim.setEasingCurve(QEasingCurve.Type.OutCubic)
        self._theme_anim.valueChanged.connect(self._on_theme_anim_val)

        self._autosave_timer = QTimer(self)
        self._autosave_timer.setSingleShot(True)
        self._autosave_timer.setInterval(1200)
        self._autosave_timer.timeout.connect(self._save_current_state)

        self.watched_folders: List[str] = [
            str(p) for p in (self._saved_state.get("watched_folders") or []) if str(p).strip()
        ]
        self._fs_watcher = QFileSystemWatcher(self)
        self._fs_watcher.directoryChanged.connect(self._on_watched_dir_changed)
        self._watch_scan_timer = QTimer(self)
        self._watch_scan_timer.setSingleShot(True)
        self._watch_scan_timer.setInterval(1500)
        self._watch_scan_timer.timeout.connect(self._scan_watched_folders_async)
        self._watch_worker: Optional[FolderWatchScanWorker] = None

        self._setup_ui()
        self._setup_system_tray(icon_path)
        self._bind_signals()
        self._apply_restored_settings()
        self._sync_fs_watcher_paths()
        if self.tracks:
            self.select_track(init_idx, auto_play=False)
        else:
            self._clear_now_playing_ui()
        self._initializing = False
        self._save_current_state()
        if self.watched_folders:
            QTimer.singleShot(800, self._scan_watched_folders_async)

    def _run_async_callback(self, fn) -> None:
        try:
            fn()
        except Exception:
            pass

    def _on_ambient_anim_val(self, val: float) -> None:
        p = float(val)
        self._amb1_cur = _lerp_color(self._amb1_start, self._amb1_target, p)
        self._amb2_cur = _lerp_color(self._amb2_start, self._amb2_target, p)
        self._bg_dirty = True
        self.update()

    def _on_theme_anim_val(self, val: float) -> None:
        self._bg_col_cur = _lerp_color(self._bg_col_start, self._bg_col_target, float(val))
        self._bg_dirty = True
        self.update()

    def _on_ambient_colors_updated(self, c1: QColor, c2: QColor) -> None:
        if self._amb1_cur != c1 or self._amb2_cur != c2:
            self._amb1_cur = QColor(c1)
            self._amb2_cur = QColor(c2)
            self._bg_dirty = True
            self.update()

    def _setup_system_tray(self, icon_path: Path) -> None:
        self.tray_icon = QSystemTrayIcon(self)
        if icon_path.exists():
            self.tray_icon.setIcon(QIcon(str(icon_path)))
            self.mini_player.setWindowIcon(QIcon(str(icon_path)))
        self.tray_icon.setToolTip("Klang — Nordic Minimalist Audio Player")
        self.tray_icon.activated.connect(self._on_tray_activated)
        if QSystemTrayIcon.isSystemTrayAvailable():
            self.tray_icon.show()

    def _on_tray_activated(self, reason: QSystemTrayIcon.ActivationReason) -> None:
        if reason in (
            QSystemTrayIcon.ActivationReason.Trigger,
            QSystemTrayIcon.ActivationReason.DoubleClick,
        ):
            self._restore_from_mini_or_tray()
        elif reason == QSystemTrayIcon.ActivationReason.Context:
            from PyQt6.QtGui import QCursor
            items = [
                ("toggle_play", "music_file", "暂停播放" if self.audio.is_playing else "开始播放"),
                ("prev", "chevron_down", "上一首曲目"),
                ("next", "chevron_down", "下一首曲目"),
                ("desktop_lyric", "desktop_lyric", "开关桌面歌词"),
                ("mini_mode", "mini_player", "切换迷你悬浮模式"),
                ("show_main", "sparkle", "显示主窗口"),
                ("quit", "close", "退出 Klang"),
            ]
            self._tray_menu = WinUIFlyoutMenu(items, theme=self.theme, backdrop_material=getattr(self, "backdrop_material", "mica"), parent=None)
            self._tray_menu.actionTriggered.connect(self._on_tray_menu_action)
            pos = QCursor.pos()
            self._tray_menu.popup_animated(pos - self._tray_menu.rect().bottomLeft())

    def _on_tray_menu_action(self, act: str) -> None:
        if act == "toggle_play":
            self.audio.toggle_play()
        elif act == "prev":
            self._on_prev_track()
        elif act == "next":
            self._on_next_track()
        elif act == "desktop_lyric":
            self.title_bar._on_toggle_desktop_lyric()
        elif act == "mini_mode":
            self._toggle_mini_player_mode()
        elif act == "show_main":
            self._restore_from_mini_or_tray()
        elif act == "quit":
            self.close()

    def _toggle_mini_player_mode(self) -> None:
        if self.mini_player.isVisible():
            self._restore_from_mini_or_tray()
        else:
            self._enter_mini_player_mode()

    def _enter_mini_player_mode(self) -> None:
        if 0 <= self.current_track_index < len(self.tracks):
            self.mini_player.set_track(self.tracks[self.current_track_index])
        self.mini_player.set_playing(self.audio.is_playing)
        self.mini_player.set_theme(self.theme)
        geo = self.frameGeometry()
        self.mini_player.move(
            geo.center().x() - self.mini_player.width() // 2,
            geo.center().y() - self.mini_player.height() // 2,
        )
        self.hide()
        self.mini_player.show_animated()

    def _restore_from_mini_or_tray(self) -> None:
        if self.mini_player.isVisible():
            self.mini_player.hide()
        self.showNormal() if not self._saved_state.get("is_maximized") else self.showMaximized()
        self.raise_()
        self.activateWindow()

    def _toggle_immersive_mode(self) -> None:
        if self.immersive_window.isVisible():
            self.immersive_window.exit_immersive()
        else:
            cur_track = self.tracks[self.current_track_index] if (0 <= self.current_track_index < len(self.tracks)) else None
            scr = None
            if self.windowHandle():
                scr = self.windowHandle().screen()
            if scr is None:
                scr = self.screen()
            self.immersive_window.enter_immersive(
                current_track=cur_track,
                is_playing=self.audio.is_playing,
                current_time=self.audio.current_time,
                duration=self.audio.duration,
                play_mode=self.play_mode,
                volume=self.audio.volume,
                target_screen=scr,
                show_translation=self.lyrics_stage.show_translation,
                syllable_mode=self.lyrics_stage.syllable_mode,
                dolby_atmos_active=self.audio.is_dolby_atmos_active,
            )
            self.hide()

    def _on_immersive_closed(self) -> None:
        self.transport_bar.update_progress(self.audio.current_time, self.audio.duration)
        self.lyrics_stage.sync_time(self.audio.current_time, source="seek")
        self.showNormal() if not self._saved_state.get("is_maximized") else self.showMaximized()
        self.raise_()
        self.activateWindow()

    def schedule_save(self) -> None:
        """防抖自动保存设置到本地磁盘，避免卡顿 I/O"""
        if getattr(self, "_initializing", False):
            return
        if hasattr(self, "_autosave_timer"):
            self._autosave_timer.start()

    def updateFrameless(self) -> None:
        super().updateFrameless()
        if hasattr(self, "titleBar"):
            self.titleBar.hide()
            self.titleBar.setFixedSize(0, 0)
        suppress_native_caption_buttons(self.winId())

    def setCentralWidget(self, widget: QWidget) -> None:
        while self._central_layout.count():
            item = self._central_layout.takeAt(0)
            if item.widget() is not None:
                item.widget().setParent(None)
        self._central_widget = widget
        self._central_layout.addWidget(widget)

    def centralWidget(self) -> Optional[QWidget]:
        return self._central_widget

    def _setup_ui(self) -> None:
        central = QWidget(self)
        central.setMouseTracking(True)
        self.setCentralWidget(central)

        root_vbox = QVBoxLayout(central)
        root_vbox.setContentsMargins(0, 0, 0, 0)
        root_vbox.setSpacing(0)

        self.title_bar = WinUITitleBar(central)
        root_vbox.addWidget(self.title_bar)

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
        self.cover_card.set_dolby_atmos_active(self.audio.is_dolby_atmos_active)
        stage_hbox.addWidget(self.cover_card, 0, Qt.AlignmentFlag.AlignVCenter)

        self.lyrics_stage = LyricsStage(stage_container)
        stage_hbox.addWidget(self.lyrics_stage, 1)

        ws_hbox.addWidget(stage_container, 1)
        root_vbox.addWidget(workspace, 1)

        self.transport_bar = TransportBar(central)
        root_vbox.addWidget(self.transport_bar)

        self.frame_overlay = WindowFrameOverlay(central)
        self.frame_overlay.raise_()

    def _apply_backdrop_to_window(self) -> None:
        try:
            tint = self.theme.mist_white.name()
            apply_window_backdrop(
                self,
                material=getattr(self, "backdrop_material", "mica"),
                is_dark=(self.theme.name == "dusk"),
                is_maximized=self.isMaximized(),
                tint_hex=tint,
            )
        except Exception:
            pass



    def showEvent(self, event: QShowEvent) -> None:
        super().showEvent(event)
        if hasattr(self, "frame_overlay") and self.centralWidget():
            self.frame_overlay.setGeometry(self.centralWidget().rect())
            self.frame_overlay.raise_()
        self._handle_window_state_change()
        QTimer.singleShot(0, self._handle_window_state_change)
        self._apply_backdrop_to_window()
        QTimer.singleShot(50, self._apply_backdrop_to_window)

    def changeEvent(self, event: QEvent) -> None:
        super().changeEvent(event)
        if event.type() == QEvent.Type.WindowStateChange:
            self._handle_window_state_change()
            # 兼容 Windows DWM 动画与异步消息延迟：在多个时间点再次确保正确重绘圆角
            QTimer.singleShot(0, self._handle_window_state_change)
            QTimer.singleShot(40, self._handle_window_state_change)
            QTimer.singleShot(120, self._handle_window_state_change)
        elif event.type() == QEvent.Type.ActivationChange:
            # 窗口切到前台/后台时预先喂饱 WASAPI 硬件缓冲，杜绝 Alt+Tab 切换窗口瞬间的欠载断流爆音
            if hasattr(self, 'audio') and self.audio and self.audio.is_playing:
                self.audio._feed_audio_sink()

    def moveEvent(self, event: QMoveEvent) -> None:
        super().moveEvent(event)
        self.schedule_save()

    def resizeEvent(self, event) -> None:
        super().resizeEvent(event)
        if hasattr(self, "frame_overlay") and self.centralWidget():
            self.frame_overlay.setGeometry(self.centralWidget().rect())
            self.frame_overlay.raise_()
            self.frame_overlay.update()
        if hasattr(self, "transport_bar"):
            self.transport_bar.update()
        if hasattr(self, "title_bar"):
            self.title_bar.set_window_maximized(self.isMaximized())
        self._bg_dirty = True
        self.update()
        self.schedule_save()

    def _handle_window_state_change(self) -> None:
        """当窗口在最大化与常规尺寸之间切换时，同步更新组件圆角与最大化图标"""
        is_max = self.isMaximized()
        if hasattr(self, "title_bar"):
            self.title_bar.set_window_maximized(is_max)
        if hasattr(self, "frame_overlay") and self.centralWidget():
            self.frame_overlay.setGeometry(self.centralWidget().rect())
            self.frame_overlay.raise_()
            self.frame_overlay.update()
        if hasattr(self, "transport_bar"):
            self.transport_bar.update()
        self._apply_backdrop_to_window()
        self._bg_dirty = True
        self.update()
        self.schedule_save()

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
        sound_profile = str(st.get("sound_profile", "默认原声"))
        if sound_profile == "原声自然":
            sound_profile = "默认原声"
        saved_gains = st.get("eq_gains_db")
        saved_reverb = st.get("reverb_mix")
        saved_out_mode = st.get("audio_output_mode", "binaural")
        saved_dev_id = str(st.get("selected_device_id", "default"))
        saved_xfade = float(st.get("crossfade_sec", 0.0))
        saved_rg = bool(st.get("replay_gain_enabled", False))
        saved_vb = float(st.get("vocal_balance", 0.0))
        saved_tt = bool(st.get("turntable_physics", True))
        saved_vw = float(st.get("vinyl_warmth", 0.0))
        saved_hp = str(st.get("headphone_profile", "bypass"))
        if hasattr(self.audio, "set_output_device"):
            self.audio.selected_device_id = saved_dev_id
        if hasattr(self.audio, "set_output_channel_mode"):
            self.audio.set_output_channel_mode(saved_out_mode)
        if hasattr(self.audio, "set_crossfade_sec"):
            self.audio.set_crossfade_sec(saved_xfade)
        if hasattr(self.audio, "set_replay_gain_enabled"):
            self.audio.set_replay_gain_enabled(saved_rg)
        if hasattr(self.audio, "set_vocal_balance"):
            self.audio.set_vocal_balance(saved_vb)
        if hasattr(self.audio, "set_turntable_physics_enabled"):
            self.audio.set_turntable_physics_enabled(saved_tt)
        if hasattr(self.audio, "set_vinyl_warmth"):
            self.audio.set_vinyl_warmth(saved_vw)
        if hasattr(self.audio, "set_headphone_profile"):
            self.audio.set_headphone_profile(saved_hp)

        self.audio.set_volume(vol)
        if muted:
            self.audio.toggle_mute()
        self.audio.set_playback_rate(rate)

        if sound_profile == "自定义" and saved_gains is not None and saved_reverb is not None:
            self.audio.set_custom_eq([float(g) for g in saved_gains], float(saved_reverb), trigger_preview=False)
        else:
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
        self._sidebar_anim.setDuration(300)
        self._sidebar_anim.setEasingCurve(QEasingCurve.Type.OutQuint)
        self._sidebar_anim.valueChanged.connect(self._on_sidebar_anim_val)

        init_w = NavigationPane.EXPANDED_WIDTH if self._sidebar_expanded else 0
        self.nav_pane.setMinimumWidth(init_w)
        self.nav_pane.setMaximumWidth(init_w)
        self.nav_pane.setVisible(init_w > 0)
        self.title_bar.set_sidebar_expanded(self._sidebar_expanded)

        # 恢复自定义歌单与排序/分类偏好
        saved_playlists = st.get("custom_playlists")
        if isinstance(saved_playlists, dict):
            self.nav_pane.set_custom_playlists(saved_playlists)
        saved_sort = str(st.get("nav_sort_mode", "default"))
        if saved_sort in NavigationPane._SORT_LABELS:
            self.nav_pane.sort_mode = saved_sort
        saved_grp = str(st.get("nav_group_mode", "artist"))
        if saved_grp in NavigationPane._GROUP_LABELS:
            self.nav_pane.group_mode = saved_grp
        self.nav_pane.rebuild_list(self.tracks, active_index=self.current_track_index)

        # 桌面歌词设置恢复
        dt_font = int(st.get("desktop_lyric_font_size", 22))
        self.desktop_lyric.font_size = dt_font
        dt_pos = st.get("desktop_lyric_pos")
        if dt_pos and isinstance(dt_pos, list) and len(dt_pos) == 2:
            self.desktop_lyric.move(int(dt_pos[0]), int(dt_pos[1]))
        if bool(st.get("desktop_lyric_open", False)):
            self.desktop_lyric.show()
            self.title_bar.set_desktop_lyric_active(True)

        # 窗口大小与位置恢复
        geo = st.get("window_geometry")
        if geo and isinstance(geo, list) and len(geo) == 4:
            gx, gy, gw, gh = int(geo[0]), int(geo[1]), int(geo[2]), int(geo[3])
            if gw >= self.minimumWidth() and gh >= self.minimumHeight():
                self.setGeometry(gx, gy, gw, gh)
        if bool(st.get("is_maximized", False)):
            self.showMaximized()

        self._on_theme_toggled(self.theme.name)

    def _save_current_state(self, sync: bool = False) -> None:
        if hasattr(self, "_autosave_timer"):
            self._autosave_timer.stop()

        geom = None
        if not self.isMaximized():
            geom = [self.x(), self.y(), self.width(), self.height()]
        elif hasattr(self, "_saved_state"):
            geom = self._saved_state.get("window_geometry")

        dt_pos = None
        if hasattr(self, "desktop_lyric") and self.desktop_lyric.isVisible():
            dt_pos = [self.desktop_lyric.x(), self.desktop_lyric.y()]
        elif hasattr(self, "_saved_state"):
            dt_pos = self._saved_state.get("desktop_lyric_pos")

        settings = {
            "volume": float(self.audio.volume),
            "is_muted": bool(self.audio.is_muted),
            "play_mode": str(self.play_mode),
            "playback_rate": float(self.audio.playback_rate),
            "lyric_font_size": int(self.lyrics_stage.lyric_font_size),
            "show_translation": bool(self.lyrics_stage.show_translation),
            "syllable_mode": bool(self.lyrics_stage.syllable_mode),
            "lyric_align_center": bool(self.lyrics_stage.align_center),
            "theme": str(self.theme.name),
            "sidebar_collapsed": not getattr(self, "_sidebar_expanded", True),
            "sound_profile": str(self.audio.sound_profile),
            "eq_gains_db": [float(g) for g in self.audio.eq_gains_db],
            "reverb_mix": float(self.audio.reverb_mix),
            "dolby_atmos_mode": str(self.audio.dolby_atmos_mode),
            "spatial_width": float(self.audio.spatial_width),
            "spatial_height": float(self.audio.spatial_height),
            "audio_output_mode": str(getattr(self.audio, "output_channel_mode", "binaural")),
            "selected_device_id": str(getattr(self.audio, "selected_device_id", "default")),
            "crossfade_sec": float(getattr(self.audio, "crossfade_sec", 0.0)),
            "replay_gain_enabled": bool(getattr(self.audio, "replay_gain_enabled", False)),
            "vocal_balance": float(getattr(self.audio, "vocal_balance", 0.0)),
            "turntable_physics": bool(getattr(self.audio, "turntable_physics_enabled", True)),
            "vinyl_warmth": float(getattr(self.audio, "vinyl_warmth", 0.0)),
            "headphone_profile": str(getattr(self.audio, "headphone_profile_id", "bypass")),
            "desktop_lyric_open": bool(hasattr(self, "desktop_lyric") and self.desktop_lyric.isVisible()),
            "desktop_lyric_pos": dt_pos,
            "desktop_lyric_font_size": int(self.desktop_lyric.font_size) if hasattr(self, "desktop_lyric") else 22,
            "immersive_fps": int(getattr(self.immersive_window, "target_fps", 120)),
            "backdrop_material": str(getattr(self, "backdrop_material", "mica")),
            "vinyl_mode": str(getattr(self.immersive_window.artwork_view, "vinyl_mode", "pure")),
            "window_geometry": geom,
            "is_maximized": bool(self.isMaximized()),
            "last_track_index": int(self.current_track_index if self.tracks is self.local_tracks else getattr(self, "_last_local_track_index", 0)),
            "custom_playlists": dict(getattr(self.nav_pane, "custom_playlists", {})),
            "watched_folders": list(getattr(self, "watched_folders", [])),
            "nav_sort_mode": str(getattr(self.nav_pane, "sort_mode", "default")),
            "nav_group_mode": str(getattr(self.nav_pane, "group_mode", "artist")),
        }
        self._saved_state.update(settings)
        save_player_state(self.local_tracks, settings, sync=sync)

    def closeEvent(self, event: QCloseEvent) -> None:
        self.audio.pause()
        self.audio._decoder.stop()
        if hasattr(self.audio, "_pre_decoder"):
            self.audio._pre_decoder.stop()
        for w in list(self._active_workers):
            try:
                w.scrapeCompleted.disconnect(self._on_auto_scrape_completed)
            except Exception:
                pass
        if hasattr(self, "mini_player"):
            self.mini_player.close()
        if hasattr(self, "immersive_window"):
            self.immersive_window.close()
        if hasattr(self, "tray_icon"):
            self.tray_icon.hide()
        if hasattr(self, "media_control") and self.media_control:
            self.media_control.cleanup()
        self.desktop_lyric.close()
        self._save_current_state(sync=True)
        super().closeEvent(event)

    def _bind_signals(self) -> None:
        # 顶部栏信号
        self.title_bar.sidebarToggleClicked.connect(self._toggle_sidebar)
        self.title_bar.scrapeClicked.connect(self._open_online_scraper_dialog)
        self.title_bar.fontSizeDeltaRequested.connect(self.lyrics_stage.adjust_lyric_font_size)
        self.title_bar.translationToggled.connect(self.lyrics_stage.set_show_translation)
        self.title_bar.translationToggled.connect(self.immersive_window.set_show_translation)
        self.title_bar.translationToggled.connect(self._on_translation_toggled_by_user)
        self.title_bar.translationToggled.connect(lambda _b: self.schedule_save())
        self.title_bar.syllableModeToggled.connect(self.lyrics_stage.set_syllable_mode)
        self.title_bar.syllableModeToggled.connect(self.immersive_window.set_syllable_mode)
        self.title_bar.syllableModeToggled.connect(lambda _b: self.schedule_save())
        self.title_bar.desktopLyricToggled.connect(self._toggle_desktop_lyric)
        self.title_bar.miniPlayerClicked.connect(self._toggle_mini_player_mode)
        self.title_bar.immersiveClicked.connect(self._toggle_immersive_mode)
        self.title_bar.themeToggled.connect(self._on_theme_toggled)
        self.title_bar.settingsClicked.connect(lambda: self._open_audio_settings_dialog(default_tab=2))
        self.immersive_window.settingsRequested.connect(lambda: self._open_audio_settings_dialog(default_tab=2))

        # 沉浸视界信号
        self.immersive_window.set_target_fps(int(self._saved_state.get("immersive_fps", 120)))
        self.immersive_window.fpsChanged.connect(lambda fps: (self._saved_state.update({"immersive_fps": fps}), self.schedule_save()))
        self.immersive_window.playToggled.connect(self.audio.toggle_play)
        self.immersive_window.prevRequested.connect(self._on_prev_track)
        self.immersive_window.nextRequested.connect(self._on_next_track)
        self.immersive_window.seekRatioRequested.connect(
            lambda ratio: self.audio.seek(ratio * self.audio.duration, auto_play_on_seek=False, source="seek")
        )
        self.immersive_window.seekTimeRequested.connect(
            lambda sec: self.audio.seek(sec, auto_play_on_seek=False, source="seek")
        )
        self.immersive_window.modeCycled.connect(self.transport_bar._cycle_play_mode)
        self.immersive_window.volumeRequested.connect(self.audio.set_volume)
        self.immersive_window.translationToggled.connect(self.lyrics_stage.set_show_translation)
        self.immersive_window.translationToggled.connect(self.title_bar.set_translation_state)
        self.immersive_window.translationToggled.connect(lambda _b: self.schedule_save())
        self.immersive_window.translationFetchRequested.connect(self._fetch_translation_for_current_track)
        self.immersive_window.syllableToggled.connect(self.lyrics_stage.set_syllable_mode)
        self.immersive_window.syllableToggled.connect(self.title_bar.set_syllable_state)
        self.immersive_window.syllableToggled.connect(lambda _b: self.schedule_save())
        self.immersive_window.closed.connect(self._on_immersive_closed)

        # 左侧栏信号
        self.nav_pane.trackSelected.connect(self._on_nav_track_selected)
        self.nav_pane.neteaseTrackSelected.connect(self._on_netease_track_selected)
        self.nav_pane.favoriteChanged.connect(self._on_nav_favorite_changed)
        self.nav_pane.trackActionRequested.connect(self._on_track_context_action)
        self.nav_pane.trackReordered.connect(self._on_track_reordered)
        self.nav_pane.importFilesRequested.connect(self._on_import_files)
        self.nav_pane.importFolderRequested.connect(self._on_import_folder)
        self.nav_pane.importLrcRequested.connect(self._on_import_lrc_file)
        self.nav_pane.watchFoldersRequested.connect(self._open_watched_folders_dialog)
        self.nav_pane.playlistsChanged.connect(self.schedule_save)

        # 封面点击快捷匹配词图 & 环境流光主色通知
        self.cover_card.scrapeRequested.connect(self._open_online_scraper_dialog)
        self.cover_card.ambientColorsChanged.connect(self._on_ambient_colors_updated)

        # 迷你悬浮播放器信号
        self.mini_player.playToggled.connect(self.audio.toggle_play)
        self.mini_player.prevClicked.connect(self._on_prev_track)
        self.mini_player.nextClicked.connect(self._on_next_track)
        self.mini_player.seekRatioRequested.connect(
            lambda ratio: self.audio.seek(ratio * self.audio.duration, auto_play_on_seek=True, source="seek")
        )
        self.mini_player.restoreRequested.connect(self._restore_from_mini_or_tray)

        # 歌词舞台信号
        self.lyrics_stage.seekRequested.connect(
            lambda sec: self.audio.seek(sec, auto_play_on_seek=True, source="seek")
        )
        self.lyrics_stage.activeLineChanged.connect(self._on_active_lyric_line_changed)
        self.lyrics_stage.fontSizeChanged.connect(self.title_bar.update_font_size_display)
        self.lyrics_stage.fontSizeChanged.connect(lambda _sz: self.schedule_save())
        self.lyrics_stage.lyricOffsetChanged.connect(self._on_lyric_offset_changed)
        self.lyrics_stage.posterRequested.connect(self._open_lyric_poster_dialog)
        self.immersive_window.posterRequested.connect(self._open_lyric_poster_dialog)

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
        self.desktop_lyric.closedByUser.connect(
            lambda: (self.title_bar.set_desktop_lyric_active(False), self.schedule_save())
        )
        self.desktop_lyric.positionChanged.connect(lambda _x, _y: self.schedule_save())
        self.desktop_lyric.fontSizeChanged.connect(lambda _sz: self.schedule_save())

        # 音频引擎信号
        self.audio.trackChanged.connect(self._on_track_changed)
        self.audio.playStateChanged.connect(self._on_play_state_changed)
        self.audio.timeUpdated.connect(self._on_time_updated)
        self.audio.volumeChanged.connect(self.transport_bar.update_volume)
        self.audio.volumeChanged.connect(lambda _v, _m: self.schedule_save())
        self.audio.playbackRateChanged.connect(self.transport_bar.set_playback_rate)
        self.audio.playbackRateChanged.connect(lambda _r: self.schedule_save())
        self.audio.soundProfileChanged.connect(
            lambda name, _g, _r: (self.transport_bar.set_sound_profile_badge(name), self.schedule_save())
        )
        self.audio.spectrumUpdated.connect(self._on_spectrum_updated)
        self.audio.trackEnded.connect(self._on_track_naturally_ended)
        self.audio.requestNextTrackPreload.connect(self._on_request_next_track_preload)
        self.audio.deviceHotplugNotification.connect(self.lyrics_stage._show_toast)
        self.audio.outputDevicesUpdated.connect(self._on_output_devices_updated)
        self.audio.acousticDnaReady.connect(self._on_acoustic_dna_ready)

        # Windows 系统级媒体控制 (SMTC) 与全局多媒体按键集成
        self.media_control = WindowsMediaControlService(self, parent=self)
        self.media_control.playRequested.connect(self.audio.play)
        self.media_control.pauseRequested.connect(self.audio.pause)
        self.media_control.playPauseToggled.connect(self.audio.toggle_play)
        self.media_control.nextRequested.connect(self._on_next_track)
        self.media_control.previousRequested.connect(self._on_prev_track)
        self.media_control.stopRequested.connect(self.audio.pause)

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
        self.schedule_save()

    def _toggle_desktop_lyric(self, open_flag: bool) -> None:
        if open_flag:
            self.desktop_lyric.show()
            self.desktop_lyric.raise_()
        else:
            self.desktop_lyric.hide()
        self.schedule_save()

    def _on_active_lyric_line_changed(self, line: LyricLine) -> None:
        self.transport_bar.update_lyric_snippet(line)
        if self.desktop_lyric.isVisible():
            self.desktop_lyric.update_lyric(line)
        if self.mini_player.isVisible():
            self.mini_player.update_lyric(line)

    def _on_spectrum_updated(self, bands: List[float]) -> None:
        if self.isVisible():
            self.cover_card.set_spectrum(bands)
        if self._active_eq_dialog is not None and self._active_eq_dialog.isVisible():
            self._active_eq_dialog.update_spectrum(bands)
        if hasattr(self, "immersive_window") and self.immersive_window.isVisible():
            self.immersive_window.update_spectrum(bands)

    def _on_track_reordered(self, from_idx: int, to_idx: int) -> None:
        if not (0 <= from_idx < len(self.local_tracks)) or not (0 <= to_idx < len(self.local_tracks)):
            return
        if from_idx == to_idx:
            return
        cur_track_obj = self.local_tracks[self.current_track_index] if (self.tracks is self.local_tracks and 0 <= self.current_track_index < len(self.local_tracks)) else None
        moved = self.local_tracks.pop(from_idx)
        self.local_tracks.insert(to_idx, moved)
        if self.tracks is self.local_tracks and cur_track_obj is not None and cur_track_obj in self.local_tracks:
            self.current_track_index = self.local_tracks.index(cur_track_obj)
        self.nav_pane.rebuild_list(self.local_tracks, active_index=self.current_track_index)
        self.lyrics_stage._show_toast("已调整曲目播放顺序")
        self.schedule_save()

    def _write_tags_in_background(self, track: NordicTrack, show_toast: bool = True) -> None:
        def _worker():
            ok, msg = write_metadata_to_audio_file(track, write_cover=True, write_lyrics=True)
            if show_toast and hasattr(self, "lyrics_stage"):
                QTimer.singleShot(0, lambda: self.lyrics_stage._show_toast(msg))

        threading.Thread(target=_worker, daemon=True).start()

    def _open_edit_metadata_dialog(self, index: int) -> None:
        if not (0 <= index < len(self.tracks)):
            return
        target = self.tracks[index]
        dlg = TrackMetadataDialog(target, theme=self.theme, parent=self)

        def _apply_meta(new_title: str, new_artist: str, new_album: str, write_to_file: bool = True) -> None:
            target.title = new_title
            target.artist = new_artist
            target.album = new_album
            self.nav_pane.refresh_item(index)
            if index == self.current_track_index:
                self.cover_card.update_track(target)
                self.transport_bar.set_track_info(target)
                self.mini_player.set_track(target)
            if write_to_file and target.file_path:
                self._write_tags_in_background(target, show_toast=True)
            else:
                self.lyrics_stage._show_toast("已保存曲目信息")
            self.schedule_save()

        dlg.metadataSaved.connect(_apply_meta)
        dlg.exec()

    def _on_lyric_offset_changed(self, new_offset: float) -> None:
        if 0 <= self.current_track_index < len(self.tracks):
            self.tracks[self.current_track_index].lyric_offset = new_offset
            self.schedule_save()

    def _on_play_mode_changed(self, mode: str) -> None:
        self.play_mode = mode
        mode_label = TransportBar._MODE_NAMES.get(mode, "列表循环")
        if mode == "smart_flow" and 0 <= self.current_track_index < len(self.tracks):
            badge = self.tracks[self.current_track_index].format_dna_badge()
            if badge:
                self.lyrics_stage._show_toast(f"情绪漫游已开启 · 当前声学指纹: {badge}")
            else:
                self.lyrics_stage._show_toast(f"播放模式: {mode_label}")
        else:
            self.lyrics_stage._show_toast(f"播放模式: {mode_label}")
        self._next_preloaded_index = None
        QTimer.singleShot(30, self._on_request_next_track_preload)
        if hasattr(self, "immersive_window") and self.immersive_window.isVisible():
            self.immersive_window.update_play_mode(mode)
        self.schedule_save()

    def _on_rate_cycled(self) -> None:
        r = self.audio.cycle_playback_rate()
        self.lyrics_stage._show_toast(f"播放倍速: {r:g}x")
        self.schedule_save()

    def _on_nav_favorite_changed(self, idx: int) -> None:
        if idx == self.current_track_index and 0 <= idx < len(self.tracks):
            self.transport_bar.set_track_info(self.tracks[idx])
        self.schedule_save()

    def _on_transport_favorite_toggled(self) -> None:
        if not (0 <= self.current_track_index < len(self.tracks)):
            return
        cur = self.tracks[self.current_track_index]
        cur.is_favorite = not cur.is_favorite
        self.transport_bar.set_track_info(cur)
        self.nav_pane.refresh_item(self.current_track_index)
        self.lyrics_stage._show_toast("已添加到「我喜欢」" if cur.is_favorite else "已取消喜欢")
        self.schedule_save()

    def _on_track_context_action(self, index: int, action: str) -> None:
        source_list = self.local_tracks if (self.nav_pane.view_mode != 4) else self.tracks
        if not (0 <= index < len(source_list)):
            return
        target = source_list[index]
        if action == "play":
            if self.nav_pane.view_mode != 4:
                self.tracks = self.local_tracks
            self.select_track(index, auto_play=True)
        elif action == "play_next":
            if index == self.current_track_index:
                self.lyrics_stage._show_toast("当前已在播放该曲目")
                return
            cur_track_obj = self.tracks[self.current_track_index] if (0 <= self.current_track_index < len(self.tracks)) else None
            target = self.tracks.pop(index)
            if cur_track_obj is not None and cur_track_obj in self.tracks:
                cur_idx = self.tracks.index(cur_track_obj)
                insert_pos = cur_idx + 1
                self.tracks.insert(insert_pos, target)
                self.current_track_index = cur_idx
            else:
                insert_pos = 0
                self.tracks.insert(0, target)
            self._next_preloaded_index = insert_pos
            self.audio.preload_next_track(target)
            self.nav_pane.rebuild_list(self.tracks, active_index=self.current_track_index)
            self.lyrics_stage._show_toast(f"已设为下一首播放：{target.title}")
            self.schedule_save()
        elif action == "add_to_playlist":
            from PyQt6.QtGui import QCursor
            items = [
                (f"pl:{name}", "music_file", f"添加到「{name}」")
                for name in self.nav_pane.custom_playlists.keys()
            ]
            items.append(("new_pl", "plus", "＋ 新建歌单并添加…"))
            menu = WinUIFlyoutMenu(items, theme=self.theme, backdrop_material=getattr(self, "backdrop_material", "mica"), parent=self)

            def _on_pl_act(act: str) -> None:
                if act == "new_pl":
                    self.nav_pane.prompt_create_playlist(initial_track_id=target.track_id)
                    self.lyrics_stage._show_toast(f"已将「{target.title}」加入新歌单")
                elif act.startswith("pl:"):
                    pl_name = act[3:]
                    added = self.nav_pane.add_track_to_playlist(pl_name, target.track_id)
                    self.lyrics_stage._show_toast(
                        f"已添加到歌单「{pl_name}」" if added else f"「{target.title}」已在该歌单中"
                    )

            menu.actionTriggered.connect(_on_pl_act)
            menu.popup_animated(QCursor.pos())
        elif action == "remove_from_playlist":
            if self.nav_pane.remove_track_from_active_playlist(target.track_id):
                self.lyrics_stage._show_toast(f"已从歌单移除：{target.title}")
        elif action == "scrape":
            self._open_online_scraper_dialog(index)
        elif action == "import_lrc":
            self._on_import_lrc_file(index)
        elif action == "edit_tags":
            self._open_edit_metadata_dialog(index)
        elif action == "write_tags":
            self._write_tags_in_background(self.tracks[index], show_toast=True)
        elif action == "remove":
            was_playing = self.audio.is_playing
            is_removing_current = (index == self.current_track_index)

            removed_track = self.tracks.pop(index)
            for plist in self.nav_pane.custom_playlists.values():
                if removed_track.track_id in plist:
                    plist.remove(removed_track.track_id)

            if not self.tracks:
                self.current_track_index = 0
                self.nav_pane.rebuild_list(self.tracks, active_index=-1)
                self._clear_now_playing_ui()
                self.lyrics_stage._show_toast("已清空曲库列表")
            elif is_removing_current:
                if self.current_track_index >= len(self.tracks):
                    self.current_track_index = max(0, len(self.tracks) - 1)
                self.nav_pane.rebuild_list(self.tracks, active_index=self.current_track_index)
                self.select_track(self.current_track_index, auto_play=was_playing)
                self.lyrics_stage._show_toast("已移除当前曲目，自动播放下一首")
            else:
                if index < self.current_track_index:
                    self.current_track_index -= 1
                self.nav_pane.rebuild_list(self.tracks, active_index=self.current_track_index)
                self.lyrics_stage._show_toast("已从列表移除")
            self.schedule_save()

    def _propagate_backdrop_material(self) -> None:
        mat = getattr(self, "backdrop_material", "mica")
        for comp in (
            getattr(self, "title_bar", None),
            getattr(self, "nav_pane", None),
            getattr(self, "cover_card", None),
            getattr(self, "lyrics_stage", None),
            getattr(self, "transport_bar", None),
            getattr(self, "_active_eq_dialog", None),
            getattr(self, "desktop_lyric", None),
            getattr(self, "mini_player", None),
            getattr(self, "_tray_menu", None),
        ):
            if comp and hasattr(comp, "set_backdrop_material"):
                comp.set_backdrop_material(mat)

    def set_backdrop_material(self, mode: str) -> None:
        self.backdrop_material = mode
        self._apply_backdrop_to_window()
        self._propagate_backdrop_material()
        self._bg_dirty = True
        self.update()
        mat_labels = {
            "solid": "纯色",
            "mica": "云母 (Mica)",
            "acrylic": "亚克力 (Acrylic)",
        }
        if hasattr(self, "lyrics_stage") and hasattr(self.lyrics_stage, "_show_toast"):
            self.lyrics_stage._show_toast(f"窗口材质: {mat_labels.get(mode, mode)}")
        self.schedule_save()

    def _open_lyric_poster_dialog(self, line_index: int = -1) -> None:
        cur_track = self.tracks[self.current_track_index] if (0 <= self.current_track_index < len(self.tracks)) else None
        if not cur_track:
            return
        idx = line_index if line_index >= 0 else getattr(self.lyrics_stage, "active_index", 0)
        dlg = LyricPosterDialog(
            track=cur_track,
            lyrics=cur_track.lyrics or [],
            current_index=idx,
            theme=self.theme,
            parent=self,
        )
        dlg.exec()

    def _open_audio_settings_dialog(self, default_tab: int = 0) -> None:
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
            dolby_mode=self.audio.dolby_atmos_mode,
            spatial_width=self.audio.spatial_width,
            spatial_height=self.audio.spatial_height,
            audio_output_mode=getattr(self.audio, "output_channel_mode", "binaural"),
            actual_channels=getattr(self.audio, "output_channels_actual", 2),
            output_device_name=getattr(self.audio, "output_device_name", ""),
            selected_device_id=getattr(self.audio, "selected_device_id", "default"),
            available_devices=self.audio.get_available_output_devices(),
            crossfade_sec=getattr(self.audio, "crossfade_sec", 0.0),
            replay_gain_enabled=getattr(self.audio, "replay_gain_enabled", False),
            vocal_balance=getattr(self.audio, "vocal_balance", 0.0),
            turntable_physics=getattr(self.audio, "turntable_physics_enabled", True),
            vinyl_warmth=getattr(self.audio, "vinyl_warmth", 0.0),
            headphone_profile_id=getattr(self.audio, "headphone_profile_id", "bypass"),
            default_tab=default_tab,
            current_theme=self.theme.name,
            current_font_size=getattr(self.lyrics_stage, "lyric_font_size", 20),
            show_translation=getattr(self.lyrics_stage, "show_translation", True),
            syllable_mode=getattr(self.lyrics_stage, "syllable_mode", True),
            immersive_fps=getattr(self.immersive_window, "target_fps", 120),
            vinyl_mode=getattr(self.immersive_window.artwork_view, "vinyl_mode", "pure"),
            backdrop_material=getattr(self, "backdrop_material", "mica"),
            current_netease_quality=getattr(netease_manager, "preferred_quality", "lossless"),
            theme=self.theme,
            parent=self,
        )
        dlg.neteaseQualityChanged.connect(self._on_netease_quality_changed)
        dlg.dolbyAtmosModeChanged.connect(self._on_dolby_mode_changed)
        dlg.spatialParamsChanged.connect(self._on_spatial_params_changed)
        dlg.outputModeChanged.connect(self._on_output_mode_changed)
        dlg.outputDeviceChanged.connect(self._on_output_device_selected)
        dlg.crossfadeChanged.connect(self._on_crossfade_changed)
        dlg.replayGainToggled.connect(self._on_replay_gain_toggled)
        dlg.vocalBalanceChanged.connect(self._on_vocal_balance_changed)
        dlg.turntablePhysicsToggled.connect(self._on_turntable_physics_toggled)
        dlg.vinylWarmthChanged.connect(self._on_vinyl_warmth_changed)
        dlg.headphoneProfileChanged.connect(self._on_headphone_profile_changed)
        dlg.soundProfileChanged.connect(
            lambda prof: (
                self.audio.set_sound_profile(prof, trigger_preview=True),
                self.lyrics_stage._show_toast(f"音效预设: {prof}"),
                self.schedule_save(),
            )
        )
        dlg.customEqChanged.connect(
            lambda gains, rev, prev: (
                self.audio.set_custom_eq(gains, rev, trigger_preview=prev),
                self.schedule_save(),
            )
        )
        dlg.playbackRateChanged.connect(lambda r: (self.audio.set_playback_rate(r), self.schedule_save()))
        dlg.lyricOffsetDeltaRequested.connect(self.lyrics_stage.adjust_lyric_offset)
        dlg.sleepTimerRequested.connect(self.audio.set_sleep_timer)

        # 偏好与视效信号连接
        dlg.themeChanged.connect(self._on_theme_toggled)
        dlg.fontSizeDeltaRequested.connect(self.lyrics_stage.adjust_lyric_font_size)
        dlg.translationToggled.connect(self.lyrics_stage.set_show_translation)
        dlg.translationToggled.connect(self.immersive_window.set_show_translation)
        dlg.translationToggled.connect(self._on_translation_toggled_by_user)
        dlg.translationToggled.connect(lambda _b: self.schedule_save())
        dlg.syllableModeToggled.connect(self.lyrics_stage.set_syllable_mode)
        dlg.syllableModeToggled.connect(self.immersive_window.set_syllable_mode)
        dlg.syllableModeToggled.connect(lambda _b: self.schedule_save())
        dlg.immersiveFpsChanged.connect(self.immersive_window.set_target_fps)
        dlg.immersiveFpsChanged.connect(lambda fps: (self._saved_state.update({"immersive_fps": fps}), self.schedule_save()))
        dlg.vinylModeChanged.connect(self.immersive_window.artwork_view.set_vinyl_mode)
        dlg.vinylModeChanged.connect(lambda m: (self._saved_state.update({"vinyl_mode": m}), self.schedule_save()))
        dlg.vinylModeToggled.connect(self.immersive_window.artwork_view.set_vinyl_mode)
        dlg.backdropMaterialChanged.connect(self.set_backdrop_material)
        dlg.scrapeRequested.connect(self._open_online_scraper_dialog)
        self._active_eq_dialog = dlg
        dlg.exec()
        self._active_eq_dialog = None
        self.schedule_save()

    def _on_import_files(self) -> None:
        files, _ = QFileDialog.getOpenFileNames(
            self,
            "选择要导入的音乐或 CUE 分轨文件",
            "",
            "音乐与 CUE 索引 (*.mp3 *.flac *.wav *.m4a *.ogg *.aac *.wma *.cue);;CUE 分轨文件 (*.cue);;音频文件 (*.mp3 *.flac *.wav *.m4a *.ogg *.aac *.wma);;所有文件 (*.*)",
        )
        if files:
            self._append_audio_files(files, auto_play_first=True)

    def _on_import_folder(self) -> None:
        folder = QFileDialog.getExistingDirectory(self, "选择音乐文件夹")
        if folder:
            audio_files = scan_folder_for_audio(folder)
            if audio_files:
                self._append_audio_files(audio_files, auto_play_first=True)

    def _open_watched_folders_dialog(self) -> None:
        dlg = WatchedFoldersDialog(self.watched_folders, theme=self.theme, parent=self)

        def _on_changed(new_folders: list) -> None:
            self.watched_folders = [str(f) for f in new_folders if str(f).strip()]
            self._sync_fs_watcher_paths()
            self.schedule_save()

        dlg.watchedFoldersChanged.connect(_on_changed)
        dlg.rescanRequested.connect(self._scan_watched_folders_async)
        dlg.exec()
        if self.watched_folders:
            self._scan_watched_folders_async()

    def _sync_fs_watcher_paths(self) -> None:
        cur_dirs = self._fs_watcher.directories()
        if cur_dirs:
            self._fs_watcher.removePaths(cur_dirs)
        valid_dirs = [f for f in self.watched_folders if Path(f).is_dir()]
        if valid_dirs:
            self._fs_watcher.addPaths(valid_dirs)

    def _on_watched_dir_changed(self, _path: str) -> None:
        self._watch_scan_timer.start()

    def _scan_watched_folders_async(self) -> None:
        if not self.watched_folders:
            return
        if self._watch_worker is not None and self._watch_worker.isRunning():
            return
        self._watch_worker = FolderWatchScanWorker(self.watched_folders, self)
        self._watch_worker.scanFinished.connect(self._on_watch_scan_finished)
        self._watch_worker.start()

    def _on_watch_scan_finished(self, file_paths: list) -> None:
        if file_paths:
            added_cnt = self._append_audio_files(file_paths, auto_play_first=(len(self.tracks) == 0))
            if added_cnt > 0 and hasattr(self, "lyrics_stage"):
                self.lyrics_stage._show_toast(f"监控目录自动新增 {added_cnt} 首曲目")

    def _on_import_lrc_file(self, target_index: Optional[int] = None) -> None:
        if not self.tracks:
            return
        idx = self.current_track_index if (target_index is None or isinstance(target_index, bool)) else int(target_index)
        if not (0 <= idx < len(self.tracks)):
            idx = self.current_track_index
        target_track = self.tracks[idx]
        lrc_file, _ = QFileDialog.getOpenFileName(
            self,
            f"为「{target_track.title}」导入本地 LRC 歌词",
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
                        target_track.lyrics = parsed
                        if idx == self.current_track_index:
                            self.lyrics_stage.load_lyrics(parsed, lyric_offset=target_track.lyric_offset)
                            self.lyrics_stage.sync_time(self.audio.current_time, source="seek")
                        self.transport_bar.set_status_message(f"已加载歌词：{p.name}")
                        self.schedule_save()
                    break
            except Exception:
                continue

    def _append_audio_files(self, file_paths: List[str], auto_play_first: bool = True) -> int:
        existing_ids = {t.track_id for t in self.local_tracks}
        existing_whole_files = {
            t.file_path for t in self.local_tracks if t.file_path and not t.cue_path
        }
        existing_cues = {t.cue_path for t in self.local_tracks if t.cue_path}
        cue_covered_files = {t.file_path for t in self.local_tracks if t.file_path and t.cue_path}
        first_new_index: Optional[int] = None
        added_count = 0
        newly_handled_tracks: List[NordicTrack] = []

        # 先处理所有的 .cue 分轨索引文件，再处理普通单曲文件
        sorted_inputs = sorted(file_paths, key=lambda x: (0 if Path(x).suffix.lower() == ".cue" else 1, x))

        for fp in sorted_inputs:
            p = Path(fp)
            suf = p.suffix.lower()
            if suf not in SUPPORTED_IMPORT_EXTS:
                continue
            norm_p = str(p.resolve())

            if suf == ".cue":
                if norm_p in existing_cues:
                    matching_cues = [t for t in self.local_tracks if t.cue_path == norm_p]
                    newly_handled_tracks.extend(matching_cues)
                    continue
                cue_tracks = parse_cue_file(norm_p, base_index_hint=len(self.local_tracks))
                if not cue_tracks:
                    continue
                # 如果此前已经把整轨大文件当单曲导入过，自动替换为拆好分轨的 CUE 曲目列表
                ref_audio_paths = {ct.file_path for ct in cue_tracks if ct.file_path}
                for ref_ap in ref_audio_paths:
                    cue_covered_files.add(ref_ap)
                self.local_tracks[:] = [
                    t for t in self.local_tracks
                    if not (t.file_path in ref_audio_paths and not t.cue_path)
                ]
                for ct in cue_tracks:
                    if ct.track_id in existing_ids:
                        matching = [t for t in self.local_tracks if t.track_id == ct.track_id]
                        if matching:
                            newly_handled_tracks.append(matching[0])
                        continue
                    if first_new_index is None:
                        first_new_index = len(self.local_tracks)
                    self.local_tracks.append(ct)
                    existing_ids.add(ct.track_id)
                    newly_handled_tracks.append(ct)
                    added_count += 1
                    self._trigger_auto_scrape_if_needed(ct)
                existing_cues.add(norm_p)
            else:
                if norm_p in existing_whole_files or norm_p in cue_covered_files:
                    matching = [t for t in self.local_tracks if t.file_path == norm_p]
                    if matching:
                        newly_handled_tracks.append(matching[0])
                    continue
                new_track = create_track_from_file(norm_p, index_hint=len(self.local_tracks))
                if new_track.track_id in existing_ids:
                    matching = [t for t in self.local_tracks if t.track_id == new_track.track_id]
                    if matching:
                        newly_handled_tracks.append(matching[0])
                    continue
                if first_new_index is None:
                    first_new_index = len(self.local_tracks)
                self.local_tracks.append(new_track)
                existing_whole_files.add(norm_p)
                existing_ids.add(new_track.track_id)
                newly_handled_tracks.append(new_track)
                added_count += 1
                self._trigger_auto_scrape_if_needed(new_track)

        # 若当前处于自定义歌单视图，将所有本次导入/拖拽的歌曲（无论新导入还是已存在于本地曲库）自动注入当前歌单
        pl_added = 0
        if self.nav_pane.view_mode == 2 and self.nav_pane.active_playlist_name:
            cur_pl = self.nav_pane.active_playlist_name
            if cur_pl not in self.nav_pane.custom_playlists:
                self.nav_pane.custom_playlists[cur_pl] = []
            target_pl_list = self.nav_pane.custom_playlists[cur_pl]
            for trk in newly_handled_tracks:
                if trk.track_id not in target_pl_list:
                    target_pl_list.append(trk.track_id)
                    pl_added += 1
            if pl_added > 0:
                self.nav_pane.playlistsChanged.emit()
                self.lyrics_stage._show_toast(f"已将 {pl_added} 首歌曲加入歌单「{cur_pl}」")

        if first_new_index is not None or pl_added > 0:
            self.nav_pane.rebuild_list(self.local_tracks, active_index=first_new_index if auto_play_first else self.current_track_index)
            if auto_play_first and first_new_index is not None:
                self.tracks = self.local_tracks
                self.select_track(first_new_index, auto_play=True)
            self._save_current_state()
        return added_count

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
                if suf in SUPPORTED_IMPORT_EXTS:
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
                            self.schedule_save()
                        break
                except Exception:
                    continue
        event.acceptProposedAction()

    def _trigger_auto_scrape_if_needed(self, track: NordicTrack) -> None:
        has_real_lyrics = len(track.lyrics) > 2
        has_cover = bool(track.cover_bytes or track.cover_path)
        if has_real_lyrics and has_cover:
            return

        worker = AutoScrapeWorker(
            track,
            need_lyrics=(not has_real_lyrics),
            need_cover=(not has_cover),
            parent=None,
        )
        worker.scrapeCompleted.connect(self._on_auto_scrape_completed)
        worker.finished.connect(lambda w=worker: self._active_workers.remove(w) if w in self._active_workers else None)
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
                    t.invalidate_cover_cache()
                if album_name and not t.album:
                    t.album = album_name

                self.nav_pane.refresh_item(idx)
                if idx == self.current_track_index:
                    self.cover_card.update_track(t)
                    self.transport_bar.set_track_info(t)
                    if lyrics:
                        self.lyrics_stage.load_lyrics(t.lyrics, lyric_offset=t.lyric_offset)
                        self.lyrics_stage.sync_time(self.audio.current_time, source="seek")
                self.schedule_save()
                break

    def _open_online_scraper_dialog(self, target_index: Optional[int] = None) -> None:
        if not self.tracks:
            return
        idx = self.current_track_index if (target_index is None or isinstance(target_index, bool)) else int(target_index)
        if not (0 <= idx < len(self.tracks)):
            idx = self.current_track_index
        target_track = self.tracks[idx]
        dlg = OnlineScraperDialog(target_track, theme=self.theme, parent=self)
        dlg.matchApplied.connect(
            lambda lyrics, cover_bytes, album_name, i=idx: self._apply_scraped_to_track(
                i, lyrics, cover_bytes, album_name
            )
        )
        dlg.exec()

    def _apply_scraped_to_track(
        self,
        index: int,
        lyrics: List[LyricLine],
        cover_bytes: Optional[bytes],
        album_name: str,
    ) -> None:
        if not (0 <= index < len(self.tracks)):
            return
        target_track = self.tracks[index]
        if lyrics:
            target_track.lyrics = lyrics
            if index == self.current_track_index:
                self.lyrics_stage.load_lyrics(lyrics, lyric_offset=target_track.lyric_offset)
                self.lyrics_stage.sync_time(self.audio.current_time, source="seek")
        if cover_bytes:
            target_track.cover_bytes = cover_bytes
            target_track.invalidate_cover_cache()
        if album_name:
            target_track.album = album_name

        if index == self.current_track_index:
            self.cover_card.update_track(target_track)
            self.transport_bar.set_track_info(target_track)
        self.nav_pane.refresh_item(index)
        if target_track.file_path:
            self._write_tags_in_background(target_track, show_toast=False)
            self.transport_bar.set_status_message("已更新歌词与封面并写入音频文件")
        else:
            self.transport_bar.set_status_message("已更新在线歌词与封面")
        self.schedule_save()

    def _clear_now_playing_ui(self) -> None:
        self.audio.pause()
        self.audio._stop_decoder_safely()
        self.audio._decoded_raw_bytes.clear()
        self.audio.current_track = None
        self.audio.current_time = 0.0
        self.audio.duration = 0.0
        self.cover_card.clear_track()
        self.lyrics_stage.load_lyrics([], lyric_offset=0.0)
        self.transport_bar.clear_track_info()
        self.transport_bar.set_playing_state(False)
        self.nav_pane.set_playing_state(False)
        if hasattr(self, "tray_icon"):
            self.tray_icon.setToolTip("Klang — Nordic Minimalist Audio Player")

    def _on_nav_track_selected(self, idx: int) -> None:
        if self.nav_pane.view_mode == 2:
            local_ids = {x.track_id for x in self.local_tracks}
            all_candidates = list(self.local_tracks) + [t for t in self.nav_pane.netease_tracks if t.track_id not in local_ids]
            if 0 <= idx < len(all_candidates):
                target = all_candidates[idx]
                if target in self.local_tracks:
                    self.tracks = self.local_tracks
                    real_idx = self.local_tracks.index(target)
                    self._last_local_track_index = real_idx
                    self.select_track(real_idx, auto_play=True)
                else:
                    self.tracks = [target]
                    self.select_track(0, auto_play=True)
                return
        self.tracks = self.local_tracks
        self._last_local_track_index = idx
        self.select_track(idx, auto_play=True)

    def _on_netease_track_selected(self, track: NordicTrack, playlist_tracks: List[NordicTrack], index: int) -> None:
        self.tracks = list(playlist_tracks)
        self.current_track_index = index
        self.select_track(index, auto_play=True)

    def select_track(self, index: int, auto_play: bool = False, is_auto_transition: bool = False) -> None:
        if not self.tracks:
            self._clear_now_playing_ui()
            return
        self._next_preloaded_index = None
        self.current_track_index = index % len(self.tracks)
        track = self.tracks[self.current_track_index]
        if track.track_id in self._flow_history:
            self._flow_history.remove(track.track_id)
        self._flow_history.append(track.track_id)
        max_hist = max(2, min(12, len(self.tracks) - 1))
        if len(self._flow_history) > max_hist:
            self._flow_history = self._flow_history[-max_hist:]
        self.nav_pane.set_active_index(self.current_track_index, track_id=track.track_id)

        self._play_track_with_netease_support(track, auto_play=auto_play, is_auto_transition=is_auto_transition)
        self.schedule_save()

    def _play_track_with_netease_support(
        self,
        track: NordicTrack,
        auto_play: bool = True,
        is_auto_transition: bool = False
    ) -> None:
        if not track.track_id.startswith("netease_"):
            self.audio.load_track(track, auto_play=auto_play, is_auto_transition=is_auto_transition)
            return

        from qt6_player.services.netease_service import netease_manager
        has_matching_cache = bool(
            track.file_path
            and Path(track.file_path).is_file()
            and Path(track.file_path).stat().st_size > 10240
        )

        if has_matching_cache:
            self.audio.load_track(track, auto_play=auto_play, is_auto_transition=is_auto_transition)
            if not track.lyrics and not getattr(track, "_lyrics_checked", False):
                track._lyrics_checked = True
                def _lyrics_worker():
                    try:
                        lyr = netease_manager.fetch_track_lyrics(track)
                        if lyr:
                            track.lyrics = lyr
                            def _on_lyr():
                                if self.audio.current_track and self.audio.current_track.track_id == track.track_id:
                                    self.lyrics_stage.load_lyrics(track.lyrics, lyric_offset=track.lyric_offset)
                                    if hasattr(self, "immersive_window") and self.immersive_window.isVisible():
                                        self.immersive_window.update_track(track)
                            self._async_callback_signal.emit(_on_lyr)
                    except Exception:
                        pass
                threading.Thread(target=_lyrics_worker, daemon=True).start()
            return

        # 后台异步换取网易云真实直链并下载缓存
        self._netease_load_seq += 1
        req_seq = self._netease_load_seq

        # 立即暂停或渐隐当前正在播放的曲目，避免旧曲目与等待下载发生重叠
        if self.audio.is_playing:
            self.audio.pause()

        self.transport_bar.set_status_message(f"正在从云端加载: {track.title}…")
        self.transport_bar.set_track_info(track)
        self.transport_bar.update_progress(0.0, max(1.0, track.duration))
        self.cover_card.update_track(track)

        def _worker():
            try:
                from qt6_player.services.netease_service import netease_manager
                resolved_path = netease_manager.resolve_track_playback_path(track)
                lyrics = netease_manager.fetch_track_lyrics(track)
                track._lyrics_checked = True
            except Exception:
                resolved_path = None
                lyrics = []

            def _on_resolved():
                if req_seq != self._netease_load_seq:
                    return
                if resolved_path and Path(resolved_path).is_file():
                    track.file_path = resolved_path
                    if lyrics:
                        track.lyrics = lyrics
                    if track in self.tracks:
                        self.current_track_index = self.tracks.index(track)
                    self.audio.load_track(track, auto_play=auto_play, is_auto_transition=is_auto_transition)
                    self.transport_bar.set_track_info(track)
                    self.cover_card.update_track(track)
                    self.lyrics_stage.load_lyrics(track.lyrics, lyric_offset=track.lyric_offset)
                    if hasattr(self, "immersive_window"):
                        self.immersive_window.update_track(track)
                    self.transport_bar.set_status_message("")
                    self.nav_pane.set_active_index(self.current_track_index, track_id=track.track_id)
                else:
                    self.transport_bar.set_status_message(f"歌曲加载失败，请检查网络或VIP状态: {track.title}")
                    if hasattr(self, "lyrics_stage") and hasattr(self.lyrics_stage, "_show_toast"):
                        self.lyrics_stage._show_toast(f"无法加载音频：{track.title}")

            self._async_callback_signal.emit(_on_resolved)

        threading.Thread(target=_worker, daemon=True).start()

    def _pick_smart_flow_next_index(self) -> int:
        """离线声学 DNA 情绪漫游算法：挑选 BPM、能量密度、频谱明暗度与冷暖色调最接近的下一首曲目"""
        n = len(self.tracks)
        if n <= 1:
            return 0
        cur_track = self.tracks[self.current_track_index]
        v_cur = cur_track.get_acoustic_vector()

        recent_set = set(self._flow_history[-max(1, min(6, n - 1)):])
        scored: List[Tuple[float, int]] = []
        for idx, cand in enumerate(self.tracks):
            if idx == self.current_track_index:
                continue
            v_cand = cand.get_acoustic_vector()
            # 4D 加权声学距离：能量密度 (1.35) + BPM 节拍 (1.15) + 频谱明暗 (1.0) + 温暖度 (0.9)
            # 同时支持半速/倍速谐波节拍亲和度 (如 70 BPM 与 140 BPM 具备节奏律动相容性)
            bpm_diff = abs(v_cur[0] - v_cand[0])
            bpm_cur_raw = 60.0 + v_cur[0] * 120.0
            bpm_cand_raw = 60.0 + v_cand[0] * 120.0
            harmonic_bpm_diff = min(
                bpm_diff,
                abs(bpm_cur_raw * 2.0 - bpm_cand_raw) / 120.0,
                abs(bpm_cur_raw * 0.5 - bpm_cand_raw) / 120.0,
            )
            dist_sq = (
                1.15 * (harmonic_bpm_diff ** 2)
                + 1.35 * ((v_cur[1] - v_cand[1]) ** 2)
                + 1.00 * ((v_cur[2] - v_cand[2]) ** 2)
                + 0.90 * ((v_cur[3] - v_cand[3]) ** 2)
            )
            dist = dist_sq ** 0.5
            # 对近期已播放过的曲目施加防重复惩罚，确保漫游不死循环
            if cand.track_id in recent_set:
                rec_pos = self._flow_history.index(cand.track_id) if cand.track_id in self._flow_history else 0
                rec_weight = (rec_pos + 1) / max(1, len(self._flow_history))
                dist += 0.65 * rec_weight
            scored.append((dist, idx))

        scored.sort(key=lambda x: x[0])
        # 在声学最相近的前 2 首候选曲目中优中选优，兼顾情绪连贯性与漫游新鲜感
        if len(scored) >= 2 and (scored[1][0] - scored[0][0]) < 0.08:
            return random.choice([scored[0][1], scored[1][1]])
        return scored[0][1]

    def _compute_next_track_index(self) -> Optional[int]:
        if not self.tracks:
            return None
        if self.play_mode == "loop_one":
            return self.current_track_index
        if self.play_mode == "sequential":
            if self.current_track_index + 1 < len(self.tracks):
                return self.current_track_index + 1
            return None
        if self.play_mode == "smart_flow" and len(self.tracks) > 1:
            return self._pick_smart_flow_next_index()
        if self.play_mode == "shuffle" and len(self.tracks) > 1:
            choices = [i for i in range(len(self.tracks)) if i != self.current_track_index]
            return random.choice(choices)
        return (self.current_track_index + 1) % len(self.tracks)

    def _on_request_next_track_preload(self) -> None:
        """在后台预解码下一首曲目到内存缓冲，实现 0ms 无缝切歌与平滑 Crossfade"""
        if not self.tracks:
            return
        next_idx = self._compute_next_track_index()
        self._next_preloaded_index = next_idx
        if next_idx is not None and 0 <= next_idx < len(self.tracks):
            nxt_track = self.tracks[next_idx]
            if nxt_track.track_id.startswith("netease_") and (not nxt_track.file_path or not Path(nxt_track.file_path).is_file()):
                def _preload_worker():
                    try:
                        from qt6_player.services.netease_service import netease_manager
                        res_p = netease_manager.resolve_track_playback_path(nxt_track)
                        netease_manager.fetch_track_lyrics(nxt_track)
                        if res_p and Path(res_p).is_file():
                            QTimer.singleShot(0, lambda: self.audio.preload_next_track(nxt_track))
                    except Exception:
                        pass
                threading.Thread(target=_preload_worker, daemon=True).start()
            else:
                self.audio.preload_next_track(nxt_track)

    def _on_prev_track(self) -> None:
        if not self.tracks:
            return
        if self.play_mode == "shuffle" and len(self.tracks) > 1:
            choices = [i for i in range(len(self.tracks)) if i != self.current_track_index]
            next_idx = random.choice(choices)
        else:
            next_idx = (self.current_track_index - 1) % len(self.tracks)
        self.select_track(next_idx, auto_play=True, is_auto_transition=False)

    def _on_next_track(self, is_auto_transition: bool = False) -> None:
        if not self.tracks:
            return
        pre_idx = getattr(self, "_next_preloaded_index", None)
        if is_auto_transition and pre_idx is not None and 0 <= pre_idx < len(self.tracks):
            next_idx = pre_idx
        elif self.play_mode == "smart_flow" and len(self.tracks) > 1:
            next_idx = self._pick_smart_flow_next_index()
        elif self.play_mode == "shuffle" and len(self.tracks) > 1:
            choices = [i for i in range(len(self.tracks)) if i != self.current_track_index]
            next_idx = random.choice(choices)
        else:
            next_idx = (self.current_track_index + 1) % len(self.tracks)
        self.select_track(next_idx, auto_play=True, is_auto_transition=is_auto_transition)
        if self.play_mode == "smart_flow" and 0 <= next_idx < len(self.tracks) and hasattr(self.lyrics_stage, "_show_toast"):
            nxt = self.tracks[next_idx]
            badge = nxt.format_dna_badge()
            if badge:
                self.lyrics_stage._show_toast(f"情绪漫游匹配：{nxt.title} ({badge})")

    def _on_track_naturally_ended(self) -> None:
        if not self.tracks:
            return
        if self.play_mode == "loop_one":
            self.select_track(self.current_track_index, auto_play=True, is_auto_transition=True)
        elif self.play_mode == "sequential":
            if self.current_track_index + 1 < len(self.tracks):
                self.select_track(self.current_track_index + 1, auto_play=True, is_auto_transition=True)
        else:
            self._on_next_track(is_auto_transition=True)

    def _on_track_changed(self, track: NordicTrack) -> None:
        self.cover_card.update_track(track)
        self.lyrics_stage.load_lyrics(track.lyrics, lyric_offset=track.lyric_offset)
        self.transport_bar.set_track_info(track)
        self.transport_bar.update_progress(0.0, track.duration)
        self.mini_player.set_track(track)
        self.mini_player.update_progress(0.0, track.duration)
        if hasattr(self, "immersive_window"):
            self.immersive_window.update_track(track)
        if hasattr(self, "tray_icon"):
            self.tray_icon.setToolTip(f"Klang — {track.title} · {track.artist}")
        if hasattr(self, "media_control") and self.media_control:
            self.media_control.update_track(track)
        if track.lyrics:
            self.desktop_lyric.update_lyric(track.lyrics[0])
            self.mini_player.update_lyric(track.lyrics[0])
            if not any(l.sub for l in track.lyrics):
                import re as _re
                has_foreign = any(_re.search(r"[a-zA-Z]", l.text) for l in track.lyrics if not l.is_interlude)
                if has_foreign:
                    self._fetch_translation_for_current_track(track, silent=True)

    def _on_play_state_changed(self, is_playing: bool) -> None:
        self.cover_card.set_playing(is_playing)
        self.transport_bar.set_playing_state(is_playing)
        self.nav_pane.set_playing_state(is_playing)
        self.mini_player.set_playing(is_playing)
        if hasattr(self, "immersive_window"):
            self.immersive_window.update_play_state(is_playing)
        if hasattr(self, "media_control") and self.media_control:
            self.media_control.set_playback_status(is_playing)

    def _on_time_updated(self, current_time: float, duration: float, source: str) -> None:
        if self.isVisible():
            self.transport_bar.update_progress(current_time, duration)
            self.lyrics_stage.sync_time(current_time, source=source)
        elif source != "tick":
            self.lyrics_stage.sync_time(current_time, source=source)
        if self.mini_player.isVisible():
            self.mini_player.update_progress(current_time, duration)
        if hasattr(self, "immersive_window") and self.immersive_window.isVisible():
            self.immersive_window.update_time(current_time, duration)
        if source == "init" and 0 <= self.current_track_index < len(self.nav_pane.item_buttons):
            self.nav_pane.item_buttons[self.current_track_index].update()
            self.schedule_save()

    def _on_theme_toggled(self, theme_name: str) -> None:
        new_theme = DUSK_THEME if theme_name == "dusk" else DAYLIGHT_THEME
        self._bg_col_start = QColor(self._bg_col_cur)
        self._bg_col_target = QColor(new_theme.mist_white)
        self.theme = new_theme
        self._bg_dirty = True
        self._theme_anim.stop()
        self._theme_anim.setStartValue(0.0)
        self._theme_anim.setEndValue(1.0)
        self._theme_anim.start()

        self.title_bar.set_theme(self.theme)
        self.nav_pane.set_theme(self.theme)
        self.cover_card.set_theme(self.theme)
        self.lyrics_stage.set_theme(self.theme)
        self.transport_bar.set_theme(self.theme)
        self.desktop_lyric.set_theme(self.theme)
        self.mini_player.set_theme(self.theme)
        self.frame_overlay.set_theme(self.theme)
        if self._active_eq_dialog:
            self._active_eq_dialog.set_theme(self.theme)
        if hasattr(self, "_tray_menu") and self._tray_menu:
            self._tray_menu.set_theme(self.theme)
        self._apply_backdrop_to_window()
        self._propagate_backdrop_material()
        self.update()
        self.schedule_save()

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
        key = event.key()
        mods = event.modifiers()

        # 系统多媒体键支持
        if key in (Qt.Key.Key_MediaTogglePlayPause, Qt.Key.Key_MediaPlay, Qt.Key.Key_MediaPause):
            self.audio.toggle_play()
            event.accept()
            return
        if key == Qt.Key.Key_MediaPrevious:
            self._on_prev_track()
            event.accept()
            return
        if key == Qt.Key.Key_MediaNext:
            self._on_next_track()
            event.accept()
            return
        if key == Qt.Key.Key_MediaStop:
            self.audio.pause()
            event.accept()
            return

        if key == Qt.Key.Key_F11:
            self._toggle_immersive_mode()
            event.accept()
            return

        if key == Qt.Key.Key_Space:
            self.audio.toggle_play()
            event.accept()
            return
        if key == Qt.Key.Key_Slash and not (mods & Qt.KeyboardModifier.ControlModifier):
            if not getattr(self, "_sidebar_expanded", True):
                self._toggle_sidebar()
            self.nav_pane.search_box.edit.setFocus()
            event.accept()
            return

        if mods & Qt.KeyboardModifier.ControlModifier:
            if key in (Qt.Key.Key_Plus, Qt.Key.Key_Equal):
                self.lyrics_stage.adjust_lyric_font_size(2)
                event.accept()
                return
            if key == Qt.Key.Key_Minus:
                self.lyrics_stage.adjust_lyric_font_size(-2)
                event.accept()
                return
            if key == Qt.Key.Key_Left:
                self._on_prev_track()
                event.accept()
                return
            if key == Qt.Key.Key_Right:
                self._on_next_track()
                event.accept()
                return
            if key == Qt.Key.Key_F:
                if not getattr(self, "_sidebar_expanded", True):
                    self._toggle_sidebar()
                self.nav_pane.search_box.edit.setFocus()
                event.accept()
                return
            if key == Qt.Key.Key_M:
                self._toggle_mini_player_mode()
                event.accept()
                return
            if key == Qt.Key.Key_D:
                self.title_bar._on_toggle_desktop_lyric()
                event.accept()
                return
        else:
            if key == Qt.Key.Key_Left:
                self.audio.seek(max(0.0, self.audio.current_time - 5.0), auto_play_on_seek=False, source="seek")
                event.accept()
                return
            if key == Qt.Key.Key_Right:
                self.audio.seek(min(self.audio.duration, self.audio.current_time + 5.0), auto_play_on_seek=False, source="seek")
                event.accept()
                return
            if key == Qt.Key.Key_Up:
                self.audio.set_volume(min(1.0, self.audio.volume + 0.05))
                self.lyrics_stage._show_toast(f"音量: {int(round(self.audio.volume * 100))}%")
                event.accept()
                return
            if key == Qt.Key.Key_Down:
                self.audio.set_volume(max(0.0, self.audio.volume - 0.05))
                self.lyrics_stage._show_toast(f"音量: {int(round(self.audio.volume * 100))}%")
                event.accept()
                return
        super().keyPressEvent(event)

    def _rebuild_bg_cache(self) -> None:
        dpr = max(1.0, self.devicePixelRatioF())
        w, h = max(1, self.width()), max(1, self.height())
        pix = QPixmap(int(w * dpr), int(h * dpr))
        pix.setDevicePixelRatio(dpr)
        pix.fill(Qt.GlobalColor.transparent)

        bp = QPainter(pix)
        bp.setRenderHint(QPainter.RenderHint.Antialiasing)

        rect = QRectF(0.0, 0.0, float(w), float(h))

        fill_col = QColor(self._bg_col_cur)
        mat = getattr(self, "backdrop_material", "mica")
        if mat == "mica":
            # 云母材质：遵循 PyQt-Fluent-Widgets (FluentWidget) 合成机制，以极低薄纱底色透出原生 DWM 云母壁纸采样
            fill_col.setAlpha(14 if self.theme.name == "daylight" else 20)
            bp.fillRect(rect, fill_col)
        elif mat == "acrylic":
            # 亚克力材质：setAcrylicEffect 已在 DWM 硬件模糊层混入北欧底色，此处保持透明避免双重叠加遮挡模糊
            pass
        else:
            # 标准纯色模式：完全不透明 (255)，完整还原北欧燕麦白/暮色黑原色
            fill_col.setAlpha(255)
            bp.fillRect(rect, fill_col)

        # 封面自适应双核流光背景 (Ambient Mesh Glow，缓存在位图中避免每帧全屏径向渐变计算)
        base_alpha = 24 if self.theme.name == "daylight" else 32
        if mat == "acrylic":
            base_alpha = 12 if self.theme.name == "daylight" else 16
        elif mat == "mica":
            base_alpha = 14 if self.theme.name == "daylight" else 18
        c1 = QColor(self._amb1_cur)
        c1.setAlpha(base_alpha)
        orb1 = QRadialGradient(QPointF(w * 0.28, h * 0.40), 500.0)
        orb1.setColorAt(0.0, c1)
        orb1.setColorAt(1.0, Qt.GlobalColor.transparent)
        bp.fillRect(rect, orb1)

        c2 = QColor(self._amb2_cur)
        c2.setAlpha(int(round(base_alpha * 0.78)))
        orb2 = QRadialGradient(QPointF(w * 0.72, h * 0.62), 440.0)
        orb2.setColorAt(0.0, c2)
        orb2.setColorAt(1.0, Qt.GlobalColor.transparent)
        bp.fillRect(rect, orb2)
        bp.end()

        self._cached_bg_pixmap = pix
        self._bg_dirty = False

    def paintEvent(self, event) -> None:
        super().paintEvent(event)
        dpr = max(1.0, self.devicePixelRatioF())
        if (
            self._bg_dirty
            or self._cached_bg_pixmap is None
            or self._cached_bg_pixmap.width() != int(self.width() * dpr)
            or self._cached_bg_pixmap.height() != int(self.height() * dpr)
        ):
            self._rebuild_bg_cache()

        if self._cached_bg_pixmap is not None:
            painter = QPainter(self)
            painter.setClipRect(event.rect())
            painter.drawPixmap(0, 0, self._cached_bg_pixmap)


    def _on_translation_toggled_by_user(self, show: bool) -> None:
        if not show:
            return
        cur_track = self.tracks[self.current_track_index] if (0 <= self.current_track_index < len(self.tracks)) else None
        if cur_track and cur_track.lyrics and not any(l.sub for l in cur_track.lyrics):
            self._fetch_translation_for_current_track(cur_track, silent=False)

    def _fetch_translation_for_current_track(self, track: NordicTrack, silent: bool = False) -> None:
        if not track or not track.lyrics:
            return
        if any(l.sub for l in track.lyrics):
            return
        if getattr(self, "_active_trans_track_id", None) == track.track_id:
            return
        self._active_trans_track_id = track.track_id

        if not silent and hasattr(self.lyrics_stage, "_show_toast"):
            self.lyrics_stage._show_toast("正在查找歌词翻译…")

        worker = AutoTranslationWorker(track, self)
        self._trans_worker = worker
        worker.translationReady.connect(lambda tid, tlyric: self._on_translation_fetched(tid, tlyric, silent))
        worker.start()

    def _on_translation_fetched(self, track_id: str, tlyric: str, silent: bool) -> None:
        self._active_trans_track_id = None
        if not tlyric.strip():
            if not silent and hasattr(self.lyrics_stage, "_show_toast"):
                self.lyrics_stage._show_toast("未找到匹配的中文译文")
            return

        target_track = None
        for t in self.tracks:
            if t.track_id == track_id:
                target_track = t
                break
        if not target_track or not target_track.lyrics:
            return

        from qt6_player.data.tracks import merge_lyrics_translations
        merged = merge_lyrics_translations(target_track.lyrics, tlyric)
        if merged:
            if target_track.file_path:
                try:
                    p = Path(target_track.file_path)
                    if p.parent.is_dir():
                        (p.parent / f"{p.stem}.tr.lrc").write_text(tlyric, encoding="utf-8")
                except Exception:
                    pass

            cur_track = self.tracks[self.current_track_index] if (0 <= self.current_track_index < len(self.tracks)) else None
            if cur_track and cur_track.track_id == track_id:
                self.lyrics_stage.load_lyrics(cur_track.lyrics, lyric_offset=cur_track.lyric_offset)
                if hasattr(self, "immersive_window") and self.immersive_window.isVisible():
                    self.immersive_window.lyrics_view.set_lyrics(cur_track.lyrics, offset=cur_track.lyric_offset)
                if hasattr(self, "desktop_lyric") and self.desktop_lyric.isVisible():
                    if cur_track.lyrics:
                        self.desktop_lyric.update_lyric(cur_track.lyrics[0])

            self.schedule_save()
            if hasattr(self.lyrics_stage, "_show_toast"):
                self.lyrics_stage._show_toast("已加载中文翻译")

    def _update_dolby_badges(self) -> None:
        is_active = self.audio.is_dolby_atmos_active
        ch = getattr(self.audio, "output_channels_actual", 2)
        if hasattr(self, "cover_card") and self.cover_card:
            self.cover_card.set_dolby_atmos_active(is_active, ch)
        if hasattr(self, "immersive_window") and self.immersive_window:
            self.immersive_window.set_dolby_atmos_active(is_active, ch)

    def _on_dolby_mode_changed(self, mode: str) -> None:
        self.audio.set_dolby_atmos_mode(mode)
        self._update_dolby_badges()
        mode_names = {"off": "已关闭", "spatial_stereo": "空间立体声", "dolby_atmos": "杜比全景声"}
        if hasattr(self.lyrics_stage, "_show_toast"):
            self.lyrics_stage._show_toast(f"空间声场: {mode_names.get(mode, mode)}")
        self.schedule_save()

    def _on_output_mode_changed(self, mode: str) -> None:
        self.audio.set_output_channel_mode(mode)
        self._update_dolby_badges()
        if self._active_eq_dialog is not None:
            self._active_eq_dialog._update_output_tip(
                self.audio.output_channel_mode,
                self.audio.output_channels_actual,
                self.audio.output_device_name,
            )
        mode_names = {
            "binaural": "双耳立体声",
            "surround_51": "5.1 环绕声",
            "surround_71": "7.1 环绕声",
        }
        msg = mode_names.get(mode, mode)
        if self.audio.output_channels_actual > 2:
            msg += f" ({self.audio.output_channels_actual} 声道)"
        elif mode != "binaural":
            msg += " (双耳虚拟环绕)"
        if hasattr(self.lyrics_stage, "_show_toast"):
            self.lyrics_stage._show_toast(msg)
        self.schedule_save()

    def _on_spatial_params_changed(self, width: float, height: float) -> None:
        self.audio.set_spatial_parameters(width, height)
        self.schedule_save()

    def _on_output_devices_updated(self, available_devices: list, selected_device_id: str) -> None:
        self._update_dolby_badges()
        if self._active_eq_dialog is not None and self._active_eq_dialog.isVisible():
            self._active_eq_dialog.update_output_devices(
                available_devices,
                selected_device_id,
                self.audio.output_device_name,
            )

    def _on_output_device_selected(self, dev_id: str) -> None:
        self.audio.set_output_device(dev_id)
        self._update_dolby_badges()
        if hasattr(self.lyrics_stage, "_show_toast"):
            self.lyrics_stage._show_toast(f"输出设备: {self.audio.output_device_name}")
        self.schedule_save()

    def _on_netease_quality_changed(self, quality: str) -> None:
        from qt6_player.services.netease_service import netease_manager
        netease_manager.set_preferred_quality(quality)
        lbl = netease_manager.QUALITY_LABELS.get(quality, quality)
        if hasattr(self.lyrics_stage, "_show_toast"):
            self.lyrics_stage._show_toast(f"网易云播放音质已设为：{lbl}")
        if hasattr(self, "nav_pane"):
            self.nav_pane._update_sub_bar_ui()
        self.schedule_save()

    def _on_crossfade_changed(self, sec: float) -> None:
        self.audio.set_crossfade_sec(sec)
        if hasattr(self.lyrics_stage, "_show_toast"):
            if sec <= 0.05:
                self.lyrics_stage._show_toast("切歌过渡: 无缝播放")
            else:
                self.lyrics_stage._show_toast(f"切歌过渡: {sec:g} 秒淡入淡出")
        self.schedule_save()

    def _on_replay_gain_toggled(self, enabled: bool) -> None:
        self.audio.set_replay_gain_enabled(enabled)
        if hasattr(self.lyrics_stage, "_show_toast"):
            self.lyrics_stage._show_toast(
                "已开启音量均衡" if enabled else "已关闭音量均衡"
            )
        self.schedule_save()

    def _on_vocal_balance_changed(self, val: float) -> None:
        self.audio.set_vocal_balance(val)
        self.schedule_save()

    def _on_turntable_physics_toggled(self, enabled: bool) -> None:
        self.audio.set_turntable_physics_enabled(enabled)
        if hasattr(self.lyrics_stage, "_show_toast"):
            self.lyrics_stage._show_toast(
                "已开启黑胶物理断电起停" if enabled else "已关闭黑胶物理起停"
            )
        self.schedule_save()

    def _on_vinyl_warmth_changed(self, val: float) -> None:
        self.audio.set_vinyl_warmth(val)
        self.schedule_save()

    def _on_headphone_profile_changed(self, profile_id: str) -> None:
        self.audio.set_headphone_profile(profile_id, trigger_preview=True)
        prof = HEADPHONE_PROFILES.get(profile_id)
        name = prof.name if prof else "原声监听"
        if hasattr(self.lyrics_stage, "_show_toast"):
            if profile_id == "bypass":
                self.lyrics_stage._show_toast("已关闭耳机硬件校准")
            else:
                self.lyrics_stage._show_toast(f"已加载耳机校准：{name}")
        self.schedule_save()

    def _on_acoustic_dna_ready(self, _track: NordicTrack) -> None:
        if self.play_mode == "smart_flow":
            self._next_preloaded_index = None
            QTimer.singleShot(20, self._on_request_next_track_preload)
        self.schedule_save()
