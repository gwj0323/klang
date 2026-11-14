using System.Text.Json;
using System.Runtime.Versioning;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services.Soda;

/// <summary>
/// 汽水音源服务（只读：推荐/模式/喜欢/歌词/授权音源）。
/// 数据目录：StateService.ResolveDataDirectory()/soda/{account}/…；账号之间互不可见。
/// 已知限制：账户态接口可能需要官方原生签名；本服务通过可选的 Node 辅助进程承载官方组件，
/// 不具备时按 SodaException(Verification/Unavailable) 明确告知，不伪造成功。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class SodaService
{
    public static SodaService Instance { get; } = new();

    public event Action? LoginChanged;
    public event Action<TrackItem>? CoverCached;

    public bool IsLoggedIn => _session.IsLoggedIn;
    public string Nickname => _session.Nickname;
    public string UserId => _session.UserId;
    public string AvatarUrl => _session.AvatarUrl;

    private const string ProtocolVersionCode = "3070000";

    private readonly SodaSession _session;
    private readonly SodaCache _cache;
    private readonly string _root;
    private SodaHttp? _http;
    private SodaNodeBridge? _bridge;
    private SodaNative.Components? _components;
    private bool _registered;
    private string? _nativeNote;
    private string _deviceId = "";
    private string _installId = "";
    private string? _favoritesPlaylistId;

    private int _generation;
    private readonly SemaphoreSlim _feedGate = new(1, 1);
    private readonly SemaphoreSlim _resolveGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _resolving = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Json, DateTime Stamp)> _detailCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan DetailTtl = TimeSpan.FromMinutes(5);

    private SodaService()
    {
        _root = Path.Combine(StateService.ResolveDataDirectory(), "soda");
        Directory.CreateDirectory(_root);
        _session = new SodaSession(_root);
        _cache = new SodaCache(_root);
        _session.TryLoad();
        _deviceId = LoadOrCreateId("device_id");
        _installId = LoadOrCreateId("install_id");
        _registered = LoadFlag("registered");
        _http = CreateHttp();
    }

    private SodaHttp CreateHttp() => new(
        () => _session.IsLoggedIn ? _session.CookieHeader : null,
        () => _deviceId, () => _installId, () => _session.IsLoggedIn, SignAsync);

    /// <summary>签名器：只有原生桥可用时才会返回头；返回 null 表示当前没有签名能力。</summary>
    /// <summary>懒初始化原生组件与 Node 桥；失败返回 false，不伪造能力。</summary>
    /// <summary>只保留当前版本/路径对应的暂存目录，清理旧副本。</summary>
    private void PruneStagedNative(string keepRoot)
    {
        try
        {
            var root = Path.GetDirectoryName(keepRoot);
            if (root == null || !Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (string.Equals(dir, keepRoot, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch { }
    }

    private bool EnsureNative()
    {
        if (_bridge != null && _components != null) return true;
        if (_components == null)
        {
            _components = SodaNative.Locate(out var note);
            _nativeNote = note;
            if (_components == null) return false;
            // 组件可能散落在官方安装目录各处：把三件公共组件复制到私有同目录，普通启动不依赖 work 环境。
            var stagedRoot = Path.Combine(_root, "native", SodaNative.StageKey(_components));
            var staged = SodaNative.Stage(_components, stagedRoot);
            if (staged == null)
            {
                _nativeNote = "无法把原生组件暂存到私有目录";
                return false;
            }
            _components = staged;
            PruneStagedNative(stagedRoot);
        }
        var node = SodaNative.FindNode();
        if (node == null) { _nativeNote = "未找到 Node 运行时"; return false; }
        var script = Path.Combine(AppContext.BaseDirectory, "Runtime", "Soda", "helper.mjs");
        if (!File.Exists(script)) { _nativeNote = "缺少 Runtime/Soda/helper.mjs"; return false; }
        _bridge ??= new SodaNodeBridge(node, script, _components.Root);
        return true;
    }

    private async Task<IReadOnlyDictionary<string, string>?> SignAsync(
        string url, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken ct)
    {
        if (!EnsureNative() || _components is null || _components.BdmsNode is null) return null;
        var headerLines = string.Join("\r\n", headers.Select(h => $"{h.Key}\r\n{h.Value}"));
        var components = _components!;
        var bridge = _bridge!;
        var data = await bridge.InvokeAsync("sign", new
        {
            bdmsNode = components.BdmsNode,
            metaSec = components.MetaSec,
            url,
            headerLines,
            deviceId = _deviceId,
        }, 8000, ct).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("headers", out var signed) ||
            signed.ValueKind != JsonValueKind.Object)
            return null;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in signed.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String) result[property.Name] = property.Value.GetString() ?? "";
        LastSignHeaderCount = result.Count;
        return result;
    }

    private bool LoadFlag(string name)
    {
        try
        {
            var path = Path.Combine(_root, "device.json");
            if (!File.Exists(path)) return false;
            var ids = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return ids != null && ids.TryGetValue(name, out var value) && value == "1";
        }
        catch { return false; }
    }

    private string LoadOrCreateId(string name)
    {
        var path = Path.Combine(_root, "device.json");
        Dictionary<string, string>? ids = null;
        try
        {
            if (File.Exists(path))
                ids = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        }
        catch { }
        ids ??= new Dictionary<string, string>();
        if (!ids.TryGetValue(name, out var value) || value.Length < 8)
        {
            var random = new Random();
            value = string.Concat(Enumerable.Range(0, 19).Select(_ => random.Next(0, 10)));
            ids[name] = value;
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(ids));
                File.Move(tmp, path, true);
            }
            catch (Exception ex) { App.LogError("SodaDevice", ex.Message); }
        }
        return value;
    }

    private string Account => _session.AccountKey;

    /// <summary>账号代次：登录/退出都会递增；异步请求收尾时用它丢弃旧账号结果。</summary>
    internal int Generation => System.Threading.Volatile.Read(ref _generation);

    /// <summary>最近一次签名返回的头部数量（0 = 未签名），供诊断/测试。</summary>
    internal int LastSignHeaderCount { get; private set; }

    #region 设备与原生组件

    /// <summary>
    /// 准备设备：已注册的设备直接复用；否则必须用官方组件完成匿名注册。
    /// 缺组件/缺 Node/注册失败/取消都会抛出，绝不用自造 id 冒充注册成功。
    /// </summary>
    public async Task<SodaDeviceInfo> PrepareAsync(CancellationToken ct = default)
    {
        if (_registered && _deviceId.Length > 0 && _installId.Length > 0)
        {
            EnsureNative(); // 已注册也把签名/解密桥准备好，否则后续账号请求无签名
            return new SodaDeviceInfo(_deviceId, _installId, ProtocolVersionCode);
        }

        if (!EnsureNative())
            throw new SodaException(SodaErrorKind.Unavailable,
                _nativeNote ?? "原生组件不可用");

        var components = _components!;
        var bridge = _bridge!;

        var data = await bridge.InvokeAsync("register", new
        {
            deviceId = _deviceId,
            installId = _installId,
            deviceNode = components.DeviceNode,
            bdmsNode = components.BdmsNode,
            metaSec = components.MetaSec,
        }, 15000, ct).ConfigureAwait(false);

        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("device_id", out var did) || string.IsNullOrWhiteSpace(did.GetString()))
            throw new SodaException(SodaErrorKind.Unavailable, "设备注册未返回 device_id");

        _deviceId = did.GetString()!;
        if (data.TryGetProperty("install_id", out var iid) && !string.IsNullOrWhiteSpace(iid.GetString()))
            _installId = iid.GetString()!;
        _registered = true;
        SaveDeviceIds();
        _http = CreateHttp();
        return new SodaDeviceInfo(_deviceId, _installId, ProtocolVersionCode);
    }

    private void SaveDeviceIds()
    {
        try
        {
            var path = Path.Combine(_root, "device.json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["device_id"] = _deviceId,
                ["install_id"] = _installId,
                ["registered"] = _registered ? "1" : "0",
            }));
            File.Move(tmp, path, true);
        }
        catch { }
    }

    #endregion

    #region 模式与推荐

    public async Task<IReadOnlyList<SodaMode>> GetModesAsync(CancellationToken ct = default)
    {
        var cached = _cache.ReadJson("modes", "shared", "list");
        if (cached != null)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<SodaMode>>(cached);
                if (list is { Count: > 0 }) return list;
            }
            catch { }
        }

        using var doc = await Http.PostAsync("/luna/pc/feed/mode", new { }, ct).ConfigureAwait(false);
        var result = new List<SodaMode> { new("default", "推荐", Preference: "default") };

        try
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("feed_mode_block", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            {
                var scenes = new List<SodaMode>();
                foreach (var block in blocks.EnumerateArray())
                {
                    if (!block.TryGetProperty("feed_mode", out var modes) || modes.ValueKind != JsonValueKind.Array) continue;
                    foreach (var mode in modes.EnumerateArray())
                    {
                        var text = mode.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                        var entity = mode.TryGetProperty("entity", out var e) ? e : default;
                        if (entity.ValueKind == JsonValueKind.Object &&
                            entity.TryGetProperty("feed_scene_mode", out var scene) && scene.ValueKind == JsonValueKind.Object)
                        {
                            var sub = scene.TryGetProperty("sub_queue_type", out var s) ? s.GetString() ?? "" : "";
                            int? sceneId = scene.TryGetProperty("scene_mode_id", out var sid) && sid.ValueKind == JsonValueKind.Number
                                ? sid.GetInt32() : null;
                            if (sub.Length > 0 && !scenes.Any(x => x.Key == sub))
                                scenes.Add(new SodaMode(sub, text, SceneId: sceneId));
                        }
                    }
                }

                var preferred = new[] { "scene_mode_chill", "scene_mode_sport", "scene_mode_happy", "scene_mode_cantonese", "scene_mode_car_mode", "scene_mode_fish" };
                result.Add(new SodaMode("familiar", "熟悉模式", Preference: "familiar"));
                result.Add(new SodaMode("fresh", "新鲜模式", Preference: "fresh"));
                foreach (var key in preferred)
                {
                    var hit = scenes.FirstOrDefault(x => x.Key == key);
                    if (hit != null) result.Add(hit);
                }
                foreach (var scene in scenes.Where(x => result.All(r => r.Key != x.Key)))
                    result.Add(scene);
            }
        }
        catch (Exception ex) { App.LogError("SodaModes.Parse", ex.Message); }

        if (result.Count <= 1) throw new SodaException(SodaErrorKind.Protocol, "模式列表为空");
        _cache.WriteJson("modes", "shared", "list", JsonSerializer.Serialize(result));
        return result;
    }

    public async Task<List<TrackItem>> GetFeedAsync(SodaMode mode, IReadOnlyList<string> playedIds, int batchNumber, CancellationToken ct = default)
    {
        await _feedGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var played = playedIds
                .Select(StripId)
                .Where(id => id.Length > 1)
                .Select(id => (object)new { id, type = "track" })
                .ToList();

            var body = new Dictionary<string, object?>
            {
                ["played_media"] = played,
                ["is_first_request"] = batchNumber <= 1,
                ["is_did_first_request"] = false,
                ["feed_counts"] = new { mix_session_count = Math.Max(1, batchNumber) },
                ["count"] = 20,
            };
            if (!string.IsNullOrEmpty(mode.Preference) && mode.Preference != "default" && mode.SceneId == null)
                body["feed_preference"] = new { preference_mode = mode.Preference };
            else if (mode.SceneId is { } sceneId)
                body["feed_preference"] = new { scene_mode_id = sceneId };

            using var doc = await Http.PostAsync("/luna/pc/feed/song-tab", body, ct).ConfigureAwait(false);
            var tracks = new List<TrackItem>();
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var type = item.TryGetProperty("type", out var ty) ? ty.GetString() : "";
                    if (type != "track") continue;
                    if (!item.TryGetProperty("entity", out var entity)) continue;
                    if (!entity.TryGetProperty("track_wrapper", out var wrapper)) continue;
                    if (!wrapper.TryGetProperty("track", out var track)) continue;
                    var mapped = MapTrack(track);
                    if (mapped != null) tracks.Add(mapped);
                }
            }
            return tracks;
        }
        finally { _feedGate.Release(); }
    }

    #endregion

    #region 喜欢

    public async Task<SodaTrackPage> GetLikedAsync(string cursor, CancellationToken ct = default)
    {
        RequireLogin();
        var playlistId = await EnsureFavoritesPlaylistAsync(ct).ConfigureAwait(false);
        if (playlistId == null) return new SodaTrackPage(new List<TrackItem>(), "", false);

        using var doc = await Http.GetAsync("/luna/pc/playlist/detail", new Dictionary<string, string>
        {
            ["playlist_id"] = playlistId,
            ["cursor"] = cursor ?? "",
            ["count"] = "50",
        }, ct).ConfigureAwait(false);

        return ParseLikedPage(doc.RootElement);
    }

    /// <summary>
    /// 官方 GetPlaylistDetailResponse：media_resources（优先，Playlist.vue 实际使用）、
    /// tracks（契约字段，兜底）、has_more/next_cursor。缺字段时报协议错误，不当空歌单。
    /// </summary>
    internal SodaTrackPage ParseLikedPage(JsonElement root)
    {
        var items = new List<TrackItem>();
        var hasResourceField = root.TryGetProperty("media_resources", out var resources) && resources.ValueKind == JsonValueKind.Array;
        var hasTrackField = root.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array;

        if (hasResourceField)
        {
            foreach (var entry in resources.EnumerateArray())
            {
                var track = ResourceTrack(entry);
                if (track == null) continue;
                var mapped = MapTrack(track.Value);
                if (mapped != null)
                {
                    mapped.IsFavorite = true;
                    items.Add(mapped);
                }
            }
        }
        else if (hasTrackField)
        {
            foreach (var entry in tracks.EnumerateArray())
            {
                var mapped = MapTrack(entry);
                if (mapped != null)
                {
                    mapped.IsFavorite = true;
                    items.Add(mapped);
                }
            }
        }
        else
        {
            throw new SodaException(SodaErrorKind.Protocol, "歌单响应缺少 media_resources/tracks 字段");
        }

        var next = root.TryGetProperty("next_cursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() ?? "" : "";
        var hasMore = root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
        return new SodaTrackPage(items, next, hasMore);
    }

    /// <summary>media_resources[i].entity 里的 track / track_wrapper.track。</summary>
    private static JsonElement? ResourceTrack(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        if (!entry.TryGetProperty("entity", out var entity) || entity.ValueKind != JsonValueKind.Object) return null;
        if (entity.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object) return track;
        if (entity.TryGetProperty("track_wrapper", out var wrapper) && wrapper.ValueKind == JsonValueKind.Object &&
            wrapper.TryGetProperty("track", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object) return wrapped;
        return null;
    }

    private async Task<string?> EnsureFavoritesPlaylistAsync(CancellationToken ct)
    {
        if (_favoritesPlaylistId is { Length: > 0 }) return _favoritesPlaylistId;
        try
        {
            using var doc = await Http.GetAsync("/luna/pc/me/playlist", null, ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("playlists", out var playlists) && playlists.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in playlists.EnumerateArray())
                {
                    var type = p.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : -1;
                    if (type != 1) continue;
                    _favoritesPlaylistId = p.TryGetProperty("id", out var id) ? id.GetString() : null;
                    break;
                }
            }
        }
        catch (SodaException ex) when (ex.Kind == SodaErrorKind.Auth)
        {
            ClearSession();
            throw;
        }
        return _favoritesPlaylistId;
    }

    #endregion

    #region 登录

    /// <summary>校验传入 Cookie（来自本次扫码），成功才写入独立 DPAPI 会话。</summary>
    public async Task<bool> AcceptLoginAsync(string cookieHeader, CancellationToken ct = default)
    {
        if (!SodaSession.TryParseCookieHeader(cookieHeader, out var normalized))
            throw new SodaException(SodaErrorKind.Auth, "Cookie 格式无效");

        var gen = Generation;
        var http = new SodaHttp(() => normalized, () => _deviceId, () => _installId, () => true);
        using var doc = await http.GetAsync("/luna/pc/me", null, ct).ConfigureAwait(false);
        var info = ParseUser(doc.RootElement);
        if (info == null) throw new SodaException(SodaErrorKind.Auth, "登录校验失败：未返回账号信息");
        ct.ThrowIfCancellationRequested();
        if (gen != Generation) throw new SodaException(SodaErrorKind.Auth, "账号已切换");

        _session.Clear();
        _session.SetCookie(normalized);
        _session.Nickname = info.Value.Nickname;
        _session.UserId = info.Value.Uid;
        _session.AvatarUrl = info.Value.Avatar;
        _session.Save();
        _favoritesPlaylistId = null;
        _http = CreateHttp();
        System.Threading.Interlocked.Increment(ref _generation);
        LoginChanged?.Invoke();
        return true;
    }

    public async Task VerifyLoginAsync(CancellationToken ct = default)
    {
        if (!_session.IsLoggedIn) return;
        try
        {
            using var doc = await Http.GetAsync("/luna/pc/me", null, ct).ConfigureAwait(false);
            var info = ParseUser(doc.RootElement);
            if (info == null) return;
            _session.Nickname = info.Value.Nickname;
            _session.UserId = info.Value.Uid;
            _session.AvatarUrl = info.Value.Avatar;
            _session.Save();
            LoginChanged?.Invoke();
        }
        catch (SodaException ex) when (ex.Kind == SodaErrorKind.Auth)
        {
            ClearSession();
        }
    }

    public void Logout()
    {
        ClearSession();
        LoginChanged?.Invoke();
    }

    private void ClearSession()
    {
        _session.Clear();
        _favoritesPlaylistId = null;
        _http = CreateHttp();
        System.Threading.Interlocked.Increment(ref _generation);
    }

    private static (string Nickname, string Uid, string Avatar)? ParseUser(JsonElement root)
    {
        foreach (var candidate in FindObjectCandidates(root))
        {
            var nickname = GetString(candidate, "nickname") ?? GetString(candidate, "name");
            var uid = GetString(candidate, "id") ?? GetString(candidate, "uid") ?? GetString(candidate, "user_id");
            var avatar = GetString(candidate, "medium_avatar_url") ?? GetString(candidate, "avatar_url")
                         ?? GetString(candidate, "thumb_avatar_url") ?? GetString(candidate, "avatar");
            if (!string.IsNullOrEmpty(uid) && nickname != null)
                return (nickname, uid, avatar ?? "");
        }
        return null;
    }

    private static IEnumerable<JsonElement> FindObjectCandidates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                    foreach (var nested in FindObjectCandidates(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var nested in FindObjectCandidates(item)) yield return nested;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private void RequireLogin()
    {
        if (!_session.IsLoggedIn) throw new SodaException(SodaErrorKind.Auth, "需要登录汽水音乐");
    }

    private SodaHttp Http => _http ??= CreateHttp();

    #endregion
}
