using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Klang_WinUI.Services;

/// <summary>
/// 天气（用于沉浸背景的雨 / 雪 / 阴天效果），数据来自 Open-Meteo（免费、无需账号）。
/// 发送的数据：查找城市时发送输入的城市名；之后每 30 分钟发送该城市的经纬度（保留两位小数，约 1 公里）。
/// 不发送任何账号、设备或播放信息。只在“天气效果”开启时联网。
/// </summary>
public sealed class WeatherService
{
    private static readonly Lazy<WeatherService> _instance = new(() => new WeatherService());
    public static WeatherService Instance => _instance.Value;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Klang-WinUI (https://github.com/gwj0323/klang)");
        return c;
    }

    private static string Sep => L.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "，" : ", ";

    public sealed record Weather(int Code, int CloudCover, float Rain, float Snow, float Overcast, DateTime FetchedAt);

    public sealed record Place(string Name, string Detail, double Lat, double Lon)
    {
        public override string ToString() => string.IsNullOrEmpty(Detail) ? Name : Name + Sep + Detail;
    }

    /// <summary>最近一次取到的天气；没开启或还没取到时为 null。</summary>
    public Weather? Latest { get; private set; }

    /// <summary>天气更新后触发（任意线程）。</summary>
    public event Action? Updated;

    private Timer? _timer;
    private int _busy;

    private static AppState State => StateService.Instance.State;

    /// <summary>按当前设置开始或停止定时刷新（启动时和设置改动后调用）。</summary>
    public void Apply()
    {
        _timer?.Dispose();
        _timer = null;
        if (!State.AmbientWeather || State.WeatherLat == null || State.WeatherLon == null)
        {
            Latest = null;
            Updated?.Invoke();
            return;
        }
        _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, TimeSpan.FromMinutes(30));
    }

    public async Task RefreshAsync()
    {
        if (!State.AmbientWeather || State.WeatherLat is not double lat || State.WeatherLon is not double lon) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var url = "https://api.open-meteo.com/v1/forecast?latitude=" + Coord(lat) + "&longitude=" + Coord(lon)
                      + "&current=weather_code,cloud_cover,precipitation&timezone=auto";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            int code = cur.GetProperty("weather_code").GetInt32();
            int cloud = cur.TryGetProperty("cloud_cover", out var cc) && cc.ValueKind == JsonValueKind.Number ? cc.GetInt32() : 0;
            var (rain, snow, overcast) = Interpret(code, cloud);
            Latest = new Weather(code, cloud, rain, snow, overcast, DateTime.Now);
            CrashReporter.Note($"天气：代码 {code}，云量 {cloud}%");
            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            App.LogError("Weather", ex.Message);   // 保留上一次的结果
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static string Coord(double v) => Math.Round(v, 2).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>WMO 天气代码 → 雨 / 雪 / 阴天强度。</summary>
    public static (float Rain, float Snow, float Overcast) Interpret(int code, int cloudCover)
    {
        float rain = code switch
        {
            51 or 56 => 0.30f,            // 毛毛雨
            53 => 0.40f,
            55 or 57 => 0.50f,
            61 or 66 or 80 => 0.55f,      // 小雨 / 阵雨
            63 or 81 => 0.75f,
            65 or 67 or 82 => 1.00f,
            95 or 96 or 99 => 1.00f,      // 雷雨
            _ => 0f
        };
        float snow = code switch
        {
            77 => 0.35f,                  // 雪粒
            71 or 85 => 0.50f,
            73 => 0.75f,
            75 or 86 => 1.00f,
            _ => 0f
        };
        float overcast = Math.Clamp(cloudCover / 100f, 0f, 1f) * 0.7f;
        if (code is 45 or 48) overcast = Math.Max(overcast, 0.85f);     // 雾
        if (rain > 0 || snow > 0) overcast = Math.Max(overcast, 0.5f);
        if (code == 3) overcast = Math.Max(overcast, 0.7f);             // 阴
        return (rain, snow, overcast);
    }

    /// <summary>天气代码的简短描述（设置里显示当前天气）。</summary>
    public static string Describe(int code) => code switch
    {
        0 => L.T("晴"),
        1 or 2 => L.T("少云"),
        3 => L.T("阴"),
        45 or 48 => L.T("雾"),
        >= 51 and <= 57 => L.T("毛毛雨"),
        >= 61 and <= 67 => L.T("雨"),
        >= 71 and <= 77 => L.T("雪"),
        >= 80 and <= 82 => L.T("阵雨"),
        85 or 86 => L.T("阵雪"),
        >= 95 => L.T("雷雨"),
        _ => L.T("未知")
    };

    /// <summary>按名称查找城市（会把输入的名称发送到 Open-Meteo 的地点查询服务）。</summary>
    public static async Task<List<Place>> SearchAsync(string name)
    {
        var list = new List<Place>();
        name = name.Trim();
        if (name.Length == 0) return list;
        string lang = L.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
        var url = "https://geocoding-api.open-meteo.com/v1/search?count=8&format=json&language=" + lang
                  + "&name=" + Uri.EscapeDataString(name);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var results)) return list;
        foreach (var r in results.EnumerateArray())
        {
            string n = r.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
            var parts = new List<string>();
            foreach (var key in new[] { "admin1", "country" })
                if (r.TryGetProperty(key, out var v) && v.GetString() is string s && s.Length > 0 && s != n) parts.Add(s);
            list.Add(new Place(n, string.Join(Sep, parts), r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble()));
        }
        return list;
    }
}
