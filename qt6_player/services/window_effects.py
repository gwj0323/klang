import sys
import ctypes
from ctypes import c_int, byref, sizeof, Structure, POINTER
from ctypes.wintypes import DWORD, ULONG

# Windows Window Long Constants
GWL_EXSTYLE = -20
WS_EX_LAYERED = 0x00080000
SWP_NOSIZE = 0x0001
SWP_NOMOVE = 0x0002
SWP_NOZORDER = 0x0004
SWP_FRAMECHANGED = 0x0020

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

# SetWindowCompositionAttribute Accent States
ACCENT_DISABLED = 0
ACCENT_ENABLE_GRADIENT = 1
ACCENT_ENABLE_TRANSPARENTGRADIENT = 2
ACCENT_ENABLE_BLURBEHIND = 3
ACCENT_ENABLE_ACRYLICBLURBEHIND = 4
ACCENT_ENABLE_HOSTBACKDROP = 5
WCA_ACCENT_POLICY = 19


class MARGINS(Structure):
    _fields_ = [
        ("cxLeftWidth", c_int),
        ("cxRightWidth", c_int),
        ("cyTopHeight", c_int),
        ("cyBottomHeight", c_int),
    ]


class ACCENT_POLICY(Structure):
    _fields_ = [
        ("AccentState", DWORD),
        ("AccentFlags", DWORD),
        ("GradientColor", DWORD),
        ("AnimationId", DWORD),
    ]


class WINDOWCOMPOSITIONATTRIBDATA(Structure):
    _fields_ = [
        ("Attribute", DWORD),
        ("Data", POINTER(ACCENT_POLICY)),
        ("SizeOfData", ULONG),
    ]


def to_dw_color(color_hex: str, alpha: int = 160) -> int:
    """将十六进制颜色打包为 Windows 内存格式 0xAABBGGRR 的 32 位 DWORD。"""
    try:
        c = (color_hex or "").lstrip("#")
        if len(c) >= 6:
            r = int(c[0:2], 16)
            g = int(c[2:4], 16)
            b = int(c[4:6], 16)
        else:
            r, g, b = 247, 248, 246
    except Exception:
        r, g, b = 247, 248, 246
    a = max(0, min(255, int(alpha)))
    return (a << 24) | (b << 16) | (g << 8) | r


def set_composition_accent(hwnd: int, state: int, flags: int = 0, color: int = 0) -> bool:
    """调用 User32 接口 SetWindowCompositionAttribute 设置背景特效策略。"""
    if sys.platform != "win32" or not hwnd:
        return False
    try:
        user32 = ctypes.windll.user32
        func = getattr(user32, "SetWindowCompositionAttribute", None)
        if not func:
            return False
        policy = ACCENT_POLICY(state, flags, color, 0)
        data = WINDOWCOMPOSITIONATTRIBDATA(
            WCA_ACCENT_POLICY,
            ctypes.pointer(policy),
            sizeof(policy),
        )
        return bool(func(hwnd, ctypes.pointer(data)))
    except Exception:
        return False


def remove_layered_style(hwnd: int) -> None:
    """去除 WS_EX_LAYERED，允许 Windows 11 DWM 系统级 Mica 和 Acrylic 特效投射至客户区。"""
    if sys.platform != "win32" or not hwnd:
        return
    try:
        user32 = ctypes.windll.user32
        ex_style = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
        if ex_style & WS_EX_LAYERED:
            user32.SetWindowLongW(hwnd, GWL_EXSTYLE, ex_style & ~WS_EX_LAYERED)
            user32.SetWindowPos(
                hwnd, 0, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED,
            )
    except Exception:
        pass


def restore_layered_style(hwnd: int) -> None:
    """恢复 WS_EX_LAYERED。"""
    if sys.platform != "win32" or not hwnd:
        return
    try:
        user32 = ctypes.windll.user32
        ex_style = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
        if not (ex_style & WS_EX_LAYERED):
            user32.SetWindowLongW(hwnd, GWL_EXSTYLE, ex_style | WS_EX_LAYERED)
            user32.SetWindowPos(
                hwnd, 0, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED,
            )
    except Exception:
        pass


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


def set_window_backdrop(
    hwnd: int,
    material: str = "mica",
    is_dark: bool = False,
    tint_hex: str = "",
) -> bool:
    """
    配置原生 Windows 11 DWM 材质特效 (Mica / Acrylic / Solid)。
    material: 'mica' | 'acrylic' | 'solid'
    """
    if sys.platform != "win32" or not hwnd:
        return False

    mat_lower = (material or "mica").lower()

    # 1. 设置系统深浅色匹配，确保 DWM 内部的光影色彩明暗与 Klang 统一
    try:
        dark_val = c_int(1 if is_dark else 0)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(
            hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, byref(dark_val), sizeof(dark_val)
        )
    except Exception:
        pass

    if mat_lower in ("solid", "none"):
        # 纯色模式：禁用 DWM backdrop 并清除 accent policy
        try:
            backdrop_none = c_int(DWMSBT_NONE)
            ctypes.windll.dwmapi.DwmSetWindowAttribute(
                hwnd, DWMWA_SYSTEMBACKDROP_TYPE, byref(backdrop_none), sizeof(backdrop_none)
            )
        except Exception:
            pass
        set_composition_accent(hwnd, ACCENT_DISABLED, 0, 0)
        return True

    # Mica 或 Acrylic 模式：必须移除 WS_EX_LAYERED，否则 DWM 硬件合成器会直接忽略透明层
    remove_layered_style(hwnd)
    extend_frame_into_client(hwnd)

    if mat_lower == "acrylic":
        # 优先使用 Windows 11 22H2+ DWMSBT_TRANSIENTWINDOW (3)
        try:
            backdrop_acrylic = c_int(DWMSBT_TRANSIENTWINDOW)
            ctypes.windll.dwmapi.DwmSetWindowAttribute(
                hwnd, DWMWA_SYSTEMBACKDROP_TYPE, byref(backdrop_acrylic), sizeof(backdrop_acrylic)
            )
        except Exception:
            pass

        # 同时接入 SetWindowCompositionAttribute 原生亚克力模糊，叠加北欧暖调温润底色
        tint = tint_hex or ("#121614" if is_dark else "#F7F8F6")
        alpha = 155 if is_dark else 135
        gradient_color = to_dw_color(tint, alpha)
        accent_flags = 2 | 0x20 | 0x40 | 0x80 | 0x100
        set_composition_accent(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, accent_flags, gradient_color)
        return True

    else:
        # Mica 云母材质
        success = False
        try:
            backdrop_mica = c_int(DWMSBT_MAINWINDOW)
            res = ctypes.windll.dwmapi.DwmSetWindowAttribute(
                hwnd, DWMWA_SYSTEMBACKDROP_TYPE, byref(backdrop_mica), sizeof(backdrop_mica)
            )
            if res == 0:
                success = True
        except Exception:
            pass

        # Windows 11 21H2 回退尝试
        if not success:
            try:
                mica_val = c_int(1)
                res = ctypes.windll.dwmapi.DwmSetWindowAttribute(
                    hwnd, DWMWA_MICA_EFFECT, byref(mica_val), sizeof(mica_val)
                )
                if res == 0:
                    success = True
            except Exception:
                pass

        # 同时注入 HostBackdrop accent
        set_composition_accent(hwnd, ACCENT_ENABLE_HOSTBACKDROP, 0, 0)
        return success


def apply_window_system_theme_and_corners(
    hwnd: int,
    is_dark: bool = False,
    is_maximized: bool = False,
    backdrop_material: str = "mica",
    tint_hex: str = "",
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
        set_window_backdrop(hwnd, material=backdrop_material, is_dark=is_dark, tint_hex=tint_hex)
    except Exception:
        pass
