using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Klang_WinUI.Controls;

/// <summary>
/// 列表加载中的骨架屏：若干行灰色占位块（封面 + 两行文字），上面有一道微光从左到右循环扫过。
/// 用来代替转圈，让人看得出“这里将会是一个列表”。
/// </summary>
public sealed class SkeletonList : Grid
{
    private const int Rows = 8;
    private const float ShineWidth = 180f;
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly Rectangle _shine = new() { Width = ShineWidth, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };

    public SkeletonList()
    {
        IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_rows);
        Children.Add(_shine);
        ElementCompositionPreview.SetIsTranslationEnabled(_shine, true);

        Build();
        Loaded += (s, e) => { ApplyColors(); Restart(); };
        ActualThemeChanged += (s, e) => ApplyColors();
        SizeChanged += (s, e) =>
        {
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            Restart();
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (s, e) => Restart());
    }

    private void Build()
    {
        // 每行的文字条宽度略有不同，看起来不像复制粘贴
        double[] titleW = { 0.62, 0.48, 0.70, 0.55, 0.66, 0.44, 0.58, 0.50 };
        double[] subW = { 0.34, 0.40, 0.28, 0.36, 0.30, 0.42, 0.26, 0.38 };
        for (int i = 0; i < Rows; i++)
        {
            var row = new Grid { Height = 56, Padding = new Thickness(8, 0, 8, 0), ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new Rectangle { Width = 40, Height = 40, RadiusX = 6, RadiusY = 6, VerticalAlignment = VerticalAlignment.Center });

            var lines = new Grid { VerticalAlignment = VerticalAlignment.Center, RowSpacing = 8 };
            lines.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            lines.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            lines.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(titleW[i], GridUnitType.Star) });
            lines.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - titleW[i], GridUnitType.Star) });
            var title = new Rectangle { Height = 11, RadiusX = 4, RadiusY = 4 };
            var sub = new Rectangle { Height = 9, RadiusX = 4, RadiusY = 4, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetRow(sub, 1);
            Grid.SetColumnSpan(sub, 2);
            lines.Children.Add(title);
            lines.Children.Add(sub);
            double sw = subW[i];
            lines.SizeChanged += (s, e) => sub.Width = Math.Max(20, e.NewSize.Width * sw);
            Grid.SetColumn(lines, 1);
            row.Children.Add(lines);

            // 越往下越淡，暗示“后面还有”
            row.Opacity = 1.0 - i * 0.1;
            _rows.Children.Add(row);
        }
    }

    private void ApplyColors()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        var block = new SolidColorBrush(dark ? Color.FromArgb(0x1C, 255, 255, 255) : Color.FromArgb(0x14, 0, 0, 0));
        foreach (var child in _rows.Children)
        {
            if (child is not Grid row) continue;
            foreach (var c in row.Children)
            {
                if (c is Rectangle r) r.Fill = block;
                else if (c is Grid lines) foreach (var l in lines.Children) if (l is Rectangle lr) lr.Fill = block;
            }
        }

        var hi = dark ? Color.FromArgb(0x1E, 255, 255, 255) : Color.FromArgb(0x8C, 255, 255, 255);
        var clear = Color.FromArgb(0, 255, 255, 255);
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.5), EndPoint = new Windows.Foundation.Point(1, 0.5) };
        brush.GradientStops.Add(new GradientStop { Color = clear, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = hi, Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = clear, Offset = 1 });
        _shine.Fill = brush;
    }

    /// <summary>微光循环扫过（合成动画，不占界面线程）；不可见时停掉。</summary>
    private void Restart()
    {
        var v = ElementCompositionPreview.GetElementVisual(_shine);
        v.StopAnimation("Translation.X");
        if (Visibility != Visibility.Visible || !IsLoaded || ActualWidth <= 0) return;

        var c = v.Compositor;
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, -ShineWidth);
        a.InsertKeyFrame(0.7f, (float)ActualWidth, c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f)));
        a.InsertKeyFrame(1f, (float)ActualWidth);
        a.Duration = TimeSpan.FromMilliseconds(1500);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("Translation.X", a);
    }
}
