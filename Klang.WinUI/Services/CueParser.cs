using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Klang_WinUI.Services;

public record CueTrackInfo(int Number, string Title, string Performer, double StartSeconds);

public record CueSheet(string AudioFilePath, string Album, string AlbumPerformer, string CuePath, List<CueTrackInfo> Tracks);

/// <summary>解析 .cue 分轨文件（支持 UTF-8 / GBK 编码），只处理单个 FILE 的常见整轨格式。</summary>
public static class CueParser
{
    private static readonly Regex QuotedOrWord = new("\"([^\"]*)\"|(\\S+)", RegexOptions.Compiled);
    private static readonly Regex IndexRegex = new(@"^INDEX\s+(\d+)\s+(\d+):(\d+):(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static CueParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static CueSheet? Parse(string cuePath)
    {
        try
        {
            var text = ReadText(cuePath);
            var dir = Path.GetDirectoryName(cuePath) ?? "";

            string audioFile = "";
            string albumTitle = "";
            string albumPerformer = "";
            var tracks = new List<CueTrackInfo>();

            int curNum = 0;
            string curTitle = "";
            string curPerformer = "";
            double? curStart = null;

            void FlushTrack()
            {
                if (curNum > 0 && curStart.HasValue)
                {
                    tracks.Add(new CueTrackInfo(curNum, curTitle, curPerformer, curStart.Value));
                }
                curNum = 0;
                curTitle = "";
                curPerformer = "";
                curStart = null;
            }

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim().TrimStart('﻿');
                if (line.Length == 0) continue;

                if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
                {
                    // 仅支持第一个 FILE；出现第二个 FILE（多文件 CUE）时停止
                    if (audioFile.Length > 0) break;
                    var m = QuotedOrWord.Match(line.Substring(5));
                    if (m.Success) audioFile = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                }
                else if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
                {
                    FlushTrack();
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var n)) curNum = n;
                }
                else if (line.StartsWith("TITLE ", StringComparison.OrdinalIgnoreCase))
                {
                    var v = Unquote(line.Substring(6));
                    if (curNum > 0) curTitle = v; else albumTitle = v;
                }
                else if (line.StartsWith("PERFORMER ", StringComparison.OrdinalIgnoreCase))
                {
                    var v = Unquote(line.Substring(10));
                    if (curNum > 0) curPerformer = v; else albumPerformer = v;
                }
                else if (curNum > 0)
                {
                    var im = IndexRegex.Match(line);
                    if (im.Success && im.Groups[1].Value == "01")
                    {
                        int mm = int.Parse(im.Groups[2].Value);
                        int ss = int.Parse(im.Groups[3].Value);
                        int ff = int.Parse(im.Groups[4].Value);
                        curStart = mm * 60 + ss + ff / 75.0;
                    }
                }
            }
            FlushTrack();

            if (audioFile.Length == 0 || tracks.Count == 0) return null;

            var audioPath = Path.IsPathRooted(audioFile) ? audioFile : Path.Combine(dir, audioFile);
            if (!File.Exists(audioPath))
            {
                // 常见情况：CUE 写的是 .wav / .ape，实际文件是同名 .flac
                var stem = Path.GetFileNameWithoutExtension(audioFile);
                string? found = null;
                foreach (var ext in new[] { ".flac", ".wav", ".ape", ".wv", ".m4a", ".mp3" })
                {
                    var p = Path.Combine(dir, stem + ext);
                    if (File.Exists(p)) { found = p; break; }
                }
                if (found == null) return null;
                audioPath = found;
            }

            return new CueSheet(Path.GetFullPath(audioPath), albumTitle, albumPerformer, cuePath, tracks);
        }
        catch
        {
            return null;
        }
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s.Substring(1, s.Length - 2);
        var m = QuotedOrWord.Match(s);
        return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : s;
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch
        {
            try { return Encoding.GetEncoding("GB18030").GetString(bytes); }
            catch { return Encoding.Default.GetString(bytes); }
        }
    }
}
