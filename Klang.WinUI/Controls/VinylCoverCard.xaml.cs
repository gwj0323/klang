using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Klang_WinUI.Models;

namespace Klang_WinUI.Controls;

public sealed partial class VinylCoverCard : UserControl
{
    private readonly DispatcherTimer _turntableTimer = new();
    private readonly DispatcherTimer _slideAnimTimer = new();
    private readonly DispatcherTimer _fadeAnimTimer = new();

    private double _targetSlideX = 0.0;
    private double _currentSlideX = 0.0;

    private double _animProg = 1.0;
    private bool _isPlaying = false;

    private readonly Border[] _bars = new Border[16];

    public VinylCoverCard()
    {
        InitializeComponent();

        _bars[0] = Bar0; _bars[1] = Bar1; _bars[2] = Bar2; _bars[3] = Bar3;
        _bars[4] = Bar4; _bars[5] = Bar5; _bars[6] = Bar6; _bars[7] = Bar7;
        _bars[8] = Bar8; _bars[9] = Bar9; _bars[10] = Bar10; _bars[11] = Bar11;
        _bars[12] = Bar12; _bars[13] = Bar13; _bars[14] = Bar14; _bars[15] = Bar15;

        // 1. 黑胶唱片匀速物理旋转计时器 (~30fps，基于 0.5,0.5 轴心)
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
                _currentSlideX += diff * 0.18; // 阻尼物理逼近
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
    }

    public void SetPlaying(bool isPlaying)
    {
        _isPlaying = isPlaying;
        _targetSlideX = isPlaying ? 48.0 : 0.0;

        if (isPlaying)
        {
            VinylDisc.Visibility = Visibility.Visible;
            _turntableTimer.Start();
        }
        else
        {
            _turntableTimer.Stop();
        }

        _slideAnimTimer.Start();
    }

    public void UpdateTrack(TrackItem? track)
    {
        // 触发切歌入场缩放动画
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
        if (bands == null || bands.Length < 16) return;

        for (int i = 0; i < 16; i++)
        {
            float energy = Math.Clamp(bands[i], 0.0f, 1.0f);
            double h = 3.0 + energy * 18.0;
            _bars[i].Height = h;
        }
    }
}
