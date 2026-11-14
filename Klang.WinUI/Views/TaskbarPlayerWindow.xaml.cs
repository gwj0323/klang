using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Klang_WinUI.Models;
using Klang_WinUI.Services;
using WinRT.Interop;

namespace Klang_WinUI.Views;

/// <summary>
/// 叠在任务栏上的播控条。停靠模式下按任务栏真实控件占用矩形寻找空白（不足则缩成紧凑版），
/// 拖动时跟随鼠标，靠近空白处出现虚线停靠提示；松手停靠或回位，不保留自由悬浮。窗口 owner 关联到任务栏，
/// Explorer 重启后会在下一次轮询里重新关联。
/// </summary>
public sealed partial class TaskbarPlayerWindow : Window
{
    private readonly IntPtr _owner;
    private readonly IntPtr _hwnd;
    private readonly DispatcherQueueTimer _timer;
    private readonly TaskbarTransparentBackdrop _transparentBackdrop;
    private readonly TaskbarEnvironmentService _environment = new();
    private readonly TaskbarSnapPreviewWindow _preview = new();
    private bool _enabled, _closed, _dragging, _moved, _hover;
    private string _trace = "";
    private string? _coverPath;

    private TrackItem? _track;
    private TaskbarVariant _variant = TaskbarVariant.Normal;
    private ScreenRect _bounds;
    private SnapResult? _snap;
    private SnapStage _dragStage = SnapStage.None;
    private TaskbarEnvironment? _dragEnvironment;
    private TaskbarNative.Point _cursor;
    private int _grabX, _grabY, _width, _height;
    private TaskbarNative.Taskbar? _bar;
    private long _capturedAt;
    private int _misses;
    private bool _pointerInside;
    private long _flashUntil;
    private bool _hidden;
    private bool? _dark;
    private UIElement? _captureSurface;
    private TaskbarNative.Point _dragStart;
    private static AppState State => StateService.Instance.State;

    public event EventHandler? RestoreRequested;
    public event EventHandler? PrevRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? FavoriteRequested;
    public event EventHandler? DismissRequested;

    public TaskbarPlayerWindow(IntPtr owner)
    {
        InitializeComponent();
        _owner = owner;
        _hwnd = WindowNative.GetWindowHandle(this);
        _transparentBackdrop = new TaskbarTransparentBackdrop(_hwnd);
        SystemBackdrop = _transparentBackdrop;
        Title = L.T("任务栏播放条");
        L.Attach(Root, this);
        WindowHelpers.SetupOverlay(this, TaskbarLayout.NormalWidth, TaskbarLayout.Height, frame: false);
        TaskbarNative.MakeNonActivating(_hwnd);
        TaskbarNative.MakePopup(_hwnd);
        int borderResult = TaskbarNative.RemoveWindowBorder(_hwnd);
        TaskbarNative.DisableDwmRounding(_hwnd);
        var styles = TaskbarNative.Styles(_hwnd);
        TaskbarTrace.Write($"window style=0x{styles.Style:X} ex=0x{styles.ExStyle:X} dwmBorder=0x{borderResult:X}");
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(400);
        _timer.Tick += (_, _) =>
        {
            try { RefreshLayout(); }
            catch (Exception ex) { App.LogError("Taskbar layout", ex); }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _preview.Dispose();
            _environment.Dispose();
        };
    }

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = true;
        SetHover(true);
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = false;
        // 刚停靠时会让底色亮一下告诉用户"我在这儿"，这段期间先别急着熄灭。
        if (Environment.TickCount64 < _flashUntil) return;
        SetHover(false);
    }

    /// <summary>松手后让播放条的位置可见：底色和拖动手柄亮 1.2 秒再熄灭。</summary>
    private void Flash()
    {
        _flashUntil = Environment.TickCount64 + 1200;
        SetHover(true);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1300);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (!_pointerInside && Environment.TickCount64 >= _flashUntil) SetHover(false);
        };
        timer.Start();
    }

    /// <summary>静止时完全透明；悬停时 4% 白底 + 4% 描边、拖动手柄与关闭按钮淡入（100ms，和汽水一致）。</summary>
    private void SetHover(bool hover)
    {
        if (_hover == hover) return;
        _hover = hover;
        Fade(CardSurface, hover ? 1 : 0);
        Fade(CompactSurface, hover ? 1 : 0);
        Fade(DragHandle, hover ? 1 : 0);
        Fade(CompactDrag, hover ? 1 : 0);
        Fade(CloseButton, hover ? 1 : 0);
        CloseButton.IsHitTestVisible = hover;
    }

    private static void Fade(UIElement element, double opacity)
    {
        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private Brush ResourceBrush(string key) => (Brush)Root.Resources[key];

    private void Action_Entered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button != FavoriteButton || _track?.IsFavorite != true)
            button.Foreground = ResourceBrush("TaskbarIconHoverBrush");
        if (button.Tag as string == "play") button.Background = ResourceBrush("TaskbarPlaybackHoverBrush");
    }

    private void Action_Exited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button == FavoriteButton) UpdateFavorite();
        else button.Foreground = ResourceBrush("TaskbarIconBrush");
        if (button.Tag as string == "play") button.Background = ResourceBrush("TaskbarPlaybackBrush");
    }

    private void ApplyPalette(bool dark)
    {
        if (_dark == dark) return;
        _dark = dark;
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        Windows.UI.Color Ink(byte alpha) => dark ? Windows.UI.Color.FromArgb(alpha, 255, 255, 255)
            : Windows.UI.Color.FromArgb(alpha, 0, 0, 0);
        void Color(string key, Windows.UI.Color value) => ((SolidColorBrush)Root.Resources[key]).Color = value;
        Color("TaskbarIconBrush", Ink(179));
        Color("TaskbarIconHoverBrush", Ink(255));
        Color("TaskbarMutedBrush", Ink(128));
        Color("TaskbarLyricBrush", Ink(230));
        Color("TaskbarPlaybackBrush", Ink(18));
        Color("TaskbarPlaybackHoverBrush", Ink(31));
        CardSurface.Background = CompactSurface.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(10, 255, 255, 255) : Windows.UI.Color.FromArgb(77, 255, 255, 255));
        CardSurface.BorderBrush = CompactSurface.BorderBrush = new SolidColorBrush(
            Windows.UI.Color.FromArgb((byte)(dark ? 10 : 20), 255, 255, 255));
        UpdateFavorite();
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled) SetHover(false);
        if (enabled)
        {
            RefreshLayout();
            _timer.Start();
        }
        else
        {
            CancelDrag();
            _timer.Stop();
            _preview.Hide();
            if (AppWindow.IsVisible) AppWindow.Hide();
        }
    }

    private static DockSide PreferredSide =>
        string.Equals(State.TaskbarPlayerSide, "left", StringComparison.OrdinalIgnoreCase) ? DockSide.Left : DockSide.Right;

    #region 停靠 / 悬浮布局

    private void RefreshLayout()
    {
        if (_closed || !_enabled) return;
        if (_dragging)
        {
            // 指针释放事件偶尔会丢：左键已经抬起却还在 dragging，就自己收尾，别把播放条卡在拖动状态。
            if ((TaskbarNative.GetAsyncKeyState(0x1B) & 0x8000) != 0) CancelDrag();
            else if (!TaskbarNative.LeftButtonDown()) CommitDrag();
            return;
        }
        var environment = Match(State.TaskbarPlayerMonitor);
        if (environment == null)
        {
            // Explorer 偶尔一次查询失败不该让播放条闪没，连续 3 次（约 1.2 秒）才隐藏。
            Trace(_environment.Snapshot().Count == 0 ? "taskbar query failed (grace)" : "no taskbar on the saved monitor (grace)");
            MissOrHide();
            return;
        }
        // UIA 可能慢于任务栏动画；隐藏/位置变化不能等其缓存过期。
        var liveBar = TaskbarNative.FindAll().FirstOrDefault(b => b.Handle == environment.Bar.Handle);
        if (liveBar == null || !TaskbarLayout.CanDock(liveBar.Bounds, liveBar.Monitor, liveBar.Scale))
        {
            HidePlayer();
            return;
        }
        if (liveBar.Bounds != environment.Bar.Bounds) { HidePlayer(); return; }
        Associate(environment.Bar);
        ApplyPalette(environment.Dark);
        _bar = environment.Bar;
        if (TaskbarNative.FullscreenOn(environment.Bar, _hwnd) || TaskbarNative.ShellPopupOver(_bounds))
        {
            Trace("hide: fullscreen foreground window");
            HidePlayer();
            return;
        }

        var target = TaskbarLayout.SelectTarget(environment.Bar.Bounds, environment.Bar.Monitor,
            environment.Blanks, environment.Bar.Scale, PreferredSide);
        if (target == null)
        {
            Trace($"no dock target (grace) blanks={environment.Blanks.Count} bar={environment.Bar.Bounds}");
            MissOrHide();
            return;
        }
        _misses = 0;
        if (!string.Equals(State.TaskbarPlayerMonitor, environment.Bar.Device, StringComparison.Ordinal))
            State.TaskbarPlayerMonitor = environment.Bar.Device;
        Place(target.Variant, target.Bounds);
        Trace($"docked {target.Variant}/{target.Side} at {target.Bounds} blanks={environment.Blanks.Count} scale={environment.Bar.Scale:0.##}");
    }

    private void MissOrHide()
    {
        if (++_misses < 3) return;
        _misses = 0;
        HidePlayer();
    }

    private void Trace(string state)
    {
        if (state == _trace) return;
        _trace = state;
        TaskbarTrace.Write(state);
    }

    private TaskbarEnvironment? Match(string? device)
    {
        var list = _environment.Snapshot();
        if (list.Count == 0) return null;
        if (!string.IsNullOrEmpty(device))
        {
            var hit = list.FirstOrDefault(e => string.Equals(e.Bar.Device, device, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        var ownerDisplay = TaskbarNative.DisplayFor(null, _owner);
        return list.FirstOrDefault(e => string.Equals(e.Bar.Device, ownerDisplay.Device, StringComparison.OrdinalIgnoreCase))
            ?? list.OrderByDescending(e => e.Bar.Bounds.Width).First();
    }

    private void Place(TaskbarVariant variant, ScreenRect bounds)
    {
        if (variant != _variant)
        {
            _variant = variant;
            NormalLayout.Visibility = variant == TaskbarVariant.Normal ? Visibility.Visible : Visibility.Collapsed;
            CompactLayout.Visibility = variant == TaskbarVariant.Normal ? Visibility.Collapsed : Visibility.Visible;
        }
        bool resized = bounds.Width != _bounds.Width || bounds.Height != _bounds.Height;
        _bounds = bounds;
        RefreshBackdrop(bounds);
        TaskbarNative.PlaceOnTop(_hwnd, bounds);
        if (resized) TaskbarNative.ClipRounded(_hwnd, bounds.Height / 40.0);
        if (!AppWindow.IsVisible)
        {
            AppWindow.Show(false);
            if (_hidden) { _hidden = false; TaskbarTrace.Write("player shown"); }
        }
    }

    /// <summary>
    /// 播放条静止时要"看起来透明"：在左右两侧各取一个任务栏像素，插值成横向渐变当底色，
    /// 每次布局轮询（400ms）顺手刷新，任务栏换主题/变色后一秒内跟上。
    /// </summary>
    private void RefreshBackdrop(ScreenRect bounds)
    {
        if (_dragging || _closed) return;
        if (_transparentBackdrop.IsTransparent)
        {
            CardBackdrop.Background = CompactBackdrop.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            return;
        }
        long now = Environment.TickCount64;
        if (now - _capturedAt < 1000) return;
        try
        {
            var edges = _bar is { } bar ? TaskbarNative.SampleTaskbarEdges(bar, bounds) : null;
            ApplyBackdrop(edges);
        }
        catch (Exception ex)
        {
            TaskbarTrace.Write($"backdrop failed: {ex}");
        }
        _capturedAt = now;
    }

    private void ApplyBackdrop((uint Left, uint Right)? edges)
    {
        var border = _variant == TaskbarVariant.Normal ? CardBackdrop : CompactBackdrop;
        if (edges is not { } pair)
        {
            border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 38, 36));
            return;
        }
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5),
        };
        gradient.GradientStops.Add(new GradientStop { Offset = 0, Color = FromColorRef(pair.Left) });
        gradient.GradientStops.Add(new GradientStop { Offset = 1, Color = FromColorRef(pair.Right) });
        border.Background = gradient;
    }

    private static Windows.UI.Color FromColorRef(uint value) => Windows.UI.Color.FromArgb(255,
        (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));

    private void HidePlayer()
    {
        _preview.Hide();
        if (!AppWindow.IsVisible) return;
        if (!_hidden) { _hidden = true; TaskbarTrace.Write("player hidden"); }
        AppWindow.Hide();
    }

    /// <summary>Explorer 重启或任务栏句柄变化时重新关联 owner（不移动、不激活）。</summary>
    private void Associate(TaskbarNative.Taskbar bar)
    {
        TaskbarNative.SetOwner(_hwnd, bar.Handle);
    }

    #endregion

    #region 拖动与吸附

    private void Drag_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (_closed || !_enabled || _dragging) return;
        if (sender is not FrameworkElement card) return;
        if (IsInsideButton(e.OriginalSource, card)) return;
        if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
        if (!TaskbarNative.GetCursorPos(out var cursor)) return;
        if (!card.CapturePointer(e.Pointer))
        {
            TaskbarTrace.Write("drag: pointer capture refused");
            return;
        }
        _dragging = true;
        _captureSurface = card;
        _dragStart = cursor;
        _moved = false;
        _snap = null;
        _dragStage = SnapStage.None;
        _dragEnvironment = null;
        _cursor = cursor;
        _width = _bounds.Width;
        _height = _bounds.Height;
        _grabX = cursor.X - _bounds.Left;
        _grabY = cursor.Y - _bounds.Top;
        TaskbarTrace.Write($"drag start at {_bounds} grab=({_grabX},{_grabY})");
        e.Handled = true;
    }

    /// <summary>拖动面覆盖整张卡片；按在按钮上时交给按钮，不启动拖动。</summary>
    private static bool IsInsideButton(object? source, FrameworkElement card)
    {
        if (source is not DependencyObject current) return false;
        while (current != null && !ReferenceEquals(current, card))
        {
            if (current is Button) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void Drag_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !TaskbarNative.GetCursorPos(out var cursor)) return;
        _cursor = cursor;
        if (!_moved && Math.Abs(cursor.X - _dragStart.X) + Math.Abs(cursor.Y - _dragStart.Y) < 4 * TaskbarNative.WindowScale(_hwnd)) return;
        if (!_moved) SetDragVisual(true);
        _moved = true;
        var free = new ScreenRect(cursor.X - _grabX, cursor.Y - _grabY,
            cursor.X - _grabX + _width, cursor.Y - _grabY + _height);
        _bounds = free;
        TaskbarNative.PlaceOnTop(_hwnd, free);
        UpdateSnap(cursor, free);
        e.Handled = true;
    }

    private void UpdateSnap(TaskbarNative.Point cursor, ScreenRect free)
    {
        var environment = EnvironmentAt(cursor);
        _dragEnvironment = environment;
        if (environment == null) { _snap = null; _preview.Hide(); return; }
        Associate(environment.Bar);
        var target = TaskbarLayout.SelectTarget(environment.Bar.Bounds, environment.Bar.Monitor,
            environment.Blanks, environment.Bar.Scale,
            free.CenterX < environment.Bar.Bounds.CenterX ? DockSide.Left : DockSide.Right);
        var snap = TaskbarDockController.Resolve(free, environment.Bar.Bounds, target, environment.Bar.Scale);
        _snap = snap;
        if (snap.Stage != _dragStage)
        {
            _dragStage = snap.Stage;
            TaskbarTrace.Write($"drag stage={snap.Stage} target={(snap.Target == null ? "none" : $"{snap.Target.Variant}/{snap.Target.Side} {snap.Target.Bounds}")}");
        }
        try
        {
            if (snap.Target != null) _preview.Show(snap.Target, snap.Stage, environment.Bar, environment.Dark);
            else _preview.Hide();
        }
        catch (Exception ex) { _preview.Hide(); App.LogError("Taskbar snap preview", ex); }
    }

    private TaskbarEnvironment? EnvironmentAt(TaskbarNative.Point cursor)
    {
        var device = TaskbarNative.DisplayAt(cursor).Device;
        var hit = Match(device);
        return hit;
    }

    private void Drag_Released(object sender, PointerRoutedEventArgs e) => CommitDrag();

    private void Drag_CaptureLost(object sender, PointerRoutedEventArgs e) => CancelDrag();

    private void CommitDrag()
    {
        if (!_dragging) return;
        bool moved = _moved;
        if (moved && TaskbarNative.GetCursorPos(out var released)) UpdateSnap(released, _bounds);
        var snap = _snap;          // StopDrag 会清空 _snap，先取出来
        StopDrag();
        if (!moved) { RefreshLayout(); return; }
        if (snap is { Stage: SnapStage.Confirm, Target: { } target })
        {
            State.TaskbarPlayerSide = target.Side == DockSide.Left ? "left" : "right";
            if (_dragEnvironment != null) State.TaskbarPlayerMonitor = _dragEnvironment.Bar.Device;
            Place(target.Variant, target.Bounds);
            StateService.Instance.RequestSave();
        }
        else
        {
            // 没落在任务栏空白处：不接受“自由悬浮”，回到原来的停靠位。
            TaskbarTrace.Write("drag released off the taskbar; re-docking");
        }
        RefreshLayout();
        Flash();
    }

    private void CancelDrag()
    {
        if (!_dragging) return;
        bool moved = _moved;
        StopDrag();
        RefreshLayout();
        if (moved) Flash();
    }

    private void SetDragVisual(bool dragging)
    {
        // 拖离任务栏时不能再拿任务栏的像素当底，换成半透明深底。
        CardBackdrop.Visibility = dragging ? Visibility.Collapsed : Visibility.Visible;
        CompactBackdrop.Visibility = dragging ? Visibility.Collapsed : Visibility.Visible;
        DragSurface.Visibility = dragging ? Visibility.Visible : Visibility.Collapsed;
        CompactDragSurface.Visibility = dragging ? Visibility.Visible : Visibility.Collapsed;
        _capturedAt = 0;
    }

    private void StopDrag()
    {
        _dragging = false;
        SetDragVisual(false);
        _snap = null;
        _captureSurface?.ReleasePointerCaptures();
        _captureSurface = null;
        _preview.Hide();
    }

    #endregion

    #region 播放状态绑定

    public void UpdateTrack(TrackItem? track)
    {
        _track = track;
        TitleText.Text = track == null ? "Klang" : string.IsNullOrWhiteSpace(track.Artist)
            ? track.Title : $"{track.Title} - {track.Artist}";
        ToolTipService.SetToolTip(TitleText, track == null ? "Klang" : $"{track.Title} · {track.Artist}");
        UpdateLyric(null);
        UpdateFavorite();
        string? cover = track?.CoverPath;
        if (_coverPath == cover) return;
        _coverPath = cover;
        bool exists = !string.IsNullOrEmpty(cover) && File.Exists(cover);
        try
        {
            var image = exists ? new BitmapImage(new Uri(cover!)) { DecodePixelWidth = 84 } : null;
            CoverBrush.ImageSource = image;
            CompactCoverBrush.ImageSource = image;
        }
        catch
        {
            exists = false;
            CoverBrush.ImageSource = null;
            CompactCoverBrush.ImageSource = null;
        }
        CoverFallback.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        CompactCoverFallback.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
    }


    public void UpdateLyric(string? lyric) => DetailText.Text = string.IsNullOrWhiteSpace(lyric)
        ? (string.IsNullOrWhiteSpace(_track?.Artist) ? "Klang" : _track.Artist) : lyric;

    public void UpdatePlayState(bool playing)
    {
        PlayIcon.Visibility = CompactPlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PauseIcon.Visibility = CompactPauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
    }

    public void UpdateFavorite()
    {
        FavoriteButton.IsEnabled = _track != null && !_track.IsOnline;
        FavoriteButton.Foreground = _track?.IsFavorite == true
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 33, 79)) : ResourceBrush("TaskbarIconBrush");
        FavoriteIcon.Fill = _track?.IsFavorite == true ? FavoriteButton.Foreground : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ToolTipService.SetToolTip(FavoriteButton, L.T(_track?.IsOnline == true
            ? "云端曲目暂不支持“喜欢”" : _track?.IsFavorite == true ? "取消喜欢" : "喜欢"));
    }

    private void Restore_Click(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke(this, EventArgs.Empty);
    private void Prev_Click(object sender, RoutedEventArgs e) => PrevRequested?.Invoke(this, EventArgs.Empty);
    private void Next_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);
    private void PlayPause_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);
    private void Favorite_Click(object sender, RoutedEventArgs e) => FavoriteRequested?.Invoke(this, EventArgs.Empty);
    private void Dismiss_Click(object sender, RoutedEventArgs e) => DismissRequested?.Invoke(this, EventArgs.Empty);

    #endregion
}

/// <summary>任务栏播控条的轻量诊断日志：只记录状态变化，便于排查“为什么没显示”。</summary>
internal static class TaskbarTrace
{
    private static readonly string FilePath = System.IO.Path.Combine(
        StateService.ResolveDataDirectory(), "taskbar-bar.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 256 * 1024) info.Delete();
            File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }
}
