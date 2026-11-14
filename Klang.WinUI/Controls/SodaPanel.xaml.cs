using System.Collections.ObjectModel;
using Klang_WinUI.Models;
using Klang_WinUI.Services;
using Klang_WinUI.Services.Soda;
using Klang_WinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Klang_WinUI.Controls;

/// <summary>Soda sidebar. The shared now-playing stage and continuing queue belong to MainWindow.</summary>
public sealed partial class SodaPanel : UserControl
{
    public event Action<SodaMode>? ModeRequested;
    public event Action<List<TrackItem>, TrackItem, SodaMode?>? TrackRequested;

    private sealed record ModeCard(string Key, string Name, string Glyph, Color Color);
    private static readonly ModeCard[] Cards =
    [
        new("familiar", "熟悉模式", "\uE8D6", Color.FromArgb(255, 154, 137, 244)),
        new("fresh", "新鲜模式", "\uE734", Color.FromArgb(255, 107, 182, 162)),
        new("scene_mode_chill", "Chill 放松", "\uE706", Color.FromArgb(255, 133, 171, 219)),
        new("scene_mode_car_mode", "驾车", "\uE804", Color.FromArgb(255, 220, 168, 106)),
        new("scene_mode_fish", "摸鱼", "\uE708", Color.FromArgb(255, 112, 181, 190)),
        new("scene_mode_cantonese", "粤语", "\uE8F2", Color.FromArgb(255, 219, 141, 167)),
        new("scene_mode_sport", "动感健身", "\uE945", Color.FromArgb(255, 211, 156, 106)),
        new("scene_mode_happy", "快乐时光", "\uE76E", Color.FromArgb(255, 216, 188, 104))
    ];

    private readonly ObservableCollection<TrackItem> _tracks = new();
    private readonly Dictionary<string, Button> _modeButtons = new();
    private readonly Dictionary<string, TextBlock> _modeLabels = new();
    private readonly Dictionary<string, SodaMode> _modes = new();
    private CancellationTokenSource? _homeCts;
    private CancellationTokenSource? _viewCts;
    private Window? _owner;
    private SodaMode? _viewMode;
    private string? _currentTrackId;
    private string _cursor = "";
    private bool _hasMore;
    private bool _active;
    private bool _subscribed;
    private bool _favoritesView;
    private bool _favoriteBusy;
    private bool _loginBusy;
    private bool _retryFavorites;
    private bool _retryAppend;
    private string _selectedKey = "default";
    private SodaService Service => SodaService.Instance;

    public SodaPanel()
    {
        InitializeComponent();
        TracksList.ItemsSource = _tracks;
        CreateModeCards();
        ApplyLabels();
        UpdateAccount();
        ActualThemeChanged += (_, _) => UpdateSelection();
        // MainWindow owns page activation; folding the sidebar must retain this page.
    }

    public async Task OnShownAsync(Window owner)
    {
        _owner = owner;
        _active = true;
        if (!_subscribed)
        {
            Service.LoginChanged += Service_LoginChanged;
            L.LanguageChanged += LanguageChanged;
            _subscribed = true;
        }
        ApplyLabels();
        UpdateAccount();
        await LoadHomeAsync(verifyAccount: true);
        if (_active && _favoritesView && Service.IsLoggedIn && _tracks.Count == 0)
            await LoadFavoritesAsync(append: false);
    }

    public void OnHidden()
    {
        _active = false;
        CancelHomeLoad();
        CancelViewLoad();
        if (_subscribed)
        {
            Service.LoginChanged -= Service_LoginChanged;
            L.LanguageChanged -= LanguageChanged;
            _subscribed = false;
        }
    }

    public void SetPlaybackStatus(string message, bool busy = false)
    {
        PlaybackStatusText.Text = message;
        PlaybackStatusPanel.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        PlaybackRing.IsActive = busy;
        PlaybackRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowTracks(List<TrackItem> tracks, string heading, SodaMode? mode = null)
    {
        // A mode may finish preparing after the user has opened their favorites.
        if (mode != null && _favoritesView) return;
        CancelViewLoad();
        _favoritesView = false;
        _hasMore = false;
        _viewMode = mode;
        NoticeBar.IsOpen = false;
        ShowTrackView(heading);
        ReplaceTracks(tracks);
        UpdateTrackState();
    }

    public void RefreshPlayingQueue(List<TrackItem> tracks, SodaMode mode)
    {
        if (_favoritesView || _viewMode?.Key != mode.Key || TracksView.Visibility != Visibility.Visible) return;
        ReplaceTracks(tracks);
        UpdateTrackState();
    }

    public void SetCurrentTrack(TrackItem? track)
    {
        _currentTrackId = track?.Id;
        // Selection belongs to this view; IsCurrent belongs to the shared playback queue.
        TracksList.SelectedItem = _tracks.FirstOrDefault(t => t.Id == _currentTrackId);
    }

    private void CreateModeCards()
    {
        foreach (var card in Cards)
        {
            var icon = new FontIcon { Glyph = card.Glyph, FontSize = 16, Foreground = new SolidColorBrush(card.Color) };
            var label = new TextBlock
            {
                FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var content = new Grid { ColumnSpacing = 12 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(icon);
            Grid.SetColumn(label, 1);
            content.Children.Add(label);
            var button = new Button
            {
                Style = (Style)Resources["SodaNavStyle"], Content = content, Tag = card.Key,
                IsEnabled = false
            };
            button.Click += ModeButton_Click;
            _modeButtons.Add(card.Key, button);
            _modeLabels.Add(card.Key, label);
            ModesPanel.Children.Add(button);
        }
    }

    private void ApplyLabels()
    {
        HomeTitle.Text = L.T("汽水音乐");
        RecommendTitle.Text = L.T("推荐");
        FavoritesTitle.Text = L.T("我喜欢的音乐");
        AutomationProperties.SetName(RecommendButton, RecommendTitle.Text);
        AutomationProperties.SetName(FavoritesButton, FavoritesTitle.Text);
        ModesLoadingText.Text = L.T("正在加载音乐模式…");
        RetryButton.Content = L.T("重试");
        PlayAllButton.Content = L.T("播放全部");
        LoadMoreButton.Content = _favoriteBusy ? L.T("正在加载…") : L.T("加载更多");
        AutomationProperties.SetName(RefreshButton, L.T("刷新"));
        ToolTipService.SetToolTip(RefreshButton, L.T("刷新"));
        foreach (var card in Cards)
        {
            _modeLabels[card.Key].Text = L.T(card.Name);
            AutomationProperties.SetName(_modeButtons[card.Key], L.T(card.Name));
            ToolTipService.SetToolTip(_modeButtons[card.Key], _modes.ContainsKey(card.Key)
                ? L.T("立即播放") : L.T("此模式暂不可用，请刷新后重试"));
        }
        if (_favoritesView) TracksHeading.Text = L.T("我喜欢的音乐");
        UpdateAccount();
        UpdateTrackState();
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = ThemeService.Brush(this, "SubtleFillColorSecondaryBrush");
        foreach (var (key, button) in _modeButtons)
            button.Background = key == _selectedKey ? selected : null;
        RecommendButton.Background = _selectedKey == "default" ? selected : null;
        FavoritesButton.Background = _selectedKey == "favorites" ? selected : null;
    }

    private void LanguageChanged() => Dispatch(ApplyLabels);

    private void Service_LoginChanged() => Dispatch(() =>
    {
        UpdateAccount();
        if (!Service.IsLoggedIn && _favoritesView)
        {
            ReturnHome();
            _tracks.Clear();
        }
    });

    private void Dispatch(Action action)
    {
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => { if (_active) action(); });
    }

    private void UpdateAccount()
    {
        bool loggedIn = Service.IsLoggedIn;
        string name = loggedIn ? Service.Nickname : L.T("未登录");
        if (loggedIn && string.IsNullOrWhiteSpace(name)) name = L.T("Soda 用户");
        AccountName.Text = name;
        AccountPicture.DisplayName = name;
        AccountButton.Content = loggedIn ? L.T("退出") : L.T("登录");
        AccountButton.IsEnabled = !_loginBusy;
        AccountPicture.ProfilePicture = null;
        if (loggedIn && Uri.TryCreate(Service.AvatarUrl, UriKind.Absolute, out var avatar)
            && avatar.Scheme is "https" or "http")
            AccountPicture.ProfilePicture = new BitmapImage(avatar);
    }

    private async Task LoadHomeAsync(bool verifyAccount)
    {
        CancelHomeLoad();
        var cts = _homeCts = new CancellationTokenSource();
        ModesLoadingPanel.Visibility = Visibility.Visible;
        ModesRing.IsActive = true;
        try
        {
            if (verifyAccount && Service.IsLoggedIn)
                await Service.VerifyLoginAsync(cts.Token);
            if (!IsHomeCurrent(cts)) return;
            UpdateAccount();
            var modes = await Service.GetModesAsync(cts.Token);
            if (!IsHomeCurrent(cts)) return;
            _modes.Clear();
            foreach (var mode in modes)
            {
                // Scene IDs are discovered by the service, never inferred from labels or order.
                if (!mode.Key.StartsWith("scene_mode_", StringComparison.Ordinal) || mode.SceneId.HasValue)
                    _modes[mode.Key] = mode;
            }
            foreach (var card in Cards) _modeButtons[card.Key].IsEnabled = _modes.ContainsKey(card.Key);
            ApplyLabels();
            if (!_modes.Keys.Any(key => _modeButtons.ContainsKey(key)))
                ShowNotice(L.T("音乐模式暂不可用，请稍后刷新。"), favorites: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (IsHomeCurrent(cts))
                ShowNotice(L.T("暂时无法加载 Soda，请检查网络后重试。"), favorites: false);
        }
        finally
        {
            if (ReferenceEquals(_homeCts, cts))
            {
                ModesLoadingPanel.Visibility = Visibility.Collapsed;
                ModesRing.IsActive = false;
                _homeCts = null;
            }
            cts.Dispose();
        }
    }

    private bool IsHomeCurrent(CancellationTokenSource cts) => _active && !cts.IsCancellationRequested && ReferenceEquals(_homeCts, cts);
    private bool IsViewCurrent(CancellationTokenSource cts) => _active && !cts.IsCancellationRequested && ReferenceEquals(_viewCts, cts) && _favoritesView;

    private void CancelHomeLoad()
    {
        var previous = _homeCts;
        _homeCts = null;
        previous?.Cancel();
        ModesRing.IsActive = false;
        ModesLoadingPanel.Visibility = Visibility.Collapsed;
    }

    private void CancelViewLoad()
    {
        var previous = _viewCts;
        _viewCts = null;
        previous?.Cancel();
        _favoriteBusy = false;
        TracksRing.IsActive = false;
    }

    private async Task<bool> LoginAsync()
    {
        if (_loginBusy || _owner == null) return false;
        _loginBusy = true;
        UpdateAccount();
        try
        {
            bool success = await SodaLoginWindow.ShowAsync(_owner);
            if (!_active) return false;
            UpdateAccount();
            if (success) await LoadHomeAsync(verifyAccount: false);
            return success && Service.IsLoggedIn && _active;
        }
        catch (Exception)
        {
            if (_active) ShowNotice(L.T("暂时无法打开登录，请稍后重试。"), favorites: false);
            return false;
        }
        finally
        {
            _loginBusy = false;
            UpdateAccount();
        }
    }

    private async void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (Service.IsLoggedIn)
        {
            Service.Logout();
            UpdateAccount();
            ReturnHome();
            _tracks.Clear();
            NoticeBar.IsOpen = false;
            await LoadHomeAsync(verifyAccount: false);
        }
        else await LoginAsync();
    }

    private async void FavoritesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Service.IsLoggedIn && !await LoginAsync()) return;
        if (!_active) return;
        await LoadFavoritesAsync(append: false);
    }

    private async Task LoadFavoritesAsync(bool append)
    {
        if (!_active || !Service.IsLoggedIn || (append && (_favoriteBusy || !_hasMore))) return;
        CancelViewLoad();
        var cts = _viewCts = new CancellationTokenSource();
        _favoritesView = true;
        _selectedKey = "favorites";
        UpdateSelection();
        _viewMode = null;
        _favoriteBusy = true;
        NoticeBar.IsOpen = false;
        if (!append)
        {
            _tracks.Clear();
            _cursor = "";
            _hasMore = false;
        }
        ShowTrackView(L.T("我喜欢的音乐"));
        UpdateTrackState();
        try
        {
            string requestedCursor = _cursor;
            var page = await Service.GetLikedAsync(requestedCursor, cts.Token);
            if (!IsViewCurrent(cts)) return;
            var known = _tracks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var track in page.Tracks)
                if (known.Add(track.Id)) _tracks.Add(track);
            _cursor = page.Cursor;
            _hasMore = page.HasMore && !string.IsNullOrEmpty(_cursor) && _cursor != requestedCursor;
            SetCurrentTrackById();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (IsViewCurrent(cts))
                ShowNotice(L.T("暂时无法加载喜欢的音乐，请检查网络后重试。"), favorites: true, append: append);
        }
        finally
        {
            if (ReferenceEquals(_viewCts, cts))
            {
                _favoriteBusy = false;
                _viewCts = null;
                UpdateTrackState();
                if (!append) RevealFavoriteTracks();
            }
            cts.Dispose();
        }
    }

    private void ShowTrackView(string heading)
    {
        TracksExpander.Visibility = Visibility.Visible;
        TracksExpander.IsExpanded = _favoritesView;
        TracksHeading.Text = heading;
        if (_favoritesView) RevealFavoriteTracks();
    }

    private void RevealFavoriteTracks() => DispatcherQueue.TryEnqueue(
        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_active || !_favoritesView) return;
            // Wait until the expanded list has its final height before scrolling to it.
            HomeScroll.UpdateLayout();
            HomeScroll.ChangeView(null, HomeScroll.ScrollableHeight, null, disableAnimation: true);
        });

    private void ReplaceTracks(IEnumerable<TrackItem> tracks)
    {
        _tracks.Clear();
        foreach (var track in tracks) _tracks.Add(track);
        SetCurrentTrackById();
    }

    private void SetCurrentTrackById() => TracksList.SelectedItem = _tracks.FirstOrDefault(t => t.Id == _currentTrackId);

    private void UpdateTrackState()
    {
        bool empty = _tracks.Count == 0;
        TracksEmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        TracksRing.IsActive = empty && _favoriteBusy;
        TracksRing.Visibility = empty && _favoriteBusy ? Visibility.Visible : Visibility.Collapsed;
        EmptyIcon.Visibility = _favoriteBusy ? Visibility.Collapsed : Visibility.Visible;
        TracksEmptyText.Text = _favoriteBusy ? L.T("正在加载喜欢的音乐…")
            : NoticeBar.IsOpen && _favoritesView ? L.T("加载未完成，请点击重试。")
            : _favoritesView ? L.T("还没有喜欢的音乐，去 Soda 收藏一些好歌吧。")
            : L.T("暂时没有可播放的歌曲，请选择其他模式或刷新。");
        TrackCountText.Text = L.F("{0} 首歌曲", _tracks.Count);
        PlayAllButton.IsEnabled = !empty;
        LoadMoreButton.Visibility = _favoritesView && _hasMore ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.IsEnabled = !_favoriteBusy;
        LoadMoreButton.Content = _favoriteBusy ? L.T("正在加载…") : L.T("加载更多");
    }

    private void ShowNotice(string message, bool favorites, bool append = false)
    {
        _retryFavorites = favorites;
        _retryAppend = append;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }

    private void ReturnHome()
    {
        CancelViewLoad();
        _favoritesView = false;
        _viewMode = null;
        TracksExpander.Visibility = Visibility.Collapsed;
        _selectedKey = "default";
        UpdateSelection();
        NoticeBar.IsOpen = false;
    }

    private void RequestMode(SodaMode mode)
    {
        CancelViewLoad();
        _favoritesView = false;
        _selectedKey = mode.Key;
        UpdateSelection();
        TracksExpander.IsExpanded = false;
        NoticeBar.IsOpen = false;
        ModeRequested?.Invoke(mode);
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key } && _modes.TryGetValue(key, out var mode)) RequestMode(mode);
    }

    private void RecommendButton_Click(object sender, RoutedEventArgs e) => RequestMode(new SodaMode("default", L.T("推荐"), "default"));
    private async void LoadMoreButton_Click(object sender, RoutedEventArgs e) => await LoadFavoritesAsync(append: true);
    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        NoticeBar.IsOpen = false;
        if (_retryFavorites && _favoritesView) await LoadFavoritesAsync(_retryAppend);
        else await LoadHomeAsync(verifyAccount: true);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        NoticeBar.IsOpen = false;
        if (_favoritesView) await LoadFavoritesAsync(append: false);
        else if (_viewMode != null && TracksView.Visibility == Visibility.Visible) RequestMode(_viewMode);
        else await LoadHomeAsync(verifyAccount: true);
    }

    private void TracksList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackItem track) TrackRequested?.Invoke(_tracks.ToList(), track, _viewMode);
    }

    private void PlayAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count > 0) TrackRequested?.Invoke(_tracks.ToList(), _tracks[0], _viewMode);
    }

}
