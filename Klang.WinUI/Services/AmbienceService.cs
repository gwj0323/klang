using System;
using System.Globalization;

namespace Klang_WinUI.Services;

/// <summary>
/// 沉浸背景的氛围参数：时段（本机时间）+ 天气（可选，联网）。
/// 背景每帧读 <see cref="Current"/>，自己平滑过渡到这里给出的目标值。
/// </summary>
public static class AmbienceService
{
    public readonly record struct Ambience(
        float Warmth,      // 色温：正 = 偏暖，负 = 偏冷（约 -0.3 ~ 0.3）
        float Brightness,  // 亮度倍数
        float Speed,       // 流动速度倍数
        float Saturation,  // 饱和度倍数
        float Rain,        // 0 ~ 1：玻璃上的水珠
        float Snow,        // 0 ~ 1：飘落的雪
        float Overcast)    // 0 ~ 1：阴天，降低饱和度
    {
        public static readonly Ambience Neutral = new(0, 1, 1, 1, 0, 0, 0);
    }

    // 一天中的关键时刻（小时）→ 色温 / 亮度 / 速度 / 饱和度；之间平滑插值
    private static readonly (float Hour, float Warm, float Bright, float Speed, float Sat)[] Keys =
    {
        (0f,  0.20f, 0.80f, 0.55f, 0.86f),   // 深夜：暗、慢、略暖
        (5f,  0.14f, 0.82f, 0.60f, 0.86f),
        (7f,  0.16f, 0.96f, 0.85f, 1.00f),   // 清晨：柔和的暖光
        (10f, 0.00f, 1.03f, 1.00f, 1.03f),
        (14f, -0.06f, 1.05f, 1.05f, 1.05f),  // 午后：明亮、略冷
        (17f, 0.10f, 1.00f, 0.95f, 1.05f),
        (19f, 0.26f, 0.93f, 0.85f, 1.02f),   // 傍晚：最暖
        (21f, 0.20f, 0.86f, 0.70f, 0.93f),
        (24f, 0.20f, 0.80f, 0.55f, 0.86f),
    };

    private static (float Warm, float Bright, float Speed, float Sat) ForHour(float hour)
    {
        for (int i = 0; i < Keys.Length - 1; i++)
        {
            var a = Keys[i];
            var b = Keys[i + 1];
            if (hour < a.Hour || hour > b.Hour) continue;
            float t = (hour - a.Hour) / (b.Hour - a.Hour);
            t = t * t * (3f - 2f * t);
            return (a.Warm + (b.Warm - a.Warm) * t, a.Bright + (b.Bright - a.Bright) * t,
                    a.Speed + (b.Speed - a.Speed) * t, a.Sat + (b.Sat - a.Sat) * t);
        }
        return (0, 1, 1, 1);
    }

    // 测试用：环境变量 KLANG_AMBIENCE="hour=23;rain=1;snow=0;overcast=0.5" 覆盖实际时间和天气
    private static readonly (float? Hour, float? Rain, float? Snow, float? Overcast) Override = ParseOverride();

    private static (float?, float?, float?, float?) ParseOverride()
    {
        float? h = null, r = null, s = null, o = null;
        var env = Environment.GetEnvironmentVariable("KLANG_AMBIENCE");
        if (string.IsNullOrWhiteSpace(env)) return (h, r, s, o);
        foreach (var part in env.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2 || !float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
            switch (kv[0].Trim().ToLowerInvariant())
            {
                case "hour": h = v; break;
                case "rain": r = v; break;
                case "snow": s = v; break;
                case "overcast": o = v; break;
            }
        }
        return (h, r, s, o);
    }

    public static bool HasOverride => Override.Hour != null || Override.Rain != null || Override.Snow != null || Override.Overcast != null;

    /// <summary>当前的目标氛围（每帧调用，开销很小）。</summary>
    public static Ambience Current
    {
        get
        {
            var st = StateService.Instance.State;
            float warm = 0, bright = 1, speed = 1, sat = 1;
            if (st.AmbientTimeOfDay || Override.Hour != null)
            {
                var now = DateTime.Now;
                float hour = Override.Hour ?? (float)(now.Hour + now.Minute / 60.0);
                (warm, bright, speed, sat) = ForHour(Math.Clamp(hour, 0f, 24f));
            }

            float rain = 0, snow = 0, overcast = 0;
            if (st.AmbientWeather)
            {
                var w = WeatherService.Instance.Latest;
                if (w != null) { rain = w.Rain; snow = w.Snow; overcast = w.Overcast; }
            }
            rain = Override.Rain ?? rain;
            snow = Override.Snow ?? snow;
            overcast = Override.Overcast ?? overcast;

            return new Ambience(warm, bright, speed, sat,
                Math.Clamp(rain, 0f, 1f), Math.Clamp(snow, 0f, 1f), Math.Clamp(overcast, 0f, 1f));
        }
    }
}
