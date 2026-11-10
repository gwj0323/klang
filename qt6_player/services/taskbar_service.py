"""
Windows Taskbar Thumbnail Toolbar Service for Klang.
Implements ITaskbarList3 COM interface to provide Prev, Play/Pause, Next controls
in the Windows Taskbar Hover Preview (Thumbnail Toolbar).
"""

import logging as _logging
import sys
import ctypes
from ctypes import wintypes
from typing import Optional
import logging

from PyQt6.QtGui import QImage, QPainter, QColor, QPainterPath
from PyQt6.QtCore import Qt, QBuffer, QIODevice

logger = logging.getLogger(__name__)

# Only on Windows
IS_WINDOWS = sys.platform == "win32"

if IS_WINDOWS:
    ole32 = ctypes.windll.ole32
    user32 = ctypes.windll.user32
else:
    ole32 = None
    user32 = None

# Constants
WM_COMMAND = 0x0111
THBN_CLICKED = 0x1800

THB_BITMAP = 0x0001
THB_ICON = 0x0002
THB_TOOLTIP = 0x0004
THB_FLAGS = 0x0008

THBF_ENABLED = 0x0000
THBF_DISABLED = 0x0001
THBF_DISMISSONCLICK = 0x0002
THBF_NOBACKGROUND = 0x0004
THBF_HIDDEN = 0x0008

BUTTON_PREV = 101
BUTTON_PLAY_PAUSE = 102
BUTTON_NEXT = 103

CLSID_TaskbarList = "{56FDF344-FD6D-11D0-958A-006097C9A090}"
IID_ITaskbarList3 = "{EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF}"


class GUID(ctypes.Structure):
    _fields_ = [
        ("Data1", ctypes.c_ulong),
        ("Data2", ctypes.c_ushort),
        ("Data3", ctypes.c_ushort),
        ("Data4", ctypes.c_ubyte * 8)
    ]

    @classmethod
    def from_str(cls, s: str):
        g = cls()
        if ole32:
            ole32.CLSIDFromString(ctypes.c_wchar_p(s), ctypes.byref(g))
        return g


class THUMBBUTTON(ctypes.Structure):
    _pack_ = 8
    _fields_ = [
        ("dwMask", wintypes.DWORD),
        ("iId", wintypes.UINT),
        ("iBitmap", wintypes.UINT),
        ("hIcon", wintypes.HICON),
        ("szTip", wintypes.WCHAR * 260),
        ("dwFlags", wintypes.DWORD),
    ]


def _create_hicon_from_image(img: QImage) -> Optional[int]:
    """Convert QImage to native Win32 HICON handle."""
    if not user32:
        return None
    try:
        buf = QBuffer()
        buf.open(QIODevice.OpenModeFlag.WriteOnly)
        img.save(buf, "PNG")
        png_data = bytes(buf.data())
        hicon = user32.CreateIconFromResourceEx(
            png_data,
            len(png_data),
            True,        # fIcon: True for icon
            0x00030000,  # dwVersion: 0x00030000 (Win32)
            img.width(),
            img.height(),
            0            # Flags: LR_DEFAULTCOLOR
        )
        return hicon
    except Exception as e:
        logger.debug(f"[Taskbar] Failed to create HICON: {e}")
        return None


def _render_prev_icon(size: int = 24) -> Optional[int]:
    """Render crisp vector 'Previous track' icon."""
    img = QImage(size, size, QImage.Format.Format_ARGB32_Premultiplied)
    img.fill(Qt.GlobalColor.transparent)
    p = QPainter(img)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.setBrush(QColor(240, 240, 240))
    p.setPen(Qt.PenStyle.NoPen)

    # Vertical bar on the left
    bar_w = max(2.0, size * 0.1)
    p.drawRoundedRect(int(size * 0.2), int(size * 0.22), int(bar_w), int(size * 0.56), 1, 1)

    # Triangle pointing left
    path = QPainterPath()
    path.moveTo(size * 0.82, size * 0.22)
    path.lineTo(size * 0.36, size * 0.5)
    path.lineTo(size * 0.82, size * 0.78)
    path.closeSubpath()
    p.drawPath(path)
    p.end()

    return _create_hicon_from_image(img)


def _render_next_icon(size: int = 24) -> Optional[int]:
    """Render crisp vector 'Next track' icon."""
    img = QImage(size, size, QImage.Format.Format_ARGB32_Premultiplied)
    img.fill(Qt.GlobalColor.transparent)
    p = QPainter(img)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.setBrush(QColor(240, 240, 240))
    p.setPen(Qt.PenStyle.NoPen)

    # Triangle pointing right
    path = QPainterPath()
    path.moveTo(size * 0.18, size * 0.22)
    path.lineTo(size * 0.64, size * 0.5)
    path.lineTo(size * 0.18, size * 0.78)
    path.closeSubpath()
    p.drawPath(path)

    # Vertical bar on the right
    bar_w = max(2.0, size * 0.1)
    p.drawRoundedRect(int(size * 0.72), int(size * 0.22), int(bar_w), int(size * 0.56), 1, 1)
    p.end()

    return _create_hicon_from_image(img)


def _render_play_icon(size: int = 24) -> Optional[int]:
    """Render crisp vector 'Play' triangle icon."""
    img = QImage(size, size, QImage.Format.Format_ARGB32_Premultiplied)
    img.fill(Qt.GlobalColor.transparent)
    p = QPainter(img)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.setBrush(QColor(240, 240, 240))
    p.setPen(Qt.PenStyle.NoPen)

    path = QPainterPath()
    path.moveTo(size * 0.28, size * 0.18)
    path.lineTo(size * 0.82, size * 0.5)
    path.lineTo(size * 0.28, size * 0.82)
    path.closeSubpath()
    p.drawPath(path)
    p.end()

    return _create_hicon_from_image(img)


def _render_pause_icon(size: int = 24) -> Optional[int]:
    """Render crisp vector 'Pause' two vertical bars icon."""
    img = QImage(size, size, QImage.Format.Format_ARGB32_Premultiplied)
    img.fill(Qt.GlobalColor.transparent)
    p = QPainter(img)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.setBrush(QColor(240, 240, 240))
    p.setPen(Qt.PenStyle.NoPen)

    bar_w = max(3.0, size * 0.16)
    p.drawRoundedRect(int(size * 0.26), int(size * 0.2), int(bar_w), int(size * 0.6), 1.5, 1.5)
    p.drawRoundedRect(int(size * 0.58), int(size * 0.2), int(bar_w), int(size * 0.6), 1.5, 1.5)
    p.end()

    return _create_hicon_from_image(img)


class TaskbarThumbnailService:
    """
    Manages Windows Taskbar thumbnail toolbar preview controls.
    Provides Prev, Play/Pause, Next interactive buttons.
    """

    def __init__(self):
        self.wm_taskbar_button_created = 0
        self.hwnd: Optional[int] = None
        self._p_tbl = None
        self._vtable = None
        self._toolbar_initialized = False
        self._is_playing = False
        self._created_icons = []
        self._state_icons = {}

        if IS_WINDOWS and user32:
            try:
                self.wm_taskbar_button_created = user32.RegisterWindowMessageW("TaskbarButtonCreated")
                logger.debug(f"[Taskbar] Registered TaskbarButtonCreated: {self.wm_taskbar_button_created}")
            except Exception as e:
                logger.debug(f"[Taskbar] RegisterWindowMessageW failed: {e}")

    def register_window(self, hwnd: int):
        """Allow UIPI message passthrough and store window handle."""
        self.hwnd = hwnd
        if not IS_WINDOWS or not user32:
            return

        # Explicitly allow TaskbarButtonCreated and WM_COMMAND through UIPI
        MSGFLT_ALLOW = 1
        try:
            if hasattr(user32, "ChangeWindowMessageFilterEx") and self.wm_taskbar_button_created:
                user32.ChangeWindowMessageFilterEx(hwnd, self.wm_taskbar_button_created, MSGFLT_ALLOW, None)
                user32.ChangeWindowMessageFilterEx(hwnd, WM_COMMAND, MSGFLT_ALLOW, None)
        except Exception as e:
            logger.debug(f"[Taskbar] ChangeWindowMessageFilterEx: {e}")

    def _get_taskbar_interface(self):
        """Create and initialize ITaskbarList3 instance if not already done."""
        if self._p_tbl and self._vtable:
            return self._p_tbl, self._vtable

        if not IS_WINDOWS or not ole32:
            return None, None

        try:
            clsid = GUID.from_str(CLSID_TaskbarList)
            iid = GUID.from_str(IID_ITaskbarList3)
            p = ctypes.c_void_p()
            hr = ole32.CoCreateInstance(ctypes.byref(clsid), None, 1, ctypes.byref(iid), ctypes.byref(p))
            if hr != 0 or not p.value:
                logger.debug(f"[Taskbar] CoCreateInstance failed hr=0x{hr & 0xFFFFFFFF:08X}")
                return None, None

            vtable = ctypes.cast(p, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
            # Slot 3 is HrInit
            HrInit = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p)(vtable[3])
            HrInit(p)

            self._p_tbl = p
            self._vtable = vtable
            return self._p_tbl, self._vtable
        except Exception as e:
            logger.debug(f"[Taskbar] COM initialization exception: {e}")
            return None, None

    def init_toolbar(self, hwnd: int, is_playing: bool = False):
        """Add thumbnail buttons (Prev, Play/Pause, Next) to the taskbar."""
        if self._toolbar_initialized:
            self.update_play_state(is_playing)
            return

        self.hwnd = hwnd
        self._is_playing = is_playing

        p_tbl, vtable = self._get_taskbar_interface()
        if not p_tbl or not vtable:
            return

        try:
            h_prev = _render_prev_icon(24)
            h_play_pause = _render_pause_icon(24) if is_playing else _render_play_icon(24)
            h_next = _render_next_icon(24)

            self._created_icons.extend([h_prev, h_play_pause, h_next])

            buttons = (THUMBBUTTON * 3)()

            # Button 101: Prev
            buttons[0].dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS
            buttons[0].iId = BUTTON_PREV
            buttons[0].hIcon = h_prev or 0
            buttons[0].szTip = "上一首"
            buttons[0].dwFlags = THBF_ENABLED

            # Button 102: Play/Pause
            buttons[1].dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS
            buttons[1].iId = BUTTON_PLAY_PAUSE
            buttons[1].hIcon = h_play_pause or 0
            buttons[1].szTip = "暂停" if is_playing else "播放"
            buttons[1].dwFlags = THBF_ENABLED

            # Button 103: Next
            buttons[2].dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS
            buttons[2].iId = BUTTON_NEXT
            buttons[2].hIcon = h_next or 0
            buttons[2].szTip = "下一首"
            buttons[2].dwFlags = THBF_ENABLED

            # Slot 15: ThumbBarAddButtons(This, HWND, UINT, LPTHUMBBUTTON)
            ThumbBarAddButtons = ctypes.WINFUNCTYPE(
                ctypes.c_long,
                ctypes.c_void_p,
                wintypes.HWND,
                wintypes.UINT,
                ctypes.POINTER(THUMBBUTTON)
            )(vtable[15])

            hr = ThumbBarAddButtons(p_tbl, hwnd, 3, buttons)
            if hr == 0:
                self._toolbar_initialized = True
                logger.info("[Taskbar] Thumbnail toolbar buttons successfully added.")
            else:
                logger.debug(f"[Taskbar] ThumbBarAddButtons hr=0x{hr & 0xFFFFFFFF:08X}")
        except Exception as e:
            logger.debug(f"[Taskbar] init_toolbar failed: {e}")

    def update_play_state(self, is_playing: bool):
        """Update the Play/Pause button icon and tooltip dynamically."""
        self._is_playing = is_playing
        if not self._toolbar_initialized or not self.hwnd:
            return

        p_tbl, vtable = self._get_taskbar_interface()
        if not p_tbl or not vtable:
            return

        try:
            h_icon = self._state_icons.get(bool(is_playing))
            if not h_icon:
                h_icon = _render_pause_icon(24) if is_playing else _render_play_icon(24)
                self._state_icons[bool(is_playing)] = h_icon
                self._created_icons.append(h_icon)

            btn = THUMBBUTTON()
            btn.dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS
            btn.iId = BUTTON_PLAY_PAUSE
            btn.hIcon = h_icon or 0
            btn.szTip = "暂停" if is_playing else "播放"
            btn.dwFlags = THBF_ENABLED

            # Slot 16: ThumbBarUpdateButtons(This, HWND, UINT, LPTHUMBBUTTON)
            ThumbBarUpdateButtons = ctypes.WINFUNCTYPE(
                ctypes.c_long,
                ctypes.c_void_p,
                wintypes.HWND,
                wintypes.UINT,
                ctypes.POINTER(THUMBBUTTON)
            )(vtable[16])

            hr = ThumbBarUpdateButtons(p_tbl, self.hwnd, 1, ctypes.byref(btn))
            if hr != 0:
                logger.debug(f"[Taskbar] ThumbBarUpdateButtons hr=0x{hr & 0xFFFFFFFF:08X}")
        except Exception as e:
            logger.debug(f"[Taskbar] update_play_state failed: {e}")

    def handle_native_event(self, message_ptr: int, handler) -> bool:
        """
        Handle Win32 messages in nativeEvent.
        Listens for TaskbarButtonCreated and WM_COMMAND clicks.
        Returns True if the event was processed and should be consumed.
        """
        if not IS_WINDOWS or not user32:
            return False

        try:
            msg = wintypes.MSG.from_address(int(message_ptr))
            
            # 1. Check for TaskbarButtonCreated
            if self.wm_taskbar_button_created and msg.message == self.wm_taskbar_button_created:
                hwnd = int(msg.hwnd) if msg.hwnd else self.hwnd
                audio = getattr(handler, "audio", handler)
                is_playing = getattr(audio, "is_playing", False)
                # 第二次及以后收到该消息（资源管理器重启后任务栏按钮被重建）：之前添加的缩略图按钮已丢失，需重新添加
                self._button_created_count = getattr(self, "_button_created_count", 0) + 1
                if self._button_created_count > 1:
                    self._toolbar_initialized = False
                    self._p_tbl = None
                    self._vtable = None
                if hwnd:
                    self.init_toolbar(hwnd, is_playing)
                return False

            # 2. Check for WM_COMMAND clicks from thumbnail toolbar
            if msg.message == WM_COMMAND:
                hiword = (msg.wParam >> 16) & 0xFFFF
                loword = msg.wParam & 0xFFFF
                if hiword == THBN_CLICKED:
                    if loword == BUTTON_PREV:
                        logger.info("[Taskbar] Click: Previous track")
                        if hasattr(handler, "_on_prev_track"):
                            handler._on_prev_track()
                        elif hasattr(handler, "prev"):
                            handler.prev()
                        return True
                    elif loword == BUTTON_PLAY_PAUSE:
                        logger.info("[Taskbar] Click: Play/Pause")
                        audio = getattr(handler, "audio", handler)
                        if hasattr(audio, "toggle_play"):
                            audio.toggle_play()
                        elif hasattr(handler, "play_pause"):
                            handler.play_pause()
                        return True
                    elif loword == BUTTON_NEXT:
                        logger.info("[Taskbar] Click: Next track")
                        if hasattr(handler, "_on_next_track"):
                            handler._on_next_track()
                        elif hasattr(handler, "next"):
                            handler.next()
                        return True
        except Exception as e:
            logger.debug(f"[Taskbar] handle_native_event error: {e}")

        return False

    def cleanup(self):
        """Free GDI icon handles on shutdown."""
        if user32:
            for h in self._created_icons:
                if h:
                    try:
                        user32.DestroyIcon(h)
                    except Exception:
                        _logging.getLogger(__name__).debug("ignored exception", exc_info=True)
            self._created_icons.clear()
            self._state_icons.clear()
