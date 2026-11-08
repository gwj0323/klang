using System;
using System.IO;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Klang_WinUI.Services.Dsp;

#pragma warning disable CS0618

namespace Klang_WinUI.Services;

public class AudioEngineService : IDisposable
{
    private static readonly Lazy<AudioEngineService> _instance = new(() => new AudioEngineService());
    public static AudioEngineService Instance => _instance.Value;

    private IWavePlayer? _wavePlayer;
    private WaveStream? _audioFileReader;
    private DspSampleProvider? _dspProvider;
    private DispatcherQueueTimer? _positionTimer;

    public event Action<PlaybackState>? StateChanged;
    public event Action<double, double>? PositionChanged;
    public event Action<float[]>? SpectrumChanged;
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
            if (_dspProvider != null)
            {
                _dspProvider.Volume = _volume;
            }
        }
    }

    // DSP 状态持久与同步
    public string CurrentPreset { get; private set; } = "默认原声";
    public float[] EqGains { get; } = new float[5];
    public SpatialMode SpatialAudioMode { get; private set; } = SpatialMode.DolbyAtmos;
    public float VocalBalance { get; private set; } = 0.0f;
    public float VinylWarmth { get; private set; } = 0.0f;
    public string HeadphoneProfileId { get; private set; } = "bypass";

    public AudioEngineService()
    {
    }

    public void InitializeDispatcher(DispatcherQueue dispatcherQueue)
    {
        _positionTimer = dispatcherQueue.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(40); // 25fps 丝滑状态刷新
        _positionTimer.Tick += (s, e) =>
        {
            if (IsPlaying && _audioFileReader != null)
            {
                PositionChanged?.Invoke(CurrentTime, TotalDuration);

                // 派发真实声学能量频谱
                if (_dspProvider != null)
                {
                    SpectrumChanged?.Invoke(_dspProvider.Processor.GetCurrentSpectrum());
                }
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

            var sampleProvider = _audioFileReader.ToSampleProvider();
            _dspProvider = new DspSampleProvider(sampleProvider)
            {
                Volume = _volume
            };
            ApplyDspSettingsTo(_dspProvider.Processor);

            var wasapi = new WasapiOut(AudioClientShareMode.Shared, 50);
            wasapi.PlaybackStopped += OnPlaybackStopped;
            wasapi.Init(_dspProvider.ToWaveProvider());

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
                var afr = new AudioFileReader(filePath);
                _audioFileReader = afr;

                _dspProvider = new DspSampleProvider(afr.ToSampleProvider())
                {
                    Volume = _volume
                };
                ApplyDspSettingsTo(_dspProvider.Processor);

                var wasapi = new WasapiOut(AudioClientShareMode.Shared, 50);
                wasapi.PlaybackStopped += OnPlaybackStopped;
                wasapi.Init(_dspProvider.ToWaveProvider());

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

        _dspProvider = null;
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

    #region DSP 控制接口

    public void SetPreset(string presetName)
    {
        CurrentPreset = presetName;
        _dspProvider?.Processor.ApplyPreset(presetName);
        if (_dspProvider != null)
        {
            Array.Copy(_dspProvider.Processor.GainsDb, EqGains, 5);
        }
    }

    public void SetEqGains(float[] gains)
    {
        for (int i = 0; i < Math.Min(5, gains.Length); i++)
        {
            EqGains[i] = gains[i];
        }
        CurrentPreset = "自定义";
        _dspProvider?.Processor.SetGains(EqGains);
    }

    public void SetSpatialMode(SpatialMode mode)
    {
        SpatialAudioMode = mode;
        _dspProvider?.Processor.Spatializer.SetMode(mode);
    }

    public void SetVocalBalance(float balance)
    {
        VocalBalance = Math.Clamp(balance, -1.0f, 1.0f);
        if (_dspProvider != null)
        {
            _dspProvider.Processor.VocalBalance = VocalBalance;
        }
    }

    public void SetVinylWarmth(float warmth)
    {
        VinylWarmth = Math.Clamp(warmth, 0.0f, 1.0f);
        if (_dspProvider != null)
        {
            _dspProvider.Processor.VinylWarmth = VinylWarmth;
        }
    }

    public void SetHeadphoneProfile(string profileId)
    {
        HeadphoneProfileId = profileId;
        _dspProvider?.Processor.SetHeadphoneProfile(profileId);
    }

    private void ApplyDspSettingsTo(NordicDspProcessor processor)
    {
        if (CurrentPreset != "自定义")
        {
            processor.ApplyPreset(CurrentPreset);
        }
        else
        {
            processor.SetGains(EqGains);
        }

        processor.Spatializer.SetMode(SpatialAudioMode);
        processor.VocalBalance = VocalBalance;
        processor.VinylWarmth = VinylWarmth;
        processor.SetHeadphoneProfile(HeadphoneProfileId);
    }

    #endregion

    public void Dispose()
    {
        Stop();
    }
}
