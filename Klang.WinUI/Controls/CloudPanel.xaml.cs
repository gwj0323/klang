using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Klang_WinUI.Models;
using Klang_WinUI.Services;
using Klang_WinUI.Services.Netease;

namespace Klang_WinUI.Controls;

/// <summary>“云端”首页中的一项（每日推荐 / 用户歌单）。</summary>
public sealed class CloudEntry
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Glyph { get; init; } = "";
    public long PlaylistId { get; init; }
    public bool IsDaily { get; init; }
}

/// <summary>网易云面板：扫码 / Cookie 登录、每日推荐、我的歌单、搜索结果。</summary>
public sealed partial class CloudPanel : UserControl
{
    private readonly ObservableCollection<CloudEntry> _entries = new();
    private readonly ObservableCollection<TrackItem> _tracks = new();
    private readonly DispatcherQueueTimer _qrTimer;
    private string _qrKey = "";
    private bool _qrBusy;
    private bool _active;
    private bool _loadingQuality = true;
    private int _requestSeq;
    private bool _inTracksMode;

    /// <summary>用户点播：参数为点播时的整个列表（作为播放队列）和被点的曲目。</summary>
    public event Action<List<TrackItem>, TrackItem>? TrackInvoked;

    private NeteaseService Svc => NeteaseService.Instance;

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

        foreach (var (key, label) in NeteaseService.Qualities)
            QualityBox.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        QualityBox.SelectedItem = QualityBox.Items.OfType<ComboBoxItem>()
            .First(i => (string)i.Tag == Svc.PreferredQuality);
        _loadingQuality = false;

        _qrTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _qrTimer.Interval = TimeSpan.FromSeconds(2);
        _qrTimer.Tick += async (s, e) => await PollQrAsync();

        Svc.LoginChanged += () => DispatcherQueue.TryEnqueue(UpdateView);
        Svc.CoverCached += t => DispatcherQueue.TryEnqueue(() => CoverReady?.Invoke(t));
        UpdateView();
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
        LoginView.Visibility = (!logged && !_inTracksMode) ? Visibility.Visible : Visibility.Collapsed;
        HomeView.Visibility = (logged && !_inTracksMode) ? Visibility.Visible : Visibility.Collapsed;
        TracksList.Visibility = _inTracksMode ? Visibility.Visible : Visibility.Collapsed;
        TracksHeader.Visibility = _inTracksMode ? Visibility.Visible : Visibility.Collapsed;

        if (logged)
        {
            _qrTimer.Stop();
            NicknameText.Text = Svc.Nickname;
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
            var (key, png) = await Svc.CreateQrAsync();
            if (key.Length == 0)
            {
                _qrKey = "";
                QrImage.Source = null;
                QrStatusText.Text = L.T("获取二维码失败，检查网络后点“刷新二维码”");
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
            QrStatusText.Text = L.T("用网易云音乐 App 扫码登录");
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
            var (code, msg) = await Svc.PollQrAsync(_qrKey);
            switch (code)
            {
                case 801: QrStatusText.Text = L.T("等待扫码…"); break;
                case 802: QrStatusText.Text = L.T("已扫码，请在手机上确认"); break;
                case 800:
                    _qrTimer.Stop();
                    _qrKey = "";
                    QrStatusText.Text = L.T("二维码已过期，正在刷新…");
                    _qrBusy = false;
                    await RefreshQrAsync();
                    return;
                case 803:
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
        var (ok, msg) = await Svc.LoginWithCookieAsync(CookieBox.Text);
        CookieStatusText.Text = msg;
        CookieLoginBtn.IsEnabled = true;
        if (ok) CookieBox.Text = "";
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        Svc.Logout();
        _qrKey = "";
        _inTracksMode = false;
        UpdateView();
    }

    private void QualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingQuality || QualityBox.SelectedItem is not ComboBoxItem it || it.Tag is not string key) return;
        Svc.PreferredQuality = key;
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
            var playlists = await Svc.GetUserPlaylistsAsync();
            _entries.Clear();
            _entries.Add(new CloudEntry { Title = L.T("每日推荐"), Subtitle = "", Glyph = "", IsDaily = true });
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
            if (list.Count == 0) ShowBusy(L.T("没有内容（可能没登录，或没有结果）"));
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

    /// <summary>高亮正在播放的云端曲目。</summary>
    public void SetCurrentTrack(TrackItem? track)
    {
        if (track != null && _tracks.Contains(track)) TracksList.SelectedItem = track;
        else TracksList.SelectedItem = null;
    }

    private void ShowBusy(string text, bool spinning = false)
    {
        BusyText.Text = text;
        BusyRing.IsActive = spinning;
        BusyPanel.Visibility = Visibility.Visible;
    }

    private void HideBusy()
    {
        BusyRing.IsActive = false;
        BusyPanel.Visibility = Visibility.Collapsed;
    }

    #endregion
}
