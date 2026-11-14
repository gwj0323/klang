using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Klang_WinUI.Services.Soda;

/// <summary>官方 3.7.0 原生组件定位（只读；不启动官方客户端，不读它的账号数据）。</summary>
internal static class SodaNative
{
    public sealed record Components(string Root, string? DeviceNode, string? BdmsNode, string? MetaSec, string Version);

    /// <summary>物理用户目录优先：开发环境可能重定向 LOCALAPPDATA，这里直接拼真实用户配置文件路径。</summary>
    private static IEnumerable<string> CandidateRoots()
    {
        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(profile))
            yield return Path.Combine(profile, "AppData", "Local", "Programs", "Soda Music");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Soda Music");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Soda Music");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Soda Music");
    }

    /// <summary>只接受固定 3.7.0；发现其它版本时返回 null，并在 note 里说明，避免新旧协议混用。</summary>
    public static Components? Locate(out string? note)
    {
        note = null;
        var overrideDir = Environment.GetEnvironmentVariable("KLANG_SODA_SDK_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir) && Directory.Exists(overrideDir))
        {
            var components = Inspect(overrideDir, "override");
            if (components != null) return components;
            note = "KLANG_SODA_SDK_DIR 下没有 device.node/bdms.node";
        }

        foreach (var root in CandidateRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var versionDir in VersionDirs(root))
            {
                var version = Path.GetFileName(versionDir);
                if (version != "3.7.0")
                {
                    note = $"只支持 3.7.0，发现 {version}";
                    continue;
                }
                var components = Inspect(versionDir, version);
                if (components != null) return components;
            }
        }
        return null;
    }

    private static Components? Inspect(string dir, string version)
    {
        var device = FindFile(dir, "device.node");
        var bdms = FindFile(dir, "bdms.node");
        if (device == null && bdms == null) return null;
        // metasecml.dll 有多个副本（如 mssdk\ 与 app.asar.unpacked\），优先取与 bdms.node 同目录的那份。
        var meta = NearbyMeta(bdms) ?? NearbyMeta(device) ?? FindFile(dir, "metasecml.dll");
        return new Components(dir, device, bdms, meta, version);
    }

    private static string? NearbyMeta(string? module)
    {
        if (string.IsNullOrEmpty(module)) return null;
        var candidate = Path.Combine(Path.GetDirectoryName(module)!, "metasecml.dll");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>优先 3.7.0；没有则取最高版本目录（物理路径下通常只有一个版本目录）。</summary>
    private static IEnumerable<string> VersionDirs(string root)
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); } catch { yield break; }
        var parsed = dirs
            .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .OrderByDescending(x => x.Version ?? new Version(0, 0));
        var preferred = parsed.FirstOrDefault(x => x.Version?.ToString(3) == "3.7.0");
        if (preferred.Dir != null) yield return preferred.Dir;
        foreach (var item in parsed)
            if (item.Dir != preferred.Dir) yield return item.Dir;
    }

    private static string? FindFile(string dir, string name)
    {
        try
        {
            return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>稳定目录名：根路径 + 版本，避免同一版本被重复拷贝。</summary>
    public static string StageKey(Components components)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(
            System.Text.Encoding.UTF8.GetBytes(components.Root + "|" + components.Version)), 0, 8).ToLowerInvariant();
        return components.Version == "override" ? "override" : $"{components.Version}-{hash}";
    }

    /// <summary>
    /// 把 bdms.node / device.node / metasecml.dll 三件公共组件复制到私有目录（同目录）。
    /// 只复制这三件，绝不动官方目录、不复制账号缓存；文件缺失或大小不符时返回 null。
    /// </summary>
    public static Components? Stage(Components components, string destRoot)
    {
        if (string.Equals(Path.GetFullPath(components.Root), Path.GetFullPath(destRoot), StringComparison.OrdinalIgnoreCase))
            return components;
        try
        {
            Directory.CreateDirectory(destRoot);
        }
        catch { return null; }

        var device = CopyComponent(components.DeviceNode, destRoot);
        var bdms = CopyComponent(components.BdmsNode, destRoot);
        var meta = CopyComponent(components.MetaSec, destRoot);
        if (device == null && bdms == null) return null;
        return new Components(destRoot, device, bdms, meta, components.Version);
    }

    private static string? CopyComponent(string? source, string destRoot)
    {
        if (string.IsNullOrEmpty(source) || !File.Exists(source)) return null;
        var target = Path.Combine(destRoot, Path.GetFileName(source));
        try
        {
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length) return target;
            var temp = target + ".tmp";
            File.Copy(source, temp, true);
            if (new FileInfo(temp).Length != new FileInfo(source).Length) { File.Delete(temp); return null; }
            File.Move(temp, target, true);
            return target;
        }
        catch { return null; }
    }

    /// <summary>Node 运行时：PATH → Program Files；找不到返回 null（调用方不得伪造成功）。</summary>
    public static string? FindNode()
    {
        foreach (var name in new[] { "KLANG_SODA_NODE_EXE", "KLANG_SODA_NODE" })
        {
            var explicitPath = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        }

        var onPath = Environment.GetEnvironmentVariable("PATH")?.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.Combine(p.Trim(), "node.exe"))
            .FirstOrDefault(File.Exists);
        if (onPath != null) return onPath;
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(fallback) ? fallback : null;
    }
}

/// <summary>
/// 隐藏的 Node 子进程：官方原生模块只在 Node 宿主里加载。
/// stdin/stdout 各一行有界 JSON；stderr 全程排空；任何路径退出前都 Kill 整棵进程树。
/// </summary>
internal sealed class SodaNodeBridge
{
    private const int MaxJsonBytes = 128 * 1024;
    private readonly string _nodePath;
    private readonly string _scriptPath;
    private readonly string? _workingDirectory;

    public SodaNodeBridge(string nodePath, string scriptPath, string? workingDirectory = null)
    {
        _nodePath = nodePath;
        _scriptPath = scriptPath;
        _workingDirectory = workingDirectory;
    }

    public async Task<JsonElement> InvokeAsync(string command, object payload, int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_nodePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (!string.IsNullOrWhiteSpace(_workingDirectory) && Directory.Exists(_workingDirectory))
        {
            psi.WorkingDirectory = _workingDirectory;
            // 只把组件目录放在 PATH 最前，供 DLL 依赖解析；不引入其它私有路径。
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = _workingDirectory + ";" + currentPath;
        }
        psi.ArgumentList.Add(_scriptPath);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new SodaException(SodaErrorKind.Unavailable, "无法启动 Node 辅助进程", 0, null, ex);
        }

        try
        {
            // stderr 必须排空，否则子进程写满管道会死锁。
            _ = process.StandardError.ReadToEndAsync();

            var payloadJson = JsonSerializer.Serialize(new { command, payload });
            if (payloadJson.Length > MaxJsonBytes) throw new SodaException(SodaErrorKind.Protocol, "请求过大");
            await process.StandardInput.WriteLineAsync(payloadJson).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            process.StandardInput.Close();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            var line = await ReadBoundedLineAsync(process.StandardOutput, MaxJsonBytes, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) throw new SodaException(SodaErrorKind.Unavailable, "Node 辅助进程无输出");

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                throw new SodaException(SodaErrorKind.Unavailable, $"原生组件：{error}");
            }
            return root.TryGetProperty("data", out var data) ? data.Clone() : default;
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }

    /// <summary>边读边限长：超过上限立即失败，不等到整行读完。</summary>
    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, int maxChars, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0) break;
            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (ch == '\n') return builder.ToString().TrimEnd('\r');
                if (builder.Length >= maxChars) throw new SodaException(SodaErrorKind.Protocol, "Node 响应过大");
                builder.Append(ch);
            }
        }
        return builder.ToString();
    }
}
