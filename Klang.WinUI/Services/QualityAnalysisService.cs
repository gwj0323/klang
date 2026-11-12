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

/// <summary>一次无损真伪检测的结果。</summary>
public sealed record AuthResult(
    string Verdict,              // ok | lossy | upsampled | padded
    float CutoffHz,              // 频谱上高频被切掉的位置（没有明显截止时为 0）
    int EffectiveBits,           // 有效位深（16 / 24）
    int SampleRate,
    float[] SpectrumDb,          // 平均频谱（dB，等间隔，0 ~ Nyquist）供界面画图
    float NyquistHz);

/// <summary>
/// 无损文件的真伪检测：取三段各 8 秒做平均频谱，看高频是不是被“一刀切”：
/// - 44.1 / 48 kHz 的无损文件，频谱在 16~21 kHz 处出现陡峭的悬崖 → 多半是 MP3 / AAC 转成的 FLAC；
/// - 88.2 kHz 以上的 Hi-Res，内容只到 22~24 kHz 就没了 → 其实是 CD 音质升采样出来的；
/// - 声称 24 bit，但最低 8 位始终是 0 → 实际是 16 bit 填充出来的。
/// 检测是启发式的，所以只说“疑似”，并且在对话框里把频谱画出来让人自己判断。
/// </summary>
public sealed class QualityAnalysisService
{
    private static readonly Lazy<QualityAnalysisService> _instance = new(() => new QualityAnalysisService());
    public static QualityAnalysisService Instance => _instance.Value;

    private readonly ConcurrentQueue<TrackItem> _queue = new();
    private readonly HashSet<string> _queued = new();
    private int _running;
    private Microsoft.UI.Dispatching.DispatcherQueue? _ui;

    /// <summary>某首歌分析完成（界面线程）。</summary>
    public event Action<TrackItem>? Analyzed;

    public void Start(Microsoft.UI.Dispatching.DispatcherQueue ui) => _ui = ui;

    public void Enqueue(IEnumerable<TrackItem> tracks)
    {
        foreach (var t in tracks)
        {
            if (!string.IsNullOrEmpty(t.AuthVerdict) || t.IsOnline || t.IsCueTrack || t.IsMissing || t.QualityTier < 1) continue;
            lock (_queued) { if (!_queued.Add(t.Id)) continue; }
            _queue.Enqueue(t);
        }
        Kick();
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
                        var r = Analyze(t.FilePath, t.Duration, t.BitDepth);
                        if (r != null)
                        {
                            _ui?.SafeEnqueue(() =>
                            {
                                t.AuthCutoffHz = r.CutoffHz;
                                t.AuthBits = r.EffectiveBits;
                                t.AuthVerdict = r.Verdict;
                                Analyzed?.Invoke(t);
                            });
                            if (++sinceSave >= 8) { sinceSave = 0; _ui?.SafeEnqueue(() => LibraryService.Instance.SaveLibrary()); }
                        }
                    }
                    catch { }
                    await Task.Delay(60);
                }
                if (sinceSave > 0) _ui?.SafeEnqueue(() => LibraryService.Instance.SaveLibrary());
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                if (!_queue.IsEmpty) Kick();
            }
        });
    }

    // ---------------------------------------------------------------------------------

    private const int N = 8192;

    /// <summary>分析一个文件。返回 null 表示解码失败 / 太短。</summary>
    public static AuthResult? Analyze(string path, double durationSeconds, int claimedBits)
    {
        if (!File.Exists(path)) return null;

        using var reader = new MediaFoundationReader(path);
        var sp = reader.ToSampleProvider();
        int sr = sp.WaveFormat.SampleRate, ch = sp.WaveFormat.Channels;
        double nyq = sr / 2.0;

        // 三段各 8 秒：20% / 50% / 80% 处（短于 30 秒的只取一段）
        var starts = durationSeconds >= 30
            ? new[] { durationSeconds * 0.2, durationSeconds * 0.5, durationSeconds * 0.8 }
            : new[] { Math.Max(0, durationSeconds * 0.15) };

        var window = new float[N];
        for (int i = 0; i < N; i++) window[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (N - 1));
        var power = new double[N / 2];
        int frames = 0;

        var re = new float[N]; var im = new float[N];
        var buf = new float[N * ch];
        long nonZero = 0, lowBitsZero = 0;           // 位深填充检测：24 bit 满刻度下，最低 8 位是否恒为 0

        foreach (double s0 in starts)
        {
            if (s0 + 1 > durationSeconds) continue;
            reader.CurrentTime = TimeSpan.FromSeconds(s0);
            int needFrames = (int)(sr * Math.Min(8.0, Math.Max(2.0, durationSeconds - s0 - 0.5)));
            int got = 0;
            var mono = new float[needFrames];
            var tmp = new float[4096 * ch];
            while (got < needFrames)
            {
                int n = sp.Read(tmp.AsSpan());
                if (n <= 0) break;
                for (int i = 0; i + ch <= n && got < needFrames; i += ch)
                {
                    float sum = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        float v = tmp[i + c];
                        sum += v;
                        long q = (long)MathF.Round(v * 8388608f);          // 还原成 24 bit 整数
                        if (q != 0) { nonZero++; if ((q & 0xFF) == 0) lowBitsZero++; }
                    }
                    mono[got++] = sum / ch;
                }
            }

            for (int off = 0; off + N <= got; off += N / 2)
            {
                double e = 0;
                for (int i = 0; i < N; i++) { float x = mono[off + i]; e += x * x; re[i] = x * window[i]; im[i] = 0; }
                if (Math.Sqrt(e / N) < 1e-4) continue;      // 静音帧不参与
                Fft(re, im);
                for (int k = 1; k < N / 2; k++) power[k] += re[k] * re[k] + im[k] * im[k];
                frames++;
            }
        }
        if (frames < 8) return null;

        // 平均功率谱 → dB，再做约 250 Hz 宽度的滑动平均
        double binHz = (double)sr / N;
        var db = new float[N / 2];
        for (int k = 1; k < N / 2; k++) db[k] = (float)(10 * Math.Log10(power[k] / frames + 1e-20));
        db[0] = db[1];
        int smoothHalf = Math.Max(1, (int)(125 / binHz));
        var sm = new float[N / 2];
        for (int k = 0; k < N / 2; k++)
        {
            int a = Math.Max(0, k - smoothHalf), b = Math.Min(N / 2 - 1, k + smoothHalf);
            double acc = 0; for (int j = a; j <= b; j++) acc += db[j];
            sm[k] = (float)(acc / (b - a + 1));
        }

        float Level(double f0, double f1)    // 区间平均电平
        {
            int a = Math.Clamp((int)(f0 / binHz), 0, N / 2 - 1), b = Math.Clamp((int)(f1 / binHz), a, N / 2 - 1);
            double acc = 0; for (int j = a; j <= b; j++) acc += sm[j];
            return (float)(acc / (b - a + 1));
        }

        float reference = Level(200, 4000);                 // 中频的平均电平，作为参照

        // 找“悬崖”：下方 200~800 Hz 的平均电平 − 上方 200~800 Hz 的平均电平，取最大的位置
        double scanFrom = 12000, scanTo = Math.Min(nyq - 900, 23800);
        double bestF = 0; float bestDrop = 0;
        for (double f = scanFrom; f <= scanTo; f += binHz * 2)
        {
            float below = Level(f - 800, f - 200), above = Level(f + 200, f + 800);
            float drop = below - above;
            if (drop > bestDrop) { bestDrop = drop; bestF = f; }
        }
        float aboveMean = bestF > 0 ? Level(bestF + 300, Math.Min(nyq - 100, bestF + 3000)) : 0f;
        bool cliff = bestF > 0 && bestDrop >= 22f && aboveMean < reference - 50f;

        // 有效位深（只有声称 > 16 bit 时才有意义）
        int bits = claimedBits;
        if (claimedBits > 16 && nonZero > 20000 && lowBitsZero >= nonZero * 0.999) bits = 16;

        string verdict = "ok";
        float cutoff = cliff ? (float)bestF : 0f;
        if (cliff)
        {
            if (sr <= 48000 && bestF < nyq - 900) verdict = "lossy";                 // 44.1 / 48k 里在 12~21 kHz 之间被切断
            else if (sr >= 88200 && bestF < 24500) verdict = "upsampled";            // Hi-Res 里内容只到 CD / 48k 的带宽
        }
        if (verdict == "ok" && bits == 16 && claimedBits > 16) verdict = "padded";

        // 画图用的频谱：降到约 400 个点（保留平滑后的值）
        int pts = 400; var plot = new float[pts];
        for (int i = 0; i < pts; i++) plot[i] = sm[Math.Min(N / 2 - 1, (int)((long)i * (N / 2) / pts))];
        return new AuthResult(verdict, cutoff, bits, sr, plot, (float)nyq);
    }

    internal static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wRe = (float)Math.Cos(ang), wIm = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cRe = 1, cIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    float tRe = re[b] * cRe - im[b] * cIm, tIm = re[b] * cIm + im[b] * cRe;
                    re[b] = re[a] - tRe; im[b] = im[a] - tIm;
                    re[a] += tRe; im[a] += tIm;
                    float nRe = cRe * wRe - cIm * wIm;
                    cIm = cRe * wIm + cIm * wRe; cRe = nRe;
                }
            }
        }
    }
}
