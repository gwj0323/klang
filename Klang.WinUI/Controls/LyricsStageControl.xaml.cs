using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
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

    /// <summary>校准模式：下一次点击歌词行不跳播，而是上报该行时间（用来对齐“此刻正在唱的那句”）。</summary>
    public bool CalibrationMode { get; set; }
    public event EventHandler<double>? CalibrationPicked;

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
        public Tween FT = new(), OT = new();   // 字号倍率 / 透明度的过渡曲线
        public float TargetBlur, Blur;         // 景深模糊（高斯模糊标准差，DIP）
        public Tween BT = new();
        public Tween LT = new();               // 逐字上移：这一行唱完离开当前句后，整行从“抬起”回落到原位
        public double StartAt;        // 错峰：到这个时刻才开始朝目标动
        public double DrawTop, DrawBlock;   // 最近一次绘制的屏幕位置（点击命中用）
        public int[]? WordStart;            // 逐字：每个字在整行文字里的起始字符位置（最后一项 = 总长度）
        public Windows.Foundation.Rect[][]? WordRects;   // 逐字：每个字在排版里占的矩形（可能跨行，所以是数组）
    }

    /// <summary>
    /// 五次 Hermite 过渡：起点和终点的加速度都为 0，起步是“慢慢加力”而不是一下子被拽走
    /// （弹簧从静止出发时加速度最大，放大那一下就显得生硬）。中途换目标时保留当前速度，曲线连续不折。
    /// </summary>
    private sealed class Tween
    {
        public double From, V0, To, T0, Dur = 1;

        public void Snap(double v) { From = To = v; V0 = 0; T0 = 0; }

        public void Retarget(double to, double now, double startAt, double dur)
        {
            double cur = Eval(now, out double vel);
            From = cur; V0 = now >= T0 ? vel : 0; To = to; T0 = Math.Max(now, startAt); Dur = dur;
        }

        public bool Done(double now) => now >= T0 + Dur;

        public double Eval(double now, out double vel)
        {
            if (now <= T0) { vel = 0; return From; }
            double t = (now - T0) / Dur;
            if (t >= 1) { vel = 0; return To; }
            double t2 = t * t, t3 = t2 * t, t4 = t3 * t, t5 = t4 * t;
            double h0 = 1 - 10 * t3 + 15 * t4 - 6 * t5;
            double h1 = t - 6 * t3 + 8 * t4 - 3 * t5;
            double h5 = 10 * t3 - 15 * t4 + 6 * t5;
            double d0 = -30 * t2 + 60 * t3 - 30 * t4;
            double d1 = 1 - 18 * t2 + 32 * t3 - 15 * t4;
            double vT = V0 * Dur;
            vel = (From * d0 + vT * d1 + To * (-d0)) / Dur;
            return From * h0 + vT * h1 + To * h5;
        }
    }

    // 放大 / 缩小和明暗变化的时长（秒）
    private const double ScaleDuration = 0.62;
    private const double FadeDuration = 0.5;

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

    private void OnEmphasisChanged() => DispatcherQueue.SafeEnqueue(() => Rebuild(snap: false));

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

    // ---- 逐字高亮用的连续时间：播放位置一般几十毫秒才更新一次，逐帧渲染时在两次更新之间按时钟外推，
    //      这样“扫过”每个字的过程才是顺滑的；暂停（位置不再前进）时停止外推并停掉渲染循环。
    private double _timeBase;
    private double _timeStamp;
    private bool _paused = true;

    private double CurrentTime()
    {
        if (_paused) return _timeBase;
        return _timeBase + Math.Min(0.3, _clock.Elapsed.TotalSeconds - _timeStamp);
    }

    private bool ActiveHasWords => _active >= 0 && _active < _rows.Count && _rows[_active].Line.Words is { Count: > 0 };

    private bool KaraokeRunning() => ActiveHasWords && !_paused && _clock.Elapsed.TotalSeconds - _timeStamp < 0.6;

    public void UpdatePlaybackTime(double currentTimeSeconds)
    {
        if (Lyrics.Count == 0) return;

        double nowS = _clock.Elapsed.TotalSeconds;
        // 位置没前进 = 暂停（或者刚 seek 过）；前进了 = 播放中
        _paused = Math.Abs(currentTimeSeconds - _timeBase) < 0.001 && nowS - _timeStamp > 0.03;
        if (Math.Abs(currentTimeSeconds - _timeBase) >= 0.001) _paused = false;
        _timeBase = currentTimeSeconds;
        _timeStamp = nowS;

        int activeIndex = LyricsService.Instance.FindActiveIndex(Lyrics, currentTimeSeconds);

        // 逐字高亮：当前行随时间推进
        if (activeIndex >= 0 && Lyrics[activeIndex].Words is { } words)
        {
            int played = 0;
            while (played < words.Count && words[played].Start <= currentTimeSeconds) played++;
            Lyrics[activeIndex].PlayedWords = played;
        }

        if (activeIndex == _lastActiveIndex)
        {
            if (!_looping && KaraokeRunning()) StartLoop();   // 逐字高亮要每帧重绘
            return;
        }
        _lastActiveIndex = activeIndex;

        // 换句：其它行的逐字进度清零
        for (int i = 0; i < Lyrics.Count; i++)
        {
            if (i != activeIndex && Lyrics[i].Words != null) Lyrics[i].PlayedWords = 0;
            Lyrics[i].IsActive = i == activeIndex;
        }

        // 逐字上移：刚唱完的那一行离开当前句时，整行从抬起的位置平滑落回
        if (WordMotion && _active >= 0 && _active < _rows.Count && _rows[_active].Line.Words is { Count: > 0 })
        {
            double now0 = _clock.Elapsed.TotalSeconds;
            var lt = _rows[_active].LT;
            lt.Snap(1);
            lt.Retarget(0, now0, now0, 0.5);
        }

        _active = activeIndex;
        // 用户正在手动滚动时只更新高亮，不抢滚动位置
        Rebuild(snap: false, keepScroll: _isUserScrolling);
    }

    // ================= 排版 =================

    private double ViewW => Math.Max(80, Viewport.ActualWidth);
    private double ViewH => Math.Max(80, Viewport.ActualHeight);
    private double TextW => Math.Max(60, ViewW - SidePad * 2);

    private static bool DepthBlur => StateService.Instance.State.LyricDepthBlur;
    private static bool WordMotion => StateService.Instance.State.LyricWordMotion;

    /// <summary>景深模糊的目标强度：当前句和相邻句清晰，往外逐级加深；随字号等比。</summary>
    private float BlurFor(int dist)
    {
        if (!DepthBlur || _isUserScrolling) return 0f;
        float s = (float)(_lyricFontSize / 22.0);
        return dist switch
        {
            0 => 0f,
            1 => 0.7f * s,
            2 => 1.5f * s,
            3 => 2.2f * s,
            _ => 2.8f * s
        };
    }

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
        if (snap) { _layoutCache.Clear(); _subCache.Clear(); foreach (var r in _rows) r.WordRects = null; }

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
            row.TargetBlur = BlurFor(dist);

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
                row.FT.Snap(row.TargetFactor);
                row.OT.Snap(row.TargetOpacity);
                row.Blur = row.TargetBlur;
                row.BT.Snap(row.TargetBlur);
                row.LT.Snap(0);
                row.StartAt = 0;
            }
            else
            {
                // 错峰：当前句最先动，其后的行像波浪一样依次跟上（间隔逐行略微收窄，尾部不会拖沓）；
                // 已唱过的行离场更干脆。用户滚动时不错峰，跟手
                int k = Math.Min(Math.Abs(i - center), MaxStaggerRows);
                double delay = i >= center
                    ? StaggerSeconds * (k + 0.6 * k * (1 - k / (MaxStaggerRows + 1.0)))
                    : StaggerSeconds * 0.5 * k;
                row.StartAt = keepScroll ? now : now + delay * 0.7;
                if (Math.Abs(row.FT.To - row.TargetFactor) > 1e-5) row.FT.Retarget(row.TargetFactor, now, row.StartAt, ScaleDuration);
                if (Math.Abs(row.OT.To - row.TargetOpacity) > 1e-5) row.OT.Retarget(row.TargetOpacity, now, row.StartAt, FadeDuration);
                if (Math.Abs(row.BT.To - row.TargetBlur) > 1e-4) row.BT.Retarget(row.TargetBlur, now, row.StartAt, ScaleDuration);
            }
        }
        if (!snap && !keepScroll) { _omega = 11.0; _zeta = 0.8; }   // 自动换句：轻微欠阻尼，到位时有一点点回弹

        if (snap) LyricsCanvas.Invalidate();
        else StartLoop();
    }

    // 位置用临界阻尼弹簧：起步速度为 0（缓入）、到位时速度也趋于 0（缓出），整个过程加速度连续，
    // 不会像“指数缓出”那样第一帧就以最大速度冲出去（那一下就是轻微的抽动）。
    private double _omega = 11.0;
    private double _zeta = 0.8;     // 阻尼比：1 = 临界阻尼（无回弹），<1 = 到位时轻微回弹

    /// <summary>弹簧的解析解（任意阻尼比、任何帧间隔下都稳定）。</summary>
    private static void Spring(ref double x, ref double v, double target, double omega, double zeta, double dt)
    {
        double rel = x - target;
        if (zeta >= 0.999)
        {
            double decay = Math.Exp(-omega * dt);
            double tmp = v + omega * rel;
            x = target + (rel + tmp * dt) * decay;
            v = (v - tmp * omega * dt) * decay;
            return;
        }
        double wd = omega * Math.Sqrt(1 - zeta * zeta);
        double e = Math.Exp(-zeta * omega * dt);
        double c = Math.Cos(wd * dt), s = Math.Sin(wd * dt);
        double a = zeta * omega / wd;
        double nx = e * (rel * (c + a * s) + v / wd * s);
        double nv = e * (v * (c - a * s) - rel * (omega * omega / wd) * s);
        x = target + nx;
        v = nv;
    }

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

        // 位置用弹簧（跟手、可随时改目标）；字号和透明度用五次曲线过渡（起步柔和，不会“猛地一放大”）
        bool moving = false;

        // 帧间隔不稳定时（掉帧）切成固定小步，避免一帧大跳
        int steps = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 120)));
        double h = dt / steps;

        foreach (var r in _rows)
        {
            r.Factor = (float)r.FT.Eval(now, out _);
            r.Opacity = (float)r.OT.Eval(now, out _);
            r.Blur = (float)r.BT.Eval(now, out _);
            if (!r.FT.Done(now) || !r.OT.Done(now) || !r.BT.Done(now) || !r.LT.Done(now)) moving = true;

            if (now < r.StartAt) { moving = true; continue; }

            double y = r.Y, vy = r.VY;
            for (int s = 0; s < steps; s++) Spring(ref y, ref vy, r.TargetY, _omega, _zeta, h);
            r.Y = y; r.VY = vy;
            if (Math.Abs(r.TargetY - r.Y) < 0.05 && Math.Abs(r.VY) < 1.0) { r.Y = r.TargetY; r.VY = 0; } else moving = true;
        }

        LyricsCanvas.Invalidate();
        if (!moving && !KaraokeRunning()) StopLoop();
    }

    // ================= 逐字高亮（卡拉 OK 扫过效果） =================

    private static double WordEnd(IList<LyricWord> words, int k)
    {
        var w = words[k];
        if (w.End > w.Start) return w.End;
        return k + 1 < words.Count ? words[k + 1].Start : w.Start + 0.35;
    }

    /// <summary>
    /// 已唱完的字亮、没唱的字暗；正在唱的那个字用一道带柔和边缘的“扫光”从左到右按进度逐渐点亮，而不是整字突然变色。
    /// 每帧用外推后的连续时间计算，所以和显示器刷新率一样顺滑。
    /// </summary>
    private void DrawKaraokeLine(CanvasDrawingSession ds, Row row, CanvasTextLayout main, IList<LyricWord> words, float x0, float op, int len)
    {
        // 未唱部分的亮度随行的透明度渐变：刚成为当前句时和上一状态（0.6）一样亮，之后慢慢暗到 0.43，
        // 避免换句瞬间整行突然变暗再扫光
        double t01 = Math.Clamp((op - 0.60) / 0.40, 0, 1);
        double dimFrac = 1.0 - t01 * (1.0 - 110.0 / 255.0);
        var dim = Color.FromArgb((byte)(op * 255 * dimFrac), _primary.R, _primary.G, _primary.B);
        var full = Color.FromArgb((byte)(op * 255), _primary.R, _primary.G, _primary.B);

        if (row.WordStart == null || row.WordStart.Length != words.Count + 1)
        {
            row.WordStart = new int[words.Count + 1];
            for (int k = 0; k < words.Count; k++) row.WordStart[k + 1] = row.WordStart[k] + words[k].Text.Length;
        }
        var ws = row.WordStart;

        double t = CurrentTime();
        int cur = -1; double p = 0; int playedWords = 0;
        for (int k = 0; k < words.Count; k++)
        {
            double end = WordEnd(words, k);
            if (t >= end) { playedWords = k + 1; continue; }
            if (t >= words[k].Start) { cur = k; p = Math.Clamp((t - words[k].Start) / Math.Max(0.02, end - words[k].Start), 0, 1); }
            break;
        }

        main.SetColor(0, len, dim);
        int playedChars = Math.Min(ws[playedWords], len);
        if (playedChars > 0) main.SetColor(0, playedChars, full);
        ds.DrawTextLayout(main, x0, 0, dim);

        if (cur < 0 || p <= 0) return;
        int start = ws[cur], wl = Math.Min(ws[cur + 1], len) - start;
        if (wl <= 0) return;

        row.WordRects ??= new Windows.Foundation.Rect[words.Count][];
        if (row.WordRects[cur] == null)
            row.WordRects[cur] = main.GetCharacterRegions(start, wl).Select(r => r.LayoutBounds).ToArray();

        main.SetColor(start, wl, full);
        const double soft = 0.35;                   // 扫光边缘的柔和宽度（占一个字宽度的比例）
        double edge = p * (1 + soft);
        float s0 = (float)Math.Clamp(edge - soft, 0, 1), s1 = (float)Math.Clamp(Math.Max(edge, edge - soft + 0.001), 0, 1);
        if (s1 <= s0) s1 = Math.Min(1f, s0 + 0.001f);
        var stops = new List<CanvasGradientStop>();
        if (s0 > 0) stops.Add(new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(255, 255, 255, 255) });
        stops.Add(new CanvasGradientStop { Position = s0, Color = Color.FromArgb(255, 255, 255, 255) });
        stops.Add(new CanvasGradientStop { Position = s1, Color = Color.FromArgb(0, 255, 255, 255) });
        if (s1 < 1f) stops.Add(new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(0, 255, 255, 255) });

        foreach (var r in row.WordRects[cur])
        {
            var clip = new Windows.Foundation.Rect(r.X + x0 - 1, r.Y - 1, r.Width + 2, r.Height + 2);
            using var mask = new CanvasLinearGradientBrush(ds, stops.ToArray())
            {
                StartPoint = new System.Numerics.Vector2((float)(r.X + x0), 0),
                EndPoint = new System.Numerics.Vector2((float)(r.X + x0 + r.Width), 0)
            };
            using (ds.CreateLayer(mask, clip))
                ds.DrawTextLayout(main, x0, 0, full);
        }
        main.SetColor(start, wl, dim);
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
            // 缩放原点取文字块（按目标倍率排好版的那块）的垂直中心：放大时向上下同时长开，
            // 不会像以顶边为原点那样只往下压、和下一行抢空间；位置动画与缩放动画才合成同一个自然的运动。
            float hc = (float)(main.LayoutBounds.Height + (sub != null ? SubGap / Math.Max(0.01f, tf) + sub.LayoutBounds.Height : 0));
            var saved = ds.Transform;
            var rowTransform = System.Numerics.Matrix3x2.CreateTranslation(0, -hc / 2f)
                             * System.Numerics.Matrix3x2.CreateScale(f)
                             * System.Numerics.Matrix3x2.CreateTranslation(cx, (float)(textTop + hc * tf / 2.0));
            float x0 = -layoutW / 2f;

            float blur = row.Blur;
            if (blur > 0.05f)
            {
                // 景深模糊：把这一行先录进命令列表，再整体做高斯模糊（GPU 上完成）
                using var cl = new CanvasCommandList(sender);
                using (var cds = cl.CreateDrawingSession())
                {
                    cds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
                    cds.Transform = rowTransform;
                    DrawRowText(cds, row, main, sub, active, x0, op, f);
                }
                using var fx = new GaussianBlurEffect
                {
                    Source = cl,
                    BlurAmount = blur,
                    BorderMode = EffectBorderMode.Soft,
                    Optimization = EffectOptimization.Speed
                };
                ds.DrawImage(fx);
            }
            else
            {
                ds.Transform = rowTransform;
                DrawRowText(ds, row, main, sub, active, x0, op, f);
                ds.Transform = saved;
            }
        }
    }

    /// <summary>画一行的主歌词与副行（坐标系已经变换到这一行的排版原点）。</summary>
    private void DrawRowText(CanvasDrawingSession ds, Row row, CanvasTextLayout main, CanvasTextLayout? sub, bool active, float x0, float op, float f)
    {
        // 主歌词。注意：排版对象是缓存的，上一次画它时设过的“按字上色”会一直留着，
        // 所以每次画之前都要先把整行颜色重设一遍——否则唱完的那一行会一直保持“已唱”的亮色，不会随距离变灰。
        var words = row.Line.Words;
        var full = Color.FromArgb((byte)(op * 255), _primary.R, _primary.G, _primary.B);
        int len = MainText(row.Line).Length;
        if (active && words is { Count: > 0 })
        {
            if (WordMotion) DrawKaraokeLineMotion(ds, row, main, words, x0, op, len);
            else DrawKaraokeLine(ds, row, main, words, x0, op, len);
        }
        else
        {
            main.SetColor(0, len, full);
            // 刚唱完的逐字行：从抬起的位置落回
            double lift = WordMotion ? row.LT.Eval(_clock.Elapsed.TotalSeconds, out _) * WordLift : 0;
            ds.DrawTextLayout(main, x0, (float)-lift, full);
        }

        if (sub != null)
        {
            float subTop = (float)(main.LayoutBounds.Height + SubGap / Math.Max(0.01f, f));
            ds.DrawTextLayout(sub, x0, subTop,
                Color.FromArgb((byte)(op * 255 * 0.62), _primary.R, _primary.G, _primary.B));
        }
    }

    // ================= 逐字动效：唱到的字上移，长音辉光 =================

    /// <summary>唱到的字上移的距离（排版坐标，随字号等比，22 号字约 2 DIP）。</summary>
    private double WordLift => _lyricFontSize * 0.095;

    private static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * x * (x * (x * 6 - 15) + 10);
    }

    private void DrawKaraokeLineMotion(CanvasDrawingSession ds, Row row, CanvasTextLayout main, IList<LyricWord> words, float x0, float op, int len)
    {
        double t01 = Math.Clamp((op - 0.60) / 0.40, 0, 1);
        double dimFrac = 1.0 - t01 * (1.0 - 110.0 / 255.0);
        var dim = Color.FromArgb((byte)(op * 255 * dimFrac), _primary.R, _primary.G, _primary.B);
        var full = Color.FromArgb((byte)(op * 255), _primary.R, _primary.G, _primary.B);

        if (row.WordStart == null || row.WordStart.Length != words.Count + 1)
        {
            row.WordStart = new int[words.Count + 1];
            for (int k = 0; k < words.Count; k++) row.WordStart[k + 1] = row.WordStart[k] + words[k].Text.Length;
        }
        var ws = row.WordStart;
        row.WordRects ??= new Windows.Foundation.Rect[words.Count][];

        double t = CurrentTime();
        int playedWords = 0, cur = -1; double p = 0;
        for (int k = 0; k < words.Count; k++)
        {
            double end = WordEnd(words, k);
            if (t >= end) { playedWords = k + 1; continue; }
            if (t >= words[k].Start) { cur = k; p = Math.Clamp((t - words[k].Start) / Math.Max(0.02, end - words[k].Start), 0, 1); }
            break;
        }

        main.SetColor(0, len, dim);
        int playedChars = Math.Min(ws[playedWords], len);
        if (playedChars > 0) main.SetColor(0, playedChars, full);

        var baseT = ds.Transform;
        bool dark = _primary.R > 128;
        double liftMax = WordLift;

        for (int k = 0; k < words.Count; k++)
        {
            int start = ws[k], wl = Math.Min(ws[k + 1], len) - start;
            if (wl <= 0) continue;
            row.WordRects[k] ??= main.GetCharacterRegions(start, wl).Select(r => r.LayoutBounds).ToArray();
            var rects = row.WordRects[k];

            double s = words[k].Start, e = WordEnd(words, k), d = e - s;
            // 上移：在这个字的时长内抬起（太短的字至少用 0.18 秒，太长的字 0.6 秒内抬完）
            double lift = t <= s ? 0 : liftMax * Smooth((t - s) / Math.Clamp(d, 0.18, 0.6));

            ds.Transform = System.Numerics.Matrix3x2.CreateTranslation(0, (float)-lift) * baseT;

            // 长音辉光：时长 ≥ 0.9 秒的字，演唱过程中辉光渐强，唱完后 0.35 秒内退去
            if (d >= 0.9 && t > s)
            {
                double g = t < e ? Smooth((t - s) / d) : 1 - Math.Clamp((t - e) / 0.35, 0, 1);
                if (g > 0.01)
                {
                    using var cl = new CanvasCommandList(ds);
                    using (var cds = cl.CreateDrawingSession())
                    {
                        cds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
                        cds.Transform = ds.Transform;
                        foreach (var r in rects)
                            using (cds.CreateLayer(1f, new Windows.Foundation.Rect(r.X + x0, r.Y, r.Width, r.Height)))
                                cds.DrawTextLayout(main, x0, 0, full);
                    }
                    using var blurFx = new GaussianBlurEffect { Source = cl, BlurAmount = (float)(_lyricFontSize * 0.28), BorderMode = EffectBorderMode.Soft };
                    using var opFx = new OpacityEffect { Source = blurFx, Opacity = (float)(g * (dark ? 0.75 : 0.22) * op) };
                    var tt = ds.Transform;
                    ds.Transform = System.Numerics.Matrix3x2.Identity;
                    ds.DrawImage(opFx);
                    ds.Transform = tt;
                }
            }

            // 这个字本体：按自己的矩形裁剪后画（只画到这个字，整行排版不变）
            foreach (var r in rects)
                using (ds.CreateLayer(1f, new Windows.Foundation.Rect(r.X + x0, r.Y, r.Width, r.Height)))
                    ds.DrawTextLayout(main, x0, 0, dim);

            // 正在唱的字：扫光
            if (k == cur && p > 0)
            {
                main.SetColor(start, wl, full);
                const double soft = 0.35;
                double edge = p * (1 + soft);
                float s0 = (float)Math.Clamp(edge - soft, 0, 1), s1 = (float)Math.Clamp(Math.Max(edge, edge - soft + 0.001), 0, 1);
                if (s1 <= s0) s1 = Math.Min(1f, s0 + 0.001f);
                var stops = new List<CanvasGradientStop>();
                if (s0 > 0) stops.Add(new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(255, 255, 255, 255) });
                stops.Add(new CanvasGradientStop { Position = s0, Color = Color.FromArgb(255, 255, 255, 255) });
                stops.Add(new CanvasGradientStop { Position = s1, Color = Color.FromArgb(0, 255, 255, 255) });
                if (s1 < 1f) stops.Add(new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(0, 255, 255, 255) });
                var stopArr = stops.ToArray();
                foreach (var r in rects)
                {
                    var clip = new Windows.Foundation.Rect(r.X + x0, r.Y, r.Width, r.Height);
                    using var mask = new CanvasLinearGradientBrush(ds, stopArr)
                    {
                        StartPoint = new System.Numerics.Vector2((float)(r.X + x0), 0),
                        EndPoint = new System.Numerics.Vector2((float)(r.X + x0 + r.Width), 0)
                    };
                    using (ds.CreateLayer(mask, clip))
                        ds.DrawTextLayout(main, x0, 0, full);
                }
                main.SetColor(start, wl, dim);
            }
        }
        ds.Transform = baseT;
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
        _omega = 20.0; _zeta = 1.0;   // 滚轮要跟手，弹簧硬一点，不回弹
        foreach (var r in _rows)
        {
            r.TargetY = SnapY(_scrollTarget + r.Top); r.StartAt = now;
            // 手动滚动时要看清每一行：模糊很快退掉
            if (r.BT.To > 0) { r.TargetBlur = 0; r.BT.Retarget(0, now, now, 0.25); }
        }
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
            if (CalibrationMode)
            {
                // 校准：点的是“此刻正在唱的那一句”，不跳播，只上报这句的时间
                CalibrationMode = false;
                CalibrationPicked?.Invoke(this, _rows[row].Line.Time);
            }
            else
            {
                // 点击某句：跳到那一刻播放，并回到自动居中
                ResumeAutoCenter();
                SeekRequested?.Invoke(this, _rows[row].Line.Time);
            }
        }
        _pressedRow = -1;
    }
}
