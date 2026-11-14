using System.Net;
using System.Text;
using System.Text.Json;

namespace Klang_WinUI.Services.Soda;

public enum SodaErrorKind { Network, Protocol, Auth, Verification, Unavailable }

public sealed class SodaException : Exception
{
    public SodaErrorKind Kind { get; }
    public int StatusCode { get; }
    public string? LogId { get; }
    public SodaException(SodaErrorKind kind, string message, int statusCode = 0, string? logId = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
        LogId = logId;
    }
}

/// <summary>
/// 汽水 HTTP 客户端。
/// 安全边界：只有 api.qishui.com 的账号请求带 Cookie；媒体/RPC 绝对 URL 走 HTTPS + 域名白名单 + 逐跳校验，
/// 不携带 Cookie。响应体有上限；签名通过原生桥签名器注入（缺少签名能力时对需要签名的账号端点明确失败）。
/// </summary>
internal sealed class SodaHttp
{
    public const string ApiHost = "api.qishui.com";
    public const string BaseUrl = "https://api.qishui.com";
    private const string Aid = "386088";
    private const string VersionName = "3.7.0";
    private const string VersionCode = "3070000";
    private const int MaxResponseChars = 8 * 1024 * 1024;

    public delegate Task<IReadOnlyDictionary<string, string>?> Signer(
        string url, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken ct);

    private static readonly string[] MediaHostSuffixes =
    {
        ".qishui.com", ".douyinpic.com", ".douyinvod.com", ".byteimg.com", ".snssdk.com", ".zijieapi.com",
    };

    private static readonly string[] SignedPathPrefixes =
    {
        "/luna/pc/feed/", "/luna/pc/track", "/luna/pc/search/", "/luna/pc/me/", "/luna/pc/discover",
        "/luna/pc/recently-played-media", "/luna/pc/comments", "/luna/user/info", "/luna/pc/media-player",
        "/luna/pc/playlist", "/luna/pc/me/playlist",
    };

    private static readonly HttpClient Client = CreateClient();

    private readonly Func<string?> _cookie;
    private readonly Func<string> _deviceId;
    private readonly Func<string> _installId;
    private readonly Func<bool> _loggedIn;
    private readonly Signer? _signer;

    public SodaHttp(Func<string?> cookieProvider, Func<string> deviceId, Func<string> installId, Func<bool> loggedIn, Signer? signer = null)
    {
        _cookie = cookieProvider;
        _deviceId = deviceId;
        _installId = installId;
        _loggedIn = loggedIn;
        _signer = signer;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 8,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SodaMusic/3.7.0");
        return client;
    }

    public static bool IsMediaHostAllowed(string host)
    {
        if (host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase)) return true;
        return MediaHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    public static bool RequiresSignature(string path) =>
        SignedPathPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));

    private string CommonQuery()
    {
        var query = new Dictionary<string, string>
        {
            ["aid"] = Aid, ["app_name"] = "luna_pc", ["region"] = "cn", ["geo_region"] = "cn", ["os_region"] = "cn",
            ["sim_region"] = "", ["device_id"] = _deviceId(), ["cdid"] = "", ["iid"] = _installId(),
            ["version_name"] = VersionName, ["version_code"] = VersionCode, ["channel"] = "official",
            ["build_mode"] = "release", ["ac"] = "wifi", ["tz_name"] = TimeZoneInfo.Local.Id,
            ["device_platform"] = "windows", ["device_type"] = "Windows",
            ["os_version"] = Environment.OSVersion.Version.ToString(3), ["fp"] = _deviceId(),
        };
        return string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<JsonDocument> PostAsync(string path, object? body, CancellationToken ct)
    {
        var url = $"{BaseUrl}{path}?{CommonQuery()}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body == null ? "{}" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        await ApplyHeadersAsync(request, path, url, ct).ConfigureAwait(false);
        return await SendAsync(request, path, ct).ConfigureAwait(false);
    }

    public async Task<JsonDocument> GetAsync(string path, IEnumerable<KeyValuePair<string, string>>? query, CancellationToken ct)
    {
        var extra = query == null ? "" : "&" + string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{BaseUrl}{path}?{CommonQuery()}{extra}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await ApplyHeadersAsync(request, path, url, ct).ConfigureAwait(false);
        return await SendAsync(request, path, ct).ConfigureAwait(false);
    }

    private async Task ApplyHeadersAsync(HttpRequestMessage request, string path, string url, CancellationToken ct)
    {
        request.Headers.TryAddWithoutValidation("x-luna-background-type", "foreground");
        request.Headers.TryAddWithoutValidation("x-luna-is-background-req", "0");
        request.Headers.TryAddWithoutValidation("x-luna-is-local-user", _loggedIn() ? "1" : "0");

        var cookie = _cookie();
        if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("cookie", cookie);

        if (_signer == null) return;
        var lines = new List<KeyValuePair<string, string>>(8);
        foreach (var header in request.Headers)
            foreach (var value in header.Value) lines.Add(new(header.Key, value));
        if (request.Content != null)
            foreach (var header in request.Content.Headers)
                foreach (var value in header.Value) lines.Add(new(header.Key, value));

        var signed = await _signer(url, lines, ct).ConfigureAwait(false);
        if (signed == null) return;
        foreach (var pair in signed) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
    }

    /// <summary>媒体/RPC 绝对地址：HTTPS + 白名单 + 不携带 Cookie + 手动逐跳校验重定向。</summary>
    public async Task<JsonDocument> GetAbsoluteAsync(string url, CancellationToken ct)
    {
        var current = ValidateAbsolute(url);
        for (var hop = 0; hop < 3; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                if (location == null) throw new SodaException(SodaErrorKind.Network, "重定向缺少位置");
                current = ValidateAbsolute(location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(current), location).ToString());
                continue;
            }
            var text = await ReadBoundedAsync(response, ct).ConfigureAwait(false);
            return ParseEnvelope(text, response, "absolute");
        }
        throw new SodaException(SodaErrorKind.Network, "重定向次数过多");
    }

    public async Task<Stream> GetStreamAsync(string url, CancellationToken ct)
    {
        var current = ValidateAbsolute(url);
        for (var hop = 0; hop < 3; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) throw new SodaException(SodaErrorKind.Network, "重定向缺少位置");
                current = ValidateAbsolute(location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(current), location).ToString());
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new SodaException(SodaErrorKind.Network, $"媒体 HTTP {(int)response.StatusCode}");
            }
            return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        throw new SodaException(SodaErrorKind.Network, "重定向次数过多");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static string ValidateAbsolute(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new SodaException(SodaErrorKind.Network, "媒体地址必须是 HTTPS");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new SodaException(SodaErrorKind.Network, "媒体地址含凭据");
        if (!IsMediaHostAllowed(uri.Host)) throw new SodaException(SodaErrorKind.Network, $"媒体域名不在白名单：{uri.Host}");
        return uri.ToString();
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, string path, CancellationToken ct)
    {
        if (_signer == null && _loggedIn() && RequiresSignature(path))
            throw new SodaException(SodaErrorKind.Unavailable, "缺少原生签名能力，账号接口不可用");

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SodaException(SodaErrorKind.Network, "请求超时");
        }
        catch (HttpRequestException)
        {
            // 不把异常原文（可能含签名 URL/查询参数）带给 UI 或日志。
            throw new SodaException(SodaErrorKind.Network, "网络请求失败");
        }

        using (response)
        {
            var logId = response.Headers.TryGetValues("x-tt-logid", out var logIds) ? logIds.FirstOrDefault() : null;
            if (response.Headers.Contains("bdturing-verify") || response.Headers.Contains("x-tt-verify-passport-decision"))
                throw new SodaException(SodaErrorKind.Verification, "需要到官方客户端完成安全验证", (int)response.StatusCode, logId);

            var text = await ReadBoundedAsync(response, ct).ConfigureAwait(false);
            return ParseEnvelope(text, response, path);
        }
    }

    private static JsonDocument ParseEnvelope(string text, HttpResponseMessage response, string path)
    {
        var logId = response.Headers.TryGetValues("x-tt-logid", out var logIds) ? logIds.FirstOrDefault() : null;
        if (!response.IsSuccessStatusCode)
            throw new SodaException(SodaErrorKind.Network, $"HTTP {(int)response.StatusCode} {path}", (int)response.StatusCode, logId);
        if (text.Length == 0)
            throw new SodaException(SodaErrorKind.Unavailable, $"空响应 {path}", (int)response.StatusCode, logId);

        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException ex) { throw new SodaException(SodaErrorKind.Protocol, "响应不是 JSON", (int)response.StatusCode, logId, ex); }

        var root = document.RootElement;
        var status = root.TryGetProperty("status_code", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
        if (status != 0)
        {
            var message = root.TryGetProperty("status_info", out var info) && info.TryGetProperty("status_msg", out var m)
                ? m.GetString() : $"status_code={status}";
            document.Dispose();
            throw new SodaException(status == 1000016 ? SodaErrorKind.Auth : SodaErrorKind.Protocol,
                string.IsNullOrWhiteSpace(message) ? $"status_code={status}" : message, (int)response.StatusCode, logId);
        }
        return document;
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[16 * 1024];
        var builder = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (builder.Length + read > MaxResponseChars)
                throw new SodaException(SodaErrorKind.Protocol, "响应体过大");
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }
}
