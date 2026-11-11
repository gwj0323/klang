using Klang_WinUI.Services;
using System;
using System.IO;
using Microsoft.UI.Xaml;
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
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            _isPlaying = value;
            PlayPauseIcon.Glyph = _isPlaying ? "\uE769" : "\uE768";
            PlayPauseBtn.SetValue(ToolTipService.ToolTipProperty, _isPlaying ? "暂停 (Space)" : "播放 (Space)");
        }
    }

    public void UpdateTrackState(string? title, string? artist, string? coverPath = null)
    {
        StatusCaptionText.Text = string.IsNullOrWhiteSpace(title) ? "未加载曲目" : title;
        LyricSnippetText.Text = string.IsNullOrWhiteSpace(artist) ? "Klang" : artist;

        if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
        {
            try
            {
                MiniCoverImage.Source = new BitmapImage(new Uri(coverPath));
                MiniCoverImage.Visibility = Visibility.Visible;
                MiniCoverFallback.Visibility = Visibility.Collapsed;
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

    public void UpdateLyricSnippet(string text)
    {
        LyricSnippetText.Text = string.IsNullOrWhiteSpace(text) ? "Klang" : text;
    }

    public void UpdateEqBadge(string presetName)
    {
        EqBadgeText.Text = string.IsNullOrWhiteSpace(presetName) ? "音效" : presetName;
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
        }
        else
        {
            TimelineSlider.Maximum = 100;
            TimelineSlider.Value = 0;
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

    public void SetShuffleState(bool isShuffle)
    {
        ShuffleIcon.Foreground = isShuffle
            ? (ThemeService.AccentText(this))
            : (ThemeService.Brush(this, "TextFillColorSecondaryBrush"));
    }

    public void SetRepeatMode(string mode)
    {
        if (mode == "loop_single")
        {
            RepeatIcon.Glyph = "\uE8ED";
            RepeatBtn.SetValue(ToolTipService.ToolTipProperty, "循环模式：单曲循环");
            RepeatIcon.Foreground = ThemeService.AccentText(this);
        }
        else
        {
            RepeatIcon.Glyph = "\uE8EE";
            RepeatBtn.SetValue(ToolTipService.ToolTipProperty, "循环模式：列表循环");
            RepeatIcon.Foreground = ThemeService.Brush(this, "TextFillColorSecondaryBrush");
        }
    }

    public void SetFavorite(bool isFav)
    {
        _isFavorite = isFav;
        FavoriteIcon.Glyph = isFav ? "\uEB52" : "\uEB51";
        FavoriteIcon.Foreground = isFav
            ? (ThemeService.AccentText(this))
            : (ThemeService.Brush(this, "TextFillColorSecondaryBrush"));
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
    private void ShuffleBtn_Click(object sender, RoutedEventArgs e) => ShuffleClicked?.Invoke(this, EventArgs.Empty);
    private void RepeatBtn_Click(object sender, RoutedEventArgs e) => RepeatClicked?.Invoke(this, EventArgs.Empty);
    private void RateBtn_Click(object sender, RoutedEventArgs e) => RateClicked?.Invoke(this, EventArgs.Empty);
    private void EqBtn_Click(object sender, RoutedEventArgs e) => EqualizerClicked?.Invoke(this, EventArgs.Empty);

    private void FavoriteBtn_Click(object sender, RoutedEventArgs e)
    {
        SetFavorite(!_isFavorite);
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