using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Controls;

/// <summary>
/// 封面画廊：曲库里每张专辑一块正方形封面，大小三档（1×1 / 2×2 / 3×3），严丝合缝铺成一面大墙，
/// 墙的长宽比接近窗口，按住鼠标可以往任意方向拖动（松手有惯性，拖过边缘会回弹），滚轮滚动，Ctrl+滚轮缩放。
///
/// 画面：背景按视口里封面的主色泛出柔光，四周暗角；悬停的封面浮起、带投影和跟着鼠标走的光泽；
/// 拖得快时整面墙微微后退；正在播放的专辑边框随音乐呼吸，角标是实时频谱；
/// 拖动时顶部提示当前所在的分组（歌手 / 首字母 / 添加月份），右下角缩略地图可点按跳转；
/// 封面先显示模糊的小图再淡入清晰版；没有封面的专辑生成几何图案配专辑名。
///
/// 整面墙用 Win2D 的 CanvasAnimatedControl 画：动画和绘制都在它自己的渲染线程上跑，没有动画时暂停。
/// 线程约定：
///   · 所有可变状态都只在持有 _sync 时读写；渲染线程的 Update / Draw 和界面线程的输入处理都持锁，各自很短；
///   · 持锁时从不调用 _cv 的成员（Paused / Invalidate），一律投递到界面线程、锁外执行，避免两边互等；
///   · 渲染线程不碰任何 XAML 对象：视口尺寸、缩放比例等在界面线程缓存到字段里；
///   · 画到离屏位图（生成图案）只在界面线程持锁时做。
/// </summary>
public sealed class CoverGalleryView : UserControl
{
    /// <summary>用户点了一张专辑：返回要播放的曲目（已按曲序排好）。</summary>
    public event EventHandler<List<TrackItem>>? AlbumPicked;
    public event EventHandler? CloseRequested;

    private enum SortMode { Recent, Album, Artist, Shuffle }

    private sealed class Tile
    {
        public required string Key { get; init; }
        public required List<TrackItem> Tracks { get; init; }
        public required string Title { get; init; }
        public required string Artist { get; init; }
        public required string Sub { get; init; }
        public required string Letter { get; init; }
        public string? Month { get; init; }
        public string? CoverPath { get; init; }
        public long Added { get; init; }
        public double Hue { get; init; }
        public Vector3 BaseTint { get; init; }
        public int Pref = 1;          // 想要的边长（格数）
        public int Size = 1;          // 实际边长
        public int Col, Row;
        public double X, Y, W;
        public Vector3 Tint;          // 封面主色（0–1），没加载前是占位色

        // 动画
        public float Hover, HoverTo;                      // 0–1：压暗层 / 专辑信息 / 投影
        public float Scale = 1;
        public double AppearAt = double.NegativeInfinity; // 进场动画开始的时刻（毫秒）

        // 绘制资源：只在视口附近时存在
        public CanvasBitmap? Bitmap;                      // 清晰封面，或生成的图案
        public CanvasImageBrush? ImageBrush;
        public int BitmapPx;
        public double ImageAt;
        public CanvasBitmap? Thumb;                       // 先到的模糊小图
        public CanvasImageBrush? ThumbBrush;
        public double ThumbAt;
        public bool Loading, Failed, Generated;
        public CanvasLinearGradientBrush? Placeholder;
        public CanvasTextLayout? TitleLayout, SubLayout;

        public bool NoCover => CoverPath == null || Failed;
        public bool HasResources => Bitmap != null || Thumb != null || Placeholder != null || TitleLayout != null;
    }

    private const double Gap = 3;                 // 标准尺寸下的缝宽（随缩放等比变化）
    private const double TargetCell = 160;
    private const double MinCell = 80, MaxCell = 360;
    private const double Pad = 14;
    private const double HeaderH = 64;
    private const double Buffer = 500;            // 视口外多远开始预载封面
    private const double KeepBuffer = 1400;       // 超出这个距离就释放
    private const int MaxLoads = 4;               // 同时解码的封面数
    private const int ThumbPx = 20;
    private const float Radius = 4;

    private readonly object _sync = new();
    private readonly Grid _root = new();
    private readonly Grid _surface = new();                 // 接收拖动 / 滚轮 / 点击的整块区域
    private readonly CanvasAnimatedControl _cv = new();     // 整面墙画在这里（独立渲染线程）
    private readonly TextBlock _count = new();
    private readonly TextBlock _sortText = new();
    private readonly TextBlock _empty = new();
    private readonly Microsoft.UI.Composition.Compositor _compositor;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dq;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _relayout;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _spectrumIdle;
    private readonly MenuFlyout _tileMenu = new();
    private readonly List<RadioMenuFlyoutItem> _sortItems = new();
    private readonly Stopwatch _time = Stopwatch.StartNew();

    private List<Tile> _all = new();
    private List<Tile> _tiles = new();          // 按当前排序（也是排版顺序，Row 单调不减）
    private readonly Dictionary<string, Tile> _byTrackId = new();
    private SortMode _sort = SortMode.Recent;
    private int _shuffleSeed;
    private int _cols, _rows;
    private double _cell = TargetCell, _gap = Gap;
    private double _cellPref = TargetCell;                  // 用户缩放到的格子大小（重排后保持）
    private double _laidOutWidth;                           // > 0 表示已经排过版
    private double _wallW, _wallH;
    private bool _needsLayout;
    private List<int[]> _occ = new();

    // 界面线程缓存给渲染线程用的视口信息
    private double _vw, _vh, _raster = 1;
    private bool _visible;

    // 平移状态：墙左上角在视口里的位置
    private double _px, _py;
    private double _vx, _vy;                                // 惯性速度（像素 / 毫秒）
    private double? _tx, _ty;                               // 平滑移动的目标位置（滚轮、定位）
    private bool _panning;
    private double _lastFrame = double.NaN;
    private double _residentX = double.NaN, _residentY = double.NaN;

    // 缩放：先按视觉比例平滑缩放，停稳后再真正改格子大小
    private float _zoomVis = 1, _zoomTo = 1;
    private Vector2 _zoomAnchor;

    // 拖动景深
    private float _depth = 1;
    private double _dragSpeed, _lastMoveAt;

    // 背景柔光
    private Vector3 _glowA = new(0.06f, 0.06f, 0.08f), _glowB = new(0.06f, 0.06f, 0.08f);
    private Vector3 _glowATo, _glowBTo;
    private bool _glowDirty = true;

    // 分组提示
    private string? _label;
    private float _labelAlpha;
    private double _labelHold;
    private CanvasTextLayout? _labelLayout;

    // 缩略地图
    private float _miniAlpha;
    private double _miniHold;                               // 快速拖动后保持显示到这个时刻
    private bool _miniHover, _miniDrag;
    private const double MiniSpeed = 0.9;                   // 超过这个速度（像素 / 毫秒）才显示缩略地图

    // 鼠标位置（光泽）
    private Point _mouse;
    private bool _mouseIn;

    // 正在播放：频谱
    private readonly float[] _bars = { 0.3f, 0.5f, 0.4f, 0.3f };
    private double _lastSpectrum = double.NegativeInfinity;
    private bool _spectrumSubscribed;

    // 每帧要推进的东西
    private readonly HashSet<Tile> _anim = new();
    private double _entranceUntil, _fadeUntil;

    // 投递到界面线程的请求（合并重复的）
    private bool _kickQueued, _drawQueued, _pauseQueued, _residencyQueued;

    // 封面加载 / 绘制资源
    private CanvasDevice? _device;
    private int _gen;                                       // 换了曲库 / 重排 / 设备丢失后，旧的加载结果作废
    private int _loadsRunning;
    private List<Tile> _queue = new();
    private CanvasLinearGradientBrush? _infoBrush;
    private CanvasRadialGradientBrush? _vignette, _sheen, _glowBrushA, _glowBrushB;
    private Color _glowColA, _glowColB;
    private CanvasTextLayout? _badgeGlyph;

    // 拖动
    private uint? _pressId;
    private Point _pressPt;
    private double _pressPx, _pressPy;
    private bool _dragging;
    private readonly List<(double t, double x, double y)> _samples = new();
    private Tile? _pressedTile;
    private Tile? _hovered;
    private bool _animateNext;
    private string? _currentKey;
    private Tile? _menuTile;
    private Color _accent = Color.FromArgb(255, 150, 140, 255);

    private readonly Button _locateBtn = new();

    private static readonly Brush White = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255));
    private static readonly Brush Faint = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    private static readonly Color Bg = Color.FromArgb(255, 13, 13, 17);
    private static readonly Vector3 GlowDefault = new(0.06f, 0.06f, 0.08f);
    private static readonly string IconFont = Environment.OSVersion.Version.Build >= 22000 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    private double Now => _time.Elapsed.TotalMilliseconds;

    public CoverGalleryView()
    {
        RequestedTheme = ElementTheme.Dark;           // 画廊本身是深色的“展厅”
        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _root.Background = new SolidColorBrush(Bg);

        _cv.ClearColor = Bg;
        _cv.IsFixedTimeStep = false;                  // 跟显示器刷新率走（120 Hz 屏不锁 60）
        _cv.Paused = true;                            // 有动画时才跑
        _cv.Update += OnUpdate;
        _cv.Draw += OnDraw;
        _cv.CreateResources += (s, e) =>
        {
            lock (_sync)
            {
                if (e.Reason != CanvasCreateResourcesReason.FirstTime) DropAllResources();
                _device = s.Device;
            }
            QueueResidency();
            RequestDraw();
        };
        _surface.Background = new SolidColorBrush(Colors.Transparent);
        _surface.Children.Add(_cv);
        _surface.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPressed), true);
        _surface.AddHandler(PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnMoved), true);
        _surface.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnReleased), true);
        _surface.PointerCanceled += (s, e) => EndPress(e, pick: false);
        _surface.PointerCaptureLost += (s, e) => EndPress(e, pick: false);
        _surface.PointerExited += (s, e) =>
        {
            lock (_sync)
            {
                _mouseIn = false;
                _miniHover = false;
                if (_pressId == null) SetHovered(null);
                Kick();
            }
        };
        _surface.PointerWheelChanged += OnWheel;
        _surface.RightTapped += OnRightTapped;

        _empty.Text = L.T("曲库里还没有可展示的封面");
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.Foreground = Faint;
        _empty.Visibility = Visibility.Collapsed;

        BuildTileMenu();

        _root.Children.Add(_surface);
        _root.Children.Add(_empty);
        _root.Children.Add(BuildHeader());
        Content = _root;

        _relayout = _dq.CreateTimer();
        _relayout.Interval = TimeSpan.FromMilliseconds(160);
        _relayout.IsRepeating = false;
        _relayout.Tick += (s, e) => { lock (_sync) { ClampNow(); AfterPan(force: true); _glowDirty = true; Kick(); } };
        // 频谱停了（暂停播放）：补画一帧，把角标换回喇叭
        _spectrumIdle = _dq.CreateTimer();
        _spectrumIdle.Interval = TimeSpan.FromMilliseconds(400);
        _spectrumIdle.IsRepeating = false;
        _spectrumIdle.Tick += (s, e) => RequestDraw();
        SizeChanged += (s, e) =>
        {
            lock (_sync)
            {
                _vw = ActualWidth; _vh = ActualHeight;
                _raster = XamlRoot?.RasterizationScale ?? 1;
                if (ActualWidth < 200) return;
                if (_needsLayout) { Relayout(keepAnchor: false); return; }
                // 窗口大小变了：墙不重排（免得封面乱跳），只把位置收回边界内
                ClampNow();
            }
            RequestDraw();
            _relayout.Stop(); _relayout.Start();
        };
        // 打开画廊时先 Load 再设为可见：等真正可见、量出宽度后再排版
        RegisterPropertyChangedCallback(VisibilityProperty, (s, dp) =>
        {
            bool vis = Visibility == Visibility.Visible;
            lock (_sync) _visible = vis;
            SubscribeSpectrum(vis);
            if (!vis) { _cv.Paused = true; return; }
            RequestDraw();
            if (!_needsLayout) return;
            _dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                lock (_sync)
                    if (_needsLayout && Visibility == Visibility.Visible && ActualWidth >= 200) Relayout(keepAnchor: false);
            });
        });
        _visible = Visibility == Visibility.Visible;
        ActualThemeChanged += (s, e) => UpdateAccent();
        Loaded += (s, e) =>
        {
            lock (_sync) _raster = XamlRoot?.RasterizationScale ?? 1;
            UpdateAccent();
            SubscribeSpectrum(Visibility == Visibility.Visible);
            ThemeService.AccentChanged -= UpdateAccent;
            ThemeService.AccentChanged += UpdateAccent;
        };
        Unloaded += (s, e) => { SubscribeSpectrum(false); ThemeService.AccentChanged -= UpdateAccent; };
    }

    private void UpdateAccent()
    {
        if (ThemeService.AccentFill(this) is SolidColorBrush b) lock (_sync) _accent = b.Color;
        RequestDraw();
    }

    // ---------------- 和渲染循环打交道（都在锁外、界面线程上执行） ----------------

    /// <summary>有动画要跑：恢复渲染循环。任何线程、持不持锁都可以调用。</summary>
    private void Kick()
    {
        lock (_sync) { if (_kickQueued) return; _kickQueued = true; }
        _dq.TryEnqueue(() =>
        {
            lock (_sync) _kickQueued = false;
            if (_cv.Paused) _cv.Paused = false;
        });
    }

    /// <summary>静态内容变了：循环暂停时补画一帧（在跑时下一帧自然会画）。</summary>
    private void RequestDraw()
    {
        lock (_sync) { if (_drawQueued) return; _drawQueued = true; }
        _dq.TryEnqueue(() =>
        {
            lock (_sync) _drawQueued = false;
            _cv.Invalidate();
        });
    }

    private bool IsIdle(double now) =>
        !_panning && !_dragging && _anim.Count == 0 && now >= _entranceUntil && now >= _fadeUntil
        && _zoomVis == _zoomTo && _depth == 1 && !_glowDirty && _glowA == _glowATo && _glowB == _glowBTo
        && _labelAlpha == LabelTarget(now) && _miniAlpha == MiniTarget(now);

    /// <summary>渲染线程发现没事可做：请界面线程暂停循环（届时再确认一次，期间有新动画就不停）。</summary>
    private void QueuePause()
    {
        if (_pauseQueued) return;
        _pauseQueued = true;
        _dq.TryEnqueue(() =>
        {
            bool idle;
            lock (_sync) { _pauseQueued = false; idle = IsIdle(Now); if (idle) _lastFrame = double.NaN; }
            if (idle && !_cv.Paused) _cv.Paused = true;
        });
    }

    private void QueueResidency()
    {
        lock (_sync) { if (_residencyQueued) return; _residencyQueued = true; }
        _dq.TryEnqueue(() =>
        {
            lock (_sync) { _residencyQueued = false; UpdateResidency(); }
        });
    }

    // ---------------- 频谱（正在播放的角标、呼吸边框） ----------------

    private void SubscribeSpectrum(bool on)
    {
        if (on == _spectrumSubscribed) return;
        _spectrumSubscribed = on;
        if (on) AudioEngineService.Instance.SpectrumChanged += OnSpectrum;
        else AudioEngineService.Instance.SpectrumChanged -= OnSpectrum;
    }

    private void OnSpectrum(float[] bands)
    {
        if (bands == null || bands.Length < 4) return;
        bool show;
        lock (_sync)
        {
            // 中低频分成 4 段；上升快、回落慢
            int usable = Math.Max(4, bands.Length * 2 / 3);
            for (int i = 0; i < 4; i++)
            {
                int a = i * usable / 4, b = Math.Max(a + 1, (i + 1) * usable / 4);
                float sum = 0;
                for (int k = a; k < b && k < bands.Length; k++) sum += bands[k];
                float target = Math.Clamp(sum / (b - a), 0f, 1f);
                _bars[i] += (target - _bars[i]) * (target > _bars[i] ? 0.6f : 0.25f);
            }
            _lastSpectrum = Now;
            show = _currentKey != null && _visible;
        }
        if (!show) return;
        RequestDraw();
        if (_dq.HasThreadAccess) { _spectrumIdle.Stop(); _spectrumIdle.Start(); }
        else _dq.TryEnqueue(() => { _spectrumIdle.Stop(); _spectrumIdle.Start(); });
    }

    // ---------------- 顶栏 ----------------

    private FrameworkElement BuildHeader()
    {
        var title = new TextBlock
        {
            Text = L.T("封面画廊"), FontSize = 20, FontWeight = FontWeights.SemiBold,
            Foreground = White, VerticalAlignment = VerticalAlignment.Center
        };
        _count.FontSize = 12;
        _count.Foreground = Faint;
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(0, 4, 0, 0);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { title, _count } };

        // 排序
        var sortMenu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (var (mode, text) in new[] { (SortMode.Recent, "最近添加"), (SortMode.Album, "专辑名"), (SortMode.Artist, "歌手"), (SortMode.Shuffle, "随机排列") })
        {
            var item = new RadioMenuFlyoutItem { Text = L.T(text), GroupName = "gallerySort", IsChecked = mode == _sort, Tag = mode };
            item.Click += (s, e) => SetSort(mode);
            _sortItems.Add(item);
            sortMenu.Items.Add(item);
        }
        _sortText.Text = L.T("最近添加");
        _sortText.FontSize = 12;
        var sortBtn = new DropDownButton
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new FontIcon { Glyph = "", FontSize = 13 }, _sortText }
            },
            Flyout = sortMenu,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 5, 10, 5)
        };
        ToolTipService.SetToolTip(sortBtn, L.T("排序"));

        var randomBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new FontIcon { Glyph = "", FontSize = 13 }, new TextBlock { Text = L.T("随机播放一张"), FontSize = 12 } }
            },
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 5, 12, 5)
        };
        randomBtn.Click += (s, e) => PickRandom();

        _locateBtn.Content = new FontIcon { Glyph = "", FontSize = 13 };
        _locateBtn.Width = 34; _locateBtn.Height = 34; _locateBtn.Padding = new Thickness(0);
        _locateBtn.CornerRadius = new CornerRadius(17);
        _locateBtn.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(_locateBtn, L.T("定位到正在播放"));
        _locateBtn.Click += (s, e) => LocateCurrent();

        var closeBtn = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 13 },
            Width = 34, Height = 34, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17)
        };
        ToolTipService.SetToolTip(closeBtn, L.T("关闭画廊 (Esc)"));
        closeBtn.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);

        var header = new Grid
        {
            Height = HeaderH,
            Padding = new Thickness(Pad + 8, 0, Pad + 4, 6),
            VerticalAlignment = VerticalAlignment.Top,
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            // 半透明：下面的背景柔光能透上来
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(200, 0, 0, 0), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(140, 0, 0, 0), Offset = 0.6 },
                    new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 1 }
                }
            }
        };
        header.Children.Add(left);
        Grid.SetColumn(_locateBtn, 1); header.Children.Add(_locateBtn);
        Grid.SetColumn(sortBtn, 2); header.Children.Add(sortBtn);
        Grid.SetColumn(randomBtn, 3); header.Children.Add(randomBtn);
        Grid.SetColumn(closeBtn, 4); header.Children.Add(closeBtn);
        return header;
    }

    private void BuildTileMenu()
    {
        var play = new MenuFlyoutItem { Text = L.T("播放专辑"), Icon = new FontIcon { Glyph = "" } };
        play.Click += (s, e) => { lock (_sync) if (_menuTile != null) Pick(_menuTile, shuffle: false); };
        var shuffle = new MenuFlyoutItem { Text = L.T("随机播放"), Icon = new FontIcon { Glyph = "" } };
        shuffle.Click += (s, e) => { lock (_sync) if (_menuTile != null) Pick(_menuTile, shuffle: true); };
        _tileMenu.Items.Add(play);
        _tileMenu.Items.Add(shuffle);
    }

    // ---------------- 正在播放 ----------------

    /// <summary>当前播放曲目变了：墙上对应的专辑亮起强调色边框，顶栏的“定位”按钮可用。</summary>
    public void SetCurrent(TrackItem? t)
    {
        lock (_sync)
        {
            _currentKey = t != null && _byTrackId.TryGetValue(t.Id, out var tile) ? tile.Key : null;
            _locateBtn.Visibility = _currentKey != null ? Visibility.Visible : Visibility.Collapsed;
        }
        RequestDraw();
    }

    // ---------------- 数据 ----------------

    private static readonly Regex DiscFolder = new(@"^(cd|disc|disk|dvd)\s*[\-_.]?\s*\d+$|^\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly StringComparer Natural = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.NumericOrdering | CompareOptions.IgnoreCase);

    private static bool NoAlbum(TrackItem t) =>
        string.IsNullOrWhiteSpace(t.Album) || t.Album.Trim() is "Unknown Album" or "未知专辑";

    /// <summary>同名专辑按所在文件夹区分（不同歌手都叫《Greatest Hits》时不会被并成一张）；CD1 / Disc 2 这类子目录算同一张。</summary>
    private static string AlbumKey(TrackItem t)
    {
        if (NoAlbum(t)) return "track:" + t.Id;
        string dir = "";
        try
        {
            var file = t.IsCueTrack && t.CueTrackPath.Length > 0 ? t.CueTrackPath : t.FilePath;
            dir = Path.GetDirectoryName(file) ?? "";
            if (DiscFolder.IsMatch(Path.GetFileName(dir))) dir = Path.GetDirectoryName(dir) ?? dir;
        }
        catch { }
        return "album:" + t.Album.Trim().ToLowerInvariant() + "|" + dir.ToLowerInvariant();
    }

    /// <summary>用曲库内容（重新）构建整面墙；没有专辑信息的歌单独占一块。</summary>
    public void Load(IEnumerable<TrackItem> library)
    {
        // 分组在锁外做（曲库大时要一会儿），渲染线程照常画旧的
        var groups = new Dictionary<string, List<TrackItem>>();
        foreach (var t in library)
        {
            if (t.IsOnline || t.IsMissing) continue;
            string key = AlbumKey(t);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<TrackItem>();
            list.Add(t);
        }

        // 月份提示跟着软件的界面语言走，而不是系统语言
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(L.Code); } catch { culture = CultureInfo.CurrentUICulture; }
        var all = new List<Tile>(groups.Count);
        var byTrack = new Dictionary<string, Tile>();
        foreach (var (key, members) in groups)
        {
            var tracks = members
                .OrderBy(x => Path.GetDirectoryName(x.FilePath) ?? "", Natural)
                .ThenBy(x => x.CueTrackNum)
                .ThenBy(x => Path.GetFileName(x.FilePath), Natural)
                .ToList();
            string? cover = null;
            foreach (var x in tracks)
            {
                var c = x.CoverPath;
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) { cover = c; break; }
            }
            var artists = tracks.Select(x => x.Artist).Distinct().ToList();
            bool single = key.StartsWith("track:");
            string artist = artists.Count == 1 ? artists[0] : L.T("群星");
            string title = single ? tracks[0].Title : tracks[0].Album.Trim();
            long added = tracks.Max(x => x.AddedTimestamp);
            string? month = null;
            if (added > 0)
                try { month = DateTimeOffset.FromUnixTimeSeconds(added).ToLocalTime().ToString("Y", culture); } catch { }
            double hue = Hash(title) % 360;
            var baseTint = ToVec(Hsv(hue, 0.30, 0.42));
            var tile = new Tile
            {
                Key = key,
                Tracks = tracks,
                Title = title,
                Artist = artist,
                Sub = tracks.Count > 1 ? $"{artist} · {L.F("{0} 首", tracks.Count)}" : artist,
                Letter = Initial(title),
                Month = month,
                CoverPath = cover,
                Added = added,
                Hue = hue,
                BaseTint = baseTint,
                Tint = baseTint
            };
            // 大小按专辑算哈希，稳定不乱跳；没封面的只占一格，曲目多的专辑更容易成为大块
            int roll = Hash(key) % 100;
            if (cover != null)
            {
                bool rich = tracks.Count >= 6;
                tile.Pref = rich && roll < 7 ? 3 : roll < (rich ? 34 : 18) ? 2 : 1;
            }
            all.Add(tile);
            foreach (var x in tracks) byTrack[x.Id] = tile;
        }

        _count.Text = L.F("{0} 张专辑", all.Count);
        _empty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        lock (_sync)
        {
            DropAllResources();
            _hovered = null; _pressedTile = null;
            _anim.Clear();
            _laidOutWidth = 0;          // 新方块还没排版，排好之前不画也不加载
            _all = all;
            _byTrackId.Clear();
            foreach (var (k, v) in byTrack) _byTrackId[k] = v;
            ApplySort();
            StopMotion();
            _animateNext = true;
            _needsLayout = true;
            if (ActualWidth >= 200 && Visibility == Visibility.Visible) Relayout(keepAnchor: false);
        }
        RequestDraw();
    }

    private void SetSort(SortMode mode)
    {
        lock (_sync)
        {
            if (mode == SortMode.Shuffle) _shuffleSeed = Random.Shared.Next();
            else if (mode == _sort) return;
            _sort = mode;
            SetHovered(null);
            ApplySort();
            StopMotion();
            _animateNext = true;
            _label = null;
            Relayout(keepAnchor: false);
        }
        foreach (var i in _sortItems) i.IsChecked = (SortMode)i.Tag == mode;
        _sortText.Text = _sortItems.First(i => (SortMode)i.Tag == mode).Text;
    }

    private void ApplySort()
    {
        _tiles = _sort switch
        {
            SortMode.Album => _all.OrderBy(t => t.Title, Natural).ThenBy(t => t.Artist, Natural).ToList(),
            SortMode.Artist => _all.OrderBy(t => t.Artist, Natural).ThenBy(t => t.Title, Natural).ToList(),
            SortMode.Shuffle => _all.OrderBy(t => Hash(t.Key + _shuffleSeed)).ToList(),
            _ => _all.OrderByDescending(t => t.Added).ThenBy(t => t.Title, Natural).ToList()
        };
    }

    /// <summary>画廊关掉后释放所有封面图和绘制资源（下次打开时重新按需加载）。</summary>
    public void ReleaseImages()
    {
        _cv.Paused = true;
        lock (_sync)
        {
            StopMotion();
            DropAllResources();
            _anim.Clear();
            _hovered = null; _pressedTile = null;
            _zoomVis = _zoomTo = 1;
            _depth = 1;
            _miniAlpha = 0; _miniHold = 0; _miniHover = _miniDrag = false;
            _laidOutWidth = 0;
            _needsLayout = true;
        }
    }

    private static int Hash(string s)
    {
        int h = 17;
        foreach (char ch in s) h = unchecked(h * 31 + ch);
        return h & 0x7fffffff;
    }

    // ---------------- 布局：正方形马赛克（持锁） ----------------

    private void Relayout(bool keepAnchor)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 200 || h < 150) return;
        _vw = w; _vh = h;
        _raster = XamlRoot?.RasterizationScale ?? 1;
        _needsLayout = false;

        // 记住视口中间那一块，重排后把它放回中间
        Tile? anchor = null;
        if (keepAnchor && _laidOutWidth > 0 && _tiles.Count > 0)
        {
            double cx = w / 2 - _px, cy = (HeaderH + h) / 2 - _py;
            anchor = _tiles.OrderBy(t => Math.Abs(t.X + t.W / 2 - cx) + Math.Abs(t.Y + t.W / 2 - cy)).First();
        }

        // 列数按标准格子算，让整面墙的长宽比接近视口（缩放不影响排法）
        double area = _tiles.Sum(t => Math.Pow(Math.Min(t.Pref, 3), 2));
        double aspect = (w - Pad * 2) / Math.Max(100, h - HeaderH - Pad);
        int screenCols = Math.Max(3, (int)Math.Ceiling((w - Pad * 2 + Gap) / (TargetCell + Gap)));
        _cols = Math.Max(screenCols, (int)Math.Round(Math.Sqrt(area * aspect)));
        int maxSize = _cols >= 7 ? 3 : _cols >= 4 ? 2 : 1;

        var forced = new HashSet<Tile>();
        int rows = Pack(maxSize, forced);
        // 墙的底边：大块会在最后几行留下空洞，把伸进这些行的大块降成一格再排一次
        for (int pass = 0; pass < 4; pass++)
        {
            int holeRow = FirstRowWithHole(rows);
            if (holeRow < 0) break;
            bool changed = false;
            foreach (var t in _tiles)
                if (t.Size > 1 && t.Row + t.Size > holeRow && forced.Add(t)) changed = true;
            if (!changed) break;
            rows = Pack(maxSize, forced);
        }
        _rows = rows;

        _gen++;                     // 还在路上的封面按旧尺寸解码，作废
        _queue.Clear();
        _cell = _cellPref;
        _gap = _cell * Gap / TargetCell;
        ApplyCell();
        foreach (var t in _tiles)
        {
            t.AppearAt = double.NegativeInfinity;
            t.Hover = t.HoverTo = 0; t.Scale = 1;
        }
        _anim.Clear();
        _hovered = null; _pressedTile = null;
        _zoomVis = _zoomTo = 1;
        _laidOutWidth = w;

        if (anchor != null) { _px = w / 2 - (anchor.X + anchor.W / 2); _py = (HeaderH + h) / 2 - (anchor.Y + anchor.W / 2); }
        else { (_px, _py) = (Pad, HeaderH); }    // 从左上角开始（最先排的专辑在那里）
        ClampNow();
        if (_animateNext) StartEntrance();
        _animateNext = false;
        _glowDirty = true;
        AfterPan(force: true);
        Kick();
        RequestDraw();
    }

    /// <summary>按当前格子大小算出每块的位置和墙的尺寸；尺寸变了的块丢掉文字排版。</summary>
    private void ApplyCell()
    {
        double cs = _cell + _gap;
        foreach (var t in _tiles)
        {
            double oldW = t.W;
            t.X = t.Col * cs;
            t.Y = t.Row * cs;
            t.W = t.Size * _cell + (t.Size - 1) * _gap;
            if (oldW != t.W) DisposeText(t);
        }
        _wallW = Math.Max(0, _cols * cs - _gap);
        _wallH = Math.Max(0, _rows * cs - _gap);
    }

    /// <summary>进场：视口里的方块从中心往外一圈圈浮现。</summary>
    private void StartEntrance()
    {
        double vw = _vw, vh = _vh, now = Now;
        double vx0 = -_px, vy0 = -_py, cx = vx0 + vw / 2, cy = vy0 + vh / 2;
        double diag = Math.Max(1, Math.Sqrt(vw * vw + vh * vh) / 2);
        foreach (var t in _tiles)
        {
            if (t.X + t.W <= vx0 || t.X >= vx0 + vw || t.Y + t.W <= vy0 || t.Y >= vy0 + vh) continue;
            double dx = t.X + t.W / 2 - cx, dy = t.Y + t.W / 2 - cy;
            t.AppearAt = now + Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / diag, 0, 1) * 450;
        }
        _entranceUntil = now + 450 + 600;
        Kick();
    }

    /// <summary>按顺序把每块放进第一个空格；放不下想要的大小就缩小，所以中间永远没有空洞。返回总行数。</summary>
    private int Pack(int maxSize, HashSet<Tile> forced)
    {
        _occ = new List<int[]>();
        int scanFrom = 0;
        for (int i = 0; i < _tiles.Count; i++)
        {
            var t = _tiles[i];
            int r = scanFrom, c = 0;
            while (true)
            {
                Ensure(r);
                c = Array.IndexOf(_occ[r], 0);
                if (c >= 0) break;
                r++;
            }
            int s = forced.Contains(t) ? 1 : Math.Min(t.Pref, maxSize);
            while (s > 1 && !Free(r, c, s)) s--;
            t.Row = r; t.Col = c; t.Size = s;
            for (int y = r; y < r + s; y++) { Ensure(y); for (int x = c; x < c + s; x++) _occ[y][x] = i + 1; }
            while (scanFrom < _occ.Count && Array.IndexOf(_occ[scanFrom], 0) < 0) scanFrom++;
        }
        int rows = _occ.Count;
        while (rows > 0 && _occ[rows - 1].All(v => v == 0)) rows--;
        return rows;
    }

    private void Ensure(int r)
    {
        while (_occ.Count <= r) _occ.Add(new int[_cols]);
    }

    private bool Free(int r, int c, int s)
    {
        if (c + s > _cols) return false;
        for (int y = r; y < r + s; y++)
        {
            if (y >= _occ.Count) continue;
            for (int x = c; x < c + s; x++) if (_occ[y][x] != 0) return false;
        }
        return true;
    }

    /// <summary>除最后一行外第一个还有空格的行（最后一行不满是正常的）；没有返回 -1。</summary>
    private int FirstRowWithHole(int rows)
    {
        for (int r = 0; r < rows - 1; r++)
            if (Array.IndexOf(_occ[r], 0) >= 0) return r;
        // 最后一行：空格必须都在右端，否则也算空洞
        if (rows > 0)
        {
            var last = _occ[rows - 1];
            int firstEmpty = Array.IndexOf(last, 0);
            if (firstEmpty >= 0 && last.Skip(firstEmpty).Any(v => v != 0)) return rows - 1;
        }
        return -1;
    }

    /// <summary>屏幕坐标下的那一块（墙缝、墙外、缩略地图上返回 null）。持锁调用。</summary>
    private Tile? HitTest(Point p)
    {
        if (_laidOutWidth <= 0 || _occ.Count == 0) return null;
        if (MiniActive(out var mr) && Inflate(mr, 6).Contains(p)) return null;
        double x = p.X - _px, y = p.Y - _py, cs = _cell + _gap;
        if (x < 0 || y < 0) return null;
        int c = (int)(x / cs), r = (int)(y / cs);
        if (c >= _cols || r >= _occ.Count) return null;
        int idx = _occ[r][c] - 1;
        if (idx < 0 || idx >= _tiles.Count) return null;
        var t = _tiles[idx];
        return x <= t.X + t.W && y <= t.Y + t.W ? t : null;
    }

    // ---------------- 平移：拖动 / 惯性 / 回弹 / 滚轮 ----------------

    private (double min, double max) BoundsX()
    {
        double w = _vw;
        if (_wallW + Pad * 2 <= w) { double c = (w - _wallW) / 2; return (c, c); }
        return (w - Pad - _wallW, Pad);
    }

    private (double min, double max) BoundsY()
    {
        double h = _vh;
        if (_wallH + HeaderH + Pad <= h) { double c = HeaderH + (h - HeaderH - Pad - _wallH) / 2; return (c, c); }
        return (h - Pad - _wallH, HeaderH);
    }

    private static double Clamp(double v, (double min, double max) b) => Math.Clamp(v, b.min, b.max);

    /// <summary>拖过边缘时的“橡皮筋”：越往外拉越费劲。</summary>
    private static double Rubber(double v, (double min, double max) b)
    {
        static double Damp(double over) => 220 * (1 - 1 / (over / 220 * 0.55 + 1));
        if (v > b.max) return b.max + Damp(v - b.max);
        if (v < b.min) return b.min - Damp(b.min - v);
        return v;
    }

    private void ClampNow()
    {
        _px = Clamp(_px, BoundsX());
        _py = Clamp(_py, BoundsY());
    }

    /// <summary>位置变了：移动超过一段距离才（到界面线程上）重新检查哪些封面该加载 / 释放。持锁调用，哪个线程都行。</summary>
    private void AfterPan(bool force = false)
    {
        if (force || double.IsNaN(_residentX) || Math.Abs(_px - _residentX) > 60 || Math.Abs(_py - _residentY) > 60)
        {
            _residentX = _px; _residentY = _py;
            QueueResidency();
        }
    }

    private void StopMotion()
    {
        _vx = _vy = 0;
        _tx = _ty = null;
        _panning = false;
    }

    private void StartPan()
    {
        _panning = true;
        Kick();
    }

    // ---------------- 渲染线程：每帧推进 ----------------

    private void OnUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        lock (_sync)
        {
            double now = Now;
            double dt = double.IsNaN(_lastFrame) ? 16 : Math.Clamp(now - _lastFrame, 1, 50);
            _lastFrame = now;

            bool moving = _panning || _dragging;
            if (_panning && !_dragging)
            {
                _panning = StepPan(dt);
                if (_panning) AfterPan();
                else { ClampNow(); AfterPan(force: true); }
            }
            StepZoom(dt);
            StepDepth(dt, now);
            StepTiles(dt);
            StepGlow(dt, moving);
            StepOverlays(dt, now, moving);
            if (IsIdle(now)) QueuePause();
        }
    }

    private bool StepPan(double dt)
    {
        var bx = BoundsX(); var by = BoundsY();
        if (_tx != null || _ty != null)
        {
            // 平滑移到目标（滚轮 / 定位 / 缩略地图）
            bool busy = false;
            double k = 1 - Math.Exp(-dt / 90);
            if (_tx is double tx) { _px += (tx - _px) * k; if (Math.Abs(tx - _px) < 0.5) { _px = tx; _tx = null; } else busy = true; }
            if (_ty is double ty) { _py += (ty - _py) * k; if (Math.Abs(ty - _py) < 0.5) { _py = ty; _ty = null; } else busy = true; }
            return busy;
        }
        // 惯性滑动；滑出边界后速度迅速衰减，再弹回来
        _px += _vx * dt; _py += _vy * dt;
        double f = Math.Pow(0.9955, dt);
        _vx *= f; _vy *= f;
        double back = 1 - Math.Exp(-dt / 70);
        if (_px > bx.max || _px < bx.min) { _vx *= Math.Pow(0.8, dt / 16); if (Math.Abs(_vx) < 0.3) { _vx = 0; _px += (Clamp(_px, bx) - _px) * back; } }
        if (_py > by.max || _py < by.min) { _vy *= Math.Pow(0.8, dt / 16); if (Math.Abs(_vy) < 0.3) { _vy = 0; _py += (Clamp(_py, by) - _py) * back; } }
        if (Math.Abs(_vx) < 0.01) _vx = 0;
        if (Math.Abs(_vy) < 0.01) _vy = 0;
        bool outX = Math.Abs(Clamp(_px, bx) - _px) > 0.5, outY = Math.Abs(Clamp(_py, by) - _py) > 0.5;
        return _vx != 0 || _vy != 0 || outX || outY;
    }

    private void StepZoom(double dt)
    {
        if (_zoomVis == _zoomTo) return;
        float k = (float)(1 - Math.Exp(-dt / 70));
        _zoomVis += (_zoomTo - _zoomVis) * k;
        if (Math.Abs(_zoomTo - _zoomVis) < 0.0015f) { _zoomVis = _zoomTo; CommitZoom(); }
    }

    /// <summary>视觉缩放停稳了：真正改格子大小，位置换算成缩放后的，让锚点下的内容不动。</summary>
    private void CommitZoom()
    {
        float z = _zoomVis;
        _zoomVis = _zoomTo = 1;
        if (Math.Abs(z - 1) < 1e-4) return;
        double ax = _zoomAnchor.X, ay = _zoomAnchor.Y;
        _px = ax + z * (_px - ax);
        _py = ay + z * (_py - ay);
        _cell = Math.Clamp(_cell * z, MinCell, MaxCell);
        _cellPref = _cell;
        _gap = _cell * Gap / TargetCell;
        ApplyCell();
        _vx = _vy = 0; _tx = _ty = null;
        _panning = true;                      // 缩完超出边界的话，交给回弹慢慢收回来
        _glowDirty = true;
        _residentX = double.NaN;
        AfterPan(force: true);
    }

    private void StepDepth(double dt, double now)
    {
        double speed = 0;
        if (_dragging) speed = now - _lastMoveAt < 70 ? _dragSpeed : 0;
        else if (_panning && _tx == null && _ty == null) speed = Math.Sqrt(_vx * _vx + _vy * _vy);
        float target = 1 - (float)Math.Min(0.035, speed * 0.011);
        float k = (float)(1 - Math.Exp(-dt / (target < _depth ? 90 : 160)));
        _depth += (target - _depth) * k;
        if (Math.Abs(target - _depth) < 0.0003f) _depth = target;
    }

    /// <summary>悬停 / 按下的过渡：压暗层和信息淡入淡出，封面浮起。</summary>
    private void StepTiles(double dt)
    {
        if (_anim.Count == 0) return;
        float kh = (float)(1 - Math.Exp(-dt / 55));
        float ks = (float)(1 - Math.Exp(-dt / 40));
        List<Tile>? done = null;
        foreach (var t in _anim)
        {
            float target = t == _pressedTile && !_dragging ? 0.98f : 1 + (HoverScale(t) - 1) * t.HoverTo;
            t.Hover += (t.HoverTo - t.Hover) * kh;
            t.Scale += (target - t.Scale) * ks;
            bool hDone = Math.Abs(t.HoverTo - t.Hover) < 0.003f, sDone = Math.Abs(target - t.Scale) < 0.0005f;
            if (hDone) t.Hover = t.HoverTo;
            if (sDone) t.Scale = target;
            if (hDone && sDone) (done ??= new()).Add(t);
        }
        if (done != null) foreach (var t in done) _anim.Remove(t);
    }

    /// <summary>背景柔光：左上、右下两团颜色分别取视口左右半边封面的主色，缓慢过渡。</summary>
    private void StepGlow(double dt, bool moving)
    {
        if (_glowDirty || moving)
        {
            _glowDirty = false;
            Vector3 a = Vector3.Zero, b = Vector3.Zero;
            double wa = 0, wb = 0, cs = _cell + _gap;
            if (_laidOutWidth > 0 && cs > 0)
            {
                int rowMin = (int)Math.Floor(-_py / cs) - 3, rowMax = (int)Math.Ceiling((_vh - _py) / cs) + 1;
                for (int i = FirstIndexAtRow(rowMin); i < _tiles.Count; i++)
                {
                    var t = _tiles[i];
                    if (t.Row > rowMax) break;
                    double cx = t.X + t.W / 2 + _px;
                    if (cx < -t.W || cx > _vw + t.W) continue;
                    double wgt = t.W * t.W;
                    if (cx < _vw / 2) { a += t.Tint * (float)wgt; wa += wgt; }
                    else { b += t.Tint * (float)wgt; wb += wgt; }
                }
            }
            _glowATo = wa > 0 ? Boost(a / (float)wa) : GlowDefault;
            _glowBTo = wb > 0 ? Boost(b / (float)wb) : _glowATo;
        }
        float k = (float)(1 - Math.Exp(-dt / 350));
        _glowA = Approach(_glowA, _glowATo, k);
        _glowB = Approach(_glowB, _glowBTo, k);
    }

    private static Vector3 Approach(Vector3 v, Vector3 to, float k)
    {
        var r = v + (to - v) * k;
        return Vector3.DistanceSquared(r, to) < 1e-6f ? to : r;
    }

    /// <summary>主色提亮饱和度、压到较暗的亮度，适合当背景光。</summary>
    private static Vector3 Boost(Vector3 c)
    {
        float gray = (c.X + c.Y + c.Z) / 3;
        c = Vector3.Clamp(new Vector3(gray) + (c - new Vector3(gray)) * 1.6f, Vector3.Zero, Vector3.One);
        float mx = Math.Max(c.X, Math.Max(c.Y, c.Z));
        if (mx < 0.02f) return GlowDefault;
        return c * (0.34f / mx);
    }

    private float LabelTarget(double now) => _label != null && now < _labelHold && WallLargerThanView ? 1 : 0;

    /// <summary>缩略地图只在快速拖动 / 甩动时出现，停下后片刻淡出；鼠标停在上面或正在用它时保持显示。</summary>
    private float MiniTarget(double now) => _miniDrag || now < _miniHold || (_miniHover && _miniAlpha > 0.05f) ? 1f : 0f;

    /// <summary>缩略地图当前可以点（看得见）。</summary>
    private bool MiniActive(out Rect r)
    {
        r = default;
        if (_miniAlpha < 0.05f && !_miniDrag) return false;
        if (MiniRect() is not Rect mr) return false;
        r = mr;
        return true;
    }

    private bool WallLargerThanView => _wallW + Pad * 2 > _vw + 1 || _wallH + HeaderH + Pad > _vh + 1;

    /// <summary>分组提示和缩略地图的淡入淡出。</summary>
    private void StepOverlays(double dt, double now, bool moving)
    {
        if (moving && _sort != SortMode.Shuffle && _laidOutWidth > 0)
        {
            var t = HitTestCenter();
            string? label = t == null ? _label : _sort switch
            {
                SortMode.Artist => t.Artist,
                SortMode.Album => t.Letter,
                _ => t.Month
            };
            if (label != _label) { _label = label; _labelLayout?.Dispose(); _labelLayout = null; }
            _labelHold = now + 700;
        }
        float k = (float)(1 - Math.Exp(-dt / 90));
        float lt = LabelTarget(now);
        _labelAlpha += (lt - _labelAlpha) * k;
        if (Math.Abs(lt - _labelAlpha) < 0.01f) _labelAlpha = lt;
        double speed = _dragging ? (now - _lastMoveAt < 70 ? _dragSpeed : 0)
            : _panning && _tx == null && _ty == null ? Math.Sqrt(_vx * _vx + _vy * _vy) : 0;
        if (speed > MiniSpeed) _miniHold = now + 900;
        float mt = MiniTarget(now);
        _miniAlpha += (mt - _miniAlpha) * k;
        if (Math.Abs(mt - _miniAlpha) < 0.01f) _miniAlpha = mt;
    }

    /// <summary>视口中心（顶栏以下）附近的那一块；正好落在缝上时往旁边找一点。</summary>
    private Tile? HitTestCenter()
    {
        double cx = _vw / 2, cy = (HeaderH + _vh) / 2;
        foreach (var (dx, dy) in new[] { (0.0, 0.0), (8.0, 8.0), (-8.0, -8.0) })
        {
            double x = cx + dx - _px, y = cy + dy - _py, cs = _cell + _gap;
            if (x < 0 || y < 0) continue;
            int c = (int)(x / cs), r = (int)(y / cs);
            if (c >= _cols || r >= _occ.Count) continue;
            int idx = _occ[r][c] - 1;
            if (idx >= 0 && idx < _tiles.Count) return _tiles[idx];
        }
        return null;
    }

    // ---------------- 输入（界面线程） ----------------

    private void Animate(Tile t)
    {
        _anim.Add(t);
        Kick();
    }

    private void OnPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !pt.Properties.IsLeftButtonPressed) return;
        bool capture = false;
        lock (_sync)
        {
            StopMotion();
            _zoomVis = _zoomTo;
            CommitZoom();
            ClampNow();
            _pressId = e.Pointer.PointerId;
            _pressPt = pt.Position;
            _pressPx = _px; _pressPy = _py;
            _dragging = false;
            _samples.Clear();
            // 按在缩略地图上：直接跳过去，按住拖动可以连续移动
            if (MiniActive(out var mr) && Inflate(mr, 6).Contains(pt.Position))
            {
                _miniDrag = true;
                _pressedTile = null;
                PanToMini(pt.Position, smooth: true);
                capture = true;
            }
            else
            {
                _pressedTile = HitTest(pt.Position);
                if (_pressedTile != null) Animate(_pressedTile);
            }
        }
        if (capture) _surface.CapturePointer(e.Pointer);
        RequestDraw();
    }

    private void OnMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        lock (_sync)
        {
            _mouse = p;
            _mouseIn = true;
            bool overMini = MiniActive(out var mr) && Inflate(mr, 6).Contains(p);
            if (overMini != _miniHover) { _miniHover = overMini; Kick(); }

            if (_pressId != e.Pointer.PointerId)
            {
                // 没按住：只是在墙上移动，更新悬停；同一块上移动时光泽跟着鼠标走
                if (_pressId == null && !_dragging)
                {
                    var t = HitTest(p);
                    if (t != _hovered) SetHovered(t);
                    else if (t != null) RequestDraw();
                }
                return;
            }
            if (_miniDrag)
            {
                PanToMini(p, smooth: false);
                e.Handled = true;
                return;
            }
            double dx = p.X - _pressPt.X, dy = p.Y - _pressPt.Y;
            if (!_dragging)
            {
                if (dx * dx + dy * dy < 36) return;     // 挪动不到 6 像素算点击
                _dragging = true;
                _surface.CapturePointer(e.Pointer);
                if (_pressedTile != null) { _anim.Add(_pressedTile); _pressedTile = null; }
                SetHovered(null);
                ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
                Kick();                                  // 拖动期间循环一直跑，每帧画最新位置
            }
            _px = Rubber(_pressPx + dx, BoundsX());
            _py = Rubber(_pressPy + dy, BoundsY());
            double now = Environment.TickCount64;
            if (_samples.Count > 0)
            {
                var last = _samples[^1];
                double span = Math.Max(1, now - last.t);
                double sp = Math.Sqrt((p.X - last.x) * (p.X - last.x) + (p.Y - last.y) * (p.Y - last.y)) / span;
                _dragSpeed += (sp - _dragSpeed) * 0.35;
            }
            _lastMoveAt = Now;
            _samples.Add((now, p.X, p.Y));
            while (_samples.Count > 2 && now - _samples[0].t > 90) _samples.RemoveAt(0);
            AfterPan();
        }
        e.Handled = true;
    }

    private void OnReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => EndPress(e, pick: true);

    private void EndPress(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e, bool pick)
    {
        var p = e.GetCurrentPoint(this).Position;
        bool release = false;
        lock (_sync)
        {
            if (_pressId != e.Pointer.PointerId) return;
            _pressId = null;
            if (_miniDrag)
            {
                _miniDrag = false;
                release = true;
                Kick();
            }
            else
            {
                var pressed = _pressedTile;
                if (pressed != null) { _pressedTile = null; Animate(pressed); }

                if (!_dragging)
                {
                    // 没拖动：在同一块上按下又松开，算点击
                    if (pick && pressed != null && HitTest(p) == pressed) Pick(pressed, shuffle: false);
                    return;
                }
                _dragging = false;
                _dragSpeed = 0;
                release = true;
                _vx = _vy = 0;
                if (_samples.Count >= 2)
                {
                    var a = _samples[0]; var b = _samples[^1];
                    double span = Math.Max(16, b.t - a.t);
                    if (Environment.TickCount64 - b.t < 80)          // 停住再松手就没有惯性
                    {
                        _vx = Math.Clamp((b.x - a.x) / span, -6, 6);
                        _vy = Math.Clamp((b.y - a.y) / span, -6, 6);
                    }
                }
                StartPan();
                SetHovered(HitTest(p));
            }
        }
        if (release) _surface.ReleasePointerCapture(e.Pointer);
    }

    private void OnWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        var props = pt.Properties;
        double d = props.MouseWheelDelta;
        bool ctrl = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0;
        bool horizontal = props.IsHorizontalMouseWheel
            || (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
        lock (_sync)
        {
            if (ctrl && !props.IsHorizontalMouseWheel)
            {
                // Ctrl+滚轮：以鼠标为中心缩放
                if (_zoomVis == 1 && _zoomTo == 1) _zoomAnchor = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
                double target = Math.Clamp(_cell * _zoomTo * Math.Pow(1.15, d / 120.0), MinCell, MaxCell);
                _zoomTo = (float)(target / _cell);
                StopMotion();
                SetHovered(null);
                Kick();
            }
            else
            {
                _vx = _vy = 0;
                if (horizontal)
                    _tx = Clamp((_tx ?? _px) + (props.IsHorizontalMouseWheel ? -d : d), BoundsX());
                else
                    _ty = Clamp((_ty ?? _py) + d, BoundsY());
                SetHovered(null);
                StartPan();
            }
        }
        e.Handled = true;
    }

    private void OnRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        Tile? t;
        lock (_sync) t = HitTest(e.GetPosition(this));
        if (t == null) return;
        lock (_sync) _menuTile = t;
        _tileMenu.ShowAt(_surface, e.GetPosition(_surface));
        e.Handled = true;
    }

    // ---------------- 缩略地图 ----------------

    /// <summary>右下角缩略地图的位置（墙比视口小时没有）。持锁调用。</summary>
    private Rect? MiniRect()
    {
        if (_laidOutWidth <= 0 || _wallW <= 0 || _wallH <= 0 || !WallLargerThanView) return null;
        double k = MiniScale();
        double mw = _wallW * k, mh = _wallH * k;
        return new Rect(_vw - 20 - mw, _vh - 20 - mh, mw, mh);
    }

    private double MiniScale() => Math.Min(180 / _wallW, 120 / _wallH);

    private static Rect Inflate(Rect r, double d) => new(r.X - d, r.Y - d, r.Width + d * 2, r.Height + d * 2);

    /// <summary>把视口中心移到缩略地图上点的位置。</summary>
    private void PanToMini(Point p, bool smooth)
    {
        if (MiniRect() is not Rect mr) return;
        double k = MiniScale();
        double wx = Math.Clamp((p.X - mr.X) / k, 0, _wallW), wy = Math.Clamp((p.Y - mr.Y) / k, 0, _wallH);
        double tx = Clamp(_vw / 2 - wx, BoundsX()), ty = Clamp((HeaderH + _vh) / 2 - wy, BoundsY());
        _vx = _vy = 0;
        if (smooth) { _tx = tx; _ty = ty; }
        else { _tx = _ty = null; _px = tx; _py = ty; AfterPan(); }
        _panning = true;
        SetHovered(null);
        Kick();
    }

    // ---------------- 封面加载 / 释放（界面线程，持锁） ----------------

    /// <summary>这块封面需要的图片边长（物理像素）：留出悬停放大的余量，最大 1024。</summary>
    private int NeedPx(Tile t) => (int)Math.Min(1024, Math.Ceiling(t.W * HoverScale(t) * _raster));

    /// <summary>视口附近的封面排队加载（近的先），没封面的生成图案，离得远的释放。</summary>
    private void UpdateResidency()
    {
        if (_tiles.Count == 0 || _laidOutWidth <= 0 || !_visible || _device == null) return;
        double vx0 = -_px, vy0 = -_py, vx1 = vx0 + _vw, vy1 = vy0 + _vh;
        double cx = (vx0 + vx1) / 2, cy = (vy0 + vy1) / 2;
        var want = new List<(Tile t, double d)>();
        foreach (var t in _tiles)
        {
            bool near = t.X + t.W > vx0 - Buffer && t.X < vx1 + Buffer && t.Y + t.W > vy0 - Buffer && t.Y < vy1 + Buffer;
            if (near)
            {
                int need = NeedPx(t);
                bool stale = t.Bitmap == null || t.BitmapPx < need * 0.85;
                if (!stale || t.Loading) continue;
                if (t.NoCover) GenerateArt(t, need);
                else
                {
                    double dx = t.X + t.W / 2 - cx, dy = t.Y + t.W / 2 - cy;
                    want.Add((t, dx * dx + dy * dy));
                }
                continue;
            }
            bool keep = t.X + t.W > vx0 - KeepBuffer && t.X < vx1 + KeepBuffer && t.Y + t.W > vy0 - KeepBuffer && t.Y < vy1 + KeepBuffer;
            if (!keep && t.HasResources) DisposeTile(t);
        }
        _queue = want.OrderBy(x => x.d).Select(x => x.t).ToList();
        PumpLoads();
    }

    private void PumpLoads()
    {
        var dev = _device;
        if (dev == null) return;
        while (_loadsRunning < MaxLoads && _queue.Count > 0)
        {
            var t = _queue[0];
            _queue.RemoveAt(0);
            if (t.Loading || t.NoCover || (t.Bitmap != null && t.BitmapPx >= NeedPx(t) * 0.85)) continue;
            t.Loading = true;
            _loadsRunning++;
            LoadCover(t, dev, NeedPx(t), _gen, wantThumb: t.Thumb == null && t.Bitmap == null);
        }
    }

    /// <summary>先解码一张 20 像素的小图（模糊占位，顺便取主色），再解码清晰版。</summary>
    private async void LoadCover(Tile t, CanvasDevice dev, int px, int gen, bool wantThumb)
    {
        string path = t.CoverPath!;
        CanvasBitmap? bmp = null;
        bool failed = false;
        try
        {
            if (wantThumb)
            {
                var (thumb, tint) = await Task.Run(async () =>
                {
                    var (bytes, side) = await DecodePixels(path, ThumbPx);
                    return (CanvasBitmap.CreateFromBytes(dev, bytes, side, side, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied),
                            AverageColor(bytes));
                });
                lock (_sync)
                {
                    if (gen == _gen && dev == _device && _laidOutWidth > 0 && t.Thumb == null)
                    {
                        t.Thumb = thumb;
                        t.ThumbBrush = new CanvasImageBrush(dev, thumb) { Interpolation = CanvasImageInterpolation.HighQualityCubic };
                        t.ThumbAt = Now;
                        t.Tint = tint;
                        _fadeUntil = Math.Max(_fadeUntil, t.ThumbAt + 180);
                        _glowDirty = true;
                        Kick();
                    }
                    else thumb.Dispose();
                }
            }
            bmp = await Task.Run(async () =>
            {
                var (bytes, side) = await DecodePixels(path, px);
                return CanvasBitmap.CreateFromBytes(dev, bytes, side, side, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
            });
        }
        catch { failed = true; }

        lock (_sync)
        {
            _loadsRunning--;
            t.Loading = false;
            if (gen != _gen || dev != _device || _laidOutWidth <= 0)
            {
                bmp?.Dispose();
            }
            else if (failed || bmp == null)
            {
                bmp?.Dispose();
                t.Failed = true;            // 封面文件坏了：当作没有封面，生成图案
                if (t.Thumb == null) GenerateArt(t, NeedPx(t));
                RequestDraw();
            }
            else
            {
                bool first = t.Bitmap == null;
                DisposeBitmap(t);
                t.Bitmap = bmp;
                t.BitmapPx = (int)bmp.SizeInPixels.Width;
                t.ImageBrush = new CanvasImageBrush(dev, bmp) { Interpolation = CanvasImageInterpolation.Linear };
                if (first)
                {
                    t.ImageAt = Now;
                    _fadeUntil = Math.Max(_fadeUntil, t.ImageAt + 260);
                }
                Kick();
                // 解码期间放大了：还不够清晰的话再排一次
                if (t.BitmapPx < NeedPx(t) * 0.85) _queue.Insert(0, t);
            }
            PumpLoads();
        }
    }

    /// <summary>后台解码：按需要的尺寸缩小、裁成居中的正方形，不把整张大图读进内存。</summary>
    private static async Task<(byte[] bytes, int side)> DecodePixels(string path, int px)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var dec = await BitmapDecoder.CreateAsync(stream);
        uint w = dec.PixelWidth, h = dec.PixelHeight;
        if (w == 0 || h == 0) throw new InvalidDataException();
        double k = Math.Min(1.0, (double)px / Math.Min(w, h));
        uint sw = Math.Max(1u, (uint)Math.Round(w * k)), sh = Math.Max(1u, (uint)Math.Round(h * k));
        uint side = Math.Min(sw, sh);
        var tf = new BitmapTransform
        {
            ScaledWidth = sw, ScaledHeight = sh,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds { X = (sw - side) / 2, Y = (sh - side) / 2, Width = side, Height = side }
        };
        var data = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tf,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (data.DetachPixelData(), (int)side);
    }

    private static Vector3 AverageColor(byte[] bgra)
    {
        double r = 0, g = 0, b = 0;
        int n = bgra.Length / 4;
        for (int i = 0; i + 3 < bgra.Length; i += 4) { b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; }
        if (n == 0) return GlowDefault;
        return new Vector3((float)(r / n / 255), (float)(g / n / 255), (float)(b / n / 255));
    }

    /// <summary>没有封面的专辑：按专辑名生成一张几何图案 + 标题排版的“封面”。</summary>
    private void GenerateArt(Tile t, int px)
    {
        var dev = _device;
        if (dev == null || px < 8) return;
        CanvasRenderTarget rt;
        try
        {
            rt = new CanvasRenderTarget(dev, px, px, 96);
            using var ds = rt.CreateDrawingSession();
            float s = px;
            var c0 = Hsv(t.Hue, 0.38, 0.46);
            var c1 = Hsv((t.Hue + 40) % 360, 0.45, 0.22);
            using (var bg = new CanvasLinearGradientBrush(ds, c0, c1) { StartPoint = Vector2.Zero, EndPoint = new Vector2(s, s) })
                ds.FillRectangle(0, 0, s, s, bg);

            var hi = Hsv((t.Hue + 180) % 360, 0.35, 0.85);
            Color Hi(byte a) => Color.FromArgb(a, hi.R, hi.G, hi.B);
            Color Wh(byte a) => Color.FromArgb(a, 255, 255, 255);
            switch (Hash(t.Key) % 4)
            {
                case 0:     // 两个交叠的大圆
                    ds.FillCircle(s * 0.82f, s * 0.22f, s * 0.52f, Hi(46));
                    ds.FillCircle(s * 0.12f, s * 0.98f, s * 0.42f, Wh(26));
                    break;
                case 1:     // 斜条纹
                    for (float d = -s; d < s * 2; d += s * 0.1f)
                        ds.DrawLine(d, s, d + s, 0, Wh(18), s * 0.035f);
                    ds.FillCircle(s * 0.85f, s * 0.85f, s * 0.28f, Hi(40));
                    break;
                case 2:     // 同心圆环
                    for (int i = 1; i <= 7; i++)
                        ds.DrawCircle(s * 0.88f, s * 0.86f, s * 0.12f * i, Wh((byte)(40 - i * 4)), s * 0.022f);
                    break;
                default:    // 点阵
                    for (float y = s * 0.06f; y < s; y += s * 0.09f)
                        for (float x = s * 0.06f; x < s; x += s * 0.09f)
                            ds.FillCircle(x, y, s * 0.011f, Wh(42));
                    ds.FillRectangle(0, s * 0.62f, s, s * 0.38f, Hi(24));
                    break;
            }

            // 标题排版：左上角大字，下面一行歌手
            float pad = s * 0.09f, tw = s - pad * 2;
            float fs = Math.Clamp(s * 0.115f, 10, 64);
            using var tf = new CanvasTextFormat
            {
                FontSize = fs, FontWeight = FontWeights.SemiBold, FontFamily = "Segoe UI",
                WordWrapping = CanvasWordWrapping.Wrap,
                TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
                TrimmingSign = CanvasTrimmingSign.Ellipsis,
                LineSpacingMode = CanvasLineSpacingMode.Uniform,
                LineSpacing = fs * 1.18f,
                LineSpacingBaseline = fs * 0.95f
            };
            using var title = new CanvasTextLayout(ds, t.Title, tf, tw, fs * 1.18f * 4);
            ds.DrawTextLayout(title, pad, pad, Wh(236));
            float th = (float)Math.Min(title.LayoutBounds.Height, fs * 1.18f * 4);
            using var af = new CanvasTextFormat
            {
                FontSize = Math.Clamp(s * 0.062f, 8, 32), FontFamily = "Segoe UI",
                WordWrapping = CanvasWordWrapping.NoWrap,
                TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
                TrimmingSign = CanvasTrimmingSign.Ellipsis
            };
            using var artist = new CanvasTextLayout(ds, t.Artist, af, tw, s * 0.1f);
            ds.DrawTextLayout(artist, pad, pad + th + s * 0.025f, Wh(165));
        }
        catch { return; }

        bool first = t.Bitmap == null;
        DisposeBitmap(t);
        t.Bitmap = rt;
        t.BitmapPx = px;
        t.ImageBrush = new CanvasImageBrush(dev, rt) { Interpolation = CanvasImageInterpolation.Linear };
        t.Generated = true;
        if (first)
        {
            t.ImageAt = Now;
            _fadeUntil = Math.Max(_fadeUntil, t.ImageAt + 260);
            Kick();
        }
    }

    private static void DisposeBitmap(Tile t)
    {
        t.ImageBrush?.Dispose(); t.ImageBrush = null;
        t.Bitmap?.Dispose(); t.Bitmap = null;
        t.BitmapPx = 0;
        t.Generated = false;
    }

    private static void DisposeText(Tile t)
    {
        t.TitleLayout?.Dispose(); t.TitleLayout = null;
        t.SubLayout?.Dispose(); t.SubLayout = null;
    }

    private static void DisposeTile(Tile t)
    {
        DisposeBitmap(t);
        DisposeText(t);
        t.ThumbBrush?.Dispose(); t.ThumbBrush = null;
        t.Thumb?.Dispose(); t.Thumb = null;
        t.Placeholder?.Dispose(); t.Placeholder = null;
    }

    private void DropAllResources()
    {
        _gen++;
        _queue.Clear();
        foreach (var t in _all) { DisposeTile(t); t.Failed = false; t.Tint = t.BaseTint; }
        _infoBrush?.Dispose(); _infoBrush = null;
        _vignette?.Dispose(); _vignette = null;
        _sheen?.Dispose(); _sheen = null;
        _glowBrushA?.Dispose(); _glowBrushA = null;
        _glowBrushB?.Dispose(); _glowBrushB = null;
        _badgeGlyph?.Dispose(); _badgeGlyph = null;
        _labelLayout?.Dispose(); _labelLayout = null;
        _residentX = double.NaN;
        _glowDirty = true;
    }

    // ---------------- 绘制（渲染线程，持锁） ----------------

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        lock (_sync)
        {
            if (_laidOutWidth <= 0 || _tiles.Count == 0 || !_visible) return;
            var ds = args.DrawingSession;
            var size = sender.Size;
            float vw = (float)size.Width, vh = (float)size.Height;
            double now = Now;

            DrawBackground(ds, sender, vw, vh);

            // 整面墙的变换：缩放（以锚点为中心）+ 拖动景深（以视口中心为中心）
            var center = new Vector2(vw / 2, (float)(HeaderH + vh) / 2);
            var baseM = Matrix3x2.CreateScale(_zoomVis, _zoomAnchor) * Matrix3x2.CreateScale(_depth, center);
            bool transformed = !baseM.IsIdentity;
            // 视口在变换前坐标里的范围，用来剔除
            double ix0 = 0, iy0 = 0, ix1 = vw, iy1 = vh;
            if (transformed && Matrix3x2.Invert(baseM, out var inv))
            {
                var a = Vector2.Transform(Vector2.Zero, inv);
                var b = Vector2.Transform(new Vector2(vw, vh), inv);
                ix0 = Math.Min(a.X, b.X); iy0 = Math.Min(a.Y, b.Y); ix1 = Math.Max(a.X, b.X); iy1 = Math.Max(a.Y, b.Y);
            }
            ds.Transform = baseM;

            double scale = sender.Dpi / 96.0;
            // 没变换时对齐到物理像素，封面不会因为半像素位置发虚
            float ox = (float)(transformed ? _px : Math.Round(_px * scale) / scale);
            float oy = (float)(transformed ? _py : Math.Round(_py * scale) / scale);
            _infoBrush ??= new CanvasLinearGradientBrush(sender, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(200, 0, 0, 0));

            // 方块按行排好，只看可能落在视口里的那几行
            double cs = _cell + _gap;
            int rowMin = (int)Math.Floor((iy0 - oy) / cs) - 3, rowMax = (int)Math.Ceiling((iy1 - oy) / cs) + 1;
            List<Tile>? raised = null;
            for (int i = FirstIndexAtRow(rowMin); i < _tiles.Count; i++)
            {
                var t = _tiles[i];
                if (t.Row > rowMax) break;
                if (t.X + t.W + ox < ix0 - 24 || t.X + ox > ix1 + 24 || t.Y + t.W + oy < iy0 - 24 || t.Y + oy > iy1 + 24) continue;
                if (t.Hover > 0.001f || t.Scale > 1.0005f) { (raised ??= new()).Add(t); continue; }
                DrawTile(ds, sender, t, ox, oy, now, baseM);
            }
            // 浮起的封面最后画，压在邻居上面
            if (raised != null) foreach (var t in raised.OrderBy(x => x.Scale)) DrawTile(ds, sender, t, ox, oy, now, baseM);

            ds.Transform = Matrix3x2.Identity;
            DrawVignette(ds, sender, vw, vh);
            DrawLabel(ds, sender, vw);
            DrawMinimap(ds, vw, vh);
        }
    }

    private void DrawBackground(CanvasDrawingSession ds, ICanvasResourceCreator rc, float vw, float vh)
    {
        var ca = ToColor(_glowA, 210);
        var cb = ToColor(_glowB, 190);
        if (_glowBrushA == null || ca != _glowColA)
        {
            _glowBrushA?.Dispose();
            _glowBrushA = new CanvasRadialGradientBrush(rc, ca, Color.FromArgb(0, ca.R, ca.G, ca.B));
            _glowColA = ca;
        }
        if (_glowBrushB == null || cb != _glowColB)
        {
            _glowBrushB?.Dispose();
            _glowBrushB = new CanvasRadialGradientBrush(rc, cb, Color.FromArgb(0, cb.R, cb.G, cb.B));
            _glowColB = cb;
        }
        float r = Math.Max(vw, vh) * 0.75f;
        _glowBrushA.Center = new Vector2(vw * 0.18f, vh * 0.15f);
        _glowBrushA.RadiusX = _glowBrushA.RadiusY = r;
        ds.FillRectangle(0, 0, vw, vh, _glowBrushA);
        _glowBrushB.Center = new Vector2(vw * 0.86f, vh * 0.92f);
        _glowBrushB.RadiusX = _glowBrushB.RadiusY = r;
        ds.FillRectangle(0, 0, vw, vh, _glowBrushB);
    }

    private void DrawVignette(CanvasDrawingSession ds, ICanvasResourceCreator rc, float vw, float vh)
    {
        _vignette ??= new CanvasRadialGradientBrush(rc, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(0, 0, 0, 0) },
            new CanvasGradientStop { Position = 0.55f, Color = Color.FromArgb(0, 0, 0, 0) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(120, 0, 0, 0) }
        });
        _vignette.Center = new Vector2(vw / 2, vh / 2);
        _vignette.RadiusX = vw * 0.72f;
        _vignette.RadiusY = vh * 0.72f;
        ds.FillRectangle(0, 0, vw, vh, _vignette);
    }

    private void DrawLabel(CanvasDrawingSession ds, ICanvasResourceCreator rc, float vw)
    {
        if (_labelAlpha < 0.01f || string.IsNullOrEmpty(_label)) return;
        if (_labelLayout == null)
        {
            using var f = new CanvasTextFormat
            {
                FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = "Segoe UI",
                WordWrapping = CanvasWordWrapping.NoWrap,
                TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
                TrimmingSign = CanvasTrimmingSign.Ellipsis
            };
            _labelLayout = new CanvasTextLayout(rc, _label, f, 420, 24);
        }
        var b = _labelLayout.LayoutBounds;
        float tw = (float)Math.Min(420, b.Width), th = (float)b.Height;
        float w = tw + 32, h = th + 14;
        float x = vw / 2 - w / 2, y = (float)HeaderH + 10 + (1 - _labelAlpha) * -6;
        ds.FillRoundedRectangle(new Rect(x, y, w, h), h / 2, h / 2, Fade(170, _labelAlpha));
        ds.DrawRoundedRectangle(new Rect(x, y, w, h), h / 2, h / 2, Fade(40, _labelAlpha, 255, 255, 255), 1);
        ds.DrawTextLayout(_labelLayout, x + 16 - (float)b.X, y + 7 - (float)b.Y, Fade(240, _labelAlpha, 255, 255, 255));
    }

    private void DrawMinimap(CanvasDrawingSession ds, float vw, float vh)
    {
        if (_miniAlpha < 0.01f || MiniRect() is not Rect mr) return;
        float a = _miniAlpha;
        var panel = Inflate(mr, 6);
        ds.FillRoundedRectangle(panel, 8, 8, Fade(160, a));
        ds.DrawRoundedRectangle(panel, 8, 8, Fade(30, a, 255, 255, 255), 1);
        float k = (float)MiniScale(), mx = (float)mr.X, my = (float)mr.Y;
        byte ta = (byte)(230 * a);
        foreach (var t in _tiles)
        {
            float s = Math.Max(1, (float)t.W * k - 0.6f);
            var c = t.Tint * 0.9f;
            if (t.Key == _currentKey) c = new Vector3(_accent.R, _accent.G, _accent.B) / 255f;
            ds.FillRectangle(mx + (float)t.X * k, my + (float)t.Y * k, s, s, ToColor(c, ta));
        }
        // 当前视口
        double x0 = Math.Max(0, -_px), y0 = Math.Max(0, -_py);
        double x1 = Math.Min(_wallW, -_px + _vw), y1 = Math.Min(_wallH, -_py + _vh);
        if (x1 > x0 && y1 > y0)
        {
            var vr = new Rect(mx + x0 * k, my + y0 * k, (x1 - x0) * k, (y1 - y0) * k);
            ds.FillRectangle(vr, Fade(40, a, 255, 255, 255));
            ds.DrawRectangle(vr, Fade(220, a, 255, 255, 255), 1.2f);
        }
    }

    private int FirstIndexAtRow(int row)
    {
        int lo = 0, hi = _tiles.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_tiles[mid].Row < row) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static double EaseOut(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return 1 - Math.Pow(1 - x, 4);
    }

    private static Color Fade(byte a, float k, byte r = 0, byte g = 0, byte b = 0) =>
        Color.FromArgb((byte)Math.Clamp(a * k, 0, 255), r, g, b);

    private static Color ToColor(Vector3 v, byte a) =>
        Color.FromArgb(a, (byte)Math.Clamp(v.X * 255, 0, 255), (byte)Math.Clamp(v.Y * 255, 0, 255), (byte)Math.Clamp(v.Z * 255, 0, 255));

    private static Vector3 ToVec(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    private void DrawTile(CanvasDrawingSession ds, ICanvasResourceCreator rc, Tile t, float ox, float oy, double now, Matrix3x2 baseM)
    {
        float w = (float)t.W;
        if (w < 8) return;
        float alpha = 1, rise = 0, s = t.Scale;
        if (now < t.AppearAt + 600)
        {
            double e = now - t.AppearAt;
            if (e < 0) return;                  // 还没轮到它浮现
            alpha = (float)Math.Clamp(e / 420, 0, 1);
            float p = (float)EaseOut(e / 600);
            s *= 0.94f + 0.06f * p;
            rise = 18 * (1 - p);
        }
        float x = (float)t.X + ox, y = (float)t.Y + oy + rise;
        var rect = new Rect(x, y, w, w);
        bool scaled = Math.Abs(s - 1) > 0.0001f;
        var m = scaled ? Matrix3x2.CreateScale(s, new Vector2(x + w / 2, y + w / 2)) * baseM : baseM;
        ds.Transform = m;

        // 浮起时的投影：几层逐渐扩大的半透明圆角矩形叠出柔和的影子
        if (t.Hover > 0.01f)
        {
            float k = t.Hover * alpha;
            for (int i = 6; i >= 1; i--)
            {
                float g = i * 2.6f;
                ds.FillRoundedRectangle(new Rect(x - g, y - g + 8, w + g * 2, w + g * 2), Radius + g, Radius + g, Fade(15, k));
            }
        }

        // 底色：按专辑名生成的渐变
        t.Placeholder ??= MakePlaceholder(rc, t.Hue);
        t.Placeholder.StartPoint = new Vector2(x, y);
        t.Placeholder.EndPoint = new Vector2(x + w, y + w);
        t.Placeholder.Opacity = alpha;
        ds.FillRoundedRectangle(rect, Radius, Radius, t.Placeholder);

        // 模糊小图：清晰版到之前先垫着
        float imgA = t.ImageBrush != null ? (float)Math.Clamp((now - t.ImageAt) / 260, 0, 1) : 0;
        if (t.ThumbBrush != null && imgA < 1)
        {
            float a = alpha * (float)Math.Clamp((now - t.ThumbAt) / 180, 0, 1);
            t.ThumbBrush.Transform = Matrix3x2.CreateScale(w / (float)t.Thumb!.SizeInPixels.Width) * Matrix3x2.CreateTranslation(x, y);
            t.ThumbBrush.Opacity = a;
            ds.FillRoundedRectangle(rect, Radius, Radius, t.ThumbBrush);
        }
        if (t.ImageBrush != null)
        {
            t.ImageBrush.Transform = Matrix3x2.CreateScale(w / t.BitmapPx) * Matrix3x2.CreateTranslation(x, y);
            t.ImageBrush.Opacity = alpha * imgA;
            ds.FillRoundedRectangle(rect, Radius, Radius, t.ImageBrush);
        }

        // 平时轻微压暗，悬停时亮起
        float dim = (1 - t.Hover) * alpha;
        if (dim > 0.01f) ds.FillRoundedRectangle(rect, Radius, Radius, Fade(56, dim));

        // 悬停光泽：一团跟着鼠标走的柔光
        if (t == _hovered && _mouseIn && t.Hover > 0.01f && Matrix3x2.Invert(m, out var inv))
        {
            var local = Vector2.Transform(new Vector2((float)_mouse.X, (float)_mouse.Y), inv);
            _sheen ??= new CanvasRadialGradientBrush(rc, Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
            _sheen.Center = local;
            _sheen.RadiusX = _sheen.RadiusY = w * 0.85f;
            _sheen.Opacity = t.Hover * alpha;
            ds.FillRoundedRectangle(rect, Radius, Radius, _sheen);
        }

        // 专辑名 / 歌手：悬停时淡入（生成的图案上已经印着标题，不再叠）
        float info = (t.Generated || t.NoCover ? 0 : t.Hover) * alpha;
        if (info > 0.01f) DrawInfo(ds, rc, t, x, y, w, info);

        if (t.Key == _currentKey) DrawNowPlaying(ds, rc, x, y, w, alpha, now);

        ds.Transform = baseM;
    }

    /// <summary>正在播放：强调色边框随音乐呼吸，角标是 4 根跳动的频谱条（暂停时换成喇叭）。</summary>
    private void DrawNowPlaying(CanvasDrawingSession ds, ICanvasResourceCreator rc, float x, float y, float w, float alpha, double now)
    {
        bool playing = now - _lastSpectrum < 350;
        float level = playing ? (_bars[0] + _bars[1] + _bars[2] + _bars[3]) / 4 : 0;
        float pulse = playing ? 0.5f + 0.5f * (float)Math.Sin(now / 2400 * Math.PI * 2) : 0;
        byte ar = _accent.R, ag = _accent.G, ab = _accent.B;
        if (playing)
        {
            // 外圈柔光
            ds.DrawRoundedRectangle(new Rect(x - 2, y - 2, w + 4, w + 4), Radius + 2, Radius + 2,
                Fade((byte)(50 + 70 * pulse), alpha, ar, ag, ab), 3 + 3 * level);
        }
        float sw = 2.5f + 1.2f * level;
        ds.DrawRoundedRectangle(new Rect(x + sw / 2, y + sw / 2, w - sw, w - sw), Radius - 1, Radius - 1,
            Fade(_accent.A, alpha, ar, ag, ab), sw);

        var pill = new Rect(x + 7, y + 7, 30, 22);
        ds.FillRoundedRectangle(pill, 11, 11, Fade(200, alpha));
        if (playing)
        {
            float bw = 3, gap = 2.5f, total = bw * 4 + gap * 3;
            float x0 = (float)pill.X + (30 - total) / 2, baseY = (float)pill.Y + 17;
            for (int i = 0; i < 4; i++)
            {
                float h = 3 + 9 * Math.Clamp(_bars[i], 0, 1);
                ds.FillRoundedRectangle(new Rect(x0 + i * (bw + gap), baseY - h, bw, h), 1.5f, 1.5f, Fade(240, alpha, 255, 255, 255));
            }
        }
        else
        {
            if (_badgeGlyph == null)
            {
                using var f = new CanvasTextFormat
                {
                    FontSize = 12, FontFamily = IconFont,
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center
                };
                _badgeGlyph = new CanvasTextLayout(rc, "", f, 30, 22);
            }
            ds.DrawTextLayout(_badgeGlyph, (float)pill.X, (float)pill.Y, Fade(240, alpha, 255, 255, 255));
        }
    }

    private void DrawInfo(CanvasDrawingSession ds, ICanvasResourceCreator rc, Tile t, float x, float y, float w, float info)
    {
        const float padX = 10, padB = 9;
        float textW = Math.Max(10, w - padX * 2);
        float titleSize = (float)Math.Clamp(w * 0.085, 12, 22);
        int maxLines = t.Size >= 2 ? 3 : 2;
        if (t.TitleLayout == null)
        {
            using var f = new CanvasTextFormat
            {
                FontSize = titleSize, FontWeight = FontWeights.SemiBold, FontFamily = "Segoe UI",
                WordWrapping = CanvasWordWrapping.Wrap,
                TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
                TrimmingSign = CanvasTrimmingSign.Ellipsis,
                LineSpacingMode = CanvasLineSpacingMode.Uniform,
                LineSpacing = titleSize * 1.3f,
                LineSpacingBaseline = titleSize * 1.02f
            };
            t.TitleLayout = new CanvasTextLayout(rc, t.Title, f, textW, titleSize * 1.3f * maxLines);
        }
        if (t.SubLayout == null)
        {
            using var f = new CanvasTextFormat
            {
                FontSize = 11, FontFamily = "Segoe UI",
                WordWrapping = CanvasWordWrapping.NoWrap,
                TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
                TrimmingSign = CanvasTrimmingSign.Ellipsis
            };
            t.SubLayout = new CanvasTextLayout(rc, t.Sub, f, textW, 16);
        }
        float titleH = (float)Math.Min(t.TitleLayout.LayoutBounds.Height, titleSize * 1.3f * maxLines);
        float subH = (float)t.SubLayout.LayoutBounds.Height;
        float top = y + w - padB - subH - 1 - titleH;
        float gradTop = Math.Max(y, top - w * 0.25f);

        _infoBrush!.StartPoint = new Vector2(x, gradTop);
        _infoBrush.EndPoint = new Vector2(x, y + w);
        _infoBrush.Opacity = info;
        ds.FillRoundedRectangle(new Rect(x, gradTop, w, y + w - gradTop), Radius, Radius, _infoBrush);
        ds.DrawTextLayout(t.TitleLayout, x + padX, top, Fade(240, info, 255, 255, 255));
        ds.DrawTextLayout(t.SubLayout, x + padX, top + titleH + 1, Fade(150, info, 255, 255, 255));
    }

    private static string Initial(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return "♪";
        int n = char.IsHighSurrogate(s[0]) && s.Length > 1 ? 2 : 1;
        return s[..n].ToUpperInvariant();
    }

    private static Color Hsv(double hh, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(hh / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = hh switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static CanvasLinearGradientBrush MakePlaceholder(ICanvasResourceCreator rc, double hue) =>
        new(rc, Hsv(hue, 0.30, 0.42), Hsv((hue + 35) % 360, 0.40, 0.24));

    // ---------------- 交互（界面线程） ----------------

    /// <summary>悬停时放大的倍数：无论大小块都只往外长约 6 像素，大块不会盖住一大片邻居。</summary>
    private static float HoverScale(Tile t) => 1f + (float)(12 / Math.Max(60, t.W));

    /// <summary>持锁调用。</summary>
    private void SetHovered(Tile? t)
    {
        if (t == _hovered) return;
        if (_hovered != null) { _hovered.HoverTo = 0; _anim.Add(_hovered); }
        _hovered = t;
        if (t != null) { t.HoverTo = 1; _anim.Add(t); }
        if (!_dragging)
            ProtectedCursor = t != null ? Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand) : null;
        Kick();
    }

    /// <summary>持锁调用；真正通知外面放到锁外（播放器开文件可能要一会儿，别让渲染线程干等）。</summary>
    private void Pick(Tile t, bool shuffle)
    {
        var tracks = t.Tracks.Where(x => !x.IsMissing).ToList();
        if (tracks.Count == 0) return;
        if (shuffle) tracks = tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        _dq.TryEnqueue(() => AlbumPicked?.Invoke(this, tracks));
    }

    /// <summary>把某一块平移到视口中间，并让它亮一下。</summary>
    private void Reveal(Tile t, Action? then)
    {
        bool near;
        lock (_sync)
        {
            double tx = Clamp(_vw / 2 - (t.X + t.W / 2), BoundsX());
            double ty = Clamp((HeaderH + _vh) / 2 - (t.Y + t.W / 2), BoundsY());
            near = Math.Abs(tx - _px) < 4 && Math.Abs(ty - _py) < 4;
            _vx = _vy = 0;
            _tx = tx; _ty = ty;
            StartPan();
        }
        var timer = _dq.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(near ? 60 : 650);
        timer.IsRepeating = false;
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            lock (_sync) { AfterPan(force: true); SetHovered(t); }
            var back = _dq.CreateTimer();
            back.Interval = TimeSpan.FromMilliseconds(700);
            back.IsRepeating = false;
            back.Tick += (s2, e2) =>
            {
                back.Stop();
                lock (_sync) if (_hovered == t) SetHovered(null);
                then?.Invoke();
            };
            back.Start();
        };
        timer.Start();
    }

    private void PickRandom()
    {
        Tile t;
        lock (_sync)
        {
            if (_tiles.Count == 0) return;
            t = _tiles[Random.Shared.Next(_tiles.Count)];
        }
        Reveal(t, () => { lock (_sync) Pick(t, shuffle: false); });
    }

    private void LocateCurrent()
    {
        Tile? t;
        lock (_sync) t = _currentKey == null ? null : _tiles.FirstOrDefault(x => x.Key == _currentKey);
        if (t != null) Reveal(t, null);
    }

    /// <summary>打开时的整体淡入。</summary>
    public void PlayOpenAnimation()
    {
        var v = ElementCompositionPreview.GetElementVisual(_root);
        var a = _compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, 0f); a.InsertKeyFrame(1f, 1f);
        a.Duration = TimeSpan.FromMilliseconds(220);
        v.StartAnimation("Opacity", a);
    }
}
