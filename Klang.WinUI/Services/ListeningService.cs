using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services;

/// <summary>听歌记录里的一条：某首歌从什么时候开始、实际听了多少秒。</summary>
public sealed class ListenEntry
{
    [JsonPropertyName("t")] public long Start { get; set; }          // 开始时间（Unix 秒，UTC）
    [JsonPropertyName("s")] public int Seconds { get; set; }         // 实际播放的秒数（暂停不算）
    [JsonPropertyName("k")] public string Key { get; set; } = "";    // 本地 = 曲目 Id；云端 = 来源:云端 id
    [JsonPropertyName("ti")] public string Title { get; set; } = "";
    [JsonPropertyName("ar")] public string Artist { get; set; } = "";
    [JsonPropertyName("al")] public string Album { get; set; } = "";
    [JsonPropertyName("d")] public double Duration { get; set; }
    [JsonPropertyName("c")] public string Cover { get; set; } = "";  // 记录时的封面文件（可能已被清理）
    [JsonPropertyName("src")] public string Source { get; set; } = "";  // 空 = 本地，否则为云端来源标识
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ListenEntry))]
internal partial class ListenJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 听歌记录：每首歌离开时（切歌、退出）追加一行到数据目录下的 listening.jsonl。
/// 只记本机，不联网；听歌报告就从这里统计。只有真的听了一会儿（≥ 30 秒或过半）才算一次。
/// </summary>
public sealed class ListeningService
{
    private static readonly Lazy<ListeningService> _instance = new(() => new ListeningService());
    public static ListeningService Instance => _instance.Value;

    private readonly string _path = Path.Combine(StateService.ResolveDataDirectory(), "listening.jsonl");
    private readonly object _lock = new();
    private readonly Stopwatch _played = new();
    private ListenEntry? _current;
    private TrackItem? _track;

    public string FilePath => _path;

    public static string KeyOf(TrackItem t) => t.IsOnline ? $"{t.CloudSource}:{t.CloudId}" : t.Id;

    /// <summary>开始播放一首歌（先把上一首记下来）。playing = 引擎此刻是否在出声。</summary>
    public void Begin(TrackItem track, bool playing)
    {
        lock (_lock)
        {
            EndCore();
            _track = track;
            _current = new ListenEntry
            {
                Start = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Key = KeyOf(track),
                Title = track.Title ?? "",
                Artist = track.Artist ?? "",
                Album = track.Album ?? "",
                Duration = track.Duration,
                Cover = track.OriginalCoverPath ?? "",
                Source = track.IsOnline ? track.CloudSource : "",
            };
            _played.Reset();
            if (playing) _played.Start();
        }
    }

    /// <summary>播放 / 暂停状态变化。启动时恢复的那首没走 Begin，第一次出声时从这里开始记。</summary>
    public void SetPlaying(bool playing, TrackItem? track)
    {
        lock (_lock)
        {
            if (_current == null)
            {
                if (playing && track != null) Begin(track, true);   // lock 可重入
                return;
            }
            if (playing) _played.Start(); else _played.Stop();
        }
    }

    /// <summary>结束当前这首（退出时调用）。</summary>
    public void End()
    {
        lock (_lock) EndCore();
    }

    private void EndCore()
    {
        var e = _current;
        var track = _track;
        _current = null;
        _track = null;
        _played.Stop();
        if (e == null) return;
        // 云端封面往往在开始播放后才下载好
        if (track != null && !string.IsNullOrEmpty(track.OriginalCoverPath)) e.Cover = track.OriginalCoverPath!;
        int s = (int)_played.Elapsed.TotalSeconds;
        if (e.Duration > 0) s = Math.Min(s, (int)(e.Duration * 2 + 60));   // 防止异常情况（比如时钟问题）记出离谱的长度
        bool counts = s >= 30 || (e.Duration > 0 && s >= 10 && s >= e.Duration * 0.5);
        if (!counts) return;
        e.Seconds = s;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(e, ListenJsonContext.Default.ListenEntry) + "\n", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            App.LogError("Listening log", ex.Message);
        }
    }

    /// <summary>读出全部记录（按时间顺序）；坏掉的行跳过。</summary>
    public List<ListenEntry> ReadAll()
    {
        var list = new List<ListenEntry>();
        try
        {
            if (!File.Exists(_path)) return list;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Length < 2) continue;
                try
                {
                    var e = JsonSerializer.Deserialize(line, ListenJsonContext.Default.ListenEntry);
                    if (e != null && e.Seconds > 0) list.Add(e);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            App.LogError("Read listening log", ex.Message);
        }
        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return list;
    }
}
