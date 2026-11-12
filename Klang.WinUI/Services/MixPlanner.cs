using System;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services;

/// <summary>一次混音的安排。时间都以“当前这首的原速时间轴”（秒）为准。</summary>
public sealed record MixPlan(
    double StartAt,        // 在当前这首的哪个拍点开始淡出（秒）
    double FadeSec,        // 淡入淡出持续多久（当前这首的时间轴上）
    double NextStart,      // 下一首从它的哪个拍点开始放（秒）
    double RateRatio,      // 下一首相对当前播放速度的变速比，让两首的速度一致
    int Beats);

/// <summary>
/// 决定“在哪个拍点开始混、混多久、下一首要不要变速”。
/// 做法：当前这首找最后一个“整段淡入淡出能在音乐结束前放完”的拍点开始；
/// 下一首从它自己开头的拍点放起；如果两首速度不同（相差不超过 8%，含 2 倍 / 半速关系），
/// 淡入淡出期间把下一首轻微变速，让两边的拍子重合。差得太多就不混，回到正常的切歌。
/// </summary>
public static class MixPlanner
{
    public const double MaxTempoChange = 0.08;

    public static MixPlan? Plan(TrackItem a, TrackItem b, double nowSec, double durationA)
    {
        if (a.Bpm <= 0 || b.Bpm <= 0) return null;

        // 下一首要播多快，才能和这首的速度对上（允许 1 倍、2 倍、半速的关系）
        double bestRatio = 0, bestDev = double.MaxValue;
        foreach (double k in new[] { 1.0, 2.0, 0.5 })
        {
            double r = a.Bpm * k / b.Bpm;
            double dev = Math.Abs(r - 1);
            if (dev <= MaxTempoChange && dev < bestDev) { bestDev = dev; bestRatio = r; }
        }
        if (bestRatio <= 0) return null;

        double beat = 60.0 / a.Bpm;
        int beats = 8;
        double fade = beats * beat;
        if (fade > 12) { beats = 4; fade = beats * beat; }
        else if (fade < 5) { beats = 16; fade = Math.Min(12, beats * beat); }

        double end = a.MixEnd > 0 ? Math.Min(a.MixEnd, durationA) : durationA;
        // 最后一个能放完整段淡入淡出的拍点
        double latest = end - 0.4 - fade;
        int k2 = (int)Math.Floor((latest - a.LastBeat) / beat);
        double start = a.LastBeat + k2 * beat;
        if (start < nowSec + 2.5) return null;        // 来不及准备了（或者这首太短）
        if (start + fade > durationA - 0.2) return null;

        return new MixPlan(start, fade, Math.Max(0, b.FirstBeat), bestRatio, beats);
    }

    /// <summary>两首歌“衔接得顺不顺”的先验分（给漫游模式选下一首时加一点权重）：速度接近、调性相容。</summary>
    public static double Affinity(TrackItem a, TrackItem b)
    {
        double s = 0;
        if (a.Bpm > 0 && b.Bpm > 0)
        {
            foreach (double k in new[] { 1.0, 2.0, 0.5 })
                if (Math.Abs(a.Bpm * k / b.Bpm - 1) <= MaxTempoChange) { s += 0.35; break; }
        }
        if (KeysCompatible(a.Camelot, b.Camelot)) s += 0.25;
        return s;
    }

    /// <summary>Camelot 轮：编号相同、或编号相差 1 且字母相同（或编号相同字母不同）算相容。</summary>
    public static bool KeysCompatible(string? x, string? y)
    {
        if (!TryParse(x, out int nx, out char lx) || !TryParse(y, out int ny, out char ly)) return false;
        if (nx == ny) return true;                               // 同号（含相对大小调）
        if (lx != ly) return false;
        int d = Math.Abs(nx - ny); d = Math.Min(d, 12 - d);
        return d == 1;
    }

    private static bool TryParse(string? s, out int n, out char l)
    {
        n = 0; l = ' ';
        if (string.IsNullOrEmpty(s) || s.Length < 2) return false;
        l = s[^1];
        return int.TryParse(s[..^1], out n) && (l == 'A' || l == 'B');
    }
}
