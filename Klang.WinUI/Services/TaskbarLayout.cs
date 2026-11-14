using System;
using System.Collections.Generic;
using System.Linq;

namespace Klang_WinUI.Services;

// 所有矩形都是物理像素；设计常量只在布局边界处乘 DPI。
internal readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;
    public bool Intersects(ScreenRect other) => Left < other.Right && Right > other.Left
        && Top < other.Bottom && Bottom > other.Top;
    public double DistanceTo(ScreenRect other)
    {
        double x = Math.Max(0, Math.Max(other.Left - Right, Left - other.Right));
        double y = Math.Max(0, Math.Max(other.Top - Bottom, Top - other.Bottom));
        return Math.Sqrt(x * x + y * y);
    }
}

internal enum TaskbarVariant { Normal, Compact }
internal enum DockSide { Left, Right }
internal enum SnapStage { None, Preview, Confirm }
internal sealed record DockTarget(ScreenRect Bounds, TaskbarVariant Variant, DockSide Side);
internal sealed record SnapResult(SnapStage Stage, DockTarget? Target);

internal static class TaskbarLayout
{
    public const int NormalWidth = 388, CompactWidth = 108, Height = 40;
    public static double ValidScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, 1, 4) : 1;

    public static bool CanDock(ScreenRect bar, ScreenRect monitor, double scale) =>
        bar.Width > bar.Height && Math.Min(bar.Bottom, monitor.Bottom) - Math.Max(bar.Top, monitor.Top) >= 28 * ValidScale(scale);

    /// <summary>从真实占用矩形求补集；不靠固定“通知区宽度”猜空白。</summary>
    public static IReadOnlyList<ScreenRect> FindBlanks(ScreenRect bar, IReadOnlyList<ScreenRect> occupied, double scale)
    {
        int margin = (int)Math.Ceiling(12 * ValidScale(scale));
        int gap = (int)Math.Ceiling(4 * ValidScale(scale));
        int start = bar.Left + margin, end = bar.Right - margin;
        var result = new List<ScreenRect>();
        foreach (var item in occupied.Where(r => r.Intersects(bar)).OrderBy(r => r.Left))
        {
            int left = Math.Max(start, item.Left - gap), right = Math.Min(end, item.Right + gap);
            if (right <= start || left >= end) continue;
            if (left > start) result.Add(new(start, bar.Top, left, bar.Bottom));
            start = Math.Max(start, right);
        }
        if (start < end) result.Add(new(start, bar.Top, end, bar.Bottom));
        return result;
    }

    public static DockTarget? SelectTarget(ScreenRect bar, ScreenRect monitor,
        IReadOnlyList<ScreenRect> blanks, double scale, DockSide preferred)
    {
        scale = ValidScale(scale);
        if (!CanDock(bar, monitor, scale)) return null;
        int height = Math.Min((int)Math.Round(Height * scale),
            Math.Min(bar.Bottom, monitor.Bottom) - Math.Max(bar.Top, monitor.Top) - (int)(4 * scale));
        var targets = new List<(DockTarget Target, int Space)>();
        foreach (var blank in blanks)
        {
            int left = Math.Max(Math.Max(bar.Left, monitor.Left), blank.Left);
            int right = Math.Min(Math.Min(bar.Right, monitor.Right), blank.Right);
            int normal = (int)Math.Ceiling(NormalWidth * scale), compact = (int)Math.Ceiling(CompactWidth * scale);
            if (right - left < compact) continue;
            var variant = right - left >= normal ? TaskbarVariant.Normal : TaskbarVariant.Compact;
            int width = variant == TaskbarVariant.Normal ? normal : compact;
            var side = left + (right - left) / 2 < bar.CenterX ? DockSide.Left : DockSide.Right;
            int x = side == DockSide.Left ? left : right - width;
            int y = Math.Max(bar.Top, monitor.Top) +
                (Math.Min(bar.Bottom, monitor.Bottom) - Math.Max(bar.Top, monitor.Top) - height) / 2;
            targets.Add((new(new(x, y, x + width, y + height), variant, side), right - left));
        }
        // 先保证可读的 normal；偏好侧没有空间时可以退到另一侧。
        return targets.OrderBy(t => t.Target.Variant).ThenBy(t => t.Target.Side != preferred)
            .ThenByDescending(t => t.Space).Select(t => t.Target).FirstOrDefault();
    }

}

/// <summary>拖动矩形与候选停靠矩形之间的纯逻辑，不访问窗口或播放器。</summary>
internal static class TaskbarDockController
{
    public static SnapResult Resolve(ScreenRect dragged, ScreenRect bar, DockTarget? target, double scale)
    {
        if (target == null) return new(SnapStage.None, null);
        bool confirm = dragged.Intersects(bar) || dragged.DistanceTo(target.Bounds) <= 10 * TaskbarLayout.ValidScale(scale);
        return new(confirm ? SnapStage.Confirm : SnapStage.Preview, target);
    }
}
