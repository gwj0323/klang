using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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
/// 封面画廊：把曲库里每张专辑的封面拼成一面紧凑的马赛克墙（大小不一的方块严丝合缝地铺满），
/// 封面默认压暗、歌名叠在封面上；鼠标移上去会亮起并浮起。点一下就播放这张专辑。
/// 正在播放的歌以一张居中的卡片浮在墙上，显示当前歌词和进度。
/// 封面只在进入视口附近时才加载，几千张专辑也不卡。
/// </summary>
public sealed class CoverGalleryView : UserControl
{
    /// <summary>用户点了一张专辑：返回该专辑的曲目。</summary>
    public event EventHandler<List<TrackItem>>? AlbumPicked;
    public event EventHandler? CloseRequested;
    public event EventHandler? PlayPauseRequested;

    private sealed class Tile
    {
        public required List<TrackItem> Tracks { get; init; }
        public required string Title { get; init; }
        public required string Artist { get; init; }
        public string? CoverPath { get; init; }
        public int Cols = 1, Rows = 1;
        public int Col, Row;
        public double X, Y, W, H;
        public Grid Host = null!;
        public Grid Surface = null!;
        public Border Dim = null!;
        public Border Ring = null!;
        public Image? Img;
        public bool Loaded;
    }

    private readonly Grid _root = new();
    private readonly ScrollViewer _scroll = new();
    private readonly Canvas _canvas = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _empty = new();
    private readonly List<Tile> _tiles = new();
    private readonly Compositor _compositor;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _relayout;
    private string? _currentKey;
    private bool _built;
    private double _cell = 150;

    private const double Gap = 4;
    private const double TargetCell = 150;

    // 居中的“正在播放”卡片
    private readonly Grid _hero = new();
    private readonly TextBlock _heroLabel = new();
    private readonly TextBlock _heroTitle = new();
    private readonly TextBlock _heroArtist = new();
    private readonly TextBlock _lyPrev = new(), _lyCur = new(), _lyNext = new();
    private readonly Image _heroCover = new();
    private readonly ProgressBar _heroBar = new();
    private readonly FontIcon _heroPlayIcon = new();
    private string _lastCur = "";

    public CoverGalleryView()
    {
        RequestedTheme = ElementTheme.Dark;           // 画廊本身是深色的“展厅”
        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _root.Background = new SolidColorBrush(Color.FromArgb(255, 14, 14, 18));

        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Content = _canvas;
        _scroll.ViewChanged += (s, e) => UpdateRealization(false);

        _title.FontSize = 16;
        _title.FontWeight = FontWeights.SemiBold;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));

        var randomBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new FontIcon { Glyph = "", FontSize = 13 }, new TextBlock { Text = L.T("随便放一张"), FontSize = 12 } }
            },
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 5, 12, 5)
        };
        randomBtn.Click += (s, e) => PickRandom();

        var closeBtn = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 13 },
            Width = 32, Height = 32, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16)
        };
        ToolTipService.SetToolTip(closeBtn, L.T("关闭画廊 (Esc)"));
        closeBtn.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);

        var header = new Grid
        {
            Height = 52,
            Padding = new Thickness(20, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Top,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 10,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(235, 14, 14, 18), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0, 14, 14, 18), Offset = 1 }
                }
            }
        };
        header.Children.Add(_title);
        Grid.SetColumn(randomBtn, 1); header.Children.Add(randomBtn);
        Grid.SetColumn(closeBtn, 2); header.Children.Add(closeBtn);

        _empty.Text = L.T("曲库里还没有可展示的封面");
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255));
        _empty.Visibility = Visibility.Collapsed;

        BuildHero();

        _root.Children.Add(_scroll);
        _root.Children.Add(_empty);
        _root.Children.Add(header);
        _root.Children.Add(_hero);
        Content = _root;

        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _relayout = dq.CreateTimer();
        _relayout.Interval = TimeSpan.FromMilliseconds(200);
        _relayout.IsRepeating = false;
        _relayout.Tick += (s, e) => Rebuild(animateIn: false);
        SizeChanged += (s, e) =>
        {
            if (!_built) return;
            _relayout.Stop(); _relayout.Start();
            PositionHero();
        };
    }

    // ---------------- 正在播放卡片 ----------------

    private void BuildHero()
    {
        _hero.Width = 460;
        _hero.Height = 400;
        _hero.HorizontalAlignment = HorizontalAlignment.Center;
        _hero.VerticalAlignment = VerticalAlignment.Center;
        _hero.Visibility = Visibility.Collapsed;
        _hero.CornerRadius = new CornerRadius(2);
        _hero.BorderThickness = new Thickness(1);
        _hero.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 140, 130, 255));
        _hero.Background = new SolidColorBrush(Color.FromArgb(255, 20, 20, 26));
        ElementCompositionPreview.SetIsTranslationEnabled(_hero, true);

        _heroCover.Stretch = Stretch.UniformToFill;
        _hero.Children.Add(_heroCover);
        // 压暗，保证歌词可读
        _hero.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(175, 8, 8, 12)) });

        var white = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        _heroLabel.FontSize = 10;
        _heroLabel.Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
        _heroTitle.FontSize = 13;
        _heroTitle.FontWeight = FontWeights.SemiBold;
        _heroTitle.Foreground = white;
        _heroTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _heroArtist.FontSize = 10;
        _heroArtist.Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
        _heroArtist.TextTrimming = TextTrimming.CharacterEllipsis;

        foreach (var tb in new[] { _lyPrev, _lyCur, _lyNext })
        {
            tb.TextWrapping = TextWrapping.Wrap;
            tb.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        _lyPrev.FontSize = 15; _lyPrev.Foreground = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
        _lyCur.FontSize = 26; _lyCur.FontWeight = FontWeights.SemiBold; _lyCur.Foreground = white;
        _lyCur.MaxLines = 3;
        _lyNext.FontSize = 15; _lyNext.Foreground = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));

        _heroPlayIcon.Glyph = ""; _heroPlayIcon.FontSize = 12;
        var playBtn = new Button
        {
            Content = _heroPlayIcon,
            Width = 28, Height = 28, Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        playBtn.Click += (s, e) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);

        var openBtn = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 11 },
            Width = 28, Height = 28, Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        ToolTipService.SetToolTip(openBtn, L.T("回到播放界面"));
        openBtn.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);

        _heroBar.Height = 3;
        _heroBar.Minimum = 0; _heroBar.Maximum = 1;
        _heroBar.VerticalAlignment = VerticalAlignment.Center;

        var bottom = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        bottom.Children.Add(playBtn);
        Grid.SetColumn(_heroBar, 1); bottom.Children.Add(_heroBar);
        Grid.SetColumn(openBtn, 2); bottom.Children.Add(openBtn);

        var lyrics = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 10,
            Children = { _lyPrev, _lyCur, _lyNext }
        };

        var body = new Grid
        {
            Padding = new Thickness(22, 18, 22, 14),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };
        var head = new StackPanel { Spacing = 2, Children = { _heroLabel, _heroTitle, _heroArtist } };
        body.Children.Add(head);
        Grid.SetRow(lyrics, 1); body.Children.Add(lyrics);
        Grid.SetRow(bottom, 2); body.Children.Add(bottom);
        _hero.Children.Add(body);
    }

    private void PositionHero()
    {
        _hero.Width = Math.Clamp(ActualWidth * 0.34, 300, 520);
        _hero.Height = Math.Clamp(ActualHeight * 0.62, 280, 460);
        _lyCur.FontSize = _hero.Width > 420 ? 26 : 21;
    }

    public void SetNowPlaying(TrackItem? t)
    {
        if (t == null) { _hero.Visibility = Visibility.Collapsed; return; }
        bool first = _hero.Visibility != Visibility.Visible;
        _hero.Visibility = Visibility.Visible;
        PositionHero();

        var tile = _tiles.FirstOrDefault(x => x.Tracks.Contains(t));
        int n = tile != null ? tile.Tracks.IndexOf(t) + 1 : 0;
        _heroLabel.Text = tile != null && tile.Tracks.Count > 1 ? $"{L.T("正在播放")} · {n:00}" : L.T("正在播放");
        _heroTitle.Text = t.Title;
        _heroArtist.Text = t.Artist;
        try
        {
            if (!string.IsNullOrEmpty(t.CoverPath) && File.Exists(t.CoverPath))
                _heroCover.Source = new BitmapImage(new Uri(t.CoverPath)) { DecodePixelWidth = 640 };
            else _heroCover.Source = null;
        }
        catch { _heroCover.Source = null; }
        _lyPrev.Text = ""; _lyCur.Text = ""; _lyNext.Text = ""; _lastCur = "";
        _heroBar.Value = 0;

        _currentKey = tile == null ? null : KeyOf(tile);
        foreach (var x in _tiles) UpdateRing(x);
        if (first) PopIn(_hero);
    }

    public void SetLyric(string prev, string cur, string next)
    {
        _lyPrev.Text = prev; _lyNext.Text = next;
        if (cur != _lastCur)
        {
            _lastCur = cur;
            _lyCur.Text = cur;
            var v = ElementCompositionPreview.GetElementVisual(_lyCur);
            var a = _compositor.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0f, 0.15f);
            a.InsertKeyFrame(1f, 1f);
            a.Duration = TimeSpan.FromMilliseconds(380);
            v.StartAnimation("Opacity", a);
            var ty = _compositor.CreateScalarKeyFrameAnimation();
            ElementCompositionPreview.SetIsTranslationEnabled(_lyCur, true);
            ty.InsertKeyFrame(0f, 10f);
            ty.InsertKeyFrame(1f, 0f, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 1f), new Vector2(0.3f, 1f)));
            ty.Duration = TimeSpan.FromMilliseconds(420);
            v.StartAnimation("Translation.Y", ty);
        }
    }

    public void SetProgress(double current, double total)
    {
        _heroBar.Value = total > 0 ? Math.Clamp(current / total, 0, 1) : 0;
    }

    public void SetIsPlaying(bool playing) => _heroPlayIcon.Glyph = playing ? "" : "";

    private void PopIn(UIElement e)
    {
        var v = ElementCompositionPreview.GetElementVisual(e);
        v.CenterPoint = new Vector3((float)_hero.Width / 2, (float)_hero.Height / 2, 0);
        var op = _compositor.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(0f, 0f); op.InsertKeyFrame(1f, 1f);
        op.Duration = TimeSpan.FromMilliseconds(420);
        v.StartAnimation("Opacity", op);
        var sc = _compositor.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0f, new Vector3(0.92f, 0.92f, 1f));
        sc.InsertKeyFrame(1f, Vector3.One, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f)));
        sc.Duration = TimeSpan.FromMilliseconds(560);
        v.StartAnimation("Scale", sc);
    }

    // ---------------- 数据 ----------------

    private static string KeyOf(Tile t) => t.Tracks[0].Album is { Length: > 0 } a && a is not ("Unknown Album" or "未知专辑") && t.Tracks.Count > 0
        ? "album:" + a : "track:" + t.Tracks[0].Id;

    /// <summary>用曲库内容（重新）构建整面墙；没有专辑信息的歌单独占一块。</summary>
    public void Load(IEnumerable<TrackItem> library)
    {
        _tiles.Clear();
        var usable = library.Where(t => !t.IsOnline && !t.IsMissing).ToList();
        var seen = new Dictionary<string, List<TrackItem>>();
        var order = new List<List<TrackItem>>();
        foreach (var t in usable)
        {
            bool noAlbum = string.IsNullOrWhiteSpace(t.Album) || t.Album is "Unknown Album" or "未知专辑";
            string key = noAlbum ? "track:" + t.Id : "album:" + t.Album;
            if (!seen.TryGetValue(key, out var list)) { seen[key] = list = new List<TrackItem>(); order.Add(list); }
            list.Add(t);
        }
        foreach (var members in order.OrderByDescending(m => m.Max(x => x.AddedTimestamp)))
        {
            var withCover = members.FirstOrDefault(x => !string.IsNullOrEmpty(x.CoverPath) && File.Exists(x.CoverPath));
            var artists = members.Select(x => x.Artist).Distinct().ToList();
            bool single = members.Count == 1 || string.IsNullOrWhiteSpace(members[0].Album) || members[0].Album is "Unknown Album" or "未知专辑";
            _tiles.Add(new Tile
            {
                Tracks = members,
                Title = single ? members[0].Title : members[0].Album,
                Artist = artists.Count == 1 ? artists[0] : L.T("群星"),
                CoverPath = withCover?.CoverPath
            });
        }
        _built = true;
        _title.Text = L.F("封面画廊 · {0} 张专辑", _tiles.Count);
        _empty.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Rebuild(animateIn: true);
    }

    /// <summary>当前播放曲目变了：墙上对应的专辑亮起边框，中间卡片换成这首歌。</summary>
    public void SetCurrent(TrackItem? track) => SetNowPlaying(track);

    private void UpdateRing(Tile t)
    {
        if (t.Ring == null) return;
        bool on = _currentKey != null && KeyOf(t) == _currentKey;
        var v = ElementCompositionPreview.GetElementVisual(t.Ring);
        if (on)
        {
            t.Ring.Opacity = 1;
            var a = _compositor.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0f, 0.4f); a.InsertKeyFrame(0.5f, 1f); a.InsertKeyFrame(1f, 0.4f);
            a.Duration = TimeSpan.FromSeconds(2.6);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            v.StartAnimation("Opacity", a);
        }
        else { v.StopAnimation("Opacity"); t.Ring.Opacity = 0; }
    }

    // ---------------- 布局：紧凑马赛克 ----------------

    private static int Hash(string s)
    {
        int h = 17;
        foreach (char ch in s) h = unchecked(h * 31 + ch);
        return h & 0x7fffffff;
    }

    private void Rebuild(bool animateIn)
    {
        double w = ActualWidth;
        if (w < 200) w = 1000;
        int cols = Math.Max(3, (int)Math.Round((w + Gap) / (TargetCell + Gap)));
        _cell = (w - (cols - 1) * Gap) / cols;

        // 先给每块定大小（按歌名哈希，稳定不乱跳），再用“找第一个放得下的空位”铺满
        var occupied = new List<bool[]>();
        bool Fits(int r, int c, int tw, int th)
        {
            if (c + tw > cols) return false;
            for (int y = r; y < r + th; y++)
            {
                if (y >= occupied.Count) continue;
                for (int x = c; x < c + tw; x++) if (occupied[y][x]) return false;
            }
            return true;
        }
        void Mark(int r, int c, int tw, int th)
        {
            while (occupied.Count < r + th) occupied.Add(new bool[cols]);
            for (int y = r; y < r + th; y++) for (int x = c; x < c + tw; x++) occupied[y][x] = true;
        }

        foreach (var t in _tiles)
        {
            int roll = Hash(t.Title + t.Artist) % 100;
            int tw = 1, th = 1;
            if (cols >= 5)
            {
                if (roll < 52) { tw = 1; th = 1; }
                else if (roll < 68) { tw = 2; th = 1; }
                else if (roll < 79) { tw = 1; th = 2; }
                else if (roll < 93) { tw = 2; th = 2; }
                else { tw = 3; th = 2; }
            }
            else if (roll >= 60) { tw = 2; th = 2; }
            else if (roll >= 40) { tw = 2; th = 1; }
            t.Cols = Math.Min(tw, cols); t.Rows = th;
        }

        int scanFrom = 0;
        foreach (var t in _tiles)
        {
            bool placed = false;
            for (int r = scanFrom; !placed; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    if (!Fits(r, c, t.Cols, t.Rows)) continue;
                    t.Row = r; t.Col = c;
                    Mark(r, c, t.Cols, t.Rows);
                    placed = true;
                    break;
                }
            }
            // 完全填满的行不用再扫
            while (scanFrom < occupied.Count && occupied[scanFrom].All(b => b)) scanFrom++;
        }

        _canvas.Children.Clear();
        _canvas.Width = w;
        _canvas.Height = occupied.Count * (_cell + Gap);

        foreach (var t in _tiles)
        {
            t.X = t.Col * (_cell + Gap);
            t.Y = t.Row * (_cell + Gap);
            t.W = t.Cols * _cell + (t.Cols - 1) * Gap;
            t.H = t.Rows * _cell + (t.Rows - 1) * Gap;
            t.Loaded = false; t.Img = null;
            BuildTile(t);
            Canvas.SetLeft(t.Host, t.X);
            Canvas.SetTop(t.Host, t.Y + 52);
            _canvas.Children.Add(t.Host);
        }
        _canvas.Height += 52 + 24;

        PositionHero();
        UpdateRealization(animateIn);
    }

    private void BuildTile(Tile t)
    {
        double minSide = Math.Min(t.W, t.H);
        var surface = new Grid
        {
            Width = t.W, Height = t.H,
            Background = PlaceholderBrush(t.Title),
            CornerRadius = new CornerRadius(2)
        };
        if (t.CoverPath == null)
        {
            surface.Children.Add(new TextBlock
            {
                Text = t.Title.Length > 0 ? t.Title[..1].ToUpperInvariant() : "♪",
                FontSize = minSide * 0.42,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        // 平时整体压暗，悬停时亮起来
        var dim = new Border { Background = new SolidColorBrush(Color.FromArgb(120, 8, 8, 14)), IsHitTestVisible = false };
        surface.Children.Add(dim);

        // 底部渐变 + 叠在封面上的歌名 / 歌手
        double titleSize = Math.Clamp(minSide * 0.125, 13, 30);
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(10, 22, 10, 9),
            Spacing = 1,
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(190, 0, 0, 0), Offset = 1 }
                }
            },
            Children =
            {
                new TextBlock
                {
                    Text = t.Title, FontSize = titleSize, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                    TextWrapping = TextWrapping.Wrap, MaxLines = t.Rows >= 2 ? 4 : 2,
                    TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = titleSize * 1.12
                },
                new TextBlock
                {
                    Text = t.Artist, FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };
        surface.Children.Add(text);

        // 左上角：专辑里的歌曲数
        if (t.Tracks.Count > 1)
        {
            surface.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 8, 0, 0),
                Padding = new Thickness(5, 1, 5, 1),
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = t.Tracks.Count.ToString("00"), FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255))
                }
            });
        }

        var ring = new Border
        {
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 150, 140, 255)),
            Opacity = 0, IsHitTestVisible = false
        };
        surface.Children.Add(ring);

        var host = new Grid { Width = t.W, Height = t.H, Background = new SolidColorBrush(Colors.Transparent) };
        host.Children.Add(surface);
        ElementCompositionPreview.SetIsTranslationEnabled(host, true);
        ToolTipService.SetToolTip(host, t.Artist.Length > 0 ? $"{t.Title} — {t.Artist}" : t.Title);

        host.PointerEntered += (s, e) => Hover(t, true);
        host.PointerExited += (s, e) => Hover(t, false);
        host.PointerPressed += (s, e) => Press(t, true);
        host.PointerCanceled += (s, e) => Press(t, false);
        host.PointerCaptureLost += (s, e) => Press(t, false);
        host.Tapped += (s, e) => { Press(t, false); Pick(t); };

        t.Host = host; t.Surface = surface; t.Dim = dim; t.Ring = ring;
        UpdateRing(t);
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
                new GradientStop { Color = Hsv(hue, 0.35, 0.55), Offset = 0 },
                new GradientStop { Color = Hsv((hue + 40) % 360, 0.45, 0.32), Offset = 1 }
            }
        };
    }

    // ---------------- 懒加载封面 ----------------

    private void UpdateRealization(bool animateIn)
    {
        double top = _scroll.VerticalOffset - 600;
        double bottom = _scroll.VerticalOffset + Math.Max(_scroll.ViewportHeight, 700) + 600;
        double viewTop = _scroll.VerticalOffset;
        double viewBottom = _scroll.VerticalOffset + Math.Max(_scroll.ViewportHeight, 800);
        double cx = ActualWidth / 2, cy = viewTop + Math.Max(_scroll.ViewportHeight, 800) / 2;
        double diag = Math.Sqrt(cx * cx + (viewBottom - viewTop) * (viewBottom - viewTop) / 4) + 1;

        foreach (var t in _tiles)
        {
            double y = t.Y + 52;
            bool want = y + t.H > top && y < bottom;
            if (want && !t.Loaded)
            {
                t.Loaded = true;
                if (t.CoverPath != null) LoadImage(t);
                if (animateIn && y + t.H > viewTop && y < viewBottom)
                {
                    double dx = t.X + t.W / 2 - cx, dy = y + t.H / 2 - cy;
                    EntranceAnimation(t, Math.Sqrt(dx * dx + dy * dy) / diag);
                }
            }
            else if (!want && t.Loaded)
            {
                t.Loaded = false;
                if (t.Img != null) { t.Surface.Children.Remove(t.Img); t.Img.Source = null; t.Img = null; }
            }
        }
    }

    private void LoadImage(Tile t)
    {
        try
        {
            var bmp = new BitmapImage { DecodePixelWidth = (int)Math.Min(640, Math.Max(t.W, t.H) * 1.3) };
            var img = new Image { Stretch = Stretch.UniformToFill, Source = bmp };
            bmp.UriSource = new Uri(t.CoverPath!);
            img.ImageOpened += (s, e) =>
            {
                var v = ElementCompositionPreview.GetElementVisual(img);
                v.Opacity = 0;
                var a = _compositor.CreateScalarKeyFrameAnimation();
                a.InsertKeyFrame(1f, 1f);
                a.Duration = TimeSpan.FromMilliseconds(320);
                v.StartAnimation("Opacity", a);
            };
            img.ImageFailed += (s, e) => ElementCompositionPreview.GetElementVisual(img).Opacity = 0;
            t.Surface.Children.Insert(0, img);
            t.Img = img;
        }
        catch { }
    }

    /// <summary>进场：从屏幕中心向外一圈圈亮起，每块略微放大浮起。</summary>
    private void EntranceAnimation(Tile t, double dist)
    {
        double delay = Math.Min(0.9, dist * 0.85);
        var v = ElementCompositionPreview.GetElementVisual(t.Host);
        v.CenterPoint = new Vector3((float)t.W / 2, (float)t.H / 2, 0);
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

        var op = _compositor.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(0f, 0f); op.InsertKeyFrame(1f, 1f);
        op.Duration = TimeSpan.FromMilliseconds(520);
        op.DelayTime = TimeSpan.FromSeconds(delay);
        op.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Opacity", op);

        var sc = _compositor.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0f, new Vector3(0.9f, 0.9f, 1f));
        sc.InsertKeyFrame(1f, Vector3.One, ease);
        sc.Duration = TimeSpan.FromMilliseconds(720);
        sc.DelayTime = TimeSpan.FromSeconds(delay);
        sc.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Scale", sc);

        var rise = _compositor.CreateScalarKeyFrameAnimation();
        rise.InsertKeyFrame(0f, 26f); rise.InsertKeyFrame(1f, 0f, ease);
        rise.Duration = TimeSpan.FromMilliseconds(720);
        rise.DelayTime = TimeSpan.FromSeconds(delay);
        rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Translation.Y", rise);
    }

    // ---------------- 交互 ----------------

    private void Hover(Tile t, bool on)
    {
        var host = ElementCompositionPreview.GetElementVisual(t.Host);
        host.CenterPoint = new Vector3((float)t.W / 2, (float)t.H / 2, 0);
        var spring = _compositor.CreateSpringVector3Animation();
        spring.FinalValue = on ? new Vector3(1.035f, 1.035f, 1f) : Vector3.One;
        spring.DampingRatio = 0.65f;
        spring.Period = TimeSpan.FromMilliseconds(55);
        host.StartAnimation("Scale", spring);

        // 压暗层淡出 = 封面亮起来
        var dim = ElementCompositionPreview.GetElementVisual(t.Dim);
        var f = _compositor.CreateScalarKeyFrameAnimation();
        f.InsertKeyFrame(1f, on ? 0f : 1f);
        f.Duration = TimeSpan.FromMilliseconds(on ? 200 : 260);
        dim.StartAnimation("Opacity", f);

        Canvas.SetZIndex(t.Host, on ? 10 : 0);
    }

    private void Press(Tile t, bool down)
    {
        var host = ElementCompositionPreview.GetElementVisual(t.Host);
        var spring = _compositor.CreateSpringVector3Animation();
        spring.FinalValue = down ? new Vector3(0.985f, 0.985f, 1f) : new Vector3(1.035f, 1.035f, 1f);
        spring.DampingRatio = 0.6f;
        spring.Period = TimeSpan.FromMilliseconds(45);
        host.StartAnimation("Scale", spring);
    }

    private void Pick(Tile t)
    {
        var tracks = t.Tracks.Where(x => !x.IsMissing).ToList();
        if (tracks.Count > 0) AlbumPicked?.Invoke(this, tracks);
    }

    private void PickRandom()
    {
        if (_tiles.Count == 0) return;
        var t = _tiles[Random.Shared.Next(_tiles.Count)];
        double target = Math.Max(0, t.Y + 52 - _scroll.ViewportHeight / 2 + t.H / 2);
        _scroll.ChangeView(null, target, null, disableAnimation: false);

        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var timer = dq.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(650);
        timer.IsRepeating = false;
        timer.Tick += (s, e) =>
        {
            UpdateRealization(false);
            Hover(t, true);
            var tm2 = dq.CreateTimer();
            tm2.Interval = TimeSpan.FromMilliseconds(380);
            tm2.IsRepeating = false;
            tm2.Tick += (s2, e2) => { Hover(t, false); Pick(t); };
            tm2.Start();
        };
        timer.Start();
    }

    /// <summary>打开时的整体淡入。</summary>
    public void PlayOpenAnimation()
    {
        var v = ElementCompositionPreview.GetElementVisual(_root);
        var a = _compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, 0f); a.InsertKeyFrame(1f, 1f);
        a.Duration = TimeSpan.FromMilliseconds(260);
        v.StartAnimation("Opacity", a);
    }
}
