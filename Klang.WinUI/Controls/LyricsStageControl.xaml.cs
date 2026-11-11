using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Controls;

/// <summary>
/// 歌词舞台。滚动与每行的缩放 / 透明度都用合成器动画实现（跑在合成线程，按显示器刷新率输出），
/// 界面线程只在“换句”“换歌”“用户滚轮”时做一次布局与目标计算，所以界面线程再忙也不会让滚动掉帧。
/// </summary>
public sealed partial class LyricsStageControl : UserControl
{
    public ObservableCollection<LyricLine> Lyrics { get; } = new();

    public event EventHandler<double>? SeekRequested;

    private readonly Visual _contentVisual;
    private readonly Compositor _compositor;
    private readonly Dictionary<FrameworkElement, LineState> _lineVisuals = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _settleTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _resumeTimer;

    private int _lastActiveIndex = -2;
    private double _targetT;          // 内容当前的平移目标（Y）
    private bool _isUserScrolling;
    private bool _needsInit;          // 载入新歌词后，等布局完成再初始化各行状态并居中

    private double _lyricFontSize = 22;
    public const int MinLyricFontSize = 14;
    public const int MaxLyricFontSize = 40;

    /// <summary>歌词字号（主歌词，14–40）。修改后立即应用到当前所有歌词行。</summary>
    public int LyricFontSize
    {
        get => (int)_lyricFontSize;
        set
        {
            _lyricFontSize = Math.Clamp(value, MinLyricFontSize, MaxLyricFontSize);
            foreach (var l in Lyrics) l.MainFontSize = _lyricFontSize;
            RequestReinit();
        }
    }

    private string _subMode = "trans";

    /// <summary>副行显示：trans 翻译 | roma 罗马音 | both 两者 | off 关闭。</summary>
    public string SubMode
    {
        get => _subMode;
        set
        {
            _subMode = value;
            foreach (var l in Lyrics) l.SubMode = value;
            RequestReinit();
        }
    }

    /// <summary>保留的兼容属性：固定颜色的上下遮罩已弃用，歌词行本身按距离渐隐。</summary>
    public bool ShowFades
    {
        set { }
    }

    public LyricsStageControl()
    {
        InitializeComponent();
        LyricsItemsControl.ItemsSource = Lyrics;

        _contentVisual = ElementCompositionPreview.GetElementVisual(ContentPanel);
        _compositor = _contentVisual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(ContentPanel, true);

        UpdateEmptyState();
        UiAnimations.AttachFade(ResumeCenterBtn, 6f);
        UiAnimations.AttachFade(EmptyLyricsPanel, 6f);

        // 用户滚动后 2.8 秒自动回到当前句
        _resumeTimer = DispatcherQueue.CreateTimer();
        _resumeTimer.Interval = TimeSpan.FromMilliseconds(2800);
        _resumeTimer.IsRepeating = false;
        _resumeTimer.Tick += (s, e) => ResumeAutoCenter();

        _settleTimer = DispatcherQueue.CreateTimer();
        _settleTimer.Interval = TimeSpan.FromMilliseconds(820);
        _settleTimer.IsRepeating = false;
        _settleTimer.Tick += (s, e) => SettleRaster();
    }

    #region 载入歌词

    private void UpdateEmptyState()
    {
        // 歌词服务在没有歌词时会给一行“暂无歌词”的占位（结束时间 9999）：不当歌词渲染，改显示小号的空状态
        bool empty = Lyrics.Count == 0 || (Lyrics.Count == 1 && Lyrics[0].EndTime >= 9999.0);
        EmptyLyricsPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ContentPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResetState()
    {
        _lastActiveIndex = -2;
        _isUserScrolling = false;
        _resumeTimer.Stop();
        ResumeCenterBtn.Visibility = Visibility.Collapsed;
        _lineVisuals.Clear();
    }

    public void LoadLyrics(string? audioFilePath)
    {
        ResetState();
        Lyrics.Clear();
        foreach (var l in LyricsService.Instance.LoadLyrics(audioFilePath))
        {
            l.MainFontSize = _lyricFontSize;
            l.SubMode = _subMode;
            Lyrics.Add(l);
        }
        UpdateEmptyState();
        RequestReinit();
    }

    /// <summary>直接载入已解析好的歌词（云端歌词）。</summary>
    public void LoadLyricLines(IList<LyricLine> lines)
    {
        ResetState();
        Lyrics.Clear();
        foreach (var l in lines)
        {
            l.MainFontSize = _lyricFontSize;
            l.SubMode = _subMode;
            Lyrics.Add(l);
        }
        UpdateEmptyState();
        RequestReinit();
    }

    /// <summary>布局完成后：给每行建合成层、按当前句设置缩放 / 透明度（不做动画），并立即居中。</summary>
    private void RequestReinit()
    {
        if (_needsInit) return;
        _needsInit = true;

        void Handler(object? s, object e)
        {
            if (Lyrics.Count > 0 && LyricsItemsControl.ContainerFromIndex(Lyrics.Count - 1) is not FrameworkElement) return;
            LyricsItemsControl.LayoutUpdated -= Handler;
            _needsInit = false;
            if (Lyrics.Count == 0) return;
            ApplyLineStates(_lastActiveIndex, animate: false);
            CenterOnActive(animate: false);
        }
        LyricsItemsControl.LayoutUpdated += Handler;
        LyricsItemsControl.InvalidateMeasure();
    }

    #endregion

    #region 播放进度驱动

    public void UpdatePlaybackTime(double currentTimeSeconds)
    {
        if (Lyrics.Count == 0) return;

        int activeIndex = LyricsService.Instance.FindActiveIndex(Lyrics, currentTimeSeconds);

        // 逐字高亮：当前行随时间推进
        if (activeIndex >= 0 && Lyrics[activeIndex].Words is { } words)
        {
            int played = 0;
            while (played < words.Count && words[played].Start <= currentTimeSeconds) played++;
            Lyrics[activeIndex].PlayedWords = played;
        }

        if (activeIndex == _lastActiveIndex) return;
        _lastActiveIndex = activeIndex;

        // 换句：其它行的逐字进度清零
        for (int i = 0; i < Lyrics.Count; i++)
        {
            if (i != activeIndex && Lyrics[i].Words != null) Lyrics[i].PlayedWords = 0;
            Lyrics[i].IsActive = i == activeIndex;
        }

        if (_needsInit) return; // 布局还没好，初始化时会按最新的 _lastActiveIndex 处理
        ApplyLineStates(activeIndex, animate: true);
        if (!_isUserScrolling) CenterOnActive(animate: true);
    }

    #endregion

    #region 每行的缩放 / 透明度（合成层隐式动画 + 栅格化倍数）

    private static (float Scale, float Opacity) StateFor(int dist) => dist switch
    {
        0 => (1.14f, 1.00f),
        1 => (1.00f, 0.50f),
        2 => (0.96f, 0.28f),
        3 => (0.93f, 0.15f),
        _ => (0.90f, 0.08f)
    };

    private const float MaxScale = 1.14f;

    /// <summary>
    /// 一行的合成层状态。
    /// 合成层的 Scale 缩放的是已渲染好的文字纹理，所以要让纹理分辨率跟上：
    /// - 动画期间，纹理按“最大放大倍数”栅格化（UIElement.RasterizationScale），缩放始终是从大图往小缩，不会糊；
    /// - 动画结束后，把栅格化倍数设成这一行自己的目标缩放，纹理和屏幕像素 1:1，静止时边缘最干净，没有非整数倍缩小带来的锯齿。
    /// </summary>
    private sealed class LineState
    {
        public required Visual Visual;
        public required FrameworkElement Container;
        public UIElement? Button;
        public float Target = 1f;
        public bool Settled = true;
    }

    private static void SetRaster(LineState st, double s)
    {
        st.Container.RasterizationScale = s;
        if (st.Button != null) st.Button.RasterizationScale = s;
    }

    /// <summary>取（或创建）一行容器的状态，并挂上“改属性就自动补间”的隐式动画。</summary>
    private LineState LineStateOf(FrameworkElement container)
    {
        if (_lineVisuals.TryGetValue(container, out var st)) return st;

        var v = ElementCompositionPreview.GetElementVisual(container);
        v.CenterPoint = new Vector3((float)(container.ActualWidth / 2), (float)(container.ActualHeight / 2), 0);
        container.SizeChanged += (s, e) =>
            v.CenterPoint = new Vector3((float)(e.NewSize.Width / 2), (float)(e.NewSize.Height / 2), 0);

        // 每行自己的位移（错峰跟随用）；缩放 / 透明度在换句时显式带延迟地启动动画
        ElementCompositionPreview.SetIsTranslationEnabled(container, true);
        v.Properties.InsertVector3("Translation", Vector3.Zero);

        st = new LineState
        {
            Visual = v,
            Container = container,
            Button = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(container, 0) as UIElement
        };
        _lineVisuals[container] = st;
        return st;
    }

    private void ApplyLineStates(int activeIndex, bool animate)
    {
        int center = activeIndex >= 0 ? activeIndex : 0;
        bool anyMoving = false;
        for (int i = 0; i < Lyrics.Count; i++)
        {
            if (LyricsItemsControl.ContainerFromIndex(i) is not FrameworkElement c) continue;
            var st = LineStateOf(c);
            var v = st.Visual;
            var (scale, opacity) = StateFor(Math.Abs(i - center));

            if (!animate)
            {
                // 初始化：直接到位，纹理按自己的缩放 1:1 栅格化
                v.StopAnimation("Scale");
                v.StopAnimation("Opacity");
                SetRaster(st, scale);
                v.Scale = new Vector3(scale, scale, 1f);
                v.Opacity = opacity;
                st.Target = scale;
                st.Settled = true;
                continue;
            }

            if (Math.Abs(scale - st.Target) > 0.001f || !st.Settled)
            {
                SetRaster(st, MaxScale);          // 动画期间只往小缩，不放大
                st.Settled = false;
                anyMoving = true;
            }
            st.Target = scale;
            StartLineAnimation(v, scale, opacity, Math.Abs(i - center));
        }

        if (anyMoving)
        {
            _settleTimer.Stop();
            _settleTimer.Start();
        }
    }

    // ---- 换句动画：错峰 + 非线性 ----
    private const double StaggerMs = 34;     // 每远一行，晚开始 34 毫秒
    private const int MaxStaggerRows = 6;    // 最多错开 6 行

    private CubicBezierEasingFunction MoveEase() =>
        _compositor.CreateCubicBezierEasingFunction(new Vector2(0.20f, 0.90f), new Vector2(0.20f, 1.00f));

    private TimeSpan StaggerFor(int dist) => TimeSpan.FromMilliseconds(StaggerMs * Math.Min(dist, MaxStaggerRows));

    /// <summary>缩放和透明度：从当前值缓出到目标，离当前句越远的行越晚开始。</summary>
    private void StartLineAnimation(Visual v, float scale, float opacity, int dist)
    {
        var ease = MoveEase();
        var delay = StaggerFor(dist);

        var sc = _compositor.CreateVector3KeyFrameAnimation();
        sc.InsertExpressionKeyFrame(0f, "this.StartingValue");
        sc.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), ease);
        sc.Duration = TimeSpan.FromMilliseconds(560);
        sc.DelayTime = delay;
        sc.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Scale", sc);

        var op = _compositor.CreateScalarKeyFrameAnimation();
        op.InsertExpressionKeyFrame(0f, "this.StartingValue");
        op.InsertKeyFrame(1f, opacity, ease);
        op.Duration = TimeSpan.FromMilliseconds(560);
        op.DelayTime = delay;
        op.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Opacity", op);
    }

    /// <summary>每行位移清零（不带动画的定位 / 手动滚动时用）。</summary>
    private void ResetLineOffsets()
    {
        foreach (var st in _lineVisuals.Values)
        {
            st.Visual.StopAnimation("Translation.Y");
            st.Visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
    }

    /// <summary>
    /// 自动居中的“错峰滑行”：内容整体先瞬间移到新位置，再让每一行从原来的视觉位置缓出滑向新位置，
    /// 当前句最先动，离它越远越晚动，形成顺着阅读方向传过去的一道波。
    /// </summary>
    private void GlideTo(double newT, int activeIndex)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        newT = Math.Round(newT * scale) / scale;
        double delta = newT - _targetT;
        _targetT = newT;

        _contentVisual.StopAnimation("Translation.Y");
        _contentVisual.Properties.InsertVector3("Translation", new Vector3(0f, (float)newT, 0f));
        if (Math.Abs(delta) < 0.5) return;

        int center = activeIndex >= 0 ? activeIndex : 0;
        var ease = MoveEase();
        foreach (var kv in _lineVisuals)
        {
            int idx = LyricsItemsControl.IndexFromContainer(kv.Key);
            if (idx < 0) continue;

            var a = _compositor.CreateScalarKeyFrameAnimation();
            a.SetScalarParameter("d", (float)-delta);
            a.InsertExpressionKeyFrame(0f, "this.StartingValue + d");   // 内容跳了 delta，这行先抵消，视觉上没动
            a.InsertKeyFrame(1f, 0f, ease);
            a.Duration = TimeSpan.FromMilliseconds(680);
            a.DelayTime = StaggerFor(Math.Abs(idx - center));
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            kv.Value.Visual.StartAnimation("Translation.Y", a);
        }
    }

    /// <summary>动画都结束了：每行的栅格化倍数改成自己的目标缩放，静止时 1:1。</summary>
    private void SettleRaster()
    {
        foreach (var st in _lineVisuals.Values)
        {
            if (st.Settled) continue;
            SetRaster(st, st.Target);
            st.Settled = true;
        }
    }

    #endregion
    #region 弹簧居中与用户滚动

    private bool TryGetLineCenter(int index, out double center)
    {
        center = 0;
        if (index < 0 || index >= Lyrics.Count) return false;
        if (LyricsItemsControl.ContainerFromIndex(index) is not FrameworkElement c || c.ActualHeight <= 0) return false;
        try
        {
            var p = c.TransformToVisual(ContentPanel).TransformPoint(new Point(0, 0));
            center = p.Y + c.ActualHeight / 2.0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把当前句滚到视口正中。animate=true 用合成器弹簧（跟随刷新率），false 直接定位。</summary>
    private void CenterOnActive(bool animate)
    {
        if (Lyrics.Count == 0 || Viewport.ActualHeight <= 0) return;
        int idx = _lastActiveIndex >= 0 ? _lastActiveIndex : 0;
        if (!TryGetLineCenter(idx, out double c)) return;
        double target = Viewport.ActualHeight / 2.0 - c;
        if (animate) GlideTo(target, idx);
        else
        {
            ResetLineOffsets();
            MoveTo(target, false, period: 0f, damping: 1f);
        }
    }

    private void MoveTo(double t, bool animate, float period, float damping)
    {
        // 停下来的位置对齐到整数物理像素，否则文字落在半个像素上会发糊
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        t = Math.Round(t * scale) / scale;
        _targetT = t;
        if (!animate)
        {
            _contentVisual.StopAnimation("Translation.Y");
            _contentVisual.Properties.InsertVector3("Translation", new Vector3(0f, (float)t, 0f));
            return;
        }

        // 用“先快后慢”的缓出曲线，而不是弹簧：弹簧起步速度为零，前一百多毫秒几乎不动，会有迟滞感；
        // 缓出曲线一开始就以最大速度走，换句的瞬间就能看到动，再柔和地停下。
        // 从当前位置出发（StartingValue），中途被打断也不会跳。
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
        var slide = _compositor.CreateScalarKeyFrameAnimation();
        slide.InsertExpressionKeyFrame(0f, "this.StartingValue");
        slide.InsertKeyFrame(1f, (float)t, ease);
        slide.Duration = TimeSpan.FromSeconds(period);
        _contentVisual.StartAnimation("Translation.Y", slide);
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        if (!_needsInit) CenterOnActive(animate: false);
    }

    private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (Lyrics.Count == 0) return;
        double delta = e.GetCurrentPoint(Viewport).Properties.MouseWheelDelta;

        // 允许滚动的范围：第一句居中 ~ 最后一句居中
        if (!TryGetLineCenter(0, out double first) || !TryGetLineCenter(Lyrics.Count - 1, out double last)) return;
        double half = Viewport.ActualHeight / 2.0;
        double tMax = half - first, tMin = half - last;

        _isUserScrolling = true;
        ResumeCenterBtn.Visibility = Visibility.Visible;
        ResumeCenterBtn.Opacity = 1.0;
        _resumeTimer.Stop();
        _resumeTimer.Start();

        ResetLineOffsets();
        MoveTo(Math.Clamp(_targetT + delta * 0.9, tMin, tMax), animate: true, period: 0.22f, damping: 1.0f);
        e.Handled = true;
    }

    private void ResumeAutoCenter()
    {
        _resumeTimer.Stop();
        _isUserScrolling = false;
        ResumeCenterBtn.Visibility = Visibility.Collapsed;
        CenterOnActive(animate: true);
    }

    private void ResumeCenterBtn_Click(object sender, RoutedEventArgs e) => ResumeAutoCenter();

    private void LyricLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is LyricLine line)
        {
            _isUserScrolling = false;
            _resumeTimer.Stop();
            ResumeCenterBtn.Visibility = Visibility.Collapsed;
            SeekRequested?.Invoke(this, line.Time);
        }
    }

    #endregion
}
