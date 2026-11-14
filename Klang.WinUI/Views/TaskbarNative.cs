using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Klang_WinUI.Services;

namespace Klang_WinUI.Views;

/// <summary>只查询 Explorer 的任务栏；播放条是本进程的独立窗口，不修改或注入 Explorer。</summary>
internal static class TaskbarNative
{
    internal sealed record Taskbar(IntPtr Handle, IntPtr MonitorHandle, string Device,
        ScreenRect Bounds, ScreenRect Monitor, ScreenRect Work, double Scale);
    internal sealed record Display(IntPtr Handle, string Device, ScreenRect Bounds, ScreenRect Work, double Scale);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly ScreenRect Screen => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr param);
    private delegate bool EnumMonitorProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumMonitorProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    private static string ClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }

    public static IReadOnlyList<Taskbar> FindAll()
    {
        var bars = new List<Taskbar>();
        EnumWindows((hwnd, _) =>
        {
            if (ClassName(hwnd) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return true;
            if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var bounds)) return true;
            var monitor = MonitorFromWindow(hwnd, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            bars.Add(new(hwnd, monitor, info.Device, bounds.Screen, info.Monitor.Screen,
                info.Work.Screen, Math.Max(1, GetDpiForWindow(hwnd) / 96.0)));
            return true;
        }, IntPtr.Zero);

        return bars;
    }

    private static Display ReadDisplay(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (!GetMonitorInfo(monitor, ref info)) throw new System.ComponentModel.Win32Exception();
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
        return new(monitor, info.Device, info.Monitor.Screen, info.Work.Screen, TaskbarLayout.ValidScale(scale));
    }

    public static Display DisplayAt(Point point) => ReadDisplay(MonitorFromPoint(point, 2));
    public static Display DisplayFor(string? device, IntPtr fallback)
    {
        IntPtr found = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (ReadDisplay(monitor).Device != device) return true;
            found = monitor;
            return false;
        }, IntPtr.Zero);
        return ReadDisplay(found != IntPtr.Zero ? found : MonitorFromWindow(fallback, 2));
    }

    public static IReadOnlyList<ScreenRect> NativeOccupants(IntPtr taskbar)
    {
        var result = new List<ScreenRect>();
        EnumChildWindows(taskbar, (hwnd, _) =>
        {
            // 不把延伸到空白区的任务列表容器本身当成按钮。
            if (ClassName(hwnd) is "TrayNotifyWnd" or "TrayClockWClass" or "TrayShowDesktopButtonWClass" or "Start")
                if (IsWindowVisible(hwnd) && GetWindowRect(hwnd, out var rect)) result.Add(rect.Screen);
            return true;
        }, IntPtr.Zero);
        // 其他播放器也可能是 taskbar-owned 的独立顶层窗口；它们不一定出现在任务栏的 UIA 子树中。
        // 将其整个矩形排除，避免 Klang 与用户放在右侧的汽水播控相互覆盖。
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == taskbar || GetWindow(hwnd, 4) != taskbar || !IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint process);
            if (process != Environment.ProcessId && GetWindowRect(hwnd, out var rect)) result.Add(rect.Screen);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static bool ShellPopupOver(ScreenRect bounds)
    {
        bool overlap = false;
        EnumWindows((hwnd, _) =>
        {
            if (ClassName(hwnd) is not ("TaskListThumbnailWnd" or "NotifyIconOverflowWindow")) return true;
            overlap = IsWindowVisible(hwnd) && GetWindowRect(hwnd, out var rect) && rect.Screen.Intersects(bounds);
            return !overlap;
        }, IntPtr.Zero);
        return overlap;
    }

    public static bool FullscreenOn(Taskbar bar, IntPtr overlay)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == overlay) return false;
        // 最大化窗口的窗口矩形也覆盖整个显示器（DWM 把不可见边框算进去了），那不是全屏。
        if (IsZoomed(foreground)) return false;
        if (ClassName(foreground) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (MonitorFromWindow(foreground, 2) != bar.MonitorHandle || !GetWindowRect(foreground, out var rect)) return false;
        var m = bar.Monitor;
        return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
    }

    public static void MakeNonActivating(IntPtr hwnd)
    {
        const int index = -20; // GWL_EXSTYLE
        const long flags = 0x08000000L | 0x00000080L; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
        // 一并清掉 WS_EX_APPWINDOW / WS_EX_WINDOWEDGE：后者会让系统在窗口外圈画一条凸起的边，
        // 在任务栏上看着就是一圈白边（无边框窗口也照样画）。
        const long clear = 0x00040000L | 0x00000100L; // WS_EX_APPWINDOW | WS_EX_WINDOWEDGE
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hwnd, index, new IntPtr((GetWindowLongPtr64(hwnd, index).ToInt64() | flags) & ~clear));
        else
            SetWindowLong32(hwnd, index, (int)((GetWindowLong32(hwnd, index) | flags) & ~clear));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037); // FRAMECHANGED | NOACTIVATE | NOZORDER | NOMOVE | NOSIZE
    }

    public static bool SetOwner(IntPtr hwnd, IntPtr owner)
    {
        if (!IsWindow(hwnd) || (owner != IntPtr.Zero && !IsWindow(owner))) return false;
        if (GetWindow(hwnd, 4) == owner) return true;
        IntPtr previous = IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, -8, owner)
            : new IntPtr(SetWindowLong32(hwnd, -8, owner.ToInt32()));
        return previous != IntPtr.Zero || Marshal.GetLastWin32Error() == 0;
    }

    /// <summary>把窗口的<b>客户区</b>摆到给定矩形上（自动补偿非客户区边框）。</summary>
    public static void PlaceOnTop(IntPtr hwnd, ScreenRect clientTarget)
    {
        int dx = 0, dy = 0, extraWidth = 0, extraHeight = 0;
        if (GetClientRect(hwnd, out var client) && GetWindowRect(hwnd, out var window))
        {
            var origin = new Point();
            if (ClientToScreen(hwnd, ref origin))
            {
                dx = origin.X - window.Left;
                dy = origin.Y - window.Top;
            }
            extraWidth = (window.Right - window.Left) - (client.Right - client.Left);
            extraHeight = (window.Bottom - window.Top) - (client.Bottom - client.Top);
        }
        // HWND_TOPMOST，SWP_NOACTIVATE；点击播放键不会抢走正在使用的应用的焦点。
        SetWindowPos(hwnd, new IntPtr(-1), clientTarget.Left - dx, clientTarget.Top - dy,
            clientTarget.Width + extraWidth, clientTarget.Height + extraHeight, 0x0010);
    }

    /// <summary>去掉 DWM 画在窗口外圈的 1px 边框，卡片自身的圆角和描边就够了。</summary>
    public static int RemoveWindowBorder(IntPtr hwnd)
    {
        try
        {
            int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            return DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref none, sizeof(int));
        }
        catch (Exception ex) { return ex.HResult; }
    }

    /// <summary>
    /// 在播放条左右两侧各取一个任务栏像素（同一行）：播放条静止时要看起来"透"，
    /// 用这两点插值成横向渐变当背景。不能拿任务栏窗口自己渲染的图（PrintWindow），
    /// 那样少了 DWM 的透明/着色，颜色会明显偏。
    /// 返回左右两个 COLORREF；取不到返回 null。
    /// </summary>
    public static (uint Left, uint Right)? SampleTaskbarEdges(Taskbar bar, ScreenRect rect)
    {
        var bounds = bar.Bounds;
        int y = rect.CenterY;
        if (y < bounds.Top || y >= bounds.Bottom) y = bounds.CenterY;
        int leftX = Math.Clamp(rect.Left - 6, bounds.Left, bounds.Right - 1);
        int rightX = Math.Clamp(rect.Right + 5, bounds.Left, bounds.Right - 1);
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        try
        {
            uint left = GetPixel(screen, leftX, y);
            uint right = GetPixel(screen, rightX, y);
            if (left == 0xFFFFFFFF || right == 0xFFFFFFFF) return null;
            return (left, right);
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    /// <summary>
    /// 把窗口彻底改成无边框弹出式窗口：只剩 WS_EX_WINDOWEDGE 时，系统仍会留 1-2px 非客户区，
    /// 客户区整体右下偏移，看起来就是"图标没居中"。
    /// </summary>
    public static void MakePopup(IntPtr hwnd)
    {
        const long remove = 0x00C00000L | 0x00040000L | 0x00080000L | 0x00020000L | 0x00010000L | 0x00800000L;
        const long popup = 0x80000000L;
        long style = IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, -16).ToInt64() : GetWindowLong32(hwnd, -16);
        long updated = (style & ~remove) | popup;
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, -16, new IntPtr(updated));
        else SetWindowLong32(hwnd, -16, (int)updated);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037); // FRAMECHANGED|NOACTIVATE|NOZORDER|NOMOVE|NOSIZE
    }

    /// <summary>左键当前是否按着（用来兜住丢失的 PointerReleased）。</summary>
    public static bool LeftButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    /// <summary>窗口当前 DPI 缩放（96 = 1.0）。</summary>
    public static double WindowScale(IntPtr hwnd) => Math.Max(1, GetDpiForWindow(hwnd)) / 96.0;

    /// <summary>客户区左上角相对窗口矩形的偏移（像素）。有非客户区边框时是 (2,2) 之类。</summary>
    public static (int X, int Y) ClientOffset(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var window)) return (0, 0);
        var origin = new Point();
        if (!ClientToScreen(hwnd, ref origin)) return (0, 0);
        return (origin.X - window.Left, origin.Y - window.Top);
    }

    /// <summary>关掉 DWM 自带的圆角（它会沿着圆角画一条亮边），改用窗口区域自己裁圆角。</summary>
    public static void DisableDwmRounding(IntPtr hwnd)
    {
        try
        {
            int none = 1; // DWMWCP_DONOTROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref none, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// 圆角区域按<b>客户区</b>裁剪：系统会在窗口最外圈画一条 1px 亮边（连无边框窗口都画），
    /// 把客户区以外的部分整个裁掉，既去掉亮边，也让内容正好落在目标矩形上。
    /// </summary>
    public static void ClipRounded(IntPtr hwnd, double scale)
    {
        if (!GetClientRect(hwnd, out var client) || !GetWindowRect(hwnd, out var window)) return;
        var origin = new Point();
        if (!ClientToScreen(hwnd, ref origin)) return;
        int left = origin.X - window.Left, top = origin.Y - window.Top;
        double factor = TaskbarLayout.ValidScale(scale);
        int radius = Math.Max(2, (int)Math.Round(8 * factor));
        var region = CreateRoundRectRgn(left, top, left + (client.Right - client.Left),
            top + (client.Bottom - client.Top), radius * 2, radius * 2);
        if (region != IntPtr.Zero && SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
    }

    /// <summary>诊断用：读窗口样式，确认是否还带着标题栏/可调边框。</summary>
    public static (long Style, long ExStyle) Styles(IntPtr hwnd) => IntPtr.Size == 8
        ? (GetWindowLongPtr64(hwnd, -16).ToInt64(), GetWindowLongPtr64(hwnd, -20).ToInt64())
        : (GetWindowLong32(hwnd, -16), GetWindowLong32(hwnd, -20));
}
