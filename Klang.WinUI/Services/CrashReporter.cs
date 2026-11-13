using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Klang_WinUI.Services;

/// <summary>
/// 补全 error.log 记不到的闪退：
/// 原生层崩溃（音频驱动、Win2D、WinUI 的 fail-fast 0xc000027b 等）不会经过 .NET 的异常处理，进程直接被系统结束。
/// 做法：启动时写一个“运行中”标记，正常退出时删除；下次启动发现标记还在，就说明上次是异常结束，
/// 这时从 Windows 事件日志（应用程序）里取出 Klang 的崩溃 / 无响应记录，连同上次最后的操作记录一起写进 error.log。
/// </summary>
public static class CrashReporter
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klang");
    private static readonly string MarkerPath = Path.Combine(Dir, "running.lock");
    private static readonly string TrailPath = Path.Combine(Dir, "trail.log");
    private static readonly string PrevTrailPath = Path.Combine(Dir, "trail.prev.log");

    private const int MaxTrailBytes = 256 * 1024;
    private static readonly object _gate = new();
    private static StreamWriter? _trail;
    private static bool _ownsMarker;   // 只有写下标记的那个进程退出时才删除它

    public static string AppVersion =>
        typeof(CrashReporter).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>启动时调用（越早越好）。</summary>
    public static void Start()
    {
        DateTime? lastStart = null;
        bool unclean = false;
        try
        {
            Directory.CreateDirectory(Dir);
            if (File.Exists(MarkerPath))
            {
                var parts = File.ReadAllText(MarkerPath).Trim().Split('|');
                if (DateTime.TryParse(parts[0], null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                    lastStart = t;
                // 标记属于一个仍在运行的 Klang（同时开了两个）：不算崩溃，也不动它的记录
                if (parts.Length > 1 && int.TryParse(parts[1], out var pid) && IsKlangAlive(pid)) return;
                unclean = true;
            }

            // 上次的操作记录留一份，用来写进崩溃报告
            if (File.Exists(TrailPath)) File.Move(TrailPath, PrevTrailPath, true);

            File.WriteAllText(MarkerPath,
                $"{DateTime.UtcNow:O}|{Environment.ProcessId}|{AppVersion}");
            _ownsMarker = true;

            // 每写一行就落盘：进程被系统直接结束时，已写入的内容也不会丢
            _trail = new StreamWriter(new FileStream(TrailPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false)) { AutoFlush = true };
            Note($"启动 Klang {AppVersion}，{Environment.OSVersion.VersionString}");
        }
        catch { }

        if (unclean)
        {
            var since = lastStart ?? DateTime.UtcNow.AddDays(-1);
            Task.Run(() => ReportPreviousCrash(since));
        }
    }

    /// <summary>正常退出时调用：删除“运行中”标记。</summary>
    public static void Stop()
    {
        try
        {
            Note("正常退出");
            lock (_gate) { _trail?.Dispose(); _trail = null; }
            if (_ownsMarker) File.Delete(MarkerPath);
        }
        catch { }
    }

    /// <summary>记一条操作记录（播放了哪首、打开了什么界面……），崩溃时用来判断当时在做什么。</summary>
    public static void Note(string what)
    {
        try
        {
            lock (_gate)
            {
                if (_trail == null) return;
                if (_trail.BaseStream.Length > MaxTrailBytes)
                {
                    _trail.BaseStream.SetLength(0);
                    _trail.BaseStream.Position = 0;
                }
                _trail.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {what}");
            }
        }
        catch { }
    }

    private static bool IsKlangAlive(int pid)
    {
        if (pid == Environment.ProcessId) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("Klang", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void ReportPreviousCrash(DateTime sinceUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"上次运行没有正常退出（启动于 {sinceUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}）");

        var events = QueryEvents(sinceUtc);
        if (events.Count > 0)
        {
            sb.AppendLine("— Windows 事件日志 —");
            foreach (var e in events) sb.AppendLine(e.Trim()).AppendLine();
        }
        else
        {
            sb.AppendLine("Windows 事件日志里没有对应的崩溃记录（可能是被任务管理器结束、关机或断电）。");
        }

        try
        {
            if (File.Exists(PrevTrailPath))
            {
                var lines = File.ReadAllLines(PrevTrailPath);
                int from = Math.Max(0, lines.Length - 40);
                sb.AppendLine("— 最后的操作记录 —");
                for (int i = from; i < lines.Length; i++) sb.AppendLine(lines[i]);
            }
        }
        catch { }

        App.LogError("Crash (previous session)", sb.ToString().TrimEnd());
    }

    /// <summary>用系统自带的 wevtutil 查询“应用程序”日志：1000 崩溃、1002 无响应、1026 .NET 运行时错误。</summary>
    private static List<string> QueryEvents(DateTime sinceUtc)
    {
        var result = new List<string>();
        try
        {
            var start = sinceUtc.AddSeconds(-5).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var query = $"*[System[(EventID=1000 or EventID=1002 or EventID=1026) and TimeCreated[@SystemTime>='{start}']]]";
            var psi = new ProcessStartInfo("wevtutil.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in new[] { "qe", "Application", "/q:" + query, "/f:text", "/c:500", "/rd:true" })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return result;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);

            // 每条事件以 “Event[n]:” 开头
            foreach (var block in output.Split(new[] { "Event[" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (block.IndexOf("Klang", StringComparison.OrdinalIgnoreCase) < 0) continue;
                result.Add("Event[" + block);
                if (result.Count >= 5) break;
            }
        }
        catch (Exception ex)
        {
            result.Add("读取事件日志失败：" + ex.Message);
        }
        return result;
    }
}
