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
        XamlRoot = root,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary
    };

    private static string Describe(TrackItem t)
    {
        string bitrate = t.Bitrate > 0 ? $" · {t.Bitrate}kbps" : "";
        string missing = t.IsMissing ? " · 文件丢失" : "";
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
            Text = $"发现 {groups.Count} 组重复。每组第一首是建议保留的（音质更高 / 文件在），其余默认勾选移除。\n只会从曲库里移除，不会删除磁盘上的文件。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
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
                        Text = (i == 0 ? "【建议保留】" : "") + Describe(g[i]),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                boxes.Add((cb, g[i]));
                panel.Children.Add(cb);
            }
        }

        var dlg = NewDialog(root, "重复歌曲",
            new ScrollViewer { Content = panel, MaxHeight = 420, MinWidth = 460 }, "从曲库移除所选", "关闭");
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
            Text = $"有 {missing.Count} 首歌的文件找不到了（被移走、重命名，或外接盘没插）。",
            TextWrapping = TextWrapping.Wrap
        });
        var list = new StackPanel { Spacing = 2 };
        foreach (var t in missing.Take(30))
            list.Children.Add(new TextBlock { Text = $"{t.Title} — {t.FilePath}", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        if (missing.Count > 30)
            list.Children.Add(new TextBlock { Text = $"…另有 {missing.Count - 30} 首", FontSize = 12 });
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220 });
        panel.Children.Add(new TextBlock
        {
            Text = "“重新定位”会在你选的文件夹里按文件名找回（收藏和歌单会保留）；“清理”会把它们从曲库移除。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        var dlg = NewDialog(root, "丢失的文件", new Grid { Children = { panel }, MinWidth = 440 }, "重新定位…", "关闭");
        dlg.SecondaryButtonText = "从曲库清理";
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
        var title = new TextBox { Header = "歌名", Text = track.Title };
        var artist = new TextBox { Header = "歌手", Text = track.Artist };
        var album = new TextBox { Header = "专辑", Text = track.Album };
        var apply = new CheckBox
        {
            Content = $"把歌手和专辑同步到同专辑的另外 {sameAlbumOthers} 首",
            Visibility = sameAlbumOthers > 0 ? Visibility.Visible : Visibility.Collapsed
        };
        var note = new TextBlock
        {
            Text = track.IsCueTrack ? "CUE 分轨只修改曲库里的显示，不写入音频文件。" : "修改会写回音频文件的标签。",
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 380, Children = { title, artist, album, apply, note } };

        var dlg = NewDialog(root, "编辑标签", panel, "保存");
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
        if (string.IsNullOrWhiteSpace(title.Text)) return null;
        return new TagEditResult(title.Text, artist.Text, album.Text, track.Album, apply.IsChecked == true);
    }

    public static async Task<(string Album, string Artist)?> ShowAlbumEditAsync(XamlRoot root, string album, string artist, int count)
    {
        var albumBox = new TextBox { Header = "专辑名", Text = album };
        var artistBox = new TextBox { Header = "歌手（留空则不改）", Text = artist };
        var panel = new StackPanel
        {
            Spacing = 10,
            MinWidth = 380,
            Children =
            {
                new TextBlock { Text = $"将修改这张专辑的 {count} 首歌，并写回各自的音频文件。", TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                albumBox,
                artistBox
            }
        };
        var dlg = NewDialog(root, "批量编辑专辑信息", panel, "保存");
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
        if (string.IsNullOrWhiteSpace(albumBox.Text)) return null;
        return (albumBox.Text, artistBox.Text);
    }

    // ---------------- 智能歌单 ----------------

    public static async Task<SmartPlaylistDef?> ShowSmartPlaylistAsync(XamlRoot root)
    {
        var name = new TextBox { Header = "名称", PlaceholderText = "例如：无损 · 我喜欢", MaxLength = 40 };

        var quality = new ComboBox { Header = "音质", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { "不限", "无损及以上", "仅 Hi-Res" }) quality.Items.Add(s);
        quality.SelectedIndex = 0;

        var fav = new CheckBox { Content = "只要我喜欢的" };

        var added = new ComboBox { Header = "添加时间", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { "不限", "最近 7 天", "最近 30 天", "最近 90 天" }) added.Items.Add(s);
        added.SelectedIndex = 0;

        var plays = new ComboBox { Header = "播放次数", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { "不限", "至少 1 次", "至少 3 次", "至少 10 次" }) plays.Items.Add(s);
        plays.SelectedIndex = 0;

        var sort = new ComboBox { Header = "排序", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { "最近添加", "最近播放", "播放最多", "歌名" }) sort.Items.Add(s);
        sort.SelectedIndex = 0;

        var limit = new ComboBox { Header = "最多显示", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { "不限", "50 首", "100 首", "200 首" }) limit.Items.Add(s);
        limit.SelectedIndex = 0;

        var panel = new StackPanel { Spacing = 10, MinWidth = 380, Children = { name, quality, fav, added, plays, sort, limit } };
        var dlg = NewDialog(root, "新建智能歌单", new ScrollViewer { Content = panel, MaxHeight = 480 }, "创建");
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;

        return new SmartPlaylistDef
        {
            Name = name.Text.Trim(),
            MinQualityTier = quality.SelectedIndex,
            OnlyFavorite = fav.IsChecked == true,
            AddedWithinDays = new[] { 0, 7, 30, 90 }[Math.Max(0, added.SelectedIndex)],
            MinPlayCount = new[] { 0, 1, 3, 10 }[Math.Max(0, plays.SelectedIndex)],
            SortBy = new[] { "added", "played", "plays", "title" }[Math.Max(0, sort.SelectedIndex)],
            Limit = new[] { 0, 50, 100, 200 }[Math.Max(0, limit.SelectedIndex)]
        };
    }
}
