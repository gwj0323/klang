using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace Klang_WinUI.Services;

/// <summary>从封面提取主色（缩小后做 k-means 聚类，偏向鲜艳而非过暗/过亮的颜色）。</summary>
public static class DominantColor
{
    public static readonly (Color Primary, Color Secondary) Fallback =
        (Color.FromArgb(255, 74, 99, 90), Color.FromArgb(255, 36, 44, 52));

    public static async Task<(Color Primary, Color Secondary)> GetAsync(string? coverPath)
    {
        if (string.IsNullOrEmpty(coverPath) || !File.Exists(coverPath)) return Fallback;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(coverPath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform { ScaledWidth = 48, ScaledHeight = 48 };
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var px = data.DetachPixelData();

            var pts = new List<(double R, double G, double B)>(48 * 48);
            for (int i = 0; i + 3 < px.Length; i += 4) pts.Add((px[i + 2], px[i + 1], px[i]));
            var pal = Cluster(pts);
            return (pal[0], pal[Math.Min(1, pal.Count - 1)]);
        }
        catch
        {
            return Fallback;
        }
    }

    private static List<Color> Cluster(List<(double R, double G, double B)> pts)
    {
        const int k = 4;
        var rnd = new Random(7);
        var centers = Enumerable.Range(0, k).Select(_ => pts[rnd.Next(pts.Count)]).ToArray();
        var assign = new int[pts.Count];

        for (int iter = 0; iter < 8; iter++)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                double best = double.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    double dr = pts[i].R - centers[c].R, dg = pts[i].G - centers[c].G, db = pts[i].B - centers[c].B;
                    double d = dr * dr + dg * dg + db * db;
                    if (d < best) { best = d; assign[i] = c; }
                }
            }
            for (int c = 0; c < k; c++)
            {
                double r = 0, g = 0, b = 0; int n = 0;
                for (int i = 0; i < pts.Count; i++)
                    if (assign[i] == c) { r += pts[i].R; g += pts[i].G; b += pts[i].B; n++; }
                if (n > 0) centers[c] = (r / n, g / n, b / n);
            }
        }

        // 打分：占比 × 饱和度，剔除极暗 / 极亮的簇
        var scored = Enumerable.Range(0, k).Select(c =>
        {
            int n = assign.Count(a => a == c);
            var (r, g, b) = centers[c];
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double sat = max <= 0 ? 0 : (max - min) / max;
            double lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
            double penalty = (lum < 0.12 || lum > 0.92) ? 0.15 : 1.0;
            return (C: c, Score: n * (0.3 + sat) * penalty);
        }).OrderByDescending(s => s.Score).ToList();

        Color ToColor((double R, double G, double B) v) =>
            Color.FromArgb(255, (byte)Math.Clamp(v.R, 0, 255), (byte)Math.Clamp(v.G, 0, 255), (byte)Math.Clamp(v.B, 0, 255));

        return scored.Select(s => ToColor(centers[s.C])).ToList();
    }

    /// <summary>取 4 个主色（按显著度排序，不足时循环补齐），用于沉浸模式的流动光晕。</summary>
    public static async Task<Color[]> GetPaletteAsync(string? coverPath)
    {
        var fb = new[] { Fallback.Primary, Fallback.Secondary, Color.FromArgb(255, 60, 70, 90), Color.FromArgb(255, 90, 60, 80) };
        if (string.IsNullOrEmpty(coverPath) || !File.Exists(coverPath)) return fb;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(coverPath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = 48, ScaledHeight = 48 },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var px = data.DetachPixelData();
            var pts = new List<(double R, double G, double B)>(48 * 48);
            for (int i = 0; i + 3 < px.Length; i += 4) pts.Add((px[i + 2], px[i + 1], px[i]));
            var pal = Cluster(pts);
            return Enumerable.Range(0, 4).Select(i => pal[i % pal.Count]).ToArray();
        }
        catch
        {
            return fb;
        }
    }

    /// <summary>把颜色压暗到适合做背景（保持色相）。</summary>
    public static Color Darken(Color c, double factor) =>
        Color.FromArgb(255, (byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
}
