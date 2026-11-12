using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Dialogs;

public enum MissingChoice { None, Relocate, Remove }

/// <summary>曲库管理用到的几个小对话框（重复歌曲、丢失文件、标签编辑、智能歌单），都用代码搭，不单独写 XAML。</summary>
public static class LibraryDialogs
{
    private static ContentDialog NewDialog(XamlRoot root, string title, object content, string primary, string close = "取消") => new()
    {
        Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
        RequestedTheme = ThemeService.Current,
        XamlRoot = root,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary
    };

    /// <summary>次要文字颜色：按窗口内容的实际主题取（Application.Current.Resources 只给浅色值，深色下会是深灰字）。</summary>
    private static Microsoft.UI.Xaml.Media.Brush Secondary(XamlRoot root)
    {
        if (root.Content is FrameworkElement fe && ThemeService.Brush(fe, "TextFillColorSecondaryBrush") is { } b) return b;
        return (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    private static string Describe(TrackItem t)
    {
        string bitrate = t.Bitrate > 0 ? $" · {t.Bitrate}kbps" : "";
        string missing = t.IsMissing ? L.T(" · 文件丢失") : "";
        return $"{t.FormatBadge}{bitrate}{missing}\n{t.FilePath}";
    }

    // ---------------- 重复歌曲 ----------------

    /// <summary>列出每组重复歌曲，默认勾选“建议移除”的（质量较低的）；返回用户确认要从曲库移除的曲目。</summary>
    public static async Task<List<TrackItem>?> ShowDuplicatesAsync(XamlRoot root, List<List<TrackItem>> groups)
    {
        var panel = new StackPanel { Spacing = 4 };
        var boxes = new List<(CheckBox Box, TrackItem Track)>();

        panel.Children.Add(new TextBlock
        {
            Text = L.F("共 {0} 组重复。每组第一首默认保留，其余已勾选。\n仅从曲库中移除，不会删除磁盘上的文件。", groups.Count),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Secondary(root),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        });

        foreach (var g in groups)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{g[0].Title} — {g[0].Artist}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            for (int i = 0; i < g.Count; i++)
            {
                var cb = new CheckBox
                {
                    IsChecked = i > 0,
                    Content = new TextBlock
                    {
                        Text = (i == 0 ? L.T("【建议保留】") : "") + Describe(g[i]),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                boxes.Add((cb, g[i]));
                panel.Children.Add(cb);
            }
        }

        var dlg = NewDialog(root, L.T("重复歌曲"),
            new ScrollViewer { Content = panel, MaxHeight = 420, MinWidth = 460 }, L.T("从曲库移除所选"), L.T("关闭"));
        var res = await dlg.ShowAsync();
        if (res != ContentDialogResult.Primary) return null;
        return boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Track).ToList();
    }

    // ---------------- 丢失文件 ----------------

    public static async Task<MissingChoice> ShowMissingAsync(XamlRoot root, List<TrackItem> missing)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = L.F("有 {0} 首歌曲的文件未找到。", missing.Count),
            TextWrapping = TextWrapping.Wrap
        });
        var list = new StackPanel { Spacing = 2 };
        foreach (var t in missing.Take(30))
            list.Children.Add(new TextBlock { Text = $"{t.Title} — {t.FilePath}", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        if (missing.Count > 30)
            list.Children.Add(new TextBlock { Text = L.F("…另有 {0} 首", missing.Count - 30), FontSize = 12 });
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220 });
        panel.Children.Add(new TextBlock
        {
            Text = L.T("重新定位：在所选文件夹中按文件名查找。\n从曲库清理：将这些歌曲移出曲库。"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Secondary(root)
        });

        var dlg = NewDialog(root, L.T("丢失的文件"), new Grid { Children = { panel }, MinWidth = 440 }, L.T("重新定位…"), L.T("关闭"));
        dlg.SecondaryButtonText = L.T("从曲库清理");
        var res = await dlg.ShowAsync();
        return res switch
        {
            ContentDialogResult.Primary => MissingChoice.Relocate,
            ContentDialogResult.Secondary => MissingChoice.Remove,
            _ => MissingChoice.None
        };
    }

    // ---------------- 标签编辑 ----------------

    public readonly record struct TagEditResult(string Title, string Artist, string Album, string OldAlbum, bool ApplyToAlbum);

    public static async Task<TagEditResult?> ShowTagEditAsync(XamlRoot root, TrackItem track, int sameAlbumOthers)
    {
        var title = new TextBox { Header = L.T("歌名"), Text = track.Title };
        var artist = new TextBox { Header = L.T("歌手"), Text = track.Artist };
        var album = new TextBox { Header = L.T("专辑名"), Text = track.Album };
        var apply = new CheckBox
        {
            Content = L.F("同时修改同专辑的另外 {0} 首", sameAlbumOthers),
            Visibility = sameAlbumOthers > 0 ? Visibility.Visible : Visibility.Collapsed
        };
        var note = new TextBlock
        {
            Text = track.IsCueTrack ? L.T("CUE 分轨仅修改曲库中的显示信息，不写入音频文件。") : L.T("修改将写入音频文件的标签。"),
            FontSize = 12,
            Foreground = Secondary(root)
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 380, Children = { title, artist, album, apply, note } };

        var dlg = NewDialog(root, L.T("编辑标签"), panel, L.T("保存"));
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
        if (string.IsNullOrWhiteSpace(title.Text)) return null;
        return new TagEditResult(title.Text, artist.Text, album.Text, track.Album, apply.IsChecked == true);
    }

    public static async Task<(string Album, string Artist)?> ShowAlbumEditAsync(XamlRoot root, string album, string artist, int count)
    {
        var albumBox = new TextBox { Header = L.T("专辑名"), Text = album };
        var artistBox = new TextBox { Header = L.T("歌手（留空则不修改）"), Text = artist };
        var panel = new StackPanel
        {
            Spacing = 10,
            MinWidth = 380,
            Children =
            {
                new TextBlock { Text = L.F("修改将应用于该专辑的 {0} 首歌曲。", count), TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                albumBox,
                artistBox
            }
        };
        var dlg = NewDialog(root, L.T("批量编辑专辑信息"), panel, L.T("保存"));
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
        if (string.IsNullOrWhiteSpace(albumBox.Text)) return null;
        return (albumBox.Text, artistBox.Text);
    }

    // ---------------- 智能歌单 ----------------

    public static async Task<SmartPlaylistDef?> ShowSmartPlaylistAsync(XamlRoot root)
    {
        var name = new TextBox { Header = L.T("名称"), PlaceholderText = L.T("例如：无损 · 喜欢"), MaxLength = 40 };

        var quality = new ComboBox { Header = L.T("音质"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { L.T("不限"), L.T("无损及以上"), L.T("仅 Hi-Res") }) quality.Items.Add(s);
        quality.SelectedIndex = 0;

        var fav = new CheckBox { Content = L.T("仅喜欢的歌曲") };

        var added = new ComboBox { Header = L.T("添加时间"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { L.T("不限"), L.T("最近 7 天"), L.T("最近 30 天"), L.T("最近 90 天") }) added.Items.Add(s);
        added.SelectedIndex = 0;

        var plays = new ComboBox { Header = L.T("播放次数"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { L.T("不限"), L.T("至少 1 次"), L.T("至少 3 次"), L.T("至少 10 次") }) plays.Items.Add(s);
        plays.SelectedIndex = 0;

        var mood = new ComboBox { Header = L.T("情绪（按声学特征）"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { L.T("不限"), L.T("安静"), L.T("专注"), L.T("动感"), L.T("明亮"), L.T("沉稳") }) mood.Items.Add(s);
        mood.SelectedIndex = 0;

        var sort = new ComboBox { Header = L.T("排序"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { L.T("最近添加"), L.T("最近播放"), L.T("播放最多"), L.T("歌名") }) sort.Items.Add(s);
        sort.SelectedIndex = 0;

        var limit = new ComboBox { Header = L.T("最多显示"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { L.T("不限"), L.T("50 首"), L.T("100 首"), L.T("200 首") }) limit.Items.Add(s);
        limit.SelectedIndex = 0;

        var panel = new StackPanel { Spacing = 10, MinWidth = 380, Children = { name, quality, fav, mood, added, plays, sort, limit } };
        var dlg = NewDialog(root, L.T("新建智能歌单"), new ScrollViewer { Content = panel, MaxHeight = 480 }, L.T("创建"));
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;

        return new SmartPlaylistDef
        {
            Name = name.Text.Trim(),
            MinQualityTier = quality.SelectedIndex,
            OnlyFavorite = fav.IsChecked == true,
            AddedWithinDays = new[] { 0, 7, 30, 90 }[Math.Max(0, added.SelectedIndex)],
            MinPlayCount = new[] { 0, 1, 3, 10 }[Math.Max(0, plays.SelectedIndex)],
            Mood = new[] { "", "calm", "focus", "energetic", "bright", "deep" }[Math.Max(0, mood.SelectedIndex)],
            SortBy = new[] { "added", "played", "plays", "title" }[Math.Max(0, sort.SelectedIndex)],
            Limit = new[] { 0, 50, 100, 200 }[Math.Max(0, limit.SelectedIndex)]
        };
    }
}
