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

                MixTick();
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
        _filePath = filePath;
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

    private string? _filePath;

    /// <summary>输出设备 / 独占模式改了之后，在当前位置无缝重开输出（保持播放或暂停状态）。</summary>
    public void ReopenOutput()
    {
        if (_audioFileReader == null || string.IsNullOrEmpty(_filePath)) return;
        string path = _filePath;
        double start = _segStart, end = _segEnd, pos = CurrentTime;
        bool wasPlaying = IsPlaying;
        Play(path, start, end);
        if (!HasSource) return;
        if (pos > 0.05) Seek(pos);
        if (!wasPlaying) Pause();
    }

    // ---------------- 节拍对齐的自动混音 ----------------

    private CrossfadeSampleProvider? _xfade;
    private WaveStream? _pendingNextReader, _pendingOldReader;
    private TimeStretchSampleProvider? _pendingNextStretch;
    private string? _pendingNextPath;
    private bool _mixAdopted;
    private bool _rampOn;
    private double _rampFrom;
    private DateTime _rampStart;

    /// <summary>下一首在引擎里“成为当前曲目”时触发（界面线程）：已经淡到一半，界面该切到新歌了。</summary>
    public event Action<string>? MixAdopted;

    /// <summary>有一次混音已经安排、正在进行，或刚结束还在收尾。</summary>
    public bool MixBusy => _xfade != null && (_xfade.State != CrossfadeSampleProvider.MixState.None || _pendingOldReader != null || _pendingNextReader != null);

    /// <summary>
    /// 安排一次混音：当前曲目播到 startAtSec（它自己时间轴上的秒数）时，把 nextPath 从 nextStartSec 开始淡入，
    /// 持续 fadeSec（当前曲目时间轴上的秒数）；下一首按 rateRatio 变速，让两首的速度对上。
    /// 不满足条件（格式不支持、时间太近、CUE 分轨等）返回 false，调用方就按正常方式切歌。
    /// </summary>
    /// <param name="bassSwap">淡入淡出期间做低频交接（节拍对齐的混音用）。</param>
    /// <param name="skipLeadingSilence">下一首从它开头第一个有声音的位置放起（普通交叉淡入淡出用，nextStartSec 被忽略）。</param>
    /// <param name="nextGainDb">下一首的回放增益：混音过程中总增益从当前这首平滑过渡到它，不再在中途跳变。</param>
    public bool TryScheduleMix(string nextPath, double nextStartSec, double startAtSec, double fadeSec, double rateRatio,
        bool bassSwap = false, bool skipLeadingSilence = false, double? nextGainDb = null, double? nextPeak = null)
    {
        var x = _xfade; var reader = _audioFileReader; var st = _stretch;
        if (x == null || reader == null || st == null || MixBusy || _segStart > 0 || _segEnd > 0) return false;
        if (!File.Exists(nextPath)) return false;

        int R = x.WaveFormat.SampleRate, C = x.WaveFormat.Channels;
        double curRate = Math.Max(0.1, st.Rate);
        double untilSec = startAtSec - reader.CurrentTime.TotalSeconds;
        if (untilSec < 0.8) return false;

        WaveStream? nextReader = null;
        try
        {
            nextReader = new MediaFoundationReader(nextPath);
            if (skipLeadingSilence) nextStartSec = FindLeadingSound(nextReader);
            nextReader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, nextStartSec));
            ISampleProvider sp = nextReader.ToSampleProvider();
            if (sp.WaveFormat.Channels > 2) { nextReader.Dispose(); return false; }
            if (sp.WaveFormat.Channels != C) sp = C == 2 ? new MonoToStereoSampleProvider(sp) : new StereoToMonoSampleProvider(sp);
            if (sp.WaveFormat.SampleRate != R) sp = new WdlResamplingSampleProvider(sp, R);
            var nst = new TimeStretchSampleProvider(sp) { Rate = curRate * rateRatio };

            long until = (long)(untilSec / curRate * R);
            long fade = (long)(fadeSec / curRate * R);
            if (!x.Schedule(nst, until, fade, bassSwap)) { nextReader.Dispose(); return false; }

            _pendingNextReader = nextReader; _pendingNextStretch = nst; _pendingNextPath = nextPath;
            _mixAdopted = false;
            _mixGainFrom = _dspProvider?.TrackGain ?? 1f;
            _mixGainTo = GainFor(nextGainDb, nextPeak);
            _mixGainRamp = true;
            return true;
        }
        catch
        {
            try { nextReader?.Dispose(); } catch { }
            return false;
        }
    }

    // 回放增益：混音时两首歌的增益可能不同。以前在界面切到下一首的那一刻直接换增益，听起来是一次音量跳变；
    // 现在整段淡入淡出期间，总增益跟着进度从这一首的值平滑过渡到下一首的值。
    private float _mixGainFrom = 1f, _mixGainTo = 1f;
    private bool _mixGainRamp;

    /// <summary>下一首开头第一个有声音的位置（秒）：最多找前 20 秒，阈值约 −50 dB；找不到返回 0。</summary>
    private static double FindLeadingSound(WaveStream reader)
    {
        try
        {
            reader.CurrentTime = TimeSpan.Zero;
            var sp = reader.ToSampleProvider();
            int sr = sp.WaveFormat.SampleRate, ch = sp.WaveFormat.Channels;
            var buf = new float[4096 * ch];
            long frames = 0, limit = (long)sr * 20;
            while (frames < limit)
            {
                int n = sp.Read(buf.AsSpan());
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    if (Math.Abs(buf[i]) > 0.003f)
                        return Math.Max(0, (frames + i / ch) / (double)sr - 0.03);   // 往前留 30 毫秒，不切掉起音
                }
                frames += n / ch;
            }
        }
        catch { }
        return 0;
    }

    /// <summary>取消还没开始的混音（用户跳转、切歌时）。</summary>
    public void CancelMix()
    {
        if (_xfade?.CancelScheduled() == true)
        {
            try { _pendingNextReader?.Dispose(); } catch { }
            _pendingNextReader = null; _pendingNextStretch = null; _pendingNextPath = null;
            _mixGainRamp = false;
        }
    }

    private void ClearMix()
    {
        try { _pendingNextReader?.Dispose(); } catch { }
        try { _pendingOldReader?.Dispose(); } catch { }
        _pendingNextReader = null; _pendingOldReader = null; _pendingNextStretch = null; _pendingNextPath = null;
        _mixAdopted = false; _rampOn = false; _mixGainRamp = false;
        _xfade = null;
    }

    private void MixTick()
    {
        var x = _xfade;
        if (x == null) return;

        // 回放增益随淡入淡出进度过渡（按分贝线性，即增益按几何插值）
        if (_mixGainRamp && _dspProvider != null)
        {
            var gs = x.State;
            if (gs == CrossfadeSampleProvider.MixState.Fading)
            {
                double p = x.Progress; p = p * p * (3 - 2 * p);
                _dspProvider.TrackGain = (float)(_mixGainFrom * Math.Pow(_mixGainTo / Math.Max(1e-4f, _mixGainFrom), p));
            }
            else if (gs == CrossfadeSampleProvider.MixState.None)
            {
                // 淡完了：落到下一首的增益上，之后恢复正常的设置方式
                _dspProvider.TrackGain = _mixAdopted || _pendingNextReader != null ? _mixGainTo : ComputeTrackGain();
                _mixGainRamp = false;
            }
        }

        if (_pendingNextReader != null && !_mixAdopted)
        {
            var s = x.State;
            if ((s == CrossfadeSampleProvider.MixState.Fading && x.Progress >= 0.5) || s == CrossfadeSampleProvider.MixState.None && x.Progress == 0 && _pendingNextReader != null && IsMixFinished(x))
                AdoptMix();
        }
        if (_mixAdopted && x.State == CrossfadeSampleProvider.MixState.None)
        {
            try { _pendingOldReader?.Dispose(); } catch { }
            _pendingOldReader = null;
            _mixAdopted = false;
        }

        if (_rampOn && _stretch != null)
        {
            double t = (DateTime.UtcNow - _rampStart).TotalSeconds / 10.0;
            if (t >= 1) { _stretch.Rate = _playbackRate; _rampOn = false; }
            else { double e = t * t * (3 - 2 * t); _stretch.Rate = _rampFrom + (_playbackRate - _rampFrom) * e; }
        }
    }

    // 淡完了（State 回到 None）但还没来得及“认领”下一首：Schedule 之后 State 不会是 None，所以只要是 None 就说明已经淡完
    private bool IsMixFinished(CrossfadeSampleProvider x) => x.State == CrossfadeSampleProvider.MixState.None;

    private void AdoptMix()
    {
        if (_pendingNextReader == null || _pendingNextStretch == null || _pendingNextPath == null) return;
        _pendingOldReader = _audioFileReader;
        _audioFileReader = _pendingNextReader;
        _stretch = _pendingNextStretch;
        _filePath = _pendingNextPath;
        _segStart = 0; _segEnd = 0;
        string path = _pendingNextPath;
        _pendingNextReader = null; _pendingNextStretch = null; _pendingNextPath = null;
        _mixAdopted = true;

        // 淡入淡出期间下一首被轻微变速过，之后在 10 秒里慢慢回到用户设定的速度
        _rampFrom = _stretch.Rate;
        _rampStart = DateTime.UtcNow;
        _rampOn = Math.Abs(_rampFrom - _playbackRate) > 0.0005;

        MixAdopted?.Invoke(path);
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
        _xfade = new CrossfadeSampleProvider(_stretch);
        _dspProvider = new DspSampleProvider(_xfade)
        {
            Volume = _volume,
            TrackGain = ComputeTrackGain(),
            Bypass = BypassDsp
        };
        ApplyDspSettingsTo(_dspProvider.Processor);

        // 输出：优先按设置走独占模式（文件原采样率 / 位深），失败则回退到共享模式并记下原因
        var plan = BuildOutput(_dspProvider, reader.WaveFormat);
        _outputPlan = plan;
        var wasapi = plan.Player;
        wasapi.PlaybackStopped += OnPlaybackStopped;

        _wavePlayer = wasapi;
        _wavePlayer.Play();
        _positionTimer?.Start();

        StateChanged?.Invoke(PlaybackState.Playing);
        PositionChanged?.Invoke(0, TotalDuration);
    }

    // ---------------- 输出：独占 / 共享 ----------------

    /// <summary>WASAPI 独占模式：按文件的原采样率和位深直接驱动声卡（绕过 Windows 混音器），下一首开始生效。</summary>
    public bool ExclusiveMode { get; set; }

    /// <summary>绕过全部音效（均衡 / 空间 / 混响 / 软限幅），纯净直通。</summary>
    public bool BypassDsp
    {
        get => _bypassDsp;
        set { _bypassDsp = value; if (_dspProvider != null) _dspProvider.Bypass = value; }
    }
    private bool _bypassDsp;

    private OutputPlan? _outputPlan;
    private MMDevice? _outputDevice;

    private MMDevice? GetOutputDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(OutputDeviceId) && OutputDeviceId != "default")
            {
                try
                {
                    var d = enumerator.GetDevice(OutputDeviceId);
                    if (d.State == DeviceState.Active) return d;
                }
                catch { /* 设备已拔出 / 不可用：回退系统默认设备 */ }
            }
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch { return null; }
    }

    private void ReleaseOutputDevice()
    {
        try { _outputDevice?.Dispose(); } catch { }
        _outputDevice = null;
        _outputPlan = null;
    }

    private OutputPlan BuildOutput(DspSampleProvider chain, WaveFormat src)
    {
        ReleaseOutputDevice();
        var device = _outputDevice = GetOutputDevice();
        string? fallback = null;

        if (ExclusiveMode)
        {
            if (device == null) fallback = L.T("未找到输出设备");
            else
            {
                try
                {
                    var plan = TryExclusive(device, chain, src, out fallback);
                    if (plan != null) return plan;
                }
                catch (Exception ex) { fallback = ex.Message; }
            }
        }

        // 共享模式：Windows 混音器会把音频转成声卡当前的混音格式
        var wasapi = device != null
            ? new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMs)
            : new WasapiOut(AudioClientShareMode.Shared, LatencyMs);
        wasapi.Init(chain.ToWaveProvider());

        var shared = new OutputPlan { Player = wasapi, Mode = "Shared", DeviceName = device?.FriendlyName ?? L.T("系统默认"), FallbackReason = fallback };
        try
        {
            var mix = device?.AudioClient.MixFormat;
            if (mix != null) { shared.MixRate = mix.SampleRate; shared.MixBits = mix.BitsPerSample; shared.MixIsFloat = mix.Encoding == WaveFormatEncoding.IeeeFloat || (mix is WaveFormatExtensible wx && wx.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT); }
        }
        catch { }
        shared.Rate = src.SampleRate; shared.Bits = 32; shared.IsFloat = true; shared.Channels = src.Channels;
        return shared;
    }

    private OutputPlan? TryExclusive(MMDevice device, DspSampleProvider chain, WaveFormat src, out string? reason)
    {
        reason = null;
        int rate = src.SampleRate, ch = src.Channels, srcBits = src.BitsPerSample > 0 ? src.BitsPerSample : 16;

        // 候选位深：先试“刚好装得下源文件的”，再往大试（24 位数据也可以放进 32 位容器）
        int[] order = srcBits <= 16 ? new[] { 16, 24, 32 } : new[] { 24, 32, 16 };
        foreach (int bits in order)
        {
            if (bits < srcBits && srcBits > 16 && bits == 16 && order[0] != 16)
            {
                // 源是 24 位却只剩 16 位可选：宁可回退到共享模式，也不静默丢位深
                continue;
            }
            foreach (WaveFormat fmt in new WaveFormat[] { new WaveFormatExtensible(rate, bits, ch), new WaveFormat(rate, bits, ch) })
            {
                bool ok;
                try { ok = device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, fmt); }
                catch { ok = false; }
                if (!ok) continue;

                try
                {
                    var wasapi = new WasapiOut(device, AudioClientShareMode.Exclusive, true, Math.Max(LatencyMs, 40));
                    wasapi.Init(new FloatToPcmWaveProvider(chain, fmt));
                    return new OutputPlan { Player = wasapi, Mode = "Exclusive", DeviceName = device.FriendlyName, Rate = rate, Bits = bits, Channels = ch, IsFloat = false };
                }
                catch (Exception ex) { reason = ex.Message; }
            }
        }
        reason ??= L.F("声卡不支持 {0} Hz 的独占输出", rate);
        return null;
    }

    /// <summary>当前播放的信号路径（用于“信号路径”面板）。没有在播放时 Playing = false。</summary>
    public SignalPathInfo GetSignalPath()
    {
        var reader = _audioFileReader;
        var plan = _outputPlan;
        if (reader == null || plan == null)
            return new SignalPathInfo(false, "", "", "", "", "", "", false, Array.Empty<string>());

        var f = reader.WaveFormat;
        string chName = f.Channels switch { 1 => L.T("单声道"), 2 => L.T("立体声"), _ => L.F("{0} 声道", f.Channels) };
        bool srcFloat = f.Encoding == WaveFormatEncoding.IeeeFloat;
        string src = L.F("{0} Hz · {1} · {2}", f.SampleRate, srcFloat ? "32-bit float" : $"{f.BitsPerSample} bit", chName);

        string decode = L.F("Media Foundation 解码 → {0}（保持文件原采样率）", srcFloat ? "32-bit float" : $"{f.BitsPerSample}-bit PCM");

        bool stretched = Math.Abs(_playbackRate - 1.0) > 0.001;
        string rateStep = stretched ? L.F("变速 {0:0.0#}×（WSOLA，保持音高）", _playbackRate) : L.T("关（1.0×，直通）");

        var dspParts = new List<string>();
        if (!BypassDsp)
        {
            if (EqGains.Any(g => Math.Abs(g) > 0.01f)) dspParts.Add(L.T("均衡器"));
            if (SpatialAudioMode != SpatialMode.Off) dspParts.Add(L.T("空间音效"));
            if (Math.Abs(VocalBalance) > 0.02f) dspParts.Add(L.T("人声平衡"));
            if (VinylWarmth > 0.02f) dspParts.Add(L.T("黑胶质感"));
            if (HeadphoneProfileId != "bypass") dspParts.Add(L.T("耳机补偿"));
            dspParts.Add(L.T("混响")); dspParts.Add(L.T("软限幅"));
        }
        string dsp = BypassDsp ? L.T("已绕过（纯净直通）") : string.Join(" · ", dspParts);

        var vol = new List<string> { L.F("软件音量 {0}%", (int)Math.Round(_volume * 100)) };
        bool rg = ReplayGainEnabled && _trackGainDb.HasValue;
        if (rg) vol.Add(L.F("回放增益 {0:+0.0;-0.0} dB", _trackGainDb!.Value));
        string volume = string.Join(" · ", vol);

        string output;
        if (plan.Mode == "Exclusive")
            output = L.F("WASAPI 独占 · {0} · {1} Hz · {2} bit PCM", plan.DeviceName, plan.Rate, plan.Bits);
        else
            output = L.F("WASAPI 共享 · {0} · 混音器转为 {1} Hz / {2}", plan.DeviceName, plan.MixRate > 0 ? plan.MixRate : 0,
                plan.MixBits > 0 ? (plan.MixIsFloat ? $"{plan.MixBits}-bit float" : $"{plan.MixBits} bit") : "?");
        if (!string.IsNullOrEmpty(plan.FallbackReason)) output += "\n" + L.F("（独占失败，已改用共享：{0}）", plan.FallbackReason);

        // ---- 比特完美的条件 ----
        var reasons = new List<string>();
        if (plan.Mode != "Exclusive") reasons.Add(L.T("未使用 WASAPI 独占，音频会经过 Windows 混音器"));
        if (!BypassDsp) reasons.Add(L.T("音效未绕过"));
        if (stretched) reasons.Add(L.T("正在变速"));
        if (_volume < 0.999f) reasons.Add(L.F("软件音量为 {0}%（需为 100%）", (int)Math.Round(_volume * 100)));
        if (rg) reasons.Add(L.T("回放增益已开启"));
        if (plan.Mode == "Exclusive")
        {
            if (plan.Rate != f.SampleRate) reasons.Add(L.T("输出采样率与文件不同"));
            if (!srcFloat && plan.Bits < f.BitsPerSample) reasons.Add(L.T("输出位深低于文件位深"));
        }
        return new SignalPathInfo(true, src, decode, rateStep, dsp, volume, output, reasons.Count == 0, reasons);
    }

    private void DisposeCurrent()
    {
        ClearMix();
        ReleaseOutputDevice();
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
            _rampOn = false;
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
        if (_mixGainRamp)
        {
            // 混音进行中（界面在淡到一半时切到了下一首）：不直接换增益，只更新过渡的终点
            _mixGainTo = ComputeTrackGain();
            return;
        }
        if (_dspProvider != null) _dspProvider.TrackGain = ComputeTrackGain();
    }

    private float ComputeTrackGain() => GainFor(_trackGainDb, _trackPeak);

    private float GainFor(double? gainDb, double? trackPeak)
    {
        if (!ReplayGainEnabled || gainDb is not double db) return 1.0f;
        double gain = Math.Pow(10.0, db / 20.0);
        // 防削波：增益后的峰值不超过满刻度
        if (trackPeak is double peak && peak > 0) gain = Math.Min(gain, 1.0 / peak);
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
        ClearMix();
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
        ReleaseOutputDevice();
        StateChanged?.Invoke(PlaybackState.Stopped);
    }

    public void Seek(double seconds)
    {
        if (_xfade?.State == CrossfadeSampleProvider.MixState.Fading) return;   // 正在混音：这一小段不响应跳转
        CancelMix();
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
        ExclusiveMode = state.WasapiExclusive;
        _bypassDsp = state.BypassDsp;
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
        state.WasapiExclusive = ExclusiveMode;
        state.BypassDsp = _bypassDsp;
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
