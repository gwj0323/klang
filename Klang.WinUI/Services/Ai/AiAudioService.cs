using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services.Ai;

/// <summary>人声分离（K 歌 / 导出伴奏）和歌词识别 / 对齐。结果缓存在数据目录 ai\karaoke 下。</summary>
public static class AiAudioService
{
    public sealed record Segment(double Start, double End, string Text);

    /// <summary>能做分离 / 识别：本地文件（或已经下载完的云端缓存），不是 CUE 分轨。</summary>
    public static bool CanProcess(TrackItem? t) =>
        t != null && !t.IsCueTrack && !string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath)
        && Cloud.ProgressiveDownload.Find(t.FilePath) == null;

    /// <summary>云端歌曲还在边下边播：下完后就能处理，返回这个下载（否则 null）。</summary>
    public static Cloud.ProgressiveDownload? PendingDownload(TrackItem? t) =>
        t == null || t.IsCueTrack || string.IsNullOrEmpty(t.FilePath) ? null : Cloud.ProgressiveDownload.Find(t.FilePath);

    private static string Key(TrackItem t)
    {
        long mtime = 0;
        try { mtime = File.GetLastWriteTimeUtc(t.FilePath).Ticks; } catch { }
        return string.Concat(t.Id.Split(Path.GetInvalidFileNameChars())) + "-" + (mtime % 0xFFFFFFF).ToString("x");
    }

    public static string InstrumentalPath(TrackItem t) => Path.Combine(AiCatalog.KaraokeDir, Key(t) + ".inst.wav");
    public static string VocalsPath(TrackItem t) => Path.Combine(AiCatalog.KaraokeDir, Key(t) + ".vocals.wav");

    // 分离时旁边有个 .partial 标记，写完才删：有标记的是没做完的（可能是上次中途退出留下的）
    public static bool IsSeparated(TrackItem t) =>
        File.Exists(InstrumentalPath(t)) && File.Exists(VocalsPath(t)) && !File.Exists(InstrumentalPath(t) + ".partial");

    /// <summary>一首歌正在进行的分离：已经写好的时间段（秒），主进程可以边分离边播放。</summary>
    public sealed class SeparationJob
    {
        private readonly object _lock = new();
        private readonly List<(double S, double E)> _ready = new();
        public Task<string> Task { get; internal set; } = null!;
        public string Instrumental { get; init; } = "";

        internal void Add(double s, double e) { lock (_lock) _ready.Add((s, e)); }

        /// <summary>[from, to] 这一段是不是都写好了（调用方把 to 限制在歌曲长度以内）。</summary>
        public bool Covers(double from, double to)
        {
            if (Task is { IsCompletedSuccessfully: true }) return true;
            lock (_lock)
            {
                double at = Math.Max(0, from);
                // 按起点排序后从 at 往后接，接不上就是没好
                foreach (var (s, e) in _ready.OrderBy(x => x.S))
                {
                    if (s > at + 0.01) break;
                    if (e > at) at = e;
                    if (at >= to - 0.01) return true;
                }
                return at >= to - 0.01;
            }
        }
    }

    // 同一首歌同时只分离一次（K 歌模式自动分离时用户又点了识别 / 导出）：后来的等前一个的结果
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SeparationJob> Running = new();

    /// <summary>这首歌正在分离的话返回它（可以查哪些段已经好了）。</summary>
    public static SeparationJob? JobFor(TrackItem t) => Running.TryGetValue(Key(t), out var j) ? j : null;

    /// <summary>
    /// 分离出伴奏和人声（已有缓存就直接返回）。返回伴奏路径。<paramref name="from"/>：先从哪里开始分离（秒，K 歌中途打开时是当前播放位置）。
    /// </summary>
    public static Task<string> SeparateAsync(TrackItem t, Action<double>? progress, CancellationToken ct, double from = 0)
    {
        var key = Key(t);
        if (Running.TryGetValue(key, out var existing))
        {
            if (from > 0) Prioritize(t, from);
            return existing.Task.WaitAsync(ct);
        }
        var job = new SeparationJob { Instrumental = InstrumentalPath(t) };
        if (!Running.TryAdd(key, job)) return Running[key].Task.WaitAsync(ct);
        job.Task = SeparateCoreAsync(t, job, from, progress, ct);
        _ = job.Task.ContinueWith(_ => Running.TryRemove(key, out SeparationJob? _), TaskScheduler.Default);
        return job.Task;
    }

    /// <summary>用户拖了进度：正在分离的这首先处理新位置。</summary>
    public static void Prioritize(TrackItem t, double from)
    {
        if (JobFor(t) is not { } job) return;
        AiService.Instance.Signal("prioritize", new JsonObject { ["inst"] = job.Instrumental, ["from"] = Math.Max(0, from) });
    }

    private static async Task<string> SeparateCoreAsync(TrackItem t, SeparationJob job, double from, Action<double>? progress, CancellationToken ct)
    {
        await Task.Yield();   // 先让调用方拿到 job.Task
        var inst = InstrumentalPath(t);
        var voc = VocalsPath(t);
        if (IsSeparated(t))
        {
            try { File.SetLastAccessTimeUtc(inst, DateTime.UtcNow); } catch { }
            return inst;
        }
        Directory.CreateDirectory(AiCatalog.KaraokeDir);
        TrimCache();
        await AiService.Instance.RequestAsync("separate", new JsonObject
        {
            ["path"] = t.FilePath, ["start"] = 0, ["end"] = 0, ["inst"] = inst, ["vocals"] = voc, ["from"] = Math.Max(0, from),
        }, progress, ct, msg =>
        {
            if (msg["ready"] is JsonArray r && r.Count == 2) job.Add(r[0]!.GetValue<double>(), r[1]!.GetValue<double>());
        });
        return inst;
    }

    /// <summary>缓存超过上限时，删掉最久没用过的分离结果。</summary>
    private static void TrimCache()
    {
        try
        {
            long limit = (long)Math.Max(256, StateService.Instance.State.AiKaraokeCacheMb) * 1024 * 1024;
            var files = new DirectoryInfo(AiCatalog.KaraokeDir).GetFiles("*.wav")
                .GroupBy(f => f.Name.Split('.')[0])
                .Select(g => (Files: g.ToList(), Size: g.Sum(f => f.Length), Last: g.Max(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc)))
                .OrderBy(g => g.Last).ToList();
            long total = files.Sum(g => g.Size);
            foreach (var g in files)
            {
                if (total <= limit) break;
                foreach (var f in g.Files) { try { f.Delete(); } catch { } }
                total -= g.Size;
            }
        }
        catch { }
    }

    public static long CacheBytes()
    {
        try { return Directory.Exists(AiCatalog.KaraokeDir) ? new DirectoryInfo(AiCatalog.KaraokeDir).GetFiles().Sum(f => f.Length) : 0; }
        catch { return 0; }
    }

    public static void ClearCache()
    {
        try { if (Directory.Exists(AiCatalog.KaraokeDir)) foreach (var f in Directory.GetFiles(AiCatalog.KaraokeDir)) { try { File.Delete(f); } catch { } } } catch { }
    }

    /// <summary>
    /// 识别歌词。装了人声分离组件时先分离（进度前一半），在纯人声上识别，准确得多、也不会把伴奏“听”成字。
    /// </summary>
    public static async Task<(string Language, List<Segment> Segments)> TranscribeAsync(TrackItem t, string? language, Action<double, string>? progress, CancellationToken ct)
    {
        string? vocals = null;
        bool sep = AiService.Instance.Ready(AiCatalog.Separator);
        if (sep)
        {
            await SeparateAsync(t, p => progress?.Invoke(p * 0.45, L.T("正在分离人声")), ct);
            vocals = VocalsPath(t);
        }
        var r = await AiService.Instance.RequestAsync("transcribe", new JsonObject
        {
            ["path"] = t.FilePath, ["start"] = 0, ["end"] = 0, ["vocals"] = vocals ?? "", ["language"] = language ?? "",
        }, p => progress?.Invoke(sep ? 0.45 + p * 0.55 : p, L.T("正在识别歌词")), ct);
        var segs = new List<Segment>();
        if (r?["segments"] is JsonArray arr)
            foreach (var s in arr)
                segs.Add(new Segment(s!["s"]!.GetValue<double>(), s["e"]!.GetValue<double>(), s["t"]!.GetValue<string>()));
        return (r?["language"]?.GetValue<string>() ?? "", segs);
    }

    /// <summary>识别结果转成 LRC（每句一行；太长的句子按标点 / 空格拆开，时间按字数分配）。</summary>
    public static string ToLrc(TrackItem t, IEnumerable<Segment> segs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[ti:{t.Title}]");
        sb.AppendLine($"[ar:{t.Artist}]");
        sb.AppendLine("[by:Klang AI (Whisper)]");
        foreach (var s in segs)
        {
            var parts = SplitLong(s.Text.Trim());
            int totalLen = Math.Max(1, parts.Sum(p => p.Length));
            double at = s.Start;
            foreach (var p in parts)
            {
                sb.AppendLine($"[{Stamp(at)}]{p}");
                at += (s.End - s.Start) * p.Length / totalLen;
            }
        }
        return sb.ToString();
    }

    private static List<string> SplitLong(string text)
    {
        // 中日韩文字每行不超过 18 字，西文不超过 48 个字符
        bool cjk = text.Count(c => c >= 0x2E80) > text.Length / 3;
        int max = cjk ? 18 : 48;
        if (text.Length <= max) return new List<string> { text };
        var outp = new List<string>();
        var cur = new StringBuilder();
        foreach (var token in System.Text.RegularExpressions.Regex.Split(text, @"(?<=[，。！？、,.!?;；\s])"))
        {
            if (cur.Length + token.Length > max && cur.Length > 0) { outp.Add(cur.ToString().Trim()); cur.Clear(); }
            cur.Append(token);
        }
        if (cur.Length > 0) outp.Add(cur.ToString().Trim());
        return outp.Where(x => x.Length > 0).ToList();
    }

    private static string Stamp(double sec)
    {
        if (sec < 0) sec = 0;
        int m = (int)(sec / 60);
        double s = sec - m * 60;
        return $"{m:00}:{s.ToString("00.00", CultureInfo.InvariantCulture)}";
    }

    private static string Norm(string s) =>
        new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static HashSet<string> Bigrams(string s)
    {
        var set = new HashSet<string>();
        for (int i = 0; i + 1 < s.Length; i++) set.Add(s.Substring(i, 2));
        if (s.Length == 1) set.Add(s);
        return set;
    }

    /// <summary>
    /// 用识别结果给现有歌词找整体偏移：每句识别结果和歌词里最像的一句配对（字符二元组相似度），
    /// 取配对时间差的中位数。配对太少时返回 null。返回值的含义同歌词偏移：显示时间 = 播放位置 + 偏移。
    /// </summary>
    public static (double Offset, int Matched)? AlignOffset(IList<LyricLine> lyrics, IList<Segment> segs)
    {
        var lines = lyrics.Where(l => !string.IsNullOrWhiteSpace(l.Text) && l.EndTime < 9000)
            .Select(l => (l.Time, B: Bigrams(Norm(l.Text)))).Where(x => x.B.Count >= 2).ToList();
        if (lines.Count < 3) return null;
        var diffs = new List<double>();
        foreach (var s in segs)
        {
            var b = Bigrams(Norm(s.Text));
            if (b.Count < 3) continue;
            double best = 0, bestTime = 0, second = 0;
            foreach (var l in lines)
            {
                int inter = b.Count(l.B.Contains);
                double sim = 2.0 * inter / (b.Count + l.B.Count);
                if (sim > best) { second = best; best = sim; bestTime = l.Time; }
                else if (sim > second) second = sim;
            }
            // 足够像，且比第二像的明显好（副歌重复时位置不确定，不用）
            if (best >= 0.45 && best - second >= 0.12 || best >= 0.7 && Math.Abs(best - second) > 0.001)
                diffs.Add(bestTime - s.Start);
        }
        if (diffs.Count < 3) return null;
        diffs.Sort();
        double median = diffs[diffs.Count / 2];
        // 只用离中位数近的再取一次平均，去掉配错的
        var close = diffs.Where(d => Math.Abs(d - median) < 1.5).ToList();
        if (close.Count < 3) return null;
        return (Math.Round(close.Average(), 2), close.Count);
    }
}
