using System;
using System.Collections.Generic;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.Wave;
using Windows.Graphics;
using Klang_WinUI.Dialogs;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI;

public sealed partial class MainWindow : Window
{
    private bool _isSidebarOpen = true;
    private TrackItem? _currentTrack;

    public MainWindow()
    {
        InitializeComponent();

        // 沉浸式标题栏集成
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 严格对标 Qt 版 1280 x 820 标准工作台尺寸
        AppWindow.Resize(new SizeInt32(1280, 820));

        // 1. 初始化音频引擎调度器
        AudioEngineService.Instance.InitializeDispatcher(this.DispatcherQueue);

        // 2. 监听音频引擎播放状态与进度
        AudioEngineService.Instance.PositionChanged += (current, total) =>
        {
            BottomTransportBar.UpdateProgress(current, total);
            StageLyricsControl.UpdatePlaybackTime(current);

            // 同步底栏单句微缩歌词 (Qt 原版特性)
            int activeIdx = LyricsService.Instance.FindActiveIndex(StageLyricsControl.Lyrics, current);
            if (activeIdx >= 0 && activeIdx < StageLyricsControl.Lyrics.Count)
            {
                BottomTransportBar.UpdateLyricSnippet(StageLyricsControl.Lyrics[activeIdx].Text);
            }
        };

        AudioEngineService.Instance.StateChanged += (state) =>
        {
            bool isPlaying = (state == PlaybackState.Playing);
            BottomTransportBar.IsPlaying = isPlaying;
            StageCoverCard.SetPlaying(isPlaying);
        };

        // 3. 绑定左侧曲库面板曲目选中事件
        LeftNavPane.TrackSelected += (s, track) =>
        {
            PlayTrack(track);
        };

        // 4. 绑定歌词点击跳播事件
        StageLyricsControl.SeekRequested += (s, timeSec) =>
        {
            AudioEngineService.Instance.Seek(timeSec);
        };

        // 5. 绑定底栏控制事件
        BottomTransportBar.PlayPauseClicked += (s, e) =>
        {
            AudioEngineService.Instance.TogglePlayPause();
        };

        BottomTransportBar.UserSeekRequested += (s, pos) =>
        {
            AudioEngineService.Instance.Seek(pos);
            StageLyricsControl.UpdatePlaybackTime(pos);
        };

        BottomTransportBar.UserVolumeChanged += (s, vol) =>
        {
            AudioEngineService.Instance.Volume = vol;
        };

        // 6. 默认初始化示例曲库（保证开箱即拥有饱满视觉）
        InitDefaultSampleTracks();
    }

    private void PlayTrack(TrackItem track)
    {
        _currentTrack = track;

        // 更新中间黑胶封面与音频规格
        StageCoverCard.UpdateTrack(track);

        // 更新右侧实时歌词
        StageLyricsControl.LoadLyrics(track.FilePath);

        // 更新底栏信息
        BottomTransportBar.UpdateLyricSnippet($"{track.Title} - {track.Artist}");

        // 开始音频播放
        if (!string.IsNullOrEmpty(track.FilePath))
        {
            AudioEngineService.Instance.Play(track.FilePath);
        }
    }

    private void InitDefaultSampleTracks()
    {
        var samples = new List<TrackItem>
        {
            new TrackItem
            {
                Index = 1,
                Title = "Arrival of the Birds",
                Artist = "The Cinematic Orchestra",
                Album = "The Crimson Wing",
                Duration = 162,
                FormatBadge = "FLAC 24bit"
            },
            new TrackItem
            {
                Index = 2,
                Title = "Experience",
                Artist = "Ludovico Einaudi",
                Album = "In a Time Lapse",
                Duration = 315,
                FormatBadge = "Hi-Res 96kHz"
            },
            new TrackItem
            {
                Index = 3,
                Title = "Cornfield Chase",
                Artist = "Hans Zimmer",
                Album = "Interstellar OST",
                Duration = 126,
                FormatBadge = "Master 192k"
            },
            new TrackItem
            {
                Index = 4,
                Title = "Nuvole Bianche",
                Artist = "Ludovico Einaudi",
                Album = "Una Mattina",
                Duration = 357,
                FormatBadge = "FLAC"
            },
            new TrackItem
            {
                Index = 5,
                Title = "Time",
                Artist = "Hans Zimmer",
                Album = "Inception OST",
                Duration = 275,
                FormatBadge = "DSD 2.8MHz"
            }
        };

        LeftNavPane.LoadTracks(samples);
        if (samples.Count > 0)
        {
            StageCoverCard.UpdateTrack(samples[0]);
            StageLyricsControl.LoadLyrics(null);
            BottomTransportBar.UpdateLyricSnippet($"{samples[0].Title} - {samples[0].Artist}");
        }
    }

    private void SidebarToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _isSidebarOpen = !_isSidebarOpen;
        SidebarColumn.Width = _isSidebarOpen ? new GridLength(286) : new GridLength(0);
        LeftNavPane.Visibility = _isSidebarOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DesktopLyricBtn_Click(object sender, RoutedEventArgs e)
    {
        // 桌面歌词开关占位
    }

    private void MiniPlayerBtn_Click(object sender, RoutedEventArgs e)
    {
        // 迷你窗模式占位
    }

    private void FullscreenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Maximized)
            {
                presenter.Restore();
            }
            else
            {
                presenter.Maximize();
            }
        }
    }

    private async void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog
        {
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
