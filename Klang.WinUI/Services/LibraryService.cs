using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
                    // 文件丢了（移走 / 外接盘没插）：仍留在列表里，标灰提示，收藏和歌单引用都不丢
                    var ghost = FromSaved(s);
                    ghost.IsMissing = true;
                    list.Add(ghost);
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
                            track.PlayCount = s.PlayCount;
                            track.LastPlayedTimestamp = s.LastPlayedTimestamp;
                            track.MetaChecked = s.MetaChecked;
                            track.MetaNeteaseId = s.MetaNeteaseId;
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
            MetaNeteaseId = s.MetaNeteaseId,
            PlayCount = s.PlayCount,
            LastPlayedTimestamp = s.LastPlayedTimestamp,
            FileMtimeTicks = s.FileMtimeTicks,
            AudioFeat = s.AudioFeat,
            AuthVerdict = s.AuthVerdict ?? "",
            AuthCutoffHz = s.AuthCutoffHz,
            AuthBits = s.AuthBits,
            RhythmDone = s.RhythmDone,
            Bpm = s.Bpm,
            FirstBeat = s.FirstBeat,
            LastBeat = s.LastBeat,
            MixEnd = s.MixEnd,
            MusicKey = s.MusicKey ?? "",
            Camelot = s.Camelot ?? ""
        };
    }

    /// <summary>把当前曲库写入状态并请求落盘。</summary>
    public void SaveLibrary()
    {
        var list = new List<SavedTrack>(Tracks.Count + _unavailable.Count);
        foreach (var t in Tracks)
        {
            long mtime = t.FileMtimeTicks;
            if (mtime == 0 && !t.IsMissing) { try { mtime = File.GetLastWriteTimeUtc(t.FilePath).Ticks; t.FileMtimeTicks = mtime; } catch { } }
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
                PlayCount = t.PlayCount,
                LastPlayedTimestamp = t.LastPlayedTimestamp,
                AudioFeat = t.AudioFeat,
                AuthVerdict = t.AuthVerdict,
                AuthCutoffHz = t.AuthCutoffHz,
                AuthBits = t.AuthBits,
                RhythmDone = t.RhythmDone,
                Bpm = t.Bpm,
                FirstBeat = t.FirstBeat,
                LastBeat = t.LastBeat,
                MixEnd = t.MixEnd,
                MusicKey = t.MusicKey,
                Camelot = t.Camelot,
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
        var byId = new Dictionary<string, TrackItem>();
        foreach (var t in Tracks) byId[t.Id] = t;
        var scanned = await ScanFolderAsync(folderPath, byId);

        var known = new HashSet<string>(byId.Keys);
        var added = new List<TrackItem>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var t in scanned)
        {
            if (byId.TryGetValue(t.Id, out var existing))
            {
                // 文件改过：把新读到的标签 / 音质信息并回已有曲目（保留收藏、播放统计、歌单引用）
                existing.Title = t.Title;
                existing.Artist = t.Artist;
                existing.Album = t.Album;
                existing.Duration = t.Duration;
                existing.SampleRate = t.SampleRate;
                existing.BitDepth = t.BitDepth;
                existing.Bitrate = t.Bitrate;
                existing.FormatBadge = t.FormatBadge;
                existing.FileMtimeTicks = t.FileMtimeTicks;
                existing.AuthVerdict = ""; existing.RhythmDone = false;          // 文件改过：声学特征和真伪检测都要重新算
                existing.AudioFeat = null;
                QualityAnalysisService.Instance.Enqueue(new[] { existing });
                RhythmAnalysisService.Instance.Enqueue(new[] { existing });
                AudioFeatureService.Instance.Enqueue(new[] { existing });
                if (!string.IsNullOrEmpty(t.CoverPath)) existing.CoverPath = t.CoverPath;
                existing.IsMissing = false;
                continue;
            }
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
        RestartWatching();
        AudioFeatureService.Instance.Enqueue(added);
        QualityAnalysisService.Instance.Enqueue(added);
        RhythmAnalysisService.Instance.Enqueue(added);
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

    /// <summary>和 RescanWatchedFoldersAsync 一样，但返回新增的曲目本身（用来排队补全元数据）。</summary>
    public async Task<List<TrackItem>> RescanWatchedFoldersAsyncCollect()
    {
        var all = new List<TrackItem>();
        foreach (var folder in State.WatchedFolders.ToList())
        {
            if (!Directory.Exists(folder)) continue;
            all.AddRange(await AddFolderAsync(folder));
        }
        return all;
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
        RestartWatching();
    }

    /// <summary>批量从曲库移除（只改曲库，不动磁盘上的文件），只保存一次。</summary>
    public void RemoveTracks(IEnumerable<TrackItem> tracks)
    {
        var ids = new HashSet<string>(tracks.Select(t => t.Id));
        if (ids.Count == 0) return;
        Tracks.RemoveAll(t => ids.Contains(t.Id));
        foreach (var id in ids) _unavailable.Remove(id);
        foreach (var list in State.CustomPlaylists.Values) list.RemoveAll(ids.Contains);
        Reindex();
        SaveLibrary();
    }

    #endregion

    #region 播放统计

    /// <summary>记一次播放：次数 +1，更新最近播放时间。</summary>
    public void RecordPlay(TrackItem track)
    {
        track.PlayCount++;
        track.LastPlayedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Tracks.Contains(track)) SaveLibrary();
    }

    #endregion

    #region 丢失文件 / 重复歌曲 / 文件夹监视

    /// <summary>检查曲库里每首歌的文件是否还在，更新“文件丢失”标记，返回当前丢失的数量。</summary>
    public async Task<int> RefreshMissingAsync()
    {
        var snapshot = Tracks.Where(t => !t.IsOnline).Select(t => (t, t.FilePath)).ToList();
        var exists = await Task.Run(() => snapshot.Select(x => File.Exists(x.FilePath)).ToList());
        int missing = 0;
        for (int i = 0; i < snapshot.Count; i++)
        {
            snapshot[i].t.IsMissing = !exists[i];
            if (!exists[i]) missing++;
        }
        return missing;
    }

    public List<TrackItem> MissingTracks() => Tracks.Where(t => t.IsMissing).ToList();

    /// <summary>
    /// 在新的根目录下按文件名找回丢失的文件（只处理能唯一匹配的）。
    /// 路径变了曲目 ID 也会变，所以同步改歌单里的引用和“上次播放”。返回找回的数量。
    /// </summary>
    public async Task<int> RelocateMissingAsync(string newRoot)
    {
        var missing = Tracks.Where(t => t.IsMissing && !t.IsCueTrack).ToList();
        if (missing.Count == 0 || !Directory.Exists(newRoot)) return 0;

        var index = await Task.Run(() =>
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.EnumerateFiles(newRoot, "*.*", SearchOption.AllDirectories))
                {
                    if (!SupportedExtensions.Contains(Path.GetExtension(f))) continue;
                    var name = Path.GetFileName(f);
                    if (!map.TryGetValue(name, out var list)) map[name] = list = new List<string>();
                    list.Add(f);
                }
            }
            catch { }
            return map;
        });

        int fixedCount = 0;
        foreach (var t in missing)
        {
            if (!index.TryGetValue(Path.GetFileName(t.FilePath), out var hits) || hits.Count != 1) continue;
            string newPath = Path.GetFullPath(hits[0]);
            string oldId = t.Id;
            string newId = TrackItem.MakeId(newPath);
            if (Tracks.Any(o => o.Id == newId && !ReferenceEquals(o, t))) continue;   // 新位置已经有同一首了

            t.FilePath = newPath;
            t.Id = newId;
            t.FileMtimeTicks = SafeMtime(newPath);
            t.IsMissing = false;
            foreach (var list in State.CustomPlaylists.Values)
                for (int i = 0; i < list.Count; i++) if (list[i] == oldId) list[i] = newId;
            if (State.LastTrackId == oldId) State.LastTrackId = newId;
            fixedCount++;
        }

        if (fixedCount > 0) SaveLibrary();
        return fixedCount;
    }

    private static string NormKey(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>质量分：文件在 > 音质档位 > 码率 > 有封面，用来在重复里推荐保留哪一首。</summary>
    public static int QualityScore(TrackItem t) =>
        (t.IsMissing ? 0 : 1_000_000) + t.QualityTier * 10_000 + Math.Min(t.Bitrate, 9_999) + (string.IsNullOrEmpty(t.CoverPath) ? 0 : 1);

    /// <summary>
    /// 找重复歌曲：歌名 + 歌手相同（忽略大小写和标点），时长相差不超过 2 秒。
    /// 每组按推荐保留的顺序排好（第一首是建议保留的）。CUE 分轨和云端曲目不参与。
    /// </summary>
    public List<List<TrackItem>> FindDuplicateGroups()
    {
        var groups = new List<List<TrackItem>>();
        var buckets = Tracks
            .Where(t => !t.IsCueTrack && !t.IsOnline && NormKey(t.Title).Length > 0)
            .GroupBy(t => NormKey(t.Title) + "|" + NormKey(t.Artist));

        foreach (var b in buckets)
        {
            if (b.Count() < 2) continue;
            var sorted = b.OrderBy(t => t.Duration).ToList();
            var cur = new List<TrackItem> { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (Math.Abs(sorted[i].Duration - cur[^1].Duration) <= 2.0) cur.Add(sorted[i]);
                else
                {
                    if (cur.Count > 1) groups.Add(cur);
                    cur = new List<TrackItem> { sorted[i] };
                }
            }
            if (cur.Count > 1) groups.Add(cur);
        }

        foreach (var g in groups) g.Sort((a, b) => QualityScore(b).CompareTo(QualityScore(a)));
        return groups.OrderBy(g => g[0].Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---- 文件夹监视：磁盘上有新增 / 改动 / 删除时自动同步 ----
    private readonly List<FileSystemWatcher> _watchers = new();
    private System.Threading.Timer? _watchDebounce;
    private Microsoft.UI.Dispatching.DispatcherQueue? _watchDispatcher;

    /// <summary>监视的文件夹发生变化并完成增量同步后触发（参数是新增的曲目）。</summary>
    public event Action<List<TrackItem>>? LibraryChangedOnDisk;

    public void StartWatching(Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _watchDispatcher = dispatcher;
        RestartWatching();
    }

    private void RestartWatching()
    {
        if (_watchDispatcher == null) return;
        foreach (var w in _watchers) { try { w.Dispose(); } catch { } }
        _watchers.Clear();

        foreach (var folder in State.WatchedFolders.ToList())
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var w = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler h = (s, e) => OnDiskChanged(e.FullPath);
                w.Created += h;
                w.Changed += h;
                w.Deleted += h;
                w.Renamed += (s, e) => OnDiskChanged(e.FullPath);
                _watchers.Add(w);
            }
            catch { }
        }
    }

    private void OnDiskChanged(string path)
    {
        var ext = Path.GetExtension(path);
        // 只关心音频和 CUE（子目录的增删 ext 为空，也要响应）
        if (ext.Length > 0 && !SupportedExtensions.Contains(ext) && !ext.Equals(".cue", StringComparison.OrdinalIgnoreCase)) return;

        // 防抖：复制一批文件时事件很密，安静 4 秒后再同步一次
        _watchDebounce ??= new System.Threading.Timer(_ => SyncAfterDiskChange(), null, Timeout.Infinite, Timeout.Infinite);
        _watchDebounce.Change(4000, Timeout.Infinite);
    }

    private void SyncAfterDiskChange()
    {
        _watchDispatcher?.TryEnqueue(async () =>
        {
            try
            {
                var added = new List<TrackItem>();
                foreach (var folder in State.WatchedFolders.ToList())
                {
                    if (!Directory.Exists(folder)) continue;
                    added.AddRange(await AddFolderAsync(folder));
                }
                await RefreshMissingAsync();
                LibraryChangedOnDisk?.Invoke(added);
            }
            catch { }
        });
    }

    #endregion

    #region 标签编辑

    /// <summary>
    /// 改歌名 / 歌手 / 专辑并写回音频文件（传 null 表示该项不改）。CUE 分轨和云端曲目只改曲库里的显示。
    /// 返回出错的提示文字，成功返回 null。
    /// </summary>
    public string? UpdateTags(TrackItem track, string? title, string? artist, string? album)
    {
        if (!track.IsCueTrack && !track.IsOnline)
        {
            if (track.IsMissing || !File.Exists(track.FilePath)) return L.T("文件不存在，无法写入标签。");
            try
            {
                using var f = TagLib.File.Create(track.FilePath);
                if (title != null) f.Tag.Title = title;
                if (artist != null) f.Tag.Performers = new[] { artist };
                if (album != null) f.Tag.Album = album;
                f.Save();
            }
            catch (Exception ex)
            {
                return L.T("写入标签失败：") + ex.Message + L.T("\n（该歌曲可能正在播放，请切换到其他歌曲后重试）");
            }
            track.FileMtimeTicks = SafeMtime(track.FilePath);
        }

        if (title != null) track.Title = title;
        if (artist != null) track.Artist = artist;
        if (album != null) track.Album = album;
        return null;
    }

    #endregion

    #region 智能歌单

    /// <summary>内置的智能歌单：不用建，也不能删。</summary>
    public static readonly IReadOnlyList<SmartPlaylistDef> BuiltInSmartPlaylists = new[]
    {
        new SmartPlaylistDef { Name = L.T("最近添加"), AddedWithinDays = 30, SortBy = "added", Limit = 100 },
        new SmartPlaylistDef { Name = L.T("最近播放"), MinPlayCount = 1, SortBy = "played", Limit = 100 },
        new SmartPlaylistDef { Name = L.T("常听"), MinPlayCount = 2, SortBy = "plays", Limit = 50 },
        // 心情歌单：根据歌曲的声学特征（响度 / 节奏密度 / 明暗）自动挑，歌曲库的声学分析完成后才有内容
        new SmartPlaylistDef { Name = L.T("情绪 · 安静"), Mood = "calm", SortBy = "mood" },
        new SmartPlaylistDef { Name = L.T("情绪 · 专注"), Mood = "focus", SortBy = "mood" },
        new SmartPlaylistDef { Name = L.T("情绪 · 动感"), Mood = "energetic", SortBy = "mood" },
        new SmartPlaylistDef { Name = L.T("情绪 · 明亮"), Mood = "bright", SortBy = "mood" },
        new SmartPlaylistDef { Name = L.T("情绪 · 沉稳"), Mood = "deep", SortBy = "mood" },
    };

    public IEnumerable<SmartPlaylistDef> AllSmartPlaylists => BuiltInSmartPlaylists.Concat(State.SmartPlaylists);

    public SmartPlaylistDef? FindSmart(string name) => AllSmartPlaylists.FirstOrDefault(s => s.Name == name);

    public bool IsBuiltInSmart(string name) => BuiltInSmartPlaylists.Any(s => s.Name == name);

    public List<TrackItem> GetSmartTracks(SmartPlaylistDef d)
    {
        IEnumerable<TrackItem> q = Tracks.Where(t => !t.IsMissing);
        Dictionary<string, int>? moodRank = null;
        if (!string.IsNullOrEmpty(d.Mood))
        {
            moodRank = new Dictionary<string, int>();
            int r = 0;
            foreach (var t in AcousticService.Instance.MoodTracks(d.Mood, Tracks)) moodRank.TryAdd(t.Id, r++);
            q = q.Where(t => moodRank.ContainsKey(t.Id));
        }
        if (d.MinQualityTier > 0) q = q.Where(t => t.QualityTier >= d.MinQualityTier);
        if (d.OnlyFavorite) q = q.Where(t => t.IsFavorite);
        if (d.MinPlayCount > 0) q = q.Where(t => t.PlayCount >= d.MinPlayCount);
        if (d.AddedWithinDays > 0)
        {
            long since = DateTimeOffset.UtcNow.AddDays(-d.AddedWithinDays).ToUnixTimeSeconds();
            q = q.Where(t => t.AddedTimestamp >= since);
        }
        q = d.SortBy switch
        {
            "played" => q.OrderByDescending(t => t.LastPlayedTimestamp),
            "plays" => q.OrderByDescending(t => t.PlayCount).ThenByDescending(t => t.LastPlayedTimestamp),
            "title" => q.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            // 心情歌单：最贴合的排在前面
            "mood" => moodRank != null ? q.OrderBy(t => moodRank[t.Id]) : q,
            _ => q.OrderByDescending(t => t.AddedTimestamp)
        };
        var list = q.ToList();
        return d.Limit > 0 && list.Count > d.Limit ? list.Take(d.Limit).ToList() : list;
    }

    public bool CreateSmartPlaylist(SmartPlaylistDef def)
    {
        def.Name = def.Name.Trim();
        if (def.Name.Length == 0 || FindSmart(def.Name) != null || State.CustomPlaylists.ContainsKey(def.Name)) return false;
        State.SmartPlaylists.Add(def);
        StateService.Instance.RequestSave();
        return true;
    }

    public void DeleteSmartPlaylist(string name)
    {
        if (State.SmartPlaylists.RemoveAll(s => s.Name == name) > 0) StateService.Instance.RequestSave();
    }

    #endregion

    #region M3U 导入 / 导出

    /// <summary>把歌单导出为 M3U8（UTF-8，带 #EXTINF，路径用绝对路径）。</summary>
    public void ExportPlaylistM3u(IEnumerable<TrackItem> tracks, string filePath)
    {
        var sb = new StringBuilder("#EXTM3U\r\n");
        foreach (var t in tracks.Where(t => !t.IsOnline && !t.IsCueTrack))
        {
            sb.Append("#EXTINF:").Append((int)Math.Round(t.Duration)).Append(',')
              .Append(string.IsNullOrWhiteSpace(t.Artist) ? "" : t.Artist + " - ").Append(t.Title).Append("\r\n")
              .Append(t.FilePath).Append("\r\n");
        }
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// 导入 M3U / M3U8 为新歌单：路径相对 M3U 所在目录解析；不在曲库里但文件存在的会一并加入曲库。
    /// 返回 (歌单名, 导入曲目数, 找不到的行数)；失败时歌单名为 null。
    /// </summary>
    public async Task<(string? Name, int Imported, int NotFound)> ImportPlaylistM3uAsync(string m3uPath)
    {
        string baseName = Path.GetFileNameWithoutExtension(m3uPath);
        string dir = Path.GetDirectoryName(m3uPath) ?? "";

        var paths = await Task.Run(() =>
        {
            var result = new List<string>();
            foreach (var raw in File.ReadAllLines(m3uPath, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                    line = Uri.UnescapeDataString(line.Substring(8)).Replace('/', '\\');
                try { result.Add(Path.GetFullPath(Path.IsPathRooted(line) ? line : Path.Combine(dir, line))); }
                catch { }
            }
            return result;
        });
        if (paths.Count == 0) return (null, 0, 0);

        string name = baseName;
        for (int i = 2; State.CustomPlaylists.ContainsKey(name) || FindSmart(name) != null; i++) name = $"{baseName} ({i})";
        State.CustomPlaylists[name] = new List<string>();

        var byId = Tracks.ToDictionary(t => t.Id, t => t);
        int imported = 0, notFound = 0;
        var newTracks = new List<TrackItem>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var p in paths)
        {
            string id = TrackItem.MakeId(p);
            if (!byId.TryGetValue(id, out var t))
            {
                if (!File.Exists(p) || !SupportedExtensions.Contains(Path.GetExtension(p))) { notFound++; continue; }
                t = await Task.Run(() => ParseTrack(p));
                if (t == null) { notFound++; continue; }
                t.AddedTimestamp = now;
                Tracks.Add(t);
                byId[t.Id] = t;
                newTracks.Add(t);
            }
            if (!State.CustomPlaylists[name].Contains(t.Id)) { State.CustomPlaylists[name].Add(t.Id); imported++; }
        }

        if (newTracks.Count > 0) { Reindex(); SaveLibrary(); MetadataService.Instance.Enqueue(newTracks); }
        else StateService.Instance.RequestSave();
        return (name, imported, notFound);
    }

    /// <summary>把歌单顺序整体改成给定的曲目顺序（拖拽排序后保存）。</summary>
    public void SetPlaylistOrder(string name, IEnumerable<TrackItem> ordered)
    {
        if (!State.CustomPlaylists.ContainsKey(name)) return;
        State.CustomPlaylists[name] = ordered.Select(t => t.Id).ToList();
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

    /// <summary>
    /// 扫描文件夹。传入 knownById 时做增量扫描：已在曲库里、文件修改时间没变的文件直接跳过，不再读标签。
    /// </summary>
    public async Task<List<TrackItem>> ScanFolderAsync(string folderPath, IReadOnlyDictionary<string, TrackItem>? knownById = null)
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

                        // 增量：这张 CUE 的分轨都在曲库里，且整轨文件没变过 → 跳过重新解析
                        if (knownById != null && sheet.Tracks.Count > 0 && File.Exists(sheet.AudioFilePath))
                        {
                            long audioMtime = SafeMtime(sheet.AudioFilePath);
                            bool allKnown = sheet.Tracks.All(ct =>
                                knownById.TryGetValue(TrackItem.MakeId(sheet.AudioFilePath, ct.Number), out var k) && k.FileMtimeTicks == audioMtime);
                            if (allKnown)
                            {
                                cueAudio.Add(sheet.AudioFilePath);
                                continue;
                            }
                        }

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
                        if (knownById != null
                            && knownById.TryGetValue(TrackItem.MakeId(Path.GetFullPath(file)), out var known)
                            && known.FileMtimeTicks == SafeMtime(file))
                        {
                            continue;   // 没变过：不读标签
                        }
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
                CueTrackNum = ct.Number,
                FileMtimeTicks = baseTrack.FileMtimeTicks
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
            ReplayPeak = rgPeak,
            FileMtimeTicks = SafeMtime(filePath)
        };
    }

    private static long SafeMtime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; } catch { return 0; }
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
