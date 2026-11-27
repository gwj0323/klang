using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Klang_WinUI.Services;
using Klang_WinUI.Services.Ai;

namespace Klang_WinUI.Controls;

/// <summary>
/// 设置 → AI 功能：总开关；“组件”一张卡片（四个功能各一行，顶上一键全部下载 / 换显卡版）；
/// “选项”一张卡片（计算设备、后台分析、伴奏缓存）。推理引擎不单独列出，下载任意组件时自动装上。
/// </summary>
public sealed class AiSettingsPanel : StackPanel
{
    private static AppState State => StateService.Instance.State;
    private static readonly AiPack[] Features = AiCatalog.All.Where(p => p != AiCatalog.Runtime).ToArray();

    private readonly ToggleSwitch _enable = new() { OnContent = L.T("开"), OffContent = L.T("关") };
    private readonly ComboBox _device = new() { Width = 180, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _deviceStatus = Small("");
    private readonly ToggleSwitch _bg = new() { OnContent = L.T("开"), OffContent = L.T("关") };
    private readonly TextBlock _bgStatus = Small("");
    private readonly TextBlock _cacheInfo = Small("");
    private readonly StackPanel _packs = new();
    private readonly Border _optionsCard;
    private readonly Dictionary<string, (ProgressBar Bar, TextBlock Text)> _progress = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _loading = true;
    private bool _probing, _probeFailed;
    private bool _batch;   // 正在一个接一个地下载（全部下载 / 换显卡版）

    public AiSettingsPanel()
    {
        Spacing = 8;
        Margin = new Thickness(0, 0, 12, 12);
        Children.Add(new TextBlock { Text = L.T("AI 功能"), FontSize = 20, FontWeight = FontWeights.SemiBold });

        Children.Add(new Border
        {
            Style = CardStyle,
            Child = Row(new StackPanel { Spacing = 2, Children = { Title(L.T("启用 AI 功能")), Small(L.T("全部在本机运行，不上传数据。")) } }, _enable),
        });

        Children.Add(new TextBlock { Text = L.T("组件"), Style = GroupStyle });
        Children.Add(new Border { Style = CardStyle, Padding = new Thickness(0), Child = _packs });

        _device.Items.Add(new ComboBoxItem { Content = L.T("自动（优先用显卡）"), Tag = "auto" });
        _device.Items.Add(new ComboBoxItem { Content = L.T("仅用 CPU"), Tag = "cpu" });
        _device.SelectedIndex = State.AiDevice == "cpu" ? 1 : 0;
        if (State.AiDevice == "npu") EnsureNpuItem("");
        var clear = new Button { Content = L.T("清空") };
        clear.Click += (s, e) => { AiAudioService.ClearCache(); RefreshStatus(); };

        Children.Add(new TextBlock { Text = L.T("选项"), Style = GroupStyle });
        var opts = new StackPanel();
        opts.Children.Add(Pad(Row(new StackPanel { Spacing = 2, Children = { Title(L.T("计算设备")), Small(L.T("有显卡时用显卡，更快。")), _deviceStatus } }, _device)));
        opts.Children.Add(Divider());
        opts.Children.Add(Pad(Row(new StackPanel { Spacing = 2, Children = { Title(L.T("在后台分析曲库")), Small(L.T("空闲时分析曲库，AI 找歌和高潮连播要用到。")), _bgStatus } }, _bg)));
        opts.Children.Add(Divider());
        opts.Children.Add(Pad(Row(new StackPanel { Spacing = 2, Children = { Title(L.T("伴奏 / 人声缓存")), Small(L.T("K 歌时分离出的音频，超过 2 GB 自动清理。")), _cacheInfo } }, clear)));
        _optionsCard = new Border { Style = CardStyle, Padding = new Thickness(0), Child = opts };
        Children.Add(_optionsCard);

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
    private static readonly Style DividerStyle = S($"<Style {Ns} TargetType=\"Border\"><Setter Property=\"Height\" Value=\"1\"/><Setter Property=\"Background\" Value=\"{{ThemeResource CardStrokeColorDefaultBrush}}\"/></Style>");
    private static TextBlock Small(string text) => new() { Text = text, Style = SmallStyle };
    private static TextBlock Title(string text) => new() { Text = text, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };
    private static Border Divider() => new() { Style = DividerStyle };
    private static Border Pad(FrameworkElement e) => new() { Padding = new Thickness(14, 12, 14, 12), Child = e };

    private static Grid Row(FrameworkElement left, FrameworkElement right)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(left);
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    private static string Mb(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB" : $"{bytes / 1024.0 / 1024:0} MB";

    /// <summary>这个组件还要下载多少（含推理引擎，只算一次）。</summary>
    private static long Need(AiPack pack) =>
        AiCatalog.Required(pack, AiService.GpuClass).Where(f => !System.IO.File.Exists(AiCatalog.PathOf(f))).Sum(f => f.Size);

    private static long RuntimeNeed() => AiCatalog.IsInstalled(AiCatalog.Runtime) ? 0 : Need(AiCatalog.Runtime);

    private void Refresh()
    {
        var ai = AiService.Instance;
        _device.IsEnabled = _bg.IsEnabled = ai.Enabled;
        _optionsCard.Opacity = _packs.Opacity = ai.Enabled ? 1 : 0.55;
        _packs.Children.Clear();
        _progress.Clear();
        _packs.Children.Add(Pad(Header()));
        foreach (var pack in Features)
        {
            _packs.Children.Add(Divider());
            _packs.Children.Add(Pad(PackRow(pack)));
        }
        RefreshStatus();
    }

    /// <summary>组件卡片的第一行：装了几个、占多少，右边一个按钮（全部下载 / 换显卡版）。</summary>
    private FrameworkElement Header()
    {
        var ai = AiService.Instance;
        int installed = Features.Count(AiCatalog.IsInstalled);
        long used = AiCatalog.All.Sum(AiCatalog.InstalledBytes);
        var missing = Features.Where(p => !AiCatalog.IsInstalled(p)).ToList();
        // 有显卡、但装的是 CPU 版：可以换成显卡版
        var upgrade = AiService.GpuClass ? Features.Where(p => AiCatalog.IsInstalled(p) && Need(p) > 0).ToList() : new();
        bool busy = _batch || Features.Any(p => ai.IsDownloading(p.Id));

        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(Title(L.F("已安装 {0} / {1}", installed, Features.Length)));
        left.Children.Add(Small(used > 0 ? L.F("共占用 {0}。", Mb(used)) : L.T("下载要用的功能，不用的可以不装。")));

        FrameworkElement right;
        if (upgrade.Count > 0 && !busy)
        {
            long size = upgrade.Sum(Need);
            var b = new Button { Content = L.F("换成显卡版（约 {0}）", Mb(size)), Style = (Style)Application.Current.Resources["AccentButtonStyle"], IsEnabled = ai.Enabled };
            ToolTipService.SetToolTip(b, L.T("当前是 CPU 版，换成显卡版会快很多。"));
            b.Click += (s, e) => DownloadAll(upgrade);
            right = b;
        }
        else if (missing.Count > 0 && !busy)
        {
            long size = missing.Sum(Need) + RuntimeNeed();
            var b = new Button { Content = L.F("全部下载（约 {0}）", Mb(size)), IsEnabled = ai.Enabled };
            b.Click += (s, e) => DownloadAll(missing);
            right = b;
        }
        else right = new Border();
        return Row(left, right);
    }

    private async void DownloadAll(List<AiPack> packs)
    {
        // 一个接一个下：同时下会重复下载推理引擎
        _batch = true;
        Refresh();
        try
        {
            foreach (var p in packs)
            {
                if (!AiService.Instance.Enabled) break;
                await AiService.Instance.DownloadAsync(p);
                if (AiService.Instance.DownloadErrors.ContainsKey(p.Id)) break;
            }
        }
        finally { _batch = false; Refresh(); }
    }

    private FrameworkElement PackRow(AiPack pack)
    {
        var ai = AiService.Instance;
        bool installed = AiCatalog.IsInstalled(pack);
        bool downloading = ai.IsDownloading(pack.Id);
        long need = Need(pack);

        var left = new StackPanel { Spacing = 3 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(Title(pack.Name));
        if (installed) head.Children.Add(new TextBlock { Text = "✓ " + L.T("已安装"), Style = AccentSmallStyle, VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(head);
        string size = installed ? L.F("已占用 {0}", Mb(AiCatalog.InstalledBytes(pack))) : L.F("约 {0}", Mb(need));
        left.Children.Add(Small(pack.Description + "  " + size));
        ToolTipService.SetToolTip(left, L.F("许可：{0}", pack.License));
        if (downloading)
        {
            ai.DownloadProgress.TryGetValue(pack.Id, out var p);
            var bar = new ProgressBar { Value = p * 100, Maximum = 100, Margin = new Thickness(0, 4, 0, 0), IsIndeterminate = p <= 0 };
            var txt = Small(p > 0 ? L.F("正在下载… {0:0}%", p * 100) : L.T("正在连接…"));
            left.Children.Add(bar);
            left.Children.Add(txt);
            _progress[pack.Id] = (bar, txt);
        }
        if (ai.DownloadErrors.TryGetValue(pack.Id, out var err))
            left.Children.Add(new TextBlock { Text = L.T("下载失败：") + err, Style = ErrorStyle });

        Button btn;
        if (downloading)
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
                // 一个功能都不剩了：推理引擎也删掉
                if (!Features.Any(AiCatalog.AnyFile) && AiCatalog.AnyFile(AiCatalog.Runtime)) ai.Remove(AiCatalog.Runtime);
                Refresh();
            };
        }
        else
        {
            btn = new Button { Content = L.T("下载"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], IsEnabled = ai.Enabled && !_batch };
            btn.Click += (s, e) => { _ = ai.DownloadAsync(pack); Refresh(); };
        }
        btn.MinWidth = 72;
        return Row(left, btn);
    }

    private void RefreshStatus()
    {
        var ai = AiService.Instance;
        foreach (var (id, (bar, txt)) in _progress)
        {
            if (!ai.DownloadProgress.TryGetValue(id, out var p)) continue;
            bar.IsIndeterminate = p <= 0;
            bar.Value = p * 100;
            if (p > 0) txt.Text = L.F("正在下载… {0:0}%", p * 100);
        }

        // 设备
        if (!AiCatalog.IsInstalled(AiCatalog.Runtime)) _deviceStatus.Text = L.T("下载任意组件后可用。");
        else if (!ai.Enabled) _deviceStatus.Text = "";
        else if (ai.LastInfo is { } info)
        {
            EnsureNpuItem(info.Npu);
            string gpu = !info.Loaded ? L.T("推理引擎加载失败：") + info.Error
                : info.Gpu ? L.F("正在使用：{0}", info.Adapter)
                : L.T("正在使用：CPU") + (State.AiDevice == "cpu" ? "" : L.T("（没有可用的显卡）"));
            string npu = info.Npu.Length == 0 || State.AiDevice == "cpu" ? ""
                : State.AiDevice != "npu" ? L.F("检测到 NPU：{0}", info.Npu)
                : info.NpuEp.Length > 0 ? L.F("后台分析：{0}", info.NpuEp)
                : info.NpuError.Length > 0 ? L.T("NPU 不可用，改用显卡或 CPU：") + info.NpuError
                : L.F("后台分析会用 NPU：{0}（第一次用时下载执行器）", info.Npu);
            _deviceStatus.Text = npu.Length > 0 ? gpu + "\n" + npu : gpu;
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
            var parts = new List<string>();
            int total = lib.Count(AiIndexService.Indexable);
            if (AiCatalog.IsInstalled(AiCatalog.Clap))
            {
                parts.Add(L.F("听感 {0} / {1} 首", idx.AudioCount(lib), total));
                parts.Add(L.F("高潮 {0} / {1} 首", idx.HighlightCount(lib), lib.Count(t => AiIndexService.Indexable(t) && !t.IsCueTrack)));
            }
            if (AiCatalog.IsInstalled(AiCatalog.TextEmbed)) parts.Add(L.F("歌词索引 {0} 首", idx.LyricsCount(lib)));
            _bgStatus.Text = idx.Status.Length > 0 ? idx.Status : string.Join(" · ", parts);
        }
        _cacheInfo.Text = L.F("已用 {0}", Mb(AiAudioService.CacheBytes()));
    }

    /// <summary>检测到 NPU（或之前选过）时，计算设备里多一项“后台分析用 NPU”。</summary>
    private void EnsureNpuItem(string npu)
    {
        if (npu.Length == 0 && State.AiDevice != "npu") return;
        if (_device.Items.OfType<ComboBoxItem>().Any(i => (string)i.Tag == "npu")) return;
        bool was = _loading;
        _loading = true;
        var item = new ComboBoxItem { Content = L.T("后台分析用 NPU"), Tag = "npu" };
        ToolTipService.SetToolTip(item, L.T("听感分析和歌词搜索放到 NPU 上，省电、不占显卡；人声分离和歌词识别仍用显卡。第一次使用时 Windows 会下载对应的 NPU 执行器。"));
        _device.Items.Insert(1, item);
        if (State.AiDevice == "npu") _device.SelectedItem = item;
        _loading = was;
    }

    private async System.Threading.Tasks.Task Probe()
    {
        try { await AiService.Instance.GetInfoAsync(); }
        catch (Exception ex) { _probeFailed = true; _deviceStatus.Text = L.T("推理引擎加载失败：") + ex.Message; }
        finally { _probing = false; }
    }
}
