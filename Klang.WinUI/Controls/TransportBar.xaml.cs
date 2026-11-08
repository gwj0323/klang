using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Klang_WinUI.Controls;

public sealed partial class TransportBar : UserControl
{
    public event EventHandler? PlayPauseClicked;
    public event EventHandler? NextClicked;
    public event EventHandler? PrevClicked;
    public event EventHandler? ShuffleClicked;
    public event EventHandler? RepeatClicked;
    public event EventHandler? EqualizerClicked;
    public event RangeBaseValueChangedEventHandler? SeekChanged;
    public event RangeBaseValueChangedEventHandler? VolumeChanged;

    private bool _isPlaying = false;
    private bool _isMuted = false;
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
            PlayPauseIcon.Glyph = _isPlaying ? "\uE769" : "\uE768"; // Pause vs Play glyph
            PlayPauseBtn.SetValue(ToolTipService.ToolTipProperty, _isPlaying ? "暂停 (Space)" : "播放 (Space)");
        }
    }

    public void UpdateTrackInfo(string title, string artist, string badge = "FLAC 24bit")
    {
        TrackTitleText.Text = string.IsNullOrWhiteSpace(title) ? "未播放曲目" : title;
        ArtistText.Text = string.IsNullOrWhiteSpace(artist) ? "Klang" : artist;
        BadgeFormat.Text = badge;
    }

    public void UpdateProgress(double currentSec, double totalSec)
    {
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
        IsPlaying = !IsPlaying;
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
            VolumeIcon.Glyph = "\uE74F"; // Mute glyph
        }
    }

    private void TimelineSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        SeekChanged?.Invoke(this, e);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (VolumeSlider.Value == 0)
        {
            VolumeIcon.Glyph = "\uE74F"; // Mute
            _isMuted = true;
        }
        else if (VolumeSlider.Value < 33)
        {
            VolumeIcon.Glyph = "\uE992"; // Low volume
            _isMuted = false;
        }
        else if (VolumeSlider.Value < 66)
        {
            VolumeIcon.Glyph = "\uE993"; // Med volume
            _isMuted = false;
        }
        else
        {
            VolumeIcon.Glyph = "\uE767"; // High volume
            _isMuted = false;
        }

        VolumeChanged?.Invoke(this, e);
    }
}
