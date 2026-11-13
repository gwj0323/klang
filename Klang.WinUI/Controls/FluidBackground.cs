using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using ComputeSharp;
using ComputeSharp.WinUI;

namespace Klang_WinUI.Controls;

/// <summary>
/// 沉浸模式背景：移植自 Qt 版 fluid_shader（封面取色 + 流动网格渐变）。
/// 1) 封面 k-means 取至多 6 个区分明显的主色，按占比分给 9 个种子点，做 Voronoi 分区后高斯模糊，得到 64×64 色块图；
/// 2) 每帧把色块图叠成五层（各自不同中心 / 尺度 / 转速），再加两级噪声扰动让颜色边界像液体一样翻卷；
/// 3) 提饱和、压亮度、暗角、抖动；换歌时新旧色块图 1.2 秒交叉淡入，随低频能量轻微脉动。
/// 原版在 GPU 着色器里逐像素计算；这里在低分辨率位图上用多线程计算，再由 XAML 平滑放大到全屏（内容本来就是模糊的）。
/// </summary>
public sealed class FluidBackground : UserControl
{
    private const int TexN = 64;
    private const int RenderH = 96;

    private readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private WriteableBitmap? _bitmap;
    private byte[] _buffer = Array.Empty<byte>();
    private int _w, _h;

    private float[]? _texCur, _texPrev;     // TexN*TexN*3，0..1
    private float _mix = 1f;
    private int _coverToken;

    // 无封面时使用的调色盘（与 Qt 版回退值一致）
    private static readonly float[][] DefaultPalette =
    {
        new[] { 0x3D / 255f, 0x7A / 255f, 0x63 / 255f },
        new[] { 0x7D / 255f, 0x6B / 255f, 0x5C / 255f },
        new[] { 0xBF / 255f, 0xA7 / 255f, 0x8A / 255f },
        new[] { 0x22 / 255f, 0x33 / 255f, 0x48 / 255f },
    };
    private static readonly float[] BaseColor = { 8 / 255f, 10 / 255f, 16 / 255f };

    private float _time, _energy, _targetEnergy;
    private readonly DispatcherQueueTimer _timer;
    private readonly Stopwatch _clock = new();
    private long _lastTicks;
    private bool _running;

    // ---- GPU 路径（ComputeSharp）：初始化失败时 _gpu 为 null，退回上面的 CPU 位图路径 ----
    private AnimatedComputeShaderPanel? _gpu;
    private GraphicsDevice? _device;
    private ReadOnlyTexture2D<Rgba32, float4>? _gCur, _gPrev, _gDummy;
    private readonly object _gLock = new();
    private TimeSpan _gLast;

    // ---- 氛围（时段 / 天气）：每帧向目标值平滑靠近，时段或天气变化时约 4 秒过渡 ----
    private float _aWarm, _aBright = 1f, _aSpeed = 1f, _aSat = 1f, _aRain, _aSnow, _aOver;
    private float _flowTime, _clockTime;
    private bool _ambInit;

    private void StepAmbience(float dt)
    {
        var a = Services.AmbienceService.Current;
        if (!_ambInit)
        {
            _ambInit = true;
            (_aWarm, _aBright, _aSpeed, _aSat, _aRain, _aSnow, _aOver) =
                (a.Warmth, a.Brightness, a.Speed, a.Saturation, a.Rain, a.Snow, a.Overcast);
        }
        else
        {
            float k = 1f - MathF.Exp(-dt / 1.4f);
            _aWarm += (a.Warmth - _aWarm) * k;
            _aBright += (a.Brightness - _aBright) * k;
            _aSpeed += (a.Speed - _aSpeed) * k;
            _aSat += (a.Saturation - _aSat) * k;
            _aRain += (a.Rain - _aRain) * k;
            _aSnow += (a.Snow - _aSnow) * k;
            _aOver += (a.Overcast - _aOver) * k;
        }
        _flowTime += dt * _aSpeed;   // 流动速度随时段变化：时间按倍数推进，画面不会跳
        _clockTime = (_clockTime + dt) % 1200f;   // 只用于雨雪的运动；回绕避免着色器里的浮点精度随运行时间下降
    }

    // ---- 雨水（只在 GPU 路径使用）：CPU 跑水珠物理，GPU 画雾珠层和水面 ----
    private readonly RainSimulation _rain = new();
    private ReadWriteTexture2D<Rgba32, float4>? _gDrizzle;
    private ReadOnlyBuffer<float4>? _gRects, _gStamps, _gErases;
    private ReadOnlyBuffer<float>? _gDepth;
    private ReadOnlyBuffer<int>? _gTiles;
    private ReadOnlyTexture2D<Rgba32, float4>? _gDropAlpha, _gDropColor;
    private volatile float _aspect = 16f / 9f;   // 界面线程写，渲染线程读

    private bool _rainActive;

    private void InitRainResources()
    {
        _gRects = _device!.AllocateReadOnlyBuffer<float4>(RainSimulation.MaxDrops);
        _gDepth = _device.AllocateReadOnlyBuffer<float>(RainSimulation.MaxDrops);
        _gTiles = _device.AllocateReadOnlyBuffer<int>(_rain.Tiles.Length);
        _gStamps = _device.AllocateReadOnlyBuffer<float4>(RainSimulation.MaxStamps);
        _gErases = _device.AllocateReadOnlyBuffer<float4>(RainSimulation.MaxErases);
        _gDrizzle = _device.AllocateReadWriteTexture2D<Rgba32, float4>(4, 4);
        var (alpha, color) = RainDropTextures.Create();   // 程序生成的水珠形状 / 折射贴图
        _gDropAlpha = _device.AllocateReadOnlyTexture2D<Rgba32, float4>(alpha, RainDropTextures.Size, RainDropTextures.Size);
        _gDropColor = _device.AllocateReadOnlyTexture2D<Rgba32, float4>(color, RainDropTextures.Size, RainDropTextures.Size);
    }

    /// <summary>渲染线程：推进雨水模拟，在 GPU 上更新雾珠层，上传这一帧的大水珠。没在下雨且玻璃已干时什么都不做。</summary>
    private void StepRain(float dt)
    {
        if (_gRects == null) return;
        if (_aRain < 0.02f && !_rainActive) return;

        _rain.Step(dt, _aRain, _aspect);

        // 雾珠层尺寸跟随屏幕宽高比（高固定 1080 模拟像素）
        if (_gDrizzle!.Width != _rain.MapW || _gDrizzle.Height != (int)RainSimulation.SimH)
        {
            var old = _gDrizzle;
            _gDrizzle = _device!.AllocateReadWriteTexture2D<Rgba32, float4>(_rain.MapW, (int)RainSimulation.SimH);
            _ = Task.Delay(3000).ContinueWith(_ => old.Dispose());
        }

        if (_rain.CleaningFrames > 0)
            _device!.For(_gDrizzle.Width, _gDrizzle.Height, new RainFadeShader(_gDrizzle, MathF.Pow(0.95f, Math.Min(dt * 60f, 2f))));
        if (_rain.EraseCount > 0)
        {
            _gErases!.CopyFrom(Cast(_rain.Erases.AsSpan(0, _rain.EraseCount)));
            _device!.For(48, 64, _rain.EraseCount, new RainEraseShader(_gDrizzle, _gErases));
        }
        if (_rain.StampCount > 0)
        {
            _gStamps!.CopyFrom(Cast(_rain.Stamps.AsSpan(0, _rain.StampCount)));
            _device!.For(16, 24, _rain.StampCount, new RainStampShader(_gDrizzle, _gStamps, _gDropAlpha!, _gDropColor!));
        }

        _gRects.CopyFrom(Cast(_rain.DropRects));
        _gDepth!.CopyFrom(_rain.DropDepth);
        _gTiles!.CopyFrom(_rain.Tiles);
        _rainActive = !_rain.IsIdle;
    }

    private static ReadOnlySpan<float4> Cast(ReadOnlySpan<ComputeSharp.Float4> s) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<ComputeSharp.Float4, float4>(s);
    // 下雨时水珠要有清晰的边缘，改用全分辨率渲染；不下雨时回到半分辨率省 GPU
    private DispatcherQueueTimer? _resTimer;

    private void UpdateResolution()
    {
        if (_gpu == null) return;
        double want = Services.AmbienceService.Current.Rain > 0.01f ? 1.0 : 0.5;
        if (Math.Abs(_gpu.ResolutionScale - want) > 0.01) _gpu.ResolutionScale = want;
    }

    /// <summary>是否正在用 GPU 渲染。</summary>
    public bool IsGpu => _gpu != null;

    private void InitGpu()
    {
        _device = GraphicsDevice.GetDefault();
        _gDummy = _device.AllocateReadOnlyTexture2D<Rgba32, float4>(
            new[] { new Rgba32(0, 0, 0, 255), new Rgba32(0, 0, 0, 255), new Rgba32(0, 0, 0, 255), new Rgba32(0, 0, 0, 255) }, 2, 2);
        _gpu = new AnimatedComputeShaderPanel(_device)
        {
            IsVerticalSyncEnabled = true,
            IsDynamicResolutionEnabled = false,
            ResolutionScale = 0.5,   // 背景本来就是模糊的，半分辨率渲染再由合成器放大，省一半 GPU
            IsPaused = true
        };
        InitRainResources();
        _gpu.ShaderRunner = new ShaderRunner<FluidShader>(CreateShader);
        Content = _gpu;
    }

    private static float3 F3(float[] c) => new(c[0], c[1], c[2]);

    /// <summary>每帧在渲染线程调用：推进能量 / 交叉淡入，取当前纹理，生成这一帧的着色器参数。</summary>
    private FluidShader CreateShader(TimeSpan elapsed)
    {
        float dt = (float)Math.Clamp((elapsed - _gLast).TotalSeconds, 0.001, 0.05);
        _gLast = elapsed;
        float k = dt * 60f;
        _energy += (_targetEnergy - _energy) * (1f - MathF.Pow(1f - 0.18f, k));
        _targetEnergy *= MathF.Pow(0.94f, k);
        if (_mix < 1f) _mix = MathF.Min(1f, _mix + dt / 1.2f);
        StepAmbience(dt);
        StepRain(dt);

        ReadOnlyTexture2D<Rgba32, float4> cur, prev;
        float has, mixv;
        lock (_gLock)
        {
            cur = _gCur ?? _gDummy!;
            prev = _gPrev ?? cur;
            has = _gCur != null ? 1f : 0f;
            mixv = _gPrev != null ? _mix : 1f;
        }
        return new FluidShader(
            _flowTime, _energy, mixv, has, prev, cur,
            F3(DefaultPalette[0]), F3(DefaultPalette[1]), F3(DefaultPalette[2]), F3(DefaultPalette[3]), F3(BaseColor),
            _clockTime, new float4(_aWarm, _aBright, _aSat, 0f), new float4(_rainActive ? MathF.Max(_aRain, 0.006f) : 0f, _aSnow, _aOver, 0f),
            _gDrizzle!, _gRects!, _gDepth!, _gTiles!, _rain.TilesX, _gDropAlpha!, _gDropColor!);
    }

    private ReadOnlyTexture2D<Rgba32, float4> UploadTexture(float[] field)
    {
        var px = new Rgba32[TexN * TexN];
        for (int i = 0; i < px.Length; i++)
            px[i] = new Rgba32(
                (byte)Math.Clamp(field[i * 3] * 255f, 0f, 255f),
                (byte)Math.Clamp(field[i * 3 + 1] * 255f, 0f, 255f),
                (byte)Math.Clamp(field[i * 3 + 2] * 255f, 0f, 255f), 255);
        return _device!.AllocateReadOnlyTexture2D<Rgba32, float4>(px, TexN, TexN);
    }

    private void SwapGpuTextures(float[]? field)
    {
        var fresh = field == null ? null : UploadTexture(field);
        ReadOnlyTexture2D<Rgba32, float4>? drop1, drop2 = null;
        lock (_gLock)
        {
            if (fresh == null) { drop1 = _gPrev; drop2 = _gCur; _gPrev = _gCur = null; }
            else if (_gCur == null) { drop1 = _gPrev; _gPrev = null; _gCur = fresh; }
            else { drop1 = _gPrev; _gPrev = _gCur; _gCur = fresh; }
        }
        // 渲染线程可能还在用刚被换下的纹理，延迟几秒再释放
        var drops = new[] { drop1, drop2 };
        _ = Task.Delay(3000).ContinueWith(_ => { foreach (var d in drops) d?.Dispose(); });
    }

    public FluidBackground()
    {
        Content = _image;
        IsTabStop = false;
        IsHitTestVisible = false;
        try { InitGpu(); }
        catch
        {
            _gpu = null; _device = null;
            Content = _image;
        }
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (s, e) => Frame();
        SizeChanged += (s, e) =>
        {
            EnsureBitmap();
            if (e.NewSize.Height > 1) _aspect = (float)(e.NewSize.Width / e.NewSize.Height);
        };
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        if (_gpu != null)
        {
            UpdateResolution();
            if (_resTimer == null)
            {
                _resTimer = DispatcherQueue.CreateTimer();
                _resTimer.Interval = TimeSpan.FromSeconds(3);
                _resTimer.Tick += (s, e) => UpdateResolution();
            }
            _resTimer.Start();
            _gpu.IsPaused = false;
            return;
        }
        _clock.Restart();
        _lastTicks = 0;
        EnsureBitmap();
        _timer.Start();
    }

    public void Stop()
    {
        _running = false;
        _resTimer?.Stop();
        if (_gpu != null) _gpu.IsPaused = true;
        _timer.Stop();
    }

    /// <summary>低频能量（0~1），让背景随节奏轻微脉动。</summary>
    public void SetEnergy(float energy) => _targetEnergy = Math.Clamp(energy, 0f, 1f);

    #region 封面 → 色块图

    public async void SetCover(string? path)
    {
        int token = ++_coverToken;
        float[]? field = null;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                var px = await LoadPixels48(path);
                field = await Task.Run(() => MakeColorField(px));
            }
            catch { field = null; }
        }
        if (token != _coverToken) return;

        if (_gpu != null) SwapGpuTextures(field);

        if (field == null)
        {
            _texCur = _texPrev = null;
            _mix = 1f;
        }
        else if (_texCur == null)
        {
            _texPrev = null;
            _texCur = field;
            _mix = 1f; // 第一张直接显示
        }
        else
        {
            _texPrev = _texCur;
            _texCur = field;
            _mix = 0f; // 新旧交叉淡入
        }
    }

    private static async Task<byte[]> LoadPixels48(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = 48, ScaledHeight = 48, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return data.DetachPixelData();
    }

    /// <summary>k-means 取主色（至多 6 个，按占比降序，太接近的合并），返回 (R,G,B,占比)。</summary>
    private static List<float[]>? ExtractColors(byte[] rgba, int k = 6)
    {
        int n = 48 * 48;
        var px = new float[n][];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            px[i] = new float[] { rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2] };
            sum += px[i][0] + px[i][1] + px[i][2];
        }
        var rng = new Random((int)(sum % int.MaxValue));

        float Dist2(float[] a, float[] b) => (a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]);

        // k-means++ 初始化
        var centers = new List<float[]> { (float[])px[rng.Next(n)].Clone() };
        var d = new double[n];
        for (int c = 1; c < k; c++)
        {
            double total = 0;
            for (int i = 0; i < n; i++)
            {
                double best = double.MaxValue;
                foreach (var ctr in centers) best = Math.Min(best, Dist2(px[i], ctr));
                d[i] = best; total += best;
            }
            int pick;
            if (total > 1e-6)
            {
                double r = rng.NextDouble() * total, acc = 0;
                pick = n - 1;
                for (int i = 0; i < n; i++) { acc += d[i]; if (acc >= r) { pick = i; break; } }
            }
            else pick = rng.Next(n); // 纯色封面
            centers.Add((float[])px[pick].Clone());
        }

        var labels = new int[n];
        for (int iter = 0; iter < 12; iter++)
        {
            for (int i = 0; i < n; i++)
            {
                float best = float.MaxValue; int bi = 0;
                for (int c = 0; c < k; c++) { float dd = Dist2(px[i], centers[c]); if (dd < best) { best = dd; bi = c; } }
                labels[i] = bi;
            }
            for (int c = 0; c < k; c++)
            {
                float r = 0, g = 0, b = 0; int cnt = 0;
                for (int i = 0; i < n; i++) if (labels[i] == c) { r += px[i][0]; g += px[i][1]; b += px[i][2]; cnt++; }
                if (cnt > 0) centers[c] = new[] { r / cnt, g / cnt, b / cnt };
            }
        }

        var counts = new int[k];
        foreach (var l in labels) counts[l]++;
        var result = new List<float[]>();
        foreach (var c in Enumerable.Range(0, k).OrderByDescending(i => counts[i]))
        {
            if (counts[c] < n * 0.02) continue;
            var col = centers[c];
            if (result.Any(r => Math.Sqrt(Dist2(col, r)) < 38.0)) continue; // 太接近的合并，保证区分度
            result.Add(new[] { col[0], col[1], col[2], counts[c] / (float)n });
        }
        return result.Count > 0 ? result : null;
    }

    #region 取色后的美化

    private static void RgbToHsl(float r, float g, float b, out float h, out float s, out float l)
    {
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        l = (max + min) / 2f;
        float d = max - min;
        if (d < 1e-5f) { h = 0; s = 0; return; }
        s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
        if (max == r) h = (g - b) / d + (g < b ? 6f : 0f);
        else if (max == g) h = (b - r) / d + 2f;
        else h = (r - g) / d + 4f;
        h /= 6f;
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0) t += 1; if (t > 1) t -= 1;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 0.5f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }

    private static void HslToRgb(float h, float s, float l, out float r, out float g, out float b)
    {
        if (s < 1e-5f) { r = g = b = l; return; }
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s, p = 2f * l - q;
        r = HueToRgb(p, q, h + 1f / 3f); g = HueToRgb(p, q, h); b = HueToRgb(p, q, h - 1f / 3f);
    }

    /// <summary>
    /// 把取到的主色“洗”得更好看：有颜色的适度提饱和，并把明度收进 0.30~0.66；
    /// 近乎灰色的保持低饱和。避免米色 / 灰色大面积混在一起变成脏灰（着色器里还会再整体提一次饱和）。
    /// </summary>
    private static void Beautify(List<float[]> colors)
    {
        foreach (var c in colors)
        {
            RgbToHsl(c[0] / 255f, c[1] / 255f, c[2] / 255f, out var h, out var s, out var l);
            if (s >= 0.12f) s = Math.Clamp(s * 1.18f, 0.38f, 0.90f);
            l = Math.Clamp(l, 0.30f, 0.66f);
            HslToRgb(h, s, l, out var r, out var g, out var b);
            c[0] = r * 255f; c[1] = g * 255f; c[2] = b * 255f;
        }
    }

    #endregion

    /// <summary>主色 → 美化 → Voronoi 分区 → 高斯模糊，得到 TexN×TexN 的色块图（0..1）。</summary>
    private static float[]? MakeColorField(byte[] rgba)
    {
        var colors = ExtractColors(rgba);
        if (colors == null) return null;
        Beautify(colors);

        double seedSum = colors.Sum(c => c[0] + c[1] + c[2]) * 1000;
        var rng = new Random((int)(seedSum % int.MaxValue));
        const int nSeeds = 9;
        float totalW = colors.Sum(c => c[3]);

        var seedColor = new List<int>();
        for (int i = 0; i < colors.Count; i++) seedColor.Add(i);
        for (int i = colors.Count; i < nSeeds; i++)
        {
            double r = rng.NextDouble() * totalW, acc = 0;
            int pick = colors.Count - 1;
            for (int c = 0; c < colors.Count; c++) { acc += colors[c][3]; if (acc >= r) { pick = c; break; } }
            seedColor.Add(pick);
        }
        var seeds = seedColor.Select(_ => new[] { (float)rng.NextDouble() * TexN, (float)rng.NextDouble() * TexN }).ToArray();

        var best = new float[TexN * TexN];
        var owner = new int[TexN * TexN];
        Array.Fill(best, float.MaxValue);
        for (int s = 0; s < seeds.Length; s++)
        {
            float sx = seeds[s][0], sy = seeds[s][1];
            foreach (float mx in new[] { sx, -sx, 2 * TexN - sx })
                foreach (float my in new[] { sy, -sy, 2 * TexN - sy })
                    for (int y = 0; y < TexN; y++)
                        for (int x = 0; x < TexN; x++)
                        {
                            float dd = (x - mx) * (x - mx) + (y - my) * (y - my);
                            int idx = y * TexN + x;
                            if (dd < best[idx]) { best[idx] = dd; owner[idx] = s; }
                        }
        }

        var rgb = new float[TexN * TexN * 3];
        for (int i = 0; i < TexN * TexN; i++)
        {
            var c = colors[seedColor[owner[i]]];
            rgb[i * 3] = c[0]; rgb[i * 3 + 1] = c[1]; rgb[i * 3 + 2] = c[2];
        }

        var kernel = GaussianKernel(TexN * 0.045);
        for (int pass = 0; pass < 2; pass++)
        {
            rgb = BlurAxis(rgb, kernel, horizontal: true);
            rgb = BlurAxis(rgb, kernel, horizontal: false);
        }
        for (int i = 0; i < rgb.Length; i++) rgb[i] = Math.Clamp(rgb[i], 0f, 255f) / 255f;
        return rgb;
    }

    private static float[] GaussianKernel(double sigma)
    {
        int radius = Math.Max(1, (int)(sigma * 3.0));
        var k = new float[radius * 2 + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++) { k[i + radius] = (float)Math.Exp(-(i * i) / (2.0 * sigma * sigma)); sum += k[i + radius]; }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        return k;
    }

    private static int Reflect(int i, int n)
    {
        if (i < 0) i = -i;
        if (i >= n) i = 2 * (n - 1) - i;
        return Math.Clamp(i, 0, n - 1);
    }

    private static float[] BlurAxis(float[] src, float[] kernel, bool horizontal)
    {
        var dst = new float[src.Length];
        int r = kernel.Length / 2;
        for (int y = 0; y < TexN; y++)
            for (int x = 0; x < TexN; x++)
                for (int ch = 0; ch < 3; ch++)
                {
                    float acc = 0;
                    for (int i = -r; i <= r; i++)
                    {
                        int sx = horizontal ? Reflect(x + i, TexN) : x;
                        int sy = horizontal ? y : Reflect(y + i, TexN);
                        acc += src[(sy * TexN + sx) * 3 + ch] * kernel[i + r];
                    }
                    dst[(y * TexN + x) * 3 + ch] = acc;
                }
        return dst;
    }

    #endregion

    #region 逐帧渲染

    private void EnsureBitmap()
    {
        double aw = ActualWidth, ah = ActualHeight;
        if (aw < 16 || ah < 16) return;
        int h = RenderH;
        int w = (int)Math.Clamp(Math.Round(h * aw / ah), 96, 256);
        if (_bitmap != null && w == _w && h == _h) return;
        _w = w; _h = h;
        _bitmap = new WriteableBitmap(w, h);
        _buffer = new byte[w * h * 4];
        _image.Source = _bitmap;
    }

    private void Frame()
    {
        if (_bitmap == null) { EnsureBitmap(); if (_bitmap == null) return; }

        long ticks = _clock.ElapsedTicks;
        float dt = _lastTicks == 0 ? 0.033f : (float)((ticks - _lastTicks) / (double)Stopwatch.Frequency);
        _lastTicks = ticks;
        dt = Math.Clamp(dt, 0.001f, 0.05f);

        StepAmbience(dt);
        _time = _flowTime;
        float k = dt * 60f;
        _energy += (_targetEnergy - _energy) * (1f - MathF.Pow(1f - 0.18f, k));
        _targetEnergy *= MathF.Pow(0.94f, k);
        if (_mix < 1f) _mix = MathF.Min(1f, _mix + dt / 1.2f); // 1.2 秒交叉淡入

        Render();
        _buffer.CopyTo(_bitmap.PixelBuffer);
        _bitmap.Invalidate();
    }

    private struct Layer
    {
        public float Cx, Cy, InvScale, Cos, Sin;
    }

    /// <summary>一张封面纹理对应的五层参数（每帧只算一次，像素循环里只做采样）。</summary>
    private sealed class Stack
    {
        public readonly Layer[] L = new Layer[5];
        public readonly float[] MaskR0 = new float[5];
        public readonly float[] MaskR1 = new float[5];
        public readonly float[] MaskW = new float[5];
    }

    private static Stack MakeStack(float t, float e)
    {
        var s = new Stack();
        float pulse = 1f + e * 0.12f;

        void Set(int i, float cx, float cy, float scale, float angle)
        {
            s.L[i] = new Layer { Cx = cx, Cy = cy, InvScale = 1f / scale, Cos = MathF.Cos(angle), Sin = MathF.Sin(angle) };
        }

        Set(0, 0f, 0f, 2.0f + 0.25f * MathF.Sin(t * 0.11f), t * 0.09f);
        Set(1, -0.55f + 0.40f * MathF.Sin(t * 0.23f), 0.15f + 0.28f * MathF.Cos(t * 0.19f), 1.25f * pulse, -t * 0.22f + 1.1f);
        Set(2, 0.60f + 0.38f * MathF.Cos(t * 0.21f), -0.10f + 0.30f * MathF.Sin(t * 0.27f), 1.10f * pulse, t * 0.26f + 2.3f);
        Set(3, 0.05f + 0.45f * MathF.Sin(t * 0.17f + 1.3f), 0.30f + 0.22f * MathF.Cos(t * 0.31f), 0.95f * pulse, -t * 0.30f + 4.0f);
        Set(4, -0.10f + 0.50f * MathF.Cos(t * 0.15f + 2.1f), -0.35f + 0.20f * MathF.Sin(t * 0.25f), 1.05f * pulse, t * 0.19f + 5.2f);

        s.MaskR0[1] = 0.10f; s.MaskR1[1] = 0.75f * pulse; s.MaskW[1] = 0.90f;
        s.MaskR0[2] = 0.10f; s.MaskR1[2] = 0.70f * pulse; s.MaskW[2] = 0.90f;
        s.MaskR0[3] = 0.05f; s.MaskR1[3] = 0.60f * pulse; s.MaskW[3] = 0.85f;
        s.MaskR0[4] = 0.05f; s.MaskR1[4] = 0.65f * pulse; s.MaskW[4] = 0.80f;
        return s;
    }

    private static float Smoothstep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>双线性采样，镜像重复寻址。</summary>
    private static void Sample(float[] tex, float u, float v, out float r, out float g, out float b)
    {
        u -= 2f * MathF.Floor(u / 2f); if (u > 1f) u = 2f - u;
        v -= 2f * MathF.Floor(v / 2f); if (v > 1f) v = 2f - v;
        float fx = u * TexN - 0.5f, fy = v * TexN - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        int x1 = Math.Clamp(x0 + 1, 0, TexN - 1), y1 = Math.Clamp(y0 + 1, 0, TexN - 1);
        x0 = Math.Clamp(x0, 0, TexN - 1); y0 = Math.Clamp(y0, 0, TexN - 1);

        int i00 = (y0 * TexN + x0) * 3, i10 = (y0 * TexN + x1) * 3, i01 = (y1 * TexN + x0) * 3, i11 = (y1 * TexN + x1) * 3;
        float w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;
        r = tex[i00] * w00 + tex[i10] * w10 + tex[i01] * w01 + tex[i11] * w11;
        g = tex[i00 + 1] * w00 + tex[i10 + 1] * w10 + tex[i01 + 1] * w01 + tex[i11 + 1] * w11;
        b = tex[i00 + 2] * w00 + tex[i10 + 2] * w10 + tex[i01 + 2] * w01 + tex[i11 + 2] * w11;
    }

    private static void CoverStack(float[] tex, Stack s, float px, float py, out float r, out float g, out float b)
    {
        float cr, cg, cb;
        void Layer(int i, out float lr, out float lg, out float lb)
        {
            var l = s.L[i];
            float dx = px - l.Cx, dy = py - l.Cy;
            // GLSL: rot(a) * v = (c*x + s*y, -s*x + c*y)
            float qx = (l.Cos * dx + l.Sin * dy) * l.InvScale + 0.5f;
            float qy = (-l.Sin * dx + l.Cos * dy) * l.InvScale + 0.5f;
            Sample(tex, qx, qy, out lr, out lg, out lb);
        }

        Layer(0, out r, out g, out b);
        for (int i = 1; i < 5; i++)
        {
            var l = s.L[i];
            float dist = MathF.Sqrt((px - l.Cx) * (px - l.Cx) + (py - l.Cy) * (py - l.Cy));
            float m = (1f - Smoothstep(s.MaskR0[i], s.MaskR1[i], dist)) * s.MaskW[i];
            if (m <= 0.0005f) continue;
            Layer(i, out cr, out cg, out cb);
            r += (cr - r) * m; g += (cg - g) * m; b += (cb - b) * m;
        }
    }

    // ---- 噪声（与着色器相同的哈希 + 梯度噪声）----
    private static float Fract(float x) => x - MathF.Floor(x);

    private static void Hash(float x, float y, out float hx, out float hy)
    {
        float a = x * 127.1f + y * 311.7f, b = x * 269.5f + y * 183.3f;
        hx = -1f + 2f * Fract(MathF.Sin(a) * 43758.5453123f);
        hy = -1f + 2f * Fract(MathF.Sin(b) * 43758.5453123f);
    }

    private static float Noise(float x, float y)
    {
        float ix = MathF.Floor(x), iy = MathF.Floor(y);
        float fx = x - ix, fy = y - iy;
        float ux = fx * fx * (3f - 2f * fx), uy = fy * fy * (3f - 2f * fy);

        Hash(ix, iy, out var h00x, out var h00y);
        Hash(ix + 1, iy, out var h10x, out var h10y);
        Hash(ix, iy + 1, out var h01x, out var h01y);
        Hash(ix + 1, iy + 1, out var h11x, out var h11y);

        float d00 = h00x * fx + h00y * fy;
        float d10 = h10x * (fx - 1) + h10y * fy;
        float d01 = h01x * fx + h01y * (fy - 1);
        float d11 = h11x * (fx - 1) + h11y * (fy - 1);
        float a = d00 + (d10 - d00) * ux;
        float b = d01 + (d11 - d01) * ux;
        return a + (b - a) * uy;
    }

    private void Render()
    {
        int W = _w, H = _h;
        float aspect = W / (float)H;
        float t = _time, e = _energy;
        var cur = _texCur;
        var prev = _texPrev ?? _texCur;
        float mix = _texPrev != null ? _mix : 1f;
        bool hasCover = cur != null;

        var stack = hasCover ? MakeStack(t, e) : null;

        // 无封面：调色盘四个高斯光斑中心
        float[] pc = new float[8];
        if (!hasCover)
        {
            pc[0] = -0.5f + 0.2f * MathF.Sin(t * 0.3f); pc[1] = 0.2f + 0.2f * MathF.Cos(t * 0.25f);
            pc[2] = 0.5f + 0.2f * MathF.Cos(t * 0.27f); pc[3] = -0.2f + 0.2f * MathF.Sin(t * 0.33f);
            pc[4] = 0.0f + 0.2f * MathF.Sin(t * 0.21f); pc[5] = 0.4f + 0.2f * MathF.Cos(t * 0.31f);
            pc[6] = 0.1f + 0.2f * MathF.Cos(t * 0.23f); pc[7] = -0.4f + 0.2f * MathF.Sin(t * 0.19f);
        }

        float warpAmp = 0.16f + e * 0.10f;
        var buf = _buffer;

        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                float uvx = (x + 0.5f) / W, uvy = 1f - (y + 0.5f) / H;
                float px = (uvx - 0.5f) * aspect, py = uvy - 0.5f;

                // 两级流体扰动：大尺度慢涌 + 小尺度快涌
                float wx = Noise(px * 1.2f + t * 0.12f, py * 1.2f + t * 0.12f);
                float wy = Noise(px * 1.2f + 5.2f - t * 0.10f, py * 1.2f + 1.3f - t * 0.10f);
                wx += 0.5f * Noise(px * 2.6f - t * 0.20f + 3.7f, py * 2.6f - t * 0.20f + 3.7f);
                wy += 0.5f * Noise(px * 2.6f + t * 0.18f + 8.1f, py * 2.6f + t * 0.18f + 8.1f);
                px += wx * warpAmp; py += wy * warpAmp;

                float r, g, b;
                if (hasCover)
                {
                    CoverStack(prev!, stack!, px, py, out r, out g, out b);
                    if (mix < 1f)
                    {
                        CoverStack(cur!, stack!, px, py, out var r2, out var g2, out var b2);
                        r += (r2 - r) * mix; g += (g2 - g) * mix; b += (b2 - b) * mix;
                    }
                }
                else
                {
                    // 调色盘：四个高斯权重光斑 + 底色
                    float sw = 0.15f, sr = BaseColor[0] * 0.15f, sg = BaseColor[1] * 0.15f, sb = BaseColor[2] * 0.15f;
                    for (int i = 0; i < 4; i++)
                    {
                        float dx = px - pc[i * 2], dy = py - pc[i * 2 + 1];
                        float k = i < 2 ? 2.2f : 2.6f;
                        float w = MathF.Exp(-k * (dx * dx + dy * dy));
                        sw += w; sr += DefaultPalette[i][0] * w; sg += DefaultPalette[i][1] * w; sb += DefaultPalette[i][2] * w;
                    }
                    r = sr / sw; g = sg / sw; b = sb / sw;
                }

                // 提饱和：模糊会把颜色洗灰，这里补回来
                float luma = r * 0.299f + g * 0.587f + b * 0.114f;
                r = MathF.Max(luma + (r - luma) * 1.7f, 0f);
                g = MathF.Max(luma + (g - luma) * 1.7f, 0f);
                b = MathF.Max(luma + (b - luma) * 1.7f, 0f);

                // 压亮度：保证白字在浅色封面上也清楚
                r *= 0.62f; g *= 0.62f; b *= 0.62f;
                r = r / (1f + r * 0.45f) * 1.15f;
                g = g / (1f + g * 0.45f) * 1.15f;
                b = b / (1f + b * 0.45f) * 1.15f;

                float boost = 1f + e * 0.08f;
                r *= boost; g *= boost; b *= boost;

                // 氛围（CPU 路径分辨率太低，不画雨雪，只做色调和阴天）
                float sat = _aSat * (1f - _aOver * 0.55f);
                float l2 = r * 0.299f + g * 0.587f + b * 0.114f;
                r = MathF.Max(l2 + (r - l2) * sat, 0f); g = MathF.Max(l2 + (g - l2) * sat, 0f); b = MathF.Max(l2 + (b - l2) * sat, 0f);
                float bri = _aBright * (1f - _aOver * 0.08f);
                r *= (1f + 0.20f * _aWarm) * bri; g *= (1f + 0.04f * _aWarm) * bri; b *= (1f - 0.22f * _aWarm) * bri;

                // 很弱的边缘收暗
                float vx = (uvx - 0.5f), vy = (uvy - 0.5f) * 1.2f;
                float vig = Smoothstep(1.35f, 0.35f, MathF.Sqrt(vx * vx + vy * vy));
                float vm = 0.78f + (1f - 0.78f) * vig;
                r *= vm; g *= vm; b *= vm;

                // 抖动，避免暗部色带
                float dither = (Fract(MathF.Sin(x * 12.9898f + y * 78.233f) * 43758.5453f) - 0.5f) / 255f;
                r += dither; g += dither; b += dither;

                int o = (y * W + x) * 4;
                buf[o] = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
                buf[o + 1] = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
                buf[o + 2] = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
                buf[o + 3] = 255;
            }
        });
    }

    #endregion
}
