using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Klang_WinUI.Models;
using Klang_WinUI.Services;
using Klang_WinUI.Services.Ai;

namespace Klang_WinUI;

/// <summary>主窗口里的 AI 功能：K 歌模式、AI 识别 / 对齐歌词、导出伴奏、AI 找歌入口。</summary>
public sealed partial class MainWindow
{
    private bool _karaoke;                       // K 歌模式（不保存，重启后关闭）
    private bool _playingInstrumental;           // 引擎当前放的是分离出的伴奏
    private CancellationTokenSource? _aiJobCts;  // 正在进行的 AI 任务（同一时间一个）
    private CancellationTokenSource? _karaokeCts;

    private bool AiJobRunning => _aiJobCts != null;

    // ================= 菜单 =================

    private void UpdateAiLyricMenu()
    {
        var ai = AiService.Instance;
        var vis = ai.Enabled ? Visibility.Visible : Visibility.Collapsed;
        AiLyricSep.Visibility = KaraokeItem.Visibility = AiTranscribeItem.Visibility = AiAlignSep.Visibility = AiAlignItem.Visibility = AiExportItem.Visibility = vis;
        AiCancelItem.Visibility = ai.Enabled && (AiJobRunning || _karaokeCts != null) ? Visibility.Visible : Visibility.Collapsed;
        if (!ai.Enabled) return;

        bool can = AiAudioService.CanProcess(_currentTrack);
        bool sep = ai.Ready(AiCatalog.Separator), wh = ai.Ready(AiCatalog.Whisper);
        KaraokeItem.IsChecked = _karaoke;
        // 云端歌曲还在下载时也能开 K 歌：下完自动分离
        KaraokeItem.IsEnabled = sep && (can || _karaoke || AiAudioService.PendingDownload(_currentTrack) != null);
        AiExportItem.IsEnabled = sep && can && !AiJobRunning;
        AiTranscribeItem.IsEnabled = wh && can && !AiJobRunning;
        AiAlignItem.IsEnabled = wh && can && !AiJobRunning && HasRealLyrics();
        string needSep = L.T("需要先在设置 → AI 功能里下载“人声分离”");
        string needWh = L.T("需要先在设置 → AI 功能里下载“歌词识别”");
        string notLocal = L.T("只支持本地歌曲");
        ToolTipService.SetToolTip(KaraokeItem, !sep ? needSep : !KaraokeItem.IsEnabled ? L.T("这首歌不支持 K 歌模式") : L.T("播放去掉人声的伴奏"));
        ToolTipService.SetToolTip(AiExportItem, !sep ? needSep : !can ? notLocal : L.T("伴奏和人声分别存成 WAV"));
        ToolTipService.SetToolTip(AiTranscribeItem, !wh ? needWh : !can ? notLocal : L.T("从声音识别歌词，可在“选择歌词”里换回"));
        ToolTipService.SetToolTip(AiAlignItem, !wh ? needWh : !HasRealLyrics() ? L.T("当前没有带时间轴的歌词") : L.T("自动调整歌词快慢"));
    }

    private bool HasRealLyrics()
    {
        var l = StageLyricsControl.Lyrics;
        return l.Count >= 3 && !(l.Count == 1 && l[0].EndTime >= 9000);
    }

    private void DiscoverMenu_Opening(object? sender, object e)
    {
        var vis = AiService.Instance.Enabled ? Visibility.Visible : Visibility.Collapsed;
        DiscoverAiItem.Visibility = HighlightModeItem.Visibility = vis;
        HighlightModeItem.IsChecked = _hlMode;
        HighlightModeItem.IsEnabled = HighlightReady || _hlMode;
        ToolTipService.SetToolTip(HighlightModeItem, HighlightReady
            ? L.T("每首只放高潮那一段，自动接到下一首的高潮")
            : L.T("需要先在设置 → AI 功能里下载“听感分析”。"));
    }

    private async void DiscoverAi_Click(object sender, RoutedEventArgs e)
    {
        await Dialogs.AiSearchDialog.ShowAsync(Content.XamlRoot, (track, list, seekTo) =>
        {
            LeftNavPane.PlayQueue(list.Count > 0 ? list : new() { track });
            if (seekTo > 0) _ = SeekWhenReadyAsync(track, seekTo);
        });
    }

    /// <summary>新歌开始播放后跳到指定位置（AI 歌词搜索点了某一句时用）。</summary>
    private async Task SeekWhenReadyAsync(TrackItem track, double seconds)
    {
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(100);
            if (_currentTrack != track) return;
            if (AudioEngineService.Instance.HasSource && AudioEngineService.Instance.IsPlaying)
            {
                AudioEngineService.Instance.Seek(Math.Max(0, seconds - 1.5));
                return;
            }
        }
    }

    private void AiCancel_Click(object sender, RoutedEventArgs e)
    {
        _aiJobCts?.Cancel();
        // 取消 K 歌的分离：K 歌模式也一起关掉（否则下一首又会自动开始分离）
        if (_karaokeCts != null || _karaoke) SetKaraoke(false);
    }

    /// <summary>没有任务在跑了：收起还停在进度状态的标签（显示结果文字的会自己定时收起）。</summary>
    private void HideAiPillIfIdle()
    {
        if (AiJobRunning || _karaokeCts != null) return;
        if (AiJobRing.Visibility == Visibility.Visible) AiJobPill.Visibility = Visibility.Collapsed;
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _aiPillTimer;

    /// <summary>标题栏的 AI 进度标签：进度环 + 文字（点它可以取消）。</summary>
    private void ShowAiProgress(string label, double p)
    {
        _aiPillTimer?.Stop();
        AiJobPill.Visibility = Visibility.Visible;
        AiJobRing.Visibility = Visibility.Visible;
        AiJobRing.IsIndeterminate = p <= 0.001;
        AiJobRing.Value = p * 100;
        AiJobText.Text = p > 0.001 ? $"{label} {p * 100:0}%" : label;
        AiJobCancelItem.IsEnabled = true;
        ToolTipService.SetToolTip(AiJobPill, L.T("点击可以取消"));
    }

    /// <summary>结果 / 提示：在进度标签里停留几秒（底栏的歌词摘要会被歌词马上刷掉），同时也写到底栏。</summary>
    private void ShowAiStatus(string text)
    {
        BottomTransportBar.UpdateLyricSnippet(text);
        AiJobRing.Visibility = Visibility.Collapsed;
        AiJobText.Text = text;
        AiJobPill.Visibility = Visibility.Visible;
        AiJobCancelItem.IsEnabled = AiJobRunning || _karaokeCts != null;
        ToolTipService.SetToolTip(AiJobPill, text);
        if (_aiPillTimer == null)
        {
            _aiPillTimer = DispatcherQueue.CreateTimer();
            _aiPillTimer.Interval = TimeSpan.FromSeconds(6);
            _aiPillTimer.IsRepeating = false;
            _aiPillTimer.Tick += (s, e) => { if (!AiJobRunning && _karaokeCts == null) AiJobPill.Visibility = Visibility.Collapsed; };
        }
        _aiPillTimer.Stop();
        _aiPillTimer.Start();
    }

    private Action<double> ThrottledStatus(string label)
    {
        var last = DateTime.MinValue;
        return p => DispatcherQueue.TryEnqueue(() =>
        {
            if ((DateTime.UtcNow - last).TotalMilliseconds < 250 && p < 1) return;
            last = DateTime.UtcNow;
            ShowAiProgress(label, p);
        });
    }

    // ================= 歌词交给 AI 索引 =================

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lyricOfferTimer;

    /// <summary>当前歌曲的歌词定下来 3 秒后交给歌词索引（在线补全的歌词只有这时才拿得到）。</summary>
    private void OfferLyricsToAi()
    {
        if (!AiService.Instance.Enabled) return;
        if (_lyricOfferTimer == null)
        {
            _lyricOfferTimer = DispatcherQueue.CreateTimer();
            _lyricOfferTimer.Interval = TimeSpan.FromSeconds(3);
            _lyricOfferTimer.IsRepeating = false;
            _lyricOfferTimer.Tick += (s, e) =>
            {
                if (_currentTrack != null) AiIndexService.Instance.OfferLyrics(_currentTrack, StageLyricsControl.Lyrics.ToList());
            };
        }
        _lyricOfferTimer.Stop();
        _lyricOfferTimer.Start();
    }

    // ================= K 歌模式 =================

    private void KaraokeItem_Click(object sender, RoutedEventArgs e) => SetKaraoke(KaraokeItem.IsChecked);

    private void KaraokeOff_Click(object sender, RoutedEventArgs e)
    {
        KaraokeBadge.Flyout?.Hide();
        SetKaraoke(false);
    }

    private void KaraokeVocalSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int v = (int)Math.Round(e.NewValue);
        KaraokeMixStream.VocalGain = v / 100f;
        KaraokeVocalText.Text = v == 0 ? L.T("无") : $"{v}%";
        var st = StateService.Instance.State;
        if (st.AiKaraokeVocal != v) { st.AiKaraokeVocal = v; StateService.Instance.RequestSave(); }
    }

    /// <summary>AI 总开关关掉、或人声分离组件被删掉：停掉 AI 任务，退出 K 歌模式换回原曲。</summary>
    private void OnAiChanged()
    {
        var ai = AiService.Instance;
        if (!ai.Enabled) _aiJobCts?.Cancel();
        if ((_karaoke || _karaokeCts != null || _playingInstrumental) && !ai.Ready(AiCatalog.Separator)) SetKaraoke(false);
        if (_hlMode && !HighlightReady) SetHighlightMode(false);
    }

    private void SetKaraoke(bool on)
    {
        _karaoke = on;
        KaraokeItem.IsChecked = on;
        KaraokeBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            int v = Math.Clamp(StateService.Instance.State.AiKaraokeVocal, 0, 100);
            KaraokeMixStream.VocalGain = v / 100f;
            KaraokeVocalSlider.Value = v;
            KaraokeVocalText.Text = v == 0 ? L.T("无") : $"{v}%";
            if (_currentTrack != null) _ = ApplyKaraokeAsync(_currentTrack);
        }
        else
        {
            _karaokeCts?.Cancel();
            if (_playingInstrumental && _currentTrack != null) SwapSource(_currentTrack, instrumental: null);
            _playingInstrumental = false;
            ShowAiStatus(L.T("已关闭 K 歌模式"));
        }
    }

    /// <summary>播放新歌时（PlayLocalAsync / 云端缓存播放）：K 歌模式开着就换成伴奏。有缓存时直接返回伴奏路径。</summary>
    private string? KaraokeSourceFor(TrackItem track)
    {
        _playingInstrumental = false;
        if (!_karaoke || !AiService.Instance.Ready(AiCatalog.Separator) || !AiAudioService.CanProcess(track)) return null;
        return AiAudioService.IsSeparated(track) ? AiAudioService.InstrumentalPath(track) : null;
    }

    private async Task ApplyKaraokeAsync(TrackItem track)
    {
        if (!_karaoke || _currentTrack != track) return;
        if (!AiService.Instance.Ready(AiCatalog.Separator)) { ShowAiStatus(L.T("K 歌模式需要“人声分离”")); return; }
        var pending = AiAudioService.PendingDownload(track);
        if (pending == null && !AiAudioService.CanProcess(track)) { ShowAiStatus(L.T("这首歌不支持 K 歌模式")); return; }
        if (_playingInstrumental) return;

        _karaokeCts?.Cancel();
        var cts = _karaokeCts = new CancellationTokenSource();
        try
        {
            // 云端歌曲边下边播：先等整首下完（下载失败或被取消就算了）
            if (pending != null)
            {
                ShowAiProgress(L.T("K 歌模式：等歌曲下载完"), 0);
                bool ok = await pending.Completion.WaitAsync(cts.Token);
                if (cts.IsCancellationRequested || !_karaoke || _currentTrack != track) return;
                if (!ok || !AiAudioService.CanProcess(track)) { ShowAiStatus(L.T("歌曲没有下载完，无法分离人声")); return; }
            }

            if (AiAudioService.IsSeparated(track))
            {
                SwapSource(track, AiAudioService.InstrumentalPath(track));
                ShowAiStatus(L.T("K 歌模式：正在播放伴奏"));
                return;
            }

            // 边分离边播放：从当前位置开始分离，写好一段就由 KaraokeTick 换成伴奏，后面的在后台继续
            var engine = AudioEngineService.Instance;
            double from = engine.HasSource ? engine.CurrentTime : 0;
            _karaokeLabel = L.T("K 歌模式：正在分离人声");
            ShowAiProgress(_karaokeLabel, 0);
            var last = DateTime.MinValue;
            var inst = await AiAudioService.SeparateAsync(track, p => DispatcherQueue.TryEnqueue(() =>
            {
                if ((DateTime.UtcNow - last).TotalMilliseconds < 250 || _karaokeCts != cts) return;
                last = DateTime.UtcNow;
                ShowAiProgress(_karaokeLabel, p);
            }), cts.Token, from);
            if (cts.IsCancellationRequested || !_karaoke || _currentTrack != track) return;
            if (!_playingInstrumental) SwapSource(track, inst);
            ShowAiStatus(L.T("K 歌模式：正在播放伴奏"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_currentTrack == track)
            {
                // 只分离了一部分就失败了：后面没写的地方是静音，换回原曲
                if (_playingInstrumental) SwapSource(track, null);
                _playingInstrumental = false;
                ShowAiStatus(L.T("人声分离失败：") + ex.Message);
            }
            App.LogError("Karaoke", ex.Message);
        }
        finally
        {
            if (_karaokeCts == cts) _karaokeCts = null;
            HideAiPillIfIdle();
        }
    }

    private string _karaokeLabel = "";
    private double _karaokeLastPos = -1;
    private DateTime _karaokeSwapAt;

    /// <summary>
    /// 边分离边播放（每次播放进度更新时调用）：当前位置往后 2 秒都分离好了就换成伴奏；
    /// 播到（或拖到）还没分离好的地方先换回原曲，并让子进程优先处理这里。
    /// </summary>
    private void KaraokeTick(double current, double total)
    {
        if (!_karaoke || _karaokeCts == null) { _karaokeLastPos = -1; return; }
        var t = _currentTrack;
        if (t == null || AiAudioService.JobFor(t) is not { } job) return;
        var engine = AudioEngineService.Instance;
        if (!engine.HasSource || total <= 0) return;

        // 拖了进度：新位置还没分离的话让子进程先做这里
        bool jumped = _karaokeLastPos >= 0 && Math.Abs(current - _karaokeLastPos) > 3;
        _karaokeLastPos = current;
        if (jumped && !job.Covers(current, Math.Min(current + 2, total - 0.05))) AiAudioService.Prioritize(t, current);

        if ((DateTime.UtcNow - _karaokeSwapAt).TotalSeconds < 1) return;   // 刚换过，别来回切
        if (!_playingInstrumental)
        {
            if (job.Covers(current, Math.Min(current + 2, total - 0.05)))
            {
                _karaokeSwapAt = DateTime.UtcNow;
                SwapSource(t, job.Instrumental);
                _karaokeLabel = L.T("K 歌模式：正在播放伴奏，后台继续分离");
            }
        }
        else if (!job.Covers(current, Math.Min(current + 0.8, total - 0.05)))
        {
            _karaokeSwapAt = DateTime.UtcNow;
            SwapSource(t, null);
            _karaokeLabel = L.T("K 歌模式：这一段还没分离好，先放原曲");
            AiAudioService.Prioritize(t, current);
        }
    }

    /// <summary>在当前位置无缝换成伴奏（instrumental 为 null 时换回原曲），保持播放 / 暂停状态。</summary>
    private void SwapSource(TrackItem track, string? instrumental)
    {
        var engine = AudioEngineService.Instance;
        if (!engine.HasSource) return;
        double pos = engine.CurrentTime;
        bool playing = engine.IsPlaying;
        if (instrumental != null) engine.Play(instrumental);
        else engine.Play(track.FilePath);
        if (!engine.HasSource) return;
        _playingInstrumental = instrumental != null;
        if (pos > 0.05) engine.Seek(pos);
        if (!playing) engine.Pause();
    }

    // ================= AI 识别 / 对齐歌词 =================

    private async void AiTranscribe_Click(object sender, RoutedEventArgs e)
    {
        var track = _currentTrack;
        if (track == null || AiJobRunning) return;
        var cts = _aiJobCts = new CancellationTokenSource();
        ShowAiProgress(L.T("正在准备…"), 0);
        try
        {
            var last = DateTime.MinValue;
            var (lang, segs) = await AiAudioService.TranscribeAsync(track, null, (p, label) => DispatcherQueue.TryEnqueue(() =>
            {
                if ((DateTime.UtcNow - last).TotalMilliseconds < 250) return;
                last = DateTime.UtcNow;
                if (_currentTrack == track) ShowAiProgress(label, p);
            }), cts.Token);
            if (segs.Count == 0)
            {
                if (_currentTrack == track) ShowAiStatus(L.T("没有识别出歌词（可能是纯音乐）"));
                return;
            }
            var lrc = AiAudioService.ToLrc(track, segs);
            LyricsService.SaveChosen(track.Id, new LyricsService.LyricSet(lrc));
            AiIndexService.Instance.Invalidate(track);
            CrashReporter.Note($"AI 识别歌词：{track.Title}（{lang}，{segs.Count} 句）");
            if (_currentTrack != track) return;
            StageLyricsControl.LoadLyricLines(LyricsService.Instance.ParseLrc(lrc));
            SyncImmersive();
            _desktopLyricWindow?.UpdateLyrics(StageLyricsControl.Lyrics);
            ShowAiStatus(L.F("已识别 {0} 句歌词（AI 识别可能有错字，可在“选择歌词”里换回）", segs.Count));
        }
        catch (OperationCanceledException) { if (_currentTrack == track) ShowAiStatus(L.T("已取消")); }
        catch (Exception ex)
        {
            if (_currentTrack == track) ShowAiStatus(L.T("识别歌词失败：") + ex.Message);
            App.LogError("AI transcribe", ex.Message);
        }
        finally { if (_aiJobCts == cts) _aiJobCts = null; HideAiPillIfIdle(); }
    }

    private async void AiAlign_Click(object sender, RoutedEventArgs e)
    {
        var track = _currentTrack;
        if (track == null || AiJobRunning || !HasRealLyrics()) return;
        var lyrics = StageLyricsControl.Lyrics.ToList();
        var cts = _aiJobCts = new CancellationTokenSource();
        ShowAiProgress(L.T("正在准备…"), 0);
        try
        {
            var last = DateTime.MinValue;
            var (_, segs) = await AiAudioService.TranscribeAsync(track, null, (p, label) => DispatcherQueue.TryEnqueue(() =>
            {
                if ((DateTime.UtcNow - last).TotalMilliseconds < 250) return;
                last = DateTime.UtcNow;
                if (_currentTrack == track) ShowAiProgress($"{L.T("正在对齐歌词")}：{label}", p);
            }), cts.Token);
            if (_currentTrack != track) return;
            var r = AiAudioService.AlignOffset(lyrics, segs.Select(s => new AiAudioService.Segment(s.Start, s.End, s.Text)).ToList());
            if (r == null)
            {
                ShowAiStatus(L.T("没能对齐：识别出的内容和当前歌词对不上（歌词可能不是这首歌的）"));
                return;
            }
            // 识别出的时间本身有零点几秒的误差：小于 0.3 秒就当作歌词本来就是准的
            if (Math.Abs(r.Value.Offset) < 0.3)
            {
                SetLyricOffset(0);
                ShowAiStatus(L.F("歌词时间本来就是准的（根据 {0} 句匹配），已清除偏移", r.Value.Matched));
                return;
            }
            SetLyricOffset(r.Value.Offset);
            ShowAiStatus(L.F("已自动对齐：歌词偏移 {0} 秒（根据 {1} 句匹配）", r.Value.Offset.ToString("+0.00;-0.00;0.00"), r.Value.Matched));
        }
        catch (OperationCanceledException) { if (_currentTrack == track) ShowAiStatus(L.T("已取消")); }
        catch (Exception ex)
        {
            if (_currentTrack == track) ShowAiStatus(L.T("对齐失败：") + ex.Message);
            App.LogError("AI align", ex.Message);
        }
        finally { if (_aiJobCts == cts) _aiJobCts = null; HideAiPillIfIdle(); }
    }

    // ================= 导出伴奏 / 人声 =================

    private async void AiExport_Click(object sender, RoutedEventArgs e)
    {
        var track = _currentTrack;
        if (track == null || AiJobRunning) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var folder = FolderPickerHelper.PickFolder(hwnd, L.T("选择伴奏和人声的保存位置"));
        if (string.IsNullOrEmpty(folder)) return;
        var cts = _aiJobCts = new CancellationTokenSource();
        ShowAiProgress(L.T("正在准备…"), 0);
        try
        {
            var inst = await AiAudioService.SeparateAsync(track, ThrottledStatus(L.T("正在分离人声")), cts.Token);
            string baseName = string.Concat($"{track.Artist} - {track.Title}".Split(Path.GetInvalidFileNameChars())).Trim();
            if (baseName.Length == 0) baseName = "track";
            var instDest = UniquePath(Path.Combine(folder, baseName + L.T("（伴奏）") + ".wav"));
            var vocDest = UniquePath(Path.Combine(folder, baseName + L.T("（人声）") + ".wav"));
            await Task.Run(() =>
            {
                File.Copy(inst, instDest);
                File.Copy(AiAudioService.VocalsPath(track), vocDest);
            });
            ShowAiStatus(L.F("已导出到 {0}", folder));
        }
        catch (OperationCanceledException) { ShowAiStatus(L.T("已取消")); }
        catch (Exception ex)
        {
            ShowAiStatus(L.T("导出失败：") + ex.Message);
            App.LogError("AI export", ex.Message);
        }
        finally { if (_aiJobCts == cts) _aiJobCts = null; HideAiPillIfIdle(); }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }
}
