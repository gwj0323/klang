using System;
using ComputeSharp;

namespace Klang_WinUI.Controls;

/// <summary>
/// 程序生成的水珠贴图（代替外部图片）：
/// - 形状：高斯衰减的软边圆斑，上半部分收得更早（附着在玻璃上的水珠上缘较平、下半部分饱满）；
///   着色器把多颗水珠叠加后再截成硬边，相邻的水珠自然连在一起。
/// - 折射：G 通道从左到右、R 通道从上到下由 1 变到 0（0.5 = 不偏），沿一条略带缓动的曲线，
///   让水珠像一个小凸透镜，把背后的景色左右、上下翻转并放大。
/// </summary>
internal static class RainDropTextures
{
    public const int Size = 128;

    /// <summary>|u| 从 0 到 1 时偏移从 0 到约 1，中间略快、边缘略缓。</summary>
    private static float Ease(float u)
    {
        float a = MathF.Min(MathF.Abs(u), 1f);
        return MathF.Sign(u) * a * (1.30f - 0.32f * a);
    }

    public static (Rgba32[] Alpha, Rgba32[] Color) Create()
    {
        var alpha = new Rgba32[Size * Size];
        var color = new Rgba32[Size * Size];
        const float sigma = 0.148f;            // 以贴图宽度为单位
        for (int y = 0; y < Size; y++)
        {
            float v = (y + 0.5f) / Size - 0.5f;   // -0.5（上）~ 0.5（下）
            for (int x = 0; x < Size; x++)
            {
                float u = (x + 0.5f) / Size - 0.5f;
                float a = MathF.Exp(-(u * u + v * v) / (2f * sigma * sigma));
                if (v < 0f) a *= SmoothStep(-0.30f, -0.20f, v);      // 上缘收平
                alpha[y * Size + x] = new Rgba32(0, 0, 0, ToByte(a));

                float g = 0.5f - 0.5f * Ease(u / 0.5f);
                float r = v >= 0f
                    ? 0.5f - 0.5f * Ease(v / 0.5f)
                    : 0.5f + 0.11f * MathF.Sin(MathF.Min(-v / 0.5f, 1f) * MathF.PI);   // 上半部分只有一点点反向偏移
                color[y * Size + x] = new Rgba32(ToByte(r), ToByte(g), 0, 255);
            }
        }
        return (alpha, color);
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
}
