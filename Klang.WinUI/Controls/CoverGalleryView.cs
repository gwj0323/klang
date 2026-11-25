using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Controls;

/// <summary>
/// 封面画廊：曲库里每张专辑一块正方形封面，大小三档（1×1 / 2×2 / 3×3），严丝合缝铺成一面大墙，
/// 墙的长宽比接近窗口，按住鼠标可以往任意方向拖动（松手有惯性，拖过边缘会回弹），滚轮也能滚。
/// 只为视口附近的方块创建界面元素，几千张专辑也不卡；鼠标移上去封面浮起并显示专辑名。
/// 点一下播放这张专辑；正在播放的专辑带强调色边框。
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
        public string? CoverPath { get; init; }
        public long Added { get; init; }
        public int Pref = 1;          // 想要的边长（格数）
        public int Size = 1;          // 实际边长
        public int Col, Row;
        public double X, Y, W;
        // 只有在视口附近时才有界面元素
        public Grid? Host;
        public Border? Dim;
        public FrameworkElement? Info;
        public Border? Ring;
    }

    private const double Gap = 3;
    private const double TargetCell = 160;
    private const double Pad = 14;
    private const double HeaderH = 64;
    private const double Buffer = 500;

    private readonly Grid _root = new();
    private readonly Grid _surface = new();                 // 接收拖动 / 滚轮的整块区域
    private readonly Canvas _canvas = new();                // 整面墙，靠 _pan 平移
    private readonly TranslateTransform _pan = new();
    private readonly RectangleGeometry _clip = new();
    private readonly TextBlock _count = new();
    private readonly TextBlock _sortText = new();
    private readonly TextBlock _empty = new();
    private readonly Compositor _compositor;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dq;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _relayout;
    private readonly MenuFlyout _tileMenu = new();
    private readonly List<RadioMenuFlyoutItem> _sortItems = new();

    private List<Tile> _all = new();
    private List<Tile> _tiles = new();          // 按当前排序
    private readonly Dictionary<string, Tile> _byTrackId = new();
    private SortMode _sort = SortMode.Recent;
    private int _shuffleSeed;
    private int _cols;
    private double _cell = TargetCell;
    private double _laidOutWidth;                           // > 0 表示已经排过版
    private double _wallW, _wallH;
    private bool _needsLayout;

    // 平移状态：墙左上角在视口里的位置
    private double _px, _py;
    private double _vx, _vy;                                // 惯性速度（像素 / 毫秒）
    private double? _tx, _ty;                               // 平滑移动的目标位置（滚轮、定位）
    private bool _animating;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private double _lastFrame;
    private double _realizedX = double.NaN, _realizedY = double.NaN;

    // 拖动
    private uint? _pressId;
    private Windows.Foundation.Point _pressPt;
    private double _pressPx, _pressPy;
    private bool _dragging;
    private bool _justDragged;                              // 刚拖完松手：这次不算点击
    private readonly List<(double t, double x, double y)> _samples = new();
    private Tile? _pressedTile;
    private Tile? _hovered;
    private bool _animateNext;
    private string? _currentKey;
    private Tile? _menuTile;
    private Brush _accent = new SolidColorBrush(Color.FromArgb(255, 150, 140, 255));

    private readonly Button _locateBtn = new();

    private static readonly Brush White = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255));
    private static readonly Brush Faint = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    private static readonly Color Bg = Color.FromArgb(255, 13, 13, 17);

    public CoverGalleryView()
    {
        RequestedTheme = ElementTheme.Dark;           // 画廊本身是深色的“展厅”
        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _root.Background = new SolidColorBrush(Bg);

        _canvas.HorizontalAlignment = HorizontalAlignment.Left;
        _canvas.VerticalAlignment = VerticalAlignment.Top;
        _canvas.RenderTransform = _pan;
        _surface.Background = new SolidColorBrush(Colors.Transparent);
        _surface.Children.Add(_canvas);
        _surface.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPressed), true);
        _surface.AddHandler(PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnMoved), true);
        _surface.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnReleased), true);
        _surface.PointerCanceled += OnReleased;
        _surface.PointerCaptureLost += OnReleased;
        _surface.PointerWheelChanged += OnWheel;
        _root.Clip = _clip;     // 平移出去的方块不能画到播放栏上

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
        _relayout.Tick += (s, e) => { ClampNow(); ApplyPan(force: true); };
        SizeChanged += (s, e) =>
        {
            _clip.Rect = new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight);
            if (ActualWidth < 200) return;
            if (_needsLayout) { Relayout(keepAnchor: false); return; }
            // 窗口大小变了：墙不重排（免得封面乱跳），只把位置收回边界内
            _relayout.Stop(); _relayout.Start();
        };
        // 打开画廊时先 Load 再设为可见：等真正可见、量出宽度后再排版
        RegisterPropertyChangedCallback(VisibilityProperty, (s, dp) =>
        {
            if (Visibility != Visibility.Visible || !_needsLayout) return;
            _dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_needsLayout && Visibility == Visibility.Visible && ActualWidth >= 200) Relayout(keepAnchor: false);
            });
        });
        ActualThemeChanged += (s, e) => _accent = ThemeService.AccentFill(this);
        Loaded += (s, e) => _accent = ThemeService.AccentFill(this);
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
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(250, Bg.R, Bg.G, Bg.B), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(215, Bg.R, Bg.G, Bg.B), Offset = 0.6 },
                    new GradientStop { Color = Color.FromArgb(0, Bg.R, Bg.G, Bg.B), Offset = 1 }
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
        play.Click += (s, e) => { if (_menuTile != null) Pick(_menuTile, shuffle: false); };
        var shuffle = new MenuFlyoutItem { Text = L.T("随机播放"), Icon = new FontIcon { Glyph = "" } };
        shuffle.Click += (s, e) => { if (_menuTile != null) Pick(_menuTile, shuffle: true); };
        _tileMenu.Items.Add(play);
        _tileMenu.Items.Add(shuffle);
    }

    // ---------------- 正在播放 ----------------

    /// <summary>当前播放曲目变了：墙上对应的专辑亮起强调色边框，顶栏的“定位”按钮可用。</summary>
    public void SetCurrent(TrackItem? t)
    {
        SetCurrentKey(t != null && _byTrackId.TryGetValue(t.Id, out var tile) ? tile.Key : null);
        _locateBtn.Visibility = _currentKey != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetCurrentKey(string? key)
    {
        if (key == _currentKey) return;
        var old = _currentKey;
        _currentKey = key;
        foreach (var t in _tiles)
            if (t.Host != null && (t.Key == old || t.Key == key)) ApplyRing(t);
    }

    private void ApplyRing(Tile t)
    {
        if (t.Ring == null) return;
        bool on = t.Key == _currentKey;
        t.Ring.BorderBrush = _accent;
        t.Ring.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
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
        Unrealize();
        _laidOutWidth = 0;          // 新方块还没排版，排好之前不许创建（滚动事件可能先到）
        _byTrackId.Clear();
        var groups = new Dictionary<string, List<TrackItem>>();
        foreach (var t in library)
        {
            if (t.IsOnline || t.IsMissing) continue;
            string key = AlbumKey(t);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<TrackItem>();
            list.Add(t);
        }

        _all = new List<Tile>(groups.Count);
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
            var tile = new Tile
            {
                Key = key,
                Tracks = tracks,
                Title = single ? tracks[0].Title : tracks[0].Album.Trim(),
                Artist = artists.Count == 1 ? artists[0] : L.T("群星"),
                CoverPath = cover,
                Added = tracks.Max(x => x.AddedTimestamp)
            };
            // 大小按专辑算哈希，稳定不乱跳；没封面的只占一格，曲目多的专辑更容易成为大块
            int roll = Hash(key) % 100;
            if (cover != null)
            {
                bool rich = tracks.Count >= 6;
                tile.Pref = rich && roll < 7 ? 3 : roll < (rich ? 34 : 18) ? 2 : 1;
            }
            _all.Add(tile);
            foreach (var x in tracks) _byTrackId[x.Id] = tile;
        }

        _count.Text = L.F("{0} 张专辑", _all.Count);
        _empty.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplySort();
        StopMotion();
        _animateNext = true;
        _needsLayout = true;
        if (ActualWidth >= 200 && Visibility == Visibility.Visible) Relayout(keepAnchor: false);
    }

    private void SetSort(SortMode mode)
    {
        if (mode == SortMode.Shuffle) _shuffleSeed = Random.Shared.Next();
        else if (mode == _sort) return;
        _sort = mode;
        foreach (var i in _sortItems) i.IsChecked = (SortMode)i.Tag == mode;
        _sortText.Text = _sortItems.First(i => (SortMode)i.Tag == mode).Text;
        Unrealize();
        ApplySort();
        StopMotion();
        _animateNext = true;
        Relayout(keepAnchor: false);
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

    /// <summary>画廊关掉后拆掉所有方块和封面（下次打开时重新按需创建）。</summary>
    public void ReleaseImages()
    {
        StopMotion();
        Unrealize();
        _laidOutWidth = 0;
        _needsLayout = true;
    }

    private static int Hash(string s)
    {
        int h = 17;
        foreach (char ch in s) h = unchecked(h * 31 + ch);
        return h & 0x7fffffff;
    }

    // ---------------- 布局：正方形马赛克 ----------------

    private void Relayout(bool keepAnchor)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 200 || h < 150) return;
        _needsLayout = false;

        // 记住视口中间那一块，重排后把它放回中间
        Tile? anchor = null;
        if (keepAnchor && _laidOutWidth > 0 && _tiles.Count > 0)
        {
            double cx = w / 2 - _px, cy = (HeaderH + h) / 2 - _py;
            anchor = _tiles.OrderBy(t => Math.Abs(t.X + t.W / 2 - cx) + Math.Abs(t.Y + t.W / 2 - cy)).First();
        }

        // 列数让整面墙的长宽比接近视口（至少铺满一屏宽）
        _cell = TargetCell;
        double area = _tiles.Sum(t => Math.Pow(Math.Min(t.Pref, 3), 2));
        double aspect = (w - Pad * 2) / Math.Max(100, h - HeaderH - Pad);
        int screenCols = Math.Max(3, (int)Math.Ceiling((w - Pad * 2 + Gap) / (_cell + Gap)));
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

        Unrealize();
        foreach (var t in _tiles)
        {
            t.X = t.Col * (_cell + Gap);
            t.Y = t.Row * (_cell + Gap);
            t.W = t.Size * _cell + (t.Size - 1) * Gap;
        }
        _wallW = Math.Max(0, _cols * (_cell + Gap) - Gap);
        _wallH = Math.Max(0, rows * (_cell + Gap) - Gap);
        _canvas.Width = _wallW;
        _canvas.Height = _wallH;
        _laidOutWidth = w;

        if (anchor != null) { _px = w / 2 - (anchor.X + anchor.W / 2); _py = (HeaderH + h) / 2 - (anchor.Y + anchor.W / 2); }
        else { (_px, _py) = (Pad, HeaderH); }    // 从左上角开始（最先排的专辑在那里）
        ClampNow();
        ApplyPan(force: true, animate: _animateNext);
        _animateNext = false;
    }

    // ---------------- 平移：拖动 / 惯性 / 回弹 / 滚轮 ----------------

    private (double min, double max) BoundsX()
    {
        double w = ActualWidth;
        if (_wallW + Pad * 2 <= w) { double c = (w - _wallW) / 2; return (c, c); }
        return (w - Pad - _wallW, Pad);
    }

    private (double min, double max) BoundsY()
    {
        double h = ActualHeight;
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

    private void ApplyPan(bool force = false, bool animate = false)
    {
        _pan.X = Math.Round(_px);
        _pan.Y = Math.Round(_py);
        // 移动超过一段距离才重新检查哪些方块该创建 / 拆掉
        if (force || double.IsNaN(_realizedX) || Math.Abs(_px - _realizedX) > 60 || Math.Abs(_py - _realizedY) > 60)
        {
            _realizedX = _px; _realizedY = _py;
            Realize(animate);
        }
    }

    private void StopMotion()
    {
        _vx = _vy = 0;
        _tx = _ty = null;
        StopFrames();
    }

    private void StartFrames()
    {
        if (_animating) return;
        _animating = true;
        _clock.Restart();
        _lastFrame = 0;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopFrames()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double dt = Math.Clamp(now - _lastFrame, 1, 50);
        _lastFrame = now;
        var bx = BoundsX(); var by = BoundsY();
        bool busy = false;

        if (_tx != null || _ty != null)
        {
            // 平滑移到目标（滚轮 / 定位）
            double k = 1 - Math.Exp(-dt / 90);
            if (_tx is double tx) { _px += (tx - _px) * k; if (Math.Abs(tx - _px) < 0.5) { _px = tx; _tx = null; } else busy = true; }
            if (_ty is double ty) { _py += (ty - _py) * k; if (Math.Abs(ty - _py) < 0.5) { _py = ty; _ty = null; } else busy = true; }
        }
        else
        {
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
            busy = _vx != 0 || _vy != 0 || outX || outY;
            if (!busy) ClampNow();
        }

        ApplyPan();
        if (!busy) { StopFrames(); ApplyPan(force: true); }
    }

    private void OnPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !pt.Properties.IsLeftButtonPressed) return;
        StopMotion();
        ClampNow();
        _pressId = e.Pointer.PointerId;
        _pressPt = pt.Position;
        _pressPx = _px; _pressPy = _py;
        _dragging = false;
        _justDragged = false;
        _samples.Clear();
    }

    private void OnMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_pressId != e.Pointer.PointerId) return;
        var p = e.GetCurrentPoint(this).Position;
        double dx = p.X - _pressPt.X, dy = p.Y - _pressPt.Y;
        if (!_dragging)
        {
            if (dx * dx + dy * dy < 36) return;     // 挪动不到 6 像素算点击
            _dragging = true;
            _surface.CapturePointer(e.Pointer);
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
            if (_pressedTile != null) { Hover(_pressedTile, false); _pressedTile = null; }
            if (_hovered != null) Hover(_hovered, false);
        }
        _px = Rubber(_pressPx + dx, BoundsX());
        _py = Rubber(_pressPy + dy, BoundsY());
        double now = Environment.TickCount64;
        _samples.Add((now, p.X, p.Y));
        while (_samples.Count > 2 && now - _samples[0].t > 90) _samples.RemoveAt(0);
        ApplyPan();
        e.Handled = true;
    }

    private void OnReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_pressId != e.Pointer.PointerId) return;
        _pressId = null;
        if (!_dragging) return;
        _dragging = false;
        _justDragged = true;
        ProtectedCursor = null;
        _surface.ReleasePointerCapture(e.Pointer);
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
        StartFrames();
    }

    private void OnWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        double d = props.MouseWheelDelta;
        bool horizontal = props.IsHorizontalMouseWheel
            || (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
        _vx = _vy = 0;
        if (horizontal)
            _tx = Clamp((_tx ?? _px) + (props.IsHorizontalMouseWheel ? -d : d), BoundsX());
        else
            _ty = Clamp((_ty ?? _py) + d, BoundsY());
        StartFrames();
        e.Handled = true;
    }

    private List<int[]> _occ = new();

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

    // ---------------- 按需创建方块 ----------------

    private void Realize(bool animate)
    {
        if (_tiles.Count == 0 || _laidOutWidth <= 0 || Visibility != Visibility.Visible) return;
        double vw = ActualWidth, vh = ActualHeight;
        // 视口在墙上的位置（墙坐标）
        double vx0 = -_px, vy0 = -_py, vx1 = vx0 + vw, vy1 = vy0 + vh;
        double cx = (vx0 + vx1) / 2, cy = (vy0 + vy1) / 2;
        double diag = Math.Max(1, Math.Sqrt(vw * vw + vh * vh) / 2);

        foreach (var t in _tiles)
        {
            bool want = t.X + t.W > vx0 - Buffer && t.X < vx1 + Buffer && t.Y + t.W > vy0 - Buffer && t.Y < vy1 + Buffer;
            if (want && t.Host == null)
            {
                CreateTile(t);
                if (animate && t.X + t.W > vx0 && t.X < vx1 && t.Y + t.W > vy0 && t.Y < vy1)
                {
                    double dx = t.X + t.W / 2 - cx, dy = t.Y + t.W / 2 - cy;
                    Entrance(t, Math.Sqrt(dx * dx + dy * dy) / diag);   // 从视口中心往外一圈圈浮现
                }
            }
            else if (!want && t.Host != null) RemoveTile(t);
        }
    }

    private void Unrealize()
    {
        foreach (var t in _all) { t.Host = null; t.Dim = null; t.Info = null; t.Ring = null; }
        foreach (var child in _canvas.Children)
            if (child is Grid g && g.Children.Count > 0 && g.Children[0] is Image img) img.Source = null;
        _canvas.Children.Clear();
        _hovered = null; _pressedTile = null;
    }

    private void RemoveTile(Tile t)
    {
        if (t.Host == null) return;
        if (t.Host.Children.Count > 0 && t.Host.Children[0] is Image img) img.Source = null;
        _canvas.Children.Remove(t.Host);
        t.Host = null; t.Dim = null; t.Info = null; t.Ring = null;
    }

    private void CreateTile(Tile t)
    {
        double side = t.W;
        if (side < 8) return;
        var host = new Grid
        {
            Width = side, Height = side,
            CornerRadius = new CornerRadius(4),
            Background = PlaceholderBrush(t.Title)
        };

        if (t.CoverPath != null)
        {
            var bmp = new BitmapImage { DecodePixelWidth = (int)Math.Min(720, side * 1.25), DecodePixelType = DecodePixelType.Logical };
            var img = new Image
            {
                Stretch = Stretch.UniformToFill, Source = bmp,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var iv = ElementCompositionPreview.GetElementVisual(img);
            iv.Opacity = 0;
            img.ImageOpened += (s, e) =>
            {
                var a = _compositor.CreateScalarKeyFrameAnimation();
                a.InsertKeyFrame(1f, 1f);
                a.Duration = TimeSpan.FromMilliseconds(260);
                iv.StartAnimation("Opacity", a);
            };
            // 封面文件坏了（解码失败）：当作没有封面，显示首字母和专辑名
            img.ImageFailed += (s, e) =>
            {
                if (t.Host != host) return;
                host.Children.Insert(1, InitialText(t.Title, side));
                if (t.Info != null)
                {
                    ElementCompositionPreview.GetElementVisual(t.Info).Opacity = 1;
                    t.Info = null;
                }
            };
            try { bmp.UriSource = new Uri(t.CoverPath); } catch { }
            host.Children.Add(img);
        }
        else host.Children.Add(InitialText(t.Title, side));

        // 平时轻微压暗，悬停时亮起
        var dim = new Border { Background = new SolidColorBrush(Color.FromArgb(56, 0, 0, 0)), IsHitTestVisible = false };
        host.Children.Add(dim);

        // 专辑名 / 歌手：悬停时淡入（没有封面的方块一直显示，否则认不出是哪张）
        double titleSize = Math.Clamp(side * 0.085, 12, 22);
        var info = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(10, side * 0.25, 10, 9),
            Spacing = 1,
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(200, 0, 0, 0), Offset = 1 }
                }
            },
            Children =
            {
                new TextBlock
                {
                    Text = t.Title, FontSize = titleSize, FontWeight = FontWeights.SemiBold, Foreground = White,
                    TextWrapping = TextWrapping.Wrap, MaxLines = t.Size >= 2 ? 3 : 2,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = t.Tracks.Count > 1 ? $"{t.Artist} · {L.F("{0} 首", t.Tracks.Count)}" : t.Artist,
                    FontSize = 11, Foreground = Faint,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };
        bool alwaysInfo = t.CoverPath == null;
        if (!alwaysInfo) ElementCompositionPreview.GetElementVisual(info).Opacity = 0;
        host.Children.Add(info);

        // 正在播放：强调色边框 + 左上角小图标
        var ring = new Border
        {
            BorderThickness = new Thickness(2.5),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
                Margin = new Thickness(7),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                Child = new FontIcon { Glyph = "", FontSize = 12, Foreground = White }
            }
        };
        host.Children.Add(ring);

        ElementCompositionPreview.SetIsTranslationEnabled(host, true);
        host.PointerEntered += (s, e) => Hover(t, true);
        host.PointerExited += (s, e) => Hover(t, false);
        host.PointerCanceled += (s, e) => Hover(t, false);
        host.PointerPressed += (s, e) => { _pressedTile = t; Press(t, true); };
        host.PointerReleased += (s, e) => { _pressedTile = null; Press(t, false); };
        host.Tapped += (s, e) => { if (!_dragging && !_justDragged) Pick(t, shuffle: false); };
        host.RightTapped += (s, e) =>
        {
            _menuTile = t;
            _tileMenu.ShowAt(host, e.GetPosition(host));
            e.Handled = true;
        };

        t.Host = host; t.Dim = dim; t.Info = alwaysInfo ? null : info; t.Ring = ring;
        ApplyRing(t);
        Canvas.SetLeft(host, t.X);
        Canvas.SetTop(host, t.Y);
        _canvas.Children.Add(host);
    }

    private static TextBlock InitialText(string title, double side) => new()
    {
        Text = Initial(title),
        FontSize = side * 0.36,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 0, side * 0.12),
        IsHitTestVisible = false
    };

    private static string Initial(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return "♪";
        int n = char.IsHighSurrogate(s[0]) && s.Length > 1 ? 2 : 1;
        return s[..n].ToUpperInvariant();
    }

    private static Brush PlaceholderBrush(string seed)
    {
        double hue = Hash(seed) % 360;
        static Color Hsv(double hh, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs(hh / 60 % 2 - 1)), m = v - c;
            (double r, double g, double b) = hh switch
            {
                < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
                < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
            };
            return Color.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }
        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Hsv(hue, 0.30, 0.42), Offset = 0 },
                new GradientStop { Color = Hsv((hue + 35) % 360, 0.40, 0.24), Offset = 1 }
            }
        };
    }

    /// <summary>进场：从左上往右下一波波浮现。</summary>
    private void Entrance(Tile t, double pos)
    {
        if (t.Host == null) return;
        var delay = TimeSpan.FromSeconds(Math.Clamp(pos, 0, 1) * 0.45);
        var v = ElementCompositionPreview.GetElementVisual(t.Host);
        v.CenterPoint = new Vector3((float)t.W / 2, (float)t.W / 2, 0);
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

        var op = _compositor.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(0f, 0f); op.InsertKeyFrame(1f, 1f);
        op.Duration = TimeSpan.FromMilliseconds(420);
        op.DelayTime = delay;
        op.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Opacity", op);

        var sc = _compositor.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0f, new Vector3(0.94f, 0.94f, 1f));
        sc.InsertKeyFrame(1f, Vector3.One, ease);
        sc.Duration = TimeSpan.FromMilliseconds(600);
        sc.DelayTime = delay;
        sc.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Scale", sc);

        var rise = _compositor.CreateScalarKeyFrameAnimation();
        rise.InsertKeyFrame(0f, 18f); rise.InsertKeyFrame(1f, 0f, ease);
        rise.Duration = TimeSpan.FromMilliseconds(600);
        rise.DelayTime = delay;
        rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Translation.Y", rise);
    }

    // ---------------- 交互 ----------------

    /// <summary>悬停时放大的倍数：无论大小块都只往外长约 8 像素，大块不会盖住一大片邻居。</summary>
    private static float HoverScale(Tile t) => 1f + (float)(12 / Math.Max(60, t.W));

    private void Hover(Tile t, bool on)
    {
        if (t.Host == null) return;
        if (on && _dragging) return;          // 拖动经过的封面不要一个个弹起来
        if (on) { if (_hovered != null && _hovered != t) Hover(_hovered, false); _hovered = t; }
        else if (_hovered == t) _hovered = null;
        var v = ElementCompositionPreview.GetElementVisual(t.Host);
        v.CenterPoint = new Vector3((float)t.W / 2, (float)t.W / 2, 0);
        var spring = _compositor.CreateSpringVector3Animation();
        float s = on ? HoverScale(t) : 1f;
        spring.FinalValue = new Vector3(s, s, 1f);
        spring.DampingRatio = 0.7f;
        spring.Period = TimeSpan.FromMilliseconds(50);
        v.StartAnimation("Scale", spring);
        Canvas.SetZIndex(t.Host, on ? 10 : 0);

        Fade(t.Dim, on ? 0f : 1f, on ? 160 : 240);
        Fade(t.Info, on ? 1f : 0f, on ? 180 : 220);
    }

    private void Fade(UIElement? e, float to, int ms)
    {
        if (e == null) return;
        var v = ElementCompositionPreview.GetElementVisual(e);
        var a = _compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, to);
        a.Duration = TimeSpan.FromMilliseconds(ms);
        v.StartAnimation("Opacity", a);
    }

    private void Press(Tile t, bool down)
    {
        if (t.Host == null || _dragging) return;
        var v = ElementCompositionPreview.GetElementVisual(t.Host);
        var spring = _compositor.CreateSpringVector3Animation();
        float s = down ? 0.98f : HoverScale(t);
        spring.FinalValue = new Vector3(s, s, 1f);
        spring.DampingRatio = 0.6f;
        spring.Period = TimeSpan.FromMilliseconds(40);
        v.StartAnimation("Scale", spring);
    }

    private void Pick(Tile t, bool shuffle)
    {
        var tracks = t.Tracks.Where(x => !x.IsMissing).ToList();
        if (tracks.Count == 0) return;
        if (shuffle) tracks = tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        AlbumPicked?.Invoke(this, tracks);
    }

    /// <summary>把某一块平移到视口中间，并让它亮一下。</summary>
    private void Reveal(Tile t, Action? then)
    {
        double tx = Clamp(ActualWidth / 2 - (t.X + t.W / 2), BoundsX());
        double ty = Clamp((HeaderH + ActualHeight) / 2 - (t.Y + t.W / 2), BoundsY());
        bool near = Math.Abs(tx - _px) < 4 && Math.Abs(ty - _py) < 4;
        _vx = _vy = 0;
        _tx = tx; _ty = ty;
        StartFrames();
        var timer = _dq.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(near ? 60 : 650);
        timer.IsRepeating = false;
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            ApplyPan(force: true);
            Hover(t, true);
            var back = _dq.CreateTimer();
            back.Interval = TimeSpan.FromMilliseconds(700);
            back.IsRepeating = false;
            back.Tick += (s2, e2) => { back.Stop(); Hover(t, false); then?.Invoke(); };
            back.Start();
        };
        timer.Start();
    }

    private void PickRandom()
    {
        if (_tiles.Count == 0) return;
        var t = _tiles[Random.Shared.Next(_tiles.Count)];
        Reveal(t, () => Pick(t, shuffle: false));
    }

    private void LocateCurrent()
    {
        var t = _currentKey == null ? null : _tiles.FirstOrDefault(x => x.Key == _currentKey);
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
