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
    public int PlayCount { get; set; }
    public long LastPlayedTimestamp { get; set; }
    public float[]? AudioFeat { get; set; }
    public string AuthVerdict { get; set; } = "";
    public float AuthCutoffHz { get; set; }
    public int AuthBits { get; set; }
    public bool RhythmDone { get; set; }
    public double Bpm { get; set; }
    public double FirstBeat { get; set; }
    public double LastBeat { get; set; }
    public double MixEnd { get; set; }
    public string MusicKey { get; set; } = "";
    public string Camelot { get; set; } = "";
}

/// <summary>漫游模型里每首歌的行为统计。</summary>
public class RoamStat
{
    public int Plays { get; set; }
    public int Completes { get; set; }
    public int Skips { get; set; }
    public float[] Hours { get; set; } = new float[24];   // 一天里各小时播放它的次数
}

/// <summary>漫游模式的本地学习结果（逻辑回归权重 + 歌曲间转移 + 每首歌的统计）。</summary>
public class RoamModel
{
    public float[] Weights { get; set; } = Array.Empty<float>();
    public int Samples { get; set; }
    public Dictionary<string, RoamStat> Stats { get; set; } = new();
    public Dictionary<string, Dictionary<string, float>> Transitions { get; set; } = new();
}

/// <summary>智能歌单的规则（按条件自动生成，不存曲目列表）。</summary>
public class SmartPlaylistDef
{
    public string Name { get; set; } = "";
    public int MinQualityTier { get; set; }        // 0 = 不限，1 = 无损及以上，2 = 仅 Hi-Res
    public bool OnlyFavorite { get; set; }
    public int AddedWithinDays { get; set; }       // 0 = 不限
    public int MinPlayCount { get; set; }          // 0 = 不限
    public string Mood { get; set; } = "";         // 声学心情：calm | focus | energetic | bright | deep；空 = 不限
    public string SortBy { get; set; } = "added";  // added | played | plays | title
    public int Limit { get; set; }                 // 0 = 不限
}

public class AppState
{
    public double Volume { get; set; } = 0.85;
    public string PlayMode { get; set; } = "loop_list";
    public string AutoMix { get; set; } = "roam";           // 节拍对齐的自动混音：off | roam（仅漫游模式）| all
    public string Language { get; set; } = "auto";          // auto（跟随系统）| zh-CN | en | Languages 文件夹里其他语言
    public string Theme { get; set; } = "system";          // system | light | dark
    public string BackdropMaterial { get; set; } = "mica"; // mica | acrylic（旧版本的 solid 按 mica 处理）
    public bool SidebarCollapsed { get; set; }
    public int LyricFontSize { get; set; } = 22;
    public bool WaveformSeekBar { get; set; } = true;      // 底栏进度条显示振幅波形（关 = 标准滑块）
    public bool LyricDepthBlur { get; set; } = true;      // 歌词景深模糊（离当前句越远越模糊）
    public bool LyricWordMotion { get; set; } = true;      // 逐字歌词：唱到的字上移、长音辉光
    public bool LyricZoomEmphasis { get; set; } = true;   // 歌词“放大强调”（当前句放大、远处缩小）；Win2D 逐帧按真实字号绘制，放大也锐利
    // 悬浮窗口：位置与字号
    public int? DesktopLyricX { get; set; }
    public int? DesktopLyricY { get; set; }
    public int DesktopLyricFontSize { get; set; } = 30;
    public int? MiniX { get; set; }
    public int? MiniY { get; set; }

    public bool AutoFetchMetadata { get; set; } = true;     // 本地歌曲缺封面/信息/歌词时在线补全
    public string LyricSubMode { get; set; } = "trans";   // trans | roma | both | off
    public int WasapiLatencyMs { get; set; } = 50;
    public bool WasapiExclusive { get; set; }               // WASAPI 独占模式：按文件原采样率 / 位深直接驱动声卡
    public bool BypassDsp { get; set; }                     // 绕过全部音效（均衡 / 空间 / 混响 / 限幅），纯净直通
    public Dictionary<string, double> LyricOffsets { get; set; } = new();   // 每首歌的歌词时间偏移（秒）
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
    public List<SmartPlaylistDef> SmartPlaylists { get; set; } = new();
    public RoamModel Roam { get; set; } = new();
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
                    state.SmartPlaylists ??= new();
                    state.Roam ??= new();
                    state.LyricOffsets ??= new();
                    state.Roam.Stats ??= new();
                    state.Roam.Transitions ??= new();
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
            catch (Exception ex)
            {
                // 序列化在计时器线程上进行，界面线程正好在改列表时会失败（大量导入时尤其常见）。
                // 以前直接放弃，这一轮的改动就丢了；现在保留“未保存”标记，稍后重试
                _dirty = true;
                _debounce.Change(800, Timeout.Infinite);
                App.LogError("Save state (will retry)", ex.Message);
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
