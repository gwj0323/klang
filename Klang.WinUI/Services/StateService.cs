using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Klang_WinUI.Services;

/// <summary>保存到磁盘的单首曲目记录（与 Qt 版 player_state.json 的 playlist 项对应）。</summary>
public class SavedTrack
{
    public string TrackId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public double Duration { get; set; }
    public string FilePath { get; set; } = "";
    public string CoverPath { get; set; } = "";
    public bool IsFavorite { get; set; }
    public int SampleRate { get; set; } = 44100;
    public int BitDepth { get; set; } = 16;
    public int Bitrate { get; set; }
    public string FormatBadge { get; set; } = "";
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public string CueTrackPath { get; set; } = "";
    public int CueTrackNum { get; set; }
    public long AddedTimestamp { get; set; }
    public long FileMtimeTicks { get; set; }
    public double? ReplayGainDb { get; set; }
    public double? ReplayPeak { get; set; }
    public bool MetaChecked { get; set; }
    public string MetaNeteaseId { get; set; } = "";
}

public class AppState
{
    public double Volume { get; set; } = 0.85;
    public string PlayMode { get; set; } = "loop_list";
    public string Theme { get; set; } = "system";          // system | light | dark
    public string BackdropMaterial { get; set; } = "mica"; // mica | acrylic | solid
    public bool SidebarCollapsed { get; set; }
    public int LyricFontSize { get; set; } = 22;
    // 悬浮窗口：位置与字号
    public int? DesktopLyricX { get; set; }
    public int? DesktopLyricY { get; set; }
    public int DesktopLyricFontSize { get; set; } = 30;
    public int? MiniX { get; set; }
    public int? MiniY { get; set; }

    public bool AutoFetchMetadata { get; set; } = true;     // 本地歌曲缺封面/信息/歌词时在线补全
    public string LyricSubMode { get; set; } = "trans";   // trans | roma | both | off
    public int WasapiLatencyMs { get; set; } = 50;
    public double PlaybackRate { get; set; } = 1.0;
    public string SelectedDeviceId { get; set; } = "default";
    public bool ReplayGainEnabled { get; set; }

    public string SoundProfile { get; set; } = "默认原声";
    public float[] EqGainsDb { get; set; } = new float[5];
    public string SpatialMode { get; set; } = "DolbyAtmos";
    public float VocalBalance { get; set; }
    public float VinylWarmth { get; set; }
    public string HeadphoneProfile { get; set; } = "bypass";

    public string NavSortMode { get; set; } = "default";
    public string NavGroupMode { get; set; } = "none";     // none | artist | album | folder

    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }

    // 网易云：Cookie 用 DPAPI 加密后保存
    public string NeteaseCookiesEnc { get; set; } = "";
    public long NeteaseUserId { get; set; }
    public string NeteaseNickname { get; set; } = "";
    public string NeteaseAvatarUrl { get; set; } = "";
    public string NeteaseQuality { get; set; } = "lossless";
    public int NeteaseCacheLimitMb { get; set; } = 2048;

    public string LastTrackId { get; set; } = "";
    public List<string> WatchedFolders { get; set; } = new();
    public Dictionary<string, List<string>> CustomPlaylists { get; set; } = new();
    public List<SavedTrack> Tracks { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppState))]
internal partial class AppStateJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 播放器状态持久化：内存中保存最新快照，写盘由防抖定时器合并，
/// 原子写入（tmp → 替换）并保留 .bak，主文件损坏时回退到 .bak（对应 Qt 版 storage_service）。
/// </summary>
public class StateService
{
    private static readonly Lazy<StateService> _instance = new(() => new StateService());
    public static StateService Instance => _instance.Value;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Timer _debounce;
    private bool _dirty;

    public AppState State { get; private set; } = new();

    public StateService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klang");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "player_state.json");
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Load();
    }

    private void Load()
    {
        foreach (var candidate in new[] { _path, Path.ChangeExtension(_path, ".bak") })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var json = File.ReadAllText(candidate);
                var state = JsonSerializer.Deserialize(json, AppStateJsonContext.Default.AppState);
                if (state != null)
                {
                    if (state.EqGainsDb == null || state.EqGainsDb.Length != 5) state.EqGainsDb = new float[5];
                    state.Tracks ??= new();
                    state.WatchedFolders ??= new();
                    state.CustomPlaylists ??= new();
                    State = state;
                    return;
                }
            }
            catch
            {
                if (candidate == _path)
                {
                    try { File.Copy(_path, Path.ChangeExtension(_path, ".corrupt"), true); } catch { }
                }
            }
        }
    }

    /// <summary>标记状态已变化，约 500ms 内合并写盘。</summary>
    public void RequestSave()
    {
        lock (_lock)
        {
            _dirty = true;
            _debounce.Change(500, Timeout.Infinite);
        }
    }

    /// <summary>立即写盘（退出时调用）。</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            _dirty = false;

            string json;
            try
            {
                json = JsonSerializer.Serialize(State, AppStateJsonContext.Default.AppState);
            }
            catch
            {
                return;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var tmp = Path.ChangeExtension(_path, ".tmp");
                    File.WriteAllText(tmp, json);
                    if (File.Exists(_path))
                    {
                        try { File.Copy(_path, Path.ChangeExtension(_path, ".bak"), true); } catch { }
                    }
                    File.Move(tmp, _path, true);
                    return;
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
        }
    }
}
