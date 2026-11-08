using System;
using System.IO;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using NAudio.Wave;

#pragma warning disable CS0618

namespace Klang_WinUI.Services;

public class AudioEngineService : IDisposable
{
    private static readonly Lazy<AudioEngineService> _instance = new(() => new AudioEngineService());
    public static AudioEngineService Instance => _instance.Value;

    private IWavePlayer? _wavePlayer;
    private WaveStream? _audioFileReader;
    private DispatcherQueueTimer? _positionTimer;

    public event Action<PlaybackState>? StateChanged;
    public event Action<double, double>? PositionChanged;
    public event Action? TrackEnded;

    public bool IsPlaying => _wavePlayer?.PlaybackState == PlaybackState.Playing;
    public double CurrentTime => _audioFileReader?.CurrentTime.TotalSeconds ?? 0;
    public double TotalDuration => _audioFileReader?.TotalTime.TotalSeconds ?? 0;

    private float _volume = 0.85f;
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_wavePlayer is WasapiOut wasapi)
            {
                // WasapiOut doesn't always support direct volume if in Exclusive,
                // but in Shared or via sample provider it's handled.
            }
            if (_audioFileReader is AudioFileReader afr)
            {
                afr.Volume = _volume;
            }
        }
    }

    public AudioEngineService()
    {
    }

    public void InitializeDispatcher(DispatcherQueue dispatcherQueue)
    {
        _positionTimer = dispatcherQueue.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(50);
        _positionTimer.Tick += (s, e) =>
        {
            if (IsPlaying && _audioFileReader != null)
            {
                PositionChanged?.Invoke(CurrentTime, TotalDuration);
            }
        };
    }

    public void Play(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        Stop();

        try
        {
            // Windows 10/11 MediaFoundationReader 原生支持 FLAC, MP3, WAV, AAC, M4A, ALAC
            _audioFileReader = new MediaFoundationReader(filePath);

            // 使用低延迟 WASAPI 共享模式 (Shared mode, 50ms buffer)
            var wasapi = new WasapiOut(AudioClientShareMode.Shared, 50);
            wasapi.PlaybackStopped += OnPlaybackStopped;
            wasapi.Init(_audioFileReader);

            _wavePlayer = wasapi;
            _wavePlayer.Play();
            _positionTimer?.Start();

            StateChanged?.Invoke(PlaybackState.Playing);
            PositionChanged?.Invoke(0, TotalDuration);
        }
        catch (Exception)
        {
            // 回退到标准 AudioFileReader
            try
            {
                var afr = new AudioFileReader(filePath) { Volume = _volume };
                _audioFileReader = afr;

                var wasapi = new WasapiOut(AudioClientShareMode.Shared, 50);
                wasapi.PlaybackStopped += OnPlaybackStopped;
                wasapi.Init(_audioFileReader);

                _wavePlayer = wasapi;
                _wavePlayer.Play();
                _positionTimer?.Start();

                StateChanged?.Invoke(PlaybackState.Playing);
                PositionChanged?.Invoke(0, TotalDuration);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Audio play error: {ex.Message}");
            }
        }
    }

    public void Pause()
    {
        if (_wavePlayer != null && IsPlaying)
        {
            _wavePlayer.Pause();
            _positionTimer?.Stop();
            StateChanged?.Invoke(PlaybackState.Paused);
        }
    }

    public void Resume()
    {
        if (_wavePlayer != null && _wavePlayer.PlaybackState == PlaybackState.Paused)
        {
            _wavePlayer.Play();
            _positionTimer?.Start();
            StateChanged?.Invoke(PlaybackState.Playing);
        }
    }

    public void TogglePlayPause()
    {
        if (IsPlaying) Pause();
        else Resume();
    }

    public void Stop()
    {
        _positionTimer?.Stop();
        if (_wavePlayer != null)
        {
            _wavePlayer.PlaybackStopped -= OnPlaybackStopped;
            _wavePlayer.Stop();
            _wavePlayer.Dispose();
            _wavePlayer = null;
        }

        if (_audioFileReader != null)
        {
            _audioFileReader.Dispose();
            _audioFileReader = null;
        }

        StateChanged?.Invoke(PlaybackState.Stopped);
    }

    public void Seek(double seconds)
    {
        if (_audioFileReader != null)
        {
            var clamped = Math.Clamp(seconds, 0, TotalDuration);
            _audioFileReader.CurrentTime = TimeSpan.FromSeconds(clamped);
            PositionChanged?.Invoke(clamped, TotalDuration);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        _positionTimer?.Stop();
        StateChanged?.Invoke(PlaybackState.Stopped);

        if (_audioFileReader != null && Math.Abs(CurrentTime - TotalDuration) < 1.0)
        {
            TrackEnded?.Invoke();
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
