using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Controls;

/// <summary>
/// 歌词舞台（Win2D 绘制）。
/// 每一帧都按“当前真实字号”重新排版、重新栅格化文字，而不是缩放一张已经渲染好的文字纹理，
/// 所以当前句放大、远处缩小、整体滚动的过程中文字始终是原生锐利的（这也是 BetterLyrics 这类项目采用的做法）。
/// 动画（错峰 + 缓出）在代码里逐帧计算，只在有动画或逐字高亮时才占用渲染循环，空闲时不重绘。
/// </summary>
public sealed partial class LyricsStageControl : UserControl
{
    public ObservableCollection<LyricLine> Lyrics { get; } = new();

    public event EventHandler<double>? SeekRequested;

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
            Rebuild(snap: true);
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
            Rebuild(snap: true);
        }
    }

    /// <summary>保留的兼容属性：固定颜色的上下遮罩已弃用，歌词行本身按距离渐隐。</summary>
    public bool ShowFades
    {
        set { }
    }

    // ================= 每一行的状态 =================

    private sealed class Row
    {
        public required LyricLine Line;
        public double Top;            // 目标布局下，这一行在内容坐标里的上边（不含滚动）
        public double Block;          // 这一行（含上下留白）的目标高度
        public double Y;              // 当前屏幕 Y（动画值）
        public double VY;             // 当前纵向速度（弹簧动画用，起步为 0，所以是缓入缓出，没有突然启动的抽动）
        public double TargetY;        // 目标屏幕 Y
        public float Factor = 1f;     // 当前缩放倍率（相对基础字号，动画值）
        public float TargetFactor = 1f;
        public float Opacity;         // 当前透明度（动画值）
        public float TargetOpacity;
        public double StartAt;        // 错峰：到这个时刻才开始朝目标动
        public double DrawTop, DrawBlock;   // 最近一次绘制的屏幕位置（点击命中用）
    }

    private readonly List<Row> _rows = new();
    private int _active = -1;
    private int _lastActiveIndex = -2;
    private double _scrollTarget;     // 内容整体的滚动目标（Y）
    private bool _isUserScrolling;
    private int _hover = -1;
    private int _pressedRow = -1;
    private Windows.Foundation.Point _pressedPt;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrame;
    private bool _looping;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _resumeTimer;

    // 排版常量（DIP）
    private const double SidePad = 48;      // 文字距离控件左右的留白
    private const double BlockPad = 14;     // 每行上下留白（相邻两行之间 = 2 × BlockPad）
    private const double SubGap = 3;
    private const double StaggerSeconds = 0.034;
    private const int MaxStaggerRows = 6;

    // 和歌曲标题（专辑图下面那行字）同一套字体：西文 Segoe UI Variable，中日韩字符交给系统的字体回退
    // （按歌词的语言自动选 Yu Gothic UI / 微软雅黑 UI），这样日文歌词的字形、笔画粗细都和标题一致。
    private const string LyricFont = "Segoe UI Variable Text";

    // 所有行用同一字重（半粗体）：笔画比常规体更粗更清晰，当前句只靠放大和亮度突出，
    // 这样切换当前句时文字宽度不会跳变，放大动画才平滑。
    private static readonly Windows.UI.Text.FontWeight LyricWeight = Microsoft.UI.Text.FontWeights.SemiBold;

    private Color _primary = Microsoft.UI.Colors.White;

    public LyricsStageControl()
    {
        InitializeComponent();

        UiAnimations.AttachFade(ResumeCenterBtn, 6f);
        UiAnimations.AttachFade(EmptyLyricsPanel, 6f);
        UpdateEmptyState();

        // 用户滚动后 2.8 秒自动回到当前句
        _resumeTimer = DispatcherQueue.CreateTimer();
        _resumeTimer.Interval = TimeSpan.FromMilliseconds(2800);
        _resumeTimer.IsRepeating = false;
        _resumeTimer.Tick += (s, e) => ResumeAutoCenter();

        UpdateColors();
        ActualThemeChanged += (s, e) => { UpdateColors(); LyricsCanvas.Invalidate(); };
        Unloaded += (s, e) => StopLoop();
        EmphasisChanged += OnEmphasisChanged;
        Unloaded += (s, e) => EmphasisChanged -= OnEmphasisChanged;
    }

    private void UpdateColors()
    {
        // 深色背景用纯白，浅色背景用近黑
        byte c = ActualTheme == ElementTheme.Dark ? (byte)255 : (byte)20;
        _primary = Color.FromArgb(255, c, c, c);
    }

    /// <summary>设置里切换“放大强调”后，所有歌词控件立即按新方式重新定位。</summary>
    public static event Action? EmphasisChanged;
    public static void NotifyEmphasisChanged() => EmphasisChanged?.Invoke();

    private void OnEmphasisChanged() => DispatcherQueue.TryEnqueue(() => Rebuild(snap: false));

    // ================= 载入歌词 =================

    private void UpdateEmptyState()
    {
        // 歌词服务在没有歌词时会给一行“暂无歌词”的占位（结束时间 9999）：不当歌词渲染，改显示小号的空状态
        bool empty = Lyrics.Count == 0 || (Lyrics.Count == 1 && Lyrics[0].EndTime >= 9999.0);
        EmptyLyricsPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        LyricsCanvas.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResetState()
    {
        _lastActiveIndex = -2;
        _active = -1;
        _isUserScrolling = false;
        _hover = -1;
        _resumeTimer.Stop();
        ResumeCenterBtn.Visibility = Visibility.Collapsed;
        _rows.Clear();
        _layoutCache.Clear();
        _subCache.Clear();
    }

    public void LoadLyrics(string? audioFilePath)
    {
        LoadLyricLines(LyricsService.Instance.LoadLyrics(audioFilePath));
    }

    /// <summary>直接载入已解析好的歌词（云端歌词）。</summary>
    public void LoadLyricLines(IList<LyricLine> lines)
    {
        ResetState();
        foreach (var old in Lyrics) old.PropertyChanged -= Line_PropertyChanged;
        Lyrics.Clear();
        foreach (var l in lines)
        {
            l.MainFontSize = _lyricFontSize;
            l.SubMode = _subMode;
            l.PropertyChanged += Line_PropertyChanged;
            Lyrics.Add(l);
            _rows.Add(new Row { Line = l });
        }
        UpdateEmptyState();
        Rebuild(snap: true);
    }

    private void Line_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 逐字进度推进 → 只需要重绘，不需要重新排版
        if (e.PropertyName == nameof(LyricLine.PlayedWords)) LyricsCanvas.Invalidate();
    }

    // ================= 播放进度驱动 =================

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

        _active = activeIndex;
        // 用户正在手动滚动时只更新高亮，不抢滚动位置
        Rebuild(snap: false, keepScroll: _isUserScrolling);
    }

    // ================= 排版 =================

    private double ViewW => Math.Max(80, Viewport.ActualWidth);
    private double ViewH => Math.Max(80, Viewport.ActualHeight);
    private double TextW => Math.Max(60, ViewW - SidePad * 2);

    private static (float SizeFactor, float Opacity) StateFor(int dist)
    {
        bool zoom = StateService.Instance.State.LyricZoomEmphasis;
        return dist switch
        {
            0 => (zoom ? 1.14f : 1f, 1.00f),
            1 => (1.00f, 0.60f),
            2 => (zoom ? 0.96f : 1f, 0.36f),
            3 => (zoom ? 0.93f : 1f, 0.22f),
            _ => (zoom ? 0.90f : 1f, 0.12f)
        };
    }

    // ---- 文字排版缓存：(行号, 字号 ×4, 是否加粗) → 排版 ----
    private readonly Dictionary<(int, int, bool), CanvasTextLayout> _layoutCache = new();
    private readonly Dictionary<(int, int), CanvasTextLayout> _subCache = new();

    private static string? LocaleFor(string text)
    {
        // 日文 / 韩文歌词要用对应语言的字形（同一个汉字日文和中文写法不同），否则会拿中文字体去显示
        foreach (var ch in text)
        {
            if ((ch >= '぀' && ch <= 'ヿ') || (ch >= 'ㇰ' && ch <= 'ㇿ')) return "ja-JP";
            if ((ch >= '가' && ch <= '힯') || (ch >= 'ᄀ' && ch <= 'ᇿ')) return "ko-KR";
        }
        return null;
    }

    private static string MainText(LyricLine line) =>
        line.Words is { Count: > 0 } w ? string.Concat(w.Select(x => x.Text)) : line.Text;

    // 排版只按“基础字号”做一次，放大 / 缩小靠绘制时的缩放变换：
    // Direct2D 在缩放变换下直接用字形轮廓重新渲染文字（不是缩放位图），所以任何倍率下都锐利，
    // 而且动画过程中不用再重新排版（不会每帧换行 / 重建排版造成卡顿或抖动）。
    // 缓存的键是“目标倍率”（只有几个固定档位），换行宽度按目标倍率折算，静止时和屏幕宽度一致。
    // 每行只有一份排版，和倍率无关（换行宽度统一按最大倍率折算）：换句时不用现场创建新排版，
    // 否则每次换句要在界面线程上同步创建好几个 CanvasTextLayout，正好卡在动画起步那几帧，表现就是轻微抽动。
    private const float WrapFactor = 1.14f;

    private CanvasTextLayout MainLayout(CanvasDevice dev, int i, float factor)
    {
        var key = (i, 0, false);
        if (_layoutCache.TryGetValue(key, out var cached)) return cached;

        var line = _rows[i].Line;
        string text = MainText(line);
        var fmt = new CanvasTextFormat
        {
            FontFamily = LyricFont,
            FontSize = (float)_lyricFontSize,
            FontWeight = LyricWeight,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            WordWrapping = CanvasWordWrapping.Wrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.None,
            LocaleName = LocaleFor(text) ?? ""
        };
        var layout = new CanvasTextLayout(dev, text, fmt, (float)(TextW / WrapFactor), 10000f);
        _layoutCache[key] = layout;
        return layout;
    }

    private CanvasTextLayout? SubLayout(CanvasDevice dev, int i, float factor)
    {
        var line = _rows[i].Line;
        string sub = line.DisplaySub;
        if (string.IsNullOrWhiteSpace(sub)) return null;

        var key = (i, 0);
        if (_subCache.TryGetValue(key, out var cached)) return cached;

        var fmt = new CanvasTextFormat
        {
            FontFamily = LyricFont,
            FontSize = (float)Math.Round(_lyricFontSize * 13.5 / 22.0, 1),
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            WordWrapping = CanvasWordWrapping.Wrap,
            LocaleName = LocaleFor(sub) ?? ""
        };
        var layout = new CanvasTextLayout(dev, sub, fmt, (float)(TextW / WrapFactor), 10000f);
        _subCache[key] = layout;
        return layout;
    }

    /// <summary>
    /// 按当前高亮句重新算每一行的目标字号 / 透明度 / 位置。
    /// snap = true 直接到位（载入 / 改字号 / 改宽度）；否则从当前状态出发，离当前句越远的行越晚开始动（错峰）。
    /// </summary>
    private void Rebuild(bool snap, bool keepScroll = false)
    {
        if (_rows.Count == 0) { LyricsCanvas.Invalidate(); return; }
        if (snap) { _layoutCache.Clear(); _subCache.Clear(); }

        var dev = CanvasDevice.GetSharedDevice();
        int center = _active >= 0 ? _active : 0;
        double now = _clock.Elapsed.TotalSeconds;

        double top = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            int dist = Math.Abs(i - center);
            var (factor, opacity) = StateFor(dist);
            row.TargetFactor = factor;
            row.TargetOpacity = opacity;

            var main = MainLayout(dev, i, factor);
            double h = main.LayoutBounds.Height * factor;
            if (SubLayout(dev, i, factor) is { } sub) h += SubGap + sub.LayoutBounds.Height * factor;

            row.Top = top;
            row.Block = h + BlockPad * 2;
            top += row.Block;
        }

        if (!keepScroll)
        {
            var a = _rows[center];
            _scrollTarget = ViewH / 2.0 - (a.Top + a.Block / 2.0);
        }

        double dpi = XamlRoot?.RasterizationScale ?? 1.0;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            // 目标位置本身就取整到物理像素：动画收敛到的就是整数像素，停下时不会再有最后半个像素的跳动
            row.TargetY = SnapY(_scrollTarget + row.Top);
            if (snap)
            {
                row.Y = row.TargetY;
                row.VY = 0;
                row.Factor = row.TargetFactor;
                row.Opacity = row.TargetOpacity;
                row.StartAt = 0;
            }
            else
            {
                // 错峰：当前句最先动，离得越远越晚。用户滚动时不错峰，跟手
                int dist = Math.Abs(i - center);
                row.StartAt = keepScroll ? now : now + StaggerSeconds * Math.Min(dist, MaxStaggerRows);
            }
        }
        if (!snap && !keepScroll) _omega = 12.0;   // 自动换句：柔和一点（约 0.4 秒到位）

        if (snap) LyricsCanvas.Invalidate();
        else StartLoop();
    }

    // 位置用临界阻尼弹簧：起步速度为 0（缓入）、到位时速度也趋于 0（缓出），整个过程加速度连续，
    // 不会像“指数缓出”那样第一帧就以最大速度冲出去（那一下就是轻微的抽动）。
    private double _omega = 12.0;

    private double SnapY(double y)
    {
        double dpi = XamlRoot?.RasterizationScale ?? 1.0;
        return Math.Round(y * dpi) / dpi;
    }

    // ================= 动画循环（只在有动画时运行） =================

    private void StartLoop()
    {
        if (_looping) return;
        _looping = true;
        _lastFrame = _clock.Elapsed.TotalSeconds;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
    }

    private void StopLoop()
    {
        if (!_looping) return;
        _looping = false;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.05, now - _lastFrame);
        _lastFrame = now;

        // 先快后慢的指数缓出：位置、字号、透明度各用一个速率
        double ap = 1 - Math.Exp(-8.0 * dt);
        double asz = 1 - Math.Exp(-9.0 * dt);
        bool moving = false;

        foreach (var r in _rows)
        {
            if (now < r.StartAt) { moving = true; continue; }

            // 临界阻尼弹簧的解析解（任何帧间隔下都稳定）
            double rel = r.Y - r.TargetY;
            double decay = Math.Exp(-_omega * dt);
            double tmp = r.VY + _omega * rel;
            r.Y = r.TargetY + (rel + tmp * dt) * decay;
            r.VY = (r.VY - tmp * _omega * dt) * decay;
            r.Factor += (float)((r.TargetFactor - r.Factor) * asz);
            r.Opacity += (float)((r.TargetOpacity - r.Opacity) * asz);

            if (Math.Abs(r.TargetY - r.Y) < 0.05 && Math.Abs(r.VY) < 1.5) { r.Y = r.TargetY; r.VY = 0; } else moving = true;
            if (Math.Abs(r.TargetFactor - r.Factor) < 0.0004f) r.Factor = r.TargetFactor; else moving = true;
            if (Math.Abs(r.TargetOpacity - r.Opacity) < 0.004f) r.Opacity = r.TargetOpacity; else moving = true;
        }

        LyricsCanvas.Invalidate();
        if (!moving) StopLoop();
    }

    // ================= 绘制 =================

    private void LyricsCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_rows.Count == 0) return;

        var ds = args.DrawingSession;
        ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        var dev = sender.Device;
        double dpi = XamlRoot?.RasterizationScale ?? 1.0;
        double viewH = ViewH;
        double padX = SidePad;

        // 一直不用的排版别无限堆积
        if (_layoutCache.Count > 20000) _layoutCache.Clear();
        if (_subCache.Count > 20000) _subCache.Clear();

        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            // 动画中用精确的小数位置（逐帧平滑，不会因为取整出现一像素一像素的台阶）；
            // 静止后才对齐到整数物理像素，文字不落在半个像素上，才不会发软
            double y = Math.Abs(row.Y - row.TargetY) < 1e-6 ? Math.Round(row.Y * dpi) / dpi : row.Y;
            row.DrawTop = y;
            row.DrawBlock = row.Block;
            if (y + row.Block < -40 || y > viewH + 40) continue;   // 屏幕外的不画

            bool active = i == _active;
            float tf = row.TargetFactor;       // 排版用目标倍率（固定档位，有缓存）
            float f = row.Factor;              // 绘制用当前倍率（动画值，逐帧平滑）
            var main = MainLayout(dev, i, tf);
            var sub = SubLayout(dev, i, tf);

            double textTop = y + BlockPad;
            float op = Math.Clamp(row.Opacity, 0f, 1f);
            float cx = (float)(padX + TextW / 2.0);
            float layoutW = (float)(TextW / WrapFactor);

            // 鼠标悬停的底色（和普通按钮的悬停一致）
            if (i == _hover && !_isUserScrolling && IsClickable(row.Line))
            {
                ds.FillRoundedRectangle((float)(padX - 16), (float)(y + 2), (float)(TextW + 32), (float)(row.Block - 4), 8, 8,
                    Color.FromArgb(18, _primary.R, _primary.G, _primary.B));
            }

            // 以“文字块上边的水平中心”为原点缩放：Direct2D 在缩放变换下用字形轮廓渲染，放大缩小过程都是锐利的
            var saved = ds.Transform;
            ds.Transform = System.Numerics.Matrix3x2.CreateScale(f) * System.Numerics.Matrix3x2.CreateTranslation(cx, (float)textTop);
            float x0 = -layoutW / 2f;

            // 主歌词：当前句有逐字时间轴 → 已唱的字亮、未唱的字暗；其他情况整行同色
            var words = row.Line.Words;
            if (active && words is { Count: > 0 })
            {
                int played = Math.Clamp(row.Line.PlayedWords, 0, words.Count);
                var dim = Color.FromArgb((byte)(op * 110), _primary.R, _primary.G, _primary.B);
                var full = Color.FromArgb((byte)(op * 255), _primary.R, _primary.G, _primary.B);
                int len = MainText(row.Line).Length;
                main.SetColor(0, len, dim);
                int playedChars = words.Take(played).Sum(w => w.Text.Length);
                if (playedChars > 0) main.SetColor(0, Math.Min(playedChars, len), full);
                ds.DrawTextLayout(main, x0, 0, full);
            }
            else
            {
                ds.DrawTextLayout(main, x0, 0, Color.FromArgb((byte)(op * 255), _primary.R, _primary.G, _primary.B));
            }

            if (sub != null)
            {
                float subTop = (float)(main.LayoutBounds.Height + SubGap / Math.Max(0.01f, f));
                ds.DrawTextLayout(sub, x0, subTop,
                    Color.FromArgb((byte)(op * 255 * 0.62), _primary.R, _primary.G, _primary.B));
            }
            ds.Transform = saved;
        }
    }

    // ================= 尺寸 / 滚轮 / 点击 =================

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Viewport.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        if (_rows.Count > 0) Rebuild(snap: true, keepScroll: _isUserScrolling);
    }

    private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        double delta = e.GetCurrentPoint(Viewport).Properties.MouseWheelDelta;

        // 允许滚动的范围：第一句居中 ~ 最后一句居中
        var first = _rows[0]; var last = _rows[^1];
        double half = ViewH / 2.0;
        double tMax = half - (first.Top + first.Block / 2.0);
        double tMin = half - (last.Top + last.Block / 2.0);

        _isUserScrolling = true;
        ResumeCenterBtn.Visibility = Visibility.Visible;
        ResumeCenterBtn.Opacity = 1.0;
        _resumeTimer.Stop();
        _resumeTimer.Start();

        _scrollTarget = Math.Clamp(_scrollTarget + delta * 0.9, tMin, tMax);
        double now = _clock.Elapsed.TotalSeconds;
        _omega = 20.0;   // 滚轮要跟手，弹簧硬一点
        foreach (var r in _rows) { r.TargetY = SnapY(_scrollTarget + r.Top); r.StartAt = now; }
        StartLoop();
        e.Handled = true;
    }

    private void ResumeCenterBtn_Click(object sender, RoutedEventArgs e) => ResumeAutoCenter();

    private void ResumeAutoCenter()
    {
        _resumeTimer.Stop();
        _isUserScrolling = false;
        ResumeCenterBtn.Visibility = Visibility.Collapsed;
        Rebuild(snap: false);
    }

    private int RowAt(double y)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (y >= _rows[i].DrawTop && y < _rows[i].DrawTop + _rows[i].DrawBlock) return i;
        return -1;
    }

    private static bool IsClickable(LyricLine l) => l.EndTime < 9000 && !string.IsNullOrWhiteSpace(l.Text);

    private void Viewport_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        int h = RowAt(e.GetCurrentPoint(Viewport).Position.Y);
        if (h != _hover) { _hover = h; LyricsCanvas.Invalidate(); }
    }

    private void Viewport_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hover != -1) { _hover = -1; LyricsCanvas.Invalidate(); }
    }

    private void Viewport_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _pressedPt = e.GetCurrentPoint(Viewport).Position;
        _pressedRow = RowAt(_pressedPt.Y);
    }

    private void Viewport_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Viewport).Position;
        int row = RowAt(p.Y);
        bool still = Math.Abs(p.X - _pressedPt.X) < 6 && Math.Abs(p.Y - _pressedPt.Y) < 6;
        if (row >= 0 && row == _pressedRow && still && IsClickable(_rows[row].Line))
        {
            // 点击某句：跳到那一刻播放，并回到自动居中
            ResumeAutoCenter();
            SeekRequested?.Invoke(this, _rows[row].Line.Time);
        }
        _pressedRow = -1;
    }
}
