using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Controls;

public sealed partial class LyricsStageControl : UserControl
{
    public ObservableCollection<LyricLine> Lyrics { get; } = new();

    private int _lastActiveIndex = -2;
    public event EventHandler<double>? SeekRequested;

    public LyricsStageControl()
    {
        InitializeComponent();
        LyricsItemsControl.ItemsSource = Lyrics;
    }

    public void LoadLyrics(string? audioFilePath)
    {
        _lastActiveIndex = -2;
        Lyrics.Clear();

        var loaded = LyricsService.Instance.LoadLyrics(audioFilePath);
        foreach (var l in loaded)
        {
            Lyrics.Add(l);
        }

        LyricsScrollViewer.ChangeView(null, 0, null, true);
    }

    public void UpdatePlaybackTime(double currentTimeSeconds)
    {
        if (Lyrics.Count == 0) return;

        int activeIndex = LyricsService.Instance.FindActiveIndex(Lyrics, currentTimeSeconds);
        if (activeIndex == _lastActiveIndex) return;

        _lastActiveIndex = activeIndex;

        for (int i = 0; i < Lyrics.Count; i++)
        {
            var line = Lyrics[i];
            line.IsActive = (i == activeIndex);
            line.DistanceFromActive = Math.Abs(i - activeIndex);
        }

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

                        LyricsScrollViewer.ChangeView(null, targetOffset, null, false);
                    }
                }
                catch
                {
                    // 布局重绘安全保护
                }
            }
        });
    }

    private void LyricLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is LyricLine line)
        {
            SeekRequested?.Invoke(this, line.Time);
        }
    }
}
