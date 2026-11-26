using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Klang_WinUI.Services.Ai;

/// <summary>
/// AI 子进程（Klang.WinUI.exe --klang-ai-worker）。和主进程用标准输入 / 输出通讯，每行一个 JSON：
///   请求 {"id":1,"op":"clap_audio",...}；进度 {"id":1,"progress":0.4}；结果 {"id":1,"ok":true,"result":...}。
/// 请求并发处理（同一个模型内部串行），长任务可以用 {"op":"cancel","target":1} 取消。标准输入关闭时退出。
/// </summary>
public static class AiWorkerHost
{
    public const string WorkerArg = "--klang-ai-worker";

    private static readonly object OutLock = new();
    private static StreamWriter? _out;
    private static readonly ConcurrentDictionary<int, CancellationTokenSource> Jobs = new();
    // 听感 / 歌词模型在后台分析时连续用，留着；人声分离和歌词识别是偶尔用一次的大模型，用完马上释放
    private static readonly ModelSlot<ClapModel> Clap = new(() => new ClapModel(), keep: true);
    private static readonly ModelSlot<E5Model> E5 = new(() => new E5Model(), keep: true);
    private static readonly ModelSlot<MdxSeparator> Mdx = new(() => new MdxSeparator(), keep: false);
    private static readonly ModelSlot<WhisperModel> Whisper = new(() => new WhisperModel(), keep: false);

    /// <summary>按需创建模型；keep=false 时最后一个使用者用完就释放（同时有别的任务在用就等它用完）。</summary>
    private sealed class ModelSlot<T>(Func<T> create, bool keep) where T : class, IDisposable
    {
        private readonly object _lock = new();
        private T? _value;
        private int _users;

        public T Acquire()
        {
            lock (_lock)
            {
                _value ??= create();
                _users++;
                return _value;
            }
        }

        public void Release()
        {
            bool freed = false;
            lock (_lock)
            {
                if (--_users > 0 || keep || _value == null) return;
                _value.Dispose();
                _value = null;
                freed = true;
            }
            if (freed) TrimMemory();
        }

        /// <summary>没人在用时释放（unload 请求）。</summary>
        public void Unload()
        {
            lock (_lock)
            {
                if (_users > 0 || _value == null) return;
                _value.Dispose();
                _value = null;
            }
        }
    }

    /// <summary>大任务做完后把托管堆（整首歌的音频数组都在大对象堆上）压缩回收，再把空闲页还给系统。</summary>
    private static void TrimMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    internal static void Log(string msg)
    {
        try { Console.Error.WriteLine(msg); } catch { }
    }

    private static void Send(JsonObject o)
    {
        var line = o.ToJsonString();
        lock (OutLock) _out!.WriteLine(line);
    }

    public static int Run(string[] args)
    {
        bool preferCpu = args.Length > 1 && args[1] == "cpu";
        // 第三个参数：在显卡上出过事的模型文件名（分号分隔），这些只用 CPU
        if (args.Length > 2)
            foreach (var n in args[2].Split(';', StringSplitOptions.RemoveEmptyEntries)) AiCatalog.GpuBlacklist.Add(n);
        var t = new Thread(() =>
        {
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            bool ok = AiRuntime.Init(preferCpu);
            string? line;
            while ((line = input.ReadLine()) != null)
            {
                JsonNode? req;
                try { req = JsonNode.Parse(line); } catch { continue; }
                if (req == null) continue;
                int id = req["id"]?.GetValue<int>() ?? 0;
                string op = req["op"]?.GetValue<string>() ?? "";
                if (op == "cancel")
                {
                    if (Jobs.TryGetValue(req["target"]?.GetValue<int>() ?? -1, out var c)) c.Cancel();
                    continue;
                }
                if (!ok && op != "info" && op != "highlight")
                {
                    Send(new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = AiRuntime.LoadError });
                    continue;
                }
                var cts = new CancellationTokenSource();
                Jobs[id] = cts;
                Task.Run(() =>
                {
                    var resp = new JsonObject { ["id"] = id };
                    try
                    {
                        resp["result"] = Handle(op, req, id, cts.Token);
                        resp["ok"] = true;
                    }
                    catch (OperationCanceledException) { resp["ok"] = false; resp["error"] = "canceled"; }
                    catch (Exception ex) { resp["ok"] = false; resp["error"] = ex.Message; Log(op + ": " + ex); }
                    finally { Jobs.TryRemove(id, out _); }
                    Send(resp);
                });
            }
        });
        t.SetApartmentState(ApartmentState.MTA);   // Media Foundation 解码在 MTA 线程上
        t.IsBackground = false;
        t.Start();
        t.Join();
        return 0;
    }

    private static JsonArray Arr(float[] v) => new(v.Select(x => (JsonNode?)JsonValue.Create(MathF.Round(x, 5))).ToArray());

    private static JsonNode? Handle(string op, JsonNode req, int id, CancellationToken ct)
    {
        string S(string k) => req[k]?.GetValue<string>() ?? "";
        double D(string k) => req[k]?.GetValue<double>() ?? 0;
        void Progress(double p) => Send(new JsonObject { ["id"] = id, ["progress"] = Math.Round(p, 4) });

        switch (op)
        {
            case "info":
                return new JsonObject
                {
                    ["loaded"] = AiRuntime.Loaded,
                    ["error"] = AiRuntime.LoadError,
                    ["gpu"] = AiRuntime.GpuDevice >= 0,
                    ["adapter"] = AiRuntime.AdapterName,
                    ["ort"] = AiRuntime.Loaded ? AiRuntime.Version : "",
                };

            case "clap_audio":
            {
                var m = Clap.Acquire();
                try
                {
                    var e = m.EmbedAudio(S("path"), D("start"), D("end"), D("duration"), ct);
                    return e == null ? null : new JsonObject { ["emb"] = Arr(e), ["gpu"] = m.AudioOnGpu, ["model"] = m.AudioModelName };
                }
                finally { Clap.Release(); }
            }

            case "clap_text":
            {
                var texts = req["texts"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
                var m = Clap.Acquire();
                try { return new JsonArray(texts.Select(s => (JsonNode?)Arr(m.EmbedText(s))).ToArray()); }
                finally { Clap.Release(); }
            }

            case "e5":
            {
                var texts = req["texts"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
                var m = E5.Acquire();
                try { return new JsonArray(m.Embed(texts).Select(v => (JsonNode?)Arr(v)).ToArray()); }
                finally { E5.Release(); }
            }

            case "separate":
            {
                string? vocals = S("vocals");
                var m = Mdx.Acquire();
                try
                {
                    m.Separate(S("path"), D("start"), D("end"), S("inst"), string.IsNullOrEmpty(vocals) ? null : vocals, Progress, ct);
                    return new JsonObject { ["gpu"] = m.OnGpu };
                }
                finally { Mdx.Release(); }
            }

            case "transcribe":
            {
                // 有分离好的人声就用人声（没有伴奏干扰，准确得多）
                string vocals = S("vocals");
                bool onVocals = !string.IsNullOrEmpty(vocals) && File.Exists(vocals);
                float[] x = onVocals
                    ? AiDsp.DecodeMono(vocals, 16000, 0, 0)
                    : AiDsp.DecodeMono(S("path"), 16000, D("start"), D("end") > D("start") ? D("end") - D("start") : 0);
                string lang = S("language");
                var m = Whisper.Acquire();
                try
                {
                    var (detected, segs) = m.Run(x, string.IsNullOrEmpty(lang) ? null : lang, Progress, ct, refineOnsets: onVocals);
                    return new JsonObject
                    {
                        ["language"] = detected,
                        ["gpu"] = m.OnGpu,
                        ["segments"] = new JsonArray(segs.Select(s => (JsonNode?)new JsonObject
                        {
                            ["s"] = Math.Round(s.Start, 2), ["e"] = Math.Round(s.End, 2), ["t"] = s.Text
                        }).ToArray())
                    };
                }
                finally { Whisper.Release(); }
            }

            case "highlight":
            {
                // 不用模型：解码整首歌找重复最多、最响的一段
                double start = D("start"), end = D("end");
                var x = AiDsp.DecodeMono(S("path"), AiHighlight.SampleRate, start, end > start ? end - start : 0);
                var h = AiHighlight.Detect(x, D("length") > 0 ? D("length") : 30, ct);
                return h == null ? new JsonObject { ["found"] = false }
                    : new JsonObject { ["found"] = true, ["s"] = Math.Round(h.Value.Start, 2), ["e"] = Math.Round(h.Value.End, 2) };
            }

            case "unload":
                Clap.Unload(); E5.Unload(); Mdx.Unload(); Whisper.Unload();
                TrimMemory();
                return null;
        }
        throw new InvalidOperationException("unknown op " + op);
    }
}
