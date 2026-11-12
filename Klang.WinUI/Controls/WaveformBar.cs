using System;
using System.Diagnostics;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Klang_WinUI.Services;

namespace Klang_WinUI.Controls;

/// <summary>
/// 波形进度条的绘制层：按这首歌的振幅画竖条，已播放部分用强调色，鼠标悬停位置之前的部分稍亮。
/// 只负责显示，拖动和点击仍由上面一层透明的 Slider 处理。
/// 换歌时旧波形先压平，新波形从左到右依次长出来。
/// </summary>
public sealed class WaveformBar : Grid
{
    private readonly CanvasControl _canvas = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private float[]? _data;                                  // 目标波形（0–1，WaveformService.Buckets 段）
    private readonly float[] _shown = new float[WaveformService.Buckets];   // 当前显示的高度（动画值）
    private readonly double[] _startAt = new double[WaveformService.Buckets];
    private double _lastFrame;
    private bool _looping;

    private double _progress;     // 0–1
    private double _hover = -1;   // 0–1；-1 = 没有悬停

    private const float BarWidth = 2f;
    private const float BarPitch = 3.5f;
    private const float MinBar = 2f;

    public WaveformBar()
    {
        _canvas.Draw += OnDraw;
        Children.Add(_canvas);
        IsHitTestVisible = false;
        ActualThemeChanged += (s, e) => _canvas.Invalidate();
        SizeChanged += (s, e) => _canvas.Invalidate();
        Unloaded += (s, e) => StopLoop();
    }

    /// <summary>换波形。null = 没有波形（云端未缓存、解码失败等），显示为一条细轨道。</summary>
    public void SetData(float[]? data)
    {
        _data = data;
        double now = _clock.Elapsed.TotalSeconds;
        int n = _shown.Length;
        for (int i = 0; i < n; i++)
            // 长出来时从左到右错开 0.35 秒；压平时同时进行
            _startAt[i] = data == null ? now : now + 0.35 * i / n;
        StartLoop();
    }

    public void SetProgress(double fraction)
    {
        fraction = double.IsNaN(fraction) ? 0 : Math.Clamp(fraction, 0, 1);
        if (Math.Abs(fraction - _progress) < 1e-5) return;
        _progress = fraction;
        if (!_looping) _canvas.Invalidate();
    }

    public void SetHover(double fraction)
    {
        _hover = fraction;
        if (!_looping) _canvas.Invalidate();
    }

    private void StartLoop()
    {
        if (_looping) return;
        _looping = true;
        _lastFrame = _clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopLoop()
    {
        if (!_looping) return;
        _looping = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.05, now - _lastFrame);
        _lastFrame = now;
        bool grow = _data != null;
        double k = 1 - Math.Exp(-(grow ? 9.0 : 16.0) * dt);
        bool moving = false;
        for (int i = 0; i < _shown.Length; i++)
        {
            if (now < _startAt[i]) { moving = true; continue; }
            float target = _data != null ? _data[i] : 0f;
            float d = target - _shown[i];
            if (Math.Abs(d) < 0.002f) { _shown[i] = target; continue; }
            _shown[i] += (float)(d * k);
            moving = true;
        }
        _canvas.Invalidate();
        if (!moving) StopLoop();
    }

    private static Color ColorOf(Brush? b, Color fallback) => b is SolidColorBrush s ? s.Color : fallback;

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        float w = (float)sender.ActualWidth, h = (float)sender.ActualHeight;
        if (w < 4 || h < 4) return;

        bool dark = ActualTheme == ElementTheme.Dark;
        var played = ColorOf(ThemeService.AccentText(this), dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        var rest = dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(60, 0, 0, 0);
        var ahead = dark ? Color.FromArgb(130, 255, 255, 255) : Color.FromArgb(115, 0, 0, 0);

        int bars = Math.Max(1, (int)((w + (BarPitch - BarWidth)) / BarPitch));
        float offset = (w - (bars * BarPitch - (BarPitch - BarWidth))) / 2f;
        int n = _shown.Length;
        float mid = h / 2f;

        void DrawBars(Color c)
        {
            for (int j = 0; j < bars; j++)
            {
                int b0 = j * n / bars, b1 = Math.Max(b0 + 1, (j + 1) * n / bars);
                float v = 0;
                for (int b = b0; b < b1 && b < n; b++) v = Math.Max(v, _shown[b]);
                float bh = MinBar + (h - MinBar) * v;
                float x = offset + j * BarPitch;
                ds.FillRoundedRectangle(x, mid - bh / 2f, BarWidth, bh, BarWidth / 2f, BarWidth / 2f, c);
            }
        }

        float px = (float)(_progress * w);
        DrawBars(rest);
        if (_hover >= 0)
        {
            float hx = (float)(_hover * w);
            if (hx > px)
                using (ds.CreateLayer(1f, new Windows.Foundation.Rect(px, 0, hx - px, h))) DrawBars(ahead);
        }
        if (px > 0)
            using (ds.CreateLayer(1f, new Windows.Foundation.Rect(0, 0, px, h))) DrawBars(played);
    }
}
