using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Klang_WinUI.Models;

public class TrackItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>界面线程的调度器；封面可能在后台线程更新，通知必须回到界面线程再触发绑定。</summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue? UiDispatcher { get; set; }

    private void Raise(string name)
    {
        var handler = PropertyChanged;
        if (handler == null) return;
        var d = UiDispatcher;
        if (d == null || d.HasThreadAccess) handler(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        else d.SafeEnqueue(() => handler(this, new System.ComponentModel.PropertyChangedEventArgs(name)));
    }
    private bool _isCurrent;
    /// <summary>是不是正在播放（列表里左侧画强调色竖条）。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            Raise(nameof(IsCurrent));
            Raise(nameof(CurrentBarVisibility));
        }
    }
    public Microsoft.UI.Xaml.Visibility CurrentBarVisibility =>
        _isCurrent ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public int Index { get; set; }
    public string Id{ get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "Unknown Title";
    public string Artist { get; set; } = "Unknown Artist";
    public string Album { get; set; } = "Unknown Album";
    public double Duration { get; set; } = 0.0;
    public string DurationStr => FormatDuration(Duration);
    public string FilePath { get; set; } = string.Empty;
    private string? _coverPath;
    public string? CoverPath
    {
        get => _coverPath;
        set
        {
            _coverPath = value;
            Raise(nameof(CoverPath));
            Raise(nameof(CoverThumb));
        }
    }

    /// <summary>列表里的封面缩略图（本地文件，或云端曲目尚未缓存时用网络小图）；没有封面时为 null。</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? CoverThumb
    {
        get
        {
            try
            {
                Uri? uri = null;
                if (!string.IsNullOrEmpty(_coverPath) && File.Exists(_coverPath)) uri = new Uri(_coverPath);
                else if (!string.IsNullOrEmpty(CoverUrl))
                    uri = new Uri(CoverUrl.Contains('?') ? CoverUrl : CoverUrl + "?param=96y96");
                return uri == null ? null : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri) { DecodePixelWidth = 96 };
            }
            catch
            {
                return null;
            }
        }
    }
    public string FormatBadge { get; set; } = "FLAC";
    public int SampleRate { get; set; } = 44100;
    public int BitDepth { get; set; } = 16;
    public int Bitrate { get; set; } = 0;
    public bool IsFavorite { get; set; } = false;

    /// <summary>CUE 分轨：在整轨音频文件内的起止秒数（EndTime &lt;= 0 表示播放到文件末尾）。</summary>
    public double StartTime { get; set; } = 0.0;
    public double EndTime { get; set; } = 0.0;
    public string CueTrackPath { get; set; } = string.Empty;
    public int CueTrackNum { get; set; } = 0;
    public bool IsCueTrack => CueTrackNum > 0;

    public long AddedTimestamp { get; set; }

    /// <summary>播放统计：累计播放次数与最近一次播放时间（Unix 秒）。</summary>
    public int PlayCount { get; set; }
    public long LastPlayedTimestamp { get; set; }

    // ---- 无损真伪检测结果（后台分析无损文件的频谱得出）----
    private string _authVerdict = "";
    /// <summary>"" = 还没分析 / 不适用；ok = 正常；lossy = 疑似有损转码；upsampled = 疑似升采样的 Hi-Res；padded = 疑似位深填充。</summary>
    public string AuthVerdict
    {
        get => _authVerdict;
        set
        {
            if (_authVerdict == value) return;
            _authVerdict = value;
            Raise(nameof(AuthVerdict));
            Raise(nameof(IsSuspectFake));
            Raise(nameof(SuspectVisibility));
            Raise(nameof(RowToolTip));
        }
    }
    /// <summary>频谱上高频被“一刀切”的频率（Hz），没有明显截止时为 0。</summary>
    public float AuthCutoffHz { get; set; }

    // ---- 节奏与调性（后台分析，用于节拍对齐的自动混音）----
    public bool RhythmDone { get; set; }
    private double _bpm;
    public double Bpm { get => _bpm; set { _bpm = value; Raise(nameof(RowToolTip)); } }   // 0 = 没有稳定节拍
    public double FirstBeat { get; set; }
    public double LastBeat { get; set; }
    public double MixEnd { get; set; }
    public string MusicKey { get; set; } = "";
    public string Camelot { get; set; } = "";
    /// <summary>有效位深（16 / 24），0 = 未检测。</summary>
    public int AuthBits { get; set; }

    public bool IsSuspectFake => _authVerdict is "lossy" or "upsampled" or "padded";
    public Microsoft.UI.Xaml.Visibility SuspectVisibility => IsSuspectFake ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>一句话说明检测结论（用于提示 / 徽标）。</summary>
    public string AuthText => _authVerdict switch
    {
        "lossy" => L.F("疑似有损转码：频谱在 {0:0.#} kHz 处截止", AuthCutoffHz / 1000f),
        "upsampled" => L.F("疑似伪 Hi-Res：有效频谱仅到 {0:0.#} kHz", AuthCutoffHz / 1000f),
        "padded" => L.T("疑似位深填充：实际仅 16 bit"),
        "ok" => L.T("频谱与位深检测正常"),
        _ => ""
    };

    /// <summary>从音频里算出的声学特征（音量 / 动态 / 明暗 / 频段 / 节奏），漫游模式用它找“听起来像”的歌；还没算出来为 null。</summary>
    public float[]? AudioFeat { get; set; }

    /// <summary>文件最后修改时间（UTC ticks），增量扫描靠它判断文件有没有变过。</summary>
    public long FileMtimeTicks { get; set; }

    private bool _isMissing;
    /// <summary>文件已被移走或删除（列表里标灰并提示“文件丢失”）。</summary>
    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (_isMissing == value) return;
            _isMissing = value;
            Raise(nameof(IsMissing));
            Raise(nameof(RowOpacity));
            Raise(nameof(RowToolTip));
        }
    }
    public double RowOpacity => _isMissing ? 0.45 : 1.0;
    public string RowToolTip
    {
        get
        {
            if (_isMissing) return L.T("文件丢失：") + FilePath;
            string s = IsSuspectFake ? FormatBadge + "\n⚠ " + AuthText : FormatBadge;
            if (_bpm > 0) s += "\n♩ " + L.F("{0:0} BPM", _bpm) + (string.IsNullOrEmpty(Camelot) ? "" : $" · {MusicKey} ({Camelot})");
            return s;
        }
    }

    /// <summary>音质档位：2 = Hi-Res，1 = 无损，0 = 有损。</summary>
    public int QualityTier
    {
        get
        {
            var ext = Path.GetExtension(FilePath).ToLowerInvariant();
            bool lossless = ext is ".flac" or ".wav" or ".ape"
                || FormatBadge.Contains("无损") || FormatBadge.StartsWith("Hi-Res", StringComparison.OrdinalIgnoreCase)
                || FormatBadge.Contains("FLAC", StringComparison.OrdinalIgnoreCase);
            if (!lossless) return 0;
            return SampleRate >= 88200 || BitDepth >= 24 ? 2 : 1;
        }
    }

    /// <summary>已尝试过在线补全封面/信息（避免每次启动重复请求）。</summary>
    public bool MetaChecked { get; set; }
    /// <summary>在线补全时匹配到的网易云歌曲 ID（用于取歌词）。</summary>
    public string MetaNeteaseId { get; set; } = string.Empty;

    /// <summary>网易云歌曲 ID；非空表示云端曲目（FilePath 在解析音源后才有值）。</summary>
    public string NeteaseId { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public bool IsOnline => NeteaseId.Length > 0;

    /// <summary>ReplayGain 标签（dB / 峰值），没有标签时为 null。</summary>
    public double? ReplayGainDb { get; set; }
    public double? ReplayPeak { get; set; }

    /// <summary>目录名，用于按目录分组。</summary>
    public string FolderName
    {
        get
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                return string.IsNullOrEmpty(dir) ? "未知目录" : dir;
            }
            catch
            {
                return "未知目录";
            }
        }
    }

    /// <summary>稳定 ID：由文件路径（及 CUE 轨号）哈希得到，歌单靠它引用曲目。</summary>
    public static string MakeId(string filePath, int cueTrackNum = 0)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()));
        var hash = Convert.ToHexString(bytes).Substring(0, 12).ToLowerInvariant();
        return cueTrackNum > 0 ? $"cue-{hash}-{cueTrackNum}" : $"local-{hash}";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0) return "00:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }
}
