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
            BottomTransportBar.IsPlaying = isPlaying;
            ImmersiveLayer.SetPlaying(isPlaying);
            GalleryLayer.SetIsPlaying(isPlaying);
            StageCoverCard.SetPlaying(isPlaying);
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
        MetadataService.Instance.TrackEnriched += track => DispatcherQueue.SafeEnqueue(() =>
        {
            RefreshTrackCover(track);
            // 歌手 / 专辑变了，且列表正按它们分组或排序时才重建，避免无谓闪动
            if (LeftNavPane.GroupMode is "artist" or "album" || LeftNavPane.SortMode is "artist" or "album")
                LeftNavPane.RefreshView();
        });

        LeftNavPane.TrackRemoved += (s, track) =>
        {
            if (_currentTrack == track) { track.IsCurrent = false; _currentTrack = null; }
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

        this.Closed += (s, e) =>
        {
            SaveWindowAndAudioState();
            StateService.Instance.Flush();
            _media.Dispose();
            _desktopLyricWindow?.Close();
            _miniPlayerWindow?.Close();
        };
    }

    #region 启动恢复与状态保存



    private async System.Threading.Tasks.Task RestoreLibraryAsync()
    {
        try
        {
            _ = Services.Netease.NeteaseService.Instance.VerifyLoginAsync();
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
        // 文件已经丢了：不开始播放，提示一下（启动时恢复上次曲目只显示，不拦）
        if (autoPlay && track.IsMissing && !track.IsOnline)
        {
            BottomTransportBar.UpdateLyricSnippet(L.T("文件丢失，无法播放：") + System.IO.Path.GetFileName(track.FilePath));
            return;
        }
        if (autoPlay)
        {
            LibraryService.Instance.RecordPlay(track);

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
        if (_currentTrack != null) _currentTrack.IsCurrent = false;
        _currentTrack = track;
        track.IsCurrent = true;

        StageCoverCard.UpdateTrack(track);
        UpdateNarrowHeader(track);
        // CUE 分轨暂不加载整轨歌词（整轨 .lrc 与分轨时间轴不一致）
        StageLyricsControl.CalibrationMode = false;
        LoadLyricOffset(track);
        StageLyricsControl.LoadLyrics(track.IsCueTrack || track.IsOnline ? null : track.FilePath);
        SyncImmersive();
        BottomTransportBar.UpdateTrackState(track.Title, track.Artist, track.CoverPath);
        BottomTransportBar.SetFavorite(track.IsFavorite);
        BottomTransportBar.UpdateProgress(0, track.Duration);
        _ = LoadWaveformAsync(track);
        LeftNavPane.SetCurrentTrack(track);
        GalleryLayer.SetCurrent(track);
        _media.UpdateTrack(track);
        _media.SetPlaybackState(autoPlay, true);

        // 同步桌面歌词与迷你播放器
        _desktopLyricWindow?.UpdateLyrics(StageLyricsControl.Lyrics);
        _desktopLyricWindow?.UpdateTrack(track);
        _miniPlayerWindow?.UpdateTrack(track);

        if (!track.IsOnline && !track.IsCueTrack) _ = FillLocalMetaAsync(track);

        State.LastTrackId = track.Id;
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

    private static bool IsNeteaseLoggedIn() => Services.Netease.NeteaseService.Instance.IsLoggedIn;

    /// <summary>云端曲目：取直链并下载缓存后播放，同时异步加载歌词。后点的曲目会取消先前的请求。</summary>
    private async System.Threading.Tasks.Task PlayOnlineAsync(TrackItem track, bool autoPlay, bool adopted = false)
    {
        _onlineCts?.Cancel();
        _onlineStatus = "";
        var cts = _onlineCts = new System.Threading.CancellationTokenSource();
        var ct = cts.Token;
        var netease = Services.Netease.NeteaseService.Instance;

        int seq = _playSeq;

        // 歌词与音源并行获取（淡出旧曲的同时就开始请求）
        var lyricTask = netease.GetLyricsAsync(track.NeteaseId, ct);
        if (!adopted)
        {
            BottomTransportBar.UpdateLyricSnippet(autoPlay ? L.T("正在获取云端音源…") : L.T("云端曲目"));

            await AudioEngineService.Instance.FadeOutAndStopAsync();
            if (ct.IsCancellationRequested || seq != _playSeq) return;
        }

        try
        {
            bool ok = await netease.ResolveAsync(track, ct);
            if (ct.IsCancellationRequested || _currentTrack != track) return;

            if (!ok)
            {
                _onlineStatus = IsNeteaseLoggedIn()
                    ? L.T("无法获取音源（可能为会员专享或无版权）")
                    : L.T("无法获取音源（可能需要先登录网易云）");
                BottomTransportBar.UpdateLyricSnippet(_onlineStatus);
            }
            else
            {
                RefreshTrackCover(track);
                if (!adopted && autoPlay && seq == _playSeq) AudioEngineService.Instance.Play(track.FilePath);
                _ = LoadWaveformAsync(track);   // 云端音源缓存到本地后才有波形
                if (!adopted) BottomTransportBar.UpdateLyricSnippet(track.FormatBadge);
            }

            // 歌词与音源是否可用无关，都照常显示
            var bundle = await lyricTask;
            if (ct.IsCancellationRequested || _currentTrack != track) return;
            if (!string.IsNullOrWhiteSpace(bundle.Lrc))
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
    }

    private void TogglePlayPause()
    {
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
            Services.Netease.NeteaseService.Instance.Prefetch(next);
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
                var netease = Services.Netease.NeteaseService.Instance;
                if (!netease.IsCached(next))
                {
                    netease.Prefetch(next);
                    if (remaining > 12) _plannedFromId = null;
                    return;
                }
                if (string.IsNullOrEmpty(next.FilePath) || !System.IO.File.Exists(next.FilePath))
                    await netease.ResolveAsync(next, System.Threading.CancellationToken.None);   // 已缓存：只是把本地路径写到曲目上
            }
            if (string.IsNullOrEmpty(next.FilePath) || !System.IO.File.Exists(next.FilePath)) return;

            // 还没分析过节拍的（云端曲目、刚导入的歌）：现在补一下（解码一首歌一般不到 1 秒）
            foreach (var t in new[] { a, next })
            {
                if (t.RhythmDone) continue;
                var res = await System.Threading.Tasks.Task.Run(() => { try { return RhythmAnalysisService.Analyze(t.FilePath, t.Duration); } catch { return null; } });
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
            e.Handled = true;
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

    private async void LyricOnline_Click(object sender, RoutedEventArgs e)
    {
        var track = _currentTrack;
        if (track == null || track.IsOnline || track.IsCueTrack) return;
        BottomTransportBar.UpdateLyricSnippet(L.T("正在从 LRCLIB 查找歌词…"));
        string? lrc = await LrclibService.FetchSyncedAsync(track, System.Threading.CancellationToken.None);
        if (_currentTrack != track) return;
        if (lrc == null)
        {
            BottomTransportBar.UpdateLyricSnippet(L.T("LRCLIB 中未找到该歌曲的歌词"));
            return;
        }
        ApplyOnlineLyrics(lrc);
        BottomTransportBar.UpdateLyricSnippet(L.T("已从 LRCLIB 获取歌词"));
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

    private void CloseGallery() => GalleryLayer.Visibility = Visibility.Collapsed;

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
        // 进入沉浸模式：主界面的封面放大移动到沉浸界面的封面位置
        bool fly = !ImmersiveLayer.IsOpen && StageCoverVisible && PrepareCoverAnimation("toImmersive", StageCoverCard.CoverTarget);

        ImmersiveLayer.LyricFontSize = Math.Min(StageLyricsControl.LyricFontSize + 6, 40);
        ImmersiveLayer.SubMode = State.LyricSubMode;
        ImmersiveLayer.Open();
        SyncImmersive();
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
        // 退出：沉浸界面的封面缩回主界面的封面位置
        bool fly = ImmersiveLayer.IsOpen && StageCoverVisible && PrepareCoverAnimation("fromImmersive", ImmersiveLayer.CoverTarget);

        ImmersiveLayer.Close();
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
