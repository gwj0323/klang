using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Klang_WinUI.Models;

namespace Klang_WinUI.Controls;

public sealed partial class VinylCoverCard : UserControl
{
    private const int NUM_BARS = 24;

    private readonly DispatcherTimer _turntableTimer = new();
    private readonly DispatcherTimer _slideAnimTimer = new();
    private readonly DispatcherTimer _fadeAnimTimer = new();
    private readonly DispatcherTimer _spectrumPhysicsTimer = new();

    private double _targetSlideX = 0.0;
    private double _currentSlideX = 0.0;
    private double _animProg = 1.0;
    private bool _isPlaying = false;

    private readonly Microsoft.UI.Xaml.Shapes.Rectangle[] _bars = new Microsoft.UI.Xaml.Shapes.Rectangle[NUM_BARS];
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse[] _dots = new Microsoft.UI.Xaml.Shapes.Ellipse[NUM_BARS];
    private readonly float[] _targets = new float[NUM_BARS];
    private readonly float[] _values = new float[NUM_BARS];
    private readonly float[] _peaks = new float[NUM_BARS];
    private readonly float[] _peakVels = new float[NUM_BARS];
    private DateTime _lastSpectrumTick = DateTime.UtcNow;

    public VinylCoverCard()
    {
        InitializeComponent();

        var accent = Application.Current.Resources["AccentFillColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush;
        for (int i = 0; i < NUM_BARS; i++)
        {
            _bars[i] = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = BAR_W, Height = 3, RadiusX = 1.7, RadiusY = 1.7, Fill = accent, Opacity = 0.35 };
            _dots[i] = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 2.8, Height = 2.8, Fill = accent, Opacity = 0, IsHitTestVisible = false };
            SpectrumCanvas.Children.Add(_bars[i]);
            SpectrumCanvas.Children.Add(_dots[i]);
        }

        // 初始化频谱初始底衬状态
        for (int i = 0; i < NUM_BARS; i++)
        {
            _values[i] = 0.04f;
            _peaks[i] = 0.04f;
        }

        // 1. 黑胶唱片匀速自转计时器 (基于 (120, 120) 圆心精确自转，绝不偏心甩动)
        _turntableTimer.Interval = TimeSpan.FromMilliseconds(33);
        _turntableTimer.Tick += (s, e) =>
        {
            if (_isPlaying)
            {
                VinylRotateTransform.Angle = (VinylRotateTransform.Angle + 1.0) % 360.0;
            }
        };

        // 2. 黑胶唱片滑出/收回物理阻尼与透明度联动计时器
        _slideAnimTimer.Interval = TimeSpan.FromMilliseconds(16);
        _slideAnimTimer.Tick += (s, e) =>
        {
            double diff = _targetSlideX - _currentSlideX;
            if (Math.Abs(diff) > 0.3)
            {
                _currentSlideX += diff * 0.18;
                VinylSlideTransform.X = _currentSlideX;
                VinylDisc.Opacity = Math.Clamp(_currentSlideX / 28.0, 0.0, 1.0);
            }
            else
            {
                _currentSlideX = _targetSlideX;
                VinylSlideTransform.X = _currentSlideX;
                VinylDisc.Opacity = _targetSlideX > 0 ? 1.0 : 0.0;

                if (_targetSlideX == 0)
                {
                    VinylDisc.Visibility = Visibility.Collapsed;
                }

                _slideAnimTimer.Stop();
            }
        };

        // 3. 切歌缩放淡入平滑过渡计时器
        _fadeAnimTimer.Interval = TimeSpan.FromMilliseconds(20);
        _fadeAnimTimer.Tick += (s, e) =>
        {
            _animProg = Math.Min(1.0, _animProg + 0.12);

            double scale = 0.94 + 0.06 * _animProg;
            CoverScaleTransform.ScaleX = scale;
            CoverScaleTransform.ScaleY = scale;
            JacketCard.Opacity = 0.3 + 0.7 * _animProg;

            MetaTranslateTransform.Y = 8.0 * (1.0 - _animProg);
            MetaInfoPanel.Opacity = 0.2 + 0.8 * _animProg;

            if (_animProg >= 1.0)
            {
                _fadeAnimTimer.Stop();
            }
        };

        // 4. 20 频段北欧声学物理律动引擎 (弹簧上升 + 重力回落 + 能量自适应透明度)
        _spectrumPhysicsTimer.Interval = TimeSpan.FromMilliseconds(25);
        _spectrumPhysicsTimer.Tick += (s, e) => StepSpectrumPhysics();
    }

    private const double BAR_W = 3.4;
    private const double CANVAS_H = 32.0;

    private void SpectrumCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        for (int i = 0; i < NUM_BARS; i++) LayoutBar(i, _values[i], _peaks[i]);
    }

    /// <summary>按 Qt 版的几何绘制一根柱与它的峰值点：柱宽 3.4，间距 2.8~6.2，透明度随能量变化。</summary>
    private void LayoutBar(int i, float val, float peak)
    {
        double w = SpectrumCanvas.ActualWidth;
        if (w <= 0) return;
        // 柱子铺满与封面等宽的区域，左右边缘与封面、文字对齐
        double barW = Math.Clamp(w / NUM_BARS * 0.42, BAR_W, 5.0);
        double gap = (w - NUM_BARS * barW) / (NUM_BARS - 1);
        double x = i * (barW + gap);
        double maxH = CANVAS_H - 6.0, baseY = CANVAS_H - 2.0;

        double bh = Math.Max(2.8, val * maxH);
        Canvas.SetLeft(_bars[i], x);
        Canvas.SetTop(_bars[i], baseY - bh);
        _bars[i].Height = bh;
        _bars[i].Width = barW;
        _bars[i].Opacity = 0.28 + 0.64 * val;

        if (peak > val + 0.04f)
        {
            Canvas.SetLeft(_dots[i], x + barW / 2.0 - 1.4);
            Canvas.SetTop(_dots[i], Math.Max(1.5, baseY - peak * maxH - 3.2) - 1.4);
            _dots[i].Opacity = 0.45;
        }
        else
        {
            _dots[i].Opacity = 0;
        }
    }

    private void StepSpectrumPhysics()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastSpectrumTick).TotalSeconds, 0.005, 0.05);
        _lastSpectrumTick = now;

        double riseAlpha = 1.0 - Math.Exp(-22.0 * dt);
        double fallAlpha = 1.0 - Math.Exp(-7.5 * dt);
        bool hasActiveEnergy = false;

        for (int i = 0; i < NUM_BARS; i++)
        {
            float tgt = _isPlaying ? _targets[i] : 0.04f;
            float cur = _values[i];

            if (tgt > cur)
                cur += (float)((tgt - cur) * riseAlpha);
            else
                cur += (float)((tgt - cur) * fallAlpha);

            cur = Math.Clamp(cur, 0.04f, 1.0f);
            _values[i] = cur;

            // 峰值保持与重力回落计算
            if (cur >= _peaks[i])
            {
                _peaks[i] = cur;
                _peakVels[i] = 0f;
            }
            else
            {
                _peakVels[i] += (float)(1.5 * dt);
                _peaks[i] = Math.Max(cur, _peaks[i] - (float)(_peakVels[i] * dt));
            }

            if (cur > 0.06f) hasActiveEnergy = true;

            LayoutBar(i, cur, _peaks[i]);
        }

        if (!hasActiveEnergy && !_isPlaying)
        {
            _spectrumPhysicsTimer.Stop();
        }
    }

    public void SetPlaying(bool isPlaying)
    {
        _isPlaying = isPlaying;
        _targetSlideX = isPlaying ? 48.0 : 0.0;

        if (isPlaying)
        {
            VinylDisc.Visibility = Visibility.Visible;
            _turntableTimer.Start();
            _lastSpectrumTick = DateTime.UtcNow;
            _spectrumPhysicsTimer.Start();
        }
        else
        {
            _turntableTimer.Stop();
            for (int i = 0; i < NUM_BARS; i++) _targets[i] = 0.04f;
        }

        _slideAnimTimer.Start();
    }

    public void UpdateTrack(TrackItem? track)
    {
        _animProg = 0.0;
        _fadeAnimTimer.Start();

        if (track == null)
        {
            TitleText.Text = "暂无播放曲目";
            ArtistText.Text = "点击左侧栏按钮导入本地音乐";
            AlbumText.Text = "";
            QualityBadgeBorder.Visibility = Visibility.Collapsed;
            CoverImage.Visibility = Visibility.Collapsed;
            FallbackPanel.Visibility = Visibility.Visible;
            return;
        }

        TitleText.Text = string.IsNullOrWhiteSpace(track.Title) ? "未命名曲目" : track.Title;
        ArtistText.Text = string.IsNullOrWhiteSpace(track.Artist) ? "未知艺术家" : track.Artist;
        AlbumText.Text = string.IsNullOrWhiteSpace(track.Album) ? "" : track.Album;

        if (!string.IsNullOrEmpty(track.FormatBadge))
        {
            QualityBadgeText.Text = track.FormatBadge;
            QualityBadgeBorder.Visibility = Visibility.Visible;
        }
        else
        {
            QualityBadgeBorder.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrEmpty(track.CoverPath) && File.Exists(track.CoverPath))
        {
            try
            {
                CoverImage.Source = new BitmapImage(new Uri(track.CoverPath));
                CoverImage.Visibility = Visibility.Visible;
                FallbackPanel.Visibility = Visibility.Collapsed;
            }
            catch
            {
                CoverImage.Visibility = Visibility.Collapsed;
                FallbackPanel.Visibility = Visibility.Visible;
            }
        }
        else
        {
            CoverImage.Visibility = Visibility.Collapsed;
            FallbackPanel.Visibility = Visibility.Visible;
        }
    }

    public void UpdateSpectrum(float[] bands)
    {
        if (bands == null || bands.Length == 0) return;

        // 将输入的 FFT 声学能量插值映射至 20 根物理律动柱
        for (int i = 0; i < NUM_BARS; i++)
        {
            double srcIdx = i * (bands.Length - 1) / (double)(NUM_BARS - 1);
            int idx0 = (int)srcIdx;
            int idx1 = Math.Min(bands.Length - 1, idx0 + 1);
            double frac = srcIdx - idx0;
            float val = (float)(bands[idx0] * (1.0 - frac) + bands[idx1] * frac);
            _targets[i] = Math.Clamp(val, 0.0f, 1.0f);
        }

        if (!_spectrumPhysicsTimer.IsEnabled)
        {
            _lastSpectrumTick = DateTime.UtcNow;
            _spectrumPhysicsTimer.Start();
        }
    }
}
