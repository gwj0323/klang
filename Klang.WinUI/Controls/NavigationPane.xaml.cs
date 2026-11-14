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
    public string CountText => L.F("{0} 首", Count);
}

/// <summary>歌单列表中的一项。</summary>
public sealed class PlaylistEntry
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public bool IsSmart { get; init; }
    public bool IsBuiltIn { get; init; }
    public string Glyph => !IsSmart ? "\uE8FD" : SmartGlyph(Name);

    // 内置智能歌单各用自己的图标（语言切换后名称会变，所以每次现查）
    private static string SmartGlyph(string name) =>
        name == L.T("最近添加") ? "\uE787" :
        name == L.T("最近播放") ? "\uE81C" :
        name == L.T("常听") ? "\uE8EE" :
        name == L.T("情绪 · 安静") ? "\uE708" :
        name == L.T("情绪 · 专注") ? "\uE7F6" :
        name == L.T("情绪 · 动感") ? "\uE945" :
        name == L.T("情绪 · 明亮") ? "\uE706" :
        name == L.T("情绪 · 沉稳") ? "\uE9D9" : "\uE9D9";
    public string CountText => L.F("{0} 首", Count);
}

/// <summary>专辑墙里的一张专辑。</summary>
public sealed class AlbumEntry
{
    public string Name { get; init; } = "";
    public string Artist { get; init; } = "";
    public int Count { get; init; }
    public Microsoft.UI.Xaml.Media.ImageSource? Cover { get; init; }
    public string Subtitle => string.IsNullOrEmpty(Artist) ? L.F("{0} 首", Count) : L.F("{0} · {1} 首", Artist, Count);
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
    private readonly ObservableCollection<AlbumEntry> _albumEntries = new();

    /// <summary>播放队列：用户点播时锁定为当时列表的快照，上一首/下一首在其中循环。</summary>
    public List<TrackItem> Queue { get; private set; } = new();

    /// <summary>以给定曲目作为播放队列，从第一首开始播放（画廊点专辑时使用）。</summary>
    public void PlayQueue(List<TrackItem> list)
    {
        if (list.Count == 0) return;
        Queue = list;
        TrackSelected?.Invoke(this, list[0]);
    }

    private string _currentTab = "all";
    private string _currentSort = "default";
    private string _groupMode = "none";
    private string? _openPlaylist;
    private bool _openIsSmart;
    private string? _openAlbum;
    private int _currentTabIndex = 0;

    // 筛选条件（只作用于本地曲库列表）
    private int _qualityFilter;            // 0 全部，1 无损及以上，2 仅 Hi-Res，-1 仅有损
    private bool _fNoCover, _fNoAlbum, _fNoLyrics, _fMissingFile;
    private bool _fFake;
    private bool FilterActive => _qualityFilter != 0 || _fNoCover || _fNoAlbum || _fNoLyrics || _fMissingFile || _fFake;

    /// <summary>有筛选条件在起作用（分析结果出来后，只有此时才需要重建列表）。</summary>
    public bool HasActiveFilter => FilterActive;
    private TrackItem? _currentTrack;

    public event EventHandler<TrackItem>? TrackSelected;
    public event EventHandler<TrackItem>? TrackRemoved;
    public event EventHandler<TrackItem>? FavoriteToggled;
    public event EventHandler? ViewStateChanged;

    public string SortMode => _currentSort;
    public string GroupMode => _groupMode;

    private static readonly Dictionary<string, string> SortLabels = new()
    {
        ["default"] = L.T("默认排序"), ["title"] = L.T("按歌名"), ["artist"] = L.T("按歌手"), ["album"] = L.T("按专辑"), ["duration"] = L.T("按时长"),
        ["added"] = L.T("最近添加"), ["played"] = L.T("最近播放"), ["plays"] = L.T("播放最多")
    };

    private static readonly Dictionary<string, string> GroupLabels = new()
    {
        ["none"] = L.T("不分组"), ["artist"] = L.T("按歌手"), ["album"] = L.T("按专辑"), ["folder"] = L.T("按目录")
    };

    public NavigationPane()
    {
        InitializeComponent();
        TracksListView.ItemsSource = _listItems;
        TracksListView.ContainerContentChanging += TracksListView_ContainerContentChanging;
        PlaylistsListView.ItemsSource = _playlistEntries;
        AlbumsGridView.ItemsSource = _albumEntries;
        UpdateEmptyState();

        LibraryService.Instance.ScanProgress += OnScanProgress;
        LibraryService.Instance.TracksMerged += OnTracksMerged;

        // 切换语言后重建列表区的文字（“共 N 首”、空状态提示这些是代码拼出来的）
        L.LanguageChanged += () => DispatcherQueue.SafeEnqueue(RefreshView);

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
                bool wasEmpty = LibraryService.Instance.Tracks.Count == 0;
                if (LibraryService.Instance.IsScanning)
                    TrackCountText.Text = L.T("等待当前扫描完成…");

                var added = await LibraryService.Instance.AddFolderAsync(folder);
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
            "albums" => 2,
            "playlists" => 3,
            "netease" => 4,
            _ => 0
        };
        if (tag != "playlists") { _openPlaylist = null; _openIsSmart = false; }
        if (tag != "albums") _openAlbum = null;

        BeginStagger();

        RefreshView();
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

        bool inAlbumList = _currentTab == "albums" && _openAlbum == null;
        bool inPlaylistOpen = _currentTab == "playlists" && _openPlaylist != null;
        bool inAlbumOpen = _currentTab == "albums" && _openAlbum != null;

        PlaylistHeader.Visibility = (inPlaylistOpen || inAlbumOpen) ? Visibility.Visible : Visibility.Collapsed;
        PlaylistHeaderText.Text = inAlbumOpen ? _openAlbum! : (_openPlaylist ?? "");
        PlaylistsPanel.Visibility = inPlaylistList ? Visibility.Visible : Visibility.Collapsed;
        AlbumsGridView.Visibility = inAlbumList ? Visibility.Visible : Visibility.Collapsed;
        TracksListView.Visibility = (inPlaylistList || inAlbumList || inNetease) ? Visibility.Collapsed : Visibility.Visible;
        AddFolderBtn.Visibility = inNetease ? Visibility.Collapsed : Visibility.Visible;

        var lib = LibraryService.Instance;

        if (inPlaylistList)
        {
            _playlistEntries.Clear();
            // 智能歌单排在前面：内置的（最近添加 / 最近播放 / 常听）+ 自己建的
            foreach (var s in lib.AllSmartPlaylists)
            {
                _playlistEntries.Add(new PlaylistEntry
                {
                    Name = s.Name, Count = lib.GetSmartTracks(s).Count, IsSmart = true, IsBuiltIn = lib.IsBuiltInSmart(s.Name)
                });
            }
            foreach (var name in lib.PlaylistNames.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
            {
                _playlistEntries.Add(new PlaylistEntry { Name = name, Count = lib.PlaylistCount(name) });
            }
            DisplayTracks.Clear();
            _listItems.Clear();
            TrackCountText.Text = L.F("共 {0} 个歌单", _playlistEntries.Count);
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (inAlbumList)
        {
            _albumEntries.Clear();
            var q = SearchBox.Text.Trim();
            foreach (var g in lib.Tracks.GroupBy(t => t.Album).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                if (q.Length > 0 && !g.Key.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    && !g.Any(t => t.Artist.Contains(q, StringComparison.CurrentCultureIgnoreCase))) continue;
                var members = g.ToList();
                var withCover = members.FirstOrDefault(t => !string.IsNullOrEmpty(t.CoverPath));
                var artists = members.Select(t => t.Artist).Distinct().ToList();
                _albumEntries.Add(new AlbumEntry
                {
                    Name = g.Key,
                    Artist = artists.Count == 1 ? artists[0] : L.T("群星"),
                    Count = members.Count,
                    Cover = withCover?.CoverThumb
                });
            }
            DisplayTracks.Clear();
            _listItems.Clear();
            TrackCountText.Text = L.F("共 {0} 张专辑", _albumEntries.Count);
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (inNetease)
        {
            DisplayTracks.Clear();
            _listItems.Clear();
            TrackCountText.Text = L.T("云端");
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

        IEnumerable<TrackItem> source;
        if (inPlaylistOpen)
        {
            var smart = _openIsSmart ? lib.FindSmart(_openPlaylist!) : null;
            source = smart != null ? lib.GetSmartTracks(smart) : lib.GetPlaylistTracks(_openPlaylist!);
        }
        else if (inAlbumOpen)
        {
            source = lib.Tracks.Where(t => t.Album == _openAlbum);
        }
        else source = lib.Tracks;

        if (_currentTab == "fav")
        {
            source = source.Where(t => t.IsFavorite);
        }

        if (FilterActive)
        {
            source = source.Where(t =>
                (_qualityFilter == 0
                    || (_qualityFilter == -1 && t.QualityTier == 0)
                    || (_qualityFilter > 0 && t.QualityTier >= _qualityFilter))
                && (!_fNoCover || MetadataService.NeedsCover(t))
                && (!_fNoAlbum || t.Album == "未知专辑" || t.Artist == "未知歌手")
                && (!_fNoLyrics || (!t.IsOnline && !File.Exists(Path.ChangeExtension(t.FilePath, ".lrc")) && !LrclibService.HasCache(t.Id) && !LyricsService.HasChosen(t.Id)))
                && (!_fMissingFile || t.IsMissing)
                && (!_fFake || t.IsSuspectFake));
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
        bool playlistOrder = inPlaylistOpen;
        IEnumerable<TrackItem> sorted = _currentSort switch
        {
            "title" => source.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            "artist" => source.OrderBy(t => t.Artist, StringComparer.CurrentCultureIgnoreCase),
            "album" => source.OrderBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase),
            "duration" => source.OrderByDescending(t => t.Duration),
            "added" => source.OrderByDescending(t => t.AddedTimestamp),
            "played" => source.OrderByDescending(t => t.LastPlayedTimestamp),
            "plays" => source.OrderByDescending(t => t.PlayCount),
            _ => playlistOrder ? source : source.OrderBy(t => t.Index)
        };

        // 只有“自己建的歌单 + 默认顺序 + 没搜索 / 筛选 / 分组”时允许拖拽调整顺序
        bool canReorder = inPlaylistOpen && !_openIsSmart && _currentSort == "default" && _groupMode == "none"
            && query.Length == 0 && !FilterActive;
        TracksListView.CanReorderItems = canReorder;
        TracksListView.AllowDrop = canReorder;

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
        TrackCountText.Text = FilterActive ? L.F("共 {0} 首 · 已筛选", DisplayTracks.Count) : L.F("共 {0} 首", DisplayTracks.Count);
        ViewOptionsText.Text = FilterActive ? L.T("整理 ●") : L.T("整理");
        EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        TracksListView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

        EmptyIcon.Glyph = "";
        if (FilterActive)
        {
            EmptyText.Text = L.T("无符合条件的歌曲\n可在“整理 → 筛选”中清除筛选条件");
            EmptyImportBtn.Visibility = Visibility.Collapsed;
        }
        else if (SearchBox.Text.Trim().Length > 0)
        {
            EmptyText.Text = L.T("未找到匹配的歌曲");
            EmptyImportBtn.Visibility = Visibility.Collapsed;
        }
        else if (_currentTab == "fav")
        {
            EmptyText.Text = L.T("暂无喜欢的歌曲\n点击播放栏的心形图标即可添加");
            EmptyImportBtn.Visibility = Visibility.Collapsed;
        }
        else if (_currentTab == "playlists" && _openPlaylist != null)
        {
            EmptyText.Text = L.T("歌单为空\n在歌曲上右键，选择“添加到歌单”");
            EmptyImportBtn.Visibility = Visibility.Collapsed;
        }
        else if (LibraryService.Instance.IsScanning)
        {
            EmptyText.Text = L.T("正在扫描音乐…\n找到的歌曲会陆续出现在这里");
            EmptyImportBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmptyText.Text = L.T("曲库暂无音乐");
            EmptyImportBtn.Visibility = Visibility.Visible;
        }
        if (_scan != null) ShowScanStatus(_scan);
    }

    #region 扫描进度

    private LibraryService.ScanStatus? _scan;
    private DateTime _lastScanRefresh;

    private void OnScanProgress(LibraryService.ScanStatus st)
    {
        if (st.Finished)
        {
            _scan = null;
            ScanBar.Visibility = Visibility.Collapsed;
            AddFolderBtn.IsEnabled = true;
            RefreshView();   // 恢复“共 N 首”和空状态
            return;
        }
        bool first = _scan == null;
        _scan = st;
        ScanBar.Visibility = Visibility.Visible;
        AddFolderBtn.IsEnabled = false;
        if (first) UpdateEmptyState();
        ShowScanStatus(st);
    }

    private void ShowScanStatus(LibraryService.ScanStatus st)
    {
        if (st.Total <= 0)
        {
            ScanBar.IsIndeterminate = true;
            TrackCountText.Text = L.T("正在查找音乐文件…");
        }
        else
        {
            ScanBar.IsIndeterminate = false;
            ScanBar.Maximum = st.Total;
            ScanBar.Value = st.Done;
            // 文字要短：工具栏一行还放着“添加”和“整理”
            TrackCountText.Text = L.F("扫描中 {0}/{1}", st.Done, st.Total);
        }
    }

    /// <summary>扫描中途并入的歌曲：每 2 秒刷新一次列表，让用户看到进展（每批都刷新会拖慢界面）。</summary>
    private void OnTracksMerged(List<TrackItem> batch)
    {
        if (_currentTab is "netease" or "playlists" or "albums") return;
        if ((DateTime.UtcNow - _lastScanRefresh).TotalSeconds < 2 && DisplayTracks.Count > 0) return;
        _lastScanRefresh = DateTime.UtcNow;
        RefreshView();
    }

    #endregion

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

    // 悬停时封面上浮出播放图标：行 Grid → 第 2 个子元素（封面 Border）→ Grid → 最后一个子元素（HoverPlay）
    private static UIElement? HoverPlayOf(object sender)
    {
        if (sender is Grid row && row.Children.Count > 0 && row.Children[0] is Border b && b.Child is Grid g && g.Children.Count > 0)
            return g.Children[g.Children.Count - 1];
        return null;
    }

    private void TrackRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (HoverPlayOf(sender) is UIElement el) { el.Opacity = 1; UiAnimations.FadeIn(el, 160); }
    }

    private void TrackRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (HoverPlayOf(sender) is UIElement el) UiAnimations.OpacityTo(el, 0f, 140);
        if (sender is FrameworkElement row) UiAnimations.ScaleTo(row, 1f);
    }

    // 按下时整行轻微缩小，松开恢复
    private void TrackRow_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement row && e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            UiAnimations.ScaleTo(row, 0.975f, 90);
    }

    private void TrackRow_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement row) UiAnimations.ScaleTo(row, 1f, 220);
    }

    private void TrackThumb_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement img) UiAnimations.FadeIn(img, 200);
    }

    /// <summary>
    /// 为“列表缩略图飞到主界面封面”的转场做准备：给这一行的缩略图拍快照。
    /// 这一行不在屏幕上（被虚拟化掉）或者这首歌没有封面时返回 false，调用方就按普通方式切换。
    /// </summary>
    public bool PrepareCoverAnimation(TrackItem track, string key)
    {
        try
        {
            if (TracksListView.Visibility != Visibility.Visible || string.IsNullOrEmpty(track.CoverPath)) return false;
            if (TracksListView.ContainerFromItem(track) is not ListViewItem item) return false;
            if (item.ContentTemplateRoot is not Grid row || row.Children.Count == 0 || row.Children[0] is not Border thumb) return false;
            // 只拍缩略图本身：整块封面区域上还盖着悬停时的深色播放图标和“正在播放”频谱，不能带进快照
            if (thumb.Child is not Grid g) return false;
            var img = g.Children.OfType<Image>().FirstOrDefault();
            if (img == null || img.ActualWidth <= 0) return false;
            Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView().PrepareToAnimate(key, img);
            return true;
        }
        catch { return false; }
    }

    // ---- 列表项依次淡入：切换标签 / 打开歌单后的一小段时间内，新出现的行按顺序错开入场 ----
    private long _staggerUntilTick;
    private int _staggerIndex;

    private void BeginStagger()
    {
        _staggerUntilTick = Environment.TickCount64 + 350;
        _staggerIndex = 0;
    }

    private void TracksListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Phase != 0) return;
        if (Environment.TickCount64 > _staggerUntilTick) return;
        int i = Math.Min(_staggerIndex++, 14);
        UiAnimations.StaggerIn(args.ItemContainer, i * 28);
    }

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

        var play = new MenuFlyoutItem { Text = L.T("播放"), Icon = new FontIcon { Glyph = "" } };
        play.Click += (s, e) =>
        {
            Queue = DisplayTracks.ToList();
            TrackSelected?.Invoke(this, track);
        };
        play.IsEnabled = !track.IsMissing;
        flyout.Items.Add(play);

        var playNext = new MenuFlyoutItem { Text = L.T("下一首播放"), Icon = new FontIcon { Glyph = "" } };
        playNext.IsEnabled = !track.IsMissing;
        playNext.Click += (s, e) => PlayNextInQueue(track);
        flyout.Items.Add(playNext);

        var similar = new MenuFlyoutItem { Text = L.T("播放相似歌曲"), Icon = new FontIcon { Glyph = "" } };
        similar.IsEnabled = !track.IsMissing && track.AudioFeat != null;
        ToolTipService.SetToolTip(similar, track.AudioFeat != null ? L.T("从当前歌曲开始，依次播放声学特征最接近的歌曲") : L.T("该歌曲尚未完成声学分析"));
        similar.Click += (s, e) => PlaySimilar(track);
        flyout.Items.Add(similar);

        var addQueue = new MenuFlyoutItem { Text = L.T("加入播放队列"), Icon = new FontIcon { Glyph = "" } };
        addQueue.IsEnabled = !track.IsMissing;
        addQueue.Click += (s, e) => AddToQueueEnd(track);
        flyout.Items.Add(addQueue);

        var fav = new MenuFlyoutItem
        {
            Text = track.IsFavorite ? L.T("取消喜欢") : L.T("加入喜欢"),
            Icon = new FontIcon { Glyph = track.IsFavorite ? "" : "" }
        };
        fav.Click += (s, e) => FavoriteToggled?.Invoke(this, track);
        flyout.Items.Add(fav);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var addSub = new MenuFlyoutSubItem { Text = L.T("添加到歌单"), Icon = new FontIcon { Glyph = "" } };
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
        var newPl = new MenuFlyoutItem { Text = L.T("新建歌单…") };
        newPl.Click += async (s, e) =>
        {
            var name = await PromptTextAsync(L.T("新建歌单"), L.T("歌单名称"), "");
            if (name != null && LibraryService.Instance.CreatePlaylist(name))
            {
                LibraryService.Instance.AddToPlaylist(name.Trim(), track);
            }
        };
        addSub.Items.Add(newPl);
        flyout.Items.Add(addSub);

        if (_currentTab == "playlists" && _openPlaylist != null && !_openIsSmart)
        {
            var playlistName = _openPlaylist;
            var rm = new MenuFlyoutItem { Text = L.T("从歌单移除"), Icon = new FontIcon { Glyph = "" } };
            rm.Click += (s, e) =>
            {
                LibraryService.Instance.RemoveFromPlaylist(playlistName, track);
                RefreshView();
            };
            flyout.Items.Add(rm);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var editTag = new MenuFlyoutItem { Text = L.T("编辑标签…"), Icon = new FontIcon { Glyph = "" } };
        editTag.Click += async (s, e) => await EditTrackTagsAsync(track);
        flyout.Items.Add(editTag);

        var analyze = new MenuFlyoutItem { Text = L.T("音质分析…"), Icon = new FontIcon { Glyph = "" } };
        analyze.IsEnabled = !track.IsOnline && !track.IsCueTrack && !track.IsMissing;
        analyze.Click += async (s, e) => await Dialogs.QualityDialog.ShowAsync(XamlRoot, track);
        flyout.Items.Add(analyze);

        var del = new MenuFlyoutItem { Text = L.T("从曲库移除"), Icon = new FontIcon { Glyph = "" } };
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

    #region 标签编辑

    private async Task EditTrackTagsAsync(TrackItem track)
    {
        if (track.IsOnline) { await ShowMessageAsync(L.T("云端曲目不支持编辑标签"), L.T("仅支持编辑本地歌曲。")); return; }
        int sameAlbum = LibraryService.Instance.Tracks.Count(t => t.Album == track.Album && !ReferenceEquals(t, track));
        var r = await Dialogs.LibraryDialogs.ShowTagEditAsync(XamlRoot, track, sameAlbum);
        if (r == null) return;

        var lib = LibraryService.Instance;
        var err = lib.UpdateTags(track, r.Value.Title.Trim(), r.Value.Artist.Trim(), r.Value.Album.Trim());
        var failed = new List<string>();
        if (err != null) failed.Add(err);
        else if (r.Value.ApplyToAlbum && sameAlbum > 0)
        {
            string oldAlbum = r.Value.OldAlbum;
            foreach (var t in lib.Tracks.Where(t => t.Album == oldAlbum && !ReferenceEquals(t, track)).ToList())
            {
                var e2 = lib.UpdateTags(t, null, r.Value.Artist.Trim(), r.Value.Album.Trim());
                if (e2 != null) failed.Add($"{t.Title}：{e2.Split('\n')[0]}");
            }
        }
        lib.SaveLibrary();
        RefreshView();
        if (failed.Count > 0) await ShowMessageAsync(L.T("写入标签时出现问题"), string.Join("\n", failed.Take(6)));
    }

    #endregion

    #region 筛选 / 曲库工具

    private void QualityFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && int.TryParse(item.Tag as string, out int v))
        {
            _qualityFilter = v;
            RefreshView();
        }
    }

    private void MissingFilter_Click(object sender, RoutedEventArgs e)
    {
        _fNoCover = FilterNoCover.IsChecked;
        _fNoAlbum = FilterNoAlbum.IsChecked;
        _fNoLyrics = FilterNoLyrics.IsChecked;
        _fMissingFile = FilterMissingFile.IsChecked;
        _fFake = FilterFake.IsChecked;
        RefreshView();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _qualityFilter = 0;
        _fNoCover = _fNoAlbum = _fNoLyrics = _fMissingFile = _fFake = false;
        FilterNoCover.IsChecked = FilterNoAlbum.IsChecked = FilterNoLyrics.IsChecked = FilterMissingFile.IsChecked = FilterFake.IsChecked = false;
        foreach (var r in FilterSubItem.Items.OfType<RadioMenuFlyoutItem>()) r.IsChecked = (string?)r.Tag == "0";
        RefreshView();
    }

    private async void FindDuplicates_Click(object sender, RoutedEventArgs e)
    {
        var groups = LibraryService.Instance.FindDuplicateGroups();
        if (groups.Count == 0)
        {
            await ShowMessageAsync(L.T("未发现重复歌曲"), L.T("已按歌名、歌手和时长比对，未发现重复。"));
            return;
        }
        var toRemove = await Dialogs.LibraryDialogs.ShowDuplicatesAsync(XamlRoot, groups);
        if (toRemove == null || toRemove.Count == 0) return;
        foreach (var t in toRemove) { Queue.Remove(t); TrackRemoved?.Invoke(this, t); }
        LibraryService.Instance.RemoveTracks(toRemove);
        RefreshView();
    }

    private async void FindMissing_Click(object sender, RoutedEventArgs e)
    {
        var lib = LibraryService.Instance;
        await lib.RefreshMissingAsync();
        var missing = lib.MissingTracks();
        if (missing.Count == 0)
        {
            await ShowMessageAsync(L.T("无丢失的文件"), L.T("曲库中的文件均存在。"));
            RefreshView();
            return;
        }

        var choice = await Dialogs.LibraryDialogs.ShowMissingAsync(XamlRoot, missing);
        if (choice == Dialogs.MissingChoice.Remove)
        {
            foreach (var t in missing) { Queue.Remove(t); TrackRemoved?.Invoke(this, t); }
            lib.RemoveTracks(missing);
        }
        else if (choice == Dialogs.MissingChoice.Relocate)
        {
            IntPtr hwnd = App.MainWindowInstance != null ? WindowNative.GetWindowHandle(App.MainWindowInstance) : IntPtr.Zero;
            string? root = FolderPickerHelper.PickFolder(hwnd, L.T("选择文件当前所在的文件夹"));
            if (!string.IsNullOrEmpty(root))
            {
                int fixedCount = await lib.RelocateMissingAsync(root);
                await ShowMessageAsync(L.T("重新定位完成"), L.F("已重新定位 {0} 首，共 {1} 首丢失。\n仅文件名在新文件夹中唯一匹配的才能重新定位。", fixedCount, missing.Count));
            }
        }
        RefreshView();
    }

    private async void AcousticMap_Click(object sender, RoutedEventArgs e)
    {
        await Dialogs.AcousticMapDialog.ShowAsync(XamlRoot, LibraryService.Instance.Tracks, (picked, near) =>
        {
            // 从地图上点的歌开始放，后面接着排它附近的歌
            var list = new List<TrackItem> { picked };
            list.AddRange(near.Where(t => !t.IsMissing));
            Queue = list;
            TrackSelected?.Invoke(this, picked);
        });
    }

    /// <summary>找相似的歌：以这首为起点，按声学特征排上最相近的 30 首。</summary>
    private void PlaySimilar(TrackItem track)
    {
        var near = AcousticService.Instance.Similar(track, LibraryService.Instance.Tracks, 30).Where(t => !t.IsMissing).ToList();
        var list = new List<TrackItem> { track };
        list.AddRange(near);
        Queue = list;
        TrackSelected?.Invoke(this, track);
    }

    private async void ResetRoam_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            RequestedTheme = ThemeService.Current,
            XamlRoot = XamlRoot,
            Title = L.T("重置漫游学习数据"),
            Content = L.F("漫游已累计 {0} 条收听记录。\n重置仅清除学习数据，不影响曲库和播放统计。", RoamingService.Instance.Samples),
            PrimaryButtonText = L.T("重置"),
            CloseButtonText = L.T("取消")
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary) RoamingService.Instance.Reset();
    }

    private async void RescanNow_Click(object sender, RoutedEventArgs e)
    {
        var lib = LibraryService.Instance;
        int total = 0;
        foreach (var folder in StateService.Instance.State.WatchedFolders.ToList())
        {
            if (!Directory.Exists(folder)) continue;
            var added = await lib.AddFolderAsync(folder);
            MetadataService.Instance.Enqueue(added);
            total += added.Count;
        }
        await lib.RefreshMissingAsync();
        RefreshView();
        await ShowMessageAsync(L.T("扫描完成"), total > 0 ? L.F("新增 {0} 首歌曲。", total) : L.T("未发现新歌曲（未改动的文件已跳过）。"));
    }

    #endregion

    #region 专辑

    private void AlbumsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumEntry a)
        {
            _openAlbum = a.Name;
            BeginStagger();
            RefreshView();
        }
    }

    private void AlbumItem_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AlbumEntry album) return;
        var flyout = new MenuFlyout();

        var play = new MenuFlyoutItem { Text = L.T("播放专辑"), Icon = new FontIcon { Glyph = "" } };
        play.Click += (s, e) =>
        {
            var list = LibraryService.Instance.Tracks.Where(t => t.Album == album.Name && !t.IsMissing).ToList();
            if (list.Count == 0) return;
            Queue = list;
            TrackSelected?.Invoke(this, list[0]);
        };
        flyout.Items.Add(play);

        var edit = new MenuFlyoutItem { Text = L.T("批量编辑专辑信息…"), Icon = new FontIcon { Glyph = "" } };
        edit.Click += (s, e) => _ = EditAlbumAsync(album.Name);
        flyout.Items.Add(edit);

        ShowFlyout(flyout, fe, args);
        args.Handled = true;
    }

    /// <summary>把整张专辑的专辑名 / 歌手一次改掉（写回每个文件）。</summary>
    private async Task EditAlbumAsync(string albumName)
    {
        var tracks = LibraryService.Instance.Tracks.Where(t => t.Album == albumName).ToList();
        if (tracks.Count == 0) return;
        var artists = tracks.Select(t => t.Artist).Distinct().ToList();
        var r = await Dialogs.LibraryDialogs.ShowAlbumEditAsync(XamlRoot, albumName, artists.Count == 1 ? artists[0] : "", tracks.Count);
        if (r == null) return;

        var errors = new List<string>();
        foreach (var t in tracks)
        {
            var err = LibraryService.Instance.UpdateTags(t, null, string.IsNullOrWhiteSpace(r.Value.Artist) ? null : r.Value.Artist.Trim(), r.Value.Album.Trim());
            if (err != null) errors.Add($"{t.Title}：{err.Split('\n')[0]}");
        }
        LibraryService.Instance.SaveLibrary();
        if (_openAlbum == albumName) _openAlbum = r.Value.Album.Trim();
        RefreshView();
        if (errors.Count > 0)
            await ShowMessageAsync(L.T("部分歌曲写入失败"), string.Join("\n", errors.Take(8)) + (errors.Count > 8 ? L.F("\n…另有 {0} 首", errors.Count - 8) : ""));
    }

    #endregion

    #region 播放队列

    /// <summary>下一首播放：插到当前曲目后面。</summary>
    private void PlayNextInQueue(TrackItem track)
    {
        EnsureQueueHasCurrent();
        Queue.Remove(track);
        int idx = _currentTrack != null ? Queue.IndexOf(_currentTrack) : -1;
        Queue.Insert(Math.Min(idx + 1, Queue.Count), track);
    }

    /// <summary>加入队列：放到队尾。</summary>
    private void AddToQueueEnd(TrackItem track)
    {
        EnsureQueueHasCurrent();
        Queue.Remove(track);
        Queue.Add(track);
    }

    private void EnsureQueueHasCurrent()
    {
        if (Queue.Count == 0) Queue = DisplayTracks.ToList();
        if (_currentTrack != null && !Queue.Contains(_currentTrack)) Queue.Insert(0, _currentTrack);
    }

    #endregion

    #region 拖拽排序 / 歌单 M3U / 智能歌单

    private void TracksListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_currentTab != "playlists" || _openPlaylist == null || _openIsSmart) return;
        var ordered = _listItems.OfType<TrackItem>().ToList();
        LibraryService.Instance.SetPlaylistOrder(_openPlaylist, ordered);
        DisplayTracks.Clear();
        foreach (var t in ordered) DisplayTracks.Add(t);
    }

    private async void NewSmartPlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        var def = await Dialogs.LibraryDialogs.ShowSmartPlaylistAsync(XamlRoot);
        if (def == null) return;
        if (!LibraryService.Instance.CreateSmartPlaylist(def))
        {
            await ShowMessageAsync(L.T("无法创建智能歌单"), L.T("名称不能为空，且不能与已有歌单重名。"));
            return;
        }
        RefreshView();
    }

    private static IntPtr MainHwnd() =>
        App.MainWindowInstance != null ? WindowNative.GetWindowHandle(App.MainWindowInstance) : IntPtr.Zero;

    private async void ImportM3uBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainHwnd());
            picker.FileTypeFilter.Add(".m3u");
            picker.FileTypeFilter.Add(".m3u8");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var (name, imported, notFound) = await LibraryService.Instance.ImportPlaylistM3uAsync(file.Path);
            if (name == null)
            {
                await ShowMessageAsync(L.T("导入失败"), L.T("文件中未读取到歌曲路径。"));
                return;
            }
            RefreshView();
            await ShowMessageAsync(L.T("导入完成"), L.F("已创建歌单“{0}”，导入 {1} 首", name, imported) + (notFound > 0 ? L.F("，{0} 首未找到文件。", notFound) : "。"));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(L.T("导入失败"), ex.Message);
        }
    }

    private async Task ExportPlaylistAsync(string name, bool isSmart)
    {
        try
        {
            var lib = LibraryService.Instance;
            var tracks = isSmart && lib.FindSmart(name) is { } def ? lib.GetSmartTracks(def) : lib.GetPlaylistTracks(name);
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = name };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainHwnd());
            picker.FileTypeChoices.Add(L.T("M3U8 播放列表"), new List<string> { ".m3u8" });
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            lib.ExportPlaylistM3u(tracks, file.Path);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(L.T("导出失败"), ex.Message);
        }
    }

    #endregion

    #region 歌单管理

    private void PlaylistsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistEntry entry)
        {
            _openPlaylist = entry.Name;
            _openIsSmart = entry.IsSmart;
            BeginStagger();
            RefreshView();
        }
    }

    private void PlaylistBackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTab == "albums")
        {
            _openAlbum = null;
            RefreshView();
            UiAnimations.Pulse(AlbumsGridView);
            return;
        }
        _openPlaylist = null;
        _openIsSmart = false;
        RefreshView();
        UiAnimations.Pulse(PlaylistsPanel);
    }

    private async void NewPlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptTextAsync(L.T("新建歌单"), L.T("歌单名称"), "");
        if (name == null) return;
        if (!LibraryService.Instance.CreatePlaylist(name))
        {
            await ShowMessageAsync(L.T("无法创建歌单"), L.T("名称不能为空，且不能与已有歌单重名。"));
            return;
        }
        RefreshView();
    }

    private void PlaylistItem_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PlaylistEntry entry)
        {
            var flyout = new MenuFlyout();

            var open = new MenuFlyoutItem { Text = L.T("打开"), Icon = new FontIcon { Glyph = "" } };
            open.Click += (s, e) =>
            {
                _openPlaylist = entry.Name;
                _openIsSmart = entry.IsSmart;
                RefreshView();
            };
            flyout.Items.Add(open);

            var export = new MenuFlyoutItem { Text = L.T("导出为 M3U…") };
            export.Click += (s, e) => _ = ExportPlaylistAsync(entry.Name, entry.IsSmart);
            flyout.Items.Add(export);

            if (entry.IsSmart)
            {
                if (!entry.IsBuiltIn)
                {
                    var delSmart = new MenuFlyoutItem { Text = L.T("删除智能歌单") };
                    delSmart.Click += (s, e) =>
                    {
                        LibraryService.Instance.DeleteSmartPlaylist(entry.Name);
                        RefreshView();
                    };
                    flyout.Items.Add(delSmart);
                }
                ShowFlyout(flyout, fe, args);
                args.Handled = true;
                return;
            }

            var rename = new MenuFlyoutItem { Text = L.T("重命名"), Icon = new FontIcon { Glyph = "" } };
            rename.Click += async (s, e) =>
            {
                var newName = await PromptTextAsync(L.T("重命名歌单"), L.T("歌单名称"), entry.Name);
                if (newName == null) return;
                if (!LibraryService.Instance.RenamePlaylist(entry.Name, newName))
                {
                    await ShowMessageAsync(L.T("无法重命名"), L.T("名称不能为空，且不能与已有歌单重名。"));
                    return;
                }
                RefreshView();
            };
            flyout.Items.Add(rename);

            var delete = new MenuFlyoutItem { Text = L.T("删除歌单"), Icon = new FontIcon { Glyph = "" } };
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
            RequestedTheme = ThemeService.Current,
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = L.T("确定"),
            CloseButtonText = L.T("取消"),
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
            RequestedTheme = ThemeService.Current,
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = L.T("确定")
        };
        await dlg.ShowAsync();
    }

    #endregion
}
