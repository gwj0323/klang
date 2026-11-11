using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Klang_WinUI.Models;
using Klang_WinUI.Services.Netease;

namespace Klang_WinUI.Services;

/// <summary>
/// 本地歌曲缺封面 / 专辑 / 歌手时，用网易云搜索自动补全，并记下匹配到的歌曲 ID 供取歌词。
/// 后台逐首排队（每首间隔约 1.5 秒），每首歌只尝试一次。
/// </summary>
public sealed class MetadataService
{
    private static readonly Lazy<MetadataService> _instance = new(() => new MetadataService());
    public static MetadataService Instance => _instance.Value;

    private const string UnknownArtist = "未知歌手";
    private const string UnknownAlbum = "未知专辑";

    private readonly Channel<TrackItem> _queue = Channel.CreateUnbounded<TrackItem>();
    private readonly HashSet<string> _queued = new();
    private readonly string _coverDir;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private bool _started;

    /// <summary>某首歌的信息已补全（后台线程触发）。</summary>
    public event Action<TrackItem>? TrackEnriched;

    private static bool Enabled => StateService.Instance.State.AutoFetchMetadata;

    private MetadataService()
    {
        _coverDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klang", "covers");
        Directory.CreateDirectory(_coverDir);
    }

    public static bool NeedsCover(TrackItem t) => string.IsNullOrEmpty(t.CoverPath) || !File.Exists(t.CoverPath);

    public static bool NeedsEnrich(TrackItem t) =>
        !t.IsOnline && !t.IsCueTrack && !t.MetaChecked &&
        (NeedsCover(t) || t.Artist == UnknownArtist || t.Album == UnknownAlbum);

    /// <summary>把需要补全的曲目加入后台队列。</summary>
    public void Enqueue(IEnumerable<TrackItem> tracks)
    {
        if (!Enabled) return;
        foreach (var t in tracks)
        {
            if (!NeedsEnrich(t)) continue;
            lock (_queued) { if (!_queued.Add(t.Id)) continue; }
            _queue.Writer.TryWrite(t);
        }
        if (!_started)
        {
            _started = true;
            _ = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        await foreach (var t in _queue.Reader.ReadAllAsync())
        {
            if (!Enabled) { lock (_queued) _queued.Remove(t.Id); continue; }
            try
            {
                await EnrichAsync(t);
            }
            catch { }
            lock (_queued) _queued.Remove(t.Id);
            await Task.Delay(1500);
        }
    }

    #region 匹配

    private static string Normalize(string s)
    {
        s = Regex.Replace(s ?? "", @"[\(\[（【].*?[\)\]）】]", "");
        s = Regex.Replace(s, @"[\s\-_·・,，.。!！?？'""“”]+", "");
        return s.ToLowerInvariant();
    }

    /// <summary>从曲目推出搜索词；文件名常见 “歌手 - 歌名”，整串交给搜索即可。</summary>
    private static string Keyword(TrackItem t)
    {
        var title = Regex.Replace(t.Title, @"\s*[\(\[（【].*?[\)\]）】]", "").Trim();
        return t.Artist != UnknownArtist ? $"{title} {t.Artist}" : title;
    }

    private static int Score(TrackItem local, TrackItem c)
    {
        string lt = Normalize(local.Title), ct = Normalize(c.Title);
        if (lt.Length == 0 || ct.Length == 0) return 0;

        int score = 0;
        if (lt == ct) score += 60;
        else if (lt.Contains(ct) || ct.Contains(lt)) score += 40; // 文件名里夹带歌手名的情况
        else return 0;

        if (local.Duration > 0 && c.Duration > 0)
        {
            double diff = Math.Abs(local.Duration - c.Duration);
            if (diff <= 3) score += 30;
            else if (diff <= 8) score += 10;
            else return 0; // 时长差太多，基本不是同一首
        }

        if (local.Artist != UnknownArtist)
        {
            string la = Normalize(local.Artist), ca = Normalize(c.Artist);
            if (ca.Contains(la) || la.Contains(ca)) score += 15;
        }
        else if (Normalize(local.Title).Contains(Normalize(c.Artist.Split('/')[0])))
        {
            score += 10; // 文件名里含有歌手名
        }
        return score;
    }

    private async Task<TrackItem?> FindMatchAsync(TrackItem t)
    {
        var results = await NeteaseService.Instance.SearchAsync(Keyword(t), 8);
        return results.Select(c => (c, s: Score(t, c))).Where(x => x.s >= 60)
            .OrderByDescending(x => x.s).Select(x => x.c).FirstOrDefault();
    }

    #endregion

    /// <summary>补全封面 / 专辑 / 歌手。成功补到任何一项返回 true。无论结果都标记为已尝试。</summary>
    public async Task<bool> EnrichAsync(TrackItem t)
    {
        if (!NeedsEnrich(t)) return false;

        bool changed = false;
        try
        {
            var m = await FindMatchAsync(t);
            if (m != null)
            {
                t.MetaNeteaseId = m.NeteaseId;
                if (t.Album == UnknownAlbum && !string.IsNullOrWhiteSpace(m.Album)) { t.Album = m.Album; changed = true; }
                if (t.Artist == UnknownArtist && m.Artist != UnknownArtist) { t.Artist = m.Artist; changed = true; }
                if (NeedsCover(t) && !string.IsNullOrEmpty(m.CoverUrl))
                {
                    var path = await DownloadCoverAsync(t, m.CoverUrl);
                    if (path != null) { t.CoverPath = path; changed = true; }
                }
            }
            t.MetaChecked = true; // 只在请求成功（有无结果均可）后标记，网络异常会抛出而不标记
        }
        catch
        {
            return false;
        }

        LibraryService.Instance.SaveLibrary();
        if (changed) TrackEnriched?.Invoke(t);
        return changed;
    }

    private async Task<string?> DownloadCoverAsync(TrackItem t, string url)
    {
        try
        {
            var u = url.Contains('?') ? url : url + "?param=800y800";
            var bytes = await _http.GetByteArrayAsync(u);
            if (bytes.Length < 2048) return null;
            var path = Path.Combine(_coverDir, $"meta_{t.Id}.jpg");
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取在线歌词（本地没有歌词时用）：必要时先搜索匹配，返回 null 表示没找到。</summary>
    public async Task<NeteaseService.LyricBundle?> FetchLyricsAsync(TrackItem t, CancellationToken ct)
    {
        if (!Enabled || t.IsOnline || t.IsCueTrack) return null;
        try
        {
            if (string.IsNullOrEmpty(t.MetaNeteaseId))
            {
                var m = await FindMatchAsync(t);
                if (m == null) return null;
                t.MetaNeteaseId = m.NeteaseId;
                LibraryService.Instance.SaveLibrary();
            }
            ct.ThrowIfCancellationRequested();
            var lb = await NeteaseService.Instance.GetLyricsAsync(t.MetaNeteaseId, ct);
            return string.IsNullOrWhiteSpace(lb.Lrc) ? null : lb;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
