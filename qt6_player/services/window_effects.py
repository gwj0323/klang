import sys
import ctypes
from ctypes import c_int, byref, sizeof, Structure

# Windows DWM Window Attributes
DWMWA_USE_IMMERSIVE_DARK_MODE = 20
DWMWA_WINDOW_CORNER_PREFERENCE = 33
DWMWA_SYSTEMBACKDROP_TYPE = 38
DWMWA_MICA_EFFECT = 1029  # For Windows 11 21H2 (Build 22000)

# Window Corner Preferences
DWMWCP_DEFAULT = 0
DWMWCP_DONOTROUND = 1
DWMWCP_ROUND = 2
DWMWCP_ROUNDSMALL = 3

# System Backdrop Types (Windows 11 22H2+ / Build 22621+)
DWMSBT_AUTO = 0
DWMSBT_NONE = 1             # Solid / None
DWMSBT_MAINWINDOW = 2       # Mica (Desktop Wallpaper Blended)
DWMSBT_TRANSIENTWINDOW = 3  # Acrylic (Desktop & Window Blur)
DWMSBT_TABBEDWINDOW = 4     # Mica Alt / Tabbed


class MARGINS(Structure):
    _fields_ = [
        ("cxLeftWidth", c_int),
        ("cxRightWidth", c_int),
        ("cyTopHeight", c_int),
        ("cyBottomHeight", c_int),
    ]


def extend_frame_into_client(hwnd: int) -> bool:
    """扩展 DWM 帧边框至客户区全域，使毛玻璃和云母材质能直接投射至整个无边框窗口。"""
    if sys.platform != "win32" or not hwnd:
        return False
    try:
        m = MARGINS(-1, -1, -1, -1)
        res = ctypes.windll.dwmapi.DwmExtendFrameIntoClientArea(hwnd, byref(m))
        return res == 0
    except Exception:
        return False


def set_window_backdrop(hwnd: int, material: str = "mica", is_dark: bool = False) -> bool:
    """
    配置原生 Windows 11 DWM 材质特效 (Mica / Acrylic / Solid)。
    material: 'mica' | 'acrylic' | 'solid'
    """
    if sys.platform != "win32" or not hwnd:
        return False

    success = False

    # 1. 设置系统深浅色匹配，确保 DWM 内部的光影色彩明暗与 Klang 统一
    try:
        dark_val = c_int(1 if is_dark else 0)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(
            hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, byref(dark_val), sizeof(dark_val)
        )
    except Exception:
        pass

    # 2. 映射材质枚举
    mat_lower = (material or "mica").lower()
    if mat_lower == "acrylic":
        backdrop_val = c_int(DWMSBT_TRANSIENTWINDOW)
    elif mat_lower in ("solid", "none"):
        backdrop_val = c_int(DWMSBT_NONE)
    elif mat_lower == "mica_alt":
        backdrop_val = c_int(DWMSBT_TABBEDWINDOW)
    else:
        backdrop_val = c_int(DWMSBT_MAINWINDOW)

    # 3. 优先尝试 Windows 11 22H2+ 原生 DWMWA_SYSTEMBACKDROP_TYPE (38)
    try:
        res = ctypes.windll.dwmapi.DwmSetWindowAttribute(
            hwnd, DWMWA_SYSTEMBACKDROP_TYPE, byref(backdrop_val), sizeof(backdrop_val)
        )
        if res == 0:
            success = True
    except Exception:
        pass

    # 4. 若为首发版 Windows 11 21H2 (Build 22000)，回退尝试 DWMWA_MICA_EFFECT (1029)
    if not success and mat_lower == "mica":
        try:
            mica_val = c_int(1)
            res = ctypes.windll.dwmapi.DwmSetWindowAttribute(
                hwnd, DWMWA_MICA_EFFECT, byref(mica_val), sizeof(mica_val)
            )
            if res == 0:
                success = True
        except Exception:
            pass

    # 5. 材质生效时扩展 frame
    if mat_lower != "solid":
        extend_frame_into_client(hwnd)

    return success


def apply_window_system_theme_and_corners(
    hwnd: int,
    is_dark: bool = False,
    is_maximized: bool = False,
    backdrop_material: str = "mica",
) -> None:
    if sys.platform != "win32" or not hwnd:
        return

    # 设置深浅色主题
    try:
        dark_val = c_int(1 if is_dark else 0)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(
            hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, byref(dark_val), sizeof(dark_val)
        )
    except Exception:
        pass

    # 设置圆角风格
    try:
        pref = c_int(DWMWCP_DONOTROUND if is_maximized else DWMWCP_ROUND)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(
            hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, byref(pref), sizeof(pref)
        )
    except Exception:
        pass

    # 应用背景材质
    try:
        set_window_backdrop(hwnd, material=backdrop_material, is_dark=is_dark)
    except Exception:
        pass
