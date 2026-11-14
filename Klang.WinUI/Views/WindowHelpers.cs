using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Klang_WinUI.Views;

/// <summary>桌面歌词 / 迷你窗口共用的无边框悬浮窗辅助：置顶、DPI 缩放、记住位置、拖动。</summary>
internal static class WindowHelpers
{
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>设为置顶、无边框、不可缩放、不出现在任务栏 / Alt+Tab，并按 DIP 尺寸缩放到当前 DPI。</summary>
    public static void SetupOverlay(Window window, int widthDip, int heightDip, bool frame = true)
    {
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        // frame=false 时完全无边框（任务栏播放条用）：有边框的窗口即使用 SetBorderAndTitleBar
        // 事后关掉，DWM 仍会在外圈留一条 1px 的白色边框。
        presenter.SetBorderAndTitleBar(hasBorder: frame, hasTitleBar: false);
        window.AppWindow.SetPresenter(presenter);
        try { window.AppWindow.IsShownInSwitchers = false; } catch { }

        var hwnd = WindowNative.GetWindowHandle(window);
        double scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
        window.AppWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));

        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    /// <summary>放到保存的位置；位置为空或已不在任何显示器内时，用 fallback 计算默认位置。</summary>
    public static void Place(AppWindow aw, int? x, int? y, Func<RectInt32, SizeInt32, PointInt32> fallback)
    {
        if (x.HasValue && y.HasValue)
        {
            var area = DisplayArea.GetFromPoint(new PointInt32(x.Value + 40, y.Value + 20), DisplayAreaFallback.None);
            if (area != null)
            {
                aw.Move(new PointInt32(x.Value, y.Value));
                return;
            }
        }
        var primary = DisplayArea.GetFromWindowId(aw.Id, DisplayAreaFallback.Primary);
        if (primary != null) aw.Move(fallback(primary.WorkArea, aw.Size));
    }

    /// <summary>窗口被拖动后回调新位置（由调用方写入状态文件）。</summary>
    public static void TrackPosition(AppWindow aw, Action<int, int> onMoved)
    {
        aw.Changed += (s, e) =>
        {
            if (e.DidPositionChange && s.IsVisible) onMoved(s.Position.X, s.Position.Y);
        };
    }

    /// <summary>按住卡片空白处拖动整个窗口；点在按钮 / 滑块上时不拖动。</summary>
    public static void EnableDrag(Window window, UIElement dragSurface)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        dragSurface.PointerPressed += (s, e) =>
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                for (var cur = dep; cur != null && !ReferenceEquals(cur, dragSurface); cur = VisualTreeHelper.GetParent(cur))
                {
                    if (cur is Button || cur is Slider) return;
                }
            }
            ReleaseCapture();
            SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        };
    }
}
