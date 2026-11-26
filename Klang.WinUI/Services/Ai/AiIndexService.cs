using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services.Ai;

/// <summary>
/// 曲库的 AI 索引（存在数据目录 ai\index 下，不放进播放状态文件）：
/// - 每首歌的 CLAP 音频向量：给声学地图 / 漫游 / 找相似用，并据此算自动标签；
/// - 每句歌词的 E5 向量：按意思搜歌词。
/// 后台按队列逐首计算（子进程里、低优先级），算一批存一次。
/// </summary>
public sealed class AiIndexService
{
    private static readonly Lazy<AiIndexService> _instance = new(() => new AiIndexService());
    public static AiIndexService Instance => _instance.Value;

    private readonly object _lock = new();
    private Dictionary<string, float[]> _audio = new();
    private Dictionary<string, LyricDoc> _lyrics = new();
    private readonly HashSet<string> _failed = new();
    private bool _loaded;
    private int _running;
    private int _version;

    public sealed record LyricEntry(float Time, string Text, float[] Emb);
    public sealed record LyricDoc(string Hash, List<LyricEntry> Lines);

    /// <summary>索引内容变了（新算出一批）。</summary>
    public event Action? Updated;
    public int Version => _version;

    private static string AudioFile => Path.Combine(AiCatalog.IndexDir, "clap.bin");
    // 向量是哪个模型文件算的（显卡版 fp32 / CPU 版 int8 算出的不能混用）；子进程每次返回实际用的文件名
    private string _audioTag = "";
    private static string LyricsFile => Path.Combine(AiCatalog.IndexDir, "lyrics.bin");

    // ================= 读写 =================

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_lock)
        {
            if (_loaded) return;
            try
            {
                if (File.Exists(AudioFile))
                {
                    using var r = new BinaryReader(File.OpenRead(AudioFile), Encoding.UTF8);
                    // 向量是哪个模型算的：换了模型（组件升级）就作废重算
                    if (r.ReadInt32() == 0x324C434B)   // "KCL2"
                    {
                        _audioTag = r.ReadString();
                        int n = r.ReadInt32();
                        for (int i = 0; i < n; i++)
                        {
                            var id = r.ReadString();
                            _audio[id] = ReadVec(r);
                        }
                    }
                }
            }
            catch (Exception ex) { App.LogError("AI index load (audio)", ex.Message); _audio.Clear(); }
            try
            {
                if (File.Exists(LyricsFile))
                {
                    using var r = new BinaryReader(File.OpenRead(LyricsFile), Encoding.UTF8);
                    if (r.ReadInt32() == 0x31594C4B)   // "KLY1"
                    {
                        int n = r.ReadInt32();
                        for (int i = 0; i < n; i++)
                        {
                            var id = r.ReadString();
                            var hash = r.ReadString();
                            int lines = r.ReadInt32();
                            var list = new List<LyricEntry>(lines);
                            for (int k = 0; k < lines; k++) list.Add(new LyricEntry(r.ReadSingle(), r.ReadString(), ReadVec(r)));
                            _lyrics[id] = new LyricDoc(hash, list);
                        }
                    }
                }
            }
            catch (Exception ex) { App.LogError("AI index load (lyrics)", ex.Message); _lyrics.Clear(); }
            _loaded = true;
        }
    }

    private static float[] ReadVec(BinaryReader r)
    {
        int d = r.ReadUInt16();
        var v = new float[d];
        for (int i = 0; i < d; i++) v[i] = (float)BitConverter.UInt16BitsToHalf(r.ReadUInt16());
        return v;
    }

    private static void WriteVec(BinaryWriter w, float[] v)
    {
        w.Write((ushort)v.Length);
        foreach (var x in v) w.Write(BitConverter.HalfToUInt16Bits((Half)x));
    }

    private void SaveAudio()
    {
        try
        {
            Directory.CreateDirectory(AiCatalog.IndexDir);
            KeyValuePair<string, float[]>[] items;
            lock (_lock) items = _audio.ToArray();
            var tmp = AudioFile + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
            {
                w.Write(0x324C434B); w.Write(_audioTag); w.Write(items.Length);
                foreach (var kv in items) { w.Write(kv.Key); WriteVec(w, kv.Value); }
            }
            File.Move(tmp, AudioFile, true);
        }
        catch (Exception ex) { App.LogError("AI index save (audio)", ex.Message); }
    }

    private void SaveLyrics()
    {
        try
        {
            Directory.CreateDirectory(AiCatalog.IndexDir);
            KeyValuePair<string, LyricDoc>[] items;
            lock (_lock) items = _lyrics.ToArray();
            var tmp = LyricsFile + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
            {
                w.Write(0x31594C4B); w.Write(items.Length);
                foreach (var kv in items)
                {
                    w.Write(kv.Key); w.Write(kv.Value.Hash); w.Write(kv.Value.Lines.Count);
                    foreach (var l in kv.Value.Lines) { w.Write(l.Time); w.Write(l.Text); WriteVec(w, l.Emb); }
                }
            }
            File.Move(tmp, LyricsFile, true);
        }
        catch (Exception ex) { App.LogError("AI index save (lyrics)", ex.Message); }
    }

    public void ClearAudio()
    {
        lock (_lock) { _audio.Clear(); _tagCache = null; }
        try { File.Delete(AudioFile); } catch { }
        Bump();
    }

    public void ClearLyrics()
    {
        lock (_lock) _lyrics.Clear();
        try { File.Delete(LyricsFile); } catch { }
        Bump();
    }

    private void Bump()
    {
        Interlocked.Increment(ref _version);
        Updated?.Invoke();
    }

    // ================= 查询 =================

    public float[]? AudioOf(TrackItem t)
    {
        EnsureLoaded();
        lock (_lock) return _audio.TryGetValue(t.Id, out var v) ? v : null;
    }

    public int AudioCount(IReadOnlyList<TrackItem> lib)
    {
        EnsureLoaded();
        lock (_lock) return lib.Count(t => _audio.ContainsKey(t.Id));
    }

    public int LyricsCount(IReadOnlyList<TrackItem> lib)
    {
        EnsureLoaded();
        lock (_lock) return lib.Count(t => _lyrics.ContainsKey(t.Id));
    }

    public static bool Indexable(TrackItem t) => !t.IsOnline && !t.IsMissing && !string.IsNullOrEmpty(t.FilePath);

    // ---------- 自动标签 ----------

    private Dictionary<string, float[]>? _labelEmb;   // 标签 / 场景 id → CLAP 文本向量
    private Dictionary<string, List<string>>? _tagCache;
    private int _tagCacheVersion = -1;

    /// <summary>标签和场景描述的 CLAP 文本向量（第一次用时让子进程算一遍，存到磁盘）。</summary>
    public async Task<Dictionary<string, float[]>?> LabelEmbeddingsAsync(CancellationToken ct = default)
    {
        if (_labelEmb != null) return _labelEmb;
        var file = Path.Combine(AiCatalog.IndexDir, "labels.bin");
        var labels = AiLabels.All.ToList();
        string sig = AiCatalog.Clap.Files.First(f => f.Role == "text").Sha256 + "|" + string.Join("|", labels.Select(l => l.Prompt));
        try
        {
            if (File.Exists(file))
            {
                using var r = new BinaryReader(File.OpenRead(file), Encoding.UTF8);
                if (r.ReadString() == sig)
                {
                    var d = new Dictionary<string, float[]>();
                    foreach (var l in labels) d[l.Id] = ReadVec(r);
                    return _labelEmb = d;
                }
            }
        }
        catch { }
        if (!AiService.Instance.Ready(AiCatalog.Clap)) return null;
        var embs = await AiService.Instance.ClapTextAsync(labels.Select(l => l.Prompt).ToList(), ct);
        if (embs.Length != labels.Count) return null;
        var dict = new Dictionary<string, float[]>();
        for (int i = 0; i < labels.Count; i++) dict[labels[i].Id] = embs[i];
        try
        {
            Directory.CreateDirectory(AiCatalog.IndexDir);
            using var w = new BinaryWriter(File.Create(file), Encoding.UTF8);
            w.Write(sig);
            foreach (var l in labels) WriteVec(w, dict[l.Id]);
        }
        catch { }
        return _labelEmb = dict;
    }

    /// <summary>
    /// 每首歌的标签（规则见下面的注释，阈值按实际曲库调过）。标签向量还没算好时返回空表。
    /// </summary>
    public Dictionary<string, List<string>> Tags(IReadOnlyList<TrackItem> lib)
    {
        EnsureLoaded();
        var labelEmb = _labelEmb;
        if (labelEmb == null) return new();
        lock (_lock)
        {
            if (_tagCache != null && _tagCacheVersion == _version) return _tagCache;
            var tracks = lib.Where(t => _audio.ContainsKey(t.Id)).ToList();
            var result = new Dictionary<string, List<string>>();
            if (tracks.Count == 0) return _tagCache = result;

            var tags = AiLabels.Tags;
            var sims = new float[tracks.Count, tags.Length];
            for (int i = 0; i < tracks.Count; i++)
            {
                var a = _audio[tracks[i].Id];
                for (int j = 0; j < tags.Length; j++) sims[i, j] = AiDsp.Dot(a, labelEmb[tags[j].Id]);
            }
            var mean = new float[tags.Length]; var std = new float[tags.Length];
            for (int j = 0; j < tags.Length; j++)
            {
                double s = 0, s2 = 0;
                for (int i = 0; i < tracks.Count; i++) { s += sims[i, j]; s2 += sims[i, j] * sims[i, j]; }
                mean[j] = (float)(s / tracks.Count);
                std[j] = (float)Math.Max(0.01, Math.Sqrt(Math.Max(0, s2 / tracks.Count - mean[j] * mean[j])));
            }
            int[] Cat(string c) => Enumerable.Range(0, tags.Length).Where(j => tags[j].Category == c).ToArray();
            var genre = Cat(AiLabels.Genre); var mood = Cat(AiLabels.Mood); var inst = Cat(AiLabels.Instrument);
            var vocal = new[] { "female", "male", "instrumental" }.Select(id => Array.FindIndex(tags, t => t.Id == id)).ToArray();
            int choir = Array.FindIndex(tags, t => t.Id == "choir");
            for (int i = 0; i < tracks.Count; i++)
            {
                int row = i;
                // 同类标签之间的 softmax 概率（CLAP 的温度约 1/100）：这首歌“更像哪一个”
                float[] Prob(int[] js)
                {
                    var e = js.Select(j => Math.Exp(100.0 * sims[row, j])).ToArray();
                    double sum = e.Sum();
                    return e.Select(x => (float)(x / sum)).ToArray();
                }
                float Z(int j) => (sims[row, j] - mean[j]) / std[j];
                var list = new List<string>();

                // 流派：绝对判断（概率够高），同时要比曲库平均更像——否则全库都会被打上同一个流派
                var gp = Prob(genre);
                list.AddRange(Enumerable.Range(0, genre.Length).OrderByDescending(k => gp[k]).Take(2)
                    .Where(k => gp[k] >= 0.25f && Z(genre[k]) >= 0.5f).Select(k => tags[genre[k]].Id));

                // 情绪 / 乐器：按在曲库里的突出程度排（原始相似度里“欢快”“合成器”这类总是偏高，直接比会挤掉“平静”“钢琴”）
                foreach (var (js, top) in new[] { (mood, 2), (inst, 3) })
                {
                    var p = Prob(js);
                    list.AddRange(Enumerable.Range(0, js.Length).OrderByDescending(k => Z(js[k])).Take(top)
                        .Where(k => Z(js[k]) >= 0.8f && p[k] >= 0.05f).Select(k => tags[js[k]].Id));
                }

                // 人声：女声 / 男声 / 纯音乐三选一；合唱单独判断
                var vp = Prob(vocal);
                int vbest = Enumerable.Range(0, vocal.Length).OrderByDescending(k => vp[k]).First();
                if (vp[vbest] >= 0.5f) list.Add(tags[vocal[vbest]].Id);
                if (Z(choir) > 1.6f) list.Add("choir");
                result[tracks[i].Id] = list;
            }            _tagCacheVersion = _version;
            return _tagCache = result;
        }
    }

    // ---------- 按描述找歌 ----------

    public sealed record Hit(TrackItem Track, float Score, string Detail, double Time = -1);

    private static bool HasCjk(string s) => s.Any(c => c >= 0x2E80 && c <= 0x9FFF || c >= 0xAC00 && c <= 0xD7AF || c >= 0x3040 && c <= 0x30FF);

    /// <summary>
    /// 用一句话描述找歌。CLAP 只懂英文：英文直接编码；中文等先匹配到最接近的几个标签 / 场景描述（有 E5 时按语义，
    /// 没有时按关键字），再用它们的英文描述加权合成查询向量。
    /// </summary>
    public async Task<(List<Hit> Hits, string Interpreted)> SearchBySoundAsync(string query, IReadOnlyList<TrackItem> lib, CancellationToken ct = default)
    {
        EnsureLoaded();
        query = query.Trim();
        if (query.Length == 0) return (new(), "");
        float[] q;
        string interpreted = "";
        if (!HasCjk(query))
        {
            q = (await AiService.Instance.ClapTextAsync(new[] { query }, ct))[0];
        }
        else
        {
            var labelEmb = await LabelEmbeddingsAsync(ct) ?? throw new InvalidOperationException(L.T("“听感分析”没有安装"));
            var labels = AiLabels.All.ToList();
            List<(AiLabels.Label L, float W)> picked;
            if (AiService.Instance.Ready(AiCatalog.TextEmbed))
            {
                var zhEmb = await ZhLabelEmbeddingsAsync(ct);
                var qe = (await AiService.Instance.E5Async(new[] { "query: " + query }, ct))[0];
                picked = labels.Select(l => (L: l, S: AiDsp.Dot(qe, zhEmb[l.Id])))
                    .OrderByDescending(x => x.S).Take(3)
                    .Select(x => (x.L, W: MathF.Exp((x.S - 0.8f) * 20f))).ToList();
            }
            else
            {
                picked = labels.Select(l => (L: l, S: l.Zh.Split(' ', '/', '（', '）').Count(k => k.Length > 0 && (query.Contains(k.Trim()) || k.Contains(query)))))
                    .Where(x => x.S > 0).OrderByDescending(x => x.S).Take(3)
                    .Select(x => (x.L, W: (float)x.S)).ToList();
                if (picked.Count == 0) throw new InvalidOperationException(L.T("没看懂这句描述：装上“歌词搜索”后中文更准，也可以直接用英文"));
            }
            q = new float[labelEmb.First().Value.Length];
            foreach (var (l, w) in picked) { var e = labelEmb[l.Id]; for (int i = 0; i < q.Length; i++) q[i] += e[i] * w; }
            AiDsp.Normalize(q);
            interpreted = string.Join("、", picked.Select(p => L.T(p.L.Zh.Split(' ')[0])));
        }

        List<Hit> hits;
        lock (_lock)
        {
            hits = lib.Where(t => _audio.ContainsKey(t.Id) && !t.IsMissing)
                .Select(t => new Hit(t, AiDsp.Dot(q, _audio[t.Id]), ""))
                .OrderByDescending(h => h.Score).Take(60).ToList();
        }
        return (hits, interpreted);
    }

    private Dictionary<string, float[]>? _zhEmb;

    private async Task<Dictionary<string, float[]>> ZhLabelEmbeddingsAsync(CancellationToken ct)
    {
        if (_zhEmb != null) return _zhEmb;
        var labels = AiLabels.All.ToList();
        var embs = await AiService.Instance.E5Async(labels.Select(l => "query: " + l.Zh).ToList(), ct);
        var d = new Dictionary<string, float[]>();
        for (int i = 0; i < labels.Count; i++) d[labels[i].Id] = embs[i];
        return _zhEmb = d;
    }

    /// <summary>按标签列出歌曲（按和标签的相似度排序）。</summary>
    public List<Hit> TracksWithTag(string tagId, IReadOnlyList<TrackItem> lib)
    {
        var tags = Tags(lib);
        var emb = _labelEmb != null && _labelEmb.TryGetValue(tagId, out var e) ? e : null;
        lock (_lock)
        {
            return lib.Where(t => tags.TryGetValue(t.Id, out var l) && l.Contains(tagId))
                .Select(t => new Hit(t, emb != null ? AiDsp.Dot(emb, _audio[t.Id]) : 0, ""))
                .OrderByDescending(h => h.Score).ToList();
        }
    }

    /// <summary>按 CLAP 向量找最像的歌（余弦）。</summary>
    public List<TrackItem> Similar(TrackItem track, IReadOnlyList<TrackItem> lib, int count)
    {
        var me = AudioOf(track);
        if (me == null) return new();
        lock (_lock)
        {
            return lib.Where(t => t.Id != track.Id && _audio.ContainsKey(t.Id) && !t.IsMissing)
                .OrderByDescending(t => AiDsp.Dot(me, _audio[t.Id])).Take(count).ToList();
        }
    }

    // ---------- 按意思搜歌词 ----------

    public async Task<List<Hit>> SearchLyricsAsync(string query, IReadOnlyList<TrackItem> lib, CancellationToken ct = default)
    {
        EnsureLoaded();
        query = query.Trim();
        if (query.Length == 0) return new();
        var q = (await AiService.Instance.E5Async(new[] { "query: " + query }, ct))[0];
        var byId = lib.Where(t => !t.IsMissing).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var best = new List<Hit>();
        lock (_lock)
        {
            foreach (var (id, doc) in _lyrics)
            {
                if (!byId.TryGetValue(id, out var t)) continue;
                LyricEntry? top = null; float topS = -1;
                foreach (var l in doc.Lines)
                {
                    float s = AiDsp.Dot(q, l.Emb);
                    // 原文里直接包含查询词的加一点分（记得原词时更准）
                    if (l.Text.Contains(query, StringComparison.OrdinalIgnoreCase)) s += 0.08f;
                    if (s > topS) { topS = s; top = l; }
                }
                if (top != null) best.Add(new Hit(t, topS, top.Text, top.Time));
            }
        }
        return best.OrderByDescending(h => h.Score).Take(40).ToList();
    }

    // ================= 后台索引 =================

    /// <summary>有新歌、组件刚装好、开关打开时调用：在后台补算缺的向量。</summary>
    public void Kick()
    {
        var ai = AiService.Instance;
        if (!ai.Enabled || !StateService.Instance.State.AiBackgroundIndex) return;
        if (!ai.Ready(AiCatalog.Clap) && !ai.Ready(AiCatalog.TextEmbed)) return;
        // 正在跑时又被叫（比如刚装好新组件）：记下来，这一轮跑完再来一轮
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) { _kickAgain = true; return; }
        _kickAgain = false;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000);
                EnsureLoaded();
                if (ai.Ready(AiCatalog.Clap)) await IndexAudioAsync();
                if (ai.Ready(AiCatalog.TextEmbed)) await IndexLyricsAsync();
                if (ai.Ready(AiCatalog.Clap)) { try { await LabelEmbeddingsAsync(); Bump(); } catch { } }
            }
            catch (Exception ex) { App.LogError("AI index", ex.Message); }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                if (_kickAgain) Kick();
            }
        });
    }

    private volatile bool _kickAgain;

    public bool IsIndexing => _running != 0;
    public string Status { get; private set; } = "";

    private bool StillWanted() => AiService.Instance.Enabled && StateService.Instance.State.AiBackgroundIndex;

    private async Task IndexAudioAsync()
    {
        var todo = LibraryService.Instance.Tracks.ToList().Where(Indexable).Where(t =>
        {
            lock (_lock) return !_audio.ContainsKey(t.Id) && !_failed.Contains("a:" + t.Id);
        }).ToList();
        int done = 0, sinceSave = 0;
        for (int qi = 0; qi < todo.Count; qi++)
        {
            var t = todo[qi];
            if (!StillWanted() || !AiService.Instance.Ready(AiCatalog.Clap)) break;
            Status = L.F("正在分析听感：{0} / {1}", done + 1, todo.Count);
            try
            {
                var r = await AiService.Instance.RequestAsync("clap_audio", new JsonObject
                {
                    ["path"] = t.FilePath,
                    ["start"] = t.IsCueTrack ? t.StartTime : 0,
                    ["end"] = t.IsCueTrack ? t.EndTime : 0,
                    ["duration"] = t.Duration,
                });
                var emb = AiService.ToFloats(r?["emb"]);
                var model = r?["model"]?.GetValue<string>() ?? "";
                if (model.Length > 0 && model != _audioTag)
                {
                    // 换了模型（比如装了显卡版）：旧向量作废，整个曲库重算
                    bool had;
                    lock (_lock) { had = _audio.Count > 0; _audio.Clear(); _tagCache = null; _audioTag = model; }
                    if (had)
                    {
                        CrashReporter.Note("AI：听感模型换成 " + model + "，重新分析");
                        lock (_lock) todo = LibraryService.Instance.Tracks.ToList().Where(Indexable).Where(x => x.Id != t.Id && !_failed.Contains("a:" + x.Id)).ToList();
                        done = 0;
                        qi = -1;   // 当前这首用新模型算好了，下面照常存；其余从头来
                        Bump();
                    }
                }
                lock (_lock)
                {
                    if (emb.Length > 0) _audio[t.Id] = emb;
                    else _failed.Add("a:" + t.Id);
                }
                if (emb.Length > 0 && ++sinceSave >= 25) { sinceSave = 0; SaveAudio(); Bump(); }
            }
            catch (Exception ex)
            {
                lock (_lock) _failed.Add("a:" + t.Id);
                if (ex is AiProcessException) { App.LogError("AI index (audio)", ex.Message); break; }
            }
            done++;
            await Task.Delay(30);
        }
        if (sinceSave > 0) { SaveAudio(); Bump(); }
        Status = "";
    }

    private static (string Hash, List<(float Time, string Text)> Lines)? ReadLyrics(TrackItem t)
    {
        List<LyricLine> lines;
        if (LyricsService.ReadChosen(t.Id) is { } chosen) lines = LyricsService.Instance.ParseLrc(chosen.Lrc, chosen.Trans, chosen.Roma);
        else if (t.IsCueTrack) return null;
        else lines = LyricsService.Instance.LoadLyrics(t.FilePath);
        if (lines.Count == 0 || lines.Count == 1 && lines[0].EndTime >= 9000) return null;
        var list = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => ((float)l.Time, Text: string.IsNullOrWhiteSpace(l.Sub) ? l.Text.Trim() : l.Text.Trim() + " / " + l.Sub.Trim()))
            .Where(x => x.Text.Length >= 2)
            .ToList();
        if (list.Count == 0) return null;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("\n", list.Select(x => x.Text)))))[..16];
        return (hash, list);
    }

    private async Task IndexLyricsAsync()
    {
        var lib = LibraryService.Instance.Tracks.ToList().Where(Indexable).ToList();
        int done = 0, sinceSave = 0;
        foreach (var t in lib)
        {
            if (!StillWanted() || !AiService.Instance.Ready(AiCatalog.TextEmbed)) break;
            done++;
            lock (_lock) if (_failed.Contains("l:" + t.Id)) continue;
            var ly = ReadLyrics(t);
            if (ly == null) continue;
            lock (_lock) if (_lyrics.TryGetValue(t.Id, out var doc) && doc.Hash == ly.Value.Hash) continue;
            Status = L.F("正在索引歌词：{0} / {1}", done, lib.Count);
            try
            {
                // 同一句重复多次（副歌）只算一次
                var distinct = ly.Value.Lines.GroupBy(x => x.Text).Select(g => g.First()).ToList();
                var embs = await AiService.Instance.E5Async(distinct.Select(x => "passage: " + x.Text).ToList());
                if (embs.Length != distinct.Count) continue;
                var entries = distinct.Select((x, i) => new LyricEntry(x.Time, x.Text, embs[i])).ToList();
                lock (_lock) _lyrics[t.Id] = new LyricDoc(ly.Value.Hash, entries);
                if (++sinceSave >= 20) { sinceSave = 0; SaveLyrics(); Bump(); }
            }
            catch (Exception ex)
            {
                lock (_lock) _failed.Add("l:" + t.Id);
                if (ex is AiProcessException) { App.LogError("AI index (lyrics)", ex.Message); break; }
            }
            await Task.Delay(20);
        }
        if (sinceSave > 0) { SaveLyrics(); Bump(); }
        Status = "";
    }

    /// <summary>
    /// 正在显示的歌词（可能来自网易云 / LRCLIB 在线补全，这些没有存在本地，后台扫描读不到）：
    /// 播放时把它交过来，没索引过或内容变了就补算。只收曲库里的本地歌曲。
    /// </summary>
    public void OfferLyrics(TrackItem t, IReadOnlyList<LyricLine> lines)
    {
        if (!Indexable(t) || !AiService.Instance.Ready(AiCatalog.TextEmbed) || !StateService.Instance.State.AiBackgroundIndex) return;
        if (lines.Count == 0 || lines.Count == 1 && lines[0].EndTime >= 9000) return;
        var list = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => (Time: (float)l.Time, Text: string.IsNullOrWhiteSpace(l.Sub) ? l.Text.Trim() : l.Text.Trim() + " / " + l.Sub.Trim()))
            .Where(x => x.Text.Length >= 2).ToList();
        if (list.Count < 3) return;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("\n", list.Select(x => x.Text)))))[..16];
        EnsureLoaded();
        lock (_lock) if (_lyrics.TryGetValue(t.Id, out var doc) && doc.Hash == hash) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var distinct = list.GroupBy(x => x.Text).Select(g => g.First()).ToList();
                var embs = await AiService.Instance.E5Async(distinct.Select(x => "passage: " + x.Text).ToList());
                if (embs.Length != distinct.Count) return;
                lock (_lock) _lyrics[t.Id] = new LyricDoc(hash, distinct.Select((x, i) => new LyricEntry(x.Time, x.Text, embs[i])).ToList());
                SaveLyrics();
                Bump();
            }
            catch { }
        });
    }

    /// <summary>某首歌的歌词变了（AI 识别 / 选择歌词）：重新索引它。</summary>
    public void Invalidate(TrackItem t)
    {
        lock (_lock) { _lyrics.Remove(t.Id); _failed.Remove("l:" + t.Id); }
        Kick();
    }
}
