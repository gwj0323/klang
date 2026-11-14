using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Klang_WinUI.Views;

/// <summary>
/// 透明合成画刷 + DWM alpha。XAML 的透明背景本身不足以清除 HWND 的黑色底面；
/// 保留 WM_ERASEBKGND 的黑色清除（alpha=0），使任务栏能从文字和按钮之间真正透出。
/// 不修改 Explorer，不使用窗口整体透明度，因此文字与图标保持清晰。
/// </summary>
internal sealed class TaskbarTransparentBackdrop : SystemBackdrop
{
    private const nuint SubclassId = 0x4B4C5442;
    private readonly IntPtr _hwnd;
    private readonly SubclassProcedure _procedure;
    private Windows.UI.Composition.Compositor? _compositor;
    private Windows.UI.Composition.CompositionColorBrush? _brush;
    private bool _hooked;
    public bool IsTransparent { get; private set; }

    public TaskbarTransparentBackdrop(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _procedure = WindowProcedure;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        try
        {
            _hooked = SetWindowSubclass(_hwnd, _procedure, SubclassId, 0);
            if (!_hooked || !EnableAlpha()) return;
            _compositor = new Windows.UI.Composition.Compositor();
            _brush = _compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
            target.SystemBackdrop = _brush;
            IsTransparent = true;
            var dc = GetDC(_hwnd);
            try { Clear(dc); }
            finally { if (dc != IntPtr.Zero) ReleaseDC(_hwnd, dc); }
            TaskbarTrace.Write("backdrop: native per-pixel transparency enabled");
        }
        catch (Exception ex)
        {
            IsTransparent = false;
            App.LogError("Taskbar transparency", ex.Message);
        }
    }

    private bool EnableAlpha()
    {
        var margins = new Margins();
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        var region = CreateRectRgn(-2, -2, -1, -1);
        if (region == IntPtr.Zero) return false;
        try
        {
            var blur = new Blur { Flags = 3, Enabled = true, Region = region };
            return DwmEnableBlurBehindWindow(_hwnd, ref blur) >= 0;
        }
        finally { DeleteObject(region); }
    }

    private void Clear(IntPtr dc)
    {
        if (dc != IntPtr.Zero && GetClientRect(_hwnd, out var rect))
            FillRect(dc, ref rect, GetStockObject(4)); // BLACK_BRUSH；DWM 的透明底面
    }

    private IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr w, IntPtr l, nuint id, nuint data)
    {
        // 原生回调中的异常绝不能穿过 Win32 边界。
        try
        {
            if (message == 0x0014 && IsTransparent) { Clear(w); return new IntPtr(1); }
            if (message == 0x031E && IsTransparent) EnableAlpha(); // WM_DWMCOMPOSITIONCHANGED
        }
        catch (Exception ex) { App.LogError("Taskbar backdrop callback", ex.Message); }
        return DefSubclassProc(hwnd, message, w, l);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        IsTransparent = false;
        if (_hooked) { RemoveWindowSubclass(_hwnd, _procedure, SubclassId); _hooked = false; }
        target.SystemBackdrop = null;
        _brush?.Dispose(); _brush = null;
        _compositor?.Dispose(); _compositor = null;
        base.OnTargetDisconnected(target);
    }

    private delegate IntPtr SubclassProcedure(IntPtr hwnd, uint message, IntPtr w, IntPtr l, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Blur
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Enabled;
        public IntPtr Region;
        [MarshalAs(UnmanagedType.Bool)] public bool TransitionOnMaximized;
    }
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProcedure proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProcedure proc, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref Blur blur);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
