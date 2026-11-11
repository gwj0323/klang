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
        ImmersiveLayer.CloseRequested += (s, e) => CloseImmersive();
        ImmersiveLayer.SeekRequested += (s, t) => AudioEngineService.Instance.Seek(t);
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
            StageLyricsControl.UpdatePlaybackTime(pos); ImmersiveLayer.UpdateTime(pos);
        };

        // 2. 监听音频进度与状态
        AudioEngineService.Instance.PositionChanged += (current, total) =>
        {
            BottomTransportBar.UpdateProgress(current, total);
            if (ImmersiveLayer.IsOpen) ImmersiveLayer.UpdateProgress(current, total);
            StageLyricsControl.UpdatePlaybackTime(current); ImmersiveLayer.UpdateTime(current);
            _media.UpdateTimeline(current, total);

            // 同步底栏单句微缩歌词
            int activeIdx = LyricsService.Instance.FindActiveIndex(StageLyricsControl.Lyrics, current);
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
                _desktopLyricWindow.UpdatePlaybackTime(current);
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
            StageCoverCard.SetPlaying(isPlaying);
            _media.SetPlaybackState(isPlaying, _currentTrack != null);
        };

        // 曲目自然播放结束，自动切下一首
        AudioEngineService.Instance.TrackEnded += () =>
        {
            DispatcherQueue.TryEnqueue(() =>
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
            PlayTrack(track);
        };

        LeftNavPane.FavoriteToggled += (s, track) => ToggleFavorite(track);
        LeftNavPane.CloudCoverReady += (s, track) => RefreshTrackCover(track);
        MetadataService.Instance.TrackEnriched += track => DispatcherQueue.TryEnqueue(() =>
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
            AudioEngineService.Instance.Seek(timeSec);
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
                "random" => L.T("已开启随机播放"),
                "roam" => L.T("已开启漫游：会根据你的听歌习惯猜下一首"),
                _ => L.T("已开启列表循环")
            });
            SavePlayMode();
        };

        BottomTransportBar.RepeatClicked += (s, e) =>
        {
            _playMode = (_playMode == "loop_single") ? "loop_list" : "loop_single";
            BottomTransportBar.SetShuffleState(_playMode);
            BottomTransportBar.SetRepeatMode(_playMode);
            BottomTransportBar.UpdateLyricSnippet(_playMode == "loop_single" ? L.T("已开启单曲循环") : L.T("已开启列表循环"));
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
            DispatcherQueue.TryEnqueue(() => BottomTransportBar.UpdateLyricSnippet(L.T("睡眠定时已到，已暂停播放")));
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
            StageLyricsControl.UpdatePlaybackTime(pos); ImmersiveLayer.UpdateTime(pos);
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

        if (_currentTrack != null) _currentTrack.IsCurrent = false;
        _currentTrack = track;
        track.IsCurrent = true;

        StageCoverCard.UpdateTrack(track);
        UpdateNarrowHeader(track);
        // CUE 分轨暂不加载整轨歌词（整轨 .lrc 与分轨时间轴不一致）
        StageLyricsControl.LoadLyrics(track.IsCueTrack || track.IsOnline ? null : track.FilePath);
        SyncImmersive();
        BottomTransportBar.UpdateTrackState(track.Title, track.Artist, track.CoverPath);
        BottomTransportBar.SetFavorite(track.IsFavorite);
        BottomTransportBar.UpdateProgress(0, track.Duration);
        LeftNavPane.SetCurrentTrack(track);
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
            _ = PlayOnlineAsync(track, autoPlay);
            return;
        }

        if (autoPlay && !string.IsNullOrEmpty(track.FilePath))
        {
            _ = PlayLocalAsync(track.FilePath, track.StartTime, track.EndTime, _playSeq);
        }
    }

    private int _playSeq;

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
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private System.Threading.CancellationTokenSource? _onlineCts;
    private string _onlineStatus = "";

    private static bool IsNeteaseLoggedIn() => Services.Netease.NeteaseService.Instance.IsLoggedIn;

    /// <summary>云端曲目：取直链并下载缓存后播放，同时异步加载歌词。后点的曲目会取消先前的请求。</summary>
    private async System.Threading.Tasks.Task PlayOnlineAsync(TrackItem track, bool autoPlay)
    {
        _onlineCts?.Cancel();
        _onlineStatus = "";
        var cts = _onlineCts = new System.Threading.CancellationTokenSource();
        var ct = cts.Token;
        var netease = Services.Netease.NeteaseService.Instance;

        int seq = _playSeq;

        // 歌词与音源并行获取（淡出旧曲的同时就开始请求）
        var lyricTask = netease.GetLyricsAsync(track.NeteaseId, ct);
        BottomTransportBar.UpdateLyricSnippet(autoPlay ? L.T("正在获取云端音源…") : L.T("云端曲目"));

        await AudioEngineService.Instance.FadeOutAndStopAsync();
        if (ct.IsCancellationRequested || seq != _playSeq) return;

        try
        {
            bool ok = await netease.ResolveAsync(track, ct);
            if (ct.IsCancellationRequested || _currentTrack != track) return;

            if (!ok)
            {
                _onlineStatus = IsNeteaseLoggedIn()
                    ? L.T("无法获取音源（可能是会员专享或无版权）")
                    : L.T("无法获取音源（可能需要登录网易云账号）");
                BottomTransportBar.UpdateLyricSnippet(_onlineStatus);
            }
            else
            {
                RefreshTrackCover(track);
                if (autoPlay && seq == _playSeq) AudioEngineService.Instance.Play(track.FilePath);
                BottomTransportBar.UpdateLyricSnippet(track.FormatBadge);
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

    private void PlayNext()
    {
        var tracks = LeftNavPane.Queue;
        if (tracks.Count == 0) return;

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
                        _nextByRoam = true;
                        PlayTrack(pick.Value.Track);
                        BottomTransportBar.UpdateLyricSnippet(L.T("漫游 · ") + pick.Value.Reason);
                        return;
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

        PlayTrack(tracks[nextIdx]);
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
        StageLyricsControl.UpdatePlaybackTime(pos); ImmersiveLayer.UpdateTime(pos);
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
            "solid" => null,
            _ => new MicaBackdrop()
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

    private const double SidebarOpenWidth = 286;
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
        SidebarRail.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
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
            if (btn.Content is FontIcon icon) icon.Foreground = sel ? accent : normal;
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
        DispatcherQueue.TryEnqueue(() => LeftNavPane.FocusSearch());
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
                StageLyricsControl.UpdatePlaybackTime(pos); ImmersiveLayer.UpdateTime(pos);
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

    private async void PosterBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTrack == null)
        {
            BottomTransportBar.UpdateLyricSnippet(L.T("先播放一首歌再生成海报"));
            return;
        }
        var lyrics = StageLyricsControl.Lyrics;
        int active = LyricsService.Instance.FindActiveIndex(lyrics, AudioEngineService.Instance.CurrentTime);
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

    private void ToggleImmersive()
    {
        if (ImmersiveLayer.IsOpen) CloseImmersive(); else OpenImmersive();
    }

    private void OpenImmersive()
    {
        ImmersiveLayer.LyricFontSize = Math.Min(StageLyricsControl.LyricFontSize + 6, 40);
        ImmersiveLayer.SubMode = State.LyricSubMode;
        ImmersiveLayer.Open();
        SyncImmersive();
        if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
        {
            EnterFullscreen();
            _immersiveSetFullscreen = true;
        }
    }

    private void CloseImmersive()
    {
        ImmersiveLayer.Close();
        if (_immersiveSetFullscreen)
        {
            ExitFullscreen();
            _immersiveSetFullscreen = false;
        }
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
        ImmersiveLayer.UpdateTime(AudioEngineService.Instance.CurrentTime);
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
