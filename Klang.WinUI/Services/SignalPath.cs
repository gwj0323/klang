using System.Collections.Generic;

namespace Klang_WinUI.Services;

/// <summary>当前播放的“信号路径”快照：从文件到声卡，每一步对声音做了什么。</summary>
public sealed record SignalPathInfo(
    bool Playing,
    string SourceFormat,           // 例：FLAC · 192 kHz · 24 bit · 立体声
    string DecodeStep,             // 例：Media Foundation → 24 位 PCM（原采样率）
    string RateStep,               // 变速
    string DspStep,                // 音效
    string VolumeStep,             // 音量 / 回放增益
    string OutputStep,             // 输出
    bool IsBitPerfect,
    IReadOnlyList<string> Reasons);// 不是比特完美的原因（比特完美时为空）

/// <summary>输出端的实际配置（独占 / 共享、设备、采样率、位深）。</summary>
public sealed class OutputPlan
{
    public required NAudio.Wave.IWavePlayer Player;
    public required string Mode;               // Exclusive | Shared
    public string DeviceName = "";
    public int Rate;
    public int Bits;
    public int Channels;
    public bool IsFloat;
    public string? FallbackReason;             // 想用独占但没成功时的原因
    public int MixRate, MixBits;               // 共享模式下声卡的混音格式（Windows 会把音频重采样成它）
    public bool MixIsFloat;
}
