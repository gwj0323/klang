using System;
using NAudio.Wave;

namespace Klang_WinUI.Services.Dsp;

/// <summary>
/// NAudio 流水线实时 DSP 处理提供者
/// 无缝串联 5段EQ、空间音频、人声平衡、黑胶模拟，输出端带连续软限幅与淡入淡出（防爆音）
/// </summary>
public class DspSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    public NordicDspProcessor Processor { get; }

    public WaveFormat WaveFormat => _source.WaveFormat;

    private float _volume = 0.85f;
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>ReplayGain 等逐曲增益（线性倍数），与音量相乘。</summary>
    public float TrackGain { get; set; } = 1.0f;

    // 淡入淡出增益：每个采样帧向目标值线性靠拢，避免起播 / 跳转 / 停止时的电平突变（爆音）
    private float _fadeGain = 0f;
    private float _fadeTarget = 1f;
    private float _fadeStep = 0f;

    public DspSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Processor = new NordicDspProcessor(_source.WaveFormat.SampleRate);
        RequestFade(1f, 25); // 起播淡入
    }

    /// <summary>在 ms 毫秒内把输出增益平滑过渡到 target（0~1）。</summary>
    public void RequestFade(float target, double ms)
    {
        double frames = Math.Max(1, ms * WaveFormat.SampleRate / 1000.0);
        _fadeTarget = target;
        _fadeStep = (float)((target - _fadeGain) / frames);
    }

    /// <summary>增益归零后重新淡入（跳转播放位置后调用）。</summary>
    public void RestartFadeIn(double ms = 25)
    {
        _fadeGain = 0f;
        RequestFade(1f, ms);
    }

    public int Read(Span<float> buffer)
    {
        int samplesRead = _source.Read(buffer);
        if (samplesRead <= 0) return samplesRead;

        ProcessBlock(buffer.Slice(0, samplesRead));
        return samplesRead;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// 连续的软限幅：|x| 不超过拐点时原样通过，超过后用 tanh 平滑压向 ±1，
    /// 拐点处值与斜率都连续（原先在 0.92 处直接换成 tanh，电平会突降约 0.2，响亮段落会出现爆裂声）。
    /// </summary>
    private static float SoftClip(float x)
    {
        const float knee = 0.85f;
        float a = Math.Abs(x);
        if (a <= knee) return x;
        float y = knee + (1f - knee) * (float)Math.Tanh((a - knee) / (1f - knee));
        return x < 0 ? -y : y;
    }

    private float NextFade()
    {
        if (_fadeGain != _fadeTarget)
        {
            _fadeGain += _fadeStep;
            if ((_fadeStep > 0 && _fadeGain >= _fadeTarget) || (_fadeStep < 0 && _fadeGain <= _fadeTarget) || _fadeStep == 0)
            {
                _fadeGain = _fadeTarget;
            }
        }
        return _fadeGain;
    }

    /// <summary>
    /// 绕过全部音效（均衡 / 空间 / 混响 / 软限幅）：采样原样通过，只剩音量、回放增益和起播淡入。
    /// 音量 = 1、没有回放增益、淡入结束之后，输出和输入逐位相同（比特完美）。
    /// </summary>
    public bool Bypass { get; set; }

    private float Out(float x) => Bypass ? Math.Clamp(x, -1f, 1f) : SoftClip(x);

    private void ProcessBlock(Span<float> span)
    {
        int channels = WaveFormat.Channels;
        float vol = _volume * TrackGain;

        if (channels == 2)
        {
            for (int i = 0; i < span.Length; i += 2)
            {
                float left = span[i];
                float right = span[i + 1];

                // 核心专业 DSP 声学链（绕过时只给频谱动画喂数据）
                if (Bypass) Processor.PushForSpectrum(left, right);
                else Processor.ProcessStereo(ref left, ref right);

                float g = vol * NextFade();
                if (Bypass && g == 1f) { span[i] = left; span[i + 1] = right; }
                else { span[i] = Out(left * g); span[i + 1] = Out(right * g); }
            }
        }
        else if (channels == 1)
        {
            for (int i = 0; i < span.Length; i++)
            {
                float mono = span[i];
                float r = mono;
                if (Bypass) Processor.PushForSpectrum(mono, mono);
                else
                {
                    Processor.ProcessStereo(ref mono, ref r);
                    mono = (mono + r) * 0.5f;
                }

                float g = vol * NextFade();
                span[i] = Bypass && g == 1f ? mono : Out(mono * g);
            }
        }
        else
        {
            // 多声道（5.1 / 7.1 等）：不做立体声 DSP，但音量、回放增益与淡入淡出依然要生效
            for (int i = 0; i < span.Length; i += channels)
            {
                float g = vol * NextFade();
                for (int c = 0; c < channels && i + c < span.Length; c++)
                {
                    span[i + c] = Bypass && g == 1f ? span[i + c] : Out(span[i + c] * g);
                }
            }
        }
    }
}
