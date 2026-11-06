import sys
import ctypes
from ctypes import c_int, byref, sizeof

DWMWA_USE_IMMERSIVE_DARK_MODE = 20
DWMWA_WINDOW_CORNER_PREFERENCE = 33
DWMWCP_DEFAULT = 0
DWMWCP_DONOTROUND = 1
DWMWCP_ROUND = 2
DWMWCP_ROUNDSMALL = 3


def apply_window_system_theme_and_corners(
    hwnd: int, is_dark: bool = False, is_maximized: bool = False
) -> None:
    if sys.platform != "win32" or not hwnd:
        return

    try:
        dark_val = c_int(1 if is_dark else 0)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(
            hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, byref(dark_val), sizeof(dark_val)
        )
    except Exception:
        pass

    try:
        pref = c_int(DWMWCP_DONOTROUND if is_maximized else DWMWCP_ROUND)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(
            hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, byref(pref), sizeof(pref)
        )
    except Exception:
        pass
