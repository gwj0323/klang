import os
import sys
import time
import subprocess
from pathlib import Path
from typing import Optional

from PyQt6.QtCore import QObject, pyqtSignal, QAbstractNativeEventFilter, QCoreApplication
from PyQt6.QtWidgets import QWidget

from qt6_player.data.tracks import NordicTrack

IS_WIN32 = sys.platform == "win32"
APP_USER_MODEL_ID = "Klang.MusicPlayer"

if IS_WIN32:
    import ctypes
    from ctypes import (
        wintypes,
        c_void_p,
        c_int,
        c_byte,
        c_short,
        c_long,
        c_ulong,
        c_ushort,
        c_int64,
        c_wchar_p,
        POINTER,
        byref,
        Structure,
        WINFUNCTYPE,
        cast,
    )

    class GUID(Structure):
        _fields_ = [
            ("Data1", c_long),
            ("Data2", c_short),
            ("Data3", c_short),
            ("Data4", c_byte * 8),
        ]

        def __init__(self, s: str):
            import uuid
            u = uuid.UUID(s)
            super().__init__(u.time_low, u.time_mid, u.time_hi_version, (c_byte * 8)(*u.bytes[8:]))

        def to_uuid(self):
            import uuid
            return uuid.UUID(bytes_le=bytes(self))

    class PropertyKey(Structure):
        _fields_ = [
            ("fmtid", GUID),
            ("pid", wintypes.DWORD),
        ]

    class PropVariant(Structure):
        _fields_ = [
            ("vt", c_ushort),
            ("wReserved1", c_ushort),
            ("wReserved2", c_ushort),
            ("wReserved3", c_ushort),
            ("pwszVal", c_wchar_p),
        ]

    combase = ctypes.windll.combase
    user32 = ctypes.windll.user32
    shell32 = ctypes.windll.shell32
    ole32 = ctypes.windll.ole32

    RoInitialize = combase.RoInitialize
    RoInitialize.argtypes = [c_int]
    RoInitialize.restype = c_int

    WindowsCreateString = combase.WindowsCreateString
    WindowsCreateString.argtypes = [c_wchar_p, wintypes.UINT, POINTER(c_void_p)]
    WindowsCreateString.restype = c_int

    WindowsDeleteString = combase.WindowsDeleteString
    WindowsDeleteString.argtypes = [c_void_p]
    WindowsDeleteString.restype = c_int

    RoGetActivationFactory = combase.RoGetActivationFactory
    RoGetActivationFactory.argtypes = [c_void_p, POINTER(GUID), POINTER(c_void_p)]
    RoGetActivationFactory.restype = c_int

    def create_hstring(s: str) -> c_void_p:
        h = c_void_p()
        WindowsCreateString(s, len(s), byref(h))
        return h

    # Win32 Virtual Keys
    VK_MEDIA_NEXT_TRACK = 0xB0
    VK_MEDIA_PREV_TRACK = 0xB1
    VK_MEDIA_STOP = 0xB2
    VK_MEDIA_PLAY_PAUSE = 0xB3
    MOD_NOREPEAT = 0x4000

    HOTKEY_ID_PLAY_PAUSE = 1001
    HOTKEY_ID_NEXT = 1002
    HOTKEY_ID_PREV = 1003
    HOTKEY_ID_STOP = 1004

    SUPPORTED_IIDS = {
        "00000000-0000-0000-c000-000000000046",  # IUnknown
        "94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90",  # IAgileObject
        "0557e996-7b23-5bae-aa81-ea0d671143a4",  # TypedEventHandler<SystemMediaTransportControls, ButtonPressedArgs>
    }

    QI_Func = WINFUNCTYPE(c_int, c_void_p, POINTER(GUID), POINTER(c_void_p))
    AddRef_Func = WINFUNCTYPE(c_ulong, c_void_p)
    Release_Func = WINFUNCTYPE(c_ulong, c_void_p)
    Invoke_Func = WINFUNCTYPE(c_int, c_void_p, c_void_p, c_void_p)

    def ensure_start_menu_shortcut() -> None:
        """
        在当前用户的 Windows 开始菜单创建带 AppUserModelID 的 Klang 快捷方式。
        Windows SMTC、任务栏与全局音量浮窗依赖此快捷方式将当前进程识别为 'Klang' 并加载应用图标，
        彻底解决系统浮窗显示 'Unknown app' (未知app) 的问题。
        """
        try:
            programs_dir = Path(os.environ.get("APPDATA", "")) / "Microsoft" / "Windows" / "Start Menu" / "Programs"
            if not programs_dir.exists():
                programs_dir.mkdir(parents=True, exist_ok=True)
            lnk_path = programs_dir / "Klang.lnk"

            if getattr(sys, "frozen", False):
                # 打包后：指向真实的 exe 本身（sys._MEIPASS 是每次运行都会变的临时目录，不能写进快捷方式）
                target_exe = str(Path(sys.executable).resolve())
                arguments = ""
                root = Path(target_exe).parent
                ico_path = target_exe
            else:
                root = Path(__file__).resolve().parent.parent.parent
                py_exe = sys.executable
                pyw_exe = Path(py_exe).parent / "pythonw.exe"
                target_exe = str(pyw_exe if pyw_exe.exists() else py_exe)
                arguments = '"' + str(root / "run_qt6_player.py") + '"'
                ico_path = str(root / "qt6_player" / "assets" / "klang_icon.ico")

            # 记录上次创建快捷方式时的目标；目标变了（换了安装位置/解释器）就重建，避免残留过期快捷方式
            marker = lnk_path.with_suffix(".klang-target")
            signature = f"{target_exe}|{arguments}"
            try:
                up_to_date = lnk_path.exists() and marker.read_text(encoding="utf-8") == signature
            except Exception:
                up_to_date = False

            def _ps_quote(v: str) -> str:
                # PowerShell 单引号字符串内，单引号需写成两个单引号
                return "'" + str(v).replace("'", "''") + "'"

            if not up_to_date:
                ps_script = (
                    "$WshShell = New-Object -ComObject WScript.Shell\n"
                    f"$Shortcut = $WshShell.CreateShortcut({_ps_quote(str(lnk_path))})\n"
                    f"$Shortcut.TargetPath = {_ps_quote(target_exe)}\n"
                    f"$Shortcut.Arguments = {_ps_quote(arguments)}\n"
                    f"$Shortcut.WorkingDirectory = {_ps_quote(str(root))}\n"
                    f"$Shortcut.IconLocation = {_ps_quote(ico_path + ',0')}\n"
                    "$Shortcut.Save()\n"
                )
                proc = subprocess.run(
                    ["powershell", "-NoProfile", "-Command", ps_script],
                    capture_output=True,
                    text=True,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                    check=False,
                )
                if proc.returncode == 0 and lnk_path.exists():
                    try:
                        marker.write_text(signature, encoding="utf-8")
                    except Exception:
                        pass

            # 为快捷方式写入 PKEY_AppUserModel_ID
            ole32.CoInitialize(None)
            CLSID_ShellLink = GUID("00021401-0000-0000-C000-000000000046")
            IID_IPersistFile = GUID("0000010b-0000-0000-C000-000000000046")
            IID_IPropertyStore = GUID("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")

            pLink = c_void_p()
            hr = ole32.CoCreateInstance(byref(CLSID_ShellLink), None, 1, byref(IID_IPersistFile), byref(pLink))
            if hr == 0 and pLink.value:
                vtable_persist = cast(pLink, POINTER(POINTER(c_void_p))).contents
                QI = WINFUNCTYPE(c_int, c_void_p, POINTER(GUID), POINTER(c_void_p))(vtable_persist[0])
                Load = WINFUNCTYPE(c_int, c_void_p, c_wchar_p, wintypes.DWORD)(vtable_persist[5])
                Save = WINFUNCTYPE(c_int, c_void_p, c_wchar_p, wintypes.BOOL)(vtable_persist[6])

                hr_load = Load(pLink, str(lnk_path), 2)  # STGM_READWRITE = 2
                pStore = c_void_p()
                hr_qi = QI(pLink, byref(IID_IPropertyStore), byref(pStore))
                if hr_qi == 0 and pStore.value:
                    vtable_store = cast(pStore, POINTER(POINTER(c_void_p))).contents
                    SetValue = WINFUNCTYPE(c_int, c_void_p, POINTER(PropertyKey), POINTER(PropVariant))(vtable_store[6])
                    Commit = WINFUNCTYPE(c_int, c_void_p)(vtable_store[7])

                    key = PropertyKey(GUID("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5)
                    val = PropVariant(31, 0, 0, 0, APP_USER_MODEL_ID)
                    SetValue(pStore, byref(key), byref(val))
                    Commit(pStore)
                    Save(pLink, str(lnk_path), True)
        except Exception:
            pass


class WindowsMediaControlService(QObject):
    """
    Windows 10/11 系统媒体传输控件 (SMTC) 与全局多媒体键盘键位综合管理服务
    """

    playRequested = pyqtSignal()
    pauseRequested = pyqtSignal()
    playPauseToggled = pyqtSignal()
    nextRequested = pyqtSignal()
    previousRequested = pyqtSignal()
    stopRequested = pyqtSignal()

    def __init__(self, window: QWidget, parent: Optional[QObject] = None):
        super().__init__(parent)
        self.window = window
        self.hwnd = int(window.winId()) if window else 0
        self._last_event_time: float = 0.0

        # SMTC 引用
        self._smtc = None
        self._updater = None
        self._music_props = None
        self._token: Optional[int] = None
        self._event_filter: Optional[QAbstractNativeEventFilter] = None
        self._registered_hotkeys: list[int] = []

        if IS_WIN32 and self.hwnd:
            self._set_app_identity()
            self._init_smtc()
            self._init_hotkeys_and_filter()

    def _set_app_identity(self) -> None:
        """向 Windows Shell 显式注册应用身份，彻底消除 'Unknown app'"""
        try:
            shell32.SetCurrentProcessExplicitAppUserModelID(APP_USER_MODEL_ID)
        except Exception:
            pass

        try:
            # 窗口 PropertyStore 绑定
            IID_IPropertyStore = GUID("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")
            pStore = c_void_p()
            SHGetPropertyStoreForWindow = shell32.SHGetPropertyStoreForWindow
            SHGetPropertyStoreForWindow.argtypes = [wintypes.HWND, POINTER(GUID), POINTER(c_void_p)]
            SHGetPropertyStoreForWindow.restype = c_int

            hr = SHGetPropertyStoreForWindow(self.hwnd, byref(IID_IPropertyStore), byref(pStore))
            if hr == 0 and pStore.value:
                vtable = cast(pStore, POINTER(POINTER(c_void_p))).contents
                SetValue = WINFUNCTYPE(c_int, c_void_p, POINTER(PropertyKey), POINTER(PropVariant))(vtable[6])
                Commit = WINFUNCTYPE(c_int, c_void_p)(vtable[7])

                key = PropertyKey(GUID("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5)
                val = PropVariant(31, 0, 0, 0, APP_USER_MODEL_ID)
                SetValue(pStore, byref(key), byref(val))
                Commit(pStore)
        except Exception:
            pass

        # 确保开始菜单存在对应的快捷方式以供 Windows 索引应用名与图标
        # 创建快捷方式要调用 PowerShell，放到后台线程，避免卡住界面启动
        import threading
        threading.Thread(target=ensure_start_menu_shortcut, daemon=True, name='klang-shortcut').start()

    def _init_smtc(self) -> None:
        try:
            RoInitialize(1)
            h_class = create_hstring("Windows.Media.SystemMediaTransportControls")
            iid_interop = GUID("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a")
            factory = c_void_p()
            hr = RoGetActivationFactory(h_class, byref(iid_interop), byref(factory))
            WindowsDeleteString(h_class)
            if hr != 0 or not factory.value:
                return

            vtable_factory = cast(factory, POINTER(POINTER(c_void_p))).contents
            GetForWindowType = WINFUNCTYPE(c_int, c_void_p, wintypes.HWND, POINTER(GUID), POINTER(c_void_p))
            GetForWindow = GetForWindowType(vtable_factory[6])

            iid_smtc = GUID("99fa3ff4-1742-42a6-902e-087d41f965ec")
            smtc = c_void_p()
            hr = GetForWindow(factory, self.hwnd, byref(iid_smtc), byref(smtc))
            if hr != 0 or not smtc.value:
                return

            self._smtc = smtc
            vtable_smtc = cast(smtc, POINTER(POINTER(c_void_p))).contents

            PutBoolType = WINFUNCTYPE(c_int, c_void_p, c_byte)
            self._put_IsEnabled = PutBoolType(vtable_smtc[11])
            self._put_IsPlayEnabled = PutBoolType(vtable_smtc[13])
            self._put_IsPauseEnabled = PutBoolType(vtable_smtc[17])
            self._put_IsNextEnabled = PutBoolType(vtable_smtc[27])
            self._put_IsPreviousEnabled = PutBoolType(vtable_smtc[25])
            self._put_IsStopEnabled = PutBoolType(vtable_smtc[15])

            # 激活基础媒体按钮
            self._put_IsEnabled(smtc, 1)
            self._put_IsPlayEnabled(smtc, 1)
            self._put_IsPauseEnabled(smtc, 1)
            self._put_IsNextEnabled(smtc, 1)
            self._put_IsPreviousEnabled(smtc, 1)
            self._put_IsStopEnabled(smtc, 1)

            PutIntType = WINFUNCTYPE(c_int, c_void_p, c_int)
            self._put_PlaybackStatus = PutIntType(vtable_smtc[7])

            # 提取 DisplayUpdater
            GetPtrType = WINFUNCTYPE(c_int, c_void_p, POINTER(c_void_p))
            get_DisplayUpdater = GetPtrType(vtable_smtc[8])
            updater = c_void_p()
            get_DisplayUpdater(smtc, byref(updater))
            self._updater = updater

            vtable_updater = cast(updater, POINTER(POINTER(c_void_p))).contents
            put_Type = PutIntType(vtable_updater[7])
            put_Type(updater, 1)  # 1 = MediaPlaybackType.Music

            # 绑定 AppMediaId 为 Klang.MusicPlayer
            PutHStringType = WINFUNCTYPE(c_int, c_void_p, c_void_p)
            try:
                put_AppMediaId = PutHStringType(vtable_updater[9])
                h_app_id = create_hstring(APP_USER_MODEL_ID)
                put_AppMediaId(updater, h_app_id)
                WindowsDeleteString(h_app_id)
            except Exception:
                pass

            get_MusicProperties = GetPtrType(vtable_updater[12])
            music_props = c_void_p()
            get_MusicProperties(updater, byref(music_props))
            self._music_props = music_props

            vtable_music = cast(music_props, POINTER(POINTER(c_void_p))).contents
            self._put_Title = PutHStringType(vtable_music[7])
            self._put_Artist = PutHStringType(vtable_music[11])
            self._put_AlbumArtist = PutHStringType(vtable_music[9])

            VoidFuncType = WINFUNCTYPE(c_int, c_void_p)
            self._update_display = VoidFuncType(vtable_updater[17])

            # 挂载 ButtonPressed 敏捷事件委托
            self._qi_cb = QI_Func(self._qi_impl)
            self._addref_cb = AddRef_Func(lambda this: 1)
            self._release_cb = Release_Func(lambda this: 1)
            self._invoke_cb = Invoke_Func(self._invoke_impl)

            self._vtable_array = (c_void_p * 4)(
                cast(self._qi_cb, c_void_p).value,
                cast(self._addref_cb, c_void_p).value,
                cast(self._release_cb, c_void_p).value,
                cast(self._invoke_cb, c_void_p).value,
            )

            class HandlerObj(Structure):
                _fields_ = [("lpVtbl", POINTER(c_void_p))]

            self._handler_inst = HandlerObj()
            self._handler_inst.lpVtbl = cast(self._vtable_array, POINTER(c_void_p))

            AddButtonPressedType = WINFUNCTYPE(c_int, c_void_p, c_void_p, POINTER(c_int64))
            add_btn = AddButtonPressedType(vtable_smtc[32])
            token = c_int64()
            hr = add_btn(smtc, byref(self._handler_inst), byref(token))
            if hr == 0:
                self._token = token.value
                self._remove_ButtonPressed = WINFUNCTYPE(c_int, c_void_p, c_int64)(vtable_smtc[33])
        except Exception:
            pass

    def _qi_impl(self, this, riid, ppv):
        try:
            u = str(riid.contents.to_uuid())
            if u in SUPPORTED_IIDS:
                ppv[0] = this
                return 0
        except Exception:
            pass
        ppv[0] = None
        return -2147467262  # E_NOINTERFACE

    def _invoke_impl(self, this, sender, args):
        """
        WinRT RPC 线程池回调入口。
        直接发射 Qt 信号利用 QueuedConnection 自动跨线程安全投递至主 GUI 线程。
        """
        now = time.time()
        if now - self._last_event_time < 0.20:
            return 0
        self._last_event_time = now

        try:
            vtable_args = cast(args, POINTER(POINTER(c_void_p))).contents
            GetButtonType = WINFUNCTYPE(c_int, c_void_p, POINTER(c_int))
            get_btn = GetButtonType(vtable_args[6])
            btn_val = c_int()
            get_btn(args, byref(btn_val))
            btn = btn_val.value

            # Play = 0, Pause = 1, Stop = 2, Next = 6, Previous = 7
            if btn == 0:
                self.playRequested.emit()
            elif btn == 1:
                self.pauseRequested.emit()
            elif btn == 2:
                self.stopRequested.emit()
            elif btn == 6:
                self.nextRequested.emit()
            elif btn == 7:
                self.previousRequested.emit()
        except Exception:
            pass
        return 0

    def _init_hotkeys_and_filter(self) -> None:
        """注册 Windows 全局快捷键与原生消息过滤器（保障后台按键瞬时捕获）"""
        hotkey_specs = [
            (HOTKEY_ID_PLAY_PAUSE, VK_MEDIA_PLAY_PAUSE),
            (HOTKEY_ID_NEXT, VK_MEDIA_NEXT_TRACK),
            (HOTKEY_ID_PREV, VK_MEDIA_PREV_TRACK),
            (HOTKEY_ID_STOP, VK_MEDIA_STOP),
        ]
        for hid, vk in hotkey_specs:
            if user32.RegisterHotKey(self.hwnd, hid, MOD_NOREPEAT, vk):
                self._registered_hotkeys.append(hid)

        class MediaNativeEventFilter(QAbstractNativeEventFilter):
            def __init__(self, service: "WindowsMediaControlService"):
                super().__init__()
                self.service = service

            def nativeEventFilter(self, event_type, message):
                if event_type in (b"windows_generic_MSG", "windows_generic_MSG"):
                    try:
                        msg = wintypes.MSG.from_address(int(message))
                        if msg.message == 0x0312:  # WM_HOTKEY
                            hid = msg.wParam
                            if self.service._handle_hotkey(hid):
                                return True, 0
                        elif msg.message == 0x0319:  # WM_APPCOMMAND
                            cmd = (msg.lParam >> 16) & ~0xF000
                            if self.service._handle_appcommand(cmd):
                                return True, 0
                    except Exception:
                        pass
                return False, 0

        self._event_filter = MediaNativeEventFilter(self)
        app = QCoreApplication.instance()
        if app:
            app.installNativeEventFilter(self._event_filter)

    def _handle_hotkey(self, hid: int) -> bool:
        now = time.time()
        if now - self._last_event_time < 0.20:
            return True
        self._last_event_time = now

        if hid == HOTKEY_ID_PLAY_PAUSE:
            self.playPauseToggled.emit()
            return True
        elif hid == HOTKEY_ID_NEXT:
            self.nextRequested.emit()
            return True
        elif hid == HOTKEY_ID_PREV:
            self.previousRequested.emit()
            return True
        elif hid == HOTKEY_ID_STOP:
            self.stopRequested.emit()
            return True
        return False

    def _handle_appcommand(self, cmd: int) -> bool:
        now = time.time()
        if now - self._last_event_time < 0.20:
            return True
        self._last_event_time = now

        if cmd == 14:  # APPCOMMAND_MEDIA_PLAY_PAUSE
            self.playPauseToggled.emit()
            return True
        elif cmd == 46:  # PLAY
            self.playRequested.emit()
            return True
        elif cmd == 47:  # PAUSE
            self.pauseRequested.emit()
            return True
        elif cmd == 11:  # NEXT
            self.nextRequested.emit()
            return True
        elif cmd == 12:  # PREV
            self.previousRequested.emit()
            return True
        elif cmd == 13:  # STOP
            self.stopRequested.emit()
            return True
        return False

    def update_track(self, track: Optional[NordicTrack]) -> None:
        if not track:
            self.update_metadata("Klang", "北欧原声音乐", "Klang Music")
            return
        artist_display = track.artist if track.artist and track.artist != "未知歌手" else "Klang"
        self.update_metadata(track.title, artist_display, track.album or "Klang Music")

    def update_metadata(self, title: str, artist: str = "", album: str = "") -> None:
        """向 Windows SMTC 同步元数据"""
        if not self._updater or not self._music_props:
            return
        try:
            ht = create_hstring(title or "Klang")
            self._put_Title(self._music_props, ht)
            WindowsDeleteString(ht)

            ha = create_hstring(artist or "")
            self._put_Artist(self._music_props, ha)
            WindowsDeleteString(ha)

            if album:
                hal = create_hstring(album)
                self._put_AlbumArtist(self._music_props, hal)
                WindowsDeleteString(hal)

            self._update_display(self._updater)
        except Exception:
            pass

    def set_playback_status(self, is_playing: bool) -> None:
        """向 Windows SMTC 同步播放状态（3=Playing, 4=Paused）"""
        if not self._smtc:
            return
        try:
            status = 3 if is_playing else 4
            self._put_PlaybackStatus(self._smtc, status)
        except Exception:
            pass

    def cleanup(self) -> None:
        """应用退出前注销全局热键与事件监听器"""
        if not IS_WIN32:
            return
        for hid in self._registered_hotkeys:
            try:
                user32.UnregisterHotKey(self.hwnd, hid)
            except Exception:
                pass
        self._registered_hotkeys.clear()

        if self._event_filter:
            app = QCoreApplication.instance()
            if app:
                try:
                    app.removeNativeEventFilter(self._event_filter)
                except Exception:
                    pass
            self._event_filter = None

        if self._smtc and self._token and hasattr(self, "_remove_ButtonPressed"):
            try:
                self._remove_ButtonPressed(self._smtc, self._token)
            except Exception:
                pass
            self._smtc = None
            self._token = None
