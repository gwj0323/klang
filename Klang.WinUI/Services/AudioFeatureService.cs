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

/// <summary>
/// 后台给曲库里的歌提取“声学特征”（每首取中段约 20 秒，全在本机算）：
/// [0] 响度(log RMS) [1] 动态(RMS 起伏) [2] 过零率 [3] 频谱质心(明暗) [4] 频谱滚降 [5] 频谱平坦度(噪声感)
/// [6] 低频占比 [7] 中频占比 [8] 高频占比 [9] 节奏密度(每秒起音数)。
/// 漫游模式用它们的相似度去找“听起来像”的歌。
/// </summary>
public sealed class AudioFeatureService
{
    public const int Dim = 10;

    private static readonly Lazy<AudioFeatureService> _instance = new(() => new AudioFeatureService());
    public static AudioFeatureService Instance => _instance.Value;

    private readonly ConcurrentQueue<TrackItem> _queue = new();
    private readonly HashSet<string> _queued = new();
    private int _running;
    private Microsoft.UI.Dispatching.DispatcherQueue? _ui;

    /// <summary>提取完成一批后触发（界面线程），可用来保存曲库。</summary>
    public int Pending => _queue.Count;

    public void Start(Microsoft.UI.Dispatching.DispatcherQueue ui) => _ui = ui;

    public void Enqueue(IEnumerable<TrackItem> tracks)
    {
        foreach (var t in tracks)
        {
            if (t.AudioFeat != null || t.IsOnline || t.IsCueTrack || t.IsMissing) continue;
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
                        var feat = Extract(t.FilePath, t.Duration);
                        if (feat != null)
                        {
                            t.AudioFeat = feat;
                            if (++sinceSave >= 10) { sinceSave = 0; SaveSoon(); }
                        }
                    }
                    catch { }
                    await Task.Delay(40);   // 让出 CPU，别影响正在播放的音频
                }
                if (sinceSave > 0) SaveSoon();
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                if (!_queue.IsEmpty) Kick();
            }
        });
    }

    private void SaveSoon() => _ui?.TryEnqueue(() => LibraryService.Instance.SaveLibrary());

    public static float[]? Extract(string path, double durationSeconds)
    {
        if (!File.Exists(path)) return null;

        using var reader = new MediaFoundationReader(path);
        var sp = reader.ToSampleProvider();
        int sr = sp.WaveFormat.SampleRate;
        int ch = sp.WaveFormat.Channels;
        if (durationSeconds > 45) reader.CurrentTime = TimeSpan.FromSeconds(durationSeconds * 0.3);

        int want = sr * 20;
        var mono = new float[want];
        var buf = new float[4096 * ch];
        int got = 0;
        while (got < want)
        {
            int n = sp.Read(buf.AsSpan());
            if (n <= 0) break;
            for (int i = 0; i + ch <= n && got < want; i += ch)
            {
                float s = 0;
                for (int c = 0; c < ch; c++) s += buf[i + c];
                mono[got++] = s / ch;
            }
        }
        if (got < sr * 3) return null;   // 太短，不可靠

        const int N = 2048, Hop = 1024;
        var window = new float[N];
        for (int i = 0; i < N; i++) window[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (N - 1));

        var re = new float[N];
        var im = new float[N];
        var prevMag = new float[N / 2];
        double binHz = (double)sr / N;
        int lowEnd = (int)(250 / binHz), midEnd = (int)(2000 / binHz);

        var rmsList = new List<double>();
        double zcrSum = 0, centroidSum = 0, rolloffSum = 0, flatSum = 0, lowSum = 0, midSum = 0, highSum = 0;
        var flux = new List<double>();
        int frames = 0;

        for (int start = 0; start + N <= got; start += Hop)
        {
            double energy = 0; int zc = 0;
            for (int i = 0; i < N; i++)
            {
                float x = mono[start + i];
                energy += x * x;
                if (i > 0 && (x >= 0) != (mono[start + i - 1] >= 0)) zc++;
                re[i] = x * window[i];
                im[i] = 0;
            }
            double rms = Math.Sqrt(energy / N);
            if (rms < 1e-5) continue;   // 静音帧不参与统计

            Fft(re, im);

            double total = 0, weighted = 0, logSum = 0, lo = 0, mid = 0, hi = 0, fl = 0;
            var mag = new float[N / 2];
            for (int k = 1; k < N / 2; k++)
            {
                double m = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                mag[k] = (float)m;
                total += m;
                weighted += m * k;
                logSum += Math.Log(m + 1e-9);
                double p = m * m;
                if (k < lowEnd) lo += p; else if (k < midEnd) mid += p; else hi += p;
                double d = m - prevMag[k];
                if (d > 0) fl += d;
            }
            if (total <= 0) continue;

            double acc = 0, roll = 0;
            for (int k = 1; k < N / 2; k++) { acc += mag[k]; if (acc >= 0.85 * total) { roll = k; break; } }
            double arith = total / (N / 2 - 1);
            double geo = Math.Exp(logSum / (N / 2 - 1));
            double band = lo + mid + hi + 1e-12;

            rmsList.Add(rms);
            zcrSum += (double)zc / N;
            centroidSum += weighted / total / (N / 2);
            rolloffSum += roll / (N / 2);
            flatSum += geo / (arith + 1e-12);
            lowSum += lo / band; midSum += mid / band; highSum += hi / band;
            flux.Add(fl);
            Array.Copy(mag, prevMag, mag.Length);
            frames++;
        }
        if (frames < 20) return null;

        double meanRms = rmsList.Average();
        double varRms = rmsList.Sum(r => (r - meanRms) * (r - meanRms)) / frames;

        // 起音：谱通量里高于均值 + 一个标准差的局部峰
        double fMean = flux.Average();
        double fStd = Math.Sqrt(flux.Sum(f => (f - fMean) * (f - fMean)) / flux.Count);
        int onsets = 0;
        for (int i = 1; i + 1 < flux.Count; i++)
            if (flux[i] > fMean + fStd && flux[i] > flux[i - 1] && flux[i] >= flux[i + 1]) onsets++;
        double seconds = (double)frames * Hop / sr;

        return new[]
        {
            (float)Math.Log10(meanRms + 1e-6),
            (float)(Math.Sqrt(varRms) / (meanRms + 1e-9)),
            (float)(zcrSum / frames),
            (float)(centroidSum / frames),
            (float)(rolloffSum / frames),
            (float)(flatSum / frames),
            (float)(lowSum / frames),
            (float)(midSum / frames),
            (float)(highSum / frames),
            (float)(onsets / Math.Max(1.0, seconds))
        };
    }

    /// <summary>原地的基 2 FFT（长度必须是 2 的幂）。</summary>
    private static void Fft(float[] re, float[] im)
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
                    float tRe = re[b] * cRe - im[b] * cIm;
                    float tIm = re[b] * cIm + im[b] * cRe;
                    re[b] = re[a] - tRe; im[b] = im[a] - tIm;
                    re[a] += tRe; im[a] += tIm;
                    float nRe = cRe * wRe - cIm * wIm;
                    cIm = cRe * wIm + cIm * wRe;
                    cRe = nRe;
                }
            }
        }
    }
}
