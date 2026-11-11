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

    /// <summary>当前曲库（含恢复自磁盘的曲目），UI 线程访问。</summary>
    public List<TrackItem> Tracks { get; } = new();

    // 启动时文件暂不可用（外接盘/NAS 未挂载）的曲目记录：不显示，但保存时原样保留，
    // 避免收藏、歌单引用被永久抹掉（对应 Qt 版 _UNAVAILABLE_ENTRIES）。
    private readonly Dictionary<string, SavedTrack> _unavailable = new();

    public LibraryService()
    {
        _coversCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klang",
            "CoversCache"
        );
        Directory.CreateDirectory(_coversCacheDir);
    }

    private static AppState State => StateService.Instance.State;

    #region 持久化恢复 / 保存

    /// <summary>从 player_state.json 恢复曲库。元数据未变化的曲目直接用保存的信息，跳过标签解析。</summary>
    public async Task<List<TrackItem>> RestoreFromStateAsync()
    {
        var saved = State.Tracks.ToList();
        var restored = await Task.Run(() =>
        {
            var list = new List<TrackItem>();
            var missing = new List<SavedTrack>();
            foreach (var s in saved)
            {
                if (string.IsNullOrEmpty(s.FilePath)) continue;
                if (!File.Exists(s.FilePath))
                {
                    missing.Add(s);
                    continue;
                }

                TrackItem? track = null;
                try
                {
                    long mtime = File.GetLastWriteTimeUtc(s.FilePath).Ticks;
                    if (s.CueTrackNum > 0 || (s.FileMtimeTicks == mtime && s.Duration > 0 && !string.IsNullOrEmpty(s.Title)))
                    {
                        track = FromSaved(s);
                    }
                    else
                    {
                        track = ParseTrack(s.FilePath);
                        if (track != null)
                        {
                            track.IsFavorite = s.IsFavorite;
                            track.AddedTimestamp = s.AddedTimestamp;
                        }
                    }
                }
                catch
                {
                    track = null;
                }

                if (track != null) list.Add(track);
            }
            return (list, missing);
        });

        Tracks.Clear();
        _unavailable.Clear();
        Tracks.AddRange(restored.list);
        foreach (var m in restored.missing) _unavailable[m.TrackId] = m;
        Reindex();
        return Tracks;
    }

    private static TrackItem FromSaved(SavedTrack s)
    {
        string? cover = !string.IsNullOrEmpty(s.CoverPath) && File.Exists(s.CoverPath) ? s.CoverPath : null;
        return new TrackItem
        {
            Id = string.IsNullOrEmpty(s.TrackId) ? TrackItem.MakeId(s.FilePath, s.CueTrackNum) : s.TrackId,
            Title = s.Title,
            Artist = s.Artist,
            Album = s.Album,
            Duration = s.Duration,
            FilePath = s.FilePath,
            CoverPath = cover,
            IsFavorite = s.IsFavorite,
            SampleRate = s.SampleRate,
            BitDepth = s.BitDepth,
            Bitrate = s.Bitrate,
            FormatBadge = s.FormatBadge,
            StartTime = s.StartTime,
            EndTime = s.EndTime,
            CueTrackPath = s.CueTrackPath,
            CueTrackNum = s.CueTrackNum,
            AddedTimestamp = s.AddedTimestamp,
            ReplayGainDb = s.ReplayGainDb,
            ReplayPeak = s.ReplayPeak,
            MetaChecked = s.MetaChecked,
            MetaNeteaseId = s.MetaNeteaseId
        };
    }

    /// <summary>把当前曲库写入状态并请求落盘。</summary>
    public void SaveLibrary()
    {
        var list = new List<SavedTrack>(Tracks.Count + _unavailable.Count);
        foreach (var t in Tracks)
        {
            long mtime = 0;
            try { mtime = File.GetLastWriteTimeUtc(t.FilePath).Ticks; } catch { }
            list.Add(new SavedTrack
            {
                TrackId = t.Id,
                Title = t.Title,
                Artist = t.Artist,
                Album = t.Album,
                Duration = t.Duration,
                FilePath = t.FilePath,
                CoverPath = t.CoverPath ?? "",
                IsFavorite = t.IsFavorite,
                SampleRate = t.SampleRate,
                BitDepth = t.BitDepth,
                Bitrate = t.Bitrate,
                FormatBadge = t.FormatBadge,
                StartTime = t.StartTime,
                EndTime = t.EndTime,
                CueTrackPath = t.CueTrackPath,
                CueTrackNum = t.CueTrackNum,
                AddedTimestamp = t.AddedTimestamp,
                ReplayGainDb = t.ReplayGainDb,
                ReplayPeak = t.ReplayPeak,
                MetaChecked = t.MetaChecked,
                MetaNeteaseId = t.MetaNeteaseId,
                FileMtimeTicks = mtime
            });
        }

        var present = new HashSet<string>(Tracks.Select(t => t.Id));
        foreach (var kv in _unavailable)
        {
            if (!present.Contains(kv.Key)) list.Add(kv.Value);
        }

        State.Tracks = list;
        StateService.Instance.RequestSave();
    }

    private void Reindex()
    {
        for (int i = 0; i < Tracks.Count; i++) Tracks[i].Index = i + 1;
    }

    #endregion

    #region 曲库操作

    /// <summary>扫描文件夹并合并到曲库（按文件路径/轨号去重），返回新增的曲目。</summary>
    public async Task<List<TrackItem>> AddFolderAsync(string folderPath)
    {
        var scanned = await ScanFolderAsync(folderPath);

        var known = new HashSet<string>(Tracks.Select(t => t.Id));
        var added = new List<TrackItem>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var t in scanned)
        {
            if (!known.Add(t.Id)) continue;
            t.AddedTimestamp = now;
            // 之前不可用的同 ID 曲目重新出现：沿用收藏状态
            if (_unavailable.Remove(t.Id, out var old)) t.IsFavorite = old.IsFavorite;
            Tracks.Add(t);
            added.Add(t);
        }

        if (!State.WatchedFolders.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
        {
            State.WatchedFolders.Add(folderPath);
        }

        Reindex();
        SaveLibrary();
        return added;
    }

    /// <summary>重新扫描所有已添加的文件夹，补入新文件。返回新增数量。</summary>
    public async Task<int> RescanWatchedFoldersAsync()
    {
        int total = 0;
        foreach (var folder in State.WatchedFolders.ToList())
        {
            if (!Directory.Exists(folder)) continue;
            total += (await AddFolderAsync(folder)).Count;
        }
        return total;
    }

    public void RemoveTrack(TrackItem track)
    {
        Tracks.Remove(track);
        _unavailable.Remove(track.Id);
        foreach (var list in State.CustomPlaylists.Values) list.RemoveAll(id => id == track.Id);
        Reindex();
        SaveLibrary();
    }

    public void ClearLibrary()
    {
        Tracks.Clear();
        _unavailable.Clear();
        State.WatchedFolders.Clear();
        State.LastTrackId = "";
        foreach (var key in State.CustomPlaylists.Keys.ToList()) State.CustomPlaylists[key].Clear();
        SaveLibrary();
    }

    public void RemoveWatchedFolder(string folder)
    {
        State.WatchedFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        StateService.Instance.RequestSave();
    }

    #endregion

    #region 歌单

    public IReadOnlyCollection<string> PlaylistNames => State.CustomPlaylists.Keys;

    public bool CreatePlaylist(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || State.CustomPlaylists.ContainsKey(name)) return false;
        State.CustomPlaylists[name] = new List<string>();
        StateService.Instance.RequestSave();
        return true;
    }

    public bool RenamePlaylist(string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || !State.CustomPlaylists.TryGetValue(oldName, out var ids)) return false;
        if (!string.Equals(oldName, newName, StringComparison.Ordinal) && State.CustomPlaylists.ContainsKey(newName)) return false;
        State.CustomPlaylists.Remove(oldName);
        State.CustomPlaylists[newName] = ids;
        StateService.Instance.RequestSave();
        return true;
    }

    public void DeletePlaylist(string name)
    {
        if (State.CustomPlaylists.Remove(name)) StateService.Instance.RequestSave();
    }

    public void AddToPlaylist(string name, TrackItem track)
    {
        if (!State.CustomPlaylists.TryGetValue(name, out var ids)) return;
        if (!ids.Contains(track.Id))
        {
            ids.Add(track.Id);
            StateService.Instance.RequestSave();
        }
    }

    public void RemoveFromPlaylist(string name, TrackItem track)
    {
        if (State.CustomPlaylists.TryGetValue(name, out var ids) && ids.Remove(track.Id))
        {
            StateService.Instance.RequestSave();
        }
    }

    public int PlaylistCount(string name)
    {
        if (!State.CustomPlaylists.TryGetValue(name, out var ids)) return 0;
        var present = new HashSet<string>(Tracks.Select(t => t.Id));
        return ids.Count(present.Contains);
    }

    /// <summary>按歌单内顺序返回曲目（曲库中不存在的引用被跳过）。</summary>
    public List<TrackItem> GetPlaylistTracks(string name)
    {
        var result = new List<TrackItem>();
        if (!State.CustomPlaylists.TryGetValue(name, out var ids)) return result;
        var map = Tracks.ToDictionary(t => t.Id);
        foreach (var id in ids)
        {
            if (map.TryGetValue(id, out var t)) result.Add(t);
        }
        return result;
    }

    #endregion

    #region 扫描与解析

    public async Task<List<TrackItem>> ScanFolderAsync(string folderPath)
    {
        return await Task.Run(() =>
        {
            var tracks = new List<TrackItem>();
            if (!Directory.Exists(folderPath)) return tracks;

            try
            {
                var allFiles = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // 1. CUE 分轨：被 CUE 引用的整轨音频不再作为单曲出现
                var cueAudio = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var cue in allFiles.Where(f => f.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var sheet = CueParser.Parse(cue);
                        if (sheet == null) continue;
                        var cueTracks = BuildCueTracks(sheet);
                        if (cueTracks.Count == 0) continue;
                        cueAudio.Add(sheet.AudioFilePath);
                        tracks.AddRange(cueTracks);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to parse cue {cue}: {ex.Message}");
                    }
                }

                // 2. 普通音频文件
                foreach (var file in allFiles.Where(f => SupportedExtensions.Contains(Path.GetExtension(f))))
                {
                    if (cueAudio.Contains(Path.GetFullPath(file))) continue;
                    try
                    {
                        var track = ParseTrack(file);
                        if (track != null) tracks.Add(track);
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

    private List<TrackItem> BuildCueTracks(CueSheet sheet)
    {
        var result = new List<TrackItem>();
        var baseTrack = ParseTrack(sheet.AudioFilePath);
        if (baseTrack == null) return result;

        double fileDuration = baseTrack.Duration;
        var cueTracks = sheet.Tracks.OrderBy(t => t.StartSeconds).ToList();
        for (int i = 0; i < cueTracks.Count; i++)
        {
            var ct = cueTracks[i];
            double start = ct.StartSeconds;
            double end = (i + 1 < cueTracks.Count) ? cueTracks[i + 1].StartSeconds : fileDuration;
            if (end <= start) end = 0;

            result.Add(new TrackItem
            {
                Id = TrackItem.MakeId(sheet.AudioFilePath, ct.Number),
                Title = string.IsNullOrWhiteSpace(ct.Title) ? $"Track {ct.Number:00}" : ct.Title,
                Artist = !string.IsNullOrWhiteSpace(ct.Performer) ? ct.Performer
                    : (!string.IsNullOrWhiteSpace(sheet.AlbumPerformer) ? sheet.AlbumPerformer : baseTrack.Artist),
                Album = !string.IsNullOrWhiteSpace(sheet.Album) ? sheet.Album : baseTrack.Album,
                Duration = end > start ? end - start : 0,
                FilePath = sheet.AudioFilePath,
                CoverPath = baseTrack.CoverPath,
                FormatBadge = baseTrack.FormatBadge,
                SampleRate = baseTrack.SampleRate,
                BitDepth = baseTrack.BitDepth,
                Bitrate = baseTrack.Bitrate,
                ReplayGainDb = baseTrack.ReplayGainDb,
                ReplayPeak = baseTrack.ReplayPeak,
                StartTime = start,
                EndTime = end,
                CueTrackPath = sheet.CuePath,
                CueTrackNum = ct.Number
            });
        }
        return result;
    }

    public TrackItem? ParseTrack(string filePath, int index = 1)
    {
        if (!File.Exists(filePath)) return null;
        filePath = Path.GetFullPath(filePath);

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
        double? rgDb = null;
        double? rgPeak = null;

        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var tag = tagFile.Tag;

            if (tag != null)
            {
                if (!double.IsNaN(tag.ReplayGainTrackGain)) rgDb = tag.ReplayGainTrackGain;
                if (!double.IsNaN(tag.ReplayGainTrackPeak)) rgPeak = tag.ReplayGainTrackPeak;
            }
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
            Id = TrackItem.MakeId(filePath),
            Title = title,
            Artist = artist,
            Album = album,
            Duration = duration,
            FilePath = filePath,
            CoverPath = coverPath,
            FormatBadge = badge,
            SampleRate = sampleRate,
            BitDepth = bitDepth,
            Bitrate = bitrate,
            ReplayGainDb = rgDb,
            ReplayPeak = rgPeak
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

    #endregion
}
