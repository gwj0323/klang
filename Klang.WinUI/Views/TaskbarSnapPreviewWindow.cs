using System;
using System.Runtime.InteropServices;
using Klang_WinUI.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace Klang_WinUI.Views;

/// <summary>
/// 独立的逐像素透明提示窗。使用 Win2D → 预乘 BGRA → UpdateLayeredWindow，
/// 不依赖 WinUI XAML 的 Transparent 来实现窗口外层透明，且整个窗口穿透鼠标。
/// </summary>
internal sealed class TaskbarSnapPreviewWindow : IDisposable
{
    private IntPtr _hwnd;
    private (int Width, int Height, SnapStage Stage, bool Dark, string Language)? _painted;
    private static readonly WndProc Callback = WindowProc;
    private const string ClassName = "Klang.TaskbarSnapPreview";
    private static bool _registered;

    public void Show(DockTarget target, SnapStage stage, TaskbarNative.Taskbar bar, bool dark)
    {
        EnsureWindow();
        if (!TaskbarNative.SetOwner(_hwnd, bar.Handle)) { Hide(); return; }
        var bounds = target.Bounds;
        var key = (bounds.Width, bounds.Height, stage, dark, L.Code);
        if (_painted != key)
        {
            Paint(bounds, stage, dark, bar.Scale);
            _painted = key;
        }
        TaskbarNative.PlaceOnTop(_hwnd, bounds);
        ShowWindow(_hwnd, 4); // SW_SHOWNOACTIVATE
    }

    public void Hide() { if (TaskbarNative.IsWindow(_hwnd)) ShowWindow(_hwnd, 0); }

    private void EnsureWindow()
    {
        if (TaskbarNative.IsWindow(_hwnd)) return;
        var instance = GetModuleHandle(null);
        if (!_registered)
        {
            var cls = new WindowClass { Instance = instance, Name = ClassName,
                Procedure = Marshal.GetFunctionPointerForDelegate(Callback) };
            if (RegisterClass(ref cls) == 0 && Marshal.GetLastWin32Error() != 1410)
                throw new System.ComponentModel.Win32Exception();
            _registered = true;
        }
        // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE
        _hwnd = CreateWindowEx(0x080800A0, ClassName, "", 0x80000000,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        _painted = null;
    }

    private void Paint(ScreenRect bounds, SnapStage stage, bool dark, double scale)
    {
        int width = bounds.Width, height = bounds.Height;
        using var image = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using (var ds = image.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(0, 0, 0, 0));
            var ink = dark ? Color.FromArgb(230, 245, 245, 245) : Color.FromArgb(230, 40, 40, 40);
            float inset = (float)scale, radius = (float)(8 * scale);
            if (stage == SnapStage.Confirm)
                ds.FillRoundedRectangle(inset, inset, width - 2 * inset, height - 2 * inset,
                    radius, radius, dark ? Color.FromArgb(80, 45, 45, 45) : Color.FromArgb(100, 245, 245, 245));
            using var stroke = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            ds.DrawRoundedRectangle(inset, inset, width - 2 * inset, height - 2 * inset,
                radius, radius, ink, (float)scale, stroke);
            using var format = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = (float)(11 * scale),
                HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
            ds.DrawText(L.T(stage == SnapStage.Confirm ? "松手固定播控" : "将播控移动到此处"),
                new Windows.Foundation.Rect(0, 0, width, height), ink, format);
        }
        var pixels = image.GetPixelBytes();
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Header = new BitmapHeader { Size = 40, Width = width,
            Height = -height, Planes = 1, BitCount = 32, SizeImage = (uint)pixels.Length } };
        IntPtr bitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero || memory == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            old = SelectObject(memory, bitmap);
            var position = new NativePoint { X = bounds.Left, Y = bounds.Top };
            var size = new NativePoint { X = width, Y = height };
            var origin = new NativePoint();
            var blend = new Blend { Alpha = 255, Format = 1 };
            if (!UpdateLayeredWindow(_hwnd, screen, ref position, ref size, memory, ref origin, 0, ref blend, 2))
                throw new System.ComponentModel.Win32Exception();
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memory, old);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Dispose() { if (TaskbarNative.IsWindow(_hwnd)) DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l) => message switch
    {
        0x0084 => new IntPtr(-1), // WM_NCHITTEST → HTTRANSPARENT
        0x0021 => new IntPtr(3),  // WM_MOUSEACTIVATE → MA_NOACTIVATE
        _ => DefWindowProc(hwnd, message, w, l)
    };

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style; public IntPtr Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? Menu; public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Operation, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapHeader Header; public uint Colors; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extended, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dest, ref NativePoint position, ref NativePoint size, IntPtr source, ref NativePoint origin, uint key, ref Blend blend, uint flags);
}
