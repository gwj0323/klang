using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Klang_WinUI.Models;
using Klang_WinUI.Services;
using Klang_WinUI.Services.Cloud;

namespace Klang_WinUI.Controls;

/// <summary>“云端”首页中的一项（每日推荐 / 用户歌单）。</summary>
public sealed class CloudEntry
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string PlaylistId { get; init; } = "";
    public bool IsDaily { get; init; }
}

/// <summary>云端面板：扫码 / Cookie 登录、每日推荐、我的歌单、搜索结果。显示的是 <see cref="CloudSources.Current"/>。</summary>
public sealed partial class CloudPanel : UserControl
{
    private readonly ObservableCollection<CloudEntry> _entries = new();
    private readonly ObservableCollection<TrackItem> _tracks = new();
    private readonly DispatcherQueueTimer _qrTimer;
    private string _qrKey = "";
    private bool _qrBusy;
    private bool _active;
    private int _requestSeq;
    private bool _inTracksMode;

    /// <summary>用户点播：参数为点播时的整个列表（作为播放队列）和被点的曲目。</summary>
    public event Action<List<TrackItem>, TrackItem>? TrackInvoked;

    private ICloudSource Svc => CloudSources.Current;

    public CloudPanel()
    {
        InitializeComponent();
        UiAnimations.AttachFade(LoginView);
        UiAnimations.AttachFade(HomeView);
        UiAnimations.AttachFade(TracksList);
        UiAnimations.AttachFade(TracksHeader, 0f);
        UiAnimations.AttachFade(BusyPanel, 6f);
        HomeList.ItemsSource = _entries;
        TracksList.ItemsSource = _tracks;

        _qrTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _qrTimer.Interval = TimeSpan.FromSeconds(2);
        _qrTimer.Tick += async (s, e) => await PollQrAsync();

        // 来源分段切换：每个来源一段；只有一个来源时不显示
        int col = 0;
        foreach (var src in CloudSources.All)
        {
            SourceSwitchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var seg = new Button { Content = src.DisplayName, Tag = src.Key };
            AutomationProperties.SetName(seg, src.DisplayName);
            Grid.SetColumn(seg, col++);
            seg.Click += (s, e) => SelectSource((string)((Button)s).Tag);
            SourceSwitchGrid.Children.Add(seg);
            var s = src;
            // 只有当前显示的来源变化才刷新界面；封面缓存不分来源，都要通知主窗口
            s.LoginChanged += () => DispatcherQueue.SafeEnqueue(() => { if (s == Svc) UpdateView(); });
            s.CoverCached += t => DispatcherQueue.SafeEnqueue(() => CoverReady?.Invoke(t));
        }
        ApplySourceUi();
        UpdateView();
    }

    /// <summary>分段切换的样式：当前来源那一段凸起。</summary>
    private void UpdateSourceSegments()
    {
        var on = (Style)Resources["SegmentOnStyle"];
        var off = (Style)Resources["SegmentOffStyle"];
        foreach (var seg in SourceSwitchGrid.Children.OfType<Button>())
            seg.Style = (string)seg.Tag == Svc.Key ? on : off;
    }

    /// <summary>重建“⋯”菜单：音质（单选）+ 退出登录。</summary>
    private void BuildAccountMenu()
    {
        var src = Svc;
        AccountMenu.Items.Clear();
        var quality = new MenuFlyoutSubItem { Text = L.T("音质") };
        foreach (var (key, label) in src.Qualities)
        {
            var item = new RadioMenuFlyoutItem { Text = label, Tag = key, GroupName = "CloudQuality", IsChecked = key == src.PreferredQuality };
            item.Click += (s, e) =>
            {
                Svc.PreferredQuality = (string)((MenuFlyoutItem)s).Tag;
                UpdateQualityText();
            };
            quality.Items.Add(item);
        }
        AccountMenu.Items.Add(quality);
        AccountMenu.Items.Add(new MenuFlyoutSeparator());
        var logout = new MenuFlyoutItem { Text = L.T("退出登录…"), Icon = new FontIcon { Glyph = "" } };
        logout.Click += Logout_Click;
        AccountMenu.Items.Add(logout);
    }

    private void UpdateQualityText()
    {
        var src = Svc;
        QualityText.Text = src.Qualities.FirstOrDefault(q => q.Key == src.PreferredQuality).Label ?? "";
    }

    /// <summary>按当前来源刷新登录提示、音质列表等。</summary>
    private void ApplySourceUi()
    {
        var src = Svc;
        LoginTitleText.Text = L.F("登录{0}", src.DisplayName);

        _loadingQrMode = true;
        QrModeBox.Items.Clear();
        foreach (var (key, label) in src.QrModes)
            QrModeBox.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        _qrMode = src.QrModes[0].Key;
        QrModeBox.SelectedIndex = 0;
        QrModeBox.Visibility = src.QrModes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _loadingQrMode = false;

        QrStatusText.Text = src.QrHintFor(_qrMode);
        CookieExpander.Visibility = src.SupportsCookieLogin ? Visibility.Visible : Visibility.Collapsed;
        CookieBox.PlaceholderText = src.CookieHint;
        CookieBox.Password = "";
        CookieStatusText.Text = "";

        UpdateSourceSegments();
        BuildAccountMenu();
        UpdateQualityText();
    }

    private string _qrMode = "";
    private bool _loadingQrMode;

    private async void QrModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingQrMode || QrModeBox.SelectedItem is not ComboBoxItem it || it.Tag is not string key || key == _qrMode) return;
        _qrMode = key;
        _qrTimer.Stop();
        _qrKey = "";
        QrImage.Source = null;
        QrStatusText.Text = Svc.QrHintFor(_qrMode);
        if (_active) await RefreshQrAsync();
    }

    private void SelectSource(string key)
    {
        if (key == Svc.Key) return;
        var src = CloudSources.Get(key);
        if (src == null) return;
        CloudSources.Current = src;
        // 换了平台：丢掉上一个平台的二维码、歌单和列表
        _requestSeq++;
        _qrTimer.Stop();
        _qrKey = "";
        QrImage.Source = null;
        _entries.Clear();
        _tracks.Clear();
        _inTracksMode = false;
        HideBusy();
        ApplySourceUi();
        UpdateView();
        if (_active && !Svc.IsLoggedIn) _ = RefreshQrAsync();
    }

    /// <summary>云端曲目的封面缓存完成，供主窗口刷新封面。</summary>
    public event Action<TrackItem>? CoverReady;

    #region 显示 / 隐藏

    public void OnShown()
    {
        _active = true;
        UpdateView();
        if (!Svc.IsLoggedIn && !_inTracksMode)
        {
            if (_qrKey.Length == 0) _ = RefreshQrAsync();
            else _qrTimer.Start();
        }
    }

    public void OnHidden()
    {
        _active = false;
        _qrTimer.Stop();
    }

    private void UpdateView()
    {
        bool logged = Svc.IsLoggedIn;
        SourceSwitch.Visibility = CloudSources.All.Count > 1 && !_inTracksMode ? Visibility.Visible : Visibility.Collapsed;
        LoginView.Visibility = (!logged && !_inTracksMode) ? Visibility.Visible : Visibility.Collapsed;
        HomeView.Visibility = (logged && !_inTracksMode) ? Visibility.Visible : Visibility.Collapsed;
        TracksList.Visibility = _inTracksMode ? Visibility.Visible : Visibility.Collapsed;
        TracksHeader.Visibility = _inTracksMode ? Visibility.Visible : Visibility.Collapsed;

        if (logged)
        {
            _qrTimer.Stop();
            NicknameText.Text = Svc.Nickname;
            AvatarPicture.DisplayName = Svc.Nickname;
            var avatar = Svc.AvatarUrl;
            AvatarPicture.ProfilePicture = Svc.IsTrustedUrl(avatar) && Uri.TryCreate(avatar, UriKind.Absolute, out var u)
                ? new BitmapImage(u) { DecodePixelWidth = 56 } : null;
            if (!_inTracksMode && _entries.Count == 0 && !_homeLoading) _ = LoadHomeAsync();
        }
        else
        {
            _entries.Clear();
            if (_active && !_inTracksMode && _qrKey.Length == 0 && !_qrBusy) _ = RefreshQrAsync();
        }
    }

    #endregion

    #region 登录

    private async Task RefreshQrAsync()
    {
        if (_qrBusy) return;
        _qrBusy = true;
        _qrTimer.Stop();
        QrRing.IsActive = true;
        QrStatusText.Text = L.T("正在获取二维码…");
        try
        {
            var src = Svc;
            var mode = _qrMode;
            var (key, png) = await src.CreateQrAsync(mode);
            if (src != Svc || mode != _qrMode) return;   // 等待期间换了来源或扫码方式
            if (key.Length == 0)
            {
                _qrKey = "";
                QrImage.Source = null;
                QrStatusText.Text = L.T("获取二维码失败，请检查网络后点击“刷新二维码”");
                return;
            }
            _qrKey = key;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            QrImage.Source = bmp;
            QrStatusText.Text = Svc.QrHintFor(_qrMode);
            if (_active) _qrTimer.Start();
        }
        catch (Exception ex)
        {
            QrStatusText.Text = L.T("获取二维码失败：") + ex.Message;
        }
        finally
        {
            _qrBusy = false;
            QrRing.IsActive = false;
        }
    }

    private async Task PollQrAsync()
    {
        if (_qrKey.Length == 0 || _qrBusy) return;
        _qrBusy = true;
        try
        {
            var src = Svc;
            var key = _qrKey;
            var (state, msg) = await src.PollQrAsync(key);
            if (src != Svc || key != _qrKey) return;   // 等待期间换了来源或刷新了二维码
            switch (state)
            {
                case QrLoginState.Waiting: QrStatusText.Text = L.T("等待扫码…"); break;
                case QrLoginState.Scanned: QrStatusText.Text = L.T("已扫码，请在手机上确认登录"); break;
                case QrLoginState.Expired:
                    _qrTimer.Stop();
                    _qrKey = "";
                    QrStatusText.Text = L.T("二维码已过期，正在刷新…");
                    _qrBusy = false;
                    await RefreshQrAsync();
                    return;
                case QrLoginState.Success:
                    _qrTimer.Stop();
                    _qrKey = "";
                    QrStatusText.Text = L.T("登录成功");
                    break;
                default:
                    QrStatusText.Text = L.T("登录出错：") + msg;
                    break;
            }
        }
        finally
        {
            _qrBusy = false;
        }
    }

    private async void RefreshQr_Click(object sender, RoutedEventArgs e)
    {
        _qrKey = "";
        await RefreshQrAsync();
    }

    private async void CookieLogin_Click(object sender, RoutedEventArgs e)
    {
        CookieLoginBtn.IsEnabled = false;
        CookieStatusText.Text = L.T("正在验证…");
        var (ok, msg) = await Svc.LoginWithCookieAsync(CookieBox.Password);
        CookieStatusText.Text = msg;
        CookieLoginBtn.IsEnabled = true;
        if (ok) CookieBox.Password = "";
    }

    /// <summary>退出登录：先确认（菜单里一点就退、要重新扫码，代价不小）。</summary>
    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        var src = Svc;
        var dlg = new ContentDialog
        {
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            RequestedTheme = ThemeService.Current,
            XamlRoot = XamlRoot,
            Title = L.F("退出{0}账号？", src.DisplayName),
            Content = L.T("退出后需要重新扫码登录。"),
            PrimaryButtonText = L.T("退出登录"),
            CloseButtonText = L.T("取消"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || src != Svc) return;
        src.Logout();
        _qrKey = "";
        _inTracksMode = false;
        UpdateView();
    }

    #endregion

    #region 首页 / 列表

    private bool _homeLoading;

    private async Task LoadHomeAsync()
    {
        _homeLoading = true;
        ShowBusy(L.T("正在加载歌单…"), spinning: true);
        try
        {
            var src = Svc;
            var playlists = await src.GetUserPlaylistsAsync();
            if (src != Svc) return;   // 加载期间换了来源
            _entries.Clear();
            if (src.HasDailyRecommend)
                _entries.Add(new CloudEntry { Title = src.DailyRecommendTitle, Subtitle = "", Glyph = "", IsDaily = true });
            foreach (var pl in playlists)
            {
                _entries.Add(new CloudEntry
                {
                    Title = pl.IsLiked ? L.T("我喜欢的音乐") : pl.Name,
                    Subtitle = pl.CountText,
                    Glyph = pl.IsLiked ? "" : "",
                    PlaylistId = pl.Id
                });
            }
            HideBusy();
        }
        catch (Exception ex)
        {
            ShowBusy(L.T("加载歌单失败：") + ex.Message);
        }
        finally
        {
            _homeLoading = false;
        }
    }

    private async void HomeList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CloudEntry entry) return;
        await ShowTracksAsync(entry.Title, () => entry.IsDaily
            ? Svc.GetDailyRecommendAsync()
            : Svc.GetPlaylistTracksAsync(entry.PlaylistId));
    }

    /// <summary>搜索云端歌曲（由主搜索框回车触发）。</summary>
    public Task SearchAsync(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return Task.CompletedTask;
        return ShowTracksAsync(L.F("搜索：{0}", keyword.Trim()), () => Svc.SearchAsync(keyword));
    }

    private async Task ShowTracksAsync(string title, Func<Task<List<TrackItem>>> loader)
    {
        int seq = ++_requestSeq;
        _inTracksMode = true;
        TracksHeaderText.Text = title;
        _tracks.Clear();
        UpdateView();
        ShowBusy(L.T("正在加载…"), spinning: true);
        try
        {
            var list = await loader();
            if (seq != _requestSeq) return; // 已被更新的请求取代
            foreach (var t in list) _tracks.Add(t);
            if (list.Count == 0) ShowBusy(L.T("无内容（可能未登录，或无搜索结果）"));
            else HideBusy();
        }
        catch (Exception ex)
        {
            if (seq == _requestSeq) ShowBusy(L.T("加载失败：") + ex.Message);
        }
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        _requestSeq++;
        _inTracksMode = false;
        HideBusy();
        UpdateView();
        if (!Svc.IsLoggedIn && _active && _qrKey.Length > 0) _qrTimer.Start();
    }

    private void TracksList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackItem t) TrackInvoked?.Invoke(_tracks.ToList(), t);
    }

    /// <summary>要求播放某首歌的 MV。</summary>
    public event Action<TrackItem>? MvRequested;

    /// <summary>要求把某首云端歌曲保存到曲库。</summary>
    public event Action<TrackItem>? SaveRequested;

    private DispatcherTimer? _toastTimer;

    /// <summary>在面板底部显示一条提示，几秒后自动消失（ms &lt;= 0 表示一直显示，直到下一条）。</summary>
    public void ShowToast(string text, int ms = 3500)
    {
        ToastText.Text = text;
        ToastBorder.Opacity = 1;
        _toastTimer?.Stop();
        if (ms <= 0) return;
        _toastTimer ??= new DispatcherTimer();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _toastTimer.Tick -= ToastTimer_Tick;
        _toastTimer.Tick += ToastTimer_Tick;
        _toastTimer.Start();
    }

    private void ToastTimer_Tick(object? sender, object e)
    {
        _toastTimer?.Stop();
        ToastBorder.Opacity = 0;
    }

    private void TrackRow_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: TrackItem t } fe) return;
        var flyout = new MenuFlyout();
        var play = new MenuFlyoutItem { Text = L.T("播放"), Icon = new FontIcon { Glyph = "" } };
        play.Click += (s, e) => TrackInvoked?.Invoke(_tracks.ToList(), t);
        flyout.Items.Add(play);
        if (Svc.SupportsMv)
        {
            var mv = new MenuFlyoutItem { Text = L.T("播放 MV"), Icon = new FontIcon { Glyph = "" }, IsEnabled = t.MvId != "0" };
            if (!mv.IsEnabled) ToolTipService.SetToolTip(mv, L.T("这首歌没有 MV"));
            mv.Click += (s, e) => MvRequested?.Invoke(t);
            flyout.Items.Add(mv);
        }
        var save = new MenuFlyoutItem { Text = L.T("保存到曲库"), Icon = new FontIcon { Glyph = "" } };
        ToolTipService.SetToolTip(save, L.T("下载到设置里指定的文件夹（默认“音乐\\Klang”），写入标签、封面和歌词，之后不依赖缓存和会员也能播放"));
        save.Click += (s, e) => SaveRequested?.Invoke(t);
        flyout.Items.Add(save);
        if (args.TryGetPosition(fe, out var pt)) flyout.ShowAt(fe, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = pt });
        else flyout.ShowAt(fe);
        args.Handled = true;
    }

    /// <summary>高亮正在播放的云端曲目。</summary>
    public void SetCurrentTrack(TrackItem? track)
    {
        if (track != null && _tracks.Contains(track)) TracksList.SelectedItem = track;
        else TracksList.SelectedItem = null;
    }

    private void ShowBusy(string text, bool spinning = false)
    {
        // 加载中且列表还是空的：显示骨架屏；列表里已有内容（刷新）时仍用转圈，避免盖住内容
        bool listEmpty = _inTracksMode ? _tracks.Count == 0 : _entries.Count == 0;
        if (spinning && listEmpty)
        {
            // 首页模式下列表上方还有账号信息那一行，骨架屏往下让出位置
            Skeleton.Margin = new Thickness(0, _inTracksMode ? 0 : 52, 0, 0);
            Skeleton.Visibility = Visibility.Visible;
            BusyRing.IsActive = false;
            BusyPanel.Visibility = Visibility.Collapsed;
            return;
        }
        Skeleton.Visibility = Visibility.Collapsed;
        BusyText.Text = text;
        BusyRing.IsActive = spinning;
        BusyPanel.Visibility = Visibility.Visible;
    }

    private void HideBusy()
    {
        Skeleton.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = false;
        BusyPanel.Visibility = Visibility.Collapsed;
    }

    #endregion
}
