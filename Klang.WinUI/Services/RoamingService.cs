using System;
using System.Collections.Generic;
using System.Linq;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services;

/// <summary>
/// 漫游模式：在本机学习“你接下来想听什么”，猜下一首。全部本地计算，不联网、不上传任何数据。
///
/// 模型 = 在线逻辑回归，预测“这首歌放出来你会听完（而不是切走）”的概率：
///   特征：A→B 的历史转移、同歌手 / 同专辑 / 同目录、声学相似度、音质与时长相近、
///         这首歌的完播率、是否喜欢、近期是否刚放过、当前时段是否常听。
///   学习：每首歌播完（完播 → 正样本）或被切走（听不到 25% → 负样本）都做一步 SGD；
///         你手动点的下一首也记为一次强转移。
///   选歌：对候选打分后按 softmax 抽样（保持新鲜感），并留一小部分随机探索。
/// </summary>
public sealed class RoamingService
{
    private static readonly Lazy<RoamingService> _instance = new(() => new RoamingService());
    public static RoamingService Instance => _instance.Value;

    private const int F = 12;                 // 特征数（不含偏置）
    private static readonly string[] FeatureNames =
    {
        L.T("你常在上一首之后听它"), L.T("同一位歌手"), L.T("同一张专辑"), L.T("同一目录"), L.T("音色和节奏相近"), L.T("音质相当"),
        L.T("时长相近"), L.T("你常听完它"), L.T("你喜欢的歌"), L.T("（近期放过）"), L.T("你常播放它"), L.T("这个时段你常听它")
    };
    private static readonly float[] Prior = { 2.0f, 0.6f, 0.4f, 0.1f, 1.4f, 0.2f, 0.3f, 1.5f, 0.5f, -2.5f, 0.3f, 0.5f, 0f };

    private readonly Random _rand = new();
    private readonly LinkedList<string> _recent = new();      // 本次运行里最近播放过的歌（避免短时间重复）
    private string? _pendingFrom;                              // 当前这首歌是接在谁后面放的

    private static RoamModel Model => StateService.Instance.State.Roam;

    public RoamingService()
    {
        EnsureWeights();
    }

    private static void EnsureWeights()
    {
        if (Model.Weights.Length != F + 1) Model.Weights = (float[])Prior.Clone();
    }

    /// <summary>已经学了多少次反馈。</summary>
    public int Samples => Model.Samples;

    public void Reset()
    {
        var m = StateService.Instance.State.Roam;
        m.Weights = (float[])Prior.Clone();
        m.Samples = 0;
        m.Stats.Clear();
        m.Transitions.Clear();
        _recent.Clear();
        _pendingFrom = null;
        StateService.Instance.RequestSave();
    }

    // ================= 学习 =================

    /// <summary>
    /// 切歌时调用：prev 是刚离开的这首，prevFraction 是它听了多少（0~1），next 是接下来要放的。
    /// byRoam 表示 next 是漫游选的（false = 你手动点的 / 普通模式自动切的）。
    /// </summary>
    public void OnTransition(TrackItem? prev, double prevFraction, TrackItem next, bool byRoam, IReadOnlyList<TrackItem> library)
    {
        EnsureWeights();
        EnsureNorm(library);
        if (prev != null && !ReferenceEquals(prev, next))
        {
            int y = prevFraction >= 0.6 ? 1 : prevFraction < 0.25 ? 0 : -1;
            var st = StatOf(prev.Id);
            st.Plays++;
            st.Hours[DateTime.Now.Hour] += 1f;
            if (y == 1) st.Completes++; else if (y == 0) st.Skips++;

            // 用它被选中时的特征做一步学习
            if (y >= 0 && _pendingFrom != null)
            {
                var from = library.FirstOrDefault(t => t.Id == _pendingFrom);
                if (from != null)
                {
                    var f = Features(from, prev, library, ignoreRecent: true);
                    Sgd(f, y);
                    AddTransition(from.Id, prev.Id, y == 1 ? 1f : -0.4f);
                }
            }

            // 手动点的下一首：很强的“A 之后想听 B”信号
            if (!byRoam && prevFraction >= 0.15) AddTransition(prev.Id, next.Id, 1.5f);

            Remember(prev.Id);
            Model.Samples++;
            StateService.Instance.RequestSave();
        }
        _pendingFrom = prev?.Id;
        Remember(next.Id);
    }

    private void Remember(string id)
    {
        _recent.Remove(id);
        _recent.AddFirst(id);
        while (_recent.Count > 20) _recent.RemoveLast();
    }

    private static RoamStat StatOf(string id)
    {
        if (!Model.Stats.TryGetValue(id, out var s)) Model.Stats[id] = s = new RoamStat();
        if (s.Hours.Length != 24) s.Hours = new float[24];
        return s;
    }

    private static void AddTransition(string from, string to, float delta)
    {
        if (!Model.Transitions.TryGetValue(from, out var row)) Model.Transitions[from] = row = new Dictionary<string, float>();
        row.TryGetValue(to, out var v);
        v = Math.Max(0f, v + delta);
        if (v <= 0f) row.Remove(to); else row[to] = v;

        // 一行最多留 24 个，其余按权重淘汰；整体缓慢衰减，让口味变化能被跟上
        if (row.Count > 24)
        {
            foreach (var k in row.OrderBy(kv => kv.Value).Take(row.Count - 24).Select(kv => kv.Key).ToList()) row.Remove(k);
        }
        foreach (var k in row.Keys.ToList()) row[k] *= 0.995f;
    }

    private static void Sgd(float[] f, int y)
    {
        var w = Model.Weights;
        double z = w[F];
        for (int i = 0; i < F; i++) z += w[i] * f[i];
        double p = 1.0 / (1.0 + Math.Exp(-z));
        double err = y - p;
        const double lr = 0.06, l2 = 0.002;
        for (int i = 0; i < F; i++)
        {
            double nw = w[i] + lr * err * f[i] - l2 * (w[i] - Prior[i]);   // 向先验收缩，防止少量数据把权重带偏
            w[i] = (float)Math.Clamp(nw, -5.0, 5.0);
        }
        w[F] = (float)Math.Clamp(w[F] + lr * err, -3.0, 3.0);
    }

    // ================= 特征 =================

    private float[] Features(TrackItem cur, TrackItem cand, IReadOnlyList<TrackItem> lib, bool ignoreRecent = false)
    {
        var f = new float[F];

        // 0 历史转移：cur → cand 占 cur 出边总权重的比例
        if (Model.Transitions.TryGetValue(cur.Id, out var row) && row.Count > 0)
        {
            row.TryGetValue(cand.Id, out var w);
            float total = row.Values.Sum();
            f[0] = total > 0 ? Math.Min(1f, w / total * 2f) : 0f;
        }

        f[1] = !string.IsNullOrEmpty(cur.Artist) && cur.Artist != "未知歌手" && cur.Artist == cand.Artist ? 1f : 0f;
        f[2] = !string.IsNullOrEmpty(cur.Album) && cur.Album != "未知专辑" && cur.Album == cand.Album ? 1f : 0f;
        f[3] = cur.FolderName == cand.FolderName ? 1f : 0f;
        f[4] = AudioSimilarity(cur, cand);
        f[5] = cur.QualityTier == cand.QualityTier ? 1f : 0f;
        f[6] = (float)Math.Exp(-Math.Abs(cur.Duration - cand.Duration) / 90.0);

        Model.Stats.TryGetValue(cand.Id, out var st);
        if (st != null)
        {
            f[7] = ((st.Completes + 1f) / (st.Completes + st.Skips + 2f) - 0.5f) * 2f;   // 平滑后的完播率，-1~1
            f[10] = Math.Min(1f, MathF.Log(1f + st.Plays) / 3f);
            f[11] = HourAffinity(st);
        }
        f[8] = cand.IsFavorite ? 1f : 0f;
        f[9] = !ignoreRecent && _recent.Contains(cand.Id) ? 1f : 0f;
        return f;
    }

    private static float HourAffinity(RoamStat st)
    {
        float total = st.Hours.Sum();
        if (total < 2f) return 0f;
        int h = DateTime.Now.Hour;
        float near = 0f;
        for (int d = -2; d <= 2; d++) near += st.Hours[(h + d + 24) % 24];
        return near / total;
    }

    // ---- 声学相似度：对特征做库内标准化后算余弦，映射到 0~1 ----
    private float[] _mean = new float[AudioFeatureService.Dim];
    private float[] _std = new float[AudioFeatureService.Dim];
    private int _normCount = -1;

    private void EnsureNorm(IReadOnlyList<TrackItem> lib)
    {
        var withFeat = lib.Where(t => t.AudioFeat != null).ToList();
        if (withFeat.Count == _normCount) return;
        _normCount = withFeat.Count;
        int d = AudioFeatureService.Dim;
        for (int i = 0; i < d; i++)
        {
            if (withFeat.Count == 0) { _mean[i] = 0; _std[i] = 1; continue; }
            float m = withFeat.Average(t => t.AudioFeat![i]);
            float v = withFeat.Sum(t => (t.AudioFeat![i] - m) * (t.AudioFeat![i] - m)) / withFeat.Count;
            _mean[i] = m;
            _std[i] = Math.Max(1e-4f, MathF.Sqrt(v));
        }
    }

    private float AudioSimilarity(TrackItem a, TrackItem b)
    {
        var fa = a.AudioFeat; var fb = b.AudioFeat;
        if (fa == null || fb == null || fa.Length != AudioFeatureService.Dim || fb.Length != AudioFeatureService.Dim) return 0f;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < fa.Length; i++)
        {
            double x = (fa[i] - _mean[i]) / _std[i];
            double y = (fb[i] - _mean[i]) / _std[i];
            dot += x * y; na += x * x; nb += y * y;
        }
        if (na <= 0 || nb <= 0) return 0f;
        return (float)((dot / Math.Sqrt(na * nb) + 1.0) / 2.0);
    }

    // ================= 选歌 =================

    public readonly record struct Pick(TrackItem Track, string Reason);

    /// <summary>从 candidates 里猜下一首。返回的 Reason 是给人看的一句理由。</summary>
    public Pick? PickNext(TrackItem? current, IReadOnlyList<TrackItem> candidates, IReadOnlyList<TrackItem> library)
    {
        EnsureWeights();
        var pool = candidates.Where(t => !t.IsMissing && !t.IsOnline && !ReferenceEquals(t, current)).ToList();
        if (pool.Count == 0) return null;
        if (current == null) return new Pick(pool[_rand.Next(pool.Count)], L.T("随机开始"));

        // 偶尔纯随机探索，防止模型只在已知口味里打转
        if (pool.Count > 3 && _rand.NextDouble() < 0.07)
            return new Pick(pool[_rand.Next(pool.Count)], L.T("随便走走，换个口味"));

        EnsureNorm(library);
        var w = Model.Weights;
        var scored = new List<(TrackItem T, double Logit, float[] Feat)>(pool.Count);
        foreach (var t in pool)
        {
            var f = Features(current, t, library);
            double z = w[F];
            for (int i = 0; i < F; i++) z += w[i] * f[i];
            scored.Add((t, z, f));
        }

        // 取分数最高的前 12 首，按 softmax（温度 0.6）抽样
        var top = scored.OrderByDescending(s => s.Logit).Take(12).ToList();
        double max = top[0].Logit;
        var weights = top.Select(s => Math.Exp((s.Logit - max) / 0.6)).ToList();
        double r = _rand.NextDouble() * weights.Sum();
        int idx = 0;
        for (; idx < weights.Count - 1; idx++) { r -= weights[idx]; if (r <= 0) break; }
        var chosen = top[idx];

        return new Pick(chosen.T, ReasonFor(chosen.Feat));
    }

    private static string ReasonFor(float[] f)
    {
        var w = Model.Weights;
        int best = -1; double bestV = 0.25;   // 贡献太小就不硬凑理由
        for (int i = 0; i < F; i++)
        {
            if (i == 9) continue;
            double c = w[i] * f[i];
            if (c > bestV) { bestV = c; best = i; }
        }
        return best < 0 ? L.T("按你的口味挑的") : FeatureNames[best];
    }
}
