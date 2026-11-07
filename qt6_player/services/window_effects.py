"""
Klang 原生 Windows 11 / 10 视效与窗口材质模块
参考 GitHub 行业标杆开源实现 (zhiyiYo/PyQt-Frameless-Window 与 zhiyiYo/PyQt-Fluent-Widgets):
- 基于 AcrylicWindow (Qt 6.10+ NoTitleBarBackgroundHint + DwmEnableBlurBehindWindow) 修复 #206 黑底问题
- 通过移除 WS_SYSMENU 与设置 SetWindowThemeAttribute (WTNCA_NODRAWCAPTION | WTNCA_NOSYSMENU)
  彻底消除 Qt 6.10+ NoTitleBarBackgroundHint 触发的 Win32 原生右上角三键重叠问题
- Windows 11 Mica (云母材质, DWMSBT_MAINWINDOW + ACCENT_ENABLE_HOSTBACKDROP)
- Windows 10/11 Acrylic (亚克力材质, WCA_ACCENT_POLICY + ACCENT_ENABLE_ACRYLICBLURBEHIND)
- 标准纯色模式 (Solid, ACCENT_DISABLED + DWMSBT_NONE)
"""

import sys
import ctypes
from ctypes import Structure, c_int, c_uint, byref, sizeof, pointer

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
DWMSBT_TRANSIENTWINDOW = 3  # System Acrylic
DWMSBT_TABBEDWINDOW = 4     # Mica Alt

# uxtheme WindowThemeNonClientAttributes
WTA_NONCLIENT = 1
WTNCA_NODRAWCAPTION = 0x00000001
WTNCA_NODRAWICON = 0x00000002
WTNCA_NOSYSMENU = 0x00000004
WTNCA_NOMIRRORHELP = 0x00000008
WTNCA_VALIDBITS = 0x0000000F


class _WTA_OPTIONS(Structure):
    _fields_ = [("dwFlags", c_uint), ("dwMask", c_uint)]


def is_win11() -> bool:
    return sys.platform == "win32" and sys.getwindowsversion().build >= 22000


def suppress_native_caption_buttons(hwnd: int) -> None:
    """
    彻底屏蔽 Windows DWM 在 Qt 6.10+ NoTitleBarBackgroundHint 模式下绘制的原生右上角最小化/最大化/关闭按钮，
    防止其与自定义 WinUITitleBar 按钮发生上下重叠。
    """
    if sys.platform != "win32" or not hwnd:
        return
    try:
        hwnd = int(hwnd)
        # 1. 设置 uxtheme WTA_NONCLIENT 禁止绘制原生标题栏文字、图标与系统菜单按钮
        opts = _WTA_OPTIONS(WTNCA_VALIDBITS, WTNCA_VALIDBITS)
        ctypes.windll.uxtheme.SetWindowThemeAttribute(
            hwnd, WTA_NONCLIENT, byref(opts), sizeof(opts)
        )
    except Exception:
        pass

    try:
        import win32gui
        import win32con
        # 2. 移除 GWL_STYLE 中的 WS_SYSMENU (DWM 只要检测到 WS_SYSMENU 就会在扩展帧右上角画原生三键)
        style = win32gui.GetWindowLong(hwnd, win32con.GWL_STYLE)
        if style & win32con.WS_SYSMENU:
            new_style = (
                (style & ~win32con.WS_SYSMENU)
                | win32con.WS_MINIMIZEBOX
                | win32con.WS_MAXIMIZEBOX
                | win32con.WS_CAPTION
                | win32con.CS_DBLCLKS
                | win32con.WS_THICKFRAME
            )
            win32gui.SetWindowLong(hwnd, win32con.GWL_STYLE, new_style)
            win32gui.SetWindowPos(
                hwnd,
                None,
                0,
                0,
                0,
                0,
                win32con.SWP_NOMOVE
                | win32con.SWP_NOSIZE
                | win32con.SWP_NOZORDER
                | win32con.SWP_NOACTIVATE
                | win32con.SWP_FRAMECHANGED,
            )
    except Exception:
        pass


def _reset_wca_accent_attr(we) -> None:
    """
    修复 PyQt-Frameless-Window WindowsWindowEffect.setMicaEffect 在 isDarkMode=True 时
    将 winCompAttrData.Attribute 修改为 WCA_USEDARKMODECOLORS (26) 后未还原为
    WCA_ACCENT_POLICY (19) 导致后续 removeBackgroundEffect 失效的问题。
    """
    try:
        from qframelesswindow.windows.c_structures import WINDOWCOMPOSITIONATTRIB
        we.winCompAttrData.Attribute = WINDOWCOMPOSITIONATTRIB.WCA_ACCENT_POLICY.value
        we.winCompAttrData.SizeOfData = sizeof(we.accentPolicy)
        we.winCompAttrData.Data = pointer(we.accentPolicy)
    except Exception:
        pass


def _sync_wca_dark_mode(we, hwnd: int, is_dark: bool) -> None:
    """同步 Win32 WCA_USEDARKMODECOLORS (26) 状态并还原 winCompAttrData.Attribute"""
    try:
        from qframelesswindow.windows.c_structures import WINDOWCOMPOSITIONATTRIB
        we.winCompAttrData.Attribute = WINDOWCOMPOSITIONATTRIB.WCA_USEDARKMODECOLORS.value
        we.accentPolicy.AccentState = 1 if is_dark else 0
        we.SetWindowCompositionAttribute(int(hwnd), pointer(we.winCompAttrData))
    except Exception:
        pass
    finally:
        _reset_wca_accent_attr(we)


def apply_window_backdrop(
    window,
    material: str = "mica",
    is_dark: bool = False,
    is_maximized: bool = False,
    tint_hex: str = "",
) -> bool:
    """
    为 AcrylicWindow 架构的无边框主窗口动态配置原生系统级材质 (Mica / Acrylic / Solid)，
    并同步圆角、屏蔽原生重叠三键、同步深浅色主题。
    """
    if sys.platform != "win32" or not window:
        return False

    try:
        hwnd = int(window.winId()) if hasattr(window, "winId") and window.winId() else int(window)
    except Exception:
        return False

    if not hwnd:
        return False

    # 始终确保隐藏 Win32 原生标题栏三键，杜绝与自定义标题栏重叠
    suppress_native_caption_buttons(hwnd)

    mat_lower = (material or "mica").lower()
    dwmapi = ctypes.windll.dwmapi

    # 1. 设置系统深浅色匹配 (DWMWA_USE_IMMERSIVE_DARK_MODE = 20)
    try:
        dark_val = c_int(1 if is_dark else 0)
        dwmapi.DwmSetWindowAttribute(
            hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, byref(dark_val), sizeof(dark_val)
        )
    except Exception:
        pass

    # 2. 设置 Windows 11 原生圆角与阴影策略 (DWMWA_WINDOW_CORNER_PREFERENCE = 33)
    if is_win11():
        try:
            pref = c_int(DWMWCP_DONOTROUND if is_maximized else DWMWCP_ROUND)
            dwmapi.DwmSetWindowAttribute(
                hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, byref(pref), sizeof(pref)
            )
        except Exception:
            pass

    # 3. 获取窗口绑定的 WindowsWindowEffect 实例
    we = getattr(window, "windowEffect", None)
    if we is None:
        try:
            from qframelesswindow import WindowEffect
            we = WindowEffect()
        except Exception:
            we = None

    if we is not None:
        _sync_wca_dark_mode(we, hwnd, is_dark)

    # 4. 按目标材质执行干净的状态切换
    if mat_lower in ("solid", "none"):
        # 标准纯色模式：清除 WCA 背景特效 + 禁用 DWM SystemBackdrop + 保留窗口阴影
        if we is not None:
            try:
                _reset_wca_accent_attr(we)
                we.removeBackgroundEffect(hwnd)
            except Exception:
                pass
        if is_win11():
            try:
                if sys.getwindowsversion().build < 22523:
                    dwmapi.DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, byref(c_int(0)), 4)
                else:
                    dwmapi.DwmSetWindowAttribute(
                        hwnd, DWMWA_SYSTEMBACKDROP_TYPE, byref(c_int(DWMSBT_NONE)), 4
                    )
            except Exception:
                pass
        if we is not None:
            try:
                we.addShadowEffect(hwnd)
            except Exception:
                pass
        return True

    elif mat_lower == "acrylic":
        # 亚克力模式：先关闭 DWM SystemBackdrop (防止 Mica 覆盖 Acrylic)，再启用 WCA 硬件亚克力毛玻璃
        if is_win11():
            try:
                if sys.getwindowsversion().build < 22523:
                    dwmapi.DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, byref(c_int(0)), 4)
                else:
                    dwmapi.DwmSetWindowAttribute(
                        hwnd, DWMWA_SYSTEMBACKDROP_TYPE, byref(c_int(DWMSBT_NONE)), 4
                    )
            except Exception:
                pass

        # 计算 RRGGBBAA 格式的北欧亚克力混合底色
        clean_hex = (tint_hex or "").lstrip("#").upper()
        if len(clean_hex) == 6:
            # 浅色模式 68% 不透明度 (AD)，深色模式 75% 不透明度 (C0)
            gradient_color = clean_hex + ("C0" if is_dark else "AD")
        elif len(clean_hex) == 8:
            gradient_color = clean_hex
        else:
            gradient_color = "181C1AC0" if is_dark else "F4F1EAAD"

        if we is not None:
            try:
                _reset_wca_accent_attr(we)
                we.enableBlurBehindWindow(hwnd)
                we.setAcrylicEffect(hwnd, gradientColor=gradient_color, enableShadow=True)
                if is_win11():
                    we.addShadowEffect(hwnd)
            except Exception:
                pass
        return True

    else:
        # Win11 云母材质 (Mica，默认)：若在 Win11 上使用原生 Mica，Win10 自动回退至 Acrylic
        if is_win11():
            if we is not None:
                try:
                    _reset_wca_accent_attr(we)
                    we.removeBackgroundEffect(hwnd)
                    we.enableBlurBehindWindow(hwnd)
                    we.setMicaEffect(hwnd, isDarkMode=is_dark, isAlt=False)
                    _reset_wca_accent_attr(we)
                except Exception:
                    pass
        else:
            # Windows 10 回退至柔和 Acrylic
            if we is not None:
                try:
                    _reset_wca_accent_attr(we)
                    we.enableBlurBehindWindow(hwnd)
                    fallback_tint = "181C1AD8" if is_dark else "F4F1EAD8"
                    we.setAcrylicEffect(hwnd, gradientColor=fallback_tint, enableShadow=True)
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
