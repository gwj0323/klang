using System;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services;

/// <summary>
/// 系统媒体传输控件 (SMTC)：键盘多媒体键、锁屏 / 音量浮层上的曲目信息与进度、上一首 / 下一首 / 播放暂停。
/// 未打包应用通过一个不播放的 MediaPlayer 取得 SMTC 实例（对应 Qt 版 media_control_service）。
/// </summary>
public sealed class MediaControlService : IDisposable
{
    private MediaPlayer? _player;
    private SystemMediaTransportControls? _smtc;
    private DispatcherQueue? _dispatcher;
    private DateTime _lastTimelineUpdate = DateTime.MinValue;

    public event Action? PlayRequested;
    public event Action? PauseRequested;
    public event Action? NextRequested;
    public event Action? PrevRequested;
    public event Action<double>? SeekRequested;

    public bool IsAvailable => _smtc != null;

    public void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        try
        {
            _player = new MediaPlayer();
            _player.CommandManager.IsEnabled = false; // 由我们手动驱动 SMTC
            _smtc = _player.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.IsStopEnabled = false;
            _smtc.ButtonPressed += OnButtonPressed;
            _smtc.PlaybackPositionChangeRequested += OnPositionChangeRequested;
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SMTC init failed: {ex.Message}");
            _smtc = null;
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var button = args.Button;
        _dispatcher?.SafeEnqueue(() =>
        {
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play: PlayRequested?.Invoke(); break;
                case SystemMediaTransportControlsButton.Pause: PauseRequested?.Invoke(); break;
                case SystemMediaTransportControlsButton.Next: NextRequested?.Invoke(); break;
                case SystemMediaTransportControlsButton.Previous: PrevRequested?.Invoke(); break;
            }
        });
    }

    private void OnPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
    {
        double sec = args.RequestedPlaybackPosition.TotalSeconds;
        _dispatcher?.SafeEnqueue(() => SeekRequested?.Invoke(sec));
    }

    public void UpdateTrack(TrackItem? track)
    {
        if (_smtc == null) return;
        try
        {
            var updater = _smtc.DisplayUpdater;
            if (track == null)
            {
                updater.ClearAll();
                updater.Update();
                _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                return;
            }

            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = track.Title;
            updater.MusicProperties.Artist = track.Artist;
            updater.MusicProperties.AlbumTitle = track.Album;
            if (!string.IsNullOrEmpty(track.CoverPath) && System.IO.File.Exists(track.CoverPath))
            {
                updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(track.CoverPath));
            }
            else
            {
                updater.Thumbnail = null;
            }
            updater.Update();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SMTC update track failed: {ex.Message}");
        }
    }

    public void SetPlaybackState(bool isPlaying, bool hasTrack)
    {
        if (_smtc == null) return;
        try
        {
            _smtc.PlaybackStatus = !hasTrack
                ? MediaPlaybackStatus.Closed
                : (isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused);
        }
        catch { }
    }

    /// <summary>更新系统浮层上的进度条（限流到每秒一次）。</summary>
    public void UpdateTimeline(double positionSec, double durationSec)
    {
        if (_smtc == null || durationSec <= 0) return;
        var now = DateTime.UtcNow;
        if ((now - _lastTimelineUpdate).TotalMilliseconds < 1000) return;
        _lastTimelineUpdate = now;
        try
        {
            var tl = new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = TimeSpan.FromSeconds(Math.Clamp(positionSec, 0, durationSec)),
                MaxSeekTime = TimeSpan.FromSeconds(durationSec),
                EndTime = TimeSpan.FromSeconds(durationSec)
            };
            _smtc.UpdateTimelineProperties(tl);
        }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            if (_smtc != null)
            {
                _smtc.ButtonPressed -= OnButtonPressed;
                _smtc.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
                _smtc.IsEnabled = false;
            }
            _player?.Dispose();
        }
        catch { }
        _player = null;
        _smtc = null;
    }
}
