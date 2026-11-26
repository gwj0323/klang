using System;
using System.Threading;

namespace Klang_WinUI.Services.Ai;

/// <summary>
/// 找一首歌的高潮（副歌）：
/// 1. 每 0.1 秒算一帧色度（12 个音级的能量），合成 0.5 秒一块；
/// 2. 块与块两两比较和声相似度，沿对角线找“隔了一段又原样出现”的长片段（副歌会重复两三遍）；
/// 3. 重复程度 + 响度（副歌通常最响）+ 避开前奏和结尾，选出得分最高的一段；
/// 4. 起点挪到附近响度突然变大的地方（一句开始的位置）。
/// </summary>
internal static class AiHighlight
{
    public const int SampleRate = 22050;
    private const int NFft = 4096;
    private const int Hop = SampleRate / 10;   // 0.1 秒一帧
    private const int FramesPerBlock = 5;      // 0.5 秒一块
    private const double BlockSec = 0.5;

    /// <summary>返回高潮的起止时间（秒）；歌太短或找不到明显重复时返回 null。</summary>
    public static (double Start, double End)? Detect(float[] x, double segLen, CancellationToken ct)
    {
        int frames = (x.Length - NFft) / Hop;
        if (frames < 600) return null;   // 不到 1 分钟

        // ---- 每帧：色度 + 响度 ----
        var win = AiDsp.Hann(NFft);
        var fft = new Fft(NFft);
        var re = new float[NFft]; var im = new float[NFft];
        var pc = new int[NFft / 2];
        for (int k = 0; k < pc.Length; k++)
        {
            double f = (double)k * SampleRate / NFft;
            pc[k] = f < 65 || f > 2100 ? -1 : ((int)Math.Round(12 * Math.Log2(f / 440.0)) % 12 + 12) % 12;
        }
        var chroma = new float[frames, 12];
        var rms = new float[frames];
        for (int fr = 0; fr < frames; fr++)
        {
            if ((fr & 127) == 0) ct.ThrowIfCancellationRequested();
            int off = fr * Hop;
            double e = 0;
            for (int i = 0; i < NFft; i++) { float s = x[off + i]; e += s * s; re[i] = s * win[i]; im[i] = 0; }
            rms[fr] = (float)Math.Sqrt(e / NFft);
            fft.Forward(re, im);
            for (int k = 1; k < pc.Length; k++)
            {
                if (pc[k] < 0) continue;
                chroma[fr, pc[k]] += MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
            }
        }

        // ---- 合成 0.5 秒的块 ----
        int n = frames / FramesPerBlock;
        var blk = new float[n][];
        var energy = new float[n];
        for (int b = 0; b < n; b++)
        {
            var v = new float[12];
            float en = 0;
            for (int f = b * FramesPerBlock; f < (b + 1) * FramesPerBlock; f++)
            {
                for (int c = 0; c < 12; c++) v[c] += chroma[f, c];
                en += rms[f];
            }
            AiDsp.Normalize(v);
            blk[b] = v;
            energy[b] = en / FramesPerBlock;
        }

        int w = (int)Math.Round(segLen / BlockSec);
        if (n < w * 2) return null;
        int minLag = (int)(10 / BlockSec);

        // ---- 沿对角线找重复：rep[i] = 从 i 开始的 w 块，和别处同样长的一段最像的平均相似度 ----
        var rep = new float[n];
        var diag = new float[n];
        for (int lag = minLag; lag <= n - w; lag++)
        {
            if ((lag & 31) == 0) ct.ThrowIfCancellationRequested();
            int len = n - lag;
            for (int i = 0; i < len; i++) diag[i] = AiDsp.Dot(blk[i], blk[i + lag]);
            double sum = 0;
            for (int i = 0; i < w; i++) sum += diag[i];
            for (int i = 0; i + w <= len; i++)
            {
                if (i > 0) sum += diag[i + w - 1] - diag[i - 1];
                float m = (float)(sum / w);
                if (m > rep[i]) rep[i] = m;
                if (m > rep[i + lag]) rep[i + lag] = m;
            }
        }

        // ---- 打分 ----
        int last = n - w;
        var segEnergy = new float[last + 1];
        {
            double s = 0;
            for (int i = 0; i < w; i++) s += energy[i];
            for (int i = 0; i <= last; i++)
            {
                if (i > 0) s += energy[i + w - 1] - energy[i - 1];
                segEnergy[i] = (float)(s / w);
            }
        }
        var (rm, rs) = Stats(rep, last + 1);
        var (em, es) = Stats(segEnergy, last + 1);
        if (rs < 1e-4) return null;

        int best = -1; double bestScore = double.MinValue;
        int introEnd = (int)(12 / BlockSec);          // 前 12 秒多半是前奏
        int outroStart = n - (int)(8 / BlockSec);      // 最后 8 秒多半在淡出
        for (int i = 0; i <= last; i++)
        {
            double score = (rep[i] - rm) / rs + 0.7 * (segEnergy[i] - em) / Math.Max(es, 1e-6);
            if (i < introEnd) score -= 1.5;
            if (i + w > outroStart) score -= 1.5;
            if (score > bestScore) { bestScore = score; best = i; }
        }
        if (best < 0 || rep[best] < 0.6f) return null;   // 没有明显的重复（纯即兴、氛围乐）

        // ---- 起点挪到附近响度突增的帧（±2 秒内）----
        int f0 = best * FramesPerBlock;
        int from = Math.Max(1, f0 - 20), to = Math.Min(frames - 1, f0 + 10);
        int onset = f0; float bestRise = 0;
        for (int f = from; f <= to; f++)
        {
            float rise = rms[f] - rms[f - 1];
            if (rise > bestRise) { bestRise = rise; onset = f; }
        }
        double start = Math.Max(0, onset * 0.1 - 0.15);
        double total = (double)x.Length / SampleRate;
        double end = Math.Min(total - 0.5, start + segLen);
        return end - start < 10 ? null : (start, end);
    }

    private static (float Mean, float Std) Stats(float[] v, int count)
    {
        double s = 0, s2 = 0;
        for (int i = 0; i < count; i++) { s += v[i]; s2 += v[i] * v[i]; }
        double m = s / count;
        return ((float)m, (float)Math.Sqrt(Math.Max(0, s2 / count - m * m)));
    }
}
