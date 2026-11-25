using System;
using System.Collections.Generic;
using System.Linq;

namespace Klang_WinUI.Services;

/// <summary>从听歌记录里统计一段时间的听歌报告。</summary>
public sealed class ListeningReport
{
    public sealed record SongStat(ListenEntry Sample, int Plays, long Seconds);
    public sealed record ArtistStat(string Name, int Plays, long Seconds);

    public DateTime From { get; init; }          // 本地时间
    public DateTime To { get; init; }
    public int Plays { get; init; }
    public long Seconds { get; init; }
    public int Songs { get; init; }
    public int Artists { get; init; }
    public int ActiveDays { get; init; }
    public int NewSongs { get; init; }           // 这段时间里第一次听的歌
    public List<SongStat> TopSongs { get; init; } = new();
    public List<ArtistStat> TopArtists { get; init; } = new();
    public double[] Hours { get; init; } = new double[24];   // 各小时的收听秒数
    public (DateTime Day, long Seconds)? BusiestDay { get; init; }
    public (DateTime At, ListenEntry Entry)? LateNight { get; init; }   // 凌晨 0~5 点里最晚的一次
    public bool IsEmpty => Plays == 0;

    private static readonly string[] ArtistSeparators = { " / ", "/", "、", ";", "；", " & ", " feat. ", " ft. " };

    public static IEnumerable<string> SplitArtists(string artist) =>
        string.IsNullOrWhiteSpace(artist)
            ? Array.Empty<string>()
            : artist.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(a => a.Length > 0 && a != "未知歌手" && a != "Unknown Artist").Distinct();

    /// <summary>统计 [from, to) 内的记录；all 是全部记录（按时间排序），用来判断哪些歌是第一次听。</summary>
    public static ListeningReport Build(IReadOnlyList<ListenEntry> all, DateTime from, DateTime to)
    {
        long f = new DateTimeOffset(from).ToUnixTimeSeconds();
        long t = new DateTimeOffset(to).ToUnixTimeSeconds();
        var inRange = all.Where(e => e.Start >= f && e.Start < t).ToList();

        var firstSeen = new Dictionary<string, long>();
        foreach (var e in all) firstSeen.TryAdd(SongKey(e), e.Start);

        var songs = inRange.GroupBy(SongKey)
            .Select(g => new SongStat(g.Last(), g.Count(), g.Sum(e => (long)e.Seconds)))
            .OrderByDescending(s => s.Plays).ThenByDescending(s => s.Seconds)
            .ToList();

        var artistMap = new Dictionary<string, (int Plays, long Seconds)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in inRange)
            foreach (var a in SplitArtists(e.Artist))
            {
                artistMap.TryGetValue(a, out var v);
                artistMap[a] = (v.Plays + 1, v.Seconds + e.Seconds);
            }
        var artists = artistMap.Select(kv => new ArtistStat(kv.Key, kv.Value.Plays, kv.Value.Seconds))
            .OrderByDescending(a => a.Seconds).ThenByDescending(a => a.Plays).ToList();

        var hours = new double[24];
        var days = new Dictionary<DateTime, long>();
        (DateTime At, ListenEntry Entry)? late = null;
        foreach (var e in inRange)
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(e.Start).LocalDateTime;
            hours[local.Hour] += e.Seconds;
            days.TryGetValue(local.Date, out var s);
            days[local.Date] = s + e.Seconds;
            if (local.Hour < 5)
            {
                if (late == null || local.TimeOfDay > late.Value.At.TimeOfDay) late = (local, e);
            }
        }
        (DateTime, long)? busiest = days.Count == 0 ? null : days.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).First();

        return new ListeningReport
        {
            From = from,
            To = to,
            Plays = inRange.Count,
            Seconds = inRange.Sum(e => (long)e.Seconds),
            Songs = songs.Count,
            Artists = artistMap.Count,
            ActiveDays = days.Count,
            NewSongs = songs.Count(s => firstSeen.TryGetValue(SongKey(s.Sample), out var first) && first >= f),
            TopSongs = songs.Take(5).ToList(),
            TopArtists = artists.Take(5).ToList(),
            Hours = hours,
            BusiestDay = busiest,
            LateNight = late,
        };
    }

    /// <summary>同一首歌：本地和云端各自的 Key 不同，按“标题 + 歌手”合并，免得同一首歌分成两条。</summary>
    private static string SongKey(ListenEntry e) =>
        string.IsNullOrWhiteSpace(e.Title) ? e.Key : (e.Title.Trim() + "\u0001" + e.Artist.Trim()).ToLowerInvariant();

    /// <summary>收听最集中的三小时（起点）；没有数据时为 -1。</summary>
    public int PeakHour()
    {
        if (Seconds <= 0) return -1;
        int best = 0; double bestV = -1;
        for (int h = 0; h < 24; h++)
        {
            double v = Hours[h] + Hours[(h + 1) % 24] + Hours[(h + 2) % 24];
            if (v > bestV) { bestV = v; best = h; }
        }
        return best;
    }
}
