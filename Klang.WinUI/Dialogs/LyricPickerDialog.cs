using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Klang_WinUI.Models;
using Klang_WinUI.Services;
using Klang_WinUI.Services.Netease;

namespace Klang_WinUI.Dialogs;

/// <summary>
/// “选择歌词”：把本地文件、内嵌歌词、网易云、LRCLIB 的候选都列出来，选中后右侧预览，确认后按曲目记住。
/// 也可以导入一个 .lrc 文件。只在用户主动打开并点“搜索”时联网。
/// </summary>
public static class LyricPickerDialog
{
    public enum Outcome { Cancel, Use, Reset }

    private sealed class Candidate
    {
        public required string Source { get; init; }
        public required string Title { get; init; }
        public string Detail { get; init; } = "";
        public required Func<CancellationToken, Task<LyricsService.LyricSet?>> Load { get; init; }
        public LyricsService.LyricSet? Set { get; set; }
        public bool Loaded { get; set; }
        public override string ToString() => Title;
    }

    private static Brush Secondary(XamlRoot root)
    {
        if (root.Content is FrameworkElement fe && ThemeService.Brush(fe, "TextFillColorSecondaryBrush") is { } b) return b;
        return (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    private static string Mmss(double sec) => sec <= 0 ? "--:--" : $"{(int)(sec / 60)}:{(int)(sec % 60):00}";

    private static string DurationNote(TrackItem track, double candidate)
    {
        if (candidate <= 0) return "";
        string s = Mmss(candidate);
        if (track.Duration <= 0) return s;
        double diff = Math.Abs(candidate - track.Duration);
        if (diff < 1.5) return s + L.T(" · 时长一致");
        return s + (diff > 8 ? L.F(" · 相差 {0} 秒（可能是别的版本）", (int)diff) : L.F(" · 相差 {0} 秒", (int)diff));
    }

    public static async Task<(Outcome Outcome, LyricsService.LyricSet? Set)> ShowAsync(XamlRoot root, TrackItem track, IntPtr ownerHwnd)
    {
        var secondary = Secondary(root);

        CancellationTokenSource? searchCts = null, previewCts = null;

        // ---- 界面 ----
        var intro = new TextBlock
        {
            Text = L.F("为《{0}》选择歌词。选定后会记住，以后播放这首歌都使用它。", track.Title),
            TextWrapping = TextWrapping.Wrap,
            Foreground = secondary,
            FontSize = 12
        };
        var keyword = new TextBox
        {
            Text = string.Join(" ", new[] { track.Title, track.Artist is "未知歌手" or "Unknown Artist" ? "" : track.Artist }.Where(s => !string.IsNullOrWhiteSpace(s))),
            PlaceholderText = L.T("歌名 歌手")
        };
        var searchBtn = new Button { Content = L.T("搜索") };
        var importBtn = new Button { Content = L.T("导入文件…") };
        var searchRow = new Grid { ColumnSpacing = 8 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.Children.Add(keyword);
        Grid.SetColumn(searchBtn, 1); searchRow.Children.Add(searchBtn);
        Grid.SetColumn(importBtn, 2); searchRow.Children.Add(importBtn);

        var status = new TextBlock { FontSize = 12, Foreground = secondary, TextWrapping = TextWrapping.Wrap };

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Width = 330, Height = 360 };
        int Count() => list.Items.Count;
        Candidate? Selected() => (list.SelectedItem as ListViewItem)?.Tag as Candidate;
        ListViewItem Add(Candidate c, int index = -1)
        {
            var row = new StackPanel { Padding = new Thickness(0, 6, 0, 6), Spacing = 1 };
            row.Children.Add(new TextBlock { Text = c.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(new TextBlock { Text = c.Detail, FontSize = 12, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis });
            var item = new ListViewItem { Content = row, Tag = c };
            ToolTipService.SetToolTip(item, c.Title + "\n" + c.Detail);
            if (index < 0) list.Items.Add(item); else list.Items.Insert(index, item);
            return item;
        }
        var preview = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 22, IsTextSelectionEnabled = true };
        var previewBox = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Child = new ScrollViewer { Content = preview, Height = 344 }
        };
        var body = new Grid { ColumnSpacing = 12 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(list);
        Grid.SetColumn(previewBox, 1); body.Children.Add(previewBox);

        var content = new StackPanel { Spacing = 10, Width = 780 };
        content.Children.Add(intro);
        content.Children.Add(searchRow);
        content.Children.Add(status);
        content.Children.Add(body);

        bool hasChosen = LyricsService.HasChosen(track.Id);
        var dlg = new ContentDialog
        {
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            RequestedTheme = ThemeService.Current,
            XamlRoot = root,
            Title = L.T("选择歌词"),
            Content = content,
            PrimaryButtonText = L.T("使用这份歌词"),
            SecondaryButtonText = hasChosen ? L.T("恢复自动匹配") : "",
            CloseButtonText = L.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        dlg.Resources["ContentDialogMaxWidth"] = 860.0;

        // ---- 预览 ----
        async void ShowPreview(Candidate? c)
        {
            previewCts?.Cancel();
            dlg.IsPrimaryButtonEnabled = false;
            if (c == null) { preview.Text = ""; return; }
            var cts = previewCts = new CancellationTokenSource();
            if (!c.Loaded)
            {
                preview.Text = L.T("正在读取歌词…");
                try { c.Set = await c.Load(cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { c.Set = null; preview.Text = L.T("读取失败：") + ex.Message; return; }
                c.Loaded = true;
                if (cts.IsCancellationRequested) return;
            }
            if (c.Set == null || !LyricsService.Instance.IsSynced(c.Set.Lrc))
            {
                preview.Text = c.Set == null ? L.T("这个来源没有歌词。") : L.T("这份歌词没有时间轴，无法跟随播放。");
                return;
            }
            var lines = LyricsService.Instance.ParseLrc(c.Set.Lrc, c.Set.Trans, c.Set.Roma);
            var sb = new StringBuilder();
            foreach (var line in lines.Take(80))
            {
                sb.Append(Mmss(line.Time).PadLeft(5)).Append("   ").Append(line.Text).Append('\n');
                if (!string.IsNullOrWhiteSpace(line.Sub)) sb.Append("          ").Append(line.Sub).Append('\n');
            }
            if (lines.Count > 80) sb.Append(L.F("…… 共 {0} 行", lines.Count));
            preview.Text = sb.ToString().TrimEnd();
            dlg.IsPrimaryButtonEnabled = true;
        }
        list.SelectionChanged += (s, e) => ShowPreview(Selected());

        // ---- 本地候选（不联网）----
        if (hasChosen)
            Add(new Candidate
            {
                Source = "chosen", Title = L.T("当前选定的歌词"), Detail = L.T("之前在这里选定，正在使用"),
                Load = _ => Task.FromResult(LyricsService.ReadChosen(track.Id))
            });
        if (!track.IsOnline && !track.IsCueTrack)
        {
            if (LyricsService.ReadLocalFile(track.FilePath) is { } lf)
                Add(new Candidate { Source = "file", Title = L.T("本地 .lrc 文件"), Detail = L.T("与音频同名，放在同一文件夹"), Load = _ => Task.FromResult<LyricsService.LyricSet?>(lf) });
            if (LyricsService.ReadEmbedded(track.FilePath) is { } em)
                Add(new Candidate { Source = "embedded", Title = L.T("音频文件内嵌的歌词"), Detail = L.T("写在音频文件的标签里"), Load = _ => Task.FromResult<LyricsService.LyricSet?>(em) });
            if (LrclibService.ReadCache(track.Id) is { } cached)
                Add(new Candidate { Source = "cache", Title = L.T("之前自动获取的歌词"), Detail = "LRCLIB", Load = _ => Task.FromResult<LyricsService.LyricSet?>(new LyricsService.LyricSet(cached)) });
        }
        int offlineCount = Count();

        // 当前自动使用的网易云版本（选中时才联网读取）
        string autoId = track.IsOnline ? track.NeteaseId : track.MetaNeteaseId;
        if (!string.IsNullOrEmpty(autoId))
            Add(new Candidate
            {
                Source = "netease-auto",
                Title = track.IsOnline ? L.T("网易云 · 这首歌的歌词") : L.T("网易云 · 自动匹配的版本"),
                Detail = track.IsOnline ? L.T("网易云提供的默认歌词") : L.T("自动补全信息时匹配到的歌曲"),
                Load = async c =>
                {
                    var b = await NeteaseService.Instance.GetLyricsAsync(autoId, c);
                    return string.IsNullOrWhiteSpace(b.Lrc) ? null : new LyricsService.LyricSet(b.Lrc, b.Trans, b.Roma);
                }
            });
        int localCount = Count();

        // ---- 在线搜索（网易云 + LRCLIB 并行）----
        async Task SearchAsync()
        {
            searchCts?.Cancel();
            var cts = searchCts = new CancellationTokenSource();
            var ct = cts.Token;
            while (Count() > localCount) list.Items.RemoveAt(Count() - 1);
            string kw = keyword.Text.Trim();
            if (kw.Length == 0) { status.Text = L.T("请输入歌名或歌手"); return; }
            status.Text = L.T("正在搜索网易云和 LRCLIB…");
            searchBtn.IsEnabled = false;

            var neTask = Task.Run(() => NeteaseService.Instance.SearchAsync(kw, 10), ct);
            var lrTask = LrclibService.SearchAsync(kw, ct);
            string errors = "";
            try
            {
                foreach (var s in await neTask)
                {
                    if (ct.IsCancellationRequested) return;
                    string id = s.NeteaseId;
                    Add(new Candidate
                    {
                        Source = "netease",
                        Title = $"{s.Title} — {s.Artist}",
                        Detail = L.T("网易云") + " · " + DurationNote(track, s.Duration) + (string.IsNullOrEmpty(s.Album) ? "" : " · " + s.Album),
                        Load = async c =>
                        {
                            var b = await NeteaseService.Instance.GetLyricsAsync(id, c);
                            return string.IsNullOrWhiteSpace(b.Lrc) ? null : new LyricsService.LyricSet(b.Lrc, b.Trans, b.Roma);
                        }
                    });
                }
            }
            catch (OperationCanceledException) { return; }
            catch { errors += L.T("网易云搜索失败。"); }
            try
            {
                foreach (var h in await lrTask)
                {
                    if (ct.IsCancellationRequested) return;
                    var set = new LyricsService.LyricSet(h.Synced);
                    Add(new Candidate
                    {
                        Source = "lrclib",
                        Title = $"{h.Title} — {h.Artist}",
                        Detail = "LRCLIB · " + DurationNote(track, h.Duration) + (string.IsNullOrEmpty(h.Album) ? "" : " · " + h.Album),
                        Load = _ => Task.FromResult<LyricsService.LyricSet?>(set)
                    });
                }
            }
            catch (OperationCanceledException) { return; }
            catch { errors += L.T("LRCLIB 搜索失败。"); }
            finally { searchBtn.IsEnabled = true; }

            if (ct.IsCancellationRequested) return;
            int online = Count() - localCount;
            status.Text = (online > 0 ? L.F("找到 {0} 个在线结果。选中一项即可在右侧预览。", online) : L.T("没有找到在线结果，可以换个关键词再试。")) + (errors.Length > 0 ? " " + errors : "");
            if (list.SelectedItem == null && Count() > 0) list.SelectedIndex = 0;
        }
        searchBtn.Click += async (s, e) => await SearchAsync();
        keyword.KeyDown += async (s, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await SearchAsync(); } };

        // ---- 导入文件 ----
        importBtn.Click += async (s, e) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
                picker.FileTypeFilter.Add(".lrc");
                picker.FileTypeFilter.Add(".txt");
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                var set = new LyricsService.LyricSet(CueParser.ReadText(file.Path));
                var c = new Candidate { Source = "import", Title = file.Name, Detail = L.T("导入的文件"), Load = _ => Task.FromResult<LyricsService.LyricSet?>(set) };
                var item = Add(c, 0);
                localCount++;
                list.SelectedItem = item;
            }
            catch (Exception ex)
            {
                status.Text = L.T("导入失败：") + ex.Message;
            }
        };

        status.Text = offlineCount > 0
            ? L.T("下面是本地已有的歌词。点“搜索”可以从网易云和 LRCLIB 查找更多版本。")
            : L.T("本地没有歌词。点“搜索”可以从网易云和 LRCLIB 查找，或者导入一个 .lrc 文件。");
        if (offlineCount > 0) list.SelectedIndex = 0;   // 只自动预览本地的，避免一打开就联网

        var result = await dlg.ShowAsync();
        searchCts?.Cancel();
        previewCts?.Cancel();
        if (result == ContentDialogResult.Secondary) return (Outcome.Reset, null);
        if (result == ContentDialogResult.Primary && Selected() is { Set: not null } chosen)
            return (Outcome.Use, chosen.Set);
        return (Outcome.Cancel, null);
    }
}
