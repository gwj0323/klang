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
    float3 colorBase,
    float clock,                                 // 真实时间（秒）：雨雪按它运动，不受时段的流动速度影响
    float4 tone,                                 // x = 色温（正暖负冷），y = 亮度，z = 饱和度
    float4 weather,                              // x = 雨，y = 雪，z = 阴天
    // ---- 雨水（Codrops RainEffect 的水面图，见 RainSimulation）----
    ReadWriteTexture2D<Rgba32, float4> rainDrizzle,   // 雾珠层（模拟像素，屏幕高 1080）
    ReadOnlyBuffer<float4> rainRects,                // 大水珠贴图位置：左上 x、y，宽、高（模拟像素）
    ReadOnlyBuffer<float> rainDepth,                 // 大水珠厚度
    ReadOnlyBuffer<int> rainTiles,                   // 按 108 像素分块：每块 [数量, 索引...]（按绘制顺序）
    int rainTilesX,
    ReadOnlyTexture2D<Rgba32, float4> dropAlpha,     // 水珠形状（RainDropTextures 生成）
    ReadOnlyTexture2D<Rgba32, float4> dropColor) : IComputeShader<float4>   // 水珠折射方向
{
    /// <summary>水面图在模拟像素 sp 处的值：雾珠层打底，再按绘制顺序叠上本块里的大水珠。</summary>
    private float4 WaterAt(float2 sp)
    {
        int2 mp = new int2(Hlsl.Clamp((int)sp.X, 0, rainDrizzle.Width - 1), Hlsl.Clamp((int)sp.Y, 0, rainDrizzle.Height - 1));
        float4 water = rainDrizzle[mp];
        int tx = Hlsl.Clamp((int)(sp.X / RainTileSize), 0, rainTilesX - 1);
        int ty = Hlsl.Clamp((int)(sp.Y / RainTileSize), 0, RainTilesY - 1);
        int b = (ty * rainTilesX + tx) * RainTileStride;
        int cnt = rainTiles[b];
        for (int i = 0; i < cnt; i++)
        {
            int idx = rainTiles[b + 1 + i];
            float4 rc = rainRects[idx];
            float2 du = (sp - rc.XY) / rc.ZW;
            if (du.X < 0.0f || du.Y < 0.0f || du.X > 1.0f || du.Y > 1.0f) continue;
            float sa = dropAlpha.Sample(du).W;
            if (sa <= 0.0f) continue;
            float3 sc = dropColor.Sample(du).XYZ;
            sc.Z = 1.0f - (1.0f - sc.Z) * (1.0f - rainDepth[idx]);       // 原版用“滤色”把厚度叠进蓝色通道
            float oa = sa + water.W * (1.0f - sa);
            float3 rgb = (sc * sa + water.XYZ * water.W * (1.0f - sa)) / Hlsl.Max(oa, 1e-5f);
            water = new float4(rgb, oa);
        }
        return water;
    }

    private const float RainSimH = 1080.0f;
    private const int RainTileSize = 108;
    private const int RainTilesY = 10;
    private const int RainTileStride = 64;
    private float Hash1(float n) => Hlsl.Frac(Hlsl.Sin(n * 12.9898f) * 43758.5453f);

    private float Hash21(float2 p) => Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(p, new float2(12.9898f, 78.233f))) * 43758.5453f);

    /// <summary>一层飘落的雪：稀疏的柔边圆点，随时间下落并左右轻摆。</summary>
    private float Snow(float2 q, float t, float scale, float speed, float seed, float density)
    {
        float2 st = q * scale + new float2(Hlsl.Sin(t * 0.21f + seed) * 0.5f, t * speed * scale);
        float2 id = Hlsl.Floor(st);
        float h = Hash21(id + seed);
        if (h > density) return 0.0f;
        float2 f = Hlsl.Frac(st) - 0.5f;
        float2 pos = (new float2(Hash21(id + seed + 3.1f), Hash21(id + seed + 5.7f)) - 0.5f) * 0.6f;
        pos.X += Hlsl.Sin(t * 0.7f + h * 40.0f) * 0.12f;
        float r = 0.05f + 0.06f * Hash21(id + seed + 9.2f);
        return Hlsl.SmoothStep(r, r * 0.15f, Hlsl.Length(f - pos));
    }

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

    /// <summary>流体背景在 p 处的颜色（封面五层叠加 + 噪声扰动 + 提饱和 / 压亮度）。下雨时透过水珠要在另一个位置再取一次。</summary>
    private float3 Scene(float2 p, float t)
    {
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

        return col;
    }

    public float4 Execute()
    {
        float2 res = new float2(DispatchSize.X, DispatchSize.Y);
        float aspect = res.X / Hlsl.Max(1.0f, res.Y);
        float2 uv = new float2((ThreadIds.X + 0.5f) / res.X, 1.0f - (ThreadIds.Y + 0.5f) / res.Y);
        float2 p = (uv - 0.5f) * new float2(aspect, 1.0f);
        float t = time;

        float2 q = new float2(uv.X * aspect, uv.Y);

        // ---- 雨：水面图（雾珠层 + 按顺序叠上去的大水珠），每个像素：R/G = 折射方向，B = 厚度，A = 水量 ----
        float4 water = new float4(0.0f, 0.0f, 0.0f, 0.0f);
        float2 sp = new float2(uv.X * aspect, 1.0f - uv.Y) * RainSimH;   // 模拟像素，y 向下
        if (weather.X > 0.001f) water = WaterAt(sp);
        float3 col = Scene(p, t);

        // 原版水面着色：水量 ×10 −3 截成硬边；折射 = (G, R) 偏离 0.5 的方向 × (128 + 厚度 × 384) 像素，取到的景色亮 4%
        float rainA = 0.0f;
        if (water.W > 0.0f)
        {
            rainA = Hlsl.Saturate(water.W * 10.0f - 3.0f);
            if (rainA > 0.0f)
            {
                float2 refr = (new float2(water.Y, water.X) - 0.5f) * 2.0f;
                float2 offPx = refr * (128.0f + water.Z * 384.0f);
                float2 pr = p + new float2(offPx.X, -offPx.Y) / RainSimH;    // 着色器 p 的 y 向上
                float3 fg = Scene(pr, t) * 1.04f;   // 透明的水：只是折射，看到的景色稍亮一点
                // 水珠边缘折射角很大，光线偏折得厉害，轮廓看起来略暗（真实水珠的样子，不是加了颜色）
                float rim = Hlsl.SmoothStep(0.40f, 0.90f, Hlsl.Length(refr));
                fg *= 1.0f - rim * 0.45f;
                col = Hlsl.Lerp(col, fg, rainA);
            }
        }

        // 氛围：饱和度（阴天再降低）、色温、亮度
        float sat = tone.Z * (1.0f - weather.Z * 0.55f);
        float l2 = Hlsl.Dot(col, new float3(0.299f, 0.587f, 0.114f));
        col = Hlsl.Max(Hlsl.Lerp(new float3(l2, l2, l2), col, sat), new float3(0.0f, 0.0f, 0.0f));
        col *= new float3(1.0f + 0.20f * tone.X, 1.0f + 0.04f * tone.X, 1.0f - 0.22f * tone.X);
        col *= tone.Y * (1.0f - weather.Z * 0.08f);
        col = Hlsl.Lerp(col, new float3(l2 * 0.95f, l2 * 0.98f, l2 * 1.05f), weather.Z * 0.15f);

        // 雨天的玻璃：没有水的地方蒙着一层淡淡的雾（越往上越浓）；水珠和擦过的水路里是清楚的景色
        if (weather.X > 0.001f)
        {
            float fog = weather.X * (0.28f + 0.30f * Hlsl.SmoothStep(0.15f, 1.0f, uv.Y));
            float lf = Hlsl.Dot(col, new float3(0.299f, 0.587f, 0.114f));
            float3 fogCol = new float3(lf, lf, lf) * 1.12f + new float3(0.045f, 0.055f, 0.075f);
            col = Hlsl.Lerp(col, fogCol, fog * (1.0f - rainA));
        }
        // 雪：三层不同大小和速度，近大远小
        if (weather.Y > 0.001f)
        {
            float flake = Snow(q, clock, 9.0f, 0.040f, 1.0f, 0.13f) * 0.55f;
            flake = Hlsl.Max(flake, Snow(q, clock, 14.0f, 0.028f, 7.0f, 0.15f) * 0.35f);
            flake = Hlsl.Max(flake, Snow(q, clock, 5.0f, 0.060f, 13.0f, 0.07f) * 0.70f);
            col = Hlsl.Lerp(col, new float3(0.90f, 0.93f, 0.97f), flake * weather.Y * 0.5f);
        }

        // 很弱的边缘收暗
        float vig = Hlsl.SmoothStep(1.35f, 0.35f, Hlsl.Length((uv - 0.5f) * new float2(1.0f, 1.2f)));
        col *= Hlsl.Lerp(0.78f, 1.0f, vig);

        // 抖动，避免暗部色带
        float dither = (Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(uv * res, new float2(12.9898f, 78.233f))) * 43758.5453f) - 0.5f) / 255.0f;
        col += new float3(dither, dither, dither);

        return new float4(Hlsl.Saturate(col), 1.0f);
    }
}
