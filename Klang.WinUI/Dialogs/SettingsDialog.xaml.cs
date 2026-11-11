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

        SelectByTag(ThemeComboBox, State.Theme, "system");
        SelectByTag(BackdropComboBox, State.BackdropMaterial, "mica");
        SelectByTag(LatencyComboBox, State.WasapiLatencyMs.ToString(), "50");
        // 输出设备列表
        DeviceComboBox.Items.Add(new ComboBoxItem { Content = "系统默认", Tag = "default" });
        foreach (var d in AudioEngineService.GetOutputDevices())
            DeviceComboBox.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
        SelectByTag(DeviceComboBox, State.SelectedDeviceId, "default");
        ReplayGainSwitch.IsOn = State.ReplayGainEnabled;
        AutoMetaSwitch.IsOn = State.AutoFetchMetadata;
        SelectByTag(CacheLimitBox, State.NeteaseCacheLimitMb.ToString(), "2048");
        RefreshCacheInfo();

        // 睡眠定时：显示剩余时间
        var sleepEnd = AudioEngineService.Instance.SleepTimerEnd;
        if (sleepEnd.HasValue)
        {
            int remain = (int)Math.Ceiling((sleepEnd.Value - DateTime.UtcNow).TotalMinutes);
            SleepStatusText.Text = $"约 {Math.Max(1, remain)} 分钟后暂停播放";
            SleepComboBox.SelectedIndex = -1;
        }
        else
        {
            SleepComboBox.SelectedIndex = 0;
        }

        LyricSizeSlider.Value = Math.Clamp(State.LyricFontSize, 14, 40);
        LyricSizeText.Text = ((int)LyricSizeSlider.Value).ToString();
        StatePathText.Text = "配置文件：" + Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klang", "player_state.json");
        RebuildFolderList();

        _loading = false;
    }

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
    }

    private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TagOf(DeviceComboBox) is not string id) return;
        State.SelectedDeviceId = id;
        AudioEngineService.Instance.OutputDeviceId = id;
        StateService.Instance.RequestSave();
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
        SleepStatusText.Text = minutes > 0 ? $"{minutes} 分钟后暂停播放" : "到点自动暂停播放";
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
        CacheInfoText.Text = $"已缓存 {count} 首，占用 {FormatBytes(bytes)}";
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
        CacheInfoText.Text += $"（已清理 {n} 个文件，释放 {FormatBytes(bytes)}）";
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
                Text = "（还没有添加文件夹）",
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
            ToolTipService.SetToolTip(remove, "不再监视此文件夹（已导入的歌曲保留）");
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
        LibraryStatusText.Text = "正在扫描…";
        try
        {
            int added = await LibraryService.Instance.RescanWatchedFoldersAsync();
            LibraryStatusText.Text = added > 0 ? $"扫描完成，新增 {added} 首" : "扫描完成，没有新的歌曲";
            if (added > 0) LibraryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            LibraryStatusText.Text = "扫描失败：" + ex.Message;
        }
        RescanBtn.IsEnabled = true;
    }

    private void ClearLibraryBtn_Click(object sender, RoutedEventArgs e)
    {
        // 二次确认，避免误触清空曲库
        if (!_clearArmed)
        {
            _clearArmed = true;
            ClearLibraryBtn.Content = "再次点击确认清空";
            LibraryStatusText.Text = "将清空曲库与文件夹列表（不会删除磁盘上的音乐文件）";
            return;
        }

        _clearArmed = false;
        ClearLibraryBtn.Content = "清空曲库";
        LibraryService.Instance.ClearLibrary();
        RebuildFolderList();
        LibraryStatusText.Text = "曲库已清空";
        LibraryChanged?.Invoke();
    }

    #endregion
}
