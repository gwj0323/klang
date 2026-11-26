using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Klang_WinUI.Services.Ai;

/// <summary>AI 子进程没装好、起不来或中途退出（不是某一首歌的问题）。</summary>
public sealed class AiProcessException : Exception
{
    public AiProcessException(string message, bool gpuCrash = false) : base(message) { GpuCrash = gpuCrash; }
    /// <summary>进程是在把某个模型放上显卡时崩溃的（已记入黑名单，重试会改用 CPU）。</summary>
    public bool GpuCrash { get; }
}

/// <summary>
/// 主进程里的 AI 入口：开关和设置、组件下载、和 AI 子进程通讯。
/// 推理全部在子进程里做（见 <see cref="AiWorkerHost"/>），主进程不加载任何模型和原生推理库；
/// 子进程空闲 3 分钟自动结束，显存和内存随之释放。
/// </summary>
public sealed class AiService
{
    private static readonly Lazy<AiService> _instance = new(() => new AiService());
    public static AiService Instance => _instance.Value;

    private static AppState State => StateService.Instance.State;

    /// <summary>按显卡来选模型版本：设置没选“仅 CPU”，且检测到过可用的显卡。</summary>
    public static bool GpuClass => State.AiDevice != "cpu" && State.AiGpuDetected;

    private AiService()
    {
        foreach (var n in State.AiGpuBlacklist) AiCatalog.GpuBlacklist.Add(n);
    }

    /// <summary>总开关（默认关）。关着时不启动子进程、不在后台分析、菜单里不显示 AI 功能。</summary>
    public bool Enabled => State.AiEnabled;

    /// <summary>某个功能能不能用：总开关开着、推理引擎和这个组件都装好了。</summary>
    public bool Ready(AiPack pack) => Enabled && AiCatalog.IsInstalled(AiCatalog.Runtime) && AiCatalog.IsInstalled(pack);

    /// <summary>开关、组件安装状态、设备改变时触发（可能在后台线程）。</summary>
    public event Action? Changed;
    public void RaiseChanged() => Changed?.Invoke();

    public void SetEnabled(bool on)
    {
        State.AiEnabled = on;
        StateService.Instance.RequestSave();
        if (!on) StopWorker();
        RaiseChanged();
        if (on) AiIndexService.Instance.Kick();
    }

    public void SetDevice(string device)
    {
        if (State.AiDevice == device) return;
        State.AiDevice = device;
        StateService.Instance.RequestSave();
        StopWorker();   // 下次请求时按新设置启动
        _info = null;
        RaiseChanged();
    }

    // ================= 组件下载 =================

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _downloads = new();
    /// <summary>正在下载的组件的进度（0~1）。</summary>
    public ConcurrentDictionary<string, double> DownloadProgress { get; } = new();
    public ConcurrentDictionary<string, string> DownloadErrors { get; } = new();

    public bool IsDownloading(string packId) => _downloads.ContainsKey(packId);

    public async Task DownloadAsync(AiPack pack)
    {
        var cts = new CancellationTokenSource();
        if (!_downloads.TryAdd(pack.Id, cts)) return;
        DownloadErrors.TryRemove(pack.Id, out _);
        DownloadProgress[pack.Id] = 0;
        RaiseChanged();
        try
        {
            // 用到推理引擎的组件：顺带把引擎也装上，再问一下有没有显卡，决定下载显卡版还是 CPU 版
            if (pack != AiCatalog.Runtime)
            {
                if (!AiCatalog.IsInstalled(AiCatalog.Runtime))
                    await AiDownloader.DownloadAsync(AiCatalog.Runtime.Files, null, cts.Token);
                try { await GetInfoAsync(); } catch { }
            }
            var files = AiCatalog.Required(pack, GpuClass).ToList();
            // 进度只更新数值，界面自己定时读（不触发 Changed，免得设置页每 0.2 秒重建一次）
            await AiDownloader.DownloadAsync(files, new Progress<(long Done, long Total)>(p =>
                DownloadProgress[pack.Id] = p.Total > 0 ? (double)p.Done / p.Total : 0), cts.Token);
            if (pack == AiCatalog.Runtime) { StopWorker(); _info = null; }
            else RestartWhenIdle();   // 新下载的模型文件（比如显卡版）要等子进程重启才会用上
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DownloadErrors[pack.Id] = ex.Message;
            App.LogError("AI download " + pack.Id, ex.Message);
        }
        finally
        {
            _downloads.TryRemove(pack.Id, out _);
            DownloadProgress.TryRemove(pack.Id, out _);
            RaiseChanged();
            AiIndexService.Instance.Kick();
        }
    }

    public void CancelDownload(string packId)
    {
        if (_downloads.TryGetValue(packId, out var c)) c.Cancel();
    }

    public void Remove(AiPack pack)
    {
        StopWorker();   // 模型文件可能正被子进程占用
        AiCatalog.Remove(pack);
        if (pack == AiCatalog.Clap) AiIndexService.Instance.ClearAudio();
        if (pack == AiCatalog.TextEmbed) AiIndexService.Instance.ClearLyrics();
        if (pack == AiCatalog.Runtime) _info = null;
        RaiseChanged();
    }

    // ================= 设备信息 =================

    public sealed record DeviceInfo(bool Loaded, bool Gpu, string Adapter, string Error);
    private DeviceInfo? _info;
    public DeviceInfo? LastInfo => _info;

    /// <summary>问子进程：推理引擎能不能加载、用的是哪块显卡。</summary>
    public async Task<DeviceInfo?> GetInfoAsync()
    {
        if (_info != null) return _info;
        if (!AiCatalog.IsInstalled(AiCatalog.Runtime)) return null;
        var r = await RequestAsync("info", new JsonObject());
        if (r is not JsonObject o) return null;
        _info = new DeviceInfo(
            o["loaded"]?.GetValue<bool>() == true,
            o["gpu"]?.GetValue<bool>() == true,
            o["adapter"]?.GetValue<string>() ?? "",
            o["error"]?.GetValue<string>() ?? "");
        if (_info.Loaded && State.AiGpuDetected != _info.Gpu && State.AiDevice != "cpu")
        {
            State.AiGpuDetected = _info.Gpu;
            StateService.Instance.RequestSave();
        }
        RaiseChanged();
        return _info;
    }

    // ================= 子进程通讯 =================

    private readonly object _procLock = new();
    private Process? _proc;
    private StreamWriter? _stdin;
    private int _nextId;
    private readonly ConcurrentDictionary<int, (TaskCompletionSource<JsonNode?> Tcs, Action<double>? Progress)> _pending = new();
    private Timer? _idle;
    private static readonly TimeSpan IdleExit = TimeSpan.FromMinutes(3);
    private readonly Queue<string> _stderrTail = new();

    /// <summary>发一个请求给子进程。出错时抛出异常（消息给人看）；取消时抛 OperationCanceledException。</summary>
    public async Task<JsonNode?> RequestAsync(string op, JsonObject args, Action<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            return await RequestOnceAsync(op, (JsonObject)args.DeepClone(), progress, ct);
        }
        catch (AiProcessException ex) when (ex.GpuCrash)
        {
            // 某个模型在显卡上让进程崩溃了：它已进黑名单，换 CPU 再试一次
            return await RequestOnceAsync(op, (JsonObject)args.DeepClone(), progress, ct);
        }
    }

    private async Task<JsonNode?> RequestOnceAsync(string op, JsonObject args, Action<double>? progress, CancellationToken ct)
    {
        if (!AiCatalog.IsInstalled(AiCatalog.Runtime)) throw new AiProcessException(L.T("请先在设置 → AI 功能里下载推理引擎"));
        int id;
        StreamWriter w;
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_procLock)
        {
            EnsureStarted();
            id = ++_nextId;
            w = _stdin!;
            _pending[id] = (tcs, progress);
            _idle?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        args["id"] = id;
        args["op"] = op;
        try
        {
            lock (_procLock) { w.WriteLine(args.ToJsonString()); w.Flush(); }
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            throw new AiProcessException(L.T("AI 进程没有响应") + "：" + ex.Message);
        }

        using var reg = ct.Register(() =>
        {
            try { lock (_procLock) { _stdin?.WriteLine(new JsonObject { ["op"] = "cancel", ["target"] = id }.ToJsonString()); _stdin?.Flush(); } } catch { }
        });
        try
        {
            var r = await tcs.Task.ConfigureAwait(false);
            return r;
        }
        catch (InvalidOperationException ex) when (ex.Message == "canceled")
        {
            throw new OperationCanceledException(ct);
        }
        finally
        {
            ArmIdle();
        }
    }

    private bool _restartRequested;

    /// <summary>手头的请求都做完后重启子进程（下一个请求时按新文件、新设置启动）。</summary>
    public void RestartWhenIdle()
    {
        lock (_procLock)
        {
            if (_pending.IsEmpty) StopWorkerLocked();
            else _restartRequested = true;
        }
    }

    private void ArmIdle()
    {
        lock (_procLock)
        {
            if (_pending.IsEmpty && _restartRequested) { _restartRequested = false; StopWorkerLocked(); return; }
            if (_pending.IsEmpty)
            {
                _idle ??= new Timer(_ => { lock (_procLock) { if (_pending.IsEmpty) StopWorkerLocked(); } });
                _idle.Change(IdleExit, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void EnsureStarted()
    {
        if (_proc is { HasExited: false }) return;
        StopWorkerLocked();
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no exe");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(AiWorkerHost.WorkerArg);
        psi.ArgumentList.Add(State.AiDevice == "cpu" ? "cpu" : "auto");
        psi.ArgumentList.Add(string.Join(";", AiCatalog.GpuBlacklist));
        var p = Process.Start(psi) ?? throw new AiProcessException(L.T("无法启动 AI 进程"));
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // 不和播放抢 CPU
        _proc = p;
        _stdin = p.StandardInput;
        var stdout = p.StandardOutput;
        var stderr = p.StandardError;
        var gpu = new GpuLoadTracker();
        var stderrDone = new ManualResetEventSlim();
        new Thread(() =>
        {
            try
            {
                string? l;
                while ((l = stderr.ReadLine()) != null)
                {
                    if (l.StartsWith("[gpu-load] ")) gpu.Loading = l[11..];
                    else if (l.StartsWith("[gpu-ok] ") && gpu.Loading == l[9..]) gpu.Loading = null;
                    else lock (_stderrTail) { _stderrTail.Enqueue(l); while (_stderrTail.Count > 20) _stderrTail.Dequeue(); }
                }
            }
            catch { }
            stderrDone.Set();
        }) { IsBackground = true, Name = "AI worker stderr" }.Start();
        new Thread(() => ReadLoop(p, stdout, gpu, stderrDone)) { IsBackground = true, Name = "AI worker reader" }.Start();
    }

    private sealed class GpuLoadTracker { public volatile string? Loading; }

    private void ReadLoop(Process p, StreamReader stdout, GpuLoadTracker gpu, ManualResetEventSlim stderrDone)
    {
        try
        {
            string? line;
            while ((line = stdout.ReadLine()) != null)
            {
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); } catch { continue; }
                if (msg == null) continue;
                int id = msg["id"]?.GetValue<int>() ?? 0;
                if (!_pending.TryGetValue(id, out var entry)) continue;
                if (msg["progress"] is JsonNode pr && msg["ok"] == null)
                {
                    try { entry.Progress?.Invoke(pr.GetValue<double>()); } catch { }
                    continue;
                }
                _pending.TryRemove(id, out _);
                if (msg["ok"]?.GetValue<bool>() == true) entry.Tcs.TrySetResult(msg["result"]?.DeepClone());
                else entry.Tcs.TrySetException(new InvalidOperationException(msg["error"]?.GetValue<string>() ?? "error"));
            }
        }
        catch { }
        // 子进程退出（正常结束或崩溃）：没完成的请求全部失败
        stderrDone.Wait(2000);   // 等标准错误读完，才知道崩溃时是不是正在把模型放上显卡
        string? crashedOn = gpu.Loading;
        if (crashedOn != null)
        {
            AiCatalog.GpuBlacklist.Add(crashedOn);
            if (!State.AiGpuBlacklist.Contains(crashedOn, StringComparer.OrdinalIgnoreCase)) State.AiGpuBlacklist.Add(crashedOn);
            StateService.Instance.RequestSave();
            App.LogError("AI worker", $"{crashedOn} 在显卡上加载时崩溃，之后改用 CPU");
        }
        string why;
        try { why = p.HasExited ? $"AI 进程已退出（代码 0x{p.ExitCode:X8}）" : "AI 进程已断开"; } catch { why = "AI 进程已断开"; }
        string tail;
        lock (_stderrTail) tail = string.Join(" | ", _stderrTail);
        if (!_pending.IsEmpty && tail.Length > 0) App.LogError("AI worker", why + "：" + tail);
        foreach (var kv in _pending.ToArray())
            if (_pending.TryRemove(kv.Key, out var e)) e.Tcs.TrySetException(new AiProcessException(L.T(why), crashedOn != null));
    }

    public void StopWorker() { lock (_procLock) StopWorkerLocked(); }

    private void StopWorkerLocked()
    {
        try { _stdin?.Dispose(); } catch { }
        try { if (_proc is { HasExited: false } && !_proc.WaitForExit(1500)) _proc.Kill(); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null; _stdin = null;
    }

    // ================= 便捷封装 =================

    public static float[] ToFloats(JsonNode? n) => n is JsonArray a ? a.Select(x => (float)x!.GetValue<double>()).ToArray() : Array.Empty<float>();

    public async Task<float[][]> ClapTextAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var r = await RequestAsync("clap_text", new JsonObject { ["texts"] = new JsonArray(texts.Select(t => (JsonNode?)t).ToArray()) }, null, ct);
        return r is JsonArray a ? a.Select(ToFloats).ToArray() : Array.Empty<float[]>();
    }

    public async Task<float[][]> E5Async(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var r = await RequestAsync("e5", new JsonObject { ["texts"] = new JsonArray(texts.Select(t => (JsonNode?)t).ToArray()) }, null, ct);
        return r is JsonArray a ? a.Select(ToFloats).ToArray() : Array.Empty<float[]>();
    }
}
