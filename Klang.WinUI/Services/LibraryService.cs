using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services;

public class LibraryService
{
    private static readonly Lazy<LibraryService> _instance = new(() => new LibraryService());
    public static LibraryService Instance => _instance.Value;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".ape", ".mp3", ".m4a", ".aac", ".ogg", ".wma"
    };

    private readonly string _coversCacheDir;

    public LibraryService()
    {
        _coversCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klang",
            "CoversCache"
        );
        Directory.CreateDirectory(_coversCacheDir);
    }

    public async Task<List<TrackItem>> ScanFolderAsync(string folderPath)
    {
        return await Task.Run(() =>
        {
            var tracks = new List<TrackItem>();
            if (!Directory.Exists(folderPath)) return tracks;

            try
            {
                var files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                    .OrderBy(f => f);

                int index = 1;
                foreach (var file in files)
                {
                    try
                    {
                        var track = ParseTrack(file, index++);
                        if (track != null)
                        {
                            tracks.Add(track);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to parse {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Scan folder error: {ex.Message}");
            }

            return tracks;
        });
    }

    public TrackItem? ParseTrack(string filePath, int index = 1)
    {
        if (!File.Exists(filePath)) return null;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);

        string title = fileNameWithoutExt;
        string artist = "未知歌手";
        string album = "未知专辑";
        double duration = 0;
        int sampleRate = 44100;
        int bitDepth = 16;
        int bitrate = 0;
        string? coverPath = null;

        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var tag = tagFile.Tag;
            var prop = tagFile.Properties;

            if (!string.IsNullOrWhiteSpace(tag?.Title)) title = tag.Title;
            if (tag?.Performers != null && tag.Performers.Length > 0 && !string.IsNullOrWhiteSpace(tag.Performers[0]))
                artist = string.Join(", ", tag.Performers);
            else if (!string.IsNullOrWhiteSpace(tag?.FirstPerformer))
                artist = tag.FirstPerformer;

            if (!string.IsNullOrWhiteSpace(tag?.Album)) album = tag.Album;

            if (prop != null)
            {
                duration = prop.Duration.TotalSeconds;
                sampleRate = prop.AudioSampleRate;
                bitDepth = prop.BitsPerSample > 0 ? prop.BitsPerSample : 16;
                bitrate = prop.AudioBitrate;
            }

            // 提取内嵌专辑封面图
            if (tag?.Pictures != null && tag.Pictures.Length > 0)
            {
                coverPath = SaveCoverToCache(tag.Pictures[0], filePath);
            }

            // 若无内嵌封面，检测同目录下常见封面图文件 (cover.jpg, folder.jpg 等)
            if (string.IsNullOrEmpty(coverPath))
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    string[] candidates = { "cover.jpg", "cover.png", "folder.jpg", "folder.png", "front.jpg", "front.png" };
                    foreach (var c in candidates)
                    {
                        var p = Path.Combine(dir, c);
                        if (File.Exists(p))
                        {
                            coverPath = p;
                            break;
                        }
                    }
                }
            }
        }
        catch
        {
            // 如果 TagLibSharp 解析失败，依然保留基本文件名
        }

        string badge = FormatAudioBadge(ext, bitDepth, sampleRate, bitrate);

        return new TrackItem
        {
            Index = index,
            Title = title,
            Artist = artist,
            Album = album,
            Duration = duration,
            FilePath = filePath,
            CoverPath = coverPath,
            FormatBadge = badge,
            SampleRate = sampleRate,
            BitDepth = bitDepth,
            Bitrate = bitrate
        };
    }

    private string FormatAudioBadge(string ext, int bitDepth, int sampleRate, int bitrate)
    {
        string extUpper = ext.TrimStart('.').ToUpperInvariant();
        if (extUpper == "FLAC" || extUpper == "WAV")
        {
            if (sampleRate >= 88200 || bitDepth >= 24)
            {
                return $"Hi-Res {sampleRate / 1000}k/{bitDepth}b";
            }
            return $"{extUpper} {bitDepth}bit";
        }

        if (extUpper == "MP3")
        {
            return bitrate >= 300 ? "MP3 320k" : $"MP3 {bitrate}k";
        }

        return extUpper;
    }

    private string? SaveCoverToCache(TagLib.IPicture picture, string audioFilePath)
    {
        try
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(audioFilePath))).Substring(0, 16);
            string ext = picture.MimeType.Contains("png") ? ".png" : ".jpg";
            string cachePath = Path.Combine(_coversCacheDir, $"cover_{hash}{ext}");

            if (!File.Exists(cachePath))
            {
                File.WriteAllBytes(cachePath, picture.Data.Data);
            }

            return cachePath;
        }
        catch
        {
            return null;
        }
    }
}
