using System.Text.Json;

namespace Klang_WinUI.Services.Soda;

/// <summary>按账号隔离的磁盘缓存：封面、曲目/音源、歌词、模式列表。</summary>
internal sealed class SodaCache
{
    private readonly string _root;

    public SodaCache(string root) => _root = root;

    public string Dir(string kind, string account)
    {
        var dir = Path.Combine(_root, account, kind);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string PathFor(string kind, string account, string key, string extension)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key)), 0, 12).ToLowerInvariant();
        return Path.Combine(Dir(kind, account), hash + extension);
    }

    public bool Exists(string kind, string account, string key, string extension) =>
        File.Exists(PathFor(kind, account, key, extension));

    public string? ReadJson(string kind, string account, string key)
    {
        var path = PathFor(kind, account, key, ".json");
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    public void WriteJson(string kind, string account, string key, string json)
    {
        try
        {
            var path = PathFor(kind, account, key, ".json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
        }
        catch (Exception ex) { App.LogError("SodaCache.Write", ex.Message); }
    }

    public static void WriteBytesAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
    }
}
