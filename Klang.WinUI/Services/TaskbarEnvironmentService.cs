using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using Klang_WinUI.Views;
using Microsoft.Win32;

namespace Klang_WinUI.Services;

internal sealed record TaskbarEnvironment(TaskbarNative.Taskbar Bar, IReadOnlyList<ScreenRect> Blanks, bool Dark);

/// <summary>
/// UIA 仅在专用 MTA 后台线程查询。UI 不等待跨进程调用；快照按实际查询耗时设置有限有效期，
/// 即使 Explorer 的可访问性提供程序卡住，也不会不断创建查询线程或阻塞播放。
/// </summary>
internal sealed class TaskbarEnvironmentService : IDisposable
{
    private readonly ManualResetEvent _stop = new(false);
    private readonly object _gate = new();
    private IReadOnlyList<TaskbarEnvironment> _latest = Array.Empty<TaskbarEnvironment>();
    private long _updated;
    private long _validFor = 2000;
    private volatile bool _disposed;
    private readonly int _processId = Environment.ProcessId;

    public TaskbarEnvironmentService()
    {
        var thread = new Thread(Poll) { IsBackground = true, Name = "Klang taskbar geometry" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public IReadOnlyList<TaskbarEnvironment> Snapshot()
    {
        lock (_gate) return Environment.TickCount64 - _updated <= _validFor ? _latest : Array.Empty<TaskbarEnvironment>();
    }

    private void Poll()
    {
        try
        {
            do
            {
                try
                {
                    bool dark = IsTaskbarDark();
                    long started = Environment.TickCount64;
                    var list = TaskbarNative.FindAll().Select(bar => new TaskbarEnvironment(bar, QueryBlanks(bar), dark)).ToArray();
                    long elapsed = Environment.TickCount64 - started;
                    lock (_gate)
                    {
                        _latest = list;
                        _updated = Environment.TickCount64;
                        // 某些 Explorer/UIA 提供程序一次查询就需数秒。固定 2 秒会让正常查询期间反复闪隐。
                        // 仍有 15 秒硬上限；任务栏是否可见、全屏与 popup 遮挡另由 UI 线程即时核对。
                        _validFor = Math.Clamp(elapsed * 2 + 1000, 2000, 15000);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Taskbar discovery: {ex.Message}");
                    lock (_gate) _latest = Array.Empty<TaskbarEnvironment>();
                }
            } while (!_stop.WaitOne(500) && !_disposed);
        }
        finally { _stop.Dispose(); }
    }

    private IReadOnlyList<ScreenRect> QueryBlanks(TaskbarNative.Taskbar bar)
    {
        if (!TaskbarLayout.CanDock(bar.Bounds, bar.Monitor, bar.Scale)) return Array.Empty<ScreenRect>();
        // 整次 UIA 查询失败或没有识别到控件时，不把未知区域当成安全空白。
        try
        {
            var occupied = TaskbarNative.NativeOccupants(bar.Handle).ToList();
            var root = AutomationElement.FromHandle(bar.Handle);
            var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.IsOffscreenProperty);
            request.Add(AutomationElement.ProcessIdProperty);
            var controls = new OrCondition(new[] { ControlType.Button, ControlType.ListItem, ControlType.MenuItem,
                ControlType.Text, ControlType.Image, ControlType.Custom, ControlType.CheckBox, ControlType.RadioButton,
                ControlType.ComboBox, ControlType.Edit, ControlType.Hyperlink, ControlType.Slider }
                .Select(type => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, type)).ToArray());
            AutomationElementCollection elements;
            using (request.Activate())
                elements = root.FindAll(TreeScope.Descendants, controls);
            if (elements.Count > 1024) return Array.Empty<ScreenRect>();
            int recognized = 0;
            foreach (AutomationElement element in elements)
            {
                var e = element.Cached;
                if (e.ProcessId == _processId || e.IsOffscreen) continue;
                var r = e.BoundingRectangle;
                if (r.IsEmpty || !double.IsFinite(r.Left) || !double.IsFinite(r.Top) || r.Width <= 0 || r.Height <= 0) continue;
                var bounds = new ScreenRect((int)Math.Floor(r.Left), (int)Math.Floor(r.Top),
                    (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));
                if (!bounds.Intersects(bar.Bounds)) continue;
                var type = e.ControlType;
                // 排除会铺满任务栏的容器；保留按钮、文字、托盘与自定义交互控件。
                if (type == ControlType.Pane || type == ControlType.Window || type == ControlType.ToolBar
                    || type == ControlType.List || type == ControlType.Group) continue;
                if (type == ControlType.Custom && bounds.Width >= bar.Bounds.Width * 0.8) continue;
                occupied.Add(bounds);
                if (type == ControlType.Button || type == ControlType.ListItem || type == ControlType.MenuItem) recognized++;
            }
            return recognized == 0 ? Array.Empty<ScreenRect>() : TaskbarLayout.FindBlanks(bar.Bounds, occupied, bar.Scale);
        }
        catch { return Array.Empty<ScreenRect>(); }
    }

    private static bool IsTaskbarDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is not int value || value == 0;
        }
        catch { return true; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Set();
        }
    }
}
