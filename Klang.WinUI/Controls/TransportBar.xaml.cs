using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    public event EventHandler? LyricsClicked;
    public event EventHandler<double>? UserSeekRequested;
    public event EventHandler<float>? UserVolumeChanged;

    private bool _isPlaying = false;
    private bool _isMuted = false;
    private bool _isUserDragging = false;
    private double _previousVolume = 85;

    public TransportBar()
    {
        InitializeComponent();
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

    public void UpdateTrackInfo(string title, string artist, string badge = "FLAC 24bit", string? coverPath = null)
    {
        TrackTitleText.Text = string.IsNullOrWhiteSpace(title) ? "未播放曲目" : title;
        ArtistText.Text = string.IsNullOrWhiteSpace(artist) ? "Klang" : artist;
        BadgeFormat.Text = badge;

        if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
        {
            try
            {
                CoverImage.Source = new BitmapImage(new Uri(coverPath));
                CoverImage.Visibility = Visibility.Visible;
                FallbackCoverIcon.Visibility = Visibility.Collapsed;
            }
            catch
            {
                ShowFallbackIcon();
            }
        }
        else
        {
            ShowFallbackIcon();
        }
    }

    private void ShowFallbackIcon()
    {
        CoverImage.Source = null;
        CoverImage.Visibility = Visibility.Collapsed;
        FallbackCoverIcon.Visibility = Visibility.Visible;
    }

    public void UpdateProgress(double currentSec, double totalSec)
    {
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
        PlayPauseClicked?.Invoke(this, EventArgs.Empty);
    }

    private void PrevBtn_Click(object sender, RoutedEventArgs e)
    {
        PrevClicked?.Invoke(this, EventArgs.Empty);
    }

    private void NextBtn_Click(object sender, RoutedEventArgs e)
    {
        NextClicked?.Invoke(this, EventArgs.Empty);
    }

    private void ShuffleBtn_Click(object sender, RoutedEventArgs e)
    {
        ShuffleClicked?.Invoke(this, EventArgs.Empty);
    }

    private void RepeatBtn_Click(object sender, RoutedEventArgs e)
    {
        RepeatClicked?.Invoke(this, EventArgs.Empty);
    }

    private void LyricsBtn_Click(object sender, RoutedEventArgs e)
    {
        LyricsClicked?.Invoke(this, EventArgs.Empty);
    }

    private void EqBtn_Click(object sender, RoutedEventArgs e)
    {
        EqualizerClicked?.Invoke(this, EventArgs.Empty);
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

    private void TimelineSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // 如果是用户点击跳转
        if (Math.Abs(e.NewValue - e.OldValue) > 1.5)
        {
            UserSeekRequested?.Invoke(this, e.NewValue);
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        float vol = (float)(VolumeSlider.Value / 100.0);
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
}
