using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klang_WinUI.Models;
using NAudio.Wave;

namespace Klang_WinUI.Services;

/// <summary>一首歌的节奏与调性分析结果。Bpm = 0 表示没有稳定的节拍（古典、氛围、现场录音等）。</summary>
public sealed record RhythmResult(
    double Bpm,
    double FirstBeat,     // 开头附近一个拍点的时间（秒，0 ~ 一拍之内）
    double LastBeat,      // 结尾附近的一个拍点（秒）
    double MixEnd,        // 音乐真正结束的位置（去掉末尾的静音）
    string Key,           // 例如 Am、F#
    string Camelot,       // 例如 8A（调性相容的歌，编号相同或相差 1）
    float Confidence);

/// <summary>
/// 后台分析节拍（BPM + 拍点位置）和调性，给“节拍对齐的自动混音”用。
/// 节拍：把音频降到 ~11 kHz，算频谱通量得到“起音强度”曲线，用自相关找速度，再在开头和结尾各找一次拍点的相位；
/// 调性：累计 12 个音级的能量，和大调 / 小调的标准音级分布做相关。
/// 全是启发式，没有稳定节拍的歌会得到 Bpm = 0，混音时就不会硬凑。
/// </summary>
public sealed class RhythmAnalysisService
{
    private static readonly Lazy<RhythmAnalysisService> _instance = new(() => new RhythmAnalysisService());
    public static RhythmAnalysisService Instance => _instance.Value;

    private readonly ConcurrentQueue<TrackItem> _queue = new();
    private readonly HashSet<string> _queued = new();
    private int _running;
    private Microsoft.UI.Dispatching.DispatcherQueue? _ui;

    public event Action<TrackItem>? Analyzed;

    public void Start(Microsoft.UI.Dispatching.DispatcherQueue ui) => _ui = ui;

    public void Enqueue(IEnumerable<TrackItem> tracks)
    {
        foreach (var t in tracks)
        {
            if (t.RhythmDone || t.IsOnline || t.IsCueTrack || t.IsMissing) continue;
            lock (_queued) { if (!_queued.Add(t.Id)) continue; }
            _queue.Enqueue(t);
        }
        Kick();
    }

    /// <summary>把分析结果写到曲目上（null 表示分析失败，也标记为“已分析”以免反复重试）。</summary>
    public static void Apply(TrackItem t, RhythmResult? r)
    {
        t.RhythmDone = true;
        if (r == null) return;
        t.Bpm = r.Bpm; t.FirstBeat = r.FirstBeat; t.LastBeat = r.LastBeat; t.MixEnd = r.MixEnd;
        t.MusicKey = r.Key; t.Camelot = r.Camelot;
    }

    private void Kick()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                int sinceSave = 0;
                while (_queue.TryDequeue(out var t))
                {
                    try
                    {
                        RhythmResult? r = null;
                        try { r = Analyze(t.FilePath, t.Duration); } catch { }
                        _ui?.TryEnqueue(() =>
                        {
                            Apply(t, r);
                            Analyzed?.Invoke(t);
                        });
                        if (++sinceSave >= 8) { sinceSave = 0; _ui?.TryEnqueue(() => LibraryService.Instance.SaveLibrary()); }
                    }
                    catch { }
                    await Task.Delay(80);
                }
                if (sinceSave > 0) _ui?.TryEnqueue(() => LibraryService.Instance.SaveLibrary());
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                if (!_queue.IsEmpty) Kick();
            }
        });
    }

    // ---------------------------------------------------------------------------------

    private const int W = 1024;       // 起音检测的帧长
    private const int HOP = 128;
    private const int KW = 4096;      // 调性检测的帧长

    /// <summary>实测起音曲线的峰值比真实拍点晚多少个采样（用合成节拍校准过，见测试）。</summary>
    private const double PeakBiasSamples = -232;

    public static RhythmResult? Analyze(string path, double durationSeconds)
    {
        if (!File.Exists(path) || durationSeconds < 20 || durationSeconds > 15 * 60) return null;

        // ---- 解码成 ~11 kHz 的单声道 ----
        float[] mono; double rate;
        using (var reader = new MediaFoundationReader(path))
        {
            var sp = reader.ToSampleProvider();
            int sr = sp.WaveFormat.SampleRate, ch = sp.WaveFormat.Channels;
            int factor = Math.Max(1, (int)Math.Round(sr / 11025.0));
            rate = (double)sr / factor;
            var list = new List<float>((int)(durationSeconds * rate) + 1024);
            var tmp = new float[4096 * ch];
            double acc = 0; int cnt = 0;
            long maxFrames = (long)((durationSeconds + 5) * sr);
            long frames = 0;
            while (frames < maxFrames)
            {
                int n = sp.Read(tmp.AsSpan());
                if (n <= 0) break;
                for (int i = 0; i + ch <= n; i += ch)
                {
                    float m = 0; for (int c = 0; c < ch; c++) m += tmp[i + c];
                    acc += m / ch;
                    if (++cnt == factor) { list.Add((float)(acc / factor)); acc = 0; cnt = 0; }
                }
                frames += n / ch;
            }
            mono = list.ToArray();
        }
        if (mono.Length < rate * 15) return null;
        double realDuration = mono.Length / rate;

        // ---- 音乐真正结束的位置：末尾连续安静的部分不算 ----
        double mixEnd = ActiveEnd(mono, rate);

        // ---- 起音强度曲线 ----
        var env = OnsetEnvelope(mono, rate, out double fps);
        if (env.Length < fps * 12) return null;

        // ---- 速度 ----
        var (bpm, conf) = EstimateTempo(env, fps);
        double firstBeat = 0, lastBeat = 0;
        if (bpm > 0)
        {
            double period = fps * 60.0 / bpm;                       // 一拍有多少帧
            double frameSec = HOP / rate;
            double bias = PeakBiasSamples / rate;

            // 开头：前 25 秒里找拍点的位置
            int headEnd = (int)Math.Min(env.Length, fps * 25);
            double ph = BestPhase(env, 0, headEnd, period);
            firstBeat = FrameTime(ph, rate) - bias;
            while (firstBeat < 0) firstBeat += 60.0 / bpm;

            // 结尾：音乐结束前 25 秒里找拍点，再取最靠后的一个
            double endSec = Math.Min(mixEnd, realDuration);
            int tailEnd = (int)Math.Min(env.Length, (endSec * fps));
            int tailStart = Math.Max(0, tailEnd - (int)(fps * 25));
            double ph2 = BestPhase(env, tailStart, tailEnd, period);
            double t0 = FrameTime(tailStart + ph2, rate) - bias;
            double beatSec = 60.0 / bpm;
            int k = (int)Math.Floor((endSec - 0.3 - t0) / beatSec);
            lastBeat = t0 + Math.Max(0, k) * beatSec;
        }

        // ---- 调性 ----
        var (key, camelot) = EstimateKey(mono, rate);

        return new RhythmResult(Math.Round(bpm, 2), Math.Round(firstBeat, 3), Math.Round(lastBeat, 3), Math.Round(mixEnd, 2), key, camelot, (float)conf);
    }

    private static double FrameTime(double frame, double rate) => (frame * HOP + W / 2.0) / rate;

    private static double ActiveEnd(float[] mono, double rate)
    {
        int block = (int)(rate * 0.5);
        int nb = mono.Length / block;
        if (nb < 4) return mono.Length / rate;
        var rms = new double[nb];
        double mx = 0;
        for (int b = 0; b < nb; b++)
        {
            double e = 0; int o = b * block;
            for (int i = 0; i < block; i++) { double x = mono[o + i]; e += x * x; }
            rms[b] = Math.Sqrt(e / block); mx = Math.Max(mx, rms[b]);
        }
        int last = nb - 1;
        while (last > 0 && rms[last] < mx * 0.02) last--;
        return Math.Min(mono.Length / rate, (last + 1) * 0.5);
    }

    private static float[] OnsetEnvelope(float[] x, double rate, out double fps)
    {
        fps = rate / HOP;
        int frames = (x.Length - W) / HOP + 1;
        var win = new float[W];
        for (int i = 0; i < W; i++) win[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (W - 1));
        int kLo = Math.Max(1, (int)(30 * W / rate)), kHi = Math.Min(W / 2 - 1, (int)(4000 * W / rate));
        var re = new float[W]; var im = new float[W];
        var prev = new float[kHi + 1]; var cur = new float[kHi + 1];
        var env = new float[frames];

        for (int t = 0; t < frames; t++)
        {
            int o = t * HOP;
            for (int i = 0; i < W; i++) { re[i] = x[o + i] * win[i]; im[i] = 0; }
            QualityAnalysisService.Fft(re, im);
            double flux = 0;
            for (int k = kLo; k <= kHi; k++)
            {
                float m = MathF.Log(1f + 1000f * MathF.Sqrt(re[k] * re[k] + im[k] * im[k]) / W);
                cur[k] = m;
                float d = m - prev[k];
                if (d > 0) flux += d;
            }
            env[t] = t == 0 ? 0 : (float)flux;
            (prev, cur) = (cur, prev);
        }

        // 去掉缓慢变化的底：减去 ~0.6 秒的滑动平均，负的削成 0，再轻微平滑
        int half = Math.Max(2, (int)(fps * 0.3));
        var outp = new float[frames];
        double run = 0; int lo = 0, hi = -1;
        for (int t = 0; t < frames; t++)
        {
            int nl = Math.Max(0, t - half), nh = Math.Min(frames - 1, t + half);
            while (hi < nh) { hi++; run += env[hi]; }
            while (lo < nl) { run -= env[lo]; lo++; }
            double mean = run / (hi - lo + 1);
            outp[t] = (float)Math.Max(0, env[t] - mean);
        }
        var sm = new float[frames];
        for (int t = 0; t < frames; t++)
        {
            float a = outp[Math.Max(0, t - 1)], b = outp[t], c = outp[Math.Min(frames - 1, t + 1)];
            sm[t] = 0.25f * a + 0.5f * b + 0.25f * c;
        }
        // 归一化（取 95 分位当满刻度，避免个别尖峰把整体压扁）
        var sorted = (float[])sm.Clone(); Array.Sort(sorted);
        float p95 = sorted[(int)(sorted.Length * 0.95)];
        if (p95 > 1e-6f) for (int t = 0; t < frames; t++) sm[t] = Math.Min(3f, sm[t] / p95);
        return sm;
    }

    private static double Interp(float[] a, double pos)
    {
        if (pos < 0 || pos >= a.Length - 1) return 0;
        int i = (int)pos; double f = pos - i;
        return a[i] * (1 - f) + a[i + 1] * f;
    }

    private static double AutoCorr(float[] env, double lag)
    {
        int n = env.Length;
        int cnt = (int)(n - lag - 1);
        if (cnt < n / 3) return 0;
        double s = 0;
        for (int t = 0; t < cnt; t++) s += env[t] * Interp(env, t + lag);
        return s / cnt;
    }

    private static double TempoScore(float[] env, double fps, double bpm)
    {
        double lag = fps * 60.0 / bpm;
        return AutoCorr(env, lag) + 0.6 * AutoCorr(env, 2 * lag) + 0.3 * AutoCorr(env, 4 * lag);
    }

    private static (double Bpm, double Confidence) EstimateTempo(float[] env, double fps)
    {
        double bestW = double.MinValue, bestBpm = 0;
        double sum = 0; int n = 0;
        var scores = new List<(double Bpm, double S)>();
        for (double bpm = 60; bpm <= 200; bpm += 0.5)
        {
            double s = TempoScore(env, fps, bpm);
            scores.Add((bpm, s)); sum += s; n++;
            double prior = Math.Exp(-0.5 * Math.Pow(Math.Log(bpm / 125.0) / Math.Log(1.6), 2));
            double w = s * prior;
            if (w > bestW) { bestW = w; bestBpm = bpm; }
        }
        double mean = sum / Math.Max(1, n);
        double peak = scores.First(p => p.Bpm == bestBpm).S;
        double conf = mean > 1e-9 ? peak / mean : 0;

        // 精修：±1.5 BPM 之间每 0.02 一档，用“梳状滤波”：整首歌里所有拍点处的起音总和（取最佳相位）。
        // 它比自相关更准——速度差 0.1%，70 秒后拍点就偏了 70 毫秒，总和会明显变小。
        double refined = bestBpm, refS = double.MinValue;
        for (double bpm = bestBpm - 1.5; bpm <= bestBpm + 1.5; bpm += 0.02)
        {
            double s = CombScore(env, fps * 60.0 / bpm);
            if (s > refS) { refS = s; refined = bpm; }
        }

        if (conf < 1.5) return (0, conf);       // 没有明显的周期：不当作有节拍
        return (refined, conf);
    }

    private static double CombScore(float[] env, double period)
    {
        double best = double.MinValue;
        for (double phi = 0; phi < period; phi += 0.5)
        {
            double s = 0;
            for (double p = phi; p < env.Length - 1; p += period) s += Interp(env, p);
            if (s > best) best = s;
        }
        return best;
    }

    /// <summary>在 [start, end) 帧里，找一个相位 φ（单位：帧，相对 start），让 start+φ+k·period 处的起音总和最大。</summary>
    private static double BestPhase(float[] env, int start, int end, double period)
    {
        double bestPhi = 0, best = double.MinValue;
        for (double phi = 0; phi < period; phi += 0.25)
        {
            double s = 0;
            for (double p = start + phi; p < end; p += period) s += Interp(env, p);
            if (s > best) { best = s; bestPhi = phi; }
        }
        return start + bestPhi - start;     // 相对 start
    }

    // ---------------- 调性 ----------------

    private static readonly double[] Major = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    private static readonly double[] Minor = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    // 以主音的音级（C = 0）为下标
    private static readonly int[] CamelotMajor = { 8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1 };
    private static readonly int[] CamelotMinor = { 5, 12, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10 };

    private static (string Key, string Camelot) EstimateKey(float[] x, double rate)
    {
        int frames = x.Length / KW;
        if (frames < 8) return ("", "");
        int from = (int)(frames * 0.25), to = (int)(frames * 0.75);
        int maxFrames = (int)(100 * rate / KW);
        if (to - from > maxFrames) { int mid = (from + to) / 2; from = mid - maxFrames / 2; to = from + maxFrames; }

        var win = new float[KW];
        for (int i = 0; i < KW; i++) win[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (KW - 1));
        var re = new float[KW]; var im = new float[KW];
        var chroma = new double[12];
        int kLo = (int)(90 * KW / rate), kHi = Math.Min(KW / 2 - 1, (int)(2500 * KW / rate));

        for (int f = from; f < to; f++)
        {
            int o = f * KW;
            for (int i = 0; i < KW; i++) { re[i] = x[o + i] * win[i]; im[i] = 0; }
            QualityAnalysisService.Fft(re, im);
            for (int k = kLo; k <= kHi; k++)
            {
                double freq = k * rate / KW;
                double semis = 12 * Math.Log2(freq / 440.0);
                double near = Math.Round(semis);
                if (Math.Abs(semis - near) > 0.35) continue;               // 离半音中心太远的（泄漏、噪声）不要
                int pc = (((int)near + 9) % 12 + 12) % 12;                    // A = 9
                chroma[pc] += Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            }
        }
        double total = chroma.Sum();
        if (total < 1e-6) return ("", "");

        double bestR = double.MinValue; int bestTonic = 0; bool bestMinor = false;
        for (int tonic = 0; tonic < 12; tonic++)
        {
            foreach (bool minor in new[] { false, true })
            {
                var prof = minor ? Minor : Major;
                // 把 chroma 旋转到以 tonic 为主音，再和标准分布求相关
                double mc = 0, mp = prof.Average();
                for (int i = 0; i < 12; i++) mc += chroma[(tonic + i) % 12]; mc /= 12;
                double num = 0, dc = 0, dp = 0;
                for (int i = 0; i < 12; i++)
                {
                    double a = chroma[(tonic + i) % 12] - mc, b = prof[i] - mp;
                    num += a * b; dc += a * a; dp += b * b;
                }
                double r = dc > 0 && dp > 0 ? num / Math.Sqrt(dc * dp) : 0;
                if (r > bestR) { bestR = r; bestTonic = tonic; bestMinor = minor; }
            }
        }
        string key = Names[bestTonic] + (bestMinor ? "m" : "");
        string camelot = (bestMinor ? CamelotMinor : CamelotMajor)[bestTonic] + (bestMinor ? "A" : "B");
        return (key, camelot);
    }
}
