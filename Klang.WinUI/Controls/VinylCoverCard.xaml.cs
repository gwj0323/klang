using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Klang_WinUI.Models;

namespace Klang_WinUI.Controls;

public sealed partial class VinylCoverCard : UserControl
{
    private const int NUM_BARS = 20;

    private readonly DispatcherTimer _turntableTimer = new();
    private readonly DispatcherTimer _slideAnimTimer = new();
    private readonly DispatcherTimer _fadeAnimTimer = new();
    private readonly DispatcherTimer _spectrumPhysicsTimer = new();

    private double _targetSlideX = 0.0;
    private double _currentSlideX = 0.0;
    private double _animProg = 1.0;
    private bool _isPlaying = false;

    private readonly Border[] _bars = new Border[NUM_BARS];
    private readonly float[] _targets = new float[NUM_BARS];
    private readonly float[] _values = new float[NUM_BARS];
    private readonly float[] _peaks = new float[NUM_BARS];
    private readonly float[] _peakVels = new float[NUM_BARS];
    private DateTime _lastSpectrumTick = DateTime.UtcNow;

    public VinylCoverCard()
    {
        InitializeComponent();

        _bars[0] = Bar0; _bars[1] = Bar1; _bars[2] = Bar2; _bars[3] = Bar3;
        _bars[4] = Bar4; _bars[5] = Bar5; _bars[6] = Bar6; _bars[7] = Bar7;
        _bars[8] = Bar8; _bars[9] = Bar9; _bars[10] = Bar10; _bars[11] = Bar11;
        _bars[12] = Bar12; _bars[13] = Bar13; _bars[14] = Bar14; _bars[15] = Bar15;
        _bars[16] = Bar16; _bars[17] = Bar17; _bars[18] = Bar18; _bars[19] = Bar19;

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

            // 映射到 3px ~ 26px 的优雅跳动柱高度与能量半透明度
            const double maxH = 26.0;
            double h = Math.Max(3.0, cur * maxH);
            _bars[i].Height = h;
            _bars[i].Opacity = 0.28 + 0.68 * cur;
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
