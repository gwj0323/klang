using System;
using NAudio.Wave;

namespace Klang_WinUI.Services.Dsp;

/// <summary>
/// 在“当前这首”的采样流上，按计划在某个精确的采样点开始，把“下一首”等功率地交叉淡入。
/// 计划用“还剩多少个采样开始”来表示（按流里的采样数倒计时，所以起点是采样级精确的，不受界面线程的抖动影响）。
/// 淡完之后，下一首就成为主流，后续的读取直接走它。
/// </summary>
public sealed class CrossfadeSampleProvider : ISampleProvider
{
    public enum MixState { None, Scheduled, Fading }

    private readonly object _lock = new();
    private ISampleProvider _primary;
    private ISampleProvider? _next;
    private long _framesUntilStart;
    private long _fadeFrames, _fadePos;
    private MixState _state = MixState.None;
    private float[] _bufA = new float[8192], _bufB = new float[8192];

    public WaveFormat WaveFormat { get; }

    public CrossfadeSampleProvider(ISampleProvider primary)
    {
        _primary = primary;
        WaveFormat = primary.WaveFormat;
    }

    public MixState State { get { lock (_lock) return _state; } }

    /// <summary>淡入淡出的进度 0~1（只在 Fading 时有意义）。</summary>
    public double Progress { get { lock (_lock) return _fadeFrames > 0 ? Math.Clamp((double)_fadePos / _fadeFrames, 0, 1) : 0; } }

    /// <summary>安排一次混音：再过 framesUntilStart 帧开始，持续 fadeFrames 帧。</summary>
    public bool Schedule(ISampleProvider next, long framesUntilStart, long fadeFrames)
    {
        if (next.WaveFormat.SampleRate != WaveFormat.SampleRate || next.WaveFormat.Channels != WaveFormat.Channels) return false;
        lock (_lock)
        {
            if (_state != MixState.None) return false;
            _next = next;
            _framesUntilStart = Math.Max(0, framesUntilStart);
            _fadeFrames = Math.Max(1, fadeFrames);
            _fadePos = 0;
            _state = MixState.Scheduled;
            return true;
        }
    }

    /// <summary>取消还没开始的计划。返回 true 表示取消成功（已经开始淡入淡出就取消不了）。</summary>
    public bool CancelScheduled()
    {
        lock (_lock)
        {
            if (_state != MixState.Scheduled) return false;
            _next = null; _state = MixState.None;
            return true;
        }
    }

    public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public int Read(Span<float> buffer)
    {
        int ch = WaveFormat.Channels;
        int total = 0;
        while (total < buffer.Length)
        {
            MixState st; long until; ISampleProvider prim, next;
            lock (_lock) { st = _state; until = _framesUntilStart; prim = _primary; next = _next!; }

            int want = buffer.Length - total;
            if (st == MixState.None)
            {
                int n = prim.Read(buffer.Slice(total, want));
                return total + n;
            }

            if (st == MixState.Scheduled)
            {
                // 还没到起点：先照常读，最多读到起点那一帧
                int frames = (int)Math.Min(want / ch, until);
                if (frames > 0)
                {
                    int n = prim.Read(buffer.Slice(total, frames * ch));
                    total += n;
                    lock (_lock) { _framesUntilStart = Math.Max(0, _framesUntilStart - n / ch); }
                    if (n < frames * ch) return total;           // 当前这首提前结束了
                }
                lock (_lock) { if (_framesUntilStart <= 0) _state = MixState.Fading; }
                continue;
            }

            // Fading：两路一起读，按等功率曲线混合
            int fFrames = (int)Math.Min(want / ch, Math.Max(1, _fadeFrames - _fadePos));
            int samples = fFrames * ch;
            if (_bufA.Length < samples) { _bufA = new float[samples]; _bufB = new float[samples]; }
            int ra = prim.Read(_bufA.AsSpan(0, samples));
            int rb = next.Read(_bufB.AsSpan(0, samples));
            if (ra < samples) Array.Clear(_bufA, ra, samples - ra);
            if (rb < samples) Array.Clear(_bufB, rb, samples - rb);

            long pos0 = _fadePos;
            for (int f = 0; f < fFrames; f++)
            {
                double t = (double)(pos0 + f) / _fadeFrames;
                float gOut = (float)Math.Cos(t * Math.PI / 2), gIn = (float)Math.Sin(t * Math.PI / 2);
                for (int c = 0; c < ch; c++)
                {
                    int i = f * ch + c;
                    buffer[total + i] = _bufA[i] * gOut + _bufB[i] * gIn;
                }
            }
            total += samples;

            lock (_lock)
            {
                _fadePos += fFrames;
                if (_fadePos >= _fadeFrames)
                {
                    _primary = _next!;       // 淡完了：下一首成为主流
                    _next = null;
                    _state = MixState.None;
                }
            }
            if (ra == 0 && rb == 0) return total;
        }
        return total;
    }
}
