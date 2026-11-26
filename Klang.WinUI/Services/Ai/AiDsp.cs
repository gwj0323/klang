using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Klang_WinUI.Services.Ai;

/// <summary>
/// 复数 FFT：长度是 2 的幂时用基 2；3×2^k（人声分离的 6144 点）拆成 3 个 2^k 点；其他长度（Whisper 的 400 点）用 Bluestein。
/// 实例里缓存了旋转因子，同一长度反复使用；不是线程安全的，每个线程自己建。
/// </summary>
internal sealed class Fft
{
    public readonly int N;
    private readonly int _mode;            // 0 = 基 2，1 = 3×2^k，2 = Bluestein
    private readonly float[] _cos = Array.Empty<float>(), _sin = Array.Empty<float>();
    private readonly int[] _rev = Array.Empty<int>();
    private readonly Fft? _sub;            // 3×2^k / Bluestein 用到的 2 的幂 FFT
    private readonly float[] _wRe = Array.Empty<float>(), _wIm = Array.Empty<float>();   // 3×2^k 的旋转因子 / Bluestein 的 chirp
    private readonly float[] _bRe = Array.Empty<float>(), _bIm = Array.Empty<float>();   // Bluestein：chirp 共轭的频谱
    private readonly float[] _t0 = Array.Empty<float>(), _t1 = Array.Empty<float>(), _t2 = Array.Empty<float>(), _t3 = Array.Empty<float>(), _t4 = Array.Empty<float>(), _t5 = Array.Empty<float>();

    public Fft(int n)
    {
        N = n;
        if ((n & (n - 1)) == 0)
        {
            _mode = 0;
            int half = n / 2;
            _cos = new float[Math.Max(1, half)]; _sin = new float[Math.Max(1, half)];
            for (int i = 0; i < half; i++) { _cos[i] = (float)Math.Cos(-2 * Math.PI * i / n); _sin[i] = (float)Math.Sin(-2 * Math.PI * i / n); }
            _rev = new int[n];
            int bits = 0; while ((1 << bits) < n) bits++;
            for (int i = 0; i < n; i++)
            {
                int r = 0; for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                _rev[i] = r;
            }
        }
        else if (n % 3 == 0 && ((n / 3) & (n / 3 - 1)) == 0)
        {
            _mode = 1;
            int m = n / 3;
            _sub = new Fft(m);
            _wRe = new float[n]; _wIm = new float[n];
            for (int k = 0; k < n; k++) { _wRe[k] = (float)Math.Cos(-2 * Math.PI * k / n); _wIm[k] = (float)Math.Sin(-2 * Math.PI * k / n); }
            _t0 = new float[m]; _t1 = new float[m]; _t2 = new float[m]; _t3 = new float[m]; _t4 = new float[m]; _t5 = new float[m];
        }
        else
        {
            _mode = 2;
            int m = 1; while (m < 2 * n - 1) m <<= 1;
            _sub = new Fft(m);
            _wRe = new float[n]; _wIm = new float[n];
            for (int k = 0; k < n; k++)
            {
                double a = Math.PI * ((long)k * k % (2L * n)) / n;
                _wRe[k] = (float)Math.Cos(a); _wIm[k] = (float)-Math.Sin(a);
            }
            _bRe = new float[m]; _bIm = new float[m];
            _bRe[0] = 1;
            for (int k = 1; k < n; k++) { _bRe[k] = _bRe[m - k] = _wRe[k]; _bIm[k] = _bIm[m - k] = -_wIm[k]; }
            _sub.Forward(_bRe, _bIm);
            _t0 = new float[m]; _t1 = new float[m];
        }
    }

    /// <summary>原地正变换（e^{-i}）。</summary>
    public void Forward(float[] re, float[] im)
    {
        switch (_mode)
        {
            case 0: Radix2(re, im); break;
            case 1: Radix3(re, im); break;
            default: Bluestein(re, im); break;
        }
    }

    /// <summary>原地逆变换（含 1/N）。</summary>
    public void Inverse(float[] re, float[] im)
    {
        for (int i = 0; i < N; i++) im[i] = -im[i];
        Forward(re, im);
        float s = 1f / N;
        for (int i = 0; i < N; i++) { re[i] *= s; im[i] = -im[i] * s; }
    }

    private void Radix2(float[] re, float[] im)
    {
        int n = N;
        for (int i = 0; i < n; i++)
        {
            int j = _rev[i];
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1, step = n / len;
            for (int i = 0; i < n; i += len)
            {
                for (int k = 0, t = 0; k < half; k++, t += step)
                {
                    int a = i + k, b = a + half;
                    float wr = _cos[t], wi = _sin[t];
                    float xr = re[b] * wr - im[b] * wi, xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                }
            }
        }
    }

    private void Radix3(float[] re, float[] im)
    {
        int m = N / 3;
        for (int j = 0; j < m; j++)
        {
            _t0[j] = re[3 * j]; _t1[j] = im[3 * j];
            _t2[j] = re[3 * j + 1]; _t3[j] = im[3 * j + 1];
            _t4[j] = re[3 * j + 2]; _t5[j] = im[3 * j + 2];
        }
        _sub!.Forward(_t0, _t1); _sub.Forward(_t2, _t3); _sub.Forward(_t4, _t5);
        for (int k = 0; k < N; k++)
        {
            int q = k % m;
            int k2 = (2 * k) % N;
            float ar = _t2[q] * _wRe[k] - _t3[q] * _wIm[k], ai = _t2[q] * _wIm[k] + _t3[q] * _wRe[k];
            float br = _t4[q] * _wRe[k2] - _t5[q] * _wIm[k2], bi = _t4[q] * _wIm[k2] + _t5[q] * _wRe[k2];
            re[k] = _t0[q] + ar + br;
            im[k] = _t1[q] + ai + bi;
        }
    }

    private void Bluestein(float[] re, float[] im)
    {
        int m = _t0.Length;
        Array.Clear(_t0); Array.Clear(_t1);
        for (int k = 0; k < N; k++)
        {
            _t0[k] = re[k] * _wRe[k] - im[k] * _wIm[k];
            _t1[k] = re[k] * _wIm[k] + im[k] * _wRe[k];
        }
        _sub!.Forward(_t0, _t1);
        for (int k = 0; k < m; k++)
        {
            float r = _t0[k] * _bRe[k] - _t1[k] * _bIm[k];
            float i = _t0[k] * _bIm[k] + _t1[k] * _bRe[k];
            _t0[k] = r; _t1[k] = i;
        }
        _sub.Inverse(_t0, _t1);
        for (int k = 0; k < N; k++)
        {
            re[k] = _t0[k] * _wRe[k] - _t1[k] * _wIm[k];
            im[k] = _t0[k] * _wIm[k] + _t1[k] * _wRe[k];
        }
    }
}

/// <summary>模型前处理用到的信号处理：窗函数、梅尔滤波器组、对数梅尔频谱、音频解码。</summary>
internal static class AiDsp
{
    /// <summary>周期 Hann 窗（和 torch.hann_window / transformers 的默认一致）。</summary>
    public static float[] Hann(int n)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
        return w;
    }

    private static double HzToMel(double f)
    {
        const double fSp = 200.0 / 3, minLogHz = 1000, minLogMel = minLogHz / fSp;
        double logStep = Math.Log(6.4) / 27.0;
        return f >= minLogHz ? minLogMel + Math.Log(f / minLogHz) / logStep : f / fSp;
    }

    private static double MelToHz(double m)
    {
        const double fSp = 200.0 / 3, minLogHz = 1000, minLogMel = minLogHz / fSp;
        double logStep = Math.Log(6.4) / 27.0;
        return m >= minLogMel ? minLogHz * Math.Exp(logStep * (m - minLogMel)) : fSp * m;
    }

    /// <summary>Slaney 刻度 + Slaney 归一化的梅尔滤波器组（librosa 默认），形状 [nMels][nFft/2+1]。</summary>
    public static float[][] MelFilters(int sr, int nFft, int nMels, double fMin, double fMax)
    {
        int bins = nFft / 2 + 1;
        var freqs = new double[bins];
        for (int i = 0; i < bins; i++) freqs[i] = (double)sr / 2 * i / (bins - 1);
        double mMin = HzToMel(fMin), mMax = HzToMel(fMax);
        var melF = new double[nMels + 2];
        for (int i = 0; i < nMels + 2; i++) melF[i] = MelToHz(mMin + (mMax - mMin) * i / (nMels + 1));
        var w = new float[nMels][];
        for (int m = 0; m < nMels; m++)
        {
            w[m] = new float[bins];
            double lowDiff = melF[m + 1] - melF[m], highDiff = melF[m + 2] - melF[m + 1];
            double enorm = 2.0 / (melF[m + 2] - melF[m]);
            for (int k = 0; k < bins; k++)
            {
                double lower = (freqs[k] - melF[m]) / lowDiff;
                double upper = (melF[m + 2] - freqs[k]) / highDiff;
                w[m][k] = (float)(Math.Max(0, Math.Min(lower, upper)) * enorm);
            }
        }
        return w;
    }

    /// <summary>
    /// 功率梅尔频谱（center=True、反射填充），返回 [帧][nMels]。
    /// </summary>
    public static float[][] MelPower(float[] x, int nFft, int hop, float[][] filters, int maxFrames = int.MaxValue)
    {
        int pad = nFft / 2;
        int frames = Math.Min(maxFrames, 1 + x.Length / hop);
        var fft = new Fft(nFft);
        var win = Hann(nFft);
        var re = new float[nFft]; var im = new float[nFft];
        int bins = nFft / 2 + 1, nMels = filters.Length;
        var power = new float[bins];
        var outp = new float[frames][];
        // 每个滤波器只在一小段频点上非零：记下范围，省掉大部分乘法
        var lo = new int[nMels]; var hi = new int[nMels];
        for (int m = 0; m < nMels; m++)
        {
            lo[m] = 0; hi[m] = -1;
            for (int k = 0; k < bins; k++) if (filters[m][k] > 0) { if (hi[m] < 0) lo[m] = k; hi[m] = k; }
        }
        for (int f = 0; f < frames; f++)
        {
            int start = f * hop - pad;
            for (int i = 0; i < nFft; i++)
            {
                int j = start + i;
                // 反射填充（不重复边界样本）
                if (j < 0) j = -j;
                if (j >= x.Length) j = 2 * x.Length - 2 - j;
                j = Math.Clamp(j, 0, x.Length - 1);
                re[i] = x[j] * win[i];
                im[i] = 0;
            }
            fft.Forward(re, im);
            for (int k = 0; k < bins; k++) power[k] = re[k] * re[k] + im[k] * im[k];
            var row = new float[nMels];
            for (int m = 0; m < nMels; m++)
            {
                double s = 0;
                var w = filters[m];
                for (int k = lo[m]; k <= hi[m]; k++) s += w[k] * power[k];
                row[m] = (float)s;
            }
            outp[f] = row;
        }
        return outp;
    }

    /// <summary>
    /// 解码一段音频为单声道 float，重采样到 sampleRate。offset / length 单位秒（length ≤ 0 = 到结尾）。
    /// </summary>
    public static float[] DecodeMono(string path, int sampleRate, double offset, double length)
    {
        var st = DecodeInterleaved(path, sampleRate, offset, length, out int ch);
        if (ch == 1) return st;
        int n = st.Length / ch;
        var mono = new float[n];
        for (int i = 0; i < n; i++)
        {
            float s = 0;
            for (int c = 0; c < ch; c++) s += st[i * ch + c];
            mono[i] = s / ch;
        }
        return mono;
    }

    /// <summary>解码为立体声（单声道复制成两路，多声道取前两路），返回左右两个数组。</summary>
    public static (float[] L, float[] R) DecodeStereo(string path, int sampleRate, double offset, double length)
    {
        var st = DecodeInterleaved(path, sampleRate, offset, length, out int ch);
        int n = st.Length / ch;
        var l = new float[n]; var r = new float[n];
        for (int i = 0; i < n; i++)
        {
            l[i] = st[i * ch];
            r[i] = ch > 1 ? st[i * ch + 1] : st[i * ch];
        }
        return (l, r);
    }

    private static float[] DecodeInterleaved(string path, int sampleRate, double offset, double length, out int channels)
    {
        using var reader = new MediaFoundationReader(path);
        if (offset > 0) reader.CurrentTime = TimeSpan.FromSeconds(offset);
        ISampleProvider sp = reader.ToSampleProvider();
        channels = sp.WaveFormat.Channels;
        if (sp.WaveFormat.SampleRate != sampleRate) sp = new WdlResamplingSampleProvider(sp, sampleRate);
        long want = length > 0 ? (long)(length * sampleRate) * channels : long.MaxValue;
        var list = new List<float[]>();
        long total = 0;
        var buf = new float[sampleRate * channels];
        while (total < want)
        {
            int ask = (int)Math.Min(buf.Length, want - total);
            ask -= ask % channels;
            if (ask <= 0) break;
            int n = sp.Read(buf.AsSpan(0, ask));
            if (n <= 0) break;
            var chunk = new float[n];
            Array.Copy(buf, chunk, n);
            list.Add(chunk);
            total += n;
        }
        var all = new float[total];
        long p = 0;
        foreach (var c in list) { Array.Copy(c, 0, all, p, c.Length); p += c.Length; }
        return all;
    }

    public static void Normalize(float[] v)
    {
        double s = 0;
        foreach (var x in v) s += x * x;
        float inv = s > 0 ? (float)(1 / Math.Sqrt(s)) : 0;
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    public static float Dot(float[] a, float[] b)
    {
        float s = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }
}
