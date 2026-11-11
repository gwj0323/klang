using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Klang_WinUI.Services;

public sealed record UpdateInfo(
    Version Current,
    Version? Latest,
    string Tag,
    string Name,
    string Notes,
    string PageUrl,
    string? DownloadUrl,
    bool HasUpdate);

/// <summary>
/// 从 GitHub 发布页检查更新。只读公开接口，请求里不带任何用户信息；
/// 只看带 “winui” 的标签（仓库里还有 Qt 版的发布），不自动下载、不自动安装。
/// </summary>
public static class UpdateService
{
    public const string Repo = "gwj0323/klang";
    public const string RepoUrl = "https://github.com/" + Repo;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Klang-WinUI-UpdateCheck");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>当前运行的版本（来自项目文件里的 Version）。</summary>
    public static Version CurrentVersion
    {
        get
        {
            var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (info != null && TryParseVersion(info, out var v)) return v;
            return Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        }
    }

    public static string CurrentVersionText
    {
        get { var v = CurrentVersion; return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}"; }
    }

    private static readonly Regex VersionRx = new(@"(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.Compiled);

    private static bool TryParseVersion(string text, out Version version)
    {
        var m = VersionRx.Match(text);
        if (!m.Success) { version = new Version(0, 0, 0); return false; }
        version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        return true;
    }

    /// <summary>只允许打开本项目 GitHub 下的链接，防止发布内容里夹带别的地址。</summary>
    public static bool IsTrustedUrl(string? url) =>
        url != null && url.StartsWith("https://github.com/" + Repo + "/", StringComparison.OrdinalIgnoreCase)
        || url == RepoUrl;

    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion;

        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden || (int)resp.StatusCode == 429)
            throw new InvalidOperationException(L.T("GitHub 暂时限制了请求次数，请稍后再试。"));
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        Version? best = null;
        JsonElement bestRel = default;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            if (rel.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;
            string tag = rel.GetProperty("tag_name").GetString() ?? "";
            if (!tag.Contains("winui", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryParseVersion(tag, out var v)) continue;
            if (best == null || v > best) { best = v; bestRel = rel; }
        }

        if (best == null)
            return new UpdateInfo(current, null, "", "", "", RepoUrl + "/releases", null, false);

        string pageUrl = bestRel.GetProperty("html_url").GetString() ?? RepoUrl + "/releases";
        string name = bestRel.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        string notes = bestRel.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        string tagName = bestRel.GetProperty("tag_name").GetString() ?? "";

        // 下载链接：优先 x64 的 zip / 安装包
        string? download = null;
        if (bestRel.TryGetProperty("assets", out var assets))
        {
            var list = new List<(string Name, string Url)>();
            foreach (var a in assets.EnumerateArray())
                list.Add((a.GetProperty("name").GetString() ?? "", a.GetProperty("browser_download_url").GetString() ?? ""));
            download = list
                .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                         || a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                         || a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Name.Contains("x64", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Url)
                .FirstOrDefault();
        }

        return new UpdateInfo(current, best, tagName, name, notes, pageUrl, download, best > current);
    }
}
