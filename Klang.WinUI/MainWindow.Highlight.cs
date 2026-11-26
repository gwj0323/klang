using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Klang_WinUI.Models;
using Klang_WinUI.Services;
using Klang_WinUI.Services.Ai;

namespace Klang_WinUI;

/// <summary>
/// 高潮：“高潮连播”模式（每首只放高潮那 30 秒左右，交叉淡入淡出接到下一首的高潮）和“从高潮开始播放”。
/// 高潮位置由后台分析算好（AiIndexService），没算过的播放时现算。
/// </summary>
public sealed partial class MainWindow
{
    private bool _hlMode;                                   // 高潮连播（不保存，重启后关闭）
    private string? _hlTrackId;                             // 已经处理过的当前曲目
    private double _hlEnd = -1;                             // 当前这首高潮的结束位置（≤0 = 不管）
    private bool _hlMixPlanned;
    private TrackItem? _hlOneShot;                          // “从高潮开始播放”点的那首
    private (TrackItem Track, double End)? _hlAdopt;        // 已经安排好淡入的下一首（淡入时已经在高潮处）

    private bool HighlightReady => AiService.Instance.Ready(AiCatalog.Clap);

    private void HighlightModeItem_Click(object sender, RoutedEventArgs e) => SetHighlightMode(HighlightModeItem.IsChecked);

    private void HighlightBadge_Click(object sender, RoutedEventArgs e) => SetHighlightMode(false);

    private void SetHighlightMode(bool on)
    {
        if (on && !HighlightReady) { ShowAiStatus(L.T("需要先在设置 → AI 功能里下载“听感分析”。")); on = false; }
        _hlMode = on;
        HighlightModeItem.IsChecked = on;
        HighlightBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            _hlTrackId = null;   // 正在放的这首也跳到高潮
            ShowAiStatus(L.T("高潮连播：每首只放高潮"));
        }
        else
        {
            _hlEnd = -1;
            if (_hlAdopt != null)
            {
                AudioEngineService.Instance.CancelMix();
                _hlAdopt = null;
                _mixNext = null;
            }
            ShowAiStatus(L.T("已关闭高潮连播"));
        }
    }

    /// <summary>右键“从高潮开始播放”。</summary>
    private void PlayFromHighlight(TrackItem track)
    {
        _hlOneShot = track;
        if (_currentTrack == track) _hlTrackId = null;
        else PlayTrack(track);
    }

    /// <summary>每次播放进度更新时调用。</summary>
    private void HighlightTick(double current, double total)
    {
        var t = _currentTrack;
        if (t == null) return;
        var engine = AudioEngineService.Instance;

        if (_hlTrackId != t.Id)
        {
            _hlTrackId = t.Id;
            _hlEnd = -1;
            _hlMixPlanned = false;
            bool oneShot = _hlOneShot == t;
            if (oneShot) _hlOneShot = null;
            if (_hlAdopt is { } ad && ad.Track == t)
            {
                // 淡入的时候已经从高潮开始了
                _hlAdopt = null;
                if (_hlMode) _hlEnd = ad.End;
                return;
            }
            _hlAdopt = null;
            if (_hlMode || oneShot) _ = SeekToHighlightAsync(t, manageEnd: _hlMode);
            return;
        }

        if (!_hlMode || _hlEnd <= 0) return;
        // 用户自己拖到了后面：这首就照常放完
        if (current > _hlEnd + 2) { _hlEnd = -1; return; }
        if (!_hlMixPlanned && current >= _hlEnd - 12) { _hlMixPlanned = true; _ = PlanHighlightMixAsync(t); }
        if (current >= _hlEnd && !engine.MixBusy)
        {
            // 没能安排淡入淡出（下一首是还没缓存的云端歌曲等）：直接切
            _hlEnd = -1;
            if (_playMode == "loop_single") { _hlTrackId = null; return; }
            PlayNext();
        }
    }

    private async Task SeekToHighlightAsync(TrackItem t, bool manageEnd)
    {
        var engine = AudioEngineService.Instance;
        // 等引擎真的换到这首（上一首可能还在淡出；云端歌曲要先取音源）
        string? inst = AiAudioService.CanProcess(t) ? AiAudioService.InstrumentalPath(t) : null;   // K 歌模式下放的是伴奏
        bool IsThis() => engine.HasSource && engine.TotalDuration > 0 && engine.CurrentPath is { } p
            && (string.Equals(p, t.FilePath, StringComparison.OrdinalIgnoreCase) || string.Equals(p, inst, StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < 150 && _currentTrack == t && !IsThis(); i++) await Task.Delay(100);
        if (_currentTrack != t || !IsThis()) return;

        AiIndexService.Highlight? h = null;
        bool cached = AiIndexService.Instance.CachedHighlight(t) != null;
        bool paused = false;
        try
        {
            if (!cached && engine.IsPlaying) { engine.Pause(); paused = true; }   // 现算要一两秒：先停住，免得开头响一下再跳
            h = await AiIndexService.Instance.HighlightAsync(t);
        }
        catch (Exception ex) { App.LogError("Highlight", ex.Message); }
        if (_currentTrack != t) return;

        double total = engine.TotalDuration;
        if (h == null)
        {
            if (!manageEnd)
            {
                if (paused) engine.Resume();
                ShowAiStatus(L.T("这首歌没找到明显的高潮"));
                return;
            }
            // 连播模式下找不到（太短、云端歌曲还没下完）：放中间偏前的 30 秒
            if (total < 50) { if (paused) engine.Resume(); return; }
            h = new AiIndexService.Highlight(total * 0.35, Math.Min(total - 1, total * 0.35 + 30));
        }
        else h = AiIndexService.Refine(h, StageLyricsControl.Lyrics, total);

        engine.Seek(h.Start);
        if (paused) engine.Resume();
        if (manageEnd && _hlMode) _hlEnd = h.End;
        if (!manageEnd) ShowAiStatus(L.T("已跳到高潮"));
    }

    /// <summary>当前这首的高潮快结束时：挑好下一首、算好它的高潮，让引擎在高潮结束处交叉淡入下一首的高潮。</summary>
    private async Task PlanHighlightMixAsync(TrackItem a)
    {
        var engine = AudioEngineService.Instance;
        try
        {
            var next = _preChosenNext;
            if (next == null)
            {
                next = PickNextTrack(out var reason, out var byRoam);
                if (next != null) { _preChosenNext = next; _preChosenReason = reason; _preChosenByRoam = byRoam; }
            }
            if (next == null || next == a || next.IsCueTrack || next.IsMissing) return;
            if (next.IsOnline)
            {
                var source = Services.Cloud.CloudSources.For(next);
                if (source == null) return;
                if (!source.IsCached(next)) { source.Prefetch(next); return; }
                if (string.IsNullOrEmpty(next.FilePath) || !System.IO.File.Exists(next.FilePath))
                    await source.ResolveAsync(next, System.Threading.CancellationToken.None);
            }
            if (string.IsNullOrEmpty(next.FilePath) || !System.IO.File.Exists(next.FilePath)) return;

            var nh = await AiIndexService.Instance.HighlightAsync(next);
            double dur = next.Duration;
            if (nh == null)
            {
                if (dur < 50) return;
                nh = new AiIndexService.Highlight(dur * 0.35, dur * 0.35 + 30);
            }
            else nh = AiIndexService.Refine(nh, AiIndexService.LocalLyrics(next), dur);

            if (_currentTrack != a || !_hlMode || _hlEnd <= 0 || !engine.IsPlaying || engine.MixBusy) return;
            const double fade = 3;
            double startAt = Math.Max(engine.CurrentTime + 0.5, _hlEnd - fade);
            // 下一首提前 1.5 秒开始淡入，副歌的第一句正好落在淡入完成前后
            bool ok = engine.TryScheduleMix(next.FilePath, Math.Max(0, nh.Start - 1.5), startAt, fade, 1.0,
                bassSwap: false, skipLeadingSilence: false, nextGainDb: next.ReplayGainDb, nextPeak: next.ReplayPeak);
            if (ok)
            {
                _mixNext = next;
                _mixPlan = null;
                _hlAdopt = (next, nh.End);
            }
        }
        catch (Exception ex) { App.LogError("Highlight mix", ex.Message); }
    }
}
