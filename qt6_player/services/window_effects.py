"""
Windows DWM 窗口圆角与深浅色模式系统适配服务
支持 Windows 11 原生 DWM 圆角策略 (DWMWA_WINDOW_CORNER_PREFERENCE)
以及深浅色沉浸式模式适配 (DWMWA_USE_IMMERSIVE_DARK_MODE)。
"""

import sys
import ctypes
from ctypes import c_int, byref, sizeof

DWMWA_USE_IMMERSIVE_DARK_MODE = 20
DWMWA_WINDOW_CORNER_PREFERENCE = 33
DWMWCP_DEFAULT = 0
DWMWCP_DONOTROUND = 1       # 强制直角（最大化时）
DWMWCP_ROUND = 2            # 12px 标准圆角（常规窗口时）
DWMWCP_ROUNDSMALL = 3


def apply_window_system_theme_and_corners(
    hwnd: int, is_dark: bool = False, is_maximized: bool = False
) -> None:
    """在 Windows 窗口句柄上同步系统深浅色模式并设置 Windows 11 DWM 圆角偏好"""
    if sys.platform != "win32" or not hwnd:
        return

    # 1. 设置深色/浅色沉浸式系统适配（适配系统控件与窗口状态）
    try:
        dark_val = c_int(1 if is_dark else 0)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(
            hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, byref(dark_val), sizeof(dark_val)
        )
    except Exception:
        pass

    # 2. Windows 11 (Build 22000+) 设置窗口圆角偏好
    try:
        pref = c_int(DWMWCP_DONOTROUND if is_maximized else DWMWCP_ROUND)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(
            hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, byref(pref), sizeof(pref)
        )
    except Exception:
        pass
