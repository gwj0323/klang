using ComputeSharp;

namespace Klang_WinUI.Controls;

/// <summary>
/// 沉浸背景的 GPU 像素着色器（ComputeSharp 把这段 C# 编译成 HLSL）。
/// 与 Qt 版 fluid_shader.py 的 GLSL 逐行对应：五层封面色块图旋转叠加 + 两级流体噪声扰动 + 提饱和 / 压亮度 / 暗角 / 抖动。
/// 为了能同时做新旧封面交叉淡入，每层同时采样旧纹理和新纹理，最后按 mix 混合。
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct FluidShader(
    float time,
    float energy,
    float mix,                                   // 0 = 旧封面，1 = 新封面
    float hasCover,                              // 0 = 无封面，用调色盘
    ReadOnlyTexture2D<Rgba32, float4> texPrev,
    ReadOnlyTexture2D<Rgba32, float4> texCur,
    float3 color1,
    float3 color2,
    float3 color3,
    float3 color4,
    float3 colorBase) : IComputeShader<float4>
{

    private float2 Hash(float2 p)
    {
        p = new float2(Hlsl.Dot(p, new float2(127.1f, 311.7f)), Hlsl.Dot(p, new float2(269.5f, 183.3f)));
        return -1.0f + 2.0f * Hlsl.Frac(Hlsl.Sin(p) * 43758.5453123f);
    }

    private float Noise(float2 p)
    {
        float2 i = Hlsl.Floor(p);
        float2 f = Hlsl.Frac(p);
        float2 u = f * f * (3.0f - 2.0f * f);
        return Hlsl.Lerp(
            Hlsl.Lerp(Hlsl.Dot(Hash(i), f), Hlsl.Dot(Hash(i + new float2(1.0f, 0.0f)), f - new float2(1.0f, 0.0f)), u.X),
            Hlsl.Lerp(Hlsl.Dot(Hash(i + new float2(0.0f, 1.0f)), f - new float2(0.0f, 1.0f)), Hlsl.Dot(Hash(i + new float2(1.0f, 1.0f)), f - new float2(1.0f, 1.0f)), u.X),
            u.Y);
    }

    /// <summary>镜像重复寻址：[0,1] 原样，[1,2] 折回，周期 2。</summary>
    private float2 Mirror(float2 u)
    {
        float2 m = u - 2.0f * Hlsl.Floor(u * 0.5f);
        return 1.0f - Hlsl.Abs(1.0f - m);
    }

    /// <summary>以 center 为中心、scale 为尺寸、angle 为角度取一层封面的纹理坐标（GLSL: rot(a) * (p - c) / s + 0.5）。</summary>
    private float2 LayerUv(float2 p, float2 center, float scale, float angle)
    {
        float c = Hlsl.Cos(angle);
        float s = Hlsl.Sin(angle);
        float2 d = p - center;
        float2 q = new float2(c * d.X + s * d.Y, -s * d.X + c * d.Y) / scale + 0.5f;
        return Mirror(q);
    }

    private float Mask(float2 p, float2 center, float r0, float r1)
    {
        return 1.0f - Hlsl.SmoothStep(r0, r1, Hlsl.Length(p - center));
    }

    public float4 Execute()
    {
        float2 res = new float2(DispatchSize.X, DispatchSize.Y);
        float aspect = res.X / Hlsl.Max(1.0f, res.Y);
        float2 uv = new float2((ThreadIds.X + 0.5f) / res.X, 1.0f - (ThreadIds.Y + 0.5f) / res.Y);
        float2 p = (uv - 0.5f) * new float2(aspect, 1.0f);
        float t = time;

        // 两级流体扰动：大尺度慢涌 + 小尺度快涌，让颜色边界像液体一样翻卷
        float2 warp = new float2(
            Noise(p * 1.2f + new float2(t * 0.12f, t * 0.12f)),
            Noise(p * 1.2f + new float2(5.2f, 1.3f) - new float2(t * 0.10f, t * 0.10f)));
        warp += 0.5f * new float2(
            Noise(p * 2.6f - new float2(t * 0.20f, t * 0.20f) + new float2(3.7f, 3.7f)),
            Noise(p * 2.6f + new float2(t * 0.18f, t * 0.18f) + new float2(8.1f, 8.1f)));
        p += warp * (0.16f + energy * 0.10f);

        float3 col;
        if (hasCover > 0.5f)
        {
            // 五层的中心 / 尺度 / 转角
            float pulse = 1.0f + energy * 0.12f;
            float2 c1 = new float2(0.0f, 0.0f);
            float s1 = 2.0f + 0.25f * Hlsl.Sin(t * 0.11f);
            float a1 = t * 0.09f;

            float2 c2 = new float2(-0.55f, 0.15f) + new float2(0.40f * Hlsl.Sin(t * 0.23f), 0.28f * Hlsl.Cos(t * 0.19f));
            float2 c3 = new float2(0.60f, -0.10f) + new float2(0.38f * Hlsl.Cos(t * 0.21f), 0.30f * Hlsl.Sin(t * 0.27f));
            float2 c4 = new float2(0.05f, 0.30f) + new float2(0.45f * Hlsl.Sin(t * 0.17f + 1.3f), 0.22f * Hlsl.Cos(t * 0.31f));
            float2 c5 = new float2(-0.10f, -0.35f) + new float2(0.50f * Hlsl.Cos(t * 0.15f + 2.1f), 0.20f * Hlsl.Sin(t * 0.25f));

            float2 uv1 = LayerUv(p, c1, s1, a1);
            float2 uv2 = LayerUv(p, c2, 1.25f * pulse, -t * 0.22f + 1.1f);
            float2 uv3 = LayerUv(p, c3, 1.10f * pulse, t * 0.26f + 2.3f);
            float2 uv4 = LayerUv(p, c4, 0.95f * pulse, -t * 0.30f + 4.0f);
            float2 uv5 = LayerUv(p, c5, 1.05f * pulse, t * 0.19f + 5.2f);

            // 圆形软遮罩，层与层之间没有硬边
            float m2 = Mask(p, c2, 0.10f, 0.75f * pulse) * 0.90f;
            float m3 = Mask(p, c3, 0.10f, 0.70f * pulse) * 0.90f;
            float m4 = Mask(p, c4, 0.05f, 0.60f * pulse) * 0.85f;
            float m5 = Mask(p, c5, 0.05f, 0.65f * pulse) * 0.80f;

            // 旧封面叠层
            float3 a = texPrev.Sample(uv1).XYZ;
            a = Hlsl.Lerp(a, texPrev.Sample(uv2).XYZ, m2);
            a = Hlsl.Lerp(a, texPrev.Sample(uv3).XYZ, m3);
            a = Hlsl.Lerp(a, texPrev.Sample(uv4).XYZ, m4);
            a = Hlsl.Lerp(a, texPrev.Sample(uv5).XYZ, m5);

            // 新封面叠层
            float3 b = texCur.Sample(uv1).XYZ;
            b = Hlsl.Lerp(b, texCur.Sample(uv2).XYZ, m2);
            b = Hlsl.Lerp(b, texCur.Sample(uv3).XYZ, m3);
            b = Hlsl.Lerp(b, texCur.Sample(uv4).XYZ, m4);
            b = Hlsl.Lerp(b, texCur.Sample(uv5).XYZ, m5);

            col = Hlsl.Lerp(a, b, mix);
        }
        else
        {
            // 无封面：四个高斯权重光斑 + 底色（调色盘渐变）
            float2 p1 = new float2(-0.5f, 0.2f) + 0.2f * new float2(Hlsl.Sin(t * 0.3f), Hlsl.Cos(t * 0.25f));
            float2 p2 = new float2(0.5f, -0.2f) + 0.2f * new float2(Hlsl.Cos(t * 0.27f), Hlsl.Sin(t * 0.33f));
            float2 p3 = new float2(0.0f, 0.4f) + 0.2f * new float2(Hlsl.Sin(t * 0.21f), Hlsl.Cos(t * 0.31f));
            float2 p4 = new float2(0.1f, -0.4f) + 0.2f * new float2(Hlsl.Cos(t * 0.23f), Hlsl.Sin(t * 0.19f));
            float w1 = Hlsl.Exp(-2.2f * Hlsl.Dot(p - p1, p - p1));
            float w2 = Hlsl.Exp(-2.2f * Hlsl.Dot(p - p2, p - p2));
            float w3 = Hlsl.Exp(-2.6f * Hlsl.Dot(p - p3, p - p3));
            float w4 = Hlsl.Exp(-2.6f * Hlsl.Dot(p - p4, p - p4));
            float sw = w1 + w2 + w3 + w4 + 0.15f;
            col = (color1 * w1 + color2 * w2 + color3 * w3 + color4 * w4 + colorBase * 0.15f) / sw;
        }

        // 提饱和：模糊会把颜色洗灰，这里补回来
        float luma = Hlsl.Dot(col, new float3(0.299f, 0.587f, 0.114f));
        col = Hlsl.Lerp(new float3(luma, luma, luma), col, 1.7f);
        col = Hlsl.Max(col, new float3(0.0f, 0.0f, 0.0f));

        // 压亮度：整体降到约六成，亮部再额外压一点，保证白字在浅色封面上也清楚
        col = col * 0.62f;
        col = col / (1.0f + col * 0.45f) * 1.15f;

        // 随低频能量轻微提亮
        col *= 1.0f + energy * 0.08f;

        // 很弱的边缘收暗
        float vig = Hlsl.SmoothStep(1.35f, 0.35f, Hlsl.Length((uv - 0.5f) * new float2(1.0f, 1.2f)));
        col *= Hlsl.Lerp(0.78f, 1.0f, vig);

        // 抖动，避免暗部色带
        float dither = (Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(uv * res, new float2(12.9898f, 78.233f))) * 43758.5453f) - 0.5f) / 255.0f;
        col += new float3(dither, dither, dither);

        return new float4(Hlsl.Saturate(col), 1.0f);
    }
}
