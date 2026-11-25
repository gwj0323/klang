using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services.Cloud;

/// <summary>歌词包：Lrc 在有逐字歌词时已是增强 LRC（&lt;时间&gt;逐字标记）。</summary>
public record LyricBundle(string Lrc, string Trans, string Roma)
{
    public static readonly LyricBundle Empty = new("", "", "");
}

/// <summary>云端平台上的一个歌单。</summary>
public sealed class CloudPlaylist
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int TrackCount { get; init; }
    /// <summary>平台的“我喜欢”歌单。</summary>
    public bool IsLiked { get; init; }
    public string CountText => L.F("{0} 首", TrackCount);
}

public enum QrLoginState { Waiting, Scanned, Expired, Success, Error }

/// <summary>一个 MV 的播放地址：Height = 这个地址的画质（像素高度），Heights = 可选的画质（从高到低）。</summary>
public sealed record MvStream(string Url, int Height, IReadOnlyList<int> Heights, string Title);

/// <summary>
/// 一个云端音乐来源（网易云等）。Klang 的播放、缓存、队列和界面只通过这个接口和平台打交道，
/// 接入新平台只需要再写一份实现，并在 <see cref="CloudSources"/> 里登记。
/// 云端曲目用 <see cref="TrackItem.CloudSource"/>（来源的 <see cref="Key"/>）+ <see cref="TrackItem.CloudId"/> 标识。
/// </summary>
public interface ICloudSource
{
    /// <summary>来源标识，写进曲目和状态文件，定了就不能再改。</summary>
    string Key { get; }
    string DisplayName { get; }

    #region 登录

    bool IsLoggedIn { get; }
    string Nickname { get; }
    /// <summary>头像地址；没有时为空（界面显示昵称首字）。</summary>
    string AvatarUrl => "";
    /// <summary>登录状态变化（可能在后台线程触发，订阅方自行切线程）。</summary>
    event Action? LoginChanged;
    /// <summary>启动后静默校验登录状态。</summary>
    Task VerifyLoginAsync();
    void Logout();

    /// <summary>扫码方式（例如 QQ / 微信）；只有一种时界面不显示切换。</summary>
    IReadOnlyList<(string Key, string Label)> QrModes { get; }
    /// <summary>扫码登录界面上的提示，例如“用某某 App 扫码登录”。</summary>
    string QrHintFor(string mode);
    /// <summary>申请登录二维码（PNG 或 JPEG）；Key 为空表示失败。</summary>
    Task<(string Key, byte[] Png)> CreateQrAsync(string mode);
    Task<(QrLoginState State, string Message)> PollQrAsync(string key);

    /// <summary>是否支持手动粘贴 Cookie 登录。</summary>
    bool SupportsCookieLogin { get; }
    /// <summary>Cookie 输入框的占位提示。</summary>
    string CookieHint { get; }
    Task<(bool Ok, string Message)> LoginWithCookieAsync(string input);

    #endregion

    #region 内容

    /// <summary>是否有每日推荐一类的推荐列表；标题见 <see cref="DailyRecommendTitle"/>。</summary>
    bool HasDailyRecommend { get; }
    string DailyRecommendTitle { get; }
    Task<List<TrackItem>> GetDailyRecommendAsync();
    Task<List<CloudPlaylist>> GetUserPlaylistsAsync();
    Task<List<TrackItem>> GetPlaylistTracksAsync(string playlistId);
    Task<List<TrackItem>> SearchAsync(string keyword, int limit = 50);
    Task<LyricBundle> GetLyricsAsync(string cloudId, CancellationToken ct = default);

    /// <summary>MV 的播放地址和可选画质（高度，像素）；没有 MV 或不支持时返回 null。</summary>
    Task<MvStream?> GetMvAsync(TrackItem track, int preferredHeight, CancellationToken ct = default) => Task.FromResult<MvStream?>(null);

    /// <summary>这个平台能不能播放 MV（决定菜单里是否出现“播放 MV”）。</summary>
    bool SupportsMv => false;

    /// <summary>曲目还不知道有没有 MV 时（MvId 为空）查一下，结果写进 track.MvId。</summary>
    Task EnsureMvIdAsync(TrackItem track, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>动态封面（短视频，静音循环播放），下载到缓存后返回本地路径；没有或不支持时返回 null。</summary>
    Task<string?> GetDynamicCoverAsync(string cloudId, CancellationToken ct = default) => Task.FromResult<string?>(null);

    #endregion

    #region 音源与缓存

    IReadOnlyList<(string Key, string Label)> Qualities { get; }
    string PreferredQuality { get; set; }

    /// <summary>为云端曲目取得可播放的本地文件（下载并缓存）。成功后 FilePath / 封面 / 音质信息已写到曲目上。</summary>
    Task<bool> ResolveAsync(TrackItem track, CancellationToken ct);
    /// <summary>按当前首选音质是否已缓存。</summary>
    bool IsCached(TrackItem track);
    /// <summary>后台预缓冲（下一首）。</summary>
    void Prefetch(TrackItem track);
    /// <summary>封面下载完成（可能在后台线程触发）。</summary>
    event Action<TrackItem>? CoverCached;

    (int Count, long Bytes) GetCacheInfo();
    (int Files, long Bytes) ClearCache();

    #endregion

    /// <summary>地址是否属于这个平台（封面、音频只从这些地址下载或显示）。</summary>
    bool IsTrustedUrl(string? url);

    /// <summary>从保存的快照还原曲目（重启后恢复上次播放用）；无效的条目跳过。</summary>
    List<TrackItem> FromSnapshots(IEnumerable<OnlineTrackSnapshot> list);
}
