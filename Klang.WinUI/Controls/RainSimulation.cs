using System;
using System.Collections.Generic;
using ComputeSharp;

namespace Klang_WinUI.Controls;

/// <summary>
/// 玻璃上的雨水模拟，移植自 Codrops 的 RainEffect（Lucas Bebber，https://github.com/codrops/RainEffect）。
/// 许可：可免费集成到个人或商业项目中，不得原样转载、再分发或出售。
///
/// 这里只做水珠的物理（逐颗对象：落下、走走停停地滑、拖出小水珠、合并、蒸发），在 CPU 上跑；
/// 画面相关的部分全部交给 GPU：
/// - 雾珠层：一张常驻的纹理，每帧在随机位置“盖章”一批小水珠（RainStampShader），大水珠滑过的地方擦掉（RainEraseShader）；
/// - 水面图：雾珠层 + 按顺序叠上去的大水珠贴图（FluidShader 里逐像素合成），R/G = 折射方向，B = 厚度，A = 水量。
/// 坐标：模拟像素，屏幕高 = <see cref="SimH"/>，x 向右，y 向下（与原版画布一致）。
/// </summary>
internal sealed class RainSimulation
{
    public const float SimH = 1080f;
    public const int MaxDrops = 900;
    public const int MaxStamps = 512;
    public const int MaxErases = 512;
    public const int TileSize = 108;           // 分块边长（模拟像素）
    public const int TilesY = 10;              // = SimH / TileSize
    public const int MaxTilesX = 26;
    public const int TileCap = 63;
    public const int TileStride = TileCap + 1;

    private sealed class Drop
    {
        public float X, Y, R, SpreadX, SpreadY, Momentum, MomentumX, LastSpawn, NextSpawn, Shrink;
        public Drop? Parent;
        public bool IsNew = true, Killed;
    }

    private List<Drop> _drops = new();
    private List<Drop> _next = new();
    private readonly Random _rng = new();

    // ---- 每帧交给 GPU 的数据 ----
    /// <summary>大水珠：(左上 x, 左上 y, 宽, 高)，单位模拟像素。</summary>
    public readonly Float4[] DropRects = new Float4[MaxDrops];
    /// <summary>每颗大水珠的厚度（0~1，对应原版 255 档贴图的蓝色通道）。</summary>
    public readonly float[] DropDepth = new float[MaxDrops];
    public readonly int[] Tiles = new int[MaxTilesX * TilesY * TileStride];
    /// <summary>这一帧要盖到雾珠层上的小水珠：(左上 x, 左上 y, 宽, 高)。</summary>
    public readonly Float4[] Stamps = new Float4[MaxStamps];
    public int StampCount { get; private set; }
    /// <summary>这一帧要从雾珠层擦掉的区域：(左上 x, 左上 y, 宽, 高)，椭圆。</summary>
    public readonly Float4[] Erases = new Float4[MaxErases];
    public int EraseCount { get; private set; }
    /// <summary>停雨后雾珠层逐帧变淡的剩余帧数（原版 clearTexture）。</summary>
    public float CleaningFrames { get; private set; }
    public int TilesX { get; private set; } = 18;
    public int MapW { get; private set; } = 1920;

    // ---- 参数（原版 weatherData，按雨量在 小雨 / 中雨 / 暴雨 之间插值）----
    private float _minR = 10, _maxR = 40, _rainChance = 0.35f, _rainLimit = 6, _drizzle = 50;
    private float _drizzleMin = 2, _drizzleMax = 4.5f, _trailMin = 0.2f, _trailMax = 0.35f;
    private bool _raining;
    private float _drizzleCounter;
    private const float TrailRate = 1f;
    private const float CollisionRadius = 0.65f, CollisionRadiusIncrease = 0.01f;
    private const float DrizzleCleaningRadiusMultiplier = 0.45f;
    private static readonly float[] SpawnArea = { -0.1f, 0.95f };

    private float Rand() => (float)_rng.NextDouble();
    private float Rand(float a, float b) => a + Rand() * (b - a);
    private bool Chance(float c) => Rand() <= c;

    private void ApplyAmount(float amount)
    {
        // 0.3 = 小雨（drizzle），0.6 = 中雨（rain），1.0 = 暴雨（storm）
        static float L(float a, float b, float t) => a + (b - a) * t;
        if (amount < 0.6f)
        {
            float t = Math.Clamp((amount - 0.3f) / 0.3f, 0f, 1f);
            _minR = 10; _maxR = 40;
            _rainChance = L(0.15f, 0.35f, t); _rainLimit = L(2, 6, t); _drizzle = L(10, 50, t);
            _drizzleMin = 2; _drizzleMax = 4.5f; _trailMin = 0.2f; _trailMax = 0.35f;
            if (amount < 0.3f) { float k = amount / 0.3f; _rainChance *= k; _drizzle *= k; }
        }
        else
        {
            float t = Math.Clamp((amount - 0.6f) / 0.4f, 0f, 1f);
            _minR = L(10, 20, t); _maxR = L(40, 45, t);
            _rainChance = L(0.35f, 0.55f, t); _rainLimit = 6; _drizzle = L(50, 80, t);
            _drizzleMin = 2; _drizzleMax = L(4.5f, 6f, t); _trailMin = L(0.2f, 0.15f, t); _trailMax = L(0.35f, 0.3f, t);
        }
    }

    public bool IsIdle => _drops.Count == 0 && CleaningFrames <= 0 && !_raining;

    /// <summary>推进一帧。dt 秒，amount 0~1 为雨量，aspect 为屏幕宽高比。</summary>
    public void Step(float dt, float amount, float aspect)
    {
        float ts = Math.Min(dt * 60f, 2f);
        float width = SimH * aspect;
        MapW = Math.Max(16, (int)MathF.Ceiling(width));
        TilesX = Math.Clamp((int)MathF.Ceiling(width / TileSize), 1, MaxTilesX);
        float areaMul = width * SimH / (1024f * 768f);

        bool raining = amount >= 0.02f;
        if (_raining && !raining)
        {
            // 停雨：现有水珠陆续蒸发，雾珠层慢慢褪去
            foreach (var d in _drops) d.Shrink = 0.1f + Rand(0, 0.5f);
            CleaningFrames = 50;
        }
        _raining = raining;
        if (raining) ApplyAmount(amount);
        float deltaR = _maxR - _minR;
        StampCount = 0;
        EraseCount = 0;
        if (CleaningFrames > 0) CleaningFrames -= ts;

        // 雾珠：每帧在随机位置添一批小水珠
        if (_raining)
        {
            _drizzleCounter += _drizzle * ts * areaMul;
            while (_drizzleCounter >= 1f)
            {
                _drizzleCounter--;
                float r = _drizzleMin + MathF.Pow(Rand(), 2f) * (_drizzleMax - _drizzleMin);
                if (StampCount < MaxStamps) Stamps[StampCount++] = SpriteRect(Rand(0, width), Rand(0, SimH), r, 0, 0);
            }
        }

        _next.Clear();

        // 新落下的雨点
        if (_raining)
        {
            float limit = _rainLimit * ts * areaMul;
            int count = 0;
            while (Chance(_rainChance * ts * areaMul) && count < limit && _drops.Count + _next.Count < MaxDrops)
            {
                count++;
                float r = _minR + MathF.Pow(Rand(), 3f) * deltaR;
                _next.Add(new Drop
                {
                    X = Rand(0, width),
                    Y = Rand(SimH * SpawnArea[0], SimH * SpawnArea[1]),
                    R = r,
                    Momentum = 1f + (r - _minR) * 0.3f + Rand(0, 0.5f),
                    SpreadX = 1.5f,
                    SpreadY = 1.5f
                });
            }
        }

        _drops.Sort((a, b) => (a.Y * width + a.X).CompareTo(b.Y * width + b.X));

        for (int i = 0; i < _drops.Count; i++)
        {
            var d = _drops[i];
            if (d.Killed) continue;

            // 重力
            if (Chance((d.R - _minR) * (0.1f / deltaR) * ts))
                d.Momentum += Rand(0, d.R / _maxR * 4f);
            // 小水珠蒸发
            if (d.R <= _minR && Chance(0.05f * ts)) d.Shrink += 0.01f;
            d.R -= d.Shrink * ts;
            if (d.R <= 0) { d.Killed = true; continue; }

            // 拖出小水珠
            if (_raining)
            {
                d.LastSpawn += d.Momentum * ts * TrailRate;
                if (d.LastSpawn > d.NextSpawn && _drops.Count + _next.Count < MaxDrops)
                {
                    _next.Add(new Drop
                    {
                        X = d.X + Rand(-d.R, d.R) * 0.1f,
                        Y = d.Y - d.R * 0.01f,
                        R = d.R * Rand(_trailMin, _trailMax),
                        SpreadY = d.Momentum * 0.15f,
                        Parent = d
                    });
                    d.R *= MathF.Pow(0.97f, ts);
                    d.LastSpawn = 0;
                    d.NextSpawn = Rand(_minR, _maxR) - d.Momentum * 2f * TrailRate + (_maxR - d.R);
                }
            }

            d.SpreadX *= MathF.Pow(0.4f, ts);
            d.SpreadY *= MathF.Pow(0.7f, ts);

            bool moved = d.Momentum > 0;
            if (moved)
            {
                d.Y += d.Momentum;
                d.X += d.MomentumX;
                if (d.Y > SimH + d.R) { d.Killed = true; continue; }
            }

            // 合并
            if (moved || d.IsNew)
            {
                int end = Math.Min(_drops.Count, i + 70);
                for (int j = i + 1; j < end; j++)
                {
                    var o = _drops[j];
                    if (o.Killed || d.R <= o.R || d.Parent == o || o.Parent == d) continue;
                    float dx = o.X - d.X, dy = o.Y - d.Y;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist < (d.R + o.R) * (CollisionRadius + d.Momentum * CollisionRadiusIncrease * ts))
                    {
                        float target = MathF.Min(MathF.Sqrt(d.R * d.R + o.R * o.R * 0.8f), _maxR);
                        d.R = target;
                        d.MomentumX += dx * 0.1f;
                        d.SpreadX = d.SpreadY = 0;
                        o.Killed = true;
                        d.Momentum = MathF.Max(o.Momentum, MathF.Min(40f, d.Momentum + target * 0.04f + 1f));
                    }
                }
            }
            d.IsNew = false;

            // 阻力
            d.Momentum -= MathF.Max(1f, _minR * 0.5f - d.Momentum) * 0.1f * ts;
            if (d.Momentum < 0) d.Momentum = 0;
            d.MomentumX *= MathF.Pow(0.7f, ts);

            // 滑过的地方擦掉雾珠（原版 paint：以 (x, y) 为左上偏移 r 的 2r × 3r 椭圆）
            if (moved && _drizzle > 0 && EraseCount < MaxErases)
            {
                float pr = d.R * DrizzleCleaningRadiusMultiplier;
                Erases[EraseCount++] = new Float4(d.X - pr, d.Y - pr, pr * 2f, pr * 3f);
            }
            _next.Add(d);
        }

        (_drops, _next) = (_next, _drops);
        _drops.RemoveAll(k => k.Killed);
        Pack(deltaR);
    }

    /// <summary>原版 drawDrop 的贴图位置：以 (x, y) 为中心，宽 2r·(spreadX+1)，高 3r·(spreadY+1)。</summary>
    private static Float4 SpriteRect(float x, float y, float r, float spreadX, float spreadY)
    {
        float w = r * 2f * (spreadX + 1f), h = r * 3f * (spreadY + 1f);
        return new Float4(x - w * 0.5f, y - h * 0.5f, w, h);
    }

    private void Pack(float deltaR)
    {
        Array.Clear(Tiles, 0, TilesX * TilesY * TileStride);
        int n = 0;
        foreach (var d in _drops)
        {
            if (n >= MaxDrops) break;
            var rect = SpriteRect(d.X, d.Y, d.R, d.SpreadX, d.SpreadY);
            float depth = Math.Clamp((d.R - _minR) / deltaR * 0.9f, 0f, 1f) / ((d.SpreadX + d.SpreadY) * 0.5f + 1f);
            DropRects[n] = rect;
            DropDepth[n] = MathF.Floor(depth * 254f) / 255f;   // 原版只有 255 档
            int tx0 = (int)MathF.Floor(rect.X / TileSize), tx1 = (int)MathF.Floor((rect.X + rect.Z) / TileSize);
            int ty0 = (int)MathF.Floor(rect.Y / TileSize), ty1 = (int)MathF.Floor((rect.Y + rect.W) / TileSize);
            for (int ty = Math.Max(0, ty0); ty <= Math.Min(TilesY - 1, ty1); ty++)
                for (int tx = Math.Max(0, tx0); tx <= Math.Min(TilesX - 1, tx1); tx++)
                {
                    int b = (ty * TilesX + tx) * TileStride;
                    int c = Tiles[b];
                    if (c >= TileCap) continue;
                    Tiles[b + 1 + c] = n;   // 按绘制顺序记录，着色器里依次叠加
                    Tiles[b] = c + 1;
                }
            n++;
        }
    }
}
