using System;

namespace Klang_WinUI.Services.Dsp;

/// <summary>
/// 实时频谱分析（对应 Qt 版 _compute_spectrum_24）：取最近 1024 个单声道采样，Hann 加窗后做 FFT，
/// 按对数频率划分 24 个频段，加高频倾斜补偿并做幂次压缩，输出 0~1。
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int Bands = 24;
    private const int N = 1024;

    private readonly float[] _ring = new float[N];
    private int _write;                      // 下一个写入位置（只由音频线程写）
    private readonly double[] _window = new double[N];
    private readonly double[] _re = new double[N];
    private readonly double[] _im = new double[N];
    private readonly double[] _cos = new double[N / 2];
    private readonly double[] _sin = new double[N / 2];
    private readonly int[] _bitrev = new int[N];

    public SpectrumAnalyzer()
    {
        for (int i = 0; i < N; i++)
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
        for (int i = 0; i < N / 2; i++)
        {
            _cos[i] = Math.Cos(2 * Math.PI * i / N);
            _sin[i] = -Math.Sin(2 * Math.PI * i / N);
        }
        int bits = (int)Math.Log2(N);
        for (int i = 0; i < N; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _bitrev[i] = r;
        }
    }

    /// <summary>音频线程调用：写入一个单声道采样。</summary>
    public void Push(float mono)
    {
        int w = _write;
        _ring[w] = mono;
        _write = (w + 1) & (N - 1);
    }

    public void Clear()
    {
        Array.Clear(_ring);
    }

    /// <summary>UI 线程调用：对最近的 N 个采样做 FFT，得到 24 个频段能量。</summary>
    public float[] Compute(double sampleRate)
    {
        int start = _write;
        for (int i = 0; i < N; i++)
        {
            _re[_bitrev[i]] = _ring[(start + i) & (N - 1)] * _window[i];
            _im[_bitrev[i]] = 0;
        }

        // 基 2 蝶形
        for (int size = 2; size <= N; size <<= 1)
        {
            int half = size >> 1, step = N / size;
            for (int i = 0; i < N; i += size)
            {
                for (int j = 0, k = 0; j < half; j++, k += step)
                {
                    int a = i + j, b = a + half;
                    double tr = _re[b] * _cos[k] - _im[b] * _sin[k];
                    double ti = _re[b] * _sin[k] + _im[b] * _cos[k];
                    _re[b] = _re[a] - tr; _im[b] = _im[a] - ti;
                    _re[a] += tr; _im[a] += ti;
                }
            }
        }

        double binHz = sampleRate / N;
        double fLo = 45.0, fHi = Math.Min(16000.0, sampleRate * 0.45);
        var result = new float[Bands];
        for (int band = 0; band < Bands; band++)
        {
            double f0 = fLo * Math.Pow(fHi / fLo, band / (double)Bands);
            double f1 = fLo * Math.Pow(fHi / fLo, (band + 1) / (double)Bands);
            int lo = Math.Max(1, (int)(f0 / binHz));
            int hi = Math.Min(N / 2, Math.Max(lo + 1, (int)(f1 / binHz)));
            double sum = 0;
            for (int k = lo; k < hi; k++)
                sum += Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) / (N * 0.25);
            double val = sum / (hi - lo);
            double tilt = 1.0 + band / (double)Bands * 2.4;
            result[band] = (float)Math.Min(1.0, Math.Pow(Math.Max(0, val * tilt * 6.5), 0.62));
        }
        return result;
    }
}
