using System;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Klang_WinUI.Services;
using Klang_WinUI.Services.Ai;

namespace Klang_WinUI.Controls;

/// <summary>设置 → AI 功能：总开关、计算设备、后台分析、各组件的下载 / 删除、K 歌缓存。</summary>
public sealed class AiSettingsPanel : StackPanel
{
    private static AppState State => StateService.Instance.State;
    private readonly ToggleSwitch _enable = new() { OnContent = L.T("开"), OffContent = L.T("关") };
    private readonly ComboBox _device = new() { Width = 180, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _deviceStatus = Small("");
    private readonly ToggleSwitch _bg = new() { OnContent = L.T("开"), OffContent = L.T("关") };
    private readonly TextBlock _bgStatus = Small("");
    private readonly StackPanel _packs = new() { Spacing = 8 };
    private readonly TextBlock _cacheInfo = Small("");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _loading = true;
    private bool _probing, _probeFailed;

    public AiSettingsPanel()
    {
        Spacing = 8;
        Margin = new Thickness(0, 0, 12, 12);
        Children.Add(new TextBlock { Text = L.T("AI 功能"), FontSize = 20, FontWeight = FontWeights.SemiBold });

        Children.Add(Card(L.T("启用 AI 功能"),
            L.T("全部在本机运行，不上传数据。"), _enable));

        _device.Items.Add(new ComboBoxItem { Content = L.T("自动（优先用显卡）"), Tag = "auto" });
        _device.Items.Add(new ComboBoxItem { Content = L.T("仅用 CPU"), Tag = "cpu" });
        _device.SelectedIndex = State.AiDevice == "cpu" ? 1 : 0;
        var devText = new StackPanel { Spacing = 2, Children = { Title(L.T("计算设备")), Small(L.T("有显卡时用显卡，更快。")), _deviceStatus } };
        Children.Add(CardRaw(devText, _device));

        Children.Add(CardRaw(new StackPanel { Spacing = 2, Children = { Title(L.T("在后台分析曲库")), Small(L.T("空闲时分析曲库，AI 找歌要用到。")), _bgStatus } }, _bg));

        Children.Add(new TextBlock { Text = L.T("组件"), Style = GroupStyle });
        Children.Add(_packs);

        var clear = new Button { Content = L.T("清空"), VerticalAlignment = VerticalAlignment.Center };
        clear.Click += (s, e) => { AiAudioService.ClearCache(); Refresh(); };
        Children.Add(CardRaw(new StackPanel { Spacing = 2, Children = { Title(L.T("伴奏 / 人声缓存")), Small(L.T("K 歌时分离出的音频，超过 2 GB 自动清理。")), _cacheInfo } }, clear));

        Children.Add(Small(L.T("均为开源模型，从官方地址下载。")));

        _enable.IsOn = State.AiEnabled;
        _bg.IsOn = State.AiBackgroundIndex;
        _enable.Toggled += (s, e) => { if (!_loading) { AiService.Instance.SetEnabled(_enable.IsOn); Refresh(); } };
        _device.SelectionChanged += (s, e) =>
        {
            if (_loading || (_device.SelectedItem as ComboBoxItem)?.Tag is not string tag) return;
            AiService.Instance.SetDevice(tag);
            Refresh();
        };
        _bg.Toggled += (s, e) =>
        {
            if (_loading) return;
            State.AiBackgroundIndex = _bg.IsOn;
            StateService.Instance.RequestSave();
            if (_bg.IsOn) AiIndexService.Instance.Kick();
        };

        AiService.Instance.Changed += OnChanged;
        _timer.Tick += (s, e) => RefreshStatus();
        _timer.Start();
        _loading = false;
        Refresh();
    }

    public void Detach()
    {
        AiService.Instance.Changed -= OnChanged;
        _timer.Stop();
    }

    private void OnChanged() => DispatcherQueue.TryEnqueue(() => { _probeFailed = false; Refresh(); });

    // 颜色都用 ThemeResource 的样式：直接取 Application.Current.Resources 拿到的是应用主题的画刷，
    // 和设置对话框实际的深 / 浅色不一致（之前卡片在深色下是浅灰的）
    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";
    private static Style S(string xaml) => (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    private static readonly Style CardStyle = S($"<Style {Ns} TargetType=\"Border\"><Setter Property=\"Padding\" Value=\"14\"/><Setter Property=\"CornerRadius\" Value=\"8\"/><Setter Property=\"BorderThickness\" Value=\"1\"/><Setter Property=\"Background\" Value=\"{{ThemeResource CardBackgroundFillColorDefaultBrush}}\"/><Setter Property=\"BorderBrush\" Value=\"{{ThemeResource CardStrokeColorDefaultBrush}}\"/></Style>");
    private static readonly Style SmallStyle = S($"<Style {Ns} TargetType=\"TextBlock\"><Setter Property=\"FontSize\" Value=\"12\"/><Setter Property=\"TextWrapping\" Value=\"Wrap\"/><Setter Property=\"Foreground\" Value=\"{{ThemeResource TextFillColorSecondaryBrush}}\"/></Style>");
    private static readonly Style AccentSmallStyle = S($"<Style {Ns} TargetType=\"TextBlock\"><Setter Property=\"FontSize\" Value=\"12\"/><Setter Property=\"Foreground\" Value=\"{{ThemeResource AccentTextFillColorPrimaryBrush}}\"/></Style>");
    private static readonly Style ErrorStyle = S($"<Style {Ns} TargetType=\"TextBlock\"><Setter Property=\"FontSize\" Value=\"12\"/><Setter Property=\"TextWrapping\" Value=\"Wrap\"/><Setter Property=\"Foreground\" Value=\"{{ThemeResource SystemFillColorCriticalBrush}}\"/></Style>");
    private static readonly Style GroupStyle = S($"<Style {Ns} TargetType=\"TextBlock\"><Setter Property=\"FontSize\" Value=\"12\"/><Setter Property=\"FontWeight\" Value=\"SemiBold\"/><Setter Property=\"Margin\" Value=\"2,8,0,0\"/><Setter Property=\"Foreground\" Value=\"{{ThemeResource TextFillColorSecondaryBrush}}\"/></Style>");
    private static TextBlock Small(string text) => new() { Text = text, Style = SmallStyle };
    private static TextBlock Title(string text) => new() { Text = text, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };

    private static Border Card(string title, string desc, FrameworkElement right) =>
        CardRaw(new StackPanel { Spacing = 2, Children = { Title(title), Small(desc) } }, right);

    private static Border CardRaw(FrameworkElement left, FrameworkElement right)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(left);
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return new Border { Style = CardStyle, Child = g };
    }

    private static string Mb(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB" : $"{bytes / 1024.0 / 1024:0} MB";

    private void Refresh()
    {
        var ai = AiService.Instance;
        _device.IsEnabled = _bg.IsEnabled = ai.Enabled;
        _packs.Opacity = ai.Enabled ? 1 : 0.55;
        _packs.Children.Clear();
        foreach (var pack in AiCatalog.All) _packs.Children.Add(PackCard(pack));
        RefreshStatus();
    }

    private Border PackCard(AiPack pack)
    {
        var ai = AiService.Instance;
        bool installed = AiCatalog.IsInstalled(pack);
        bool downloading = ai.IsDownloading(pack.Id);
        var left = new StackPanel { Spacing = 3 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(Title(pack.Name));
        if (installed) head.Children.Add(new TextBlock { Text = "✓ " + L.T("已安装"), Style = AccentSmallStyle, VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(head);
        left.Children.Add(Small(pack.Description));
        long need = AiCatalog.Required(pack, AiService.GpuClass).Where(f => !System.IO.File.Exists(AiCatalog.PathOf(f))).Sum(f => f.Size);
        left.Children.Add(Small(installed ? L.F("已占用 {0}", Mb(AiCatalog.InstalledBytes(pack))) : L.F("约 {0}", Mb(need))));
        ToolTipService.SetToolTip(left, L.F("许可：{0}", pack.License));
        if (downloading)
        {
            ai.DownloadProgress.TryGetValue(pack.Id, out var p);
            left.Children.Add(new ProgressBar { Value = p * 100, Maximum = 100, Margin = new Thickness(0, 4, 0, 0), IsIndeterminate = p <= 0 });
            left.Children.Add(Small(p > 0 ? L.F("正在下载… {0:0}%", p * 100) : L.T("正在连接…")));
        }
        if (ai.DownloadErrors.TryGetValue(pack.Id, out var err))
            left.Children.Add(new TextBlock { Text = L.T("下载失败：") + err, Style = ErrorStyle });

        // 有显卡、但装的是 CPU 版（之前没检测到显卡，或者先选了仅 CPU）：提示可以换显卡版
        bool upgrade = installed && !downloading && need > 0 && AiService.GpuClass;
        if (upgrade) left.Children.Add(Small(L.F("当前是 CPU 版，可下载显卡版（约 {0}）。", Mb(need))));

        Button btn;
        if (upgrade)
        {
            btn = new Button { Content = L.T("下载显卡版"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            btn.Click += (s, e) => { _ = ai.DownloadAsync(pack); Refresh(); };
        }
        else if (downloading)
        {
            btn = new Button { Content = L.T("取消") };
            btn.Click += (s, e) => ai.CancelDownload(pack.Id);
        }
        else if (installed)
        {
            btn = new Button { Content = L.T("删除") };
            bool armed = false;
            btn.Click += (s, e) =>
            {
                if (!armed) { armed = true; btn.Content = L.T("再点一次确认删除"); return; }
                ai.Remove(pack);
                Refresh();
            };
        }
        else
        {
            btn = new Button { Content = L.T("下载"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], IsEnabled = ai.Enabled };
            btn.Click += (s, e) => { _ = ai.DownloadAsync(pack); Refresh(); };
        }
        return CardRaw(left, btn);
    }

    private void RefreshStatus()
    {
        var ai = AiService.Instance;
        // 下载进度条
        foreach (var pack in AiCatalog.All)
        {
            if (!ai.IsDownloading(pack.Id)) continue;
            int i = AiCatalog.All.ToList().IndexOf(pack);
            if (i < _packs.Children.Count && _packs.Children[i] is Border { Child: Grid g } && g.Children[0] is StackPanel sp)
            {
                ai.DownloadProgress.TryGetValue(pack.Id, out var p);
                foreach (var c in sp.Children)
                {
                    if (c is ProgressBar pb) { pb.IsIndeterminate = p <= 0; pb.Value = p * 100; }
                }
                if (sp.Children.LastOrDefault(c => c is TextBlock t && t.FontSize == 12) is TextBlock last && p > 0 && sp.Children.OfType<ProgressBar>().Any())
                    last.Text = L.F("正在下载… {0:0}%", p * 100);
            }
        }

        // 设备
        if (!AiCatalog.IsInstalled(AiCatalog.Runtime)) _deviceStatus.Text = L.T("下载任意组件后可用。");
        else if (!ai.Enabled) _deviceStatus.Text = "";
        else if (ai.LastInfo is { } info)
        {
            _deviceStatus.Text = !info.Loaded ? L.T("推理引擎加载失败：") + info.Error
                : info.Gpu ? L.F("正在使用：{0}", info.Adapter)
                : L.T("正在使用：CPU") + (State.AiDevice == "cpu" ? "" : L.T("（没有可用的显卡）"));
        }
        else if (_probeFailed) { }
        else if (!_probing)
        {
            _probing = true;
            _deviceStatus.Text = L.T("正在检测…");
            _ = Probe();
        }

        // 后台分析
        var lib = LibraryService.Instance.Tracks.ToList();
        var idx = AiIndexService.Instance;
        if (!ai.Enabled) _bgStatus.Text = "";
        else
        {
            var parts = new System.Collections.Generic.List<string>();
            if (AiCatalog.IsInstalled(AiCatalog.Clap)) parts.Add(L.F("听感 {0} / {1} 首", idx.AudioCount(lib), lib.Count(AiIndexService.Indexable)));
            if (AiCatalog.IsInstalled(AiCatalog.TextEmbed)) parts.Add(L.F("歌词索引 {0} 首", idx.LyricsCount(lib)));
            _bgStatus.Text = idx.Status.Length > 0 ? idx.Status : string.Join(" · ", parts);
        }
        _cacheInfo.Text = L.F("已用 {0}", Mb(AiAudioService.CacheBytes()));
    }

    private async System.Threading.Tasks.Task Probe()
    {
        try { await AiService.Instance.GetInfoAsync(); }
        catch (Exception ex) { _probeFailed = true; _deviceStatus.Text = L.T("推理引擎加载失败：") + ex.Message; }
        finally { _probing = false; }
    }
}
