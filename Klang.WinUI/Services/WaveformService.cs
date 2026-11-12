using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Klang_WinUI.Services;

/// <summary>
/// 波形进度条用的振幅概要：把整首歌分成固定数量的小段，每段取响度（RMS），归一化到 0–1。
/// 第一次播放时在后台解码计算，结果缓存在 %LocalAppData%\Klang\Waveforms，之后直接读取。
/// </summary>
public static class WaveformService
{
    public const int Buckets = 800;

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klang", "Waveforms");

    private static readonly ConcurrentDictionary<string, float[]> Memory = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);   // 同一时间只解码一首，不和播放抢磁盘与 CPU

    /// <summary>取波形；没有缓存时在后台计算。文件不存在、不是本地文件或解码失败时返回 null。</summary>
    public static async Task<float[]?> GetAsync(string? path, double start = 0, double end = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        string key;
        try
        {
            var fi = new FileInfo(path);
            key = Hash($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{start:0.###}|{end:0.###}");
        }
        catch { return null; }

        if (Memory.TryGetValue(key, out var hit)) return hit;

        string file = Path.Combine(CacheDir, key + ".wf");
        var cached = await Task.Run(() => ReadCache(file), ct);
        if (cached != null) { Remember(key, cached); return cached; }

        await Gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var data = await Task.Run(() => Compute(path, start, end, ct), ct);
            if (data == null) return null;
            Remember(key, data);
            try
            {
                Directory.CreateDirectory(CacheDir);
                var bytes = new byte[data.Length];
                for (int i = 0; i < data.Length; i++) bytes[i] = (byte)Math.Round(Math.Clamp(data[i], 0f, 1f) * 255);
                await File.WriteAllBytesAsync(file, bytes, CancellationToken.None);
            }
            catch { }
            return data;
        }
        finally { Gate.Release(); }
    }

    private static void Remember(string key, float[] data)
    {
        if (Memory.Count > 64) Memory.Clear();
        Memory[key] = data;
    }

    private static float[]? ReadCache(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length != Buckets) return null;
            var r = new float[Buckets];
            for (int i = 0; i < Buckets; i++) r[i] = bytes[i] / 255f;
            return r;
        }
        catch { return null; }
    }

    private static float[]? Compute(string path, double start, double end, CancellationToken ct)
    {
        try
        {
            using var reader = new MediaFoundationReader(path);
            double total = reader.TotalTime.TotalSeconds;
            if (start > 0 && start < total) reader.CurrentTime = TimeSpan.FromSeconds(start);
            double stop = end > start ? Math.Min(end, total) : total;
            double span = stop - Math.Max(0, start);
            if (span <= 1) return null;

            var sp = reader.ToSampleProvider();
            int sr = sp.WaveFormat.SampleRate, ch = sp.WaveFormat.Channels;
            long totalFrames = (long)(span * sr);
            double framesPerBucket = (double)totalFrames / Buckets;

            var sum = new double[Buckets];
            var cnt = new int[Buckets];
            var tmp = new float[8192 * ch];
            long frame = 0;
            while (frame < totalFrames)
            {
                ct.ThrowIfCancellationRequested();
                int n = sp.Read(tmp.AsSpan());
                if (n <= 0) break;
                for (int i = 0; i + ch <= n && frame < totalFrames; i += ch, frame++)
                {
                    double m = 0;
                    for (int c = 0; c < ch; c++) m += tmp[i + c];
                    m /= ch;
                    int b = (int)Math.Min(Buckets - 1, frame / framesPerBucket);
                    sum[b] += m * m;
                    cnt[b]++;
                }
            }
            if (frame < totalFrames * 0.5) return null;

            var rms = new float[Buckets];
            for (int b = 0; b < Buckets; b++) rms[b] = cnt[b] > 0 ? (float)Math.Sqrt(sum[b] / cnt[b]) : 0f;

            // 用第 98 百分位做归一化：个别爆点不会把其余部分压得很矮
            var sorted = (float[])rms.Clone();
            Array.Sort(sorted);
            float refLevel = Math.Max(1e-4f, sorted[(int)(Buckets * 0.98)]);
            for (int b = 0; b < Buckets; b++)
            {
                float v = Math.Clamp(rms[b] / refLevel, 0f, 1f);
                rms[b] = MathF.Pow(v, 0.8f);   // 略微提亮安静段落，看得出起伏
            }
            return rms;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static string Hash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }
}
