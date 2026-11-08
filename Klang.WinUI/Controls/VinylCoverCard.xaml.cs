using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Klang_WinUI.Models;

namespace Klang_WinUI.Controls;

public sealed partial class VinylCoverCard : UserControl
{
    private DispatcherTimer? _spectrumTimer;
    private readonly Random _rand = new();

    public VinylCoverCard()
    {
        InitializeComponent();
        InitSpectrumAnimation();
    }

    private void InitSpectrumAnimation()
    {
        _spectrumTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _spectrumTimer.Tick += (s, e) =>
        {
            for (int i = 0; i < SpectrumGrid.Children.Count; i++)
            {
                if (SpectrumGrid.Children[i] is Border bar)
                {
                    bar.Height = _rand.Next(4, 22);
                }
            }
        };
    }

    public void SetPlaying(bool isPlaying)
    {
        if (isPlaying)
        {
            _spectrumTimer?.Start();
        }
        else
        {
            _spectrumTimer?.Stop();
        }
    }

    public void UpdateTrack(TrackItem? track)
    {
        if (track == null)
        {
            TitleText.Text = "未播放曲目";
            ArtistText.Text = "Klang - 沉浸式声学播放器";
            AlbumText.Text = "Studio Workspace";
            QualityBadgeText.Text = "WASAPI · 0ms";
            ShowFallbackCover();
            return;
        }

        TitleText.Text = string.IsNullOrWhiteSpace(track.Title) ? "未知曲目" : track.Title;
        ArtistText.Text = string.IsNullOrWhiteSpace(track.Artist) ? "未知歌手" : track.Artist;
        AlbumText.Text = string.IsNullOrWhiteSpace(track.Album) ? "未知专辑" : track.Album;
        QualityBadgeText.Text = track.FormatBadge;

        if (!string.IsNullOrEmpty(track.CoverPath) && File.Exists(track.CoverPath))
        {
            try
            {
                CoverImage.Source = new BitmapImage(new Uri(track.CoverPath));
                CoverImage.Visibility = Visibility.Visible;
                FallbackIcon.Visibility = Visibility.Collapsed;
            }
            catch
            {
                ShowFallbackCover();
            }
        }
        else
        {
            ShowFallbackCover();
        }
    }

    private void ShowFallbackCover()
    {
        CoverImage.Source = null;
        CoverImage.Visibility = Visibility.Collapsed;
        FallbackIcon.Visibility = Visibility.Visible;
    }
}
