using Klang_WinUI.Models;
using Klang_WinUI.Services;
using Klang_WinUI.Services.Soda;
using Microsoft.UI.Xaml;

namespace Klang_WinUI;

public sealed partial class MainWindow
{
    private bool _sodaViewVisible;
    private bool _sodaChoosing;
    private bool _sodaAdvancing;
    private bool _sodaWarming;
    private string _sodaAccount = "";
    private SodaFeedQueue? _sodaFeed;
    private readonly CancellationTokenSource _sodaLifetime = new();
    private CancellationTokenSource? _sodaSelection;
    private CancellationTokenSource? _sodaChannel;
    private int _sodaSelectionVersion;

    private void InitializeSoda()
    {
        SodaHome.ModeRequested += mode => _ = ChooseSodaModeAsync(mode);
        SodaHome.TrackRequested += (tracks, track, mode) => _ = ChooseSodaTrackAsync(tracks, track, mode);
        SodaService.Instance.CoverCached += OnSodaCoverCached;
        SodaService.Instance.LoginChanged += OnSodaLoginChanged;
        _sodaAccount = SodaService.Instance.UserId;
    }

    private void OnSodaCoverCached(TrackItem track) => DispatcherQueue.SafeEnqueue(() => RefreshTrackCover(track));

    private void OnSodaLoginChanged() => DispatcherQueue.SafeEnqueue(() =>
    {
        var account = SodaService.Instance.UserId;
        if (_sodaAccount == account) return;
        _sodaAccount = account;
        ResetSodaChannel();
        if (_currentTrack?.SodaId.Length > 0)
        {
            _onlineCts?.Cancel();
            _playSeq++;
            AudioEngineService.Instance.Stop();
            BottomTransportBar.UpdateLyricSnippet(L.T("汽水账号已变更，请重新选择歌曲"));
        }
    });

    private void SetSodaView(bool visible)
    {
        if (_sodaViewVisible == visible) return;
        _sodaViewVisible = visible;
        // Soda uses the same sidebar and now-playing stage as the NetEase page.
        if (visible) SetSidebarOpen(true);
        else ShowSidebarPart(_isSidebarOpen);
        if (visible)
        {
            CloseGallery();
            _ = ShowSodaHomeAsync();
        }
        else SodaHome.OnHidden();
    }

    private async Task ShowSodaHomeAsync()
    {
        try { await SodaHome.OnShownAsync(this); }
        catch (OperationCanceledException) { }
        catch { SodaHome.SetPlaybackStatus(L.T("汽水音乐暂时无法加载，请稍后重试")); }
    }

    private CancellationToken BeginSodaSelection()
    {
        _sodaSelection?.Cancel();
        _sodaSelection?.Dispose();
        _sodaSelection = CancellationTokenSource.CreateLinkedTokenSource(_sodaLifetime.Token);
        _sodaSelectionVersion++;
        return _sodaSelection.Token;
    }

    private async Task ChooseSodaModeAsync(SodaMode mode)
    {
        var ct = BeginSodaSelection();
        var version = _sodaSelectionVersion;
        _sodaChoosing = true;
        SodaHome.SetPlaybackStatus(L.F("正在开启{0}…", mode.Name), true);
        try
        {
            if (!SodaService.Instance.IsLoggedIn) throw new InvalidOperationException(L.T("请先登录汽水音乐"));
            var queue = new SodaFeedQueue(mode, SodaService.Instance.GetFeedAsync);
            while (queue.Tracks.Count == 0 && queue.HasMore) await queue.LoadMoreAsync(ct);
            TrackItem? first = null;
            foreach (var candidate in queue.Tracks.Take(5))
            {
                if (await SodaService.Instance.ResolveAsync(candidate, ct)) { first = candidate; break; }
            }
            ct.ThrowIfCancellationRequested();
            if (version != _sodaSelectionVersion) return;
            if (first == null) throw new InvalidOperationException(L.T("当前模式暂时没有可播放的歌曲，请换个模式或重试"));

            _sodaChannel?.Cancel();
            _sodaChannel?.Dispose();
            _sodaChannel = CancellationTokenSource.CreateLinkedTokenSource(_sodaLifetime.Token);
            _sodaFeed = queue;
            _sodaChoosing = false;
            LeftNavPane.SetExternalQueue(queue.Tracks);
            SodaHome.ShowTracks(queue.Tracks, mode.Name, mode);
            PlayTrack(first);
            queue.MarkPlayed(first);
            SodaHome.SetPlaybackStatus(L.F("正在播放 · {0}", mode.Name));
            _ = WarmSodaQueueAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        { if (version == _sodaSelectionVersion) SodaHome.SetPlaybackStatus(ex.Message); }
        finally { if (version == _sodaSelectionVersion) _sodaChoosing = false; }
    }

    private async Task ChooseSodaTrackAsync(List<TrackItem> tracks, TrackItem track, SodaMode? mode)
    {
        var ct = BeginSodaSelection();
        int version = _sodaSelectionVersion;
        _sodaChoosing = true;
        SodaHome.SetPlaybackStatus(L.T("正在准备播放…"), true);
        try
        {
            if (!await SodaService.Instance.ResolveAsync(track, ct))
                throw new InvalidOperationException(L.T("这首歌曲暂不可播放，可能仅提供试听或需要会员"));
            ct.ThrowIfCancellationRequested();
            if (version != _sodaSelectionVersion) return;
            // Selecting a favorite must leave the previous continuous channel.
            if (mode == null || _sodaFeed?.Mode.Key != mode.Key)
            {
                _sodaChannel?.Cancel();
                _sodaFeed = null;
                LeftNavPane.SetExternalQueue(tracks);
            }
            PlayTrack(track);
            _sodaFeed?.MarkPlayed(track);
            SodaHome.SetPlaybackStatus(L.T("正在播放"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == _sodaSelectionVersion) SodaHome.SetPlaybackStatus(ex.Message); }
        finally { if (version == _sodaSelectionVersion) _sodaChoosing = false; }
    }

    private bool HasSodaFeed => _sodaFeed != null && ReferenceEquals(LeftNavPane.Queue, _sodaFeed.Tracks)
        && _currentTrack?.SodaId.Length > 0;

    private bool TryPlaySodaNext()
    {
        if (_sodaChoosing) return true;
        if (!HasSodaFeed) return false;
        if (!_sodaAdvancing) _ = AdvanceSodaAsync();
        return true;
    }

    private async Task AdvanceSodaAsync()
    {
        var queue = _sodaFeed!;
        var current = _currentTrack;
        var ct = _sodaChannel?.Token ?? _sodaLifetime.Token;
        int version = _sodaSelectionVersion;
        _sodaAdvancing = true;
        try
        {
            int index = current == null ? 0 : queue.Tracks.IndexOf(current) + 1;
            for (int tried = 0; tried < 8; tried++, index++)
            {
                while (index >= queue.Tracks.Count && queue.HasMore) await queue.LoadMoreAsync(ct);
                if (index >= queue.Tracks.Count) break;
                var next = queue.Tracks[index];
                if (!await SodaService.Instance.ResolveAsync(next, ct)) continue;
                ct.ThrowIfCancellationRequested();
                if (_sodaFeed != queue || _currentTrack != current || version != _sodaSelectionVersion) return;
                _preChosenNext = null;
                PlayTrack(next);
                queue.MarkPlayed(next);
                SodaHome.RefreshPlayingQueue(queue.Tracks, queue.Mode);
                SodaHome.SetPlaybackStatus(L.F("正在播放 · {0}", queue.Mode.Name));
                _ = WarmSodaQueueAsync();
                return;
            }
            if (_sodaFeed == queue && _currentTrack == current && version == _sodaSelectionVersion)
                SodaHome.SetPlaybackStatus(L.T("当前模式暂时没有更多可播放的歌曲，请重试"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_sodaFeed == queue && version == _sodaSelectionVersion)
            { SodaHome.SetPlaybackStatus(ex.Message); BottomTransportBar.UpdateLyricSnippet(ex.Message); }
        }
        finally { _sodaAdvancing = false; }
    }

    private async Task WarmSodaQueueAsync()
    {
        if (!HasSodaFeed || _sodaWarming || _sodaChoosing) return;
        var queue = _sodaFeed!;
        var current = _currentTrack!;
        var ct = _sodaChannel?.Token ?? _sodaLifetime.Token;
        _sodaWarming = true;
        try
        {
            int index = queue.Tracks.IndexOf(current);
            if (queue.Tracks.Count - index <= 3 && queue.HasMore) await queue.LoadMoreAsync(ct);
            if (_sodaFeed != queue || _currentTrack != current || ct.IsCancellationRequested) return;
            if (index + 1 < queue.Tracks.Count) SodaService.Instance.Prefetch(queue.Tracks[index + 1]);
        }
        catch (OperationCanceledException) { }
        catch { /* Background prefetch must not interrupt playback. Next provides a retry. */ }
        finally { _sodaWarming = false; }
    }

    private void ResetSodaChannel()
    {
        _sodaSelection?.Cancel();
        _sodaChannel?.Cancel();
        _sodaSelectionVersion++;
        _sodaChoosing = false;
        _sodaFeed = null;
    }

    private void CancelPendingSodaPlayback()
    {
        _sodaSelection?.Cancel();
        _sodaSelectionVersion++;
        _sodaChoosing = false;
        _sodaChannel?.Cancel();
        _sodaChannel?.Dispose();
        _sodaChannel = CancellationTokenSource.CreateLinkedTokenSource(_sodaLifetime.Token);
    }

    private void CloseSoda()
    {
        _sodaLifetime.Cancel();
        ResetSodaChannel();
        SodaHome.OnHidden();
        SodaService.Instance.CoverCached -= OnSodaCoverCached;
        SodaService.Instance.LoginChanged -= OnSodaLoginChanged;
    }
}
