"""
Klang 原生 Windows 11 / 10 视效与窗口材质模块
参考 GitHub 行业标杆开源实现 (zhiyiYo/PyQt-Frameless-Window 与 PyQt-Fluent-Widgets):
- 接入原生 DWM 硬件合成与非客户区扩展机制 (WM_NCCALCSIZE)
- Windows 11 Mica (云母材质, DWMSBT_MAINWINDOW) 质感
- Windows 10/11 Acrylic (亚克力材质, DWMSBT_TRANSIENTWINDOW / ACCENT_ENABLE_ACRYLICBLURBEHIND)
- 标准纯色模式 (Solid, DWMSBT_NONE)
- 保持北欧温润原色底蕴，同时通透显现桌面壁纸与应用背景
"""

import sys
import ctypes
from ctypes import c_int, byref, sizeof, Structure
from ctypes.wintypes import DWORD

# Windows DWM Window Attributes
DWMWA_USE_IMMERSIVE_DARK_MODE = 20
DWMWA_WINDOW_CORNER_PREFERENCE = 33
DWMWA_SYSTEMBACKDROP_TYPE = 38
DWMWA_MICA_EFFECT = 1029

# Window Corner Preferences
DWMWCP_DEFAULT = 0
DWMWCP_DONOTROUND = 1
DWMWCP_ROUND = 2
DWMWCP_ROUNDSMALL = 3

# System Backdrop Types (Windows 11 22H2+ / Build 22621+)
DWMSBT_AUTO = 0
DWMSBT_NONE = 1             # Solid / None
DWMSBT_MAINWINDOW = 2       # Mica
DWMSBT_TRANSIENTWINDOW = 3  # Acrylic
DWMSBT_TABBEDWINDOW = 4     # Mica Alt


def is_win11() -> bool:
    return sys.platform == "win32" and sys.getwindowsversion().build >= 22000


def apply_window_backdrop(
    window,
    material: str = "mica",
    is_dark: bool = False,
    is_maximized: bool = False,
    tint_hex: str = "",
) -> bool:
    """
    为无边框窗口配置原生系统级材质 (Mica / Acrylic / Solid)，并同步圆角与沉浸式深浅色主题。
    结合 PyQt-Frameless-Window 成熟的 WindowEffect 硬件合成引擎与 Win32 DWM 特性。
    """
    if sys.platform != "win32" or not window:
        return False

    try:
        hwnd = int(window.winId()) if hasattr(window, "winId") and window.winId() else int(window)
    except Exception:
        return False

    if not hwnd:
        return False

    mat_lower = (material or "mica").lower()
    dwmapi = ctypes.windll.dwmapi

    # 1. 设置系统深浅色匹配 (DWMWA_USE_IMMERSIVE_DARK_MODE)
    try:
        dark_val = c_int(1 if is_dark else 0)
        dwmapi.DwmSetWindowAttribute(
            hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, byref(dark_val), sizeof(dark_val)
        )
    except Exception:
        pass

    # 2. 设置 Windows 11 原生圆角风格 (DWMWA_WINDOW_CORNER_PREFERENCE)
    if is_win11():
        try:
            pref = c_int(DWMWCP_DONOTROUND if is_maximized else DWMWCP_ROUND)
            dwmapi.DwmSetWindowAttribute(
                hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, byref(pref), sizeof(pref)
            )
        except Exception:
            pass

    # 3. 配置 WindowEffect 背景材质
    we = getattr(window, "windowEffect", None)
    if we is None:
        try:
            from qframelesswindow import WindowEffect
            we = WindowEffect()
        except Exception:
            we = None

    if mat_lower in ("solid", "none"):
        # 纯色模式：清除背景特效与 DWM backdrop
        if we:
            try:
                we.removeBackgroundEffect(hwnd)
            except Exception:
                pass
        try:
            backdrop_none = c_int(DWMSBT_NONE)
            dwmapi.DwmSetWindowAttribute(
                hwnd, DWMWA_SYSTEMBACKDROP_TYPE, byref(backdrop_none), sizeof(backdrop_none)
            )
        except Exception:
            pass
        return True

    elif mat_lower == "acrylic":
        # 亚克力模式：使用 DWM 硬件亚克力磨砂毛玻璃，叠加北欧柔和微调底色
        tint = tint_hex
        if not tint:
            # 格式 RRGGBBAA:
            # 深色模式: #121614 75% alpha (C0)
            # 浅色模式: #F7F8F6 65% alpha (A0)
            tint = "121614C0" if is_dark else "F7F8F6A0"
        else:
            tint = tint.lstrip("#")
            if len(tint) == 6:
                tint += "C0" if is_dark else "A0"

        if we:
            try:
                we.setAcrylicEffect(hwnd, gradientColor=tint, enableShadow=True)
            except Exception:
                pass

        # 如果是 Windows 11 22H2+，同时注入 DWMSBT_TRANSIENTWINDOW (3)
        try:
            backdrop_acrylic = c_int(DWMSBT_TRANSIENTWINDOW)
            dwmapi.DwmSetWindowAttribute(
                hwnd, DWMWA_SYSTEMBACKDROP_TYPE, byref(backdrop_acrylic), sizeof(backdrop_acrylic)
            )
        except Exception:
            pass
        return True

    else:
        # Mica 云母材质 (默认)
        if we:
            try:
                we.setMicaEffect(hwnd, isDarkMode=is_dark, isAlt=False)
            except Exception:
                pass

        # 针对 Windows 11 22H2+ 设置 DWMSBT_MAINWINDOW (2)
        try:
            backdrop_mica = c_int(DWMSBT_MAINWINDOW)
            dwmapi.DwmSetWindowAttribute(
                hwnd, DWMWA_SYSTEMBACKDROP_TYPE, byref(backdrop_mica), sizeof(backdrop_mica)
            )
        except Exception:
            pass
        return True


def apply_window_system_theme_and_corners(
    hwnd: int,
    is_dark: bool = False,
    is_maximized: bool = False,
    backdrop_material: str = "mica",
    tint_hex: str = "",
) -> None:
    """兼容旧接口调用方式"""
    apply_window_backdrop(
        hwnd,
        material=backdrop_material,
        is_dark=is_dark,
        is_maximized=is_maximized,
        tint_hex=tint_hex,
    )


def set_window_backdrop(
    hwnd: int,
    material: str = "mica",
    is_dark: bool = False,
    tint_hex: str = "",
) -> bool:
    """兼容旧接口调用方式"""
    return apply_window_backdrop(
        hwnd,
        material=material,
        is_dark=is_dark,
        is_maximized=False,
        tint_hex=tint_hex,
    )
