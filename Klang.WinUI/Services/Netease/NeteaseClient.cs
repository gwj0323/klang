using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Klang_WinUI.Services.Netease;

/// <summary>
/// 网易云 WeAPI 加密与请求（移植自 vendor/pyncm 的 weapi 实现，二者同为 GPL-3.0）。
/// 参数两次 AES-CBC 加密，第二次的密钥用固定 RSA 密文 encSecKey 传递。
/// </summary>
public static class NeteaseCrypto
{
    private const string AesKey = "0CoJUm6Qyw8W8jud";
    private const string AesIv = "0102030405060708";

    // 与 pyncm 相同的固定第二密钥及其 RSA 密文，省去每次现场做 RSA
    private const string SecondKey = "mos9527ItoooItop";
    private const string EncSecKey =
        "01a1c399271006da676da55763419f10f0e589515c49530b33418eec82202fc42dae0cd3aa4a2b7bdc3dafa7c6a918e405f3cdbc5d0349ef86913fc2dbe8764ed782e202e7828b547e85f6ae28b8b120bcf5fd3777a55731521612dcaff9813246a42876303b0f2307c9f264671ddc87159ff162e689fdfae5acb3af10250754";

    private static string AesCbcBase64(string plain, string key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.IV = Encoding.UTF8.GetBytes(AesIv);
        var data = Encoding.UTF8.GetBytes(plain);
        return Convert.ToBase64String(aes.EncryptCbc(data, aes.IV, PaddingMode.PKCS7));
    }

    public static Dictionary<string, string> Weapi(string json) => new()
    {
        ["params"] = AesCbcBase64(AesCbcBase64(json, AesKey), SecondKey),
        ["encSecKey"] = EncSecKey
    };
}

/// <summary>带 Cookie 的网易云 HTTP 客户端（手动管理 Cookie，便于加密持久化）。</summary>
public sealed class NeteaseClient
{
    private const string Host = "https://music.163.com";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly object _cookieLock = new();
    private readonly Dictionary<string, string> _cookies = new();

    public NeteaseClient()
    {
        _http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        ResetCookies();
    }

    public HttpClient Http => _http;

    public string CsrfToken
    {
        get { lock (_cookieLock) return _cookies.TryGetValue("__csrf", out var v) ? v : ""; }
    }

    public bool HasMusicU
    {
        get { lock (_cookieLock) return _cookies.ContainsKey("MUSIC_U"); }
    }

    /// <summary>清空并恢复为未登录的 PC 客户端标识（避免风控）。</summary>
    public void ResetCookies()
    {
        lock (_cookieLock)
        {
            _cookies.Clear();
            _cookies["os"] = "pc";
            _cookies["appver"] = "3.0.18.203152";
            _cookies["channel"] = "netease";
        }
    }

    public void SetCookies(IDictionary<string, string> cookies)
    {
        lock (_cookieLock)
        {
            foreach (var kv in cookies) _cookies[kv.Key] = kv.Value;
        }
    }

    public Dictionary<string, string> GetCookies()
    {
        lock (_cookieLock) return new Dictionary<string, string>(_cookies);
    }

    /// <summary>解析 Cookie 字符串（支持粘贴整段 Cookie 或 Set-Cookie，忽略 Path/Expires 等属性）。</summary>
    public static Dictionary<string, string> ParseCookieString(string raw)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "path", "domain", "expires", "max-age", "secure", "httponly", "samesite" };
        foreach (Match m in Regex.Matches(raw, @"([A-Za-z0-9_\-\.]+)=([^;,\s][^;\r\n]*?)(?:;|,|$)"))
        {
            var k = m.Groups[1].Value.Trim();
            var v = m.Groups[2].Value.Trim();
            if (k.Length > 0 && !ignored.Contains(k)) result[k] = v;
        }
        return result;
    }

    private string CookieHeader()
    {
        lock (_cookieLock) return string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private void AbsorbSetCookie(HttpResponseMessage rsp)
    {
        if (!rsp.Headers.TryGetValues("Set-Cookie", out var values)) return;
        lock (_cookieLock)
        {
            foreach (var line in values)
            {
                var first = line.Split(';', 2)[0];
                int eq = first.IndexOf('=');
                if (eq <= 0) continue;
                var name = first[..eq].Trim();
                var val = first[(eq + 1)..].Trim();
                if (val.Length == 0 || val == "deleted") _cookies.Remove(name);
                else _cookies[name] = val;
            }
        }
    }

    /// <summary>调用 weapi 接口并返回 JSON；网络或解析失败返回 null。</summary>
    public async Task<JsonNode?> WeapiAsync(string path, JsonObject? payload = null, CancellationToken ct = default)
    {
        payload ??= new JsonObject();
        string csrf = CsrfToken;
        payload["csrf_token"] = csrf;

        var form = new FormUrlEncodedContent(NeteaseCrypto.Weapi(payload.ToJsonString()));
        var url = $"{Host}{path}?csrf_token={Uri.EscapeDataString(csrf)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", Host);
        req.Headers.TryAddWithoutValidation("Cookie", CookieHeader());

        using var rsp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        AbsorbSetCookie(rsp);
        var text = await rsp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
