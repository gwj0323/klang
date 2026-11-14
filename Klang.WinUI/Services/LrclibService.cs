using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services;

/// <summary>
/// LRCLIB（lrclib.net）开放歌词库：免费、不需要账号，按歌名 / 歌手 / 专辑 / 时长匹配带时间轴的歌词。
/// 请求里只有歌名、歌手、专辑和时长，用的是通用的客户端标识。找到的歌词存进本地缓存（不动你的音乐文件夹）。
/// </summary>
public static class LrclibService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Klang-WinUI (https://github.com/gwj0323/klang)");
        return c;
    }

    public static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klang", "LyricsCache");

    public static string CachePath(string trackId) => Path.Combine(CacheDir, trackId + ".lrc");

    public static string? ReadCache(string trackId)
    {
        try
        {
            var p = CachePath(trackId);
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }
        catch { return null; }
    }

    public static bool HasCache(string trackId)
    {
        try { return File.Exists(CachePath(trackId)); } catch { return false; }
    }

    public static void WriteCache(string trackId, string lrc)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(CachePath(trackId), lrc);
        }
        catch { }
    }

    /// <summary>取带时间轴的歌词。找不到返回 null；找到的会同时写进缓存。</summary>
    public static async Task<string?> FetchSyncedAsync(TrackItem t, CancellationToken ct)
    {
        if (t.IsOnline || string.IsNullOrWhiteSpace(t.Title)) return null;

        string title = t.Title.Trim();
        bool hasArtist = !string.IsNullOrWhiteSpace(t.Artist) && t.Artist != "未知歌手" && t.Artist != "Unknown Artist";
        bool hasAlbum = !string.IsNullOrWhiteSpace(t.Album) && t.Album != "未知专辑";
        int dur = (int)Math.Round(t.Duration);

        try
        {
            // 1) 精确匹配（要歌手）
            if (hasArtist && dur > 0)
            {
                string url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title)
                    + "&artist_name=" + Uri.EscapeDataString(t.Artist)
                    + (hasAlbum ? "&album_name=" + Uri.EscapeDataString(t.Album) : "")
                    + "&duration=" + dur;
                using var resp = await Http.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    var synced = Synced(doc.RootElement);
                    if (synced != null) { WriteCache(t.Id, synced); return synced; }
                }
            }

            // 2) 模糊搜索：按时长最接近的一条
            string q = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title)
                + (hasArtist ? "&artist_name=" + Uri.EscapeDataString(t.Artist) : "");
            using var resp2 = await Http.GetAsync(q, ct);
            if (!resp2.IsSuccessStatusCode) return null;
            using var doc2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync(ct));
            if (doc2.RootElement.ValueKind != JsonValueKind.Array) return null;

            var best = doc2.RootElement.EnumerateArray()
                .Select(e => (E: e, Synced: Synced(e), Dur: e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0))
                .Where(x => x.Synced != null)
                .Where(x => dur <= 0 || x.Dur <= 0 || Math.Abs(x.Dur - t.Duration) <= 6)   // 时长差太多的多半是别的版本
                .OrderBy(x => dur <= 0 || x.Dur <= 0 ? 0 : Math.Abs(x.Dur - t.Duration))
                .FirstOrDefault();
            if (best.Synced == null) return null;
            WriteCache(t.Id, best.Synced);
            return best.Synced;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public sealed record Hit(string Title, string Artist, string Album, double Duration, string Synced);

    /// <summary>按关键词搜索，返回所有带时间轴的结果（供用户自己挑选）。</summary>
    public static async Task<System.Collections.Generic.List<Hit>> SearchAsync(string keyword, CancellationToken ct)
    {
        var list = new System.Collections.Generic.List<Hit>();
        keyword = keyword.Trim();
        if (keyword.Length == 0) return list;
        using var resp = await Http.GetAsync("https://lrclib.net/api/search?q=" + Uri.EscapeDataString(keyword), ct);
        if (!resp.IsSuccessStatusCode) return list;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var synced = Synced(e);
            if (synced == null) continue;
            string S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            double dur = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            list.Add(new Hit(S("trackName"), S("artistName"), S("albumName"), dur, synced));
            if (list.Count >= 20) break;
        }
        return list;
    }

    private static string? Synced(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("instrumental", out var ins) && ins.ValueKind == JsonValueKind.True) return null;
        if (e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
        {
            var text = s.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }
}
