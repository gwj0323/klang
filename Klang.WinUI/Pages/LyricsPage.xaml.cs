using System;
using System.Collections.ObjectModel;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Pages;

public sealed partial class LyricsPage : Page
{
    public ObservableCollection<LyricLine> Lyrics { get; } = new();

    private int _lastActiveIndex = -2;
    private TrackItem? _currentTrack;

    public LyricsPage()
    {
        InitializeComponent();
        LyricsItemsControl.ItemsSource = Lyrics;
    }

    public void SetCurrentTrack(TrackItem? track)
    {
        _currentTrack = track;
        _lastActiveIndex = -2;

        if (track == null)
        {
            StageTitleText.Text = "未播放曲目";
            StageArtistText.Text = "Klang - 沉浸式听音空间";
            StageBadgeText.Text = "WASAPI · 0ms";
            ShowFallbackCover();
            LoadDefaultLyrics();
            return;
        }

        StageTitleText.Text = string.IsNullOrWhiteSpace(track.Title) ? "未知曲目" : track.Title;
        StageArtistText.Text = $"{track.Artist} · {track.Album}";
        StageBadgeText.Text = track.FormatBadge;

        // 封面图显示
        if (!string.IsNullOrEmpty(track.CoverPath) && File.Exists(track.CoverPath))
        {
            try
            {
                StageCoverImage.Source = new BitmapImage(new Uri(track.CoverPath));
                StageCoverImage.Visibility = Visibility.Visible;
                StageFallbackIcon.Visibility = Visibility.Collapsed;
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

        // 关联 LRC 歌词与内嵌歌词
        var loaded = LyricsService.Instance.LoadLyrics(track.FilePath);
        Lyrics.Clear();
        foreach (var l in loaded)
        {
            Lyrics.Add(l);
        }

        // 重置滚动位置到顶部
        LyricsScrollViewer.ChangeView(null, 0, null, true);
    }

    private void ShowFallbackCover()
    {
        StageCoverImage.Source = null;
        StageCoverImage.Visibility = Visibility.Collapsed;
        StageFallbackIcon.Visibility = Visibility.Visible;
    }

    private void LoadDefaultLyrics()
    {
        Lyrics.Clear();
        var placeholder = LyricsService.Instance.LoadLyrics(null);
        foreach (var l in placeholder)
        {
            Lyrics.Add(l);
        }
    }

    public void UpdatePlaybackTime(double currentTimeSeconds)
    {
        if (Lyrics.Count == 0) return;

        int activeIndex = LyricsService.Instance.FindActiveIndex(Lyrics, currentTimeSeconds);
        if (activeIndex == _lastActiveIndex) return;

        _lastActiveIndex = activeIndex;

        // 更新所有歌词行的状态、缩放与渐隐透明度
        for (int i = 0; i < Lyrics.Count; i++)
        {
            var line = Lyrics[i];
            bool isActive = (i == activeIndex);
            line.IsActive = isActive;
            line.DistanceFromActive = Math.Abs(i - activeIndex);
        }

        // 丝滑居中滚动
        if (activeIndex >= 0)
        {
            ScrollToActiveLine(activeIndex);
        }
    }

    private void ScrollToActiveLine(int index)
    {
        if (index < 0 || index >= Lyrics.Count) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (LyricsItemsControl.ContainerFromIndex(index) is FrameworkElement container)
            {
                try
                {
                    var transform = container.TransformToVisual(LyricsScrollViewer);
                    var point = transform.TransformPoint(new Point(0, 0));
                    double viewportH = LyricsScrollViewer.ActualHeight;

                    if (viewportH > 0)
                    {
                        double deltaY = point.Y - (viewportH / 2.0) + (container.ActualHeight / 2.0);
                        double targetOffset = LyricsScrollViewer.VerticalOffset + deltaY;

                        // false 表示原生启用顺滑滚动插值动画 (Spring / Cubic Easing)
                        LyricsScrollViewer.ChangeView(null, targetOffset, null, false);
                    }
                }
                catch
                {
                    // 容器处于布局重绘阶段时的保护
                }
            }
        });
    }

    private void LyricLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is LyricLine line)
        {
            // 点击歌词行瞬间，毫秒级跳播到此时刻
            AudioEngineService.Instance.Seek(line.Time);
        }
    }
}
