using ComputeSharp;

namespace Klang_WinUI.Controls;

// 雾珠层的 GPU 操作（移植自 Codrops RainEffect 里画布上的 drawImage / destination-out，见 RainSimulation）。
// 雾珠层每个像素：R/G = 折射方向，B = 厚度，A = 水量（贴图由 RainDropTextures 生成）。

/// <summary>把一批小水珠贴图按“覆盖”方式盖到雾珠层上。线程 (x, y) = 贴图内像素，z = 第几颗。</summary>
[ThreadGroupSize(8, 8, 1)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct RainStampShader(
    ReadWriteTexture2D<Rgba32, float4> target,
    ReadOnlyBuffer<float4> rects,
    ReadOnlyTexture2D<Rgba32, float4> dropAlpha,
    ReadOnlyTexture2D<Rgba32, float4> dropColor) : IComputeShader
{
    public void Execute()
    {
        float4 r = rects[ThreadIds.Z];
        int2 px = new int2((int)Hlsl.Floor(r.X) + ThreadIds.X, (int)Hlsl.Floor(r.Y) + ThreadIds.Y);
        if (px.X < 0 || px.Y < 0 || px.X >= target.Width || px.Y >= target.Height) return;
        float2 uv = (new float2(px.X + 0.5f, px.Y + 0.5f) - r.XY) / r.ZW;
        if (uv.X < 0.0f || uv.Y < 0.0f || uv.X > 1.0f || uv.Y > 1.0f) return;

        float a = dropAlpha.Sample(uv).W;
        if (a <= 0.0f) return;
        float3 c = dropColor.Sample(uv).XYZ;
        float4 dst = target[px];
        float oa = a + dst.W * (1.0f - a);
        float3 rgb = (c * a + dst.XYZ * dst.W * (1.0f - a)) / Hlsl.Max(oa, 1e-5f);
        target[px] = new float4(rgb, oa);
    }
}

/// <summary>大水珠滑过的地方把雾珠擦掉（椭圆，边缘一像素过渡）。线程 (x, y) = 区域内像素，z = 第几处。</summary>
[ThreadGroupSize(8, 8, 1)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct RainEraseShader(
    ReadWriteTexture2D<Rgba32, float4> target,
    ReadOnlyBuffer<float4> rects) : IComputeShader
{
    public void Execute()
    {
        float4 r = rects[ThreadIds.Z];
        int2 px = new int2((int)Hlsl.Floor(r.X) + ThreadIds.X, (int)Hlsl.Floor(r.Y) + ThreadIds.Y);
        if (px.X < 0 || px.Y < 0 || px.X >= target.Width || px.Y >= target.Height) return;
        float2 c = r.XY + r.ZW * 0.5f;
        float2 v = (new float2(px.X + 0.5f, px.Y + 0.5f) - c) / (r.ZW * 0.5f);
        float l = Hlsl.Length(v);
        if (l >= 1.0f) return;
        float cover = Hlsl.Saturate((1.0f - l) * r.Z * 0.5f);   // 约一像素宽的抗锯齿边
        float4 dst = target[px];
        target[px] = new float4(dst.XYZ, dst.W * (1.0f - cover));
    }
}

/// <summary>停雨后让整个雾珠层逐帧变淡（原版 clearTexture）。</summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct RainFadeShader(ReadWriteTexture2D<Rgba32, float4> target, float keep) : IComputeShader
{
    public void Execute()
    {
        float4 v = target[ThreadIds.XY];
        target[ThreadIds.XY] = new float4(v.XYZ, v.W * keep);
    }
}
