using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT.Interop;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Controls;

/// <summary>列表中的分组标题行（按歌手 / 专辑 / 目录分组时插入）。</summary>
public sealed class GroupHeader
{
    public string Title { get; init; } = "";
    public int Count { get; init; }
    public string CountText => $"{Count} 首";
}

/// <summary>歌单列表中的一项。</summary>
public sealed class PlaylistEntry
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public string CountText => $"{Count} 首";
}

public sealed class TrackListTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TrackTemplate { get; set; }
    public DataTemplate? GroupHeaderTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is GroupHeader ? GroupHeaderTemplate : TrackTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}

public sealed partial class NavigationPane : UserControl
{
    /// <summary>当前列表中显示的曲目（按显示顺序，不含分组标题）。</summary>
    public ObservableCollection<TrackItem> DisplayTracks { get; } = new();

    /// <summary>实际驱动 ListView 的项（曲目 + 分组标题）。</summary>
    private readonly ObservableCollection<object> _listItems = new();
    private readonly ObservableCollection<PlaylistEntry> _playlistEntries = new();

    /// <summary>播放队列：用户点播时锁定为当时列表的快照，上一首/下一首在其中循环。</summary>
    public List<TrackItem> Queue { get; private set; } = new();

    private string _currentTab = "all";
    private string _currentSort = "default";
    private string _groupMode = "none";
    private string? _openPlaylist;
    private int _currentTabIndex = 0;
    private TrackItem? _currentTrack;

    public event EventHandler<TrackItem>? TrackSelected;
    public event EventHandler<TrackItem>? TrackRemoved;
    public event EventHandler<TrackItem>? FavoriteToggled;
    public event EventHandler? ViewStateChanged;

    public string SortMode => _currentSort;
    public string GroupMode => _groupMode;

    private static readonly Dictionary<string, string> SortLabels = new()
    {
        ["default"] = "默认排序", ["title"] = "按歌名", ["artist"] = "按歌手", ["album"] = "按专辑", ["duration"] = "按时长"
    };

    private static readonly Dictionary<string, string> GroupLabels = new()
    {
        ["none"] = "不分组", ["artist"] = "按歌手", ["album"] = "按专辑", ["folder"] = "按目录"
    };

    public NavigationPane()
    {
        InitializeComponent();
        TracksListView.ItemsSource = _listItems;
        PlaylistsListView.ItemsSource = _playlistEntries;
        UpdateEmptyState();

        UiAnimations.AttachFade(PlaylistsPanel);
        UiAnimations.AttachFade(CloudView);
        UiAnimations.AttachFade(EmptyStatePanel, 6f);
        UiAnimations.AttachFade(PlaylistHeader, 0f);

        CloudView.TrackInvoked += (list, track) =>
        {
            Queue = list;
            TrackSelected?.Invoke(this, track);
        };
        CloudView.CoverReady += t => CloudCoverReady?.Invoke(this, t);
    }

    /// <summary>云端曲目的封面缓存完成。</summary>
    public event EventHandler<TrackItem>? CloudCoverReady;

    private bool _cloudShown;

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_currentTab == "netease") _ = CloudView.SearchAsync(sender.Text);
    }

    #region 初始化 / 持久化

    /// <summary>应用保存的排序 / 分组方式（在加载曲目前调用）。</summary>
    public void ApplyViewState(string sortMode, string groupMode)
    {
        _currentSort = SortLabels.ContainsKey(sortMode) ? sortMode : "default";
        _groupMode = GroupLabels.ContainsKey(groupMode) ? groupMode : "none";
        SyncViewMenu();
    }

    /// <summary>让“排序 / 分组”菜单里的单选标记与当前状态一致。</summary>
    private void SyncViewMenu()
    {
        foreach (var sub in ViewOptionsFlyout.Items.OfType<MenuFlyoutSubItem>())
        {
            foreach (var radio in sub.Items.OfType<RadioMenuFlyoutItem>())
            {
                string cur = radio.GroupName == "LibSort" ? _currentSort : _groupMode;
                radio.IsChecked = (string?)radio.Tag == cur;
            }
        }
    }

    /// <summary>从曲库服务重新载入列表（启动恢复 / 添加 / 移除之后）。</summary>
    public void ReloadFromLibrary()
    {
        RefreshView();
        if (Queue.Count == 0) Queue = LibraryService.Instance.Tracks.ToList();
    }

    public void FocusSearch()
    {
        SearchBox.Focus(FocusState.Keyboard);
    }

    /// <summary>高亮并滚动到正在播放的曲目。</summary>
    public void SetCurrentTrack(TrackItem? track)
    {
        _currentTrack = track;
        CloudView.SetCurrentTrack(track);
        if (track != null && _listItems.Contains(track))
        {
            TracksListView.SelectedItem = track;
            TracksListView.ScrollIntoView(track);
        }
        else
        {
            TracksListView.SelectedItem = null;
        }
    }

    /// <summary>把队列设为当前显示的列表（键盘直接播放第一首时使用）。</summary>
    public void LockQueueToDisplay()
    {
        if (DisplayTracks.Count > 0) Queue = DisplayTracks.ToList();
    }

    public void RemoveFromQueue(TrackItem track) => Queue.Remove(track);

    /// <summary>把队列重置为整个曲库（曲库被设置页改动后使用）。</summary>
    public void ResetQueue() => Queue = LibraryService.Instance.Tracks.ToList();

    public string CurrentTab => _currentTab;

    #endregion

    #region 添加文件夹

    private async void AddFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTab == "netease") return;

        try
        {
            IntPtr hwnd = IntPtr.Zero;
            if (App.MainWindowInstance != null)
            {
                hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            }

            string? folder = FolderPickerHelper.PickFolder(hwnd);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                AddFolderBtn.IsEnabled = false;
                bool wasEmpty = LibraryService.Instance.Tracks.Count == 0;

                var added = await LibraryService.Instance.AddFolderAsync(folder);
                AddFolderBtn.IsEnabled = true;
                MetadataService.Instance.Enqueue(added);

                RefreshView();

                // 仅在曲库原本为空时自动开始播放第一首，避免打断正在播放的音乐
                if (wasEmpty && added.Count > 0 && DisplayTracks.Count > 0)
                {
                    Queue = DisplayTracks.ToList();
                    TrackSelected?.Invoke(this, DisplayTracks[0]);
                }
                else if (added.Count > 0)
                {
                    Queue = LibraryService.Instance.Tracks.ToList();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Pick folder error: {ex.Message}");
            AddFolderBtn.IsEnabled = true;
        }
    }

    #endregion

    #region 分页标签

    /// <summary>当前分页变化（全部 / 喜欢 / 歌单 / 云端）。</summary>
    public event EventHandler<string>? TabChanged;

    /// <summary>以代码切换分页（竖排图标栏使用）。</summary>
    public void SelectTab(string tag)
    {
        var item = TabSelector.Items.OfType<SelectorBarItem>().FirstOrDefault(i => (string?)i.Tag == tag);
        if (item != null) TabSelector.SelectedItem = item;
    }

    private void TabSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is not string tag || tag == _currentTab) return;

        _currentTab = tag;
        _currentTabIndex = tag switch
        {
            "fav" => 1,
            "playlists" => 2,
            "netease" => 3,
            _ => 0
        };
        if (tag != "playlists") _openPlaylist = null;

        RefreshView();
        UiAnimations.Pulse(TracksListView);
        TabChanged?.Invoke(this, tag);
    }

    #endregion

    #region 搜索 / 排序 / 分组

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        RefreshView();
    }

    private void SortItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string sortKey)
        {
            _currentSort = sortKey;
            SyncViewMenu();
            RefreshView();
            ViewStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void GroupItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string key)
        {
            _groupMode = key;
            SyncViewMenu();
            RefreshView();
            ViewStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    #endregion

    #region 视图刷新

    /// <summary>根据当前标签 / 歌单 / 搜索 / 排序 / 分组重建列表。</summary>
    public void RefreshView()
    {
        bool inPlaylistList = _currentTab == "playlists" && _openPlaylist == null;
        bool inNetease = _currentTab == "netease";

        if (!inNetease)
        {
            CloudView.Visibility = Visibility.Collapsed;
            if (_cloudShown)
            {
                _cloudShown = false;
                CloudView.OnHidden();
            }
        }

        PlaylistHeader.Visibility = (_currentTab == "playlists" && _openPlaylist != null) ? Visibility.Visible : Visibility.Collapsed;
        PlaylistHeaderText.Text = _openPlaylist ?? "";
        PlaylistsPanel.Visibility = inPlaylistList ? Visibility.Visible : Visibility.Collapsed;
        TracksListView.Visibility = (inPlaylistList || inNetease) ? Visibility.Collapsed : Visibility.Visible;
        AddFolderBtn.Visibility = inNetease ? Visibility.Collapsed : Visibility.Visible;

        var lib = LibraryService.Instance;

        if (inPlaylistList)
        {
            _playlistEntries.Clear();
            foreach (var name in lib.PlaylistNames.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
            {
                _playlistEntries.Add(new PlaylistEntry { Name = name, Count = lib.PlaylistCount(name) });
            }
            DisplayTracks.Clear();
            _listItems.Clear();
            TrackCountText.Text = $"共 {_playlistEntries.Count} 个歌单";
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (inNetease)
        {
            DisplayTracks.Clear();
            _listItems.Clear();
            TrackCountText.Text = "云端";
            EmptyIcon.Glyph = "";
            EmptyImportBtn.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            CloudView.Visibility = Visibility.Visible;
            if (!_cloudShown)
            {
                _cloudShown = true;
                CloudView.OnShown();
            }
            return;
        }

        IEnumerable<TrackItem> source = (_currentTab == "playlists" && _openPlaylist != null)
            ? lib.GetPlaylistTracks(_openPlaylist)
            : lib.Tracks;

        if (_currentTab == "fav")
        {
            source = source.Where(t => t.IsFavorite);
        }

        var query = SearchBox.Text.Trim();
        if (query.Length > 0)
        {
            source = source.Where(t =>
                t.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                t.Artist.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                t.Album.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        // 歌单内默认保持添加顺序；其余按曲库顺序
        bool playlistOrder = _currentTab == "playlists" && _openPlaylist != null;
        IEnumerable<TrackItem> sorted = _currentSort switch
        {
            "title" => source.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            "artist" => source.OrderBy(t => t.Artist, StringComparer.CurrentCultureIgnoreCase),
            "album" => source.OrderBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase),
            "duration" => source.OrderByDescending(t => t.Duration),
            _ => playlistOrder ? source : source.OrderBy(t => t.Index)
        };

        var ordered = sorted.ToList();
        var items = new List<object>();
        var tracksInOrder = new List<TrackItem>();

        if (_groupMode == "none")
        {
            items.AddRange(ordered);
            tracksInOrder.AddRange(ordered);
        }
        else
        {
            Func<TrackItem, string> keyOf = _groupMode switch
            {
                "artist" => t => t.Artist,
                "album" => t => t.Album,
                _ => t => t.FolderName
            };
            Func<string, string> titleOf = _groupMode == "folder"
                ? k => { var n = Path.GetFileName(k.TrimEnd('\\', '/')); return string.IsNullOrEmpty(n) ? k : n; }
                : k => k;

            foreach (var g in ordered.GroupBy(keyOf).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var members = g.ToList();
                items.Add(new GroupHeader { Title = titleOf(g.Key), Count = members.Count });
                items.AddRange(members);
                tracksInOrder.AddRange(members);
            }
        }

        DisplayTracks.Clear();
        foreach (var t in tracksInOrder) DisplayTracks.Add(t);
        _listItems.Clear();
        foreach (var it in items) _listItems.Add(it);

        UpdateEmptyState();

        if (_currentTrack != null && _listItems.Contains(_currentTrack))
        {
            TracksListView.SelectedItem = _currentTrack;
        }
    }

    private void UpdateEmptyState()
    {
        bool isEmpty = DisplayTracks.Count == 0;
        TrackCountText.Text = $"共 {DisplayTracks.Count} 首";
        EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        TracksListView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

        EmptyIcon.Glyph = "";
        if (SearchBox.Text.Trim().Length > 0)
        {
            EmptyText.Text = "没有找到匹配的歌曲";
            EmptyImportBtn.Visibility = Visibility.Collapsed;
        }
        else if (_currentTab == "fav")
        {
            EmptyText.Text = "还没有喜欢的歌曲\n点击播放栏的爱心即可收藏";
            EmptyImportBtn.Visibility = Visibility.Collapsed;
        }
        else if (_currentTab == "playlists" && _openPlaylist != null)
        {
            EmptyText.Text = "歌单是空的\n在曲目上右键选择“添加到歌单”";
            EmptyImportBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmptyText.Text = "曲库暂无音乐";
            EmptyImportBtn.Visibility = Visibility.Visible;
        }
    }

    #endregion

    #region 曲目点击

    private void TracksListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackItem track)
        {
            Queue = DisplayTracks.ToList();
            TrackSelected?.Invoke(this, track);
        }
    }

    private void TracksListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TracksListView.SelectedItem is TrackItem track)
        {
            Queue = DisplayTracks.ToList();
            TrackSelected?.Invoke(this, track);
        }
    }

    #endregion

    #region 曲目右键菜单

    private void TrackItem_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TrackItem track)
        {
            var flyout = BuildTrackMenu(track);
            ShowFlyout(flyout, fe, args);
            args.Handled = true;
        }
    }

    private static void ShowFlyout(MenuFlyout flyout, FrameworkElement anchor, ContextRequestedEventArgs args)
    {
        if (args.TryGetPosition(anchor, out var pt))
            flyout.ShowAt(anchor, new FlyoutShowOptions { Position = pt });
        else
            flyout.ShowAt(anchor);
    }

    private MenuFlyout BuildTrackMenu(TrackItem track)
    {
        var flyout = new MenuFlyout();

        var play = new MenuFlyoutItem { Text = "播放", Icon = new FontIcon { Glyph = "" } };
        play.Click += (s, e) =>
        {
            Queue = DisplayTracks.ToList();
            TrackSelected?.Invoke(this, track);
        };
        flyout.Items.Add(play);

        var fav = new MenuFlyoutItem
        {
            Text = track.IsFavorite ? "取消喜欢" : "喜欢",
            Icon = new FontIcon { Glyph = track.IsFavorite ? "" : "" }
        };
        fav.Click += (s, e) => FavoriteToggled?.Invoke(this, track);
        flyout.Items.Add(fav);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var addSub = new MenuFlyoutSubItem { Text = "添加到歌单", Icon = new FontIcon { Glyph = "" } };
        foreach (var name in LibraryService.Instance.PlaylistNames.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new MenuFlyoutItem { Text = name };
            var captured = name;
            item.Click += (s, e) =>
            {
                LibraryService.Instance.AddToPlaylist(captured, track);
            };
            addSub.Items.Add(item);
        }
        if (addSub.Items.Count > 0) addSub.Items.Add(new MenuFlyoutSeparator());
        var newPl = new MenuFlyoutItem { Text = "新建歌单…" };
        newPl.Click += async (s, e) =>
        {
            var name = await PromptTextAsync("新建歌单", "歌单名称", "");
            if (name != null && LibraryService.Instance.CreatePlaylist(name))
            {
                LibraryService.Instance.AddToPlaylist(name.Trim(), track);
            }
        };
        addSub.Items.Add(newPl);
        flyout.Items.Add(addSub);

        if (_currentTab == "playlists" && _openPlaylist != null)
        {
            var playlistName = _openPlaylist;
            var rm = new MenuFlyoutItem { Text = "从歌单移除", Icon = new FontIcon { Glyph = "" } };
            rm.Click += (s, e) =>
            {
                LibraryService.Instance.RemoveFromPlaylist(playlistName, track);
                RefreshView();
            };
            flyout.Items.Add(rm);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var del = new MenuFlyoutItem { Text = "从曲库移除", Icon = new FontIcon { Glyph = "" } };
        del.Click += (s, e) =>
        {
            LibraryService.Instance.RemoveTrack(track);
            Queue.Remove(track);
            TrackRemoved?.Invoke(this, track);
            RefreshView();
        };
        flyout.Items.Add(del);

        return flyout;
    }

    #endregion

    #region 歌单管理

    private void PlaylistsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistEntry entry)
        {
            _openPlaylist = entry.Name;
            RefreshView();
            UiAnimations.Pulse(TracksListView);
        }
    }

    private void PlaylistBackBtn_Click(object sender, RoutedEventArgs e)
    {
        _openPlaylist = null;
        RefreshView();
        UiAnimations.Pulse(PlaylistsPanel);
    }

    private async void NewPlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptTextAsync("新建歌单", "歌单名称", "");
        if (name == null) return;
        if (!LibraryService.Instance.CreatePlaylist(name))
        {
            await ShowMessageAsync("无法创建歌单", "名称不能为空，也不能与已有歌单重名。");
            return;
        }
        RefreshView();
    }

    private void PlaylistItem_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PlaylistEntry entry)
        {
            var flyout = new MenuFlyout();

            var open = new MenuFlyoutItem { Text = "打开", Icon = new FontIcon { Glyph = "" } };
            open.Click += (s, e) =>
            {
                _openPlaylist = entry.Name;
                RefreshView();
            };
            flyout.Items.Add(open);

            var rename = new MenuFlyoutItem { Text = "重命名", Icon = new FontIcon { Glyph = "" } };
            rename.Click += async (s, e) =>
            {
                var newName = await PromptTextAsync("重命名歌单", "歌单名称", entry.Name);
                if (newName == null) return;
                if (!LibraryService.Instance.RenamePlaylist(entry.Name, newName))
                {
                    await ShowMessageAsync("无法重命名", "名称不能为空，也不能与已有歌单重名。");
                    return;
                }
                RefreshView();
            };
            flyout.Items.Add(rename);

            var delete = new MenuFlyoutItem { Text = "删除歌单", Icon = new FontIcon { Glyph = "" } };
            delete.Click += (s, e) =>
            {
                LibraryService.Instance.DeletePlaylist(entry.Name);
                RefreshView();
            };
            flyout.Items.Add(delete);

            ShowFlyout(flyout, fe, args);
            args.Handled = true;
        }
    }

    private async Task<string?> PromptTextAsync(string title, string placeholder, string initial)
    {
        if (XamlRoot == null) return null;

        var box = new TextBox { PlaceholderText = placeholder, Text = initial, MaxLength = 60, MinWidth = 280 };
        box.SelectAll();
        var dlg = new ContentDialog
        {
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        var text = box.Text.Trim();
        return text.Length == 0 ? null : text;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (XamlRoot == null) return;
        var dlg = new ContentDialog
        {
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "好的"
        };
        await dlg.ShowAsync();
    }

    #endregion
}
