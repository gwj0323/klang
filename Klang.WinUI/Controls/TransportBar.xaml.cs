using Klang_WinUI.Services;
using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Klang_WinUI.Controls;

public sealed partial class TransportBar : UserControl
{
    public event EventHandler? PlayPauseClicked;
    public event EventHandler? NextClicked;
    public event EventHandler? PrevClicked;
    public event EventHandler? ShuffleClicked;
    public event EventHandler? RepeatClicked;
    public event EventHandler? EqualizerClicked;
    public event EventHandler? RateClicked;
    public event EventHandler? FavoriteClicked;
    public event EventHandler<double>? UserSeekRequested;
    public event EventHandler<float>? UserVolumeChanged;

    private bool _isPlaying = false;
    private bool _isMuted = false;
    private bool _isUserDragging = false;
    private bool _isFavorite = false;
    private double _previousVolume = 85;
    private readonly DispatcherTimer _bounceTimer = new();

    public TransportBar()
    {
        InitializeComponent();
        ActualThemeChanged += (s, e) => RefreshIconColors();
        Loaded += (s, e) => RefreshIconColors();

        _bounceTimer.Interval = TimeSpan.FromMilliseconds(160);
        _bounceTimer.Tick += (s, e) =>
        {
            _bounceTimer.Stop();
            PlayPauseScale.ScaleX = 1.0;
            PlayPauseScale.ScaleY = 1.0;
        };

        // 核心修复：通过 handledEventsToo=true 挂载，彻底突破 Slider Thumb 吞噬 Pointer 事件的问题
        TimelineSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) =>
        {
            _isUserDragging = true;
        }), true);

        TimelineSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((s, e) =>
        {
            if (_isUserDragging)
            {
                _isUserDragging = false;
                UserSeekRequested?.Invoke(this, TimelineSlider.Value);
            }
        }), true);

        TimelineSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((s, e) =>
        {
            if (_isUserDragging)
            {
                _isUserDragging = false;
                UserSeekRequested?.Invoke(this, TimelineSlider.Value);
            }
        }), true);

        // 悬停：波形上把“从当前位置到鼠标处”的部分提亮，预示点击后跳到哪里
        TimelineSlider.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((s, e) =>
        {
            double w = Waveform.ActualWidth;
            if (w <= 0) return;
            double x = e.GetCurrentPoint(Waveform).Position.X;
            Waveform.SetHover(Math.Clamp(x / w, 0, 1));
        }), true);
        TimelineSlider.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((s, e) => Waveform.SetHover(-1)), true);

        ApplyWaveformMode();
        void OnModeChanged() => DispatcherQueue.SafeEnqueue(ApplyWaveformMode);
        WaveformModeChanged += OnModeChanged;
        Unloaded += (s, e) => WaveformModeChanged -= OnModeChanged;
        Loaded += (s, e) => { WaveformModeChanged -= OnModeChanged; WaveformModeChanged += OnModeChanged; ApplyWaveformMode(); };
    }

    /// <summary>设置当前曲目的波形（null = 暂无，显示为细轨道）。</summary>
    public void SetWaveform(float[]? data) => Waveform.SetData(data);

    /// <summary>设置里切换“波形进度条”后通知所有底栏（以及负责加载波形的主窗口）。</summary>
    public static event Action? WaveformModeChanged;
    public static void NotifyWaveformModeChanged() => WaveformModeChanged?.Invoke();

    /// <summary>开：显示波形，上面的 Slider 透明只负责操作；关：标准滑块。</summary>
    private void ApplyWaveformMode()
    {
        bool on = StateService.Instance.State.WaveformSeekBar;
        Waveform.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TimelineSlider.Opacity = on ? 0 : 1;
        if (!on) Waveform.SetHover(-1);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            _isPlaying = value;
            PlayPauseIcon.IsPlaying = _isPlaying;
            PlayPauseBtn.SetValue(ToolTipService.ToolTipProperty, _isPlaying ? L.T("暂停 (Space)") : L.T("播放 (Space)"));
        }
    }

    private string? _lastTitle, _lastCover;

    public void UpdateTrackState(string? title, string? artist, string? coverPath = null)
    {
        string t = string.IsNullOrWhiteSpace(title) ? L.T("未加载曲目") : title;
        string a = string.IsNullOrWhiteSpace(artist) ? "Klang" : artist;
        // 换歌：标题与歌手上移淡出后换成新内容再上浮淡入；只是信息补全时直接替换
        if (_lastTitle != null && _lastTitle != t && IsLoaded)
        {
            int snip = ++_snippetSeq;
            UiAnimations.FadeThrough(TrackTextPanel, () =>
            {
                StatusCaptionText.Text = t;
                // 淡出期间副标题已被更新（比如“正在获取云端音源…”）就不再覆盖
                if (snip == _snippetSeq) LyricSnippetText.Text = a;
            }, 120, 340, 6f);
        }
        else { StatusCaptionText.Text = t; LyricSnippetText.Text = a; }
        _lastTitle = t;

        bool coverChanged = coverPath != _lastCover;
        _lastCover = coverPath;

        if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
        {
            try
            {
                MiniCoverImage.Source = new BitmapImage(new Uri(coverPath));
                MiniCoverImage.Visibility = Visibility.Visible;
                MiniCoverFallback.Visibility = Visibility.Collapsed;
                if (coverChanged && IsLoaded) UiAnimations.FadeIn(MiniCoverImage, 420);
            }
            catch
            {
                MiniCoverImage.Visibility = Visibility.Collapsed;
                MiniCoverFallback.Visibility = Visibility.Visible;
            }
        }
        else
        {
            MiniCoverImage.Visibility = Visibility.Collapsed;
            MiniCoverFallback.Visibility = Visibility.Visible;
        }
    }

    private int _snippetSeq;

    public void UpdateLyricSnippet(string text)
    {
        _snippetSeq++;
        LyricSnippetText.Text = string.IsNullOrWhiteSpace(text) ? "Klang" : text;
    }

    public void UpdateEqBadge(string presetName)
    {
        EqBadgeText.Text = string.IsNullOrWhiteSpace(presetName) ? L.T("音效") : presetName;
    }

    public void UpdateProgress(double currentSec, double totalSec)
    {
        // 用户拖拽时坚决屏蔽计时器的倒灌覆盖
        if (_isUserDragging) return;

        CurrentTimeText.Text = FormatTime(currentSec);
        TotalTimeText.Text = FormatTime(totalSec);

        if (totalSec > 0)
        {
            TimelineSlider.Maximum = totalSec;
            TimelineSlider.Value = Math.Min(currentSec, totalSec);
            Waveform.SetProgress(currentSec / totalSec);
        }
        else
        {
            TimelineSlider.Maximum = 100;
            TimelineSlider.Value = 0;
            Waveform.SetProgress(0);
        }
    }

    /// <summary>以 0–1 设置音量滑块（会触发 UserVolumeChanged）。</summary>
    public void SetVolume(float volume01)
    {
        VolumeSlider.Value = Math.Round(Math.Clamp(volume01, 0f, 1f) * 100);
    }

    public double VolumePercent => VolumeSlider.Value;

    public void SetPlaybackRate(double rate)
    {
        RateText.Text = $"{rate:0.0#}x";
    }

    /// <summary>三种“下一首”方式：列表顺序 / 随机 / 漫游（本地学习猜下一首）。</summary>
    public void SetShuffleState(string playMode)
    {
        bool roam = playMode == "roam";
        ShuffleIcon.Glyph = roam ? "" : "";   // 漫游用地球图标，其余用随机图标
        string tip = roam ? L.T("漫游：根据收听历史选择下一首（点击切换为顺序播放）")
            : playMode == "random" ? L.T("随机播放（点击切换为漫游）") : L.T("随机播放 / 漫游");
        ToolTipService.SetToolTip(ShuffleBtn, tip);
        AutomationProperties.SetName(ShuffleBtn, tip);
        SetShuffleState(playMode is "random" or "roam");
    }

    private void SignalBtn_Click(object sender, RoutedEventArgs e)
    {
        var info = AudioEngineService.Instance.GetSignalPath();
        var flyout = new Flyout { Content = Dialogs.SignalPathPanel.Build(info, this) };
        flyout.Opened += (s, a) => L.Apply(flyout.Content);
        flyout.ShowAt(SignalBtn);
    }

    private bool _shuffleOn;
    private bool _repeatSingle;

    public void SetShuffleState(bool isShuffle)
    {
        _shuffleOn = isShuffle;
        RefreshIconColors();
    }

    public void SetRepeatMode(string mode)
    {
        _repeatSingle = mode == "loop_single";
        RepeatIcon.Glyph = _repeatSingle ? "" : "";
        RepeatBtn.SetValue(ToolTipService.ToolTipProperty, _repeatSingle ? L.T("循环模式：单曲循环") : L.T("循环模式：列表循环"));
        RefreshIconColors();
    }

    public void SetFavorite(bool isFav)
    {
        _isFavorite = isFav;
        FavoriteIcon.Glyph = isFav ? "" : "";
        RefreshIconColors();
    }

    /// <summary>
    /// 随机 / 循环 / 喜欢三个图标的颜色（开 = 强调色，关 = 次要文字色）。
    /// 颜色要按控件“当前实际主题”取，所以主题变化后（比如启动时设置里强制了深色）必须重新套一遍。
    /// </summary>
    private void RefreshIconColors()
    {
        var off = ThemeService.Brush(this, "TextFillColorSecondaryBrush");
        var on = ThemeService.AccentText(this);
        ShuffleIcon.Foreground = _shuffleOn ? on : off;
        RepeatIcon.Foreground = _repeatSingle ? on : off;
        FavoriteIcon.Foreground = _isFavorite ? on : off;
    }
    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) return "00:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? ts.ToString(@"hh\:mm\:ss")
            : ts.ToString(@"mm\:ss");
    }

    private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        PlayPauseScale.ScaleX = 0.88;
        PlayPauseScale.ScaleY = 0.88;
        _bounceTimer.Start();

        PlayPauseClicked?.Invoke(this, EventArgs.Empty);
    }

    private void PrevBtn_Click(object sender, RoutedEventArgs e) => PrevClicked?.Invoke(this, EventArgs.Empty);
    private void NextBtn_Click(object sender, RoutedEventArgs e) => NextClicked?.Invoke(this, EventArgs.Empty);
    private void ShuffleBtn_Click(object sender, RoutedEventArgs e)
    {
        ShuffleClicked?.Invoke(this, EventArgs.Empty);
        UiAnimations.SlideIn(ShuffleIcon);          // 图标已换成新模式的：从左侧滑入
    }

    private void RepeatBtn_Click(object sender, RoutedEventArgs e)
    {
        RepeatClicked?.Invoke(this, EventArgs.Empty);
        UiAnimations.Spin(RepeatIcon);              // 循环图标转一圈
    }
    private void RateBtn_Click(object sender, RoutedEventArgs e) => RateClicked?.Invoke(this, EventArgs.Empty);
    private void EqBtn_Click(object sender, RoutedEventArgs e) => EqualizerClicked?.Invoke(this, EventArgs.Empty);

    private void FavoriteBtn_Click(object sender, RoutedEventArgs e)
    {
        SetFavorite(!_isFavorite);
        // 收藏：红心从小弹到略大再落回；取消收藏：轻微收缩一下
        if (_isFavorite) UiAnimations.Bounce(FavoriteIcon);
        else UiAnimations.Bounce(FavoriteIcon, 1f, 0.82f, 260);
        FavoriteClicked?.Invoke(this, EventArgs.Empty);
    }

    private void VolumeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isMuted)
        {
            _isMuted = false;
            VolumeSlider.Value = _previousVolume > 0 ? _previousVolume : 80;
            VolumeIcon.Glyph = "\uE767";
        }
        else
        {
            _previousVolume = VolumeSlider.Value;
            _isMuted = true;
            VolumeSlider.Value = 0;
            VolumeIcon.Glyph = "\uE74F";
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        float vol = (float)(VolumeSlider.Value / 100.0);
        int pct = (int)VolumeSlider.Value;
        if (VolumePctText != null)
        {
            VolumePctText.Text = $"{pct}%";
        }

        if (VolumeSlider.Value == 0)
        {
            VolumeIcon.Glyph = "\uE74F";
            _isMuted = true;
        }
        else if (VolumeSlider.Value < 33)
        {
            VolumeIcon.Glyph = "\uE992";
            _isMuted = false;
        }
        else if (VolumeSlider.Value < 66)
        {
            VolumeIcon.Glyph = "\uE993";
            _isMuted = false;
        }
        else
        {
            VolumeIcon.Glyph = "\uE767";
            _isMuted = false;
        }

        UserVolumeChanged?.Invoke(this, vol);
    }

    private void TimelineSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUserDragging)
        {
            CurrentTimeText.Text = FormatTime(e.NewValue);
            if (TimelineSlider.Maximum > 0) Waveform.SetProgress(e.NewValue / TimelineSlider.Maximum);
        }
    }
}

/// <summary>进度滑块的悬浮提示：把秒数显示成 mm:ss。</summary>
public sealed class TimeToolTipConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, System.Convert.ToDouble(value)));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}