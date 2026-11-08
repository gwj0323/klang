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
    public event EventHandler<double>? UserSeekRequested;
    public event EventHandler<float>? UserVolumeChanged;

    private bool _isPlaying = false;
    private bool _isMuted = false;
    private bool _isUserDragging = false;
    private double _previousVolume = 85;

    private DispatcherTimer? _waveTimer;
    private readonly Random _rand = new();

    public TransportBar()
    {
        InitializeComponent();
        InitWaveTimer();
    }

    private void InitWaveTimer()
    {
        _waveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _waveTimer.Tick += (s, e) =>
        {
            MiniWave1.Height = _rand.Next(4, 14);
            MiniWave2.Height = _rand.Next(8, 20);
            MiniWave3.Height = _rand.Next(6, 18);
            MiniWave4.Height = _rand.Next(4, 12);
        };
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            _isPlaying = value;
            PlayPauseIcon.Glyph = _isPlaying ? "\uE769" : "\uE768";
            PlayPauseBtn.SetValue(ToolTipService.ToolTipProperty, _isPlaying ? "暂停 (Space)" : "播放 (Space)");

            if (_isPlaying) _waveTimer?.Start();
            else _waveTimer?.Stop();
        }
    }

    public void UpdateLyricSnippet(string text)
    {
        LyricSnippetText.Text = string.IsNullOrWhiteSpace(text)
            ? "♪ 享受纯粹音乐沉浸 ♪"
            : text;
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
