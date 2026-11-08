using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services;

public class LyricsService
{
    private static readonly Lazy<LyricsService> _instance = new(() => new LyricsService());
    public static LyricsService Instance => _instance.Value;

    private static readonly Regex LrcTagRegex = new(@"\[(\d{1,2}):(\d{1,2}(?:\.\d{1,3})?)\]", RegexOptions.Compiled);
    private static readonly Regex HeaderNoiseRegex = new(@"^\[(ti|ar|al|by|offset|kana|re|ve):.*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public record RawLrcEntry(double Time, string Text);

    public List<LyricLine> LoadLyrics(string? audioFilePath)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath))
        {
            return CreateEmptyPlaceholder("♪ 纯音乐 / 暂无歌词 ♪");
        }

        string dir = Path.GetDirectoryName(audioFilePath) ?? "";
        string baseName = Path.GetFileNameWithoutExtension(audioFilePath);

        // 1. 尝试同目录下同名 .lrc 文件
        string localLrcPath = Path.Combine(dir, $"{baseName}.lrc");
        string localTransPath = Path.Combine(dir, $"{baseName}.trans.lrc");
        if (!File.Exists(localTransPath))
        {
            localTransPath = Path.Combine(dir, $"{baseName}.zh.lrc");
        }

        if (File.Exists(localLrcPath))
        {
            try
            {
                string lrcText = File.ReadAllText(localLrcPath);
                string? transText = File.Exists(localTransPath) ? File.ReadAllText(localTransPath) : null;
                var parsed = ParseLrc(lrcText, transText);
                if (parsed.Count > 0) return parsed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading local lrc: {ex.Message}");
            }
        }

        // 2. 尝试从音频文件的元数据标签 (ID3v2 USLT / FLAC VORBISCOMMENT) 读取内嵌歌词
        try
        {
            using var tagFile = TagLib.File.Create(audioFilePath);
            string? embedded = tagFile.Tag?.Lyrics;
            if (!string.IsNullOrWhiteSpace(embedded))
            {
                var parsed = ParseLrc(embedded);
                if (parsed.Count > 0) return parsed;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error reading embedded lyrics: {ex.Message}");
        }

        return CreateEmptyPlaceholder("♪ 享受纯粹音乐沉浸 ♪");
    }

    public List<LyricLine> ParseLrc(string primaryLrc, string? transLrc = null)
    {
        var primaryEntries = ParseRawEntries(primaryLrc);
        if (primaryEntries.Count == 0)
        {
            return CreateEmptyPlaceholder("♪ 纯音乐 / 暂无歌词 ♪");
        }

        var transEntries = !string.IsNullOrWhiteSpace(transLrc) ? ParseRawEntries(transLrc) : new List<RawLrcEntry>();

        var rawList = new List<(double Time, string Text, string Sub)>();
        foreach (var entry in primaryEntries)
        {
            double t = entry.Time;
            string text = entry.Text;
            string mainText = text;
            string subText = "";

            if (text.Contains("//"))
            {
                var parts = text.Split("//", 2);
                mainText = parts[0].Trim();
                subText = parts[1].Trim();
            }
            else if (transEntries.Count > 0)
            {
                var bestTrans = transEntries
                    .Where(te => Math.Abs(te.Time - t) <= 0.6)
                    .OrderBy(te => Math.Abs(te.Time - t))
                    .Select(te => te.Text)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(bestTrans))
                {
                    subText = bestTrans;
                }
            }

            rawList.Add((t, mainText, subText));
        }

        var result = new List<LyricLine>();
        for (int i = 0; i < rawList.Count; i++)
        {
            var item = rawList[i];
            double nextTime = (i + 1 < rawList.Count) ? rawList[i + 1].Time : item.Time + 6.0;
            double endTime = Math.Max(item.Time + 1.2, nextTime - 0.1);

            result.Add(new LyricLine
            {
                Index = i,
                Time = item.Time,
                EndTime = endTime,
                Text = item.Text,
                Sub = item.Sub
            });
        }

        return result;
    }

    private List<RawLrcEntry> ParseRawEntries(string text)
    {
        var entries = new List<RawLrcEntry>();
        if (string.IsNullOrWhiteSpace(text)) return entries;

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || HeaderNoiseRegex.IsMatch(line))
                continue;

            var matches = LrcTagRegex.Matches(line);
            if (matches.Count == 0) continue;

            string lyricText = LrcTagRegex.Replace(line, "").Trim();

            foreach (Match match in matches)
            {
                if (double.TryParse(match.Groups[1].Value, out double minutes) &&
                    double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
                {
                    double totalSec = minutes * 60.0 + seconds;
                    entries.Add(new RawLrcEntry(totalSec, lyricText));
                }
            }
        }

        return entries.OrderBy(e => e.Time).ToList();
    }

    public int FindActiveIndex(IList<LyricLine> lyrics, double currentTime)
    {
        if (lyrics == null || lyrics.Count == 0) return -1;

        int active = -1;
        for (int i = 0; i < lyrics.Count; i++)
        {
            if (currentTime >= lyrics[i].Time)
            {
                active = i;
            }
            else
            {
                break;
            }
        }

        return active;
    }

    private List<LyricLine> CreateEmptyPlaceholder(string message)
    {
        return new List<LyricLine>
        {
            new LyricLine { Index = 0, Time = 0.0, EndTime = 9999.0, Text = message, Sub = "Klang · 沉浸式听音空间" }
        };
    }
}
