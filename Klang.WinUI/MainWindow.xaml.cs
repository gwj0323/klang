using System;
using System.Collections.Generic;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
    private string _playMode = "loop_list"; // "loop_list" | "loop_single" | "random"
    private readonly Random _rand = new();
    private double _playbackRate = 1.0;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 1280 x 820 标准工作台尺寸
        AppWindow.Resize(new SizeInt32(1280, 820));

        // 1. 初始化音频引擎调度器
        AudioEngineService.Instance.InitializeDispatcher(this.DispatcherQueue);

        // 2. 监听音频进度与状态
        AudioEngineService.Instance.PositionChanged += (current, total) =>
        {
            BottomTransportBar.UpdateProgress(current, total);
            StageLyricsControl.UpdatePlaybackTime(current);

            // 同步底栏单句微缩歌词
            int activeIdx = LyricsService.Instance.FindActiveIndex(StageLyricsControl.Lyrics, current);
            if (activeIdx >= 0 && activeIdx < StageLyricsControl.Lyrics.Count)
            {
                BottomTransportBar.UpdateLyricSnippet(StageLyricsControl.Lyrics[activeIdx].Text);
            }
        };

        // 真实声学能量频谱驱动黑胶唱片卡动态律动柱
        AudioEngineService.Instance.SpectrumChanged += (bands) =>
        {
            StageCoverCard.UpdateSpectrum(bands);
        };

        AudioEngineService.Instance.StateChanged += (state) =>
        {
            bool isPlaying = (state == PlaybackState.Playing);
            BottomTransportBar.IsPlaying = isPlaying;
            StageCoverCard.SetPlaying(isPlaying);
        };

        // 曲目自然播放结束，自动切下一首
        AudioEngineService.Instance.TrackEnded += () =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_playMode == "loop_single" && _currentTrack != null)
                {
                    PlayTrack(_currentTrack);
                }
                else
                {
                    PlayNext();
                }
            });
        };

        // 3. 绑定左侧曲库选中与点击事件
        LeftNavPane.TrackSelected += (s, track) =>
        {
            PlayTrack(track);
        };

        // 4. 歌词点击跳播
        StageLyricsControl.SeekRequested += (s, timeSec) =>
        {
            AudioEngineService.Instance.Seek(timeSec);
        };

        // 5. 底栏控制事件绑定
        BottomTransportBar.PlayPauseClicked += (s, e) =>
        {
            if (_currentTrack == null && LeftNavPane.DisplayTracks.Count > 0)
            {
                PlayTrack(LeftNavPane.DisplayTracks[0]);
            }
            else
            {
                AudioEngineService.Instance.TogglePlayPause();
            }
        };

        BottomTransportBar.NextClicked += (s, e) => PlayNext();
        BottomTransportBar.PrevClicked += (s, e) => PlayPrev();

        BottomTransportBar.ShuffleClicked += (s, e) =>
        {
            _playMode = (_playMode == "random") ? "loop_list" : "random";
            BottomTransportBar.SetShuffleState(_playMode == "random");
            BottomTransportBar.UpdateLyricSnippet(_playMode == "random" ? "已开启随机播放" : "已开启列表循环");
        };

        BottomTransportBar.RepeatClicked += (s, e) =>
        {
            _playMode = (_playMode == "loop_single") ? "loop_list" : "loop_single";
            BottomTransportBar.SetRepeatMode(_playMode);
            BottomTransportBar.UpdateLyricSnippet(_playMode == "loop_single" ? "已开启单曲循环" : "已开启列表循环");
        };

        BottomTransportBar.RateClicked += (s, e) =>
        {
            _playbackRate = _playbackRate switch
            {
                1.0 => 1.25,
                1.25 => 1.5,
                1.5 => 2.0,
                2.0 => 0.75,
                _ => 1.0
            };
            BottomTransportBar.SetPlaybackRate(_playbackRate);
            BottomTransportBar.UpdateLyricSnippet($"播放倍速：{_playbackRate:0.0}x");
        };

        BottomTransportBar.FavoriteClicked += (s, e) =>
        {
            if (_currentTrack != null)
            {
                _currentTrack.IsFavorite = !_currentTrack.IsFavorite;
                BottomTransportBar.SetFavorite(_currentTrack.IsFavorite);
            }
        };

        BottomTransportBar.EqualizerClicked += async (s, e) =>
        {
            await OpenEqualizerDialogAsync();
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

        // 初始化界面初始文案与状态
        BottomTransportBar.SetShuffleState(false);
        BottomTransportBar.SetRepeatMode("loop_list");
        BottomTransportBar.UpdateEqBadge(AudioEngineService.Instance.CurrentPreset);
        StageCoverCard.UpdateTrack(null);
        StageLyricsControl.LoadLyrics(null);
        BottomTransportBar.UpdateTrackState(null, null, null);
    }

    private void PlayTrack(TrackItem track)
    {
        _currentTrack = track;

        StageCoverCard.UpdateTrack(track);
        StageLyricsControl.LoadLyrics(track.FilePath);
        BottomTransportBar.UpdateTrackState(track.Title, track.Artist, track.CoverPath);
        BottomTransportBar.SetFavorite(track.IsFavorite);

        if (!string.IsNullOrEmpty(track.FilePath))
        {
            AudioEngineService.Instance.Play(track.FilePath);
        }
    }

    private void PlayNext()
    {
        var tracks = LeftNavPane.DisplayTracks;
        if (tracks.Count == 0) return;

        int nextIdx = 0;
        if (_currentTrack != null)
        {
            int currentIdx = tracks.IndexOf(_currentTrack);
            if (currentIdx >= 0)
            {
                if (_playMode == "random" && tracks.Count > 1)
                {
                    nextIdx = _rand.Next(0, tracks.Count);
                    if (nextIdx == currentIdx) nextIdx = (nextIdx + 1) % tracks.Count;
                }
                else
                {
                    nextIdx = (currentIdx + 1) % tracks.Count;
                }
            }
        }

        PlayTrack(tracks[nextIdx]);
    }

    private void PlayPrev()
    {
        var tracks = LeftNavPane.DisplayTracks;
        if (tracks.Count == 0) return;

        int prevIdx = 0;
        if (_currentTrack != null)
        {
            int currentIdx = tracks.IndexOf(_currentTrack);
            if (currentIdx >= 0)
            {
                prevIdx = (currentIdx - 1 + tracks.Count) % tracks.Count;
            }
        }

        PlayTrack(tracks[prevIdx]);
    }

    private async System.Threading.Tasks.Task OpenEqualizerDialogAsync()
    {
        try
        {
            if (this.Content?.XamlRoot == null) return;

            var eqDialog = new EqualizerDialog
            {
                XamlRoot = this.Content.XamlRoot
            };

            await eqDialog.ShowAsync();
            BottomTransportBar.UpdateEqBadge(AudioEngineService.Instance.CurrentPreset);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EQ dialog error: {ex.Message}");
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
        // 桌面歌词暂留后续
    }

    private void MiniPlayerBtn_Click(object sender, RoutedEventArgs e)
    {
        // 迷你窗暂留后续
    }

    private void FullscreenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
    }

    private async void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (this.Content?.XamlRoot == null) return;

            var dialog = new SettingsDialog
            {
                XamlRoot = this.Content.XamlRoot
            };

            dialog.BackdropChanged += (mat) =>
            {
                if (mat == "mica")
                {
                    this.SystemBackdrop = new MicaBackdrop();
                }
                else if (mat == "acrylic")
                {
                    this.SystemBackdrop = new DesktopAcrylicBackdrop();
                }
                else
                {
                    this.SystemBackdrop = null;
                }
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Settings dialog error: {ex.Message}");
        }
    }
}
