using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace Klang_WinUI.Services.Soda;

/// <summary>
/// 独立会话：DPAPI 加密后的 Cookie 头 + 账号标识。
/// 账号隔离由目录名（账号哈希）保证；退出登录即删除文件。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SodaSession
{
    public string CookieHeader { get; private set; } = "";
    public string Nickname { get; set; } = "";
    public string UserId { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public bool IsLoggedIn => CookieHeader.Length > 0;

    private readonly string _root;
    private string FilePath => Path.Combine(_root, "session.bin");

    public SodaSession(string root) => _root = root;

    public string AccountKey
    {
        get
        {
            if (!IsLoggedIn) return "guest";
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(UserId.Length > 0 ? UserId : CookieHeader));
            return "u_" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }
    }

    public bool TryLoad()
    {
        try
        {
            if (!File.Exists(FilePath)) return false;
            var protectedBytes = File.ReadAllBytes(FilePath);
            var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            using var doc = JsonDocument.Parse(plain);
            var root = doc.RootElement;
            CookieHeader = root.TryGetProperty("cookie", out var c) ? c.GetString() ?? "" : "";
            Nickname = root.TryGetProperty("nickname", out var n) ? n.GetString() ?? "" : "";
            UserId = root.TryGetProperty("uid", out var u) ? u.GetString() ?? "" : "";
            AvatarUrl = root.TryGetProperty("avatar", out var a) ? a.GetString() ?? "" : "";
            return IsLoggedIn;
        }
        catch (Exception ex)
        {
            App.LogError("SodaSession.Load", ex.Message);
            Clear();
            return false;
        }
    }

    /// <summary>仅由 SodaService 在登录校验通过后调用。</summary>
    public void SetCookie(string cookie) => CookieHeader = cookie;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_root);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
            {
                ["cookie"] = CookieHeader,
                ["nickname"] = Nickname,
                ["uid"] = UserId,
                ["avatar"] = AvatarUrl,
            });
            var protectedBytes = ProtectedData.Protect(payload, null, DataProtectionScope.CurrentUser);
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            App.LogError("SodaSession.Save", ex.Message);
        }
    }

    public void Clear()
    {
        CookieHeader = "";
        Nickname = "";
        UserId = "";
        AvatarUrl = "";
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    /// <summary>解析用户提供的 "a=b; c=d" Cookie 头；只接受安全字符，拒绝换行/分号注入。</summary>
    public static bool TryParseCookieHeader(string raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var pairs = new List<string>();
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0) continue;
            var name = part[..index].Trim();
            var value = part[(index + 1)..].Trim();
            if (name.Length == 0 || value.Length == 0) continue;
            if (!name.All(ch => char.IsAsciiLetterOrDigit(ch) || "!#$%&'*+-.^_`|~".Contains(ch))) return false;
            if (value.Any(ch => ch is ';' or '\r' or '\n' or '\0')) return false;
            if (value.Length > 16384) return false;
            pairs.Add($"{name}={value}");
        }
        if (pairs.Count == 0) return false;
        normalized = string.Join("; ", pairs);
        return true;
    }
}
