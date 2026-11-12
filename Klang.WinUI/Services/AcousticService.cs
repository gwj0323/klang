using System;
using System.Collections.Generic;
using System.Linq;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services;

/// <summary>曲库地图上的一个点。</summary>
public sealed record MapPoint(TrackItem Track, float X, float Y, int Cluster);

/// <summary>
/// 基于每首歌的 10 维声学特征（响度、动态、过零率、频谱质心……）做三件事，全部在本机计算：
/// 1. 曲库地图：标准化后用 PCA 降到二维，听起来相似的歌会聚在一起；横轴是“能量”（安静 → 动感），纵轴是“明暗”（沉稳 → 明亮）；
/// 2. 心情歌单：按能量 / 明暗给每首歌打分，挑出安静、专注、动感、明亮、沉稳的歌；
/// 3. 找相似：在标准化后的特征空间里找最近邻。
/// </summary>
public sealed class AcousticService
{
    private static readonly Lazy<AcousticService> _instance = new(() => new AcousticService());
    public static AcousticService Instance => _instance.Value;

    private const int D = AudioFeatureService.Dim;

    private sealed class Entry
    {
        public required TrackItem Track;
        public required float[] Z;       // 标准化特征
        public float Energy, Brightness; // 综合分数
        public float X, Y;
        public int Cluster;
    }

    private List<Entry> _entries = new();
    private int _version = -1;

    /// <summary>已经提取出声学特征、能放上地图的歌曲数量。</summary>
    public int AnalyzedCount(IReadOnlyList<TrackItem> lib) => lib.Count(t => t.AudioFeat is { Length: D });

    private void Ensure(IReadOnlyList<TrackItem> lib)
    {
        var withFeat = lib.Where(t => t.AudioFeat is { Length: D } && !t.IsMissing).ToList();
        int ver = withFeat.Count;
        if (ver == _version) return;
        _version = ver;
        _entries = new List<Entry>();
        if (ver == 0) return;

        // 标准化
        var mean = new float[D]; var std = new float[D];
        for (int i = 0; i < D; i++)
        {
            mean[i] = withFeat.Average(t => t.AudioFeat![i]);
            float v = withFeat.Sum(t => (t.AudioFeat![i] - mean[i]) * (t.AudioFeat![i] - mean[i])) / ver;
            std[i] = Math.Max(1e-5f, MathF.Sqrt(v));
        }
        foreach (var t in withFeat)
        {
            var z = new float[D];
            for (int i = 0; i < D; i++) z[i] = (t.AudioFeat![i] - mean[i]) / std[i];
            _entries.Add(new Entry
            {
                Track = t,
                Z = z,
                // 能量：响度 + 节奏密度 + 过零率；明暗：质心 + 滚降 + 高频占比
                Energy = 0.4f * z[0] + 0.4f * z[9] + 0.2f * z[2],
                Brightness = 0.5f * z[3] + 0.3f * z[4] + 0.2f * z[8]
            });
        }

        // 能量 / 明暗再各自标准化（均值 0、标准差 1），后面的心情阈值才有统一的含义
        Standardize(e => e.Energy, (e, v) => e.Energy = v);
        Standardize(e => e.Brightness, (e, v) => e.Brightness = v);

        ComputeMap();
        ComputeClusters();
    }

    private void Standardize(Func<Entry, float> get, Action<Entry, float> set)
    {
        if (_entries.Count == 0) return;
        float m = _entries.Average(get);
        float sd = MathF.Sqrt(_entries.Sum(e => (get(e) - m) * (get(e) - m)) / _entries.Count);
        if (sd < 1e-5f) sd = 1f;
        foreach (var e in _entries) set(e, (get(e) - m) / sd);
    }

    // ---------- PCA（幂迭代求前两个主成分） ----------
    private void ComputeMap()
    {
        int n = _entries.Count;
        if (n < 3)
        {
            // 点太少，PCA 没意义：直接用能量 / 明暗当坐标
            foreach (var e in _entries) { e.X = e.Energy; e.Y = e.Brightness; }
            Normalize();
            return;
        }

        var cov = new double[D, D];
        foreach (var e in _entries)
            for (int a = 0; a < D; a++)
                for (int b = 0; b < D; b++) cov[a, b] += e.Z[a] * e.Z[b];
        for (int a = 0; a < D; a++) for (int b = 0; b < D; b++) cov[a, b] /= n;

        var pc1 = PowerIteration(cov, null);
        var pc2 = PowerIteration(cov, pc1);

        foreach (var e in _entries)
        {
            double x = 0, y = 0;
            for (int i = 0; i < D; i++) { x += pc1[i] * e.Z[i]; y += pc2[i] * e.Z[i]; }
            e.X = (float)x; e.Y = (float)y;
        }

        // 定向：横轴向右 = 更动感，纵轴向上 = 更明亮（PCA 的符号是任意的）
        if (Corr(_entries.Select(e => e.X), _entries.Select(e => e.Energy)) < 0) foreach (var e in _entries) e.X = -e.X;
        if (Corr(_entries.Select(e => e.Y), _entries.Select(e => e.Brightness)) < 0) foreach (var e in _entries) e.Y = -e.Y;
        Normalize();
    }

    private static double[] PowerIteration(double[,] m, double[]? deflate)
    {
        var v = new double[D];
        for (int i = 0; i < D; i++) v[i] = 1.0 / Math.Sqrt(D) + (i % 3) * 0.01;
        for (int it = 0; it < 120; it++)
        {
            var w = new double[D];
            for (int a = 0; a < D; a++) for (int b = 0; b < D; b++) w[a] += m[a, b] * v[b];
            if (deflate != null)
            {
                double dot = 0; for (int i = 0; i < D; i++) dot += w[i] * deflate[i];
                for (int i = 0; i < D; i++) w[i] -= dot * deflate[i];
            }
            double norm = Math.Sqrt(w.Sum(x => x * x));
            if (norm < 1e-12) break;
            for (int i = 0; i < D; i++) v[i] = w[i] / norm;
        }
        return v;
    }

    private static double Corr(IEnumerable<float> a, IEnumerable<float> b)
    {
        var x = a.ToArray(); var y = b.ToArray();
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); syy += (y[i] - my) * (y[i] - my); }
        return sxx < 1e-12 || syy < 1e-12 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }

    private void Normalize()
    {
        if (_entries.Count == 0) return;
        float minX = _entries.Min(e => e.X), maxX = _entries.Max(e => e.X), minY = _entries.Min(e => e.Y), maxY = _entries.Max(e => e.Y);
        float sx = Math.Max(1e-4f, maxX - minX), sy = Math.Max(1e-4f, maxY - minY);
        foreach (var e in _entries) { e.X = (e.X - minX) / sx * 2 - 1; e.Y = (e.Y - minY) / sy * 2 - 1; }
    }

    // ---------- 简单 k-means 给地图上色 ----------
    private void ComputeClusters()
    {
        int n = _entries.Count;
        int k = Math.Clamp(n / 4, 1, 6);
        if (n == 0) return;
        var rnd = new Random(5);
        var centers = _entries.OrderBy(_ => rnd.Next()).Take(k).Select(e => (float[])e.Z.Clone()).ToList();
        for (int it = 0; it < 15; it++)
        {
            foreach (var e in _entries)
            {
                int best = 0; float bd = float.MaxValue;
                for (int c = 0; c < centers.Count; c++) { float d = Dist2(e.Z, centers[c]); if (d < bd) { bd = d; best = c; } }
                e.Cluster = best;
            }
            for (int c = 0; c < centers.Count; c++)
            {
                var members = _entries.Where(e => e.Cluster == c).ToList();
                if (members.Count == 0) continue;
                for (int i = 0; i < D; i++) centers[c][i] = members.Average(m => m.Z[i]);
            }
        }
    }

    private static float Dist2(float[] a, float[] b)
    {
        float s = 0; for (int i = 0; i < a.Length; i++) { float d = a[i] - b[i]; s += d * d; }
        return s;
    }

    // ---------- 对外接口 ----------

    public List<MapPoint> GetMap(IReadOnlyList<TrackItem> lib)
    {
        Ensure(lib);
        return _entries.Select(e => new MapPoint(e.Track, e.X, e.Y, e.Cluster)).ToList();
    }

    /// <summary>在特征空间里离 track 最近的 count 首歌（不含它自己）。没有特征时返回空。</summary>
    public List<TrackItem> Similar(TrackItem track, IReadOnlyList<TrackItem> lib, int count)
    {
        Ensure(lib);
        var me = _entries.FirstOrDefault(e => ReferenceEquals(e.Track, track) || e.Track.Id == track.Id);
        if (me == null) return new List<TrackItem>();
        return _entries.Where(e => !ReferenceEquals(e, me))
            .OrderBy(e => Dist2(e.Z, me.Z))
            .Take(count)
            .Select(e => e.Track)
            .ToList();
    }

    /// <summary>
    /// 心情："calm" 安静 | "focus" 专注 | "energetic" 动感 | "bright" 明亮 | "deep" 沉稳。
    /// 按分数阈值筛选（能量、明暗都已标准化，单位是曲库内的标准差），所以每个歌单的数量取决于曲库本身的分布，
    /// 而不是固定比例；结果按贴合程度从高到低排列。
    /// </summary>
    public List<TrackItem> MoodTracks(string mood, IReadOnlyList<TrackItem> lib)
    {
        Ensure(lib);
        if (_entries.Count == 0) return new List<TrackItem>();

        const float T = 0.5f;   // 偏离曲库平均水平半个标准差以上，才算明显“安静 / 动感 / 明亮 / 沉稳”
        IEnumerable<(Entry E, float Score)> scored = mood switch
        {
            "calm" => _entries.Select(e => (e, -e.Energy)),
            "energetic" => _entries.Select(e => (e, e.Energy)),
            "bright" => _entries.Select(e => (e, e.Brightness)),
            "deep" => _entries.Select(e => (e, -e.Brightness)),
            // 专注：能量在平均附近略偏低、动态起伏小（没有突然的大声小声）
            "focus" => _entries
                .Where(e => Math.Abs(e.Energy + 0.2f) < 0.6f && e.Z[1] < 0.3f)
                .Select(e => (e, T + 1f - Math.Abs(e.Energy + 0.2f) - 0.5f * Math.Max(0, e.Z[1]))),
            _ => Enumerable.Empty<(Entry, float)>()
        };
        return scored.Where(x => x.Score >= T)
            .OrderByDescending(x => x.Score)
            .Select(x => x.E.Track)
            .ToList();
    }

    public static string MoodName(string mood) => mood switch
    {
        "calm" => L.T("安静"),
        "focus" => L.T("专注"),
        "energetic" => L.T("动感"),
        "bright" => L.T("明亮"),
        "deep" => L.T("沉稳"),
        _ => ""
    };
}
