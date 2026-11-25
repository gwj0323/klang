using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NAudio.Wave;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using Klang_WinUI.Controls;
using Klang_WinUI.Dialogs;
using Klang_WinUI.Models;
using Klang_WinUI.Services;
using Klang_WinUI.Views;

namespace Klang_WinUI;

public sealed partial class MainWindow : Window
{
    private bool _isSidebarOpen = true;
    private TrackItem? _currentTrack;
    private string _playMode = "loop_list"; // "loop_list" | "loop_single" | "random"
    private readonly Random _rand = new();
    private double _playbackRate = 1.0;

    private readonly MediaControlService _media = new();
    private DesktopLyricWindow? _desktopLyricWindow;
    private MiniPlayerWindow? _miniPlayerWindow;
    private TaskbarPlayerWindow? _taskbarPlayerWindow;
    private bool _isDesktopLyricVisible = false;

    private static AppState State => StateService.Instance.State;

    public MainWindow()
    {
        InitializeComponent();
        L.Attach((FrameworkElement)Content, this);   // 界面里写死的文字按当前语言翻译，并跟随语言切换

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 0. 恢复持久化设置（窗口、主题、材质、音量与音效）
        var st = State;
        AppWindow.Resize(new SizeInt32(Math.Max(900, st.WindowWidth), Math.Max(600, st.WindowHeight)));
        if (st.WindowMaximized && AppWindow.Presenter is OverlappedPresenter op) op.Maximize();

        AudioEngineService.Instance.LoadSettingsFromState(st);
        ApplyBackdrop(st.BackdropMaterial);
        ThemeService.Apply(this, st.Theme);
        StageLyricsControl.LyricFontSize = st.LyricFontSize;
        ApplySubMode(st.LyricSubMode, announce: false);
        TrackItem.UiDispatcher = DispatcherQueue;
        UiAnimations.AttachFade(ImmersiveLayer, 0f);
        UiAnimations.AttachFade(SidebarRail, 0f);
        UiAnimations.AttachFade(LeftNavPane, 0f);
        LeftNavPane.TabChanged += (s, tab) => UpdateRail(tab);
        UpdateRail("all");
        ((FrameworkElement)Content).ActualThemeChanged += (s, e) => UpdateRail(_railTab);
        StageLyricsControl.CalibrationPicked += StageLyricsControl_CalibrationPicked;
        ImmersiveLayer.CloseRequested += (s, e) => CloseImmersive();
        MvLayer.CloseRequested += (s, e) => CloseMv(resume: true);
        LeftNavPane.MvRequested += (s, t) => PlayMv(t);
        StageCoverCard.MvRequested += (s, e) => { if (_currentTrack != null) PlayMv(_currentTrack); };
        MvLayer.FullscreenRequested += (s, e) =>
        {
            if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) { ExitFullscreen(); _mvSetFullscreen = false; }
            else { EnterFullscreen(); _mvSetFullscreen = true; }
            UpdateMvCaptionInset();
        };
        Activated += (s, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) ImmersiveLayer.OnWindowDeactivated(); };
        ImmersiveLayer.SeekRequested += (s, t) => AudioEngineService.Instance.Seek(Math.Max(0, t - _lyricOffset));
        ImmersiveLayer.VolumeChanged += (s, v) => BottomTransportBar.SetVolume(v);
        ImmersiveLayer.SetVolume(AudioEngineService.Instance.Volume);
        ImmersiveLayer.PlayPauseRequested += (s, e) => TogglePlayPause();
        ImmersiveLayer.PrevRequested += (s, e) => PlayPrev();
        ImmersiveLayer.NextRequested += (s, e) => PlayNext();
        _playMode = st.PlayMode is "loop_single" or "random" or "roam" ? st.PlayMode : "loop_list";
        if (st.SidebarCollapsed) SetSidebarOpen(false); 
        LeftNavPane.ApplyViewState(st.NavSortMode, st.NavGroupMode);

        // 1. 初始化音频引擎调度器
        AudioEngineService.Instance.InitializeDispatcher(this.DispatcherQueue);

        // 1b. 系统媒体控件 (SMTC)：多媒体键与系统浮层
        _media.Initialize(this.DispatcherQueue);
        _media.PlayRequested += () => { if (!AudioEngineService.Instance.IsPlaying) TogglePlayPause(); };
        _media.PauseRequested += () => { if (AudioEngineService.Instance.IsPlaying) AudioEngineService.Instance.Pause(); };
        _media.NextRequested += PlayNext;
        _media.PrevRequested += PlayPrev;
        _media.SeekRequested += pos =>
        {
            AudioEngineService.Instance.Seek(pos);
            StageLyricsControl.UpdatePlaybackTime(pos + _lyricOffset); ImmersiveLayer.UpdateTime(pos + _lyricOffset);
        };

        // 设置里重新打开波形进度条：为当前曲目补上波形
        Controls.TransportBar.WaveformModeChanged += () => DispatcherQueue.SafeEnqueue(() =>
        {
            if (State.WaveformSeekBar && _currentTrack != null) _ = LoadWaveformAsync(_currentTrack);
        });

        // 2. 监听音频进度与状态
        AudioEngineService.Instance.PositionChanged += (current, total) =>
        {
            BottomTransportBar.UpdateProgress(current, total);
            TryPlanMix(current, total);
            TryPrefetchNext(current, total);
            if (ImmersiveLayer.IsOpen) ImmersiveLayer.UpdateProgress(current, total);
            StageLyricsControl.UpdatePlaybackTime(current + _lyricOffset); ImmersiveLayer.UpdateTime(current + _lyricOffset);
            _media.UpdateTimeline(current, total);

            // 同步底栏单句微缩歌词
            int activeIdx = LyricsService.Instance.FindActiveIndex(StageLyricsControl.Lyrics, current + _lyricOffset);
            _taskbarPlayerWindow?.UpdateLyric(activeIdx >= 0 && activeIdx < StageLyricsControl.Lyrics.Count
                && StageLyricsControl.Lyrics[activeIdx].EndTime < 9000
                ? StageLyricsControl.Lyrics[activeIdx].Text : null);
            if (GalleryLayer.Visibility == Visibility.Visible)
            {
                var gl = StageLyricsControl.Lyrics;
                bool real = activeIdx >= 0 && activeIdx < gl.Count && gl[activeIdx].EndTime < 9000;
                GalleryLayer.SetProgress(current, total);
                GalleryLayer.SetLyric(real && activeIdx > 0 ? gl[activeIdx - 1].Text : "", real ? gl[activeIdx].Text : "", real && activeIdx + 1 < gl.Count ? gl[activeIdx + 1].Text : "");
            }
            if (activeIdx >= 0 && activeIdx < StageLyricsControl.Lyrics.Count)
            {
                // “暂无歌词”之类的占位行不显示，改显示歌手
                var line = StageLyricsControl.Lyrics[activeIdx];
                BottomTransportBar.UpdateLyricSnippet(line.EndTime >= 9000 ? (_currentTrack?.Artist ?? "") : line.Text);
                if (_miniPlayerWindow != null && _miniPlayerWindow.AppWindow.IsVisible)
                    _miniPlayerWindow.UpdateLyric(line.EndTime >= 9000 ? "" : line.Text);
            }

            // 同步桌面歌词与迷你播放器
            if (_desktopLyricWindow != null && _isDesktopLyricVisible)
            {
                _desktopLyricWindow.UpdatePlaybackTime(current + _lyricOffset);
            }
            if (_miniPlayerWindow != null && _miniPlayerWindow.AppWindow.IsVisible)
            {
                _miniPlayerWindow.UpdateProgress(current, total);
            }
        };

        // 真实声学能量频谱驱动黑胶唱片卡动态律动柱
        AudioEngineService.Instance.SpectrumChanged += (bands) =>
        {
            StageCoverCard.UpdateSpectrum(bands);
            // 沉浸背景随低频能量脉动（前 6 个频段的平均值）
            if (ImmersiveLayer.IsOpen && bands != null && bands.Length >= 6)
                ImmersiveLayer.SetEnergy(bands.Take(6).Average());
        };

        AudioEngineService.Instance.StateChanged += (state) =>
        {
            bool isPlaying = (state == PlaybackState.Playing);
            ListeningService.Instance.SetPlaying(isPlaying, _currentTrack);
            BottomTransportBar.IsPlaying = isPlaying;
            ImmersiveLayer.SetPlaying(isPlaying);
            GalleryLayer.SetIsPlaying(isPlaying);
            StageCoverCard.SetPlaying(isPlaying);
            ApplyDynamicCover();
            UpdateEasterEgg();
            _taskbarPlayerWindow?.UpdatePlayState(isPlaying);
            _media.SetPlaybackState(isPlaying, _currentTrack != null);
        };

        AudioEngineService.Instance.MixAdopted += path => DispatcherQueue.SafeEnqueue(() => OnMixAdopted(path));

        // 曲目自然播放结束，自动切下一首
        AudioEngineService.Instance.TrackEnded += () =>
        {
            DispatcherQueue.SafeEnqueue(() =>
            {
                _endedNaturally = true;   // 引擎此时已复位，听了多少得靠这个标记告诉漫游模型：整首都听完了
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
            // 点列表里的歌：缩略图“飞”到主界面的大封面位置（主封面可见、且确实换了一首时才做）
            bool fly = track != _currentTrack && !_stageNarrow && !ImmersiveLayer.IsOpen
                && StageCoverCard.Visibility == Visibility.Visible
                && LeftNavPane.PrepareCoverAnimation(track, "listCover");
            StageCoverCard.SkipNextCrossfade = fly;
            PlayTrack(track);
            StageCoverCard.SkipNextCrossfade = false;
            if (fly) StartCoverAnimation("listCover", StageCoverCard.CoverTarget);
        };

        GalleryLayer.CloseRequested += (s, e) => CloseGallery();
        GalleryLayer.PlayPauseRequested += (s, e) => TogglePlayPause();
        GalleryLayer.AlbumPicked += (s, tracks) => LeftNavPane.PlayQueue(tracks);
        LeftNavPane.FavoriteToggled += (s, track) => ToggleFavorite(track);
        LeftNavPane.CloudCoverReady += (s, track) => RefreshTrackCover(track);
        LeftNavPane.CoversAdjusted += (s, tracks) =>
        {
            if (_currentTrack != null && tracks.Contains(_currentTrack)) RefreshTrackCover(_currentTrack);
        };
        MetadataService.Instance.TrackEnriched += track => DispatcherQueue.SafeEnqueue(() =>
        {
            RefreshTrackCover(track);
            // 歌手 / 专辑变了，且列表正按它们分组或排序时才重建，避免无谓闪动
            if (LeftNavPane.GroupMode is "artist" or "album" || LeftNavPane.SortMode is "artist" or "album")
                LeftNavPane.RefreshView();
        });

        LeftNavPane.TrackRemoved += (s, track) =>
        {
            if (_currentTrack == track) { track.IsCurrent = false; _currentTrack = null; LoadDynamicCover(null); }
            _taskbarPlayerWindow?.UpdateTrack(_currentTrack);
        };

        LeftNavPane.ViewStateChanged += (s, e) =>
        {
            State.NavSortMode = LeftNavPane.SortMode;
            State.NavGroupMode = LeftNavPane.GroupMode;
            StateService.Instance.RequestSave();
        };

        // 4. 歌词点击跳播
        StageLyricsControl.SeekRequested += (s, timeSec) =>
        {
            AudioEngineService.Instance.Seek(Math.Max(0, timeSec - _lyricOffset));
        };

        // 5. 底栏控制事件绑定
        BottomTransportBar.PlayPauseClicked += (s, e) => TogglePlayPause();

        BottomTransportBar.NextClicked += (s, e) => PlayNext();
        BottomTransportBar.PrevClicked += (s, e) => PlayPrev();

        BottomTransportBar.ShuffleClicked += (s, e) =>
        {
            // 列表循环 → 随机 → 漫游 → 列表循环
            _playMode = _playMode switch { "random" => "roam", "roam" => "loop_list", _ => "random" };
            BottomTransportBar.SetShuffleState(_playMode);
            BottomTransportBar.SetRepeatMode(_playMode);
            BottomTransportBar.UpdateLyricSnippet(_playMode switch
            {
                "random" => L.T("已切换为随机播放"),
                "roam" => L.T("已切换为漫游：根据收听历史选择下一首"),
                _ => L.T("已切换为列表循环")
            });
            SavePlayMode();
        };

        BottomTransportBar.RepeatClicked += (s, e) =>
        {
            _playMode = (_playMode == "loop_single") ? "loop_list" : "loop_single";
            BottomTransportBar.SetShuffleState(_playMode);
            BottomTransportBar.SetRepeatMode(_playMode);
            BottomTransportBar.UpdateLyricSnippet(_playMode == "loop_single" ? L.T("已切换为单曲循环") : L.T("已切换为列表循环"));
            SavePlayMode();
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
            AudioEngineService.Instance.PlaybackRate = _playbackRate;
            State.PlaybackRate = _playbackRate;
            StateService.Instance.RequestSave();
            BottomTransportBar.SetPlaybackRate(_playbackRate);
            BottomTransportBar.UpdateLyricSnippet(L.F("播放倍速：{0:0.0#}x", _playbackRate));
        };

        AudioEngineService.Instance.SleepTimerElapsed += () =>
        {
            DispatcherQueue.SafeEnqueue(() => BottomTransportBar.UpdateLyricSnippet(L.T("睡眠定时已结束，已暂停播放")));
        };

        BottomTransportBar.FavoriteClicked += (s, e) =>
        {
            if (_currentTrack != null) ToggleFavorite(_currentTrack, fromTransport: true);
        };

        BottomTransportBar.QueueClicked += (s, anchor) =>
            QueueFlyout.Show(anchor, () => LeftNavPane.Queue, LeftNavPane.UpNext, _currentTrack,
                play: t => { _preChosenNext = null; PlayTrack(t); },
                playNext: LeftNavPane.PlayNextInQueue,
                remove: LeftNavPane.RemoveFromQueue);
        LeftNavPane.UpNextChanged += (s, e) => OnUpNextChanged();

        BottomTransportBar.EqualizerClicked += async (s, e) =>
        {
            await OpenEqualizerDialogAsync();
        };

        BottomTransportBar.UserSeekRequested += (s, pos) =>
        {
            AudioEngineService.Instance.Seek(pos);
            StageLyricsControl.UpdatePlaybackTime(pos + _lyricOffset); ImmersiveLayer.UpdateTime(pos + _lyricOffset);
        };

        BottomTransportBar.UserVolumeChanged += (s, vol) =>
        {
            AudioEngineService.Instance.Volume = vol;
            ImmersiveLayer.SetVolume(vol);
            State.Volume = vol;
            StateService.Instance.RequestSave();
        };

        // 初始化界面初始文案与状态
        BottomTransportBar.SetShuffleState(_playMode);
        BottomTransportBar.SetRepeatMode(_playMode);
        BottomTransportBar.SetVolume(AudioEngineService.Instance.Volume);
        _playbackRate = AudioEngineService.Instance.PlaybackRate;
        BottomTransportBar.SetPlaybackRate(_playbackRate);
        BottomTransportBar.UpdateEqBadge(AudioEngineService.Instance.CurrentPreset);
        StageCoverCard.UpdateTrack(null);
        StageLyricsControl.LoadLyrics(null);
        BottomTransportBar.UpdateTrackState(null, null, null);

        // 6. 全局键盘快捷键（Preview 阶段拦截，避免被按钮 / 列表先吞掉）
        if (this.Content is UIElement rootElement)
        {
            rootElement.PreviewKeyDown += Root_PreviewKeyDown;
            if (rootElement is FrameworkElement fe)
            {
                fe.Loaded += async (s, e) => await RestoreLibraryAsync();
            }
        }

        WeatherService.Instance.Apply();   // 只有开启了“天气效果”并设置了城市才会联网

        InitTray();
        _ = AutoCheckUpdateAsync();

        this.Closed += (s, e) =>
        {
            _tray?.Dispose();
            _tray = null;
            SaveWindowAndAudioState();
            ListeningService.Instance.End();
            LibraryService.Instance.FlushPendingSave();
            StateService.Instance.Flush();
            _media.Dispose();
            _desktopLyricWindow?.Close();
            _miniPlayerWindow?.Close();
            _taskbarPlayerWindow?.Close();
            CrashReporter.Stop();   // 放在最后：退出过程中崩溃也算异常结束，下次启动会报告
        };

        if (State.TaskbarPlayerEnabled) SetTaskbarPlayerEnabled(true);
    }

    #region 启动恢复与状态保存



    private async System.Threading.Tasks.Task RestoreLibraryAsync()
    {
        try
        {
            Dialogs.SettingsDialog.DynamicCoverSettingChanged += () => LoadDynamicCover(_currentTrack);
            Dialogs.SettingsDialog.EasterEggChanged += on => { StageCoverCard.SetEasterEggVisible(on); UpdateEasterEgg(); };
            StageCoverCard.SetEasterEggVisible(State.EasterEgg);
            UpdateEasterEgg();
            foreach (var src in Services.Cloud.CloudSources.All)
            {
                // 登录状态确认后（启动时恢复的云端歌曲常常早于这一步），补取当前歌曲的动态封面
                var s = src;
                s.LoginChanged += () => DispatcherQueue.SafeEnqueue(() =>
                {
                    if (_currentTrack is { IsOnline: true } t && _dynCoverPath == null
                        && Services.Cloud.CloudSources.For(t) == s) LoadDynamicCover(t);
                });
                _ = s.VerifyLoginAsync();
            }
            await LibraryService.Instance.RestoreFromStateAsync();
            LeftNavPane.ReloadFromLibrary();
            MetadataService.Instance.Enqueue(LibraryService.Instance.Tracks);

            // 恢复上次播放的曲目（仅显示，不自动开始播放）
            var last = LibraryService.Instance.Tracks.FirstOrDefault(t => t.Id == State.LastTrackId);
            if (last != null)
            {
                LeftNavPane.ResetQueue();
                PlayTrack(last, autoPlay: false);
            }
            else if (State.LastOnlineQueue.Count > 0)
            {
                // 云端曲目不在曲库里：用保存的快照还原那首歌和当时的云端队列
                var queue = Services.Cloud.CloudSources.FromSnapshots(State.LastOnlineQueue);
                var online = queue.FirstOrDefault(t => t.Id == State.LastTrackId);
                if (online != null)
                {
                    LeftNavPane.RestoreQueue(queue);
                    _savedOnlineQueue = LeftNavPane.Queue;
                    PlayTrack(online, autoPlay: false);
                }
            }

            // 后台：核对文件还在不在 → 增量扫描已添加的文件夹（没变的文件直接跳过）→ 开始监视文件夹变化
            _ = SyncLibraryInBackgroundAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Restore library error: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task SyncLibraryInBackgroundAsync()
    {
        try
        {
            var lib = LibraryService.Instance;
            AudioFeatureService.Instance.Start(DispatcherQueue);
            await lib.RefreshMissingAsync();
            var added = await lib.RescanWatchedFoldersAsyncCollect();
            if (added.Count > 0) MetadataService.Instance.Enqueue(added);
            LeftNavPane.RefreshView();
            AudioFeatureService.Instance.Enqueue(lib.Tracks);   // 后台慢慢给还没算过的歌提取声学特征

            // 无损真伪检测：后台分析所有无损文件的频谱，结果出来后刷新徽标 / 列表
            QualityAnalysisService.Instance.Start(DispatcherQueue);
            QualityAnalysisService.Instance.Analyzed += t =>
            {
                if (_currentTrack == t) StageCoverCard.UpdateTrackBadge(t);
                if (LeftNavPane.HasActiveFilter) LeftNavPane.RefreshView();
            };
            QualityAnalysisService.Instance.Enqueue(lib.Tracks);
            RhythmAnalysisService.Instance.Start(DispatcherQueue);
            RhythmAnalysisService.Instance.Enqueue(lib.Tracks);

            lib.LibraryChangedOnDisk += newTracks =>
            {
                if (newTracks.Count > 0) MetadataService.Instance.Enqueue(newTracks);
                LeftNavPane.RefreshView();
            };
            lib.StartWatching(DispatcherQueue);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Library sync error: {ex.Message}");
        }
    }

    /// <summary>
    /// “下一首播放” / 队列变了：之前预先选好的下一首作废，快放完时按新的顺序重新选（并重新预缓冲、规划混音）。
    /// 已经排好但还没开始的混音一并取消；已经在淡入淡出的就让它播完。
    /// </summary>
    private void OnUpNextChanged()
    {
        if (_mixNext != null)
        {
            if (!AudioEngineService.Instance.CancelMix()) return;
            _mixNext = null; _mixPlan = null;
        }
        _preChosenNext = null;
        _prefetchFromId = null;
        _plannedFromId = null;
    }

    private void SavePlayMode()
    {
        State.PlayMode = _playMode;
        StateService.Instance.RequestSave();

        // 播放模式变了：之前按旧模式预先选好的“下一首”作废（已经排好混音的除外），之后按新模式重新选并预缓冲
        if (_mixNext == null)
        {
            _preChosenNext = null;
            _prefetchFromId = null;
        }
    }

    private void SaveWindowAndAudioState()
    {
        try
        {
            var st = State;
            if (AppWindow.Presenter is OverlappedPresenter op)
            {
                st.WindowMaximized = op.State == OverlappedPresenterState.Maximized;
                if (op.State == OverlappedPresenterState.Restored)
                {
                    st.WindowWidth = AppWindow.Size.Width;
                    st.WindowHeight = AppWindow.Size.Height;
                }
            }
            AudioEngineService.Instance.SaveSettingsToState(st);
            st.SidebarCollapsed = !_isSidebarOpen;
            st.PlayMode = _playMode;
            LibraryService.Instance.SaveLibrary();
            StateService.Instance.RequestSave();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save state error: {ex.Message}");
        }
    }

    #endregion

    private bool _endedNaturally;
    private bool _nextByRoam;

    #region 窄窗口布局

    private const double NarrowStageWidth = 760;   // 舞台区宽度小于它（约窗口 < 1050px）就改成单栏
    private bool _stageNarrow;

    private void StageGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < NarrowStageWidth;
        if (narrow == _stageNarrow) return;
        _stageNarrow = narrow;
        StageCoverCard.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        StageCoverColumn.Width = narrow ? new GridLength(0) : GridLength.Auto;
        StageGrid.ColumnSpacing = narrow ? 0 : 48;
        StageGrid.Padding = narrow ? new Thickness(28, 16, 24, 16) : new Thickness(46, 24, 42, 24);
        NarrowHeader.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        UpdateNarrowHeader(_currentTrack);
    }

    private void UpdateNarrowHeader(TrackItem? track)
    {
        NarrowTitle.Text = track == null ? L.T("暂无播放曲目") : (string.IsNullOrWhiteSpace(track.Title) ? L.T("未命名曲目") : track.Title);
        NarrowArtist.Text = track == null ? "" : track.Artist;
        NarrowCover.Source = track?.CoverThumb;
    }

    #endregion

    #region 播放控制

    private void PlayTrack(TrackItem track, bool autoPlay = true)
    {
        if (MvLayer.IsOpen) CloseMv(resume: false);   // 切到别的歌（媒体键、托盘菜单等）：关掉 MV，不再恢复原来的歌
        CrashReporter.Note($"{(autoPlay ? "播放" : "载入")}：{track.Artist} - {track.Title}" +
            (track.IsOnline ? $"（{Services.Cloud.CloudSources.For(track)?.DisplayName ?? track.CloudSource}）" :$"（{System.IO.Path.GetExtension(track.FilePath)} {track.SampleRate}Hz/{track.BitDepth}bit）") +
            (_adoptingMix ? " 混音接入" : ""));
        // 文件已经丢了：不开始播放，提示一下（启动时恢复上次曲目只显示，不拦）
        if (autoPlay && track.IsMissing && !track.IsOnline)
        {
            BottomTransportBar.UpdateLyricSnippet(L.T("文件丢失，无法播放：") + System.IO.Path.GetFileName(track.FilePath));
            return;
        }
        if (autoPlay)
        {
            LibraryService.Instance.RecordPlay(track);
            // 听歌报告用的记录：引擎开始播放新歌时会通知“播放中”，那时才开始计时；
            // 混音接入时声音不断，也不会再通知，直接开始计
            ListeningService.Instance.Begin(track, _adoptingMix && AudioEngineService.Instance.IsPlaying);

            // 喂给漫游模型：上一首听了多少（自然播完 = 1；否则按进度算），这一首是不是漫游选的
            var engine = AudioEngineService.Instance;
            double frac = 0;
            if (_endedNaturally) frac = 1;
            else if (_currentTrack != null && _currentTrack.Duration > 0 && engine.HasSource)
                frac = Math.Clamp((engine.CurrentTime - _currentTrack.StartTime) / _currentTrack.Duration, 0, 1);
            if (_currentTrack != null && !_currentTrack.IsOnline && !track.IsOnline)
                RoamingService.Instance.OnTransition(_currentTrack, frac, track, _nextByRoam, LibraryService.Instance.Tracks);
            _endedNaturally = false;
            _nextByRoam = false;
        }

        if (!_adoptingMix) { _mixNext = null; _preChosenNext = null; }
        _prefetchFromId = null;   // 新的一首：允许再次为它的下一首预缓冲
        if (autoPlay) LeftNavPane.UpNext.Remove(track);   // 已经播到了，不再算“下一首播放”
        if (_currentTrack != null) _currentTrack.IsCurrent = false;
        _currentTrack = track;
        track.IsCurrent = true;

        StageCoverCard.UpdateTrack(track);
        UpdateMvButton(track);
        UpdateEasterEgg();
        if (autoPlay && State.EasterEgg) StageCoverCard.GreetEasterEgg();   // 切歌时小人打个招呼（启动恢复时不打）
        LoadDynamicCover(track);
        UpdateTrayTip();
        UpdateNarrowHeader(track);
        // CUE 分轨暂不加载整轨歌词（整轨 .lrc 与分轨时间轴不一致）
        StageLyricsControl.CalibrationMode = false;
        LoadLyricOffset(track);
        if (LyricsService.ReadChosen(track.Id) is { } chosen)
            StageLyricsControl.LoadLyricLines(LyricsService.Instance.ParseLrc(chosen.Lrc, chosen.Trans, chosen.Roma));   // 用户选定的歌词优先
        else
            StageLyricsControl.LoadLyrics(track.IsCueTrack || track.IsOnline ? null : track.FilePath);
        SyncImmersive();
        BottomTransportBar.UpdateTrackState(track.Title, track.Artist, track.CoverPath);
        BottomTransportBar.SetFavorite(track.IsFavorite);
        BottomTransportBar.UpdateProgress(0, track.Duration);
        _ = LoadWaveformAsync(track);
        LeftNavPane.SetCurrentTrack(track);
        if (GalleryLayer.Visibility == Visibility.Visible) GalleryLayer.SetCurrent(track);   // 关着时不解码它的大封面，打开时会设置
        _media.UpdateTrack(track);
        ScheduleMemoryTrim();
        _media.SetPlaybackState(autoPlay, true);

        // 同步桌面歌词与迷你播放器
        _desktopLyricWindow?.UpdateLyrics(StageLyricsControl.Lyrics);
        _desktopLyricWindow?.UpdateTrack(track);
        _miniPlayerWindow?.UpdateTrack(track);
        _taskbarPlayerWindow?.UpdateTrack(track);

        if (!track.IsOnline && !track.IsCueTrack) _ = FillLocalMetaAsync(track);

        State.LastTrackId = track.Id;
        SaveOnlineQueue(track);
        StateService.Instance.RequestSave();

        AudioEngineService.Instance.SetTrackReplayGain(track.ReplayGainDb, track.ReplayPeak);

        _playSeq++;
        if (track.IsOnline)
        {
            // 混音接管过来的云端曲目：引擎已经在播了，这里只补歌词、封面等，不能再停掉重播
            _ = PlayOnlineAsync(track, autoPlay, adopted: _adoptingMix);
            return;
        }

        if (autoPlay && !_adoptingMix && !string.IsNullOrEmpty(track.FilePath))
        {
            _ = PlayLocalAsync(track.FilePath, track.StartTime, track.EndTime, _playSeq);
        }
    }

    private int _playSeq;

    private const int MaxSavedOnlineQueue = 1000;
    private List<TrackItem>? _savedOnlineQueue;

    /// <summary>
    /// 云端曲目不在曲库里，重启后无法按 LastTrackId 找回：把当前云端队列存成快照。
    /// 队列没变时不重复生成；播放本地曲目后清空。
    /// </summary>
    private void SaveOnlineQueue(TrackItem track)
    {
        if (!track.IsOnline)
        {
            _savedOnlineQueue = null;
            if (State.LastOnlineQueue.Count > 0) State.LastOnlineQueue = new();
            return;
        }
        var queue = LeftNavPane.Queue;
        if (ReferenceEquals(queue, _savedOnlineQueue) && queue.Contains(track)) return;

        var online = queue.Where(t => t.IsOnline).ToList();
        int i = online.IndexOf(track);
        if (i < 0) { online = new List<TrackItem> { track }; i = 0; }
        if (online.Count > MaxSavedOnlineQueue)
        {
            int start = Math.Clamp(i - MaxSavedOnlineQueue / 2, 0, online.Count - MaxSavedOnlineQueue);
            online = online.GetRange(start, MaxSavedOnlineQueue);
        }
        State.LastOnlineQueue = online.Select(Services.Cloud.CloudSources.ToSnapshot).ToList();
        _savedOnlineQueue = queue.Contains(track) ? queue : null;
    }

    private System.Threading.CancellationTokenSource? _waveCts;

    /// <summary>底栏波形：先压平旧波形，再在后台取新波形（有缓存时几乎立即出现）。</summary>
    private async System.Threading.Tasks.Task LoadWaveformAsync(TrackItem track)
    {
        _waveCts?.Cancel();
        var cts = _waveCts = new System.Threading.CancellationTokenSource();
        BottomTransportBar.SetWaveform(null);
        if (!State.WaveformSeekBar) return;   // 关闭时不解码计算
        if (string.IsNullOrEmpty(track.FilePath) || track.FilePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            double start = track.IsCueTrack ? track.StartTime : 0;
            double end = track.IsCueTrack ? track.EndTime : 0;
            var data = await WaveformService.GetAsync(track.FilePath, start, end, cts.Token);
            if (!cts.IsCancellationRequested && _currentTrack == track) BottomTransportBar.SetWaveform(data);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    /// <summary>正在播放时先淡出旧曲再起播，避免切歌时电平突然截断的爆音。</summary>
    private async System.Threading.Tasks.Task PlayLocalAsync(string path, double start, double end, int seq)
    {
        var engine = AudioEngineService.Instance;
        if (engine.IsPlaying)
        {
            await engine.FadeOutAndStopAsync();
            if (seq != _playSeq) return; // 期间又切了别的曲目
        }
        engine.Play(path, start, end);
    }

    private System.Threading.CancellationTokenSource? _metaCts;

    /// <summary>本地曲目缺封面/信息/歌词时，立刻为当前这首在线补全（后台队列之外的优先通道）。</summary>
    private async System.Threading.Tasks.Task FillLocalMetaAsync(TrackItem track)
    {
        if (!State.AutoFetchMetadata) return;
        _metaCts?.Cancel();
        var cts = _metaCts = new System.Threading.CancellationTokenSource();
        try
        {
            // 封面 / 专辑 / 歌手：补全成功后由 TrackEnriched 事件刷新界面
            if (MetadataService.NeedsEnrich(track))
                await MetadataService.Instance.EnrichAsync(track);

            // 没有本地歌词时，用匹配到的网易云歌曲取歌词
            var lyr = StageLyricsControl.Lyrics;
            bool noLyrics = lyr.Count == 0 || (lyr.Count == 1 && lyr[0].EndTime >= 9000);
            if (noLyrics && !cts.IsCancellationRequested && _currentTrack == track)
            {
                var bundle = await MetadataService.Instance.FetchLyricsAsync(track, cts.Token);
                if (bundle != null && _currentTrack == track && !cts.IsCancellationRequested)
                {
                    StageLyricsControl.LoadLyricLines(LyricsService.Instance.ParseLrc(bundle.Lrc, bundle.Trans, bundle.Roma));
                    SyncImmersive();
                    _desktopLyricWindow?.UpdateLyrics(StageLyricsControl.Lyrics);
                }
            }

            // 网易云也没有时，再试 LRCLIB 开放歌词库（同样受“自动补全”开关控制）
            lyr = StageLyricsControl.Lyrics;
            noLyrics = lyr.Count == 0 || (lyr.Count == 1 && lyr[0].EndTime >= 9000);
            if (noLyrics && !track.IsCueTrack && !cts.IsCancellationRequested && _currentTrack == track)
            {
                var lrc = await LrclibService.FetchSyncedAsync(track, cts.Token);
                if (lrc != null && _currentTrack == track && !cts.IsCancellationRequested)
                    ApplyOnlineLyrics(lrc);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private System.Threading.CancellationTokenSource? _onlineCts;
    private string _onlineStatus = "";

    /// <summary>云端曲目：取直链并下载缓存后播放，同时异步加载歌词。后点的曲目会取消先前的请求。</summary>
    private async System.Threading.Tasks.Task PlayOnlineAsync(TrackItem track, bool autoPlay, bool adopted = false)
    {
        _onlineCts?.Cancel();
        _onlineStatus = "";
        var cts = _onlineCts = new System.Threading.CancellationTokenSource();
        var ct = cts.Token;
        var source = Services.Cloud.CloudSources.For(track);
        if (source == null)
        {
            // 来源已不存在（例如曲目来自之后被移除的平台）
            _onlineStatus = L.T("无法获取音源（不支持的云端来源）");
            BottomTransportBar.UpdateLyricSnippet(_onlineStatus);
            return;
        }

        int seq = _playSeq;

        // 歌词与音源并行获取（淡出旧曲的同时就开始请求）
        var lyricTask = source.GetLyricsAsync(track.CloudId, ct);
        if (!adopted)
        {
            BottomTransportBar.UpdateLyricSnippet(autoPlay ? L.T("正在获取云端音源…") : L.T("云端曲目"));

            await AudioEngineService.Instance.FadeOutAndStopAsync();
            // 上一首云端歌曲边下边播还没下完的，已经不听了：停止下载（预缓冲的下载不受影响）
            Services.Cloud.ProgressiveDownload.CancelPlaybackDownloads();
            if (ct.IsCancellationRequested || seq != _playSeq) return;
        }

        try
        {
            bool ok = await source.ResolveAsync(track, ct);
            if (ct.IsCancellationRequested || _currentTrack != track) return;

            if (!ok)
            {
                _onlineStatus = source.IsLoggedIn
                    ? L.T("无法获取音源（可能为会员专享或无版权）")
                    : L.F("无法获取音源（可能需要先登录{0}）", source.DisplayName);
                BottomTransportBar.UpdateLyricSnippet(_onlineStatus);
            }
            else
            {
                RefreshTrackCover(track);
                if (!adopted && autoPlay && seq == _playSeq) AudioEngineService.Instance.Play(track.FilePath);
                // 云端音源缓存到本地后才有波形；边下边播时等整首下完再算
                if (Services.Cloud.ProgressiveDownload.Find(track.FilePath) is { } dl)
                    _ = dl.Completion.ContinueWith(t =>
                    {
                        if (t.Result) DispatcherQueue.TryEnqueue(() => { if (_currentTrack == track) _ = LoadWaveformAsync(track); });
                    }, System.Threading.Tasks.TaskScheduler.Default);
                else
                    _ = LoadWaveformAsync(track);
                if (!adopted) BottomTransportBar.UpdateLyricSnippet(track.FormatBadge);
            }

            // 歌词与音源是否可用无关，都照常显示
            var bundle = await lyricTask;
            if (ct.IsCancellationRequested || _currentTrack != track) return;
            if (!string.IsNullOrWhiteSpace(bundle.Lrc) && !LyricsService.HasChosen(track.Id))   // 用户选定过歌词就不覆盖
            {
                var lines = LyricsService.Instance.ParseLrc(bundle.Lrc, bundle.Trans, bundle.Roma);
                StageLyricsControl.LoadLyricLines(lines);
                SyncImmersive();
                _desktopLyricWindow?.UpdateLyrics(StageLyricsControl.Lyrics);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_currentTrack == track) BottomTransportBar.UpdateLyricSnippet(L.T("云端播放失败：") + ex.Message);
        }
    }

    /// <summary>彩蛋小人跟着当前歌曲的播放状态和 BPM 晃腿。</summary>
    private void UpdateEasterEgg()
    {
        if (!State.EasterEgg) return;
        StageCoverCard.UpdateEasterEgg(AudioEngineService.Instance.IsPlaying, _currentTrack?.Bpm ?? 0);
    }

    #region 启动时自动检查更新

    private UpdateInfo? _pendingUpdate;
    private bool _updateDialogOpen;

    /// <summary>启动后稍等片刻检查 GitHub 上的新版本；失败时不打扰用户，只记日志。</summary>
    private async System.Threading.Tasks.Task AutoCheckUpdateAsync()
    {
        if (!State.AutoCheckUpdate) return;
        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(8));   // 不和启动时的曲库恢复、扫描抢资源
        UpdateInfo info;
        try { info = await UpdateService.CheckAsync(); }
        catch (Exception ex) { App.LogError("UpdateCheck", ex.Message); return; }
        if (!info.HasUpdate || info.Latest == null) return;
        if (State.SkippedUpdateVersion == info.Latest.ToString(3)) return;
        CrashReporter.Note("发现新版本 " + info.Latest.ToString(3));
        _pendingUpdate = info;
        ShowPendingUpdate();
    }

    /// <summary>
    /// 弹出新版本提示。窗口藏在托盘里、沉浸模式打开着、或者正有别的对话框时先不弹，
    /// 等窗口恢复 / 退出沉浸模式时再弹（每次启动最多提示一次）。
    /// </summary>
    private async void ShowPendingUpdate()
    {
        var info = _pendingUpdate;
        if (info == null || _updateDialogOpen || !AppWindow.IsVisible || ImmersiveLayer.IsOpen || Content?.XamlRoot == null) return;
        _updateDialogOpen = true;
        try
        {
            var notes = CleanReleaseNotes(info.Notes);
            var dlg = new ContentDialog
            {
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
                RequestedTheme = ThemeService.Current,
                XamlRoot = Content.XamlRoot,
                Title = L.F("发现新版本 {0}", info.Latest!.ToString(3)),
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = L.F("当前版本 {0}", UpdateService.CurrentVersionText), Opacity = 0.7 },
                        new ScrollViewer
                        {
                            MaxHeight = 280,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            Content = new TextBlock { Text = notes.Length > 0 ? notes : L.T("（发布说明为空）"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
                        }
                    }
                },
                PrimaryButtonText = L.T("下载"),
                SecondaryButtonText = L.T("跳过此版本"),
                CloseButtonText = L.T("稍后"),
                DefaultButton = ContentDialogButton.Primary
            };
            ContentDialogResult result;
            try { result = await dlg.ShowAsync(); }
            catch (Exception)   // 同一时间只能有一个对话框：别的对话框开着时，过一会儿再试
            {
                _updateDialogOpen = false;
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(30));
                ShowPendingUpdate();
                return;
            }
            _pendingUpdate = null;
            if (result == ContentDialogResult.Primary)
            {
                var url = UpdateService.IsTrustedUrl(info.DownloadUrl) ? info.DownloadUrl : info.PageUrl;
                if (UpdateService.IsTrustedUrl(url)) { try { await Windows.System.Launcher.LaunchUriAsync(new Uri(url!)); } catch { } }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                State.SkippedUpdateVersion = info.Latest.ToString(3);
                StateService.Instance.RequestSave();
            }
        }
        finally
        {
            _updateDialogOpen = false;
        }
    }

    /// <summary>发布说明是 Markdown：去掉标题井号和加粗符号，按纯文本显示。</summary>
    private static string CleanReleaseNotes(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var lines = md.Replace("\r\n", "\n").Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^\s*#{1,6}\s*", ""))
            .Select(l => l.Replace("**", "").Replace("`", ""));
        return string.Join("\n", lines).Trim();
    }

    #endregion

    #region 系统托盘 / 关闭按钮

    private Views.TrayIcon? _tray;
    private bool _exiting, _closePromptOpen;

    private void InitTray()
    {
        AppWindow.Closing += AppWindow_Closing;
        UpdateTrayIcon();
    }

    /// <summary>关闭行为不是“直接退出”，或者窗口正藏在托盘里时，显示托盘图标。</summary>
    public void UpdateTrayIcon()
    {
        bool want = State.CloseAction != "exit" || !AppWindow.IsVisible;
        if (want && _tray == null)
        {
            try
            {
                _tray = new Views.TrayIcon
                {
                    IsPlaying = () => AudioEngineService.Instance.IsPlaying,
                    IsDark = () => (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark
                };
                _tray.ShowRequested += ShowFromTray;
                _tray.PlayPauseRequested += TogglePlayPause;
                _tray.PrevRequested += PlayPrev;
                _tray.NextRequested += PlayNext;
                _tray.ExitRequested += ExitApp;
                UpdateTrayTip();
            }
            catch (Exception ex)
            {
                App.LogError("Tray", ex.Message);
                _tray = null;
            }
        }
        else if (!want && _tray != null)
        {
            _tray.Dispose();
            _tray = null;
        }
    }

    private void UpdateTrayTip() =>
        _tray?.SetToolTip(_currentTrack == null ? "Klang" : $"{_currentTrack.Title} - {_currentTrack.Artist}");

    /// <summary>
    /// 关掉当前打开的对话框（设置、更新提示等），等它真正关闭后返回；没有打开的对话框时返回 false。
    /// 对话框开着时直接关闭窗口，进程会在 CoreMessagingXP.dll 里崩溃；同一时间也只能开一个对话框，关闭询问弹不出来。
    /// </summary>
    private async System.Threading.Tasks.Task<bool> CloseOpenDialogsAsync()
    {
        if (Content?.XamlRoot is not { } root) return false;
        var waits = new List<System.Threading.Tasks.Task>();
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            if (popup.Child is not ContentDialog dlg) continue;
            var tcs = new System.Threading.Tasks.TaskCompletionSource();
            dlg.Closed += (s, e) => tcs.TrySetResult();
            dlg.Hide();
            waits.Add(tcs.Task);
        }
        if (waits.Count == 0) return false;
        await System.Threading.Tasks.Task.WhenAny(System.Threading.Tasks.Task.WhenAll(waits), System.Threading.Tasks.Task.Delay(1500));
        await System.Threading.Tasks.Task.Delay(50);   // 让对话框的关闭动画和弹出层清理走完
        return true;
    }

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        string action = _exiting ? "exit" : State.CloseAction;
        if (action == "exit")
        {
            // 有对话框开着：先取消这次关闭，关掉对话框后再关窗口
            if (Content?.XamlRoot != null && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot).Any(p => p.Child is ContentDialog))
            {
                args.Cancel = true;
                _exiting = true;
                await CloseOpenDialogsAsync();
                Close();
            }
            return;
        }

        args.Cancel = true;
        if (action == "tray")
        {
            await CloseOpenDialogsAsync();
            HideToTray();
            return;
        }

        // 第一次（或没记住选择时）：询问
        if (_closePromptOpen) return;
        _closePromptOpen = true;
        try
        {
            await CloseOpenDialogsAsync();
            var remember = new CheckBox { Content = L.T("记住我的选择，不再询问"), Margin = new Thickness(0, 12, 0, 0) };
            var dlg = new ContentDialog
            {
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
                RequestedTheme = ThemeService.Current,
                XamlRoot = Content.XamlRoot,
                Title = L.T("关闭 Klang"),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = L.T("最小化到系统托盘后会继续在后台播放，可以从托盘图标重新打开或退出。"), TextWrapping = TextWrapping.Wrap },
                        remember
                    }
                },
                PrimaryButtonText = L.T("最小化到托盘"),
                SecondaryButtonText = L.T("退出"),
                CloseButtonText = L.T("取消"),
                DefaultButton = ContentDialogButton.Primary
            };
            ContentDialogResult result;
            try { result = await dlg.ShowAsync(); }
            catch (Exception ex) { App.LogError("ClosePrompt", ex.Message); return; }   // 仍有对话框没关掉时 ShowAsync 会抛异常
            if (result == ContentDialogResult.None) return;
            bool toTray = result == ContentDialogResult.Primary;
            if (remember.IsChecked == true)
            {
                State.CloseAction = toTray ? "tray" : "exit";
                StateService.Instance.RequestSave();
            }
            if (toTray) HideToTray(); else ExitApp();
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    private void HideToTray()
    {
        CrashReporter.Note("最小化到托盘");
        SaveWindowAndAudioState();
        StateService.Instance.Flush();
        AppWindow.Hide();
        UpdateTrayIcon();
    }

    /// <summary>从托盘恢复（或第二次启动 Klang 时把已有窗口带到前台）。</summary>
    public void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        UpdateTrayIcon();
        ShowPendingUpdate();   // 藏在托盘期间发现的新版本，窗口恢复后再提示
    }

    /// <summary>真正退出（托盘菜单、关闭询问里选“退出”）。</summary>
    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    #endregion

    #region 动态封面

    private string? _dynCoverPath;
    private System.Threading.CancellationTokenSource? _dynCoverCts;

    /// <summary>换歌时取这首歌的动态封面（目前只有网易云，需要登录）；取到后盖在静态封面上播放。</summary>
    private async void LoadDynamicCover(TrackItem? track)
    {
        _dynCoverCts?.Cancel();
        _dynCoverCts = null;
        _dynCoverPath = null;
        ApplyDynamicCover();
        if (track == null || !track.IsOnline || !State.DynamicCover) return;
        var source = Services.Cloud.CloudSources.For(track);
        if (source == null) return;

        var cts = _dynCoverCts = new System.Threading.CancellationTokenSource();
        string? path = null;
        try { path = await source.GetDynamicCoverAsync(track.CloudId, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { App.LogError("DynamicCover", ex.Message); }
        if (cts.IsCancellationRequested || _currentTrack != track || path == null) return;
        _dynCoverPath = path;
        ApplyDynamicCover();
    }

    /// <summary>动态封面只在当前看得到的那个封面上播放：沉浸模式打开时在沉浸界面，否则在主界面；随音乐暂停。</summary>
    private void ApplyDynamicCover()
    {
        bool playing = AudioEngineService.Instance.IsPlaying;
        bool immersive = ImmersiveLayer.IsOpen;
        StageCoverCard.SetDynamicCover(immersive ? null : _dynCoverPath, playing);
        ImmersiveLayer.SetDynamicCover(immersive ? _dynCoverPath : null, playing);
    }

    #endregion

    /// <summary>云端封面缓存完成后刷新各处封面。</summary>
    private void RefreshTrackCover(TrackItem track)
    {
        if (_currentTrack != track) return;
        StageCoverCard.UpdateTrack(track);
        BottomTransportBar.UpdateTrackState(track.Title, track.Artist, track.CoverPath);
        if (_onlineStatus.Length > 0) BottomTransportBar.UpdateLyricSnippet(_onlineStatus);
        if (ImmersiveLayer.IsOpen) ImmersiveLayer.SetTrack(track);
        _media.UpdateTrack(track);
        _miniPlayerWindow?.UpdateTrack(track);
        _taskbarPlayerWindow?.UpdateTrack(track);
    }

    private void TogglePlayPause()
    {
        // MV 播放中：系统媒体键 / 托盘菜单的播放暂停作用于 MV
        if (MvLayer.IsOpen) { MvLayer.TogglePlayPause(); return; }
        var engine = AudioEngineService.Instance;
        if (_currentTrack != null && !engine.HasSource)
        {
            // 恢复的曲目 / 播放已结束：从头开始播放
            PlayTrack(_currentTrack);
        }
        else if (_currentTrack == null)
        {
            LeftNavPane.LockQueueToDisplay();
            if (LeftNavPane.DisplayTracks.Count > 0) PlayTrack(LeftNavPane.DisplayTracks[0]);
        }
        else
        {
            engine.TogglePlayPause();
        }
    }

    private TrackItem? _preChosenNext;
    private string? _preChosenReason;
    private bool _preChosenByRoam;

    /// <summary>按当前播放模式挑下一首（不播放）。漫游模式的理由和“是漫游挑的”一并返回。</summary>
    private TrackItem? PickNextTrack(out string? reason, out bool byRoam)
    {
        reason = null; byRoam = false;

        // “下一首播放”点过的歌优先，不受随机 / 漫游模式影响
        foreach (var up in LeftNavPane.UpNext)
            if (up != _currentTrack && (!up.IsMissing || up.IsOnline)) return up;

        var tracks = LeftNavPane.Queue;
        if (tracks.Count == 0) return null;

        int nextIdx = 0;
        if (_currentTrack != null)
        {
            int currentIdx = tracks.IndexOf(_currentTrack);
            if (currentIdx >= 0)
            {
                if (_playMode == "roam" && tracks.Count > 1)
                {
                    var pick = RoamingService.Instance.PickNext(_currentTrack, tracks, LibraryService.Instance.Tracks);
                    if (pick != null)
                    {
                        byRoam = true;
                        reason = pick.Value.Reason;
                        return pick.Value.Track;
                    }
                    nextIdx = (currentIdx + 1) % tracks.Count;
                }
                else if (_playMode == "random" && tracks.Count > 1)
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
        return tracks[nextIdx];
    }

    private void PlayNext()
    {
        if (MvLayer.IsOpen) CloseMv(resume: false);
        TrackItem? next; string? reason; bool byRoam;
        if (_preChosenNext != null)
        {
            next = _preChosenNext; reason = _preChosenReason; byRoam = _preChosenByRoam;
            _preChosenNext = null;
        }
        else next = PickNextTrack(out reason, out byRoam);
        if (next == null) return;

        _nextByRoam = byRoam;
        PlayTrack(next);
        if (reason != null) BottomTransportBar.UpdateLyricSnippet(L.T("漫游 · ") + reason);
    }

    // ---------------- 云端曲目预缓冲 ----------------

    private string? _prefetchFromId;

    /// <summary>
    /// 当前这首快放完时（剩余 60 秒以内），提前确定下一首；如果它是还没缓存的云端曲目，就在后台开始下载，
    /// 切过去时不用再等“正在获取云端音源”。每首歌只触发一次。
    /// </summary>
    private void TryPrefetchNext(double current, double total)
    {
        var cur = _currentTrack;
        if (cur == null || total <= 0 || _playMode == "loop_single" || _prefetchFromId == cur.Id) return;
        if (!AudioEngineService.Instance.IsPlaying) return;
        if (total - current > 60) return;

        _prefetchFromId = cur.Id;
        try
        {
            // 和自动混音共用“预先选好的下一首”，保证预缓冲的就是之后真正播放的那一首（随机 / 漫游模式下尤其重要）
            var next = _preChosenNext;
            if (next == null)
            {
                next = PickNextTrack(out var reason, out var byRoam);
                if (next != null) { _preChosenNext = next; _preChosenReason = reason; _preChosenByRoam = byRoam; }
            }
            if (next == null || next == cur || !next.IsOnline) return;
            Services.Cloud.CloudSources.For(next)?.Prefetch(next);
        }
        catch { }
    }

    // ---------------- 节拍对齐的自动混音 ----------------


    private TrackItem? _mixNext;
    private MixPlan? _mixPlan;
    private string? _plannedFromId;
    private bool _adoptingMix;
    private bool _planning;

    /// <summary>快放完时：挑好下一首，算出该在哪个拍点开始混，交给引擎。任何一步不满足就什么都不做，照常切歌。</summary>
    private async void TryPlanMix(double current, double total)
    {
        var mode = State.AutoMix;
        if (mode == "off" || _planning || _currentTrack == null || _playMode == "loop_single") return;
        if (mode == "roam" && _playMode != "roam") return;
        var a = _currentTrack;
        if (a.IsCueTrack || _plannedFromId == a.Id) return;
        // 云端曲目播放的是缓存到本地的文件，同样可以混；还没有本地文件就不混
        if (string.IsNullOrEmpty(a.FilePath) || !System.IO.File.Exists(a.FilePath)) return;
        var engine = AudioEngineService.Instance;
        if (!engine.IsPlaying || engine.MixBusy) return;
        double remaining = total - current;
        if (remaining > 45 || remaining < 3) return;

        _planning = true;
        _plannedFromId = a.Id;
        try
        {
            TrackItem? next;
            if (_preChosenNext != null) next = _preChosenNext;
            else
            {
                next = PickNextTrack(out var reason, out var byRoam);
                if (next != null) { _preChosenNext = next; _preChosenReason = reason; _preChosenByRoam = byRoam; }
            }
            if (next == null || next == a || next.IsCueTrack || next.IsMissing) return;

            // 下一首是云端曲目：要等预缓冲把它下载到本地。还没下完就先不算“已规划”，稍后再试；
            // 剩余时间不多了还没好，就放弃混音，照常切歌
            if (next.IsOnline)
            {
                var source = Services.Cloud.CloudSources.For(next);
                if (source == null) return;
                if (!source.IsCached(next))
                {
                    source.Prefetch(next);
                    if (remaining > 12) _plannedFromId = null;
                    return;
                }
                if (string.IsNullOrEmpty(next.FilePath) || !System.IO.File.Exists(next.FilePath))
                    await source.ResolveAsync(next, System.Threading.CancellationToken.None);   // 已缓存：只是把本地路径写到曲目上
            }
            if (string.IsNullOrEmpty(next.FilePath) || !System.IO.File.Exists(next.FilePath)) return;

            // 还没分析过节拍的（云端曲目、刚导入的歌）：现在补一下（解码一首歌一般不到 1 秒）
            foreach (var t in new[] { a, next })
            {
                if (t.RhythmDone) continue;
                RhythmResult? res = null;
                try { res = await AnalysisWorker.Instance.RhythmAsync(t.FilePath, t.Duration); } catch { }
                RhythmAnalysisService.Apply(t, res);
            }
            if (_currentTrack != a || !engine.IsPlaying) return;

            // 先试对拍混音；对不上（速度差太多、没有稳定节拍）就退回普通的交叉淡入淡出
            string len = State.AutoMixLength;
            var plan = MixPlanner.Plan(a, next, engine.CurrentTime, engine.TotalDuration, len);
            bool ok;
            if (plan != null)
            {
                ok = engine.TryScheduleMix(next.FilePath, plan.NextStart, plan.StartAt, plan.FadeSec, plan.RateRatio,
                    bassSwap: true, skipLeadingSilence: false, nextGainDb: next.ReplayGainDb, nextPeak: next.ReplayPeak);
            }
            else
            {
                plan = MixPlanner.PlanPlain(a, engine.CurrentTime, engine.TotalDuration, len);
                ok = plan != null && engine.TryScheduleMix(next.FilePath, 0, plan.StartAt, plan.FadeSec, 1.0,
                    bassSwap: false, skipLeadingSilence: true, nextGainDb: next.ReplayGainDb, nextPeak: next.ReplayPeak);
            }
            if (ok) { _mixNext = next; _mixPlan = plan; }
        }
        catch { }
        finally { _planning = false; }
    }

    /// <summary>引擎已经淡到一半：界面切到新歌，但不要再让引擎重新开始播放。</summary>
    private void OnMixAdopted(string path)
    {
        CrashReporter.Note("自动混音切到下一首");
        var next = _mixNext; _mixNext = null;
        if (next == null || next.FilePath != path) return;
        _endedNaturally = true;                 // 对漫游模型来说，上一首是完整听完的
        _nextByRoam = _preChosenByRoam;
        string? reason = _preChosenReason;
        _preChosenNext = null;
        _adoptingMix = true;
        try { PlayTrack(next); }
        finally { _adoptingMix = false; }
        var mp = _mixPlan; _mixPlan = null;
        string mixText = mp != null && !mp.BeatMatched
            ? L.T("交叉淡入淡出")
            : L.F("节拍混音 · {0:0} BPM", next.Bpm) + (string.IsNullOrEmpty(next.Camelot) ? "" : " · " + next.Camelot);
        BottomTransportBar.UpdateLyricSnippet(reason != null ? L.T("漫游 · ") + reason + "  ·  " + mixText : mixText);
    }

    private void PlayPrev()
    {
        if (MvLayer.IsOpen) CloseMv(resume: false);
        var tracks = LeftNavPane.Queue;
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

    private void ToggleFavorite(TrackItem track, bool fromTransport = false)
    {
        if (track.IsOnline)
        {
            BottomTransportBar.UpdateLyricSnippet(L.T("云端曲目暂不支持“喜欢”"));
            BottomTransportBar.SetFavorite(false);
            return;
        }
        track.IsFavorite = !track.IsFavorite;
        if (track == _currentTrack && !fromTransport)
        {
            BottomTransportBar.SetFavorite(track.IsFavorite);
        }
        LibraryService.Instance.SaveLibrary();

        if (track == _currentTrack) _taskbarPlayerWindow?.UpdateFavorite();
        // “喜欢”页里取消喜欢后该曲目应立即从列表消失
        if (LeftNavPane.CurrentTab == "fav") LeftNavPane.RefreshView();
    }

    private void SeekBy(double deltaSeconds)
    {
        var engine = AudioEngineService.Instance;
        if (!engine.HasSource) return;
        double pos = Math.Clamp(engine.CurrentTime + deltaSeconds, 0, engine.TotalDuration);
        engine.Seek(pos);
        StageLyricsControl.UpdatePlaybackTime(pos + _lyricOffset); ImmersiveLayer.UpdateTime(pos + _lyricOffset);
    }

    private void ChangeVolume(float delta)
    {
        BottomTransportBar.SetVolume(AudioEngineService.Instance.Volume + delta);
    }

    #endregion

    #region 键盘快捷键

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var focused = this.Content?.XamlRoot != null
            ? FocusManager.GetFocusedElement(this.Content.XamlRoot) as DependencyObject
            : null;

        bool ctrl = IsKeyDown(VirtualKey.Control);
        bool alt = IsKeyDown(VirtualKey.Menu);
        bool typing = focused is TextBox || focused is PasswordBox || focused is RichEditBox;
        // 列表 / 滑块 / 下拉框自己要用方向键，不抢
        bool navigating = focused is ListViewItem || focused is Slider || focused is ComboBox || focused is ComboBoxItem;
        var key = e.Key;

        // 任何情况下都生效
        if (key == VirtualKey.F11)
        {
            ToggleFullscreen();
            if (MvLayer.IsOpen) UpdateMvCaptionInset();
            e.Handled = true;
            return;
        }
        // MV 播放层打开时：Esc 关闭、空格暂停 / 继续；方向键交给播放控制条，其余快捷键不响应（底下的播放器让出了声卡）
        if (MvLayer.IsOpen)
        {
            if (key == VirtualKey.Escape) { CloseMv(resume: true); e.Handled = true; }
            else if (key == VirtualKey.Space && !typing) { MvLayer.TogglePlayPause(); e.Handled = true; }
            else if (key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Tab or VirtualKey.Enter)) e.Handled = true;
            return;
        }
        if (key == VirtualKey.Escape && GalleryLayer.Visibility == Visibility.Visible)
        {
            CloseGallery();
            e.Handled = true;
            return;
        }
        if (key == VirtualKey.Escape && ImmersiveLayer.IsOpen)
        {
            CloseImmersive();
            e.Handled = true;
            return;
        }
        if (ctrl && key == VirtualKey.F)
        {
            if (!_isSidebarOpen) SetSidebarOpen(true);
            LeftNavPane.FocusSearch();
            e.Handled = true;
            return;
        }

        // 在输入框里打字时，不拦截其余按键
        if (typing || alt) return;

        if (ctrl)
        {
            switch (key)
            {
                case VirtualKey.B: SetTaskbarPlayerEnabled(!State.TaskbarPlayerEnabled); e.Handled = true; return;
                case VirtualKey.M: ToggleMiniPlayer(); e.Handled = true; return;
                case VirtualKey.D: ToggleDesktopLyric(); e.Handled = true; return;
                case VirtualKey.T: CycleSubMode(); e.Handled = true; return;
                case VirtualKey.I: ToggleImmersive(); e.Handled = true; return;
                case VirtualKey.Left: PlayPrev(); e.Handled = true; return;
                case VirtualKey.Right: PlayNext(); e.Handled = true; return;
                case VirtualKey.Add:
                case (VirtualKey)187: // Ctrl + '='（即 Ctrl + +）
                    StageLyricsControl.LyricFontSize += 2;
                    SaveLyricFontSize();
                    e.Handled = true;
                    return;
                case VirtualKey.Subtract:
                case (VirtualKey)189: // Ctrl + '-'
                    StageLyricsControl.LyricFontSize -= 2;
                    SaveLyricFontSize();
                    e.Handled = true;
                    return;
            }
            return;
        }

        switch (key)
        {
            case VirtualKey.Space:
                TogglePlayPause();
                e.Handled = true;
                break;
            case (VirtualKey)191: // '/' 聚焦搜索框
                if (!_isSidebarOpen) SetSidebarOpen(true);
                LeftNavPane.FocusSearch();
                e.Handled = true;
                break;
            case VirtualKey.Left when !navigating:
                SeekBy(-5);
                e.Handled = true;
                break;
            case VirtualKey.Right when !navigating:
                SeekBy(5);
                e.Handled = true;
                break;
            case VirtualKey.Up when !navigating:
                ChangeVolume(0.05f);
                e.Handled = true;
                break;
            case VirtualKey.Down when !navigating:
                ChangeVolume(-0.05f);
                e.Handled = true;
                break;
        }
    }

    private void SaveLyricFontSize()
    {
        State.LyricFontSize = StageLyricsControl.LyricFontSize;
        StateService.Instance.RequestSave();
    }

    #endregion

    #region 对话框 / 窗口

    private void ApplyBackdrop(string material)
    {
        this.SystemBackdrop = material switch
        {
            "acrylic" => new DesktopAcrylicBackdrop(),
            _ => new MicaBackdrop()   // 旧版本保存的 "solid"（已移除）也按云母处理
        };
    }

    private async System.Threading.Tasks.Task OpenEqualizerDialogAsync()
    {
        try
        {
            if (this.Content?.XamlRoot == null) return;

            var eqDialog = new EqualizerDialog
            {
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ThemeService.ToElementTheme(State.Theme)
            };

            await eqDialog.ShowAsync();
            BottomTransportBar.UpdateEqBadge(AudioEngineService.Instance.CurrentPreset);

            // 音效设置持久化
            AudioEngineService.Instance.SaveSettingsToState(State);
            StateService.Instance.RequestSave();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EQ dialog error: {ex.Message}");
        }
    }

    private const double SidebarOpenWidth = 342;
    private const double SidebarRailWidth = 56;

    private void SetSidebarOpen(bool open)
    {
        _isSidebarOpen = open;
        State.SidebarCollapsed = !open;
        StateService.Instance.RequestSave();
        AnimateSidebar(open ? SidebarOpenWidth : SidebarRailWidth, animate: IsLoaded());
    }

    private bool IsLoaded() => Content is FrameworkElement fe && fe.IsLoaded;

    private void SidebarHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 宽度收缩过程中裁剪侧栏，避免内容溢出到舞台上
        SidebarHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sidebarTimer;

    /// <summary>侧栏宽度缓动（easeOutCubic，约 240ms）。</summary>
    private void AnimateSidebar(double target, bool animate)
    {
        _sidebarTimer?.Stop();
        double from = SidebarColumn.Width.Value;
        if (!animate || Math.Abs(from - target) < 1)
        {
            SidebarColumn.Width = new GridLength(target);
            ShowSidebarPart(target > SidebarRailWidth + 1);
            return;
        }

        // 展开显示完整侧栏，折叠显示竖排图标栏（两者带淡入淡出）
        ShowSidebarPart(target > SidebarRailWidth + 1);
        var start = DateTime.UtcNow;
        const double ms = 240;
        _sidebarTimer = DispatcherQueue.CreateTimer();
        _sidebarTimer.Interval = TimeSpan.FromMilliseconds(8);
        _sidebarTimer.Tick += (s, e) =>
        {
            double t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / ms);
            double eased = 1 - Math.Pow(1 - t, 3);
            SidebarColumn.Width = new GridLength(from + (target - from) * eased);
            if (t >= 1)
            {
                _sidebarTimer?.Stop();
            }
        };
        _sidebarTimer.Start();
    }

    private void ShowSidebarPart(bool full)
    {
        LeftNavPane.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>竖排图标栏：高亮当前分页。</summary>
    private string _railTab = "all";

    private void UpdateRail(string tab)
    {
        _railTab = tab;
        var root = (FrameworkElement)Content;
        var accent = ThemeService.AccentText(root);
        var normal = ThemeService.Brush(root, "TextFillColorPrimaryBrush");
        var selBg = ThemeService.Brush(root, "SubtleFillColorSecondaryBrush");
        foreach (var (btn, tag) in new[] { (RailAll, "all"), (RailFav, "fav"), (RailAlbums, "albums"), (RailPlaylists, "playlists"), (RailCloud, "netease") })
        {
            bool sel = tag == tab;
            btn.Background = sel ? selBg : null;
            if (btn.Content is StackPanel { Children: [FontIcon icon, ..] }) icon.Foreground = sel ? accent : normal;
        }
    }

    private void RailTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            SetSidebarOpen(true);
            LeftNavPane.SelectTab(tag);
        }
    }

    private void RailSearch_Click(object sender, RoutedEventArgs e)
    {
        SetSidebarOpen(true);
        DispatcherQueue.SafeEnqueue(() => LeftNavPane.FocusSearch());
    }

    private void SidebarToggleBtn_Click(object sender, RoutedEventArgs e) => SetSidebarOpen(!_isSidebarOpen);

    private async void DiscoverReport_Click(object sender, RoutedEventArgs e) =>
        await Dialogs.ListeningReportDialog.ShowAsync(Content.XamlRoot);

    private void DiscoverMap_Click(object sender, RoutedEventArgs e) => LeftNavPane.ShowAcousticMap();

    private void DesktopLyricBtn_Click(object sender, RoutedEventArgs e) => ToggleDesktopLyric();

    private void ToggleDesktopLyric()
    {
        _isDesktopLyricVisible = !_isDesktopLyricVisible;
        if (_desktopLyricWindow == null)
        {
            _desktopLyricWindow = new DesktopLyricWindow();
            _desktopLyricWindow.PrevRequested += (s, ev) => PlayPrev();
            _desktopLyricWindow.NextRequested += (s, ev) => PlayNext();
            _desktopLyricWindow.ClosedByUser += (s, ev) =>
            {
                _isDesktopLyricVisible = false;
                UpdateDesktopLyricBtnState();
            };
        }

        if (_isDesktopLyricVisible)
        {
            _desktopLyricWindow.UpdateLyrics(StageLyricsControl.Lyrics);
            _desktopLyricWindow.UpdateTrack(_currentTrack);
            _desktopLyricWindow.UpdatePlayState(AudioEngineService.Instance.IsPlaying);
            _desktopLyricWindow.AppWindow.Show();
        }
        else
        {
            _desktopLyricWindow.AppWindow.Hide();
        }

        UpdateDesktopLyricBtnState();
    }

    private void UpdateDesktopLyricBtnState()
    {
        DesktopLyricItem.IsChecked = _isDesktopLyricVisible;
    }

    private void MiniPlayerBtn_Click(object sender, RoutedEventArgs e) => ToggleMiniPlayer();

    private void TaskbarPlayerItem_Click(object sender, RoutedEventArgs e) =>
        SetTaskbarPlayerEnabled(TaskbarPlayerItem.IsChecked);

    private void SetTaskbarPlayerEnabled(bool enabled)
    {
        if (enabled && _taskbarPlayerWindow == null)
        {
            _taskbarPlayerWindow = new TaskbarPlayerWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            _taskbarPlayerWindow.PrevRequested += (s, e) => PlayPrev();
            _taskbarPlayerWindow.NextRequested += (s, e) => PlayNext();
            _taskbarPlayerWindow.PlayPauseRequested += (s, e) => TogglePlayPause();
            _taskbarPlayerWindow.FavoriteRequested += (s, e) =>
            {
                if (_currentTrack != null) ToggleFavorite(_currentTrack);
            };
            _taskbarPlayerWindow.RestoreRequested += (s, e) =>
            {
                AppWindow.Show();
                Activate();
            };
            _taskbarPlayerWindow.DismissRequested += (s, e) => SetTaskbarPlayerEnabled(false);
        }
        State.TaskbarPlayerEnabled = enabled;
        TaskbarPlayerItem.IsChecked = enabled;
        StateService.Instance.RequestSave();
        if (!enabled)
        {
            // 关掉就整个关闭窗口：连同后台的任务栏查询线程一起停掉，不再占用 Klang 和 Explorer 的 CPU
            var w = _taskbarPlayerWindow;
            _taskbarPlayerWindow = null;
            if (w != null) { w.SetEnabled(false); w.Close(); }
            return;
        }
        _taskbarPlayerWindow?.UpdateTrack(_currentTrack);
        _taskbarPlayerWindow?.UpdatePlayState(AudioEngineService.Instance.IsPlaying);
        _taskbarPlayerWindow?.SetEnabled(true);
    }

    private void ToggleMiniPlayer()
    {
        if (_miniPlayerWindow == null)
        {
            _miniPlayerWindow = new MiniPlayerWindow();
            ThemeService.Apply(_miniPlayerWindow, State.Theme);
            _miniPlayerWindow.PrevRequested += (s, ev) => PlayPrev();
            _miniPlayerWindow.NextRequested += (s, ev) => PlayNext();
            _miniPlayerWindow.SeekRequested += (s, pos) =>
            {
                AudioEngineService.Instance.Seek(pos);
                StageLyricsControl.UpdatePlaybackTime(pos + _lyricOffset); ImmersiveLayer.UpdateTime(pos + _lyricOffset);
            };
            _miniPlayerWindow.RestoreRequested += (s, ev) =>
            {
                this.AppWindow.Show();
                this.Activate();
            };
            _miniPlayerWindow.ClosedByUser += (s, ev) =>
            {
                this.AppWindow.Show();
                this.Activate();
            };
        }

        _miniPlayerWindow.UpdateTrack(_currentTrack);
        _miniPlayerWindow.UpdatePlayState(AudioEngineService.Instance.IsPlaying);
        _miniPlayerWindow.UpdateProgress(AudioEngineService.Instance.CurrentTime, AudioEngineService.Instance.TotalDuration);
        _miniPlayerWindow.AppWindow.Show();
        this.AppWindow.Hide();
    }

    #region 歌词副行 / 海报 / 沉浸模式

    private static readonly string[] SubModes = { "trans", "roma", "both", "off" };
    private static readonly string[] SubModeLabels = { "翻译", "罗马音", "双行", "关" };

    private void SubModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string mode }) ApplySubMode(mode, announce: true);
    }

    private void CycleSubMode()
    {
        int i = Array.IndexOf(SubModes, State.LyricSubMode);
        ApplySubMode(SubModes[(i + 1) % SubModes.Length], announce: true);
    }

    private void ApplySubMode(string mode, bool announce)
    {
        int i = Array.IndexOf(SubModes, mode);
        if (i < 0) { i = 0; mode = SubModes[0]; }
        State.LyricSubMode = mode;
        StateService.Instance.RequestSave();
        SubTransItem.IsChecked = mode == "trans";
        SubRomaItem.IsChecked = mode == "roma";
        SubBothItem.IsChecked = mode == "both";
        SubOffItem.IsChecked = mode == "off";
        StageLyricsControl.SubMode = mode;
        ImmersiveLayer.SubMode = mode;
        if (announce) BottomTransportBar.UpdateLyricSnippet(L.F("歌词副行：{0}", L.T(SubModeLabels[i])));
    }

    // ---------------- 歌词时间校准 / 在线补全 ----------------

    /// <summary>当前曲目的歌词偏移（秒）。正值 = 歌词提前出现。显示时间 = 播放位置 + 偏移。</summary>
    private double _lyricOffset;

    private void LoadLyricOffset(TrackItem? track)
    {
        _lyricOffset = 0;
        if (track != null && State.LyricOffsets.TryGetValue(track.Id, out var o)) _lyricOffset = o;
        UpdateOffsetLabel();
        // 本地双语歌词的“原文与翻译对调”也按歌曲记住；在载入新歌词之前设好
        StageLyricsControl.SwapPairs = track != null && State.LyricSwapTracks.Contains(track.Id);
    }

    private void LyricMenu_Opening(object? sender, object e)
    {
        SwapPairsItem.IsEnabled = StageLyricsControl.HasPairedLines;
        SwapPairsItem.IsChecked = StageLyricsControl.SwapPairs;
        ToolTipService.SetToolTip(SwapPairsItem, SwapPairsItem.IsEnabled
            ? L.T("双语歌词把原文和翻译认反时使用，按歌曲记住")
            : L.T("当前歌词没有同一时间写成两行的双语歌词"));
    }

    private void SwapPairsItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTrack == null || !StageLyricsControl.HasPairedLines) return;
        bool swap = !StageLyricsControl.SwapPairs;
        StageLyricsControl.SwapPairs = swap;
        State.LyricSwapTracks.Remove(_currentTrack.Id);
        if (swap) State.LyricSwapTracks.Add(_currentTrack.Id);
        StateService.Instance.RequestSave();
        SyncImmersive();
        _desktopLyricWindow?.UpdateLyrics(StageLyricsControl.Lyrics);
        BottomTransportBar.UpdateLyricSnippet(swap ? L.T("已对调原文与翻译") : L.T("已恢复原文与翻译"));
    }

    private void SetLyricOffset(double v)
    {
        _lyricOffset = Math.Round(Math.Clamp(v, -30, 30), 2);
        if (_currentTrack != null)
        {
            if (Math.Abs(_lyricOffset) < 0.005) State.LyricOffsets.Remove(_currentTrack.Id);
            else State.LyricOffsets[_currentTrack.Id] = _lyricOffset;
            StateService.Instance.RequestSave();
        }
        UpdateOffsetLabel();
        double now = AudioEngineService.Instance.CurrentTime + _lyricOffset;
        StageLyricsControl.UpdatePlaybackTime(now); ImmersiveLayer.UpdateTime(now);
        BottomTransportBar.UpdateLyricSnippet(L.F("歌词偏移：{0} 秒", _lyricOffset.ToString("+0.0;-0.0;0.0")));
    }

    private void UpdateOffsetLabel() =>
        LyricOffsetInfo.Text = L.F("当前偏移：{0} 秒", _lyricOffset.ToString("+0.0;-0.0;0.0"));

    private void LyricEarlier_Click(object sender, RoutedEventArgs e) => SetLyricOffset(_lyricOffset + 0.5);
    private void LyricLater_Click(object sender, RoutedEventArgs e) => SetLyricOffset(_lyricOffset - 0.5);
    private void LyricOffsetReset_Click(object sender, RoutedEventArgs e) => SetLyricOffset(0);

    private void LyricCalibrate_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTrack == null) return;
        StageLyricsControl.CalibrationMode = true;
        BottomTransportBar.UpdateLyricSnippet(L.T("请点击当前正在演唱的歌词行"));
    }

    private void StageLyricsControl_CalibrationPicked(object? sender, double lineTime)
    {
        // 此刻引擎位置 pos 唱的是 lineTime 这句 → 需要 pos + offset = lineTime
        SetLyricOffset(lineTime - AudioEngineService.Instance.CurrentTime);
    }

    /// <summary>“选择歌词”：列出本地与在线的候选，让用户自己挑；选定后按曲目记住。</summary>
    private async void LyricChoose_Click(object sender, RoutedEventArgs e)
    {
        var track = _currentTrack;
        if (track == null)
        {
            BottomTransportBar.UpdateLyricSnippet(L.T("请先播放歌曲，再选择歌词"));
            return;
        }
        try
        {
            var (outcome, set) = await LyricPickerDialog.ShowAsync(Content.XamlRoot, track, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (_currentTrack != track) return;
            switch (outcome)
            {
                case LyricPickerDialog.Outcome.Use when set != null:
                    LyricsService.SaveChosen(track.Id, set);
                    StageLyricsControl.LoadLyricLines(LyricsService.Instance.ParseLrc(set.Lrc, set.Trans, set.Roma));
                    SyncImmersive();
                    _desktopLyricWindow?.UpdateLyrics(StageLyricsControl.Lyrics);
                    BottomTransportBar.UpdateLyricSnippet(L.T("已使用选定的歌词"));
                    CrashReporter.Note("选择歌词：" + track.Title);
                    break;
                case LyricPickerDialog.Outcome.Reset:
                    LyricsService.ClearChosen(track.Id);
                    ReloadAutomaticLyrics(track);
                    BottomTransportBar.UpdateLyricSnippet(L.T("已恢复自动匹配的歌词"));
                    break;
            }
        }
        catch (Exception ex)
        {
            App.LogError("Lyric picker", ex);
        }
    }

    /// <summary>撤销用户选定后，按原来的顺序重新取歌词（本地 → 网易云 / LRCLIB）。</summary>
    private void ReloadAutomaticLyrics(TrackItem track)
    {
        if (track.IsOnline)
        {
            StageLyricsControl.LoadLyrics(null);
            _ = ReloadOnlineLyricsAsync(track);
        }
        else
        {
            StageLyricsControl.LoadLyrics(track.IsCueTrack ? null : track.FilePath);
            if (!track.IsCueTrack) _ = FillLocalMetaAsync(track);
        }
        SyncImmersive();
        _desktopLyricWindow?.UpdateLyrics(StageLyricsControl.Lyrics);
    }

    private async System.Threading.Tasks.Task ReloadOnlineLyricsAsync(TrackItem track)
    {
        try
        {
            var source = Services.Cloud.CloudSources.For(track);
            if (source == null) return;
            var b = await source.GetLyricsAsync(track.CloudId);
            if (_currentTrack != track || string.IsNullOrWhiteSpace(b.Lrc) || LyricsService.HasChosen(track.Id)) return;
            StageLyricsControl.LoadLyricLines(LyricsService.Instance.ParseLrc(b.Lrc, b.Trans, b.Roma));
            SyncImmersive();
            _desktopLyricWindow?.UpdateLyrics(StageLyricsControl.Lyrics);
        }
        catch { }
    }

    private void ApplyOnlineLyrics(string lrc)
    {
        StageLyricsControl.LoadLyricLines(LyricsService.Instance.ParseLrc(lrc));
        SyncImmersive();
        _desktopLyricWindow?.UpdateLyrics(StageLyricsControl.Lyrics);
    }

    private async void PosterBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTrack == null)
        {
            BottomTransportBar.UpdateLyricSnippet(L.T("请先播放歌曲，再生成海报"));
            return;
        }
        var lyrics = StageLyricsControl.Lyrics;
        int active = LyricsService.Instance.FindActiveIndex(lyrics, AudioEngineService.Instance.CurrentTime + _lyricOffset);
        try
        {
            var dlg = new PosterDialog(_currentTrack, lyrics.ToList(), active)
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = ThemeService.ToElementTheme(State.Theme)
            };
            await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Poster dialog error: {ex.Message}");
        }
    }

    private bool _immersiveSetFullscreen;

    // ---------------- MV ----------------

    private bool _mvSetFullscreen, _mvResumePlay;

    /// <summary>
    /// 播放这首歌的 MV：暂停当前的歌并让出声卡（独占模式下普通暂停仍占着设备，MV 会没声音），
    /// MV 用它自己的声音播放；关闭后回到原来的歌和位置。
    /// </summary>
    public async void PlayMv(TrackItem track)
    {
        var source = Services.Cloud.CloudSources.For(track);
        if (source == null || !source.SupportsMv || MvLayer.IsOpen) return;
        CrashReporter.Note($"播放 MV：{track.Artist} - {track.Title}");
        if (ImmersiveLayer.IsOpen) CloseImmersive();
        if (GalleryLayer.Visibility == Visibility.Visible) CloseGallery();
        _mvResumePlay = AudioEngineService.Instance.IsPlaying;
        UpdateMvCaptionInset();
        MvLayer.Open(track, source);
        await AudioEngineService.Instance.SuspendOutputAsync();
    }

    /// <summary>MV 播放层的全屏按钮图标跟随窗口当前是否全屏。</summary>
    private void UpdateMvCaptionInset()
    {
        double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        double titleBar = AppWindow.TitleBar.Height > 0 ? AppWindow.TitleBar.Height / scale : 48;
        MvLayer.SetFullscreenState(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen, titleBar);
    }

    /// <summary>封面上的 MV 按钮：当前歌曲有 MV 才显示；还不知道有没有时（从保存的队列恢复的）先查一下。</summary>
    private async void UpdateMvButton(TrackItem track)
    {
        var source = Services.Cloud.CloudSources.For(track);
        bool supported = track.IsOnline && source?.SupportsMv == true;
        StageCoverCard.SetMvAvailable(supported && track.HasMv);
        if (!supported || track.MvId.Length > 0) return;
        try { await source!.EnsureMvIdAsync(track); } catch { return; }
        if (_currentTrack == track) StageCoverCard.SetMvAvailable(track.HasMv);
    }

    private void CloseMv(bool resume)
    {
        if (!MvLayer.IsOpen) return;
        MvLayer.Close();
        if (_mvSetFullscreen) { ExitFullscreen(); _mvSetFullscreen = false; }
        var engine = AudioEngineService.Instance;
        if (resume && engine.IsSuspended) engine.ResumeOutput(_mvResumePlay);
        else engine.ForgetSuspended();
    }

    private void ImmersiveBtn_Click(object sender, RoutedEventArgs e) => ToggleImmersive();

    // ---------------- 封面画廊 ----------------

    private void GalleryBtn_Click(object sender, RoutedEventArgs e) => ToggleGallery();

    private void ToggleGallery()
    {
        if (GalleryLayer.Visibility == Visibility.Visible) CloseGallery(); else OpenGallery();
    }

    private void OpenGallery()
    {
        GalleryLayer.Load(LibraryService.Instance.Tracks);
        GalleryLayer.SetCurrent(_currentTrack);
        GalleryLayer.SetIsPlaying(AudioEngineService.Instance.IsPlaying);
        GalleryLayer.Visibility = Visibility.Visible;
        GalleryLayer.PlayOpenAnimation();
    }

    private void CloseGallery()
    {
        GalleryLayer.Visibility = Visibility.Collapsed;
        GalleryLayer.ReleaseImages();   // 几十上百张封面，关掉后不必一直占着内存
        ScheduleMemoryTrim();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _trimTimer;

    /// <summary>
    /// 换歌 / 关掉画廊几秒后做一次后台垃圾回收：托管堆很小（约 20 MB），.NET 很少主动回收，
    /// 而换下来的封面图片背后的原生内存（含显存）要等回收时才释放，不回收的话会随着切歌越积越多。
    /// </summary>
    private void ScheduleMemoryTrim()
    {
        _trimTimer ??= DispatcherQueue.CreateTimer();
        _trimTimer.Stop();
        _trimTimer.Interval = TimeSpan.FromSeconds(4);
        _trimTimer.IsRepeating = false;
        _trimTimer.Tick -= TrimTick;
        _trimTimer.Tick += TrimTick;
        _trimTimer.Start();
    }

    private static void TrimTick(Microsoft.UI.Dispatching.DispatcherQueueTimer s, object e)
        => GC.Collect(2, GCCollectionMode.Forced, blocking: false);

    private void ToggleImmersive()
    {
        if (ImmersiveLayer.IsOpen) CloseImmersive(); else OpenImmersive();
    }

    // ---------------- 封面的连贯转场 ----------------

    private static Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService CoverAnimations =>
        Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView();

    /// <summary>主界面的大封面此刻是否可见（窄窗口下它是隐藏的，没有封面的歌也不做转场）。</summary>
    private bool StageCoverVisible =>
        !_stageNarrow && StageCoverCard.Visibility == Visibility.Visible && !string.IsNullOrEmpty(_currentTrack?.CoverPath);

    private bool PrepareCoverAnimation(string key, UIElement source)
    {
        try { CoverAnimations.PrepareToAnimate(key, source); return true; }
        catch { return false; }
    }

    /// <summary>把之前拍好快照的封面“飞”到目标元素；目标还没排好版或启动失败就取消，按普通方式显示。</summary>
    private void StartCoverAnimation(string key, UIElement target)
    {
        try
        {
            var anim = CoverAnimations.GetAnimation(key);
            if (anim == null) return;
            anim.Configuration = new Microsoft.UI.Xaml.Media.Animation.BasicConnectedAnimationConfiguration();
            ApplyCoverMotionCurves(anim, target);
            if (!anim.TryStart(target)) anim.Cancel();
        }
        catch { }
    }

    private const int CoverFlightMs = 460;

    /// <summary>
    /// 封面飞行的运动曲线：横向、纵向、缩放各用一条不同的缓动，而不是默认的匀速直线。
    /// 横向起步快、很早就接近终点；纵向先慢后快再缓停 —— 两者合起来是一条弧线；
    /// 缩放略微冲过头再收回，落定时有一点弹性。任何一步不被支持就退回默认动画。
    /// </summary>
    private static void ApplyCoverMotionCurves(Microsoft.UI.Xaml.Media.Animation.ConnectedAnimation anim, UIElement target)
    {
        try
        {
            var c = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(target).Compositor;
            var dur = TimeSpan.FromMilliseconds(CoverFlightMs);

            var easeX = c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.16f, 0.9f), new System.Numerics.Vector2(0.2f, 1f));
            var easeY = c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.55f, 0f), new System.Numerics.Vector2(0.15f, 1f));
            var easeScale = c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.3f, 0.2f), new System.Numerics.Vector2(0.2f, 1.18f));

            var x = c.CreateScalarKeyFrameAnimation();
            x.Duration = dur;
            x.InsertExpressionKeyFrame(0f, "StartingValue");
            x.InsertExpressionKeyFrame(1f, "FinalValue", easeX);

            var y = c.CreateScalarKeyFrameAnimation();
            y.Duration = dur;
            y.InsertExpressionKeyFrame(0f, "StartingValue");
            y.InsertExpressionKeyFrame(1f, "FinalValue", easeY);

            // 缩放分量要用标量动画（系统分别套到 Scale.x / Scale.y 上），用二维向量会被拒绝
            var s = c.CreateScalarKeyFrameAnimation();
            s.Duration = dur;
            s.InsertExpressionKeyFrame(0f, "StartingValue");
            s.InsertExpressionKeyFrame(1f, "FinalValue", easeScale);

            anim.SetAnimationComponent(Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationComponent.OffsetX, x);
            anim.SetAnimationComponent(Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationComponent.OffsetY, y);
            anim.SetAnimationComponent(Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationComponent.Scale, s);
        }
        catch (Exception ex) { App.LogError("Cover motion curves", ex.Message); }
    }

    private void OpenImmersive()
    {
        CrashReporter.Note("打开沉浸界面");
        // 进入沉浸模式：主界面的封面放大移动到沉浸界面的封面位置
        bool fly = !ImmersiveLayer.IsOpen && StageCoverVisible && PrepareCoverAnimation("toImmersive", StageCoverCard.CoverTarget);

        ImmersiveLayer.LyricFontSize = Math.Min(StageLyricsControl.LyricFontSize + 6, 40);
        ImmersiveLayer.SubMode = State.LyricSubMode;
        ImmersiveLayer.Open();
        SyncImmersive();
        ApplyDynamicCover();
        if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
        {
            EnterFullscreen();
            _immersiveSetFullscreen = true;
        }

        // 等沉浸界面（以及全屏后的新尺寸）排好版再起飞，否则目标位置还是旧的
        if (fly) DispatcherQueue.SafeEnqueue(() => StartCoverAnimation("toImmersive", ImmersiveLayer.CoverTarget));
    }

    private void CloseImmersive()
    {
        CrashReporter.Note("关闭沉浸界面");
        // 退出：沉浸界面的封面缩回主界面的封面位置
        bool fly = ImmersiveLayer.IsOpen && StageCoverVisible && PrepareCoverAnimation("fromImmersive", ImmersiveLayer.CoverTarget);

        ImmersiveLayer.Close();
        ApplyDynamicCover();
        ShowPendingUpdate();   // 沉浸模式期间发现的新版本，退出后再提示
        if (_immersiveSetFullscreen)
        {
            ExitFullscreen();
            _immersiveSetFullscreen = false;
        }

        if (fly) DispatcherQueue.SafeEnqueue(() => StartCoverAnimation("fromImmersive", StageCoverCard.CoverTarget));
    }

    // 自绘标题栏的窗口全屏时，系统仍会在最上面画 1 像素的窗口边框（浅色主题下是白线）。
    // 全屏期间关掉“内容延伸进标题栏”，退出时再恢复。
    private void EnterFullscreen()
    {
        ExtendsContentIntoTitleBar = false;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    private void ExitFullscreen()
    {
        AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
    }

    /// <summary>沉浸层打开时，同步当前曲目与歌词。</summary>
    private void SyncImmersive()
    {
        if (!ImmersiveLayer.IsOpen) return;
        ImmersiveLayer.SetTrack(_currentTrack);
        ImmersiveLayer.SetPlaying(AudioEngineService.Instance.IsPlaying);
        ImmersiveLayer.UpdateProgress(AudioEngineService.Instance.CurrentTime, AudioEngineService.Instance.TotalDuration);
        ImmersiveLayer.SetLyrics(StageLyricsControl.Lyrics);
        ImmersiveLayer.UpdateTime(AudioEngineService.Instance.CurrentTime + _lyricOffset);
    }

    #endregion

    private void FullscreenBtn_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private async void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (this.Content?.XamlRoot == null) return;

            var dialog = new SettingsDialog
            {
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ThemeService.ToElementTheme(State.Theme)
            };

            dialog.BackdropChanged += ApplyBackdrop;
            dialog.TaskbarPlayerChanged += SetTaskbarPlayerEnabled;
            dialog.ThemeChanged += theme =>
            {
                ThemeService.Apply(this, theme);
                // ContentDialog 在弹出层里，需要单独同步主题
                dialog.RequestedTheme = ThemeService.ToElementTheme(theme);
                ThemeService.Apply(_miniPlayerWindow, theme);
            };
            dialog.LyricFontSizeChanged += size => StageLyricsControl.LyricFontSize = size;
            dialog.LibraryChanged += () =>
            {
                LeftNavPane.ReloadFromLibrary();
                LeftNavPane.ResetQueue();
                MetadataService.Instance.Enqueue(LibraryService.Instance.Tracks);
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Settings dialog error: {ex.Message}");
        }
    }

    #endregion
}
