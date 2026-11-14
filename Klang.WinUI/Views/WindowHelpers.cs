using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Klang_WinUI.Views;

/// <summary>桌面歌词 / 迷你窗口共用的无边框悬浮窗辅助：置顶、DPI 缩放、记住位置、拖动。</summary>
internal static class WindowHelpers
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>设为置顶、无边框、不可缩放、不出现在任务栏 / Alt+Tab，并按 DIP 尺寸缩放到当前 DPI。</summary>
    public static void SetupOverlay(Window window, int widthDip, int heightDip)
    {
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        // 有边框、无标题栏：系统会给这种窗口画圆角和细边框
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        window.AppWindow.SetPresenter(presenter);
        try { window.AppWindow.IsShownInSwitchers = false; } catch { }

        var hwnd = WindowNative.GetWindowHandle(window);
        DipSizes.AddOrUpdate(window, new DipSize(widthDip, heightDip));
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

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);

    // 每个悬浮窗按 DIP 设定的尺寸：拖到缩放比例不同的显示器后按新比例重新设定
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> DipSizes = new();

    private sealed record DipSize(int Width, int Height);

    /// <summary>
    /// 按住卡片空白处拖动整个窗口；点在按钮 / 滑块上时不拖动。
    /// 以前用“假装按住标题栏”（WM_NCLBUTTONDOWN + HTCAPTION）交给系统拖动：在 WinUI 3 里窗口要等松手后才跳过去，
    /// 位移还会按显示缩放比例放大（125% 时多走 25%），右键也能拖，单击卡片会被吞掉。
    /// 现在自己处理：只认左键，移动超过几个像素才算拖动，按物理光标的位移移动窗口，并保证窗口留在屏幕内。
    /// </summary>
    public static void EnableDrag(Window window, UIElement dragSurface)
    {
        var aw = window.AppWindow;
        bool pressed = false, moving = false;
        POINT start = default;
        PointInt32 origin = default;

        dragSurface.PointerPressed += (s, e) =>
        {
            var pt = e.GetCurrentPoint(dragSurface);
            if (!pt.Properties.IsLeftButtonPressed) return;
            if (IsInteractive(e.OriginalSource, dragSurface)) return;
            if (!GetCursorPos(out start)) return;
            origin = aw.Position;
            pressed = true;
            moving = false;
            dragSurface.CapturePointer(e.Pointer);
        };

        dragSurface.PointerMoved += (s, e) =>
        {
            if (!pressed || !GetCursorPos(out var cur)) return;
            int dx = cur.X - start.X, dy = cur.Y - start.Y;
            if (!moving && Math.Abs(dx) + Math.Abs(dy) < 4) return;   // 小于 4 像素当作点击
            moving = true;
            aw.Move(KeepOnScreen(aw, new PointInt32(origin.X + dx, origin.Y + dy), cur));
            e.Handled = true;
        };

        void End(object s, PointerRoutedEventArgs e)
        {
            if (!pressed) return;
            pressed = false;
            dragSurface.ReleasePointerCapture(e.Pointer);
            if (moving) FitToDpi(window);
            moving = false;
        }
        dragSurface.PointerReleased += End;
        dragSurface.PointerCaptureLost += (s, e) => { pressed = false; moving = false; };
    }

    private static bool IsInteractive(object source, UIElement surface)
    {
        if (source is not DependencyObject dep) return false;
        for (var cur = dep; cur != null && !ReferenceEquals(cur, surface); cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase || cur is Slider || cur is TextBox) return true;
        }
        return false;
    }

    /// <summary>窗口至少保留一部分在光标所在显示器的工作区内：横向留 80 像素，顶部不超出（否则抓不回来）。</summary>
    private static PointInt32 KeepOnScreen(AppWindow aw, PointInt32 pos, POINT cursor)
    {
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        if (area == null) return pos;
        var work = area.WorkArea;
        int w = aw.Size.Width, h = aw.Size.Height;
        int x = Math.Clamp(pos.X, work.X - w + 80, work.X + work.Width - 80);
        int y = Math.Clamp(pos.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - Math.Min(h, 48)));
        return new PointInt32(x, y);
    }

    /// <summary>拖到缩放比例不同的显示器后，按新比例重新设定窗口尺寸（以窗口中心为准）。</summary>
    private static void FitToDpi(Window window)
    {
        if (!DipSizes.TryGetValue(window, out var o) || o is not DipSize dip) return;
        var hwnd = WindowNative.GetWindowHandle(window);
        double scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
        var want = new SizeInt32((int)(dip.Width * scale), (int)(dip.Height * scale));
        var aw = window.AppWindow;
        if (Math.Abs(aw.Size.Width - want.Width) <= 2 && Math.Abs(aw.Size.Height - want.Height) <= 2) return;
        var c = new PointInt32(aw.Position.X + aw.Size.Width / 2, aw.Position.Y + aw.Size.Height / 2);
        aw.MoveAndResize(new RectInt32(c.X - want.Width / 2, c.Y - want.Height / 2, want.Width, want.Height));
    }
}
