using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Klang_WinUI.Services;

namespace Klang_WinUI.Dialogs;

public sealed partial class SettingsDialog : ContentDialog
{
    public event Action<string>? BackdropChanged;
    public event Action<string>? ThemeChanged;
    public event Action<int>? LyricFontSizeChanged;
    /// <summary>曲库内容被设置页改变（重新扫描 / 清空）。</summary>
    public event Action? LibraryChanged;

    private readonly bool _loading = true;
    private bool _clearArmed;

    private static AppState State => StateService.Instance.State;

    public SettingsDialog()
    {
        InitializeComponent();

        // 语言：跟随系统 + Languages 文件夹里的每一门
        LanguageComboBox.Items.Add(new ComboBoxItem { Content = L.T("跟随系统"), Tag = "auto" });
        foreach (var lang in L.Available())
            LanguageComboBox.Items.Add(new ComboBoxItem { Content = lang.Name, Tag = lang.Code });
        SelectByTag(LanguageComboBox, State.Language, "auto");

        SelectByTag(ThemeComboBox, State.Theme, "system");
        SelectByTag(BackdropComboBox, State.BackdropMaterial, "mica");
        SelectByTag(LatencyComboBox, State.WasapiLatencyMs.ToString(), "50");
        SelectByTag(AutoMixComboBox, State.AutoMix, "roam");
        SelectByTag(AutoMixLengthComboBox, State.AutoMixLength, "normal");
        // 输出设备列表
        DeviceComboBox.Items.Add(new ComboBoxItem { Content = L.T("系统默认"), Tag = "default" });
        foreach (var d in AudioEngineService.GetOutputDevices())
            DeviceComboBox.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
        SelectByTag(DeviceComboBox, State.SelectedDeviceId, "default");
        ReplayGainSwitch.IsOn = State.ReplayGainEnabled;
        ExclusiveSwitch.IsOn = State.WasapiExclusive;
        BypassSwitch.IsOn = State.BypassDsp;
        LyricZoomSwitch.IsOn = State.LyricZoomEmphasis;
        LyricBlurSwitch.IsOn = State.LyricDepthBlur;
        WaveformSwitch.IsOn = State.WaveformSeekBar;
        VinylDiscSwitch.IsOn = State.ShowVinylDisc;
        LyricWordMotionSwitch.IsOn = State.LyricWordMotion;
        AutoMetaSwitch.IsOn = State.AutoFetchMetadata;
        SelectByTag(CacheLimitBox, State.NeteaseCacheLimitMb.ToString(), "2048");
        RefreshCacheInfo();

        // 睡眠定时：显示剩余时间
        var sleepEnd = AudioEngineService.Instance.SleepTimerEnd;
        if (sleepEnd.HasValue)
        {
            int remain = (int)Math.Ceiling((sleepEnd.Value - DateTime.UtcNow).TotalMinutes);
            SleepStatusText.Text = L.F("约 {0} 分钟后暂停播放", Math.Max(1, remain));
            SleepComboBox.SelectedIndex = -1;
        }
        else
        {
            SleepComboBox.SelectedIndex = 0;
        }

        LyricSizeSlider.Value = Math.Clamp(State.LyricFontSize, 14, 40);
        LyricSizeText.Text = ((int)LyricSizeSlider.Value).ToString();
        StatePathText.Text = L.T("配置文件：") + Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klang", "player_state.json");
        RebuildFolderList();
        AppVersionText.Text = L.F("版本 {0}（WinUI 3）", UpdateService.CurrentVersionText);

        _loading = false;

        L.Apply(this);
        Loaded += (s, e) => L.Apply(this);   // 模板展开后（导航栏等）再翻译一遍
    }

    private void ExclusiveSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        State.WasapiExclusive = ExclusiveSwitch.IsOn;
        AudioEngineService.Instance.ExclusiveMode = ExclusiveSwitch.IsOn;
        StateService.Instance.RequestSave();
        AudioEngineService.Instance.ReopenOutput();
    }

    private void BypassSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        State.BypassDsp = BypassSwitch.IsOn;
        AudioEngineService.Instance.BypassDsp = BypassSwitch.IsOn;   // 立即生效
        StateService.Instance.RequestSave();
    }

    private void LyricZoomSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        State.LyricZoomEmphasis = LyricZoomSwitch.IsOn;
        StateService.Instance.RequestSave();
        Controls.LyricsStageControl.NotifyEmphasisChanged();
    }

    private void VinylDiscSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        State.ShowVinylDisc = VinylDiscSwitch.IsOn;
        StateService.Instance.RequestSave();
        Controls.VinylCoverCard.NotifyVinylDiscSettingChanged();
    }

    private void WaveformSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        State.WaveformSeekBar = WaveformSwitch.IsOn;
        StateService.Instance.RequestSave();
        Controls.TransportBar.NotifyWaveformModeChanged();
    }

    private void LyricBlurSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        State.LyricDepthBlur = LyricBlurSwitch.IsOn;
        StateService.Instance.RequestSave();
        Controls.LyricsStageControl.NotifyEmphasisChanged();
    }

    private void LyricWordMotionSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        State.LyricWordMotion = LyricWordMotionSwitch.IsOn;
        StateService.Instance.RequestSave();
        Controls.LyricsStageControl.NotifyEmphasisChanged();
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(LanguageComboBox) is not string tag) return;
        L.SetLanguage(tag);          // 保存 + 通知各窗口重新翻译
        L.Apply(this);               // 设置对话框自己也立刻刷新
        // 语言下拉里“跟随系统”这一项是代码生成的，单独刷新
        if (LanguageComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == "auto") is { } auto)
            auto.Content = L.T("跟随系统");
    }

    #region 分类切换

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        PageAppearance.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        PagePlayback.Visibility = tag == "playback" ? Visibility.Visible : Visibility.Collapsed;
        PageLibrary.Visibility = tag == "library" ? Visibility.Visible : Visibility.Collapsed;
        PageNetwork.Visibility = tag == "network" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion

    #region 关于 / 检查更新

    private UpdateInfo? _update;

    private async void CheckUpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateRing.Visibility = Visibility.Visible;
        UpdateRing.IsActive = true;
        UpdateResultPanel.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = L.T("正在检查…");
        try
        {
            _update = await UpdateService.CheckAsync();
            if (_update.Latest == null)
            {
                UpdateStatusText.Text = L.T("GitHub 上未找到 WinUI 版本的发布。");
            }
            else if (_update.HasUpdate)
            {
                UpdateStatusText.Text = L.F("发现新版本 {0}（当前 {1}）", _update.Latest.ToString(3), UpdateService.CurrentVersionText);
                UpdateTitleText.Text = string.IsNullOrWhiteSpace(_update.Name) ? _update.Tag : _update.Name;
                UpdateNotesText.Text = string.IsNullOrWhiteSpace(_update.Notes) ? L.T("（发布说明为空）") : _update.Notes.Trim();
                DownloadUpdateBtn.IsEnabled = UpdateService.IsTrustedUrl(_update.DownloadUrl);
                UpdateResultPanel.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateStatusText.Text = L.F("已是最新版本（{0}）", UpdateService.CurrentVersionText);
            }
        }
        catch (TaskCanceledException)
        {
            UpdateStatusText.Text = L.T("连接 GitHub 超时，请检查网络后重试。");
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = L.T("检查失败：") + ex.Message;
        }
        finally
        {
            UpdateRing.IsActive = false;
            UpdateRing.Visibility = Visibility.Collapsed;
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    private static async void OpenTrusted(string? url)
    {
        if (!UpdateService.IsTrustedUrl(url)) return;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(url!)); } catch { }
    }

    private void DownloadUpdateBtn_Click(object sender, RoutedEventArgs e) => OpenTrusted(_update?.DownloadUrl);

    private void ReleasePageBtn_Click(object sender, RoutedEventArgs e) => OpenTrusted(_update?.PageUrl);

    #endregion

    private static void SelectByTag(ComboBox box, string tag, string fallback)
    {
        var items = box.Items.OfType<ComboBoxItem>().ToList();
        var match = items.FirstOrDefault(i => (string)i.Tag == tag) ?? items.First(i => (string)i.Tag == fallback);
        box.SelectedItem = match;
    }

    private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(ThemeComboBox) is not string tag) return;
        State.Theme = tag;
        StateService.Instance.RequestSave();
        ThemeChanged?.Invoke(tag);
    }

    private void BackdropComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(BackdropComboBox) is not string tag) return;
        State.BackdropMaterial = tag;
        StateService.Instance.RequestSave();
        BackdropChanged?.Invoke(tag);
    }

    private void LyricSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (LyricSizeText == null) return;
        int size = (int)Math.Round(e.NewValue);
        LyricSizeText.Text = size.ToString();
        if (_loading) return;
        State.LyricFontSize = size;
        StateService.Instance.RequestSave();
        LyricFontSizeChanged?.Invoke(size);
    }

    private void LatencyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(LatencyComboBox) is not string tag || !int.TryParse(tag, out var ms)) return;
        State.WasapiLatencyMs = ms;
        AudioEngineService.Instance.LatencyMs = ms;
        StateService.Instance.RequestSave();
        AudioEngineService.Instance.ReopenOutput();
    }

    private void AutoMixLengthComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(AutoMixLengthComboBox) is not string len) return;
        State.AutoMixLength = len;
        StateService.Instance.RequestSave();
    }

    private void AutoMixComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(AutoMixComboBox) is not string mode) return;
        State.AutoMix = mode;
        StateService.Instance.RequestSave();
        if (mode != "off") RhythmAnalysisService.Instance.Enqueue(LibraryService.Instance.Tracks);
    }

    private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(DeviceComboBox) is not string id) return;
        State.SelectedDeviceId = id;
        AudioEngineService.Instance.OutputDeviceId = id;
        StateService.Instance.RequestSave();
        AudioEngineService.Instance.ReopenOutput();
    }

    private void ReplayGainSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        State.ReplayGainEnabled = ReplayGainSwitch.IsOn;
        AudioEngineService.Instance.SetReplayGainEnabled(ReplayGainSwitch.IsOn);
        StateService.Instance.RequestSave();
    }

    private void SleepComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(SleepComboBox) is not string tag || !int.TryParse(tag, out var minutes)) return;
        AudioEngineService.Instance.SetSleepTimer(minutes);
        SleepStatusText.Text = minutes > 0 ? L.F("{0} 分钟后暂停播放", minutes) : L.T("到时自动暂停播放");
    }

    private void AutoMetaSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        State.AutoFetchMetadata = AutoMetaSwitch.IsOn;
        StateService.Instance.RequestSave();
        if (AutoMetaSwitch.IsOn) MetadataService.Instance.Enqueue(LibraryService.Instance.Tracks);
    }

    #region 网易云缓存

    private static string FormatBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.##} GB" :
        b >= 1L << 20 ? $"{b / (double)(1L << 20):0.#} MB" : $"{b / 1024.0:0} KB";

    private void RefreshCacheInfo()
    {
        var (count, bytes) = Services.Netease.NeteaseService.Instance.GetCacheInfo();
        CacheInfoText.Text = L.F("已缓存 {0} 首，占用 {1}", count, FormatBytes(bytes));
    }

    private void CacheLimitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(CacheLimitBox) is not string tag || !int.TryParse(tag, out var mb)) return;
        State.NeteaseCacheLimitMb = mb;
        StateService.Instance.RequestSave();
    }

    private void ClearCacheBtn_Click(object sender, RoutedEventArgs e)
    {
        var (n, bytes) = Services.Netease.NeteaseService.Instance.ClearCache();
        RefreshCacheInfo();
        CacheInfoText.Text += L.F("（已清理 {0} 个文件，释放 {1}）", n, FormatBytes(bytes));
    }

    #endregion

    #region 曲库管理

    private void RebuildFolderList()
    {
        FoldersPanel.Children.Clear();
        if (State.WatchedFolders.Count == 0)
        {
            FoldersPanel.Children.Add(new TextBlock
            {
                Text = L.T("（尚未添加文件夹）"),
                FontSize = 12,
                Opacity = 0.6
            });
            return;
        }

        foreach (var folder in State.WatchedFolders.ToList())
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var path = new TextBlock
            {
                Text = folder,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTipService.SetToolTip(path, folder);
            row.Children.Add(path);

            var remove = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 11 },
                Padding = new Thickness(6, 3, 6, 3)
            };
            ToolTipService.SetToolTip(remove, L.T("停止监视此文件夹（已导入的歌曲将保留）"));
            var captured = folder;
            remove.Click += (s, e) =>
            {
                LibraryService.Instance.RemoveWatchedFolder(captured);
                RebuildFolderList();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);

            FoldersPanel.Children.Add(row);
        }
    }

    private async void RescanBtn_Click(object sender, RoutedEventArgs e)
    {
        RescanBtn.IsEnabled = false;
        LibraryStatusText.Text = L.T("正在扫描…");
        try
        {
            int added = await LibraryService.Instance.RescanWatchedFoldersAsync();
            LibraryStatusText.Text = added > 0 ? L.F("扫描完成，新增 {0} 首", added) : L.T("扫描完成，未发现新歌曲");
            if (added > 0) LibraryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            LibraryStatusText.Text = L.T("扫描失败：") + ex.Message;
        }
        RescanBtn.IsEnabled = true;
    }

    private void ClearLibraryBtn_Click(object sender, RoutedEventArgs e)
    {
        // 二次确认，避免误触清空曲库
        if (!_clearArmed)
        {
            _clearArmed = true;
            ClearLibraryBtn.Content = L.T("再次点击以确认清空");
            LibraryStatusText.Text = L.T("将清空曲库和文件夹列表，不会删除磁盘上的音乐文件");
            return;
        }

        _clearArmed = false;
        ClearLibraryBtn.Content = L.T("清空曲库");
        LibraryService.Instance.ClearLibrary();
        RebuildFolderList();
        LibraryStatusText.Text = L.T("曲库已清空");
        LibraryChanged?.Invoke();
    }

    #endregion
}
