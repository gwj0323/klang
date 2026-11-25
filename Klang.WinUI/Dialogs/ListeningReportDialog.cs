using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using Klang_WinUI.Services;

namespace Klang_WinUI.Dialogs;

/// <summary>听歌报告：最近 7 天 / 30 天 / 今年 / 全部的收听统计，做成一张能保存的图。</summary>
public static class ListeningReportDialog
{
    private const double CardWidth = 360;

    public static async Task ShowAsync(XamlRoot root)
    {
        var all = await Task.Run(() => ListeningService.Instance.ReadAll());

        var periods = new SelectorBar();
        string[] keys = { "week", "month", "year", "all" };
        string[] labels = { L.T("最近 7 天"), L.T("最近 30 天"), L.T("今年"), L.T("全部") };
        for (int i = 0; i < keys.Length; i++) periods.Items.Add(new SelectorBarItem { Text = labels[i], Tag = keys[i] });

        var cardHost = new Border();
        var status = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
        };
        var panel = new StackPanel { Spacing = 10, Children = { periods, cardHost, status } };

        var dlg = new ContentDialog
        {
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            RequestedTheme = ThemeService.Current,
            XamlRoot = root,
            Title = L.T("听歌报告"),
            Content = new ScrollViewer { Content = panel, HorizontalScrollMode = ScrollMode.Disabled },
            PrimaryButtonText = L.T("保存图片"),
            CloseButtonText = L.T("关闭"),
            DefaultButton = ContentDialogButton.Primary,
        };

        Border? card = null;
        int seq = 0;
        async void Show(string key)
        {
            int my = ++seq;
            var (from, to) = Range(key, all);
            var report = ListeningReport.Build(all, from, to);
            var built = await BuildCardAsync(report, labels[Array.IndexOf(keys, key)]);
            if (my != seq) return;
            card = built;
            cardHost.Child = built;
            dlg.IsPrimaryButtonEnabled = !report.IsEmpty;
            status.Text = "";
        }

        periods.SelectionChanged += (s, e) => { if (s.SelectedItem?.Tag is string k) Show(k); };
        // 记录少时默认看全部，否则看最近 7 天
        int defaultIdx = all.Count(e => e.Start >= DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds()) >= 5 ? 0 : 3;
        periods.SelectedItem = periods.Items[defaultIdx];

        dlg.PrimaryButtonClick += async (s, args) =>
        {
            args.Cancel = true;   // 保存后不关闭
            var deferral = args.GetDeferral();
            try
            {
                if (card == null) return;
                var path = await SaveCardAsync(card);
                status.Text = L.T("已保存：") + path;
            }
            catch (Exception ex)
            {
                status.Text = L.T("保存失败：") + ex.Message;
            }
            finally { deferral.Complete(); }
        };

        L.Apply(dlg);
        try { await dlg.ShowAsync(); }
        catch (Exception ex) { App.LogError("ListeningReport", ex.Message); }
    }

    private static (DateTime From, DateTime To) Range(string key, List<ListenEntry> all)
    {
        var now = DateTime.Now;
        var to = now.Date.AddDays(1);
        return key switch
        {
            "week" => (now.Date.AddDays(-6), to),
            "month" => (now.Date.AddDays(-29), to),
            "year" => (new DateTime(now.Year, 1, 1), to),
            _ => (all.Count > 0 ? DateTimeOffset.FromUnixTimeSeconds(all[0].Start).LocalDateTime.Date : now.Date, to),
        };
    }

    // ================= 报告卡片 =================

    private static readonly Brush White = new SolidColorBrush(Colors.White);
    private static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Faint = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Tile = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    private static async Task<Border> BuildCardAsync(ListeningReport r, string periodLabel)
    {
        var stack = new StackPanel { Spacing = 18, RequestedTheme = ElementTheme.Dark };
        var bg = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        var stop0 = new GradientStop { Color = Color.FromArgb(255, 0x3A, 0x4A, 0x44), Offset = 0 };
        var stop1 = new GradientStop { Color = Color.FromArgb(255, 0x1A, 0x22, 0x26), Offset = 1 };
        bg.GradientStops.Add(stop0);
        bg.GradientStops.Add(stop1);
        var card = new Border { Width = CardWidth, Padding = new Thickness(26, 26, 26, 20), Background = bg, Child = stack };

        // 标题
        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(Text(L.T("听歌报告"), 12, Faint, FontWeights.SemiBold));
        head.Children.Add(Text(periodLabel, 24, White, FontWeights.Bold));
        head.Children.Add(Text($"{r.From:yyyy.M.d} – {r.To.AddDays(-1):yyyy.M.d}", 12, Dim));
        stack.Children.Add(head);

        if (r.IsEmpty)
        {
            stack.Children.Add(Text(L.T("这段时间还没有听歌记录。"), 15, White, FontWeights.SemiBold, wrap: true));
            stack.Children.Add(Text(L.T("从这个版本开始，Klang 会在本机记录每首歌听了多久（只存在这台电脑上）。多听几首再来看吧。"), 12, Dim, wrap: true));
            stack.Children.Add(Text("KLANG", 12, Faint));
            return card;
        }

        // 三个大数字
        var nums = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < 3; i++) nums.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddNumber(nums, 0, FormatHours(r.Seconds), L.T("听歌时长"));
        AddNumber(nums, 1, r.Plays.ToString(), L.T("播放次数"));
        AddNumber(nums, 2, r.Songs.ToString(), L.T("首歌曲"));
        stack.Children.Add(nums);

        // 最常听的歌
        var songs = new StackPanel { Spacing = 8 };
        songs.Children.Add(Section(L.T("最常听的歌")));
        for (int i = 0; i < r.TopSongs.Count; i++)
        {
            var s = r.TopSongs[i];
            songs.Children.Add(i == 0 ? TopSongRow(s) : SongRow(i + 1, s));
        }
        stack.Children.Add(songs);

        // 最常听的歌手
        if (r.TopArtists.Count > 0)
        {
            var artists = new StackPanel { Spacing = 7 };
            artists.Children.Add(Section(L.T("最常听的歌手")));
            long max = Math.Max(1, r.TopArtists[0].Seconds);
            for (int i = 0; i < r.TopArtists.Count; i++) artists.Children.Add(ArtistRow(i + 1, r.TopArtists[i], max));
            stack.Children.Add(artists);
        }

        // 时段分布
        var hours = new StackPanel { Spacing = 6 };
        hours.Children.Add(Section(L.T("什么时候听")));
        hours.Children.Add(HourChart(r.Hours));
        int peak = r.PeakHour();
        if (peak >= 0)
            hours.Children.Add(Text(L.F("你最常在 {0}:00 – {1}:00 听歌，{2}", peak.ToString("00"), ((peak + 3) % 24).ToString("00"), PeakName(peak)), 12, Dim, wrap: true));
        stack.Children.Add(hours);

        // 几句小结
        var facts = new StackPanel { Spacing = 4 };
        facts.Children.Add(Fact(L.F("听了 {0} 位歌手，{1} 天在听歌", r.Artists, r.ActiveDays)));
        if (r.NewSongs > 0) facts.Children.Add(Fact(L.F("第一次听的歌：{0} 首", r.NewSongs)));
        if (r.BusiestDay is { } bd && r.ActiveDays > 1) facts.Children.Add(Fact(L.F("听得最多的一天：{0}，{1}", bd.Day.ToString("M.d"), FormatHours(bd.Seconds))));
        if (r.LateNight is { } ln) facts.Children.Add(Fact(L.F("最晚的一次：{0}，在听《{1}》", ln.At.ToString("HH:mm"), ln.Entry.Title)));
        stack.Children.Add(facts);

        stack.Children.Add(Text("KLANG", 12, Faint));

        // 背景取第一名的封面主色
        var coverPath = CoverOf(r.TopSongs.FirstOrDefault()?.Sample);
        if (coverPath != null)
        {
            try
            {
                var (p, s2) = await DominantColor.GetAsync(coverPath);
                stop0.Color = DominantColor.Darken(p, 0.7);
                stop1.Color = DominantColor.Darken(s2, 0.35);
            }
            catch { }
        }
        return card;
    }

    private static string PeakName(int h)
    {
        int mid = (h + 1) % 24;
        return mid switch
        {
            < 5 => L.T("是个夜猫子"),
            < 9 => L.T("早起的鸟儿有歌听"),
            < 12 => L.T("上午是你的歌单时间"),
            < 14 => L.T("午休时间最放松"),
            < 18 => L.T("午后最适合听歌"),
            < 21 => L.T("傍晚是你的歌单时间"),
            _ => L.T("夜里的歌最好听"),
        };
    }

    private static string FormatHours(long seconds)
    {
        long minutes = seconds / 60;
        if (minutes < 60) return L.F("{0} 分钟", minutes);
        double h = seconds / 3600.0;
        return L.F("{0} 小时", h >= 100 ? h.ToString("0") : h.ToString("0.#"));
    }

    private static TextBlock Text(string s, double size, Brush fg, Windows.UI.Text.FontWeight? weight = null, bool wrap = false) => new()
    {
        Text = s,
        FontSize = size,
        Foreground = fg,
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    private static TextBlock Section(string s) => Text(s, 13, Faint, FontWeights.SemiBold);

    private static UIElement Fact(string s) => Text("· " + s, 12, Dim, wrap: true);

    private static void AddNumber(Grid g, int col, string value, string label)
    {
        var b = new Border
        {
            Background = Tile,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 10, 8, 10),
            Child = new StackPanel
            {
                Spacing = 2,
                Children = { Text(value, 18, White, FontWeights.Bold), Text(label, 11, Dim) },
            },
        };
        Grid.SetColumn(b, col);
        g.Children.Add(b);
    }

    private static UIElement TopSongRow(ListeningReport.SongStat s)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(Cover(s.Sample, 64, 8));
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        info.Children.Add(Text(s.Sample.Title, 16, White, FontWeights.SemiBold));
        info.Children.Add(Text(s.Sample.Artist, 12, Dim));
        info.Children.Add(Text(L.F("{0} 次 · {1}", s.Plays, FormatHours(s.Seconds)), 12, Faint));
        Grid.SetColumn(info, 1);
        g.Children.Add(info);
        return g;
    }

    private static UIElement SongRow(int rank, ListeningReport.SongStat s)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var n = Text(rank.ToString(), 13, Faint, FontWeights.SemiBold);
        n.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(n);
        var cover = Cover(s.Sample, 34, 5);
        Grid.SetColumn(cover, 1);
        g.Children.Add(cover);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Text(s.Sample.Title, 13, White, FontWeights.SemiBold));
        info.Children.Add(Text(s.Sample.Artist, 11, Dim));
        Grid.SetColumn(info, 2);
        g.Children.Add(info);
        var count = Text(L.F("{0} 次", s.Plays), 12, Faint);
        count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(count, 3);
        g.Children.Add(count);
        return g;
    }

    private static UIElement ArtistRow(int rank, ListeningReport.ArtistStat a, long max)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var n = Text(rank.ToString(), 13, Faint, FontWeights.SemiBold);
        g.Children.Add(n);
        var mid = new StackPanel { Spacing = 4 };
        mid.Children.Add(Text(a.Name, 13, White, FontWeights.SemiBold));
        mid.Children.Add(new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(6, 200.0 * a.Seconds / max),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
        });
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);
        var t = Text(FormatHours(a.Seconds), 12, Faint);
        Grid.SetColumn(t, 2);
        g.Children.Add(t);
        return g;
    }

    private static UIElement HourChart(double[] hours)
    {
        const double h = 46;
        double max = Math.Max(1, hours.Max());
        var bars = new Grid { Height = h, ColumnSpacing = 3 };
        for (int i = 0; i < 24; i++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rect = new Rectangle
            {
                Height = Math.Max(2, h * hours[i] / max),
                VerticalAlignment = VerticalAlignment.Bottom,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb((byte)(hours[i] > 0 ? 0xD0 : 0x30), 0xFF, 0xFF, 0xFF)),
            };
            Grid.SetColumn(rect, i);
            bars.Children.Add(rect);
        }
        var axis = new Grid();
        for (int i = 0; i < 4; i++) axis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string[] ticks = { "0", "6", "12", "18" };
        for (int i = 0; i < 4; i++)
        {
            var tb = Text(ticks[i], 10, Faint);
            Grid.SetColumn(tb, i);
            axis.Children.Add(tb);
        }
        return new StackPanel { Spacing = 3, Children = { bars, axis } };
    }

    private static FrameworkElement Cover(ListenEntry e, double size, double radius)
    {
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(radius),
            Background = Tile,
        };
        var path = CoverOf(e);
        if (path != null)
        {
            try
            {
                border.Child = new Image
                {
                    Stretch = Stretch.UniformToFill,
                    Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = (int)(size * 2) },
                };
            }
            catch { }
        }
        else
        {
            border.Child = new FontIcon { Glyph = "", FontSize = size * 0.4, Foreground = Faint };
        }
        return border;
    }

    /// <summary>这条记录的封面：记录时的封面文件，没了就到曲库里按 Id 找。</summary>
    private static string? CoverOf(ListenEntry? e)
    {
        if (e == null) return null;
        try
        {
            if (!string.IsNullOrEmpty(e.Cover) && File.Exists(e.Cover)) return e.Cover;
            if (string.IsNullOrEmpty(e.Source))
            {
                var t = LibraryService.Instance.Tracks.FirstOrDefault(x => x.Id == e.Key);
                var p = t?.CoverPath;
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            }
        }
        catch { }
        return null;
    }

    private static async Task<string> SaveCardAsync(FrameworkElement card)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(card);
        var pixels = await rtb.GetPixelsAsync();

        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Klang");
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"Klang_report_{DateTime.Now:yyyyMMdd_HHmmss}.png");

        using var ms = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels.ToArray());
        await enc.FlushAsync();
        ms.Seek(0);
        var bytes = new byte[ms.Size];
        await ms.ReadAsync(bytes.AsBuffer(), (uint)ms.Size, InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
}
