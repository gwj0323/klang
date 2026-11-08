using System;
using NAudio.Wave;

namespace Klang_WinUI.Services.Dsp;

/// <summary>
/// NAudio 流水线实时 DSP 处理提供者
/// 无缝串联 5段EQ、空间音频、人声平衡、黑胶模拟与软限幅防爆音
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

    public DspSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Processor = new NordicDspProcessor(_source.WaveFormat.SampleRate);
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

    private void ProcessBlock(Span<float> span)
    {
        int channels = WaveFormat.Channels;
        float vol = _volume;

        if (channels == 2)
        {
            for (int i = 0; i < span.Length; i += 2)
            {
                float left = span[i];
                float right = span[i + 1];

                // 核心专业 DSP 声学链
                Processor.ProcessStereo(ref left, ref right);

                // 音量线性增益
                left *= vol;
                right *= vol;

                // 软饱和抗数字削波限幅器 (Soft Clipper)
                if (Math.Abs(left) > 0.92f)
                {
                    left = (float)Math.Tanh(left);
                }
                if (Math.Abs(right) > 0.92f)
                {
                    right = (float)Math.Tanh(right);
                }

                span[i] = left;
                span[i + 1] = right;
            }
        }
        else if (channels == 1)
        {
            for (int i = 0; i < span.Length; i++)
            {
                float mono = span[i];
                float r = mono;
                Processor.ProcessStereo(ref mono, ref r);

                mono = (mono + r) * 0.5f * vol;
                if (Math.Abs(mono) > 0.92f)
                {
                    mono = (float)Math.Tanh(mono);
                }
                span[i] = mono;
            }
        }
    }
}
