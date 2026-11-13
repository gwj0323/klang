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
        InitLowpass();
    }

    public MixState State { get { lock (_lock) return _state; } }

    /// <summary>淡入淡出的进度 0~1（只在 Fading 时有意义）。</summary>
    public double Progress { get { lock (_lock) return _fadeFrames > 0 ? Math.Clamp((double)_fadePos / _fadeFrames, 0, 1) : 0; } }

    /// <summary>安排一次混音：再过 framesUntilStart 帧开始，持续 fadeFrames 帧。</summary>
    public bool Schedule(ISampleProvider next, long framesUntilStart, long fadeFrames, bool bassSwap = false)
    {
        if (next.WaveFormat.SampleRate != WaveFormat.SampleRate || next.WaveFormat.Channels != WaveFormat.Channels) return false;
        lock (_lock)
        {
            if (_state != MixState.None) return false;
            _next = next;
            _framesUntilStart = Math.Max(0, framesUntilStart);
            _fadeFrames = Math.Max(1, fadeFrames);
            _fadePos = 0;
            _bassSwap = bassSwap;
            Array.Clear(_lpA); Array.Clear(_lpB); Array.Clear(_lpA2); Array.Clear(_lpB2);
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

    // ---- 低频交接 ----
    // 两首歌的鼓和贝斯同时响会发浑。开启后，淡入淡出期间把每一路拆成低频（约 200 Hz 以下）和其余部分：
    // 其余部分照常等功率交叉；低频则在中段很短的一段里从旧歌“交给”新歌 —— 任何时刻基本只有一首歌的低音。
    // 高频 = 原信号 − 低频，所以两部分加起来严格等于原信号，淡入淡出的两端和不开启时完全一样。
    private bool _bassSwap;
    private readonly double[] _lpA = new double[8], _lpB = new double[8];   // 每声道 4 个状态（x1 x2 y1 y2）
    // 第二级：“原信号 − 低频”里还残留一部分低频（滤波器有相位差，减不干净），
    // 对这部分再滤一次并按低频增益扣掉，交接前后另一首歌的低音才真正被压下去
    private readonly double[] _lpA2 = new double[8], _lpB2 = new double[8];
    private double _b0, _b1, _b2, _a1, _a2;

    private void InitLowpass()
    {
        double w0 = 2 * Math.PI * 200.0 / WaveFormat.SampleRate;
        double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));   // Q = 0.707（巴特沃斯）
        double a0 = 1 + alpha;
        _b0 = (1 - cos) / 2 / a0; _b1 = (1 - cos) / a0; _b2 = _b0;
        _a1 = -2 * cos / a0; _a2 = (1 - alpha) / a0;
    }

    private double Lowpass(double[] st, int c, double x)
    {
        int o = c * 4;
        double y = _b0 * x + _b1 * st[o] + _b2 * st[o + 1] - _a1 * st[o + 2] - _a2 * st[o + 3];
        st[o + 1] = st[o]; st[o] = x;
        st[o + 3] = st[o + 2]; st[o + 2] = y;
        return y;
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
                if (!_bassSwap || ch > 2)
                {
                    for (int c = 0; c < ch; c++)
                    {
                        int i = f * ch + c;
                        buffer[total + i] = _bufA[i] * gOut + _bufB[i] * gIn;
                    }
                }
                else
                {
                    // 低频在 35% ~ 65% 这一段里完成交接
                    double u = Math.Clamp((t - 0.35) / 0.30, 0, 1);
                    u = u * u * (3 - 2 * u);
                    float bOut = (float)Math.Cos(u * Math.PI / 2), bIn = (float)Math.Sin(u * Math.PI / 2);
                    for (int c = 0; c < ch; c++)
                    {
                        int i = f * ch + c;
                        float a = _bufA[i], b = _bufB[i];
                        float lowA = (float)Lowpass(_lpA, c, a), lowB = (float)Lowpass(_lpB, c, b);
                        float resA = a - lowA, resB = b - lowB;
                        float highA = resA - (1f - bOut) * (float)Lowpass(_lpA2, c, resA);
                        float highB = resB - (1f - bIn) * (float)Lowpass(_lpB2, c, resB);
                        // 低频增益为 1 时 high + low 严格等于原信号，所以淡入淡出的两端和不开启时逐位相同
                        buffer[total + i] = highA * gOut + highB * gIn + lowA * bOut + lowB * bIn;
                    }
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
