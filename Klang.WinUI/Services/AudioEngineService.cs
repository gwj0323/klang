using System;
using System.Collections.Generic;
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

    // CUE 分轨：在整轨文件内的播放区间（_segEnd <= 0 表示播放到文件末尾）
    private double _segStart = 0;
    private double _segEnd = 0;

    /// <summary>WASAPI 共享模式缓冲时长（毫秒），下次开始播放时生效。</summary>
    public int LatencyMs { get; set; } = 50;

    public bool HasSource => _audioFileReader != null;
    public bool IsPlaying => _wavePlayer?.PlaybackState == PlaybackState.Playing;
    public double CurrentTime => Math.Max(0, (_audioFileReader?.CurrentTime.TotalSeconds ?? 0) - _segStart);
    public double TotalDuration
    {
        get
        {
            if (_audioFileReader == null) return 0;
            double fileTotal = _audioFileReader.TotalTime.TotalSeconds;
            double end = _segEnd > 0 ? Math.Min(_segEnd, fileTotal) : fileTotal;
            return Math.Max(0, end - _segStart);
        }
    }

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
            CheckSleepTimer();
            if (!IsPlaying) return;

            if (IsPlaying && _audioFileReader != null)
            {
                // CUE 分轨播放到区间末尾：手动结束并通知切歌
                if (_segEnd > 0 && _audioFileReader.CurrentTime.TotalSeconds >= _segEnd - 0.03)
                {
                    Stop();
                    TrackEnded?.Invoke();
                    return;
                }

                PositionChanged?.Invoke(CurrentTime, TotalDuration);

                // 派发真实声学能量频谱
                if (_dspProvider != null)
                {
                    SpectrumChanged?.Invoke(_dspProvider.Processor.GetCurrentSpectrum());
                }
            }
        };
    }

    public void Play(string filePath, double segmentStart = 0, double segmentEnd = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        Stop();
        _segStart = Math.Max(0, segmentStart);
        _segEnd = segmentEnd > _segStart ? segmentEnd : 0;

        try
        {
            // Windows 10/11 MediaFoundationReader 原生支持 FLAC, MP3, WAV, AAC, M4A, ALAC
            StartPipeline(new MediaFoundationReader(filePath));
        }
        catch (Exception)
        {
            // 回退到标准 AudioFileReader
            try
            {
                DisposeCurrent();
                StartPipeline(new AudioFileReader(filePath));
            }
            catch (Exception ex)
            {
                DisposeCurrent();
                System.Diagnostics.Debug.WriteLine($"Audio play error: {ex.Message}");
            }
        }
    }

    /// <summary>读取 → (变速) → DSP → WASAPI 输出。</summary>
    private void StartPipeline(WaveStream reader)
    {
        _audioFileReader = reader;
        if (_segStart > 0) reader.CurrentTime = TimeSpan.FromSeconds(_segStart);

        _stretch = new TimeStretchSampleProvider(reader.ToSampleProvider())
        {
            Rate = _playbackRate
        };
        _dspProvider = new DspSampleProvider(_stretch)
        {
            Volume = _volume,
            TrackGain = ComputeTrackGain()
        };
        ApplyDspSettingsTo(_dspProvider.Processor);

        var wasapi = CreateOutput();
        wasapi.PlaybackStopped += OnPlaybackStopped;
        wasapi.Init(_dspProvider.ToWaveProvider());

        _wavePlayer = wasapi;
        _wavePlayer.Play();
        _positionTimer?.Start();

        StateChanged?.Invoke(PlaybackState.Playing);
        PositionChanged?.Invoke(0, TotalDuration);
    }

    private WasapiOut CreateOutput()
    {
        if (!string.IsNullOrEmpty(OutputDeviceId) && OutputDeviceId != "default")
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(OutputDeviceId);
                if (device.State == DeviceState.Active)
                {
                    return new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMs);
                }
            }
            catch
            {
                // 设备已拔出 / 不可用：回退系统默认设备
            }
        }
        return new WasapiOut(AudioClientShareMode.Shared, LatencyMs);
    }

    private void DisposeCurrent()
    {
        if (_wavePlayer != null)
        {
            _wavePlayer.PlaybackStopped -= OnPlaybackStopped;
            try { _wavePlayer.Stop(); } catch { }
            _wavePlayer.Dispose();
            _wavePlayer = null;
        }
        _audioFileReader?.Dispose();
        _audioFileReader = null;
        _dspProvider = null;
        _stretch = null;
    }

    #region 变速 / 回放增益 / 输出设备 / 睡眠定时

    private TimeStretchSampleProvider? _stretch;
    private double _playbackRate = 1.0;

    /// <summary>播放速度 0.5–2.0，保持音高（WSOLA）。</summary>
    public double PlaybackRate
    {
        get => _playbackRate;
        set
        {
            _playbackRate = Math.Clamp(value, 0.5, 2.0);
            if (_stretch != null) _stretch.Rate = _playbackRate;
        }
    }

    public bool ReplayGainEnabled { get; private set; }
    private double? _trackGainDb;
    private double? _trackPeak;

    public void SetReplayGainEnabled(bool enabled)
    {
        ReplayGainEnabled = enabled;
        if (_dspProvider != null) _dspProvider.TrackGain = ComputeTrackGain();
    }

    /// <summary>设置当前曲目的 ReplayGain 标签（在 Play 之前调用）。</summary>
    public void SetTrackReplayGain(double? gainDb, double? peak)
    {
        _trackGainDb = gainDb;
        _trackPeak = peak;
        if (_dspProvider != null) _dspProvider.TrackGain = ComputeTrackGain();
    }

    private float ComputeTrackGain()
    {
        if (!ReplayGainEnabled || _trackGainDb is not double db) return 1.0f;
        double gain = Math.Pow(10.0, db / 20.0);
        // 防削波：增益后的峰值不超过满刻度
        if (_trackPeak is double peak && peak > 0) gain = Math.Min(gain, 1.0 / peak);
        return (float)Math.Clamp(gain, 0.05, 4.0);
    }

    public string OutputDeviceId { get; set; } = "default";

    public record OutputDeviceInfo(string Id, string Name);

    public static List<OutputDeviceInfo> GetOutputDevices()
    {
        var list = new List<OutputDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new OutputDeviceInfo(d.ID, d.FriendlyName));
                d.Dispose();
            }
        }
        catch { }
        return list;
    }

    private DateTime? _sleepAt;
    public DateTime? SleepTimerEnd => _sleepAt;
    public event Action? SleepTimerElapsed;

    /// <summary>minutes &lt;= 0 取消睡眠定时。</summary>
    public void SetSleepTimer(int minutes)
    {
        _sleepAt = minutes > 0 ? DateTime.UtcNow.AddMinutes(minutes) : null;
    }

    private void CheckSleepTimer()
    {
        if (_sleepAt.HasValue && DateTime.UtcNow >= _sleepAt.Value)
        {
            _sleepAt = null;
            Pause();
            SleepTimerElapsed?.Invoke();
        }
    }

    #endregion

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

    /// <summary>
    /// 先把输出淡出再停止（切歌时避免电平突然截断造成的“啪”声）。
    /// 淡出只影响之后生成的采样，所以要再等一个输出缓冲的时间，让淡出的部分真正播完。
    /// </summary>
    public async System.Threading.Tasks.Task FadeOutAndStopAsync()
    {
        var dsp = _dspProvider;
        if (dsp != null && IsPlaying)
        {
            dsp.RequestFade(0f, 25);
            await System.Threading.Tasks.Task.Delay(LatencyMs + 45);
        }
        Stop();
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
        _stretch = null;
        StateChanged?.Invoke(PlaybackState.Stopped);
    }

    public void Seek(double seconds)
    {
        if (_audioFileReader != null)
        {
            var clamped = Math.Clamp(seconds, 0, TotalDuration);
            _audioFileReader.CurrentTime = TimeSpan.FromSeconds(_segStart + clamped);
            _stretch?.Reset();
            _dspProvider?.RestartFadeIn(15);
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

    #region 设置持久化

    /// <summary>从保存的状态恢复音量与全部 DSP 设置（在 UI 初始化前调用）。</summary>
    public void LoadSettingsFromState(AppState state)
    {
        _volume = Math.Clamp((float)state.Volume, 0f, 1f);
        LatencyMs = Math.Clamp(state.WasapiLatencyMs, 20, 500);
        _playbackRate = Math.Clamp(state.PlaybackRate, 0.5, 2.0);
        OutputDeviceId = string.IsNullOrEmpty(state.SelectedDeviceId) ? "default" : state.SelectedDeviceId;
        ReplayGainEnabled = state.ReplayGainEnabled;

        CurrentPreset = string.IsNullOrWhiteSpace(state.SoundProfile) ? "默认原声" : state.SoundProfile;
        for (int i = 0; i < 5; i++) EqGains[i] = state.EqGainsDb[i];
        if (Enum.TryParse<SpatialMode>(state.SpatialMode, out var mode)) SpatialAudioMode = mode;
        VocalBalance = Math.Clamp(state.VocalBalance, -1f, 1f);
        VinylWarmth = Math.Clamp(state.VinylWarmth, 0f, 1f);
        HeadphoneProfileId = string.IsNullOrWhiteSpace(state.HeadphoneProfile) ? "bypass" : state.HeadphoneProfile;
    }

    /// <summary>把音量与 DSP 设置写回状态（调用方负责 RequestSave）。</summary>
    public void SaveSettingsToState(AppState state)
    {
        state.Volume = _volume;
        state.WasapiLatencyMs = LatencyMs;
        state.PlaybackRate = _playbackRate;
        state.SelectedDeviceId = OutputDeviceId;
        state.ReplayGainEnabled = ReplayGainEnabled;
        state.SoundProfile = CurrentPreset;
        state.EqGainsDb = (float[])EqGains.Clone();
        state.SpatialMode = SpatialAudioMode.ToString();
        state.VocalBalance = VocalBalance;
        state.VinylWarmth = VinylWarmth;
        state.HeadphoneProfile = HeadphoneProfileId;
    }

    #endregion

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
        CurrentPreset = L.T("自定义");
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
