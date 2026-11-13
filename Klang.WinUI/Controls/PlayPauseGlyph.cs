using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Klang_WinUI.Controls;

/// <summary>
/// 播放 / 暂停图标：两种状态之间做形变过渡。
/// 把播放三角形从中间竖着切成左右两块，每块 4 个顶点，和暂停的两根竖条一一对应，逐帧插值。
/// </summary>
public sealed class PlayPauseGlyph : Canvas
{
    // 16 × 16 的坐标系；播放三角形整体向右偏一点，视觉上才居中
    private static readonly Point[] PlayL = { new(3.5, 1.5), new(8.9, 4.75), new(8.9, 11.25), new(3.5, 14.5) };
    private static readonly Point[] PlayR = { new(8.5, 4.5), new(14.2, 8), new(14.2, 8), new(8.5, 11.5) };
    private static readonly Point[] PauseL = { new(3, 2), new(6.6, 2), new(6.6, 14), new(3, 14) };
    private static readonly Point[] PauseR = { new(9.4, 2), new(13, 2), new(13, 14), new(9.4, 14) };

    private readonly Polygon _left = new(), _right = new();
    private readonly Stopwatch _clock = new();
    private double _from, _to, _value;   // 0 = 播放三角形，1 = 暂停竖条
    private bool _hooked;
    private const double DurationMs = 220;

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(PlayPauseGlyph),
        new PropertyMetadata(null, (d, e) => { var g = (PlayPauseGlyph)d; g._left.Fill = g._right.Fill = (Brush)e.NewValue; }));

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public PlayPauseGlyph()
    {
        Width = 16; Height = 16;
        Children.Add(_left);
        Children.Add(_right);
        Apply(0);
        Unloaded += (s, e) => Unhook();
    }

    private bool _isPlaying;

    /// <summary>true = 正在播放（显示暂停图标）。</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            _from = _value; _to = value ? 1 : 0;
            if (!IsLoaded) { _value = _to; Apply(_value); return; }
            _clock.Restart();
            if (!_hooked) { _hooked = true; CompositionTarget.Rendering += OnRendering; }
        }
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnRendering;
        _value = _to; Apply(_value);
    }

    private void OnRendering(object? sender, object e)
    {
        double t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / DurationMs, 0, 1);
        double eased = t * t * (3 - 2 * t);
        _value = _from + (_to - _from) * eased;
        Apply(_value);
        if (t >= 1) Unhook();
    }

    private void Apply(double v)
    {
        _left.Points = Lerp(PlayL, PauseL, v);
        _right.Points = Lerp(PlayR, PauseR, v);
    }

    private static PointCollection Lerp(Point[] a, Point[] b, double v)
    {
        var pc = new PointCollection();
        for (int i = 0; i < a.Length; i++)
            pc.Add(new Point(a[i].X + (b[i].X - a[i].X) * v, a[i].Y + (b[i].Y - a[i].Y) * v));
        return pc;
    }
}
