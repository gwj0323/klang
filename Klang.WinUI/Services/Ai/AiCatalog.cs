using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Klang_WinUI.Services.Ai;

/// <summary>
/// 一个要下载的文件。Url 指向 NuGet 包（.nupkg 本质是 zip）时，ZipEntry 是包里要取出的那个文件，
/// 只按需读取 zip 里的这一段，不下载整个包。
/// Role 是它在模型里的角色（同一角色可以有显卡版和 CPU 版）；Device：gpu = 显卡版（fp32 / fp16），
/// cpu = CPU 版（int8 量化，体积小、CPU 上快，但 DirectML 跑量化模型不稳定），any = 都用这一个。
/// </summary>
public sealed record AiFile(string Role, string Url, string RelPath, long Size, string Sha256 = "", string? ZipEntry = null, string Device = "any")
{
    public string FileName => Path.GetFileName(RelPath);
}

/// <summary>一个可选的 AI 组件：一组文件，装好后对应一项功能。</summary>
public sealed record AiPack(string Id, string NameZh, string DescriptionZh, AiFile[] Files, string License)
{
    public string Name => L.T(NameZh);
    public string Description => L.T(DescriptionZh);
}

/// <summary>
/// AI 组件清单。全部是开源模型，从 Hugging Face / GitHub / NuGet 的官方地址下载，存到数据目录的 ai 文件夹，不随安装包分发。
/// </summary>
public static class AiCatalog
{
    public const string OrtVersion = "1.24.4";
    private const string DmlVersion = "1.15.4";

    public static string Root => Path.Combine(StateService.ResolveDataDirectory(), "ai");
    public static string ModelsDir => Path.Combine(Root, "models");
    public static string IndexDir => Path.Combine(Root, "index");
    public static string KaraokeDir => Path.Combine(Root, "karaoke");

    /// <summary>当前进程架构在 NuGet 包里的目录名。</summary>
    private static (string Rid, string Dml) Arch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => ("win-arm64", "arm64-win"),
        Architecture.X86 => ("win-x86", "x86-win"),
        _ => ("win-x64", "x64-win"),
    };

    private static string Hf(string repo, string path) => $"https://huggingface.co/{repo}/resolve/main/{path}";

    public static readonly AiPack Runtime = new(
        "runtime",
        "推理引擎",
        "所有 AI 功能都要用，会自动安装。",
        new[]
        {
            new AiFile("ort", $"https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.directml/{OrtVersion}/microsoft.ml.onnxruntime.directml.{OrtVersion}.nupkg",
                "runtime/onnxruntime.dll", 17_400_000, ZipEntry: $"runtimes/{Arch.Rid}/native/onnxruntime.dll"),
            new AiFile("dml", $"https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/{DmlVersion}/microsoft.ai.directml.{DmlVersion}.nupkg",
                "runtime/DirectML.dll", 18_500_000, ZipEntry: $"bin/{Arch.Dml}/DirectML.dll"),
        },
        "MIT");

    private const string ClapRepo = "Xenova/larger_clap_music_and_speech";

    public static readonly AiPack Clap = new(
        "clap",
        "听感分析",
        "AI 找歌、自动标签、高潮连播，声学地图更准。",
        new[]
        {
            // LAION 在音乐和语音上专门训练的版本（比通用版更懂流派、配器、人声）
            new AiFile("audio", Hf(ClapRepo, "onnx/audio_model.onnx"), "clap-music/audio_model.onnx", 281749092, "3ecc72d27740e2a09ced20cf22fd6244122e5e506008763a0f368b3b4ff6eac8", Device: "gpu"),
            new AiFile("audio", Hf(ClapRepo, "onnx/audio_model_quantized.onnx"), "clap-music/audio_model_quantized.onnx", 78155433, "021dd4fb962b4ed20cc3a6730b09e0ccf9f9c49931047032118a3b64828513a6", Device: "cpu"),
            new AiFile("text", Hf(ClapRepo, "onnx/text_model_quantized.onnx"), "clap-music/text_model_quantized.onnx", 126603262, "8f9f29c5f6adee917553d4b3a70729c731c0d18b88efca0ae67c1a1fc278f3b6"),
            new AiFile("vocab", Hf(ClapRepo, "vocab.json"), "clap-music/vocab.json", 798293),
            new AiFile("merges", Hf(ClapRepo, "merges.txt"), "clap-music/merges.txt", 456318),
        },
        "Apache-2.0（LAION CLAP music & speech）");

    public static readonly AiPack TextEmbed = new(
        "e5",
        "歌词搜索",
        "按意思搜歌词，不用记原词。",
        new[]
        {
            new AiFile("model", Hf("Xenova/multilingual-e5-small", "onnx/model_quantized.onnx"), "e5/model_quantized.onnx", 118308185, "f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193"),
            new AiFile("tokenizer", Hf("Xenova/multilingual-e5-small", "tokenizer.json"), "e5/tokenizer.json", 17082730, "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39"),
        },
        "MIT（intfloat/multilingual-e5-small）");

    public static readonly AiPack Separator = new(
        "separator",
        "人声分离",
        "K 歌模式、导出伴奏。",
        new[]
        {
            new AiFile("model", "https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/UVR-MDX-NET-Inst_HQ_3.onnx",
                "separator/UVR-MDX-NET-Inst_HQ_3.onnx", 66759214),
        },
        "MIT（Ultimate Vocal Remover）");

    private const string WhisperRepo = "onnx-community/whisper-small";

    public static readonly AiPack Whisper = new(
        "whisper",
        "歌词识别",
        "从声音识别歌词，或自动对齐歌词时间。",
        new[]
        {
            new AiFile("encoder", Hf(WhisperRepo, "onnx/encoder_model_fp16.onnx"), "whisper/encoder_model_fp16.onnx", 176607756, "5549cd8666ff4b694ceb128bfa48b95bdcceec29075cf2c2212f90002cc058de", Device: "gpu"),
            new AiFile("encoder", Hf(WhisperRepo, "onnx/encoder_model_quantized.onnx"), "whisper/encoder_model_quantized.onnx", 92326160, "a43a83f3c5361cd591cfa7c36f14b43cf7cb22f47a415cc14a8d557be800fa92", Device: "cpu"),
            // 解码器每一步计算量很小：实测放显卡（fp16）反而比 CPU（int8）慢 3 倍（DirectML 每次调用的开销大），所以只用 CPU 版；
            // 用带 KV 缓存的 merged 版，每步只算新的一个 token
            new AiFile("decoder", Hf(WhisperRepo, "onnx/decoder_model_merged_quantized.onnx"), "whisper/decoder_model_merged_quantized.onnx", 156750845, "ec07c3cbb64172c39791e26ee870a65ac22b458c36722bfe2776b3dbf741e0c9"),
            new AiFile("vocab", Hf(WhisperRepo, "vocab.json"), "whisper/vocab.json", 1036584),
            new AiFile("merges", Hf(WhisperRepo, "merges.txt"), "whisper/merges.txt", 493869),
        },
        "MIT（OpenAI Whisper）");

    public static IReadOnlyList<AiPack> All { get; } = new[] { Runtime, Clap, TextEmbed, Separator, Whisper };

    public static AiPack? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public static string PathOf(AiFile f) => Path.Combine(ModelsDir, f.RelPath.Replace('/', Path.DirectorySeparatorChar));

    // ---------------- 显卡版 / CPU 版的选择 ----------------
    // 主进程按设置和检测结果决定下载哪个版本；子进程按实际情况（有没有显卡、哪个模型在显卡上出过事）挑已有的文件。

    /// <summary>在显卡上建会话时让进程崩溃过的模型文件名（之后只在 CPU 上跑）。主进程从设置读，子进程从启动参数读。</summary>
    public static HashSet<string> GpuBlacklist { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>量化模型不交给 DirectML（实测会在建会话时崩溃），只在 CPU 上跑。</summary>
    public static bool GpuAllowed(string fileName) => !fileName.Contains("quantized", StringComparison.OrdinalIgnoreCase) && !GpuBlacklist.Contains(fileName);

    /// <summary>
    /// 这个组件在当前设备上需要哪些文件：有显卡（且没被列入黑名单）就要显卡版，否则要 CPU 版。
    /// </summary>
    public static IEnumerable<AiFile> Required(AiPack pack, bool gpu)
    {
        foreach (var g in pack.Files.GroupBy(f => f.Role))
        {
            var gpuFile = g.FirstOrDefault(f => f.Device == "gpu");
            var cpuFile = g.FirstOrDefault(f => f.Device == "cpu");
            var any = g.FirstOrDefault(f => f.Device == "any");
            if (any != null) { yield return any; continue; }
            if (gpu && gpuFile != null && GpuAllowed(gpuFile.FileName)) yield return gpuFile;
            else if (cpuFile != null) yield return cpuFile;
            else if (gpuFile != null) yield return gpuFile;
        }
    }

    /// <summary>运行时挑文件：优先用当前设备对应的版本，没有就用另一个已下载的（fp32 在 CPU 上也能跑）。</summary>
    public static string Pick(AiPack pack, string role, bool gpu)
    {
        var files = pack.Files.Where(f => f.Role == role).ToList();
        var preferred = Required(pack, gpu).FirstOrDefault(f => f.Role == role);
        if (preferred != null && File.Exists(PathOf(preferred))) return PathOf(preferred);
        var existing = files.FirstOrDefault(f => File.Exists(PathOf(f)));
        return PathOf(existing ?? preferred ?? files[0]);
    }

    public static long RequiredSize(AiPack pack, bool gpu) => Required(pack, gpu).Sum(f => f.Size);

    /// <summary>组件在当前设备上能用（下载完成后才会改成正式文件名，所以存在就是完整的）。</summary>
    public static bool IsInstalled(AiPack pack) => IsInstalled(pack, AiService.GpuClass);

    /// <summary>
    /// 程序自带的推理引擎（Windows App SDK 里的 Windows ML：onnxruntime.dll + DirectML.dll）所在目录；没有时返回 null。
    /// 有它就不用再下载推理引擎，NPU 也只能通过它用。
    /// </summary>
    public static string? BundledRuntimeDir
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            return File.Exists(Path.Combine(dir, "onnxruntime.dll")) && File.Exists(Path.Combine(dir, "Microsoft.Windows.AI.MachineLearning.dll")) ? dir : null;
        }
    }

    public static bool IsInstalled(AiPack pack, bool gpu) =>
        pack == Runtime && BundledRuntimeDir != null
        || Required(pack, gpu).All(f => File.Exists(PathOf(f)))
        // 换到了另一种设备、对应版本还没下：已有的另一个版本能用也算装好（fp32 在 CPU 上能跑；fp16 显卡版在 CPU 上不保证）
        || pack.Files.GroupBy(f => f.Role).All(g => g.Any(f => File.Exists(PathOf(f)) && (f.Device != "gpu" || !f.FileName.Contains("fp16"))));

    public static bool IsInstalled(string id) => Find(id) is { } p && IsInstalled(p);

    public static bool AnyFile(AiPack pack) => pack.Files.Any(f => File.Exists(PathOf(f)));

    public static long InstalledBytes(AiPack pack) => pack.Files.Select(PathOf).Where(File.Exists).Sum(p => new FileInfo(p).Length);

    public static void Remove(AiPack pack)
    {
        foreach (var f in pack.Files)
        {
            var p = PathOf(f);
            try { if (File.Exists(p)) File.Delete(p); } catch { }
            try { if (File.Exists(p + ".part")) File.Delete(p + ".part"); } catch { }
        }
    }
}
