using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Klang_WinUI.Models;
using Klang_WinUI.Services;
using Klang_WinUI.Services.Ai;

namespace Klang_WinUI.Dialogs;

/// <summary>
/// AI 找歌：按听感描述（CLAP）、按歌词意思（E5）、按自动标签浏览。
/// 点一首就从它开始播放，后面接着排其余结果；可以把结果存成歌单。
/// </summary>
public static class AiSearchDialog
{
    /// <param name="onPlay">参数：点的歌、播放队列（从它开始）、要跳到的秒数（歌词搜索时是那一句的时间，否则 -1）。</param>
    public static async Task ShowAsync(XamlRoot root, Action<TrackItem, List<TrackItem>, double> onPlay)
    {
        var ai = AiService.Instance;
        var idx = AiIndexService.Instance;
        var lib = LibraryService.Instance.Tracks.ToList();
        // 画刷按主窗口实际的深 / 浅色取（直接取应用资源拿到的是系统主题的颜色）
        var scope = root.Content as FrameworkElement;
        var secondary = (scope != null ? ThemeService.Brush(scope, "TextFillColorSecondaryBrush") : null) ?? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var accent = (scope != null ? ThemeService.Brush(scope, "AccentTextFillColorPrimaryBrush") : null) ?? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];

        var modes = new SelectorBar();
        void AddMode(string tag, string text) => modes.Items.Add(new SelectorBarItem { Text = text, Tag = tag });
        AddMode("sound", L.T("按听感描述"));
        AddMode("lyrics", L.T("按歌词意思"));
        AddMode("tags", L.T("按标签"));

        var box = new TextBox { PlaceholderText = "", MinWidth = 520 };
        var go = new Button { Content = L.T("搜索"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var queryRow = new Grid { ColumnSpacing = 8 };
        queryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        queryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        queryRow.Children.Add(box);
        Grid.SetColumn(go, 1);
        queryRow.Children.Add(go);

        var hint = new TextBlock { FontSize = 12, Foreground = secondary, TextWrapping = TextWrapping.Wrap };
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, -6), Foreground = accent };
        var tagHost = new StackPanel { Spacing = 8 };
        // 标签区自己滚动、限高，下面的歌曲列表占满剩下的高度（以前列表固定 340，标签一多就被挤得很短）
        var tagScroll = new ScrollViewer { Content = tagHost, MaxHeight = 210, Visibility = Visibility.Collapsed, Padding = new Thickness(0, 0, 12, 0) };
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        var panel = new Grid { RowSpacing = 10, Width = 640, Height = Math.Clamp(root.Size.Height - 230, 420, 760) };
        UIElement[] rows = { modes, queryRow, hint, tagScroll, status, list };
        for (int r = 0; r < rows.Length; r++)
        {
            panel.RowDefinitions.Add(new RowDefinition { Height = r == rows.Length - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetRow((FrameworkElement)rows[r], r);
            panel.Children.Add(rows[r]);
        }

        var dlg = new ContentDialog
        {
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            RequestedTheme = ThemeService.Current,
            XamlRoot = root,
            Title = L.T("AI 找歌"),
            Content = panel,
            PrimaryButtonText = L.T("播放全部"),
            SecondaryButtonText = L.T("存为歌单…"),
            CloseButtonText = L.T("关闭"),
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
        };
        dlg.Resources["ContentDialogMaxWidth"] = 760.0;   // 默认最大宽度 548，放不下搜索框和按钮
        dlg.Resources["ContentDialogMaxHeight"] = 1200.0;  // 高度跟着窗口走（见上面 panel 的高度）

        var hits = new List<AiIndexService.Hit>();
        string mode = "sound";
        string lastQuery = "";
        CancellationTokenSource? cts = null;

        void ShowHits(List<AiIndexService.Hit> h, string emptyText)
        {
            hits = h;
            list.Items.Clear();
            foreach (var hit in h) list.Items.Add(Row(hit, secondary));
            dlg.IsPrimaryButtonEnabled = dlg.IsSecondaryButtonEnabled = h.Count > 0;
            if (h.Count == 0) status.Text = emptyText;
        }

        string Coverage()
        {
            int local = lib.Count(AiIndexService.Indexable);
            return mode == "lyrics"
                ? L.F("已索引 {0} 首的歌词（在线歌词要播放过才会加入）", idx.LyricsCount(lib))
                : L.F("已分析 {0} / {1} 首", idx.AudioCount(lib), local) + (idx.IsIndexing ? L.T("，后台还在分析") : "");
        }

        void UpdateMode()
        {
            bool tags = mode == "tags";
            queryRow.Visibility = tags ? Visibility.Collapsed : Visibility.Visible;
            tagScroll.Visibility = tags ? Visibility.Visible : Visibility.Collapsed;
            status.Text = "";
            ShowHits(new(), "");
            switch (mode)
            {
                case "sound":
                    box.PlaceholderText = L.T("例如：下雨天在咖啡馆听的爵士 / 适合跑步的 / sad piano ballad");
                    hint.Text = (ai.Ready(AiCatalog.Clap)
                        ? L.T("描述想听的感觉，英文最准。")
                        : L.T("需要先在设置 → AI 功能里下载“听感分析”。")) + "  " + Coverage();
                    box.IsEnabled = go.IsEnabled = ai.Ready(AiCatalog.Clap);
                    break;
                case "lyrics":
                    box.PlaceholderText = L.T("例如：唱下雨天等人的那首 / 关于离别和火车站");
                    hint.Text = (ai.Ready(AiCatalog.TextEmbed)
                        ? L.T("记得大意就行，点结果从那一句开始放。")
                        : L.T("需要先在设置 → AI 功能里下载“歌词搜索”。")) + "  " + Coverage();
                    box.IsEnabled = go.IsEnabled = ai.Ready(AiCatalog.TextEmbed);
                    break;
                default:
                    hint.Text = ai.Ready(AiCatalog.Clap) ? L.T("点标签列出对应的歌。") + "  " + Coverage()
                        : L.T("需要先在设置 → AI 功能里下载“听感分析”。");
                    _ = BuildTagsAsync();
                    break;
            }
        }

        async Task BuildTagsAsync()
        {
            tagHost.Children.Clear();
            if (!ai.Ready(AiCatalog.Clap)) return;
            status.Text = L.T("正在准备标签…");
            try { await idx.LabelEmbeddingsAsync(); }
            catch (Exception ex) { status.Text = L.T("标签准备失败：") + ex.Message; return; }
            var tags = idx.Tags(lib);
            status.Text = tags.Count == 0 ? L.T("还没有分析好的歌曲，稍后再来看看。") : "";
            var counts = tags.Values.SelectMany(v => v).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            foreach (var cat in new[] { AiLabels.Genre, AiLabels.Mood, AiLabels.Instrument, AiLabels.Vocal })
            {
                var items = AiLabels.Tags.Where(t => t.Category == cat && counts.ContainsKey(t.Id)).OrderByDescending(t => counts[t.Id]).ToList();
                if (items.Count == 0) continue;
                tagHost.Children.Add(new TextBlock { Text = AiLabels.CategoryName(cat), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = secondary });
                // 手动换行：按估计的宽度把按钮排成几行
                StackPanel? line = null; double used = 0;
                foreach (var t in items)
                {
                    string text = $"{L.T(t.Zh)}  {counts[t.Id]}";
                    double w = text.Sum(c => c > 0x2E80 ? 14 : 8) + 30;
                    if (line == null || used + w > 630) { line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; tagHost.Children.Add(line); used = 0; }
                    var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 4), FontSize = 13, Tag = t.Id };
                    b.Click += (s, e) =>
                    {
                        var h = idx.TracksWithTag(t.Id, lib);
                        status.Text = L.F("“{0}”：{1} 首", L.T(t.Zh), h.Count);
                        ShowHits(h, "");
                    };
                    line.Children.Add(b);
                    used += w + 6;
                }
            }
        }

        async void Search()
        {
            var q = box.Text.Trim();
            if (q.Length == 0) return;
            cts?.Cancel();
            var my = cts = new CancellationTokenSource();
            status.Text = L.T("正在搜索…（第一次需要加载模型，稍等几秒）");
            ShowHits(new(), "");
            lastQuery = q;
            try
            {
                if (mode == "sound")
                {
                    var (h, interp) = await idx.SearchBySoundAsync(q, lib, my.Token);
                    if (my.IsCancellationRequested) return;
                    status.Text = interp.Length > 0 ? L.F("理解为：{0}", interp) : "";
                    ShowHits(h, L.T("没有找到：曲库里的歌可能还没分析完"));
                }
                else
                {
                    var h = await idx.SearchLyricsAsync(q, lib, my.Token);
                    if (my.IsCancellationRequested) return;
                    status.Text = "";
                    ShowHits(h, L.T("没有找到：还没有索引过歌词的歌"));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!my.IsCancellationRequested) status.Text = L.T("搜索失败：") + ex.Message; }
        }

        go.Click += (s, e) => Search();
        box.KeyDown += (s, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Search(); } };
        modes.SelectionChanged += (s, e) => { if (s.SelectedItem?.Tag is string m) { mode = m; UpdateMode(); } };

        TrackItem? picked = null; double seek = -1;
        list.ItemClick += (s, e) =>
        {
            int i = list.Items.IndexOf(e.ClickedItem);
            if (i < 0 || i >= hits.Count) return;
            picked = hits[i].Track;
            seek = hits[i].Time;
            dlg.Hide();
        };

        dlg.SecondaryButtonClick += async (s, e) =>
        {
            // 存为歌单：名字默认用搜索词 / 标签
            e.Cancel = true;
            var defer = e.GetDeferral();
            try
            {
                var nameBox = new TextBox { Text = (mode == "tags" ? status.Text.Split('：')[0].Trim('“', '”') : lastQuery), MinWidth = 300 };
                var tip = new TextBlock { FontSize = 12, Foreground = secondary };
                var p = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = L.T("歌单名称") }, nameBox, tip } };
                var flyout = new Flyout { Content = p };
                var ok = new Button { Content = L.T("保存"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                p.Children.Add(ok);
                ok.Click += (_, __) =>
                {
                    var name = nameBox.Text.Trim();
                    if (name.Length == 0) return;
                    var libSvc = LibraryService.Instance;
                    if (!libSvc.CreatePlaylist(name)) { tip.Text = L.T("已有同名歌单，换个名字"); return; }
                    foreach (var h in hits) libSvc.AddToPlaylist(name, h.Track);
                    status.Text = L.F("已存为歌单“{0}”（{1} 首）", name, hits.Count);
                    flyout.Hide();
                };
                flyout.ShowAt(list);
            }
            finally { defer.Complete(); }
        };

        modes.SelectedItem = modes.Items[0];
        var result = await dlg.ShowAsync();
        cts?.Cancel();
        if (picked != null)
        {
            int i = hits.FindIndex(h => h.Track == picked);
            var queue = hits.Skip(Math.Max(0, i)).Select(h => h.Track).Concat(hits.Take(Math.Max(0, i)).Select(h => h.Track)).ToList();
            onPlay(picked, queue, seek);
        }
        else if (result == ContentDialogResult.Primary && hits.Count > 0)
        {
            onPlay(hits[0].Track, hits.Select(h => h.Track).ToList(), -1);
        }
    }

    private static FrameworkElement Row(AiIndexService.Hit h, Brush secondary)
    {
        var title = new TextBlock { Text = string.IsNullOrWhiteSpace(h.Track.Title) ? L.T("未命名曲目") : h.Track.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        var sub = new TextBlock { Text = h.Track.Artist, FontSize = 12, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis };
        var sp = new StackPanel { Spacing = 1, Padding = new Thickness(0, 4, 0, 4), Children = { title, sub } };
        if (h.Detail.Length > 0)
        {
            string time = h.Time >= 0 ? $"{(int)(h.Time / 60)}:{(int)(h.Time % 60):00}  " : "";
            sp.Children.Add(new TextBlock { Text = "♪ " + time + h.Detail, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.85 });
        }
        return sp;
    }
}
