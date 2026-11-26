using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Klang_WinUI.Services.Ai;

/// <summary>
/// AI 子进程里的推理环境：从数据目录加载下载好的 onnxruntime.dll / DirectML.dll，选显卡，创建会话。
/// 主进程从不加载这些原生库：模型或驱动出问题只影响子进程。
/// </summary>
internal static class AiRuntime
{
    private static IntPtr _ort;
    public static bool Loaded { get; private set; }
    public static int GpuDevice { get; private set; } = -1;     // DirectML 的适配器序号；-1 = 只用 CPU
    public static string AdapterName { get; private set; } = "";
    public static string LoadError { get; private set; } = "";

    /// <summary>按角色挑模型文件（有显卡时优先显卡版）。</summary>
    public static string Pick(AiPack pack, string role) => AiCatalog.Pick(pack, role, GpuDevice >= 0);

    /// <summary>加载原生库并决定用哪块显卡。preferCpu = 设置里选了“仅 CPU”。</summary>
    public static bool Init(bool preferCpu)
    {
        if (Loaded) return true;
        try
        {
            var dir = Path.Combine(AiCatalog.ModelsDir, "runtime");
            var ortPath = Path.Combine(dir, "onnxruntime.dll");
            var dmlPath = Path.Combine(dir, "DirectML.dll");
            if (!File.Exists(ortPath)) { LoadError = L.T("推理引擎没有安装"); return false; }

            // 先按完整路径载入我们的 DirectML.dll：系统目录里也有一个旧版的，onnxruntime 按名字找时会用已载入的这个
            if (File.Exists(dmlPath)) NativeLibrary.TryLoad(dmlPath, out _);
            _ort = NativeLibrary.Load(ortPath);
            NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, Resolve);
            _ = OrtEnv.Instance();   // 触发初始化，路径不对会在这里就报错
            Loaded = true;

            if (!preferCpu && File.Exists(dmlPath) && Environment.OSVersion.Version.Build >= 18362)
            {
                var best = Dxgi.BestAdapter();
                if (best != null) { GpuDevice = best.Value.Index; AdapterName = best.Value.Name; }
            }
            return true;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            return false;
        }
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path) =>
        name.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase) && !name.Contains("extensions", StringComparison.OrdinalIgnoreCase)
            ? _ort : IntPtr.Zero;

    public static string Version
    {
        get { try { return OrtEnv.Instance().GetVersionString(); } catch { return ""; } }
    }

    /// <summary>创建会话：能用显卡就用 DirectML，失败（驱动太旧、显存不够、算子不支持）时退回 CPU。</summary>
    public static (InferenceSession Session, bool OnGpu) Create(string modelPath, bool allowGpu = true)
    {
        string name = Path.GetFileName(modelPath);
        if (allowGpu && GpuDevice >= 0 && AiCatalog.GpuAllowed(name))
        {
            // 先告诉主进程正在把哪个模型放上显卡：DirectML 出问题时会直接让进程崩溃（catch 不到），
            // 主进程看到这行之后进程就没了，会把它记进黑名单，以后这个模型只用 CPU
            AiWorkerHost.Log("[gpu-load] " + name);
            try
            {
                var so = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    EnableMemoryPattern = false,
                    EnableCpuMemArena = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                };
                so.AppendExecutionProvider_DML(GpuDevice);
                var session = new InferenceSession(modelPath, so);
                AiWorkerHost.Log("[gpu-ok] " + name);
                return (session, true);
            }
            catch (Exception ex)
            {
                AiWorkerHost.Log("[gpu-ok] " + name);   // 抛异常不算崩溃，已经安全退回 CPU
                AiWorkerHost.Log($"DirectML 不可用，改用 CPU：{name}：{ex.Message}");
            }
        }
        var cpu = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            // 不用内存池：池子会一直留着推理时的峰值内存，K 歌 / 识别做完后进程还占好几百 MB
            EnableCpuMemArena = false,
            // 留一半核心给播放和界面
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };
        return (new InferenceSession(modelPath, cpu), false);
    }

    public static NamedOnnxValue Input(string name, float[] data, params int[] dims) =>
        NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, dims));

    public static NamedOnnxValue Input(string name, long[] data, params int[] dims) =>
        NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data, dims));

    public static NamedOnnxValue Input(string name, bool[] data, params int[] dims) =>
        NamedOnnxValue.CreateFromTensor(name, new DenseTensor<bool>(data, dims));

    /// <summary>取出一个输出的数据和形状（按名字，没有就取第一个）。</summary>
    public static (float[] Data, int[] Dims) Output(IReadOnlyCollection<DisposableNamedOnnxValue> results, string? name = null)
    {
        var r = (name != null ? results.FirstOrDefault(x => x.Name == name) : null) ?? results.First();
        var t = r.AsTensor<float>();
        var dims = t.Dimensions.ToArray();
        if (t is DenseTensor<float> dt) return (dt.Buffer.ToArray(), dims);
        return (t.ToArray(), dims);
    }

    /// <summary>用 DXGI 列出显卡，挑独立显存最大的那块（排除微软基本显示适配器这类软件适配器）。</summary>
    private static class Dxgi
    {
        [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow; public int LuidHigh;
            public uint Flags;
        }

        private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
        private delegate int GetDesc1Fn(IntPtr self, out AdapterDesc1 desc);

        public static (int Index, string Name)? BestAdapter()
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out var factory) < 0 || factory == IntPtr.Zero) return null;
            try
            {
                var vtbl = Marshal.ReadIntPtr(factory);
                var enumFn = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Fn>(Marshal.ReadIntPtr(vtbl, 12 * IntPtr.Size));
                (int, string)? best = null;
                ulong bestMem = 0;
                for (uint i = 0; i < 16; i++)
                {
                    if (enumFn(factory, i, out var adapter) < 0 || adapter == IntPtr.Zero) break;
                    try
                    {
                        var avt = Marshal.ReadIntPtr(adapter);
                        var descFn = Marshal.GetDelegateForFunctionPointer<GetDesc1Fn>(Marshal.ReadIntPtr(avt, 10 * IntPtr.Size));
                        if (descFn(adapter, out var d) < 0) continue;
                        if ((d.Flags & 2) != 0 || d.VendorId == 0x1414) continue;   // DXGI_ADAPTER_FLAG_SOFTWARE / 微软软件渲染
                        ulong mem = d.DedicatedVideoMemory;
                        if (best == null || mem > bestMem) { best = ((int)i, d.Description.Trim()); bestMem = mem; }
                    }
                    finally { Marshal.Release(adapter); }
                }
                return best;
            }
            finally { Marshal.Release(factory); }
        }
    }
}
