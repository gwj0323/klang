using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Klang_WinUI.Services;

namespace Klang_WinUI.Controls;

/// <summary>
/// 列表里“正在播放”那一行盖在封面缩略图上的小频谱：4 根竖条跟着实际播放的频谱跳动，暂停时停在原处。
/// 只有可见（即当前曲目所在行）时才处理频谱数据。
/// </summary>
public sealed class NowPlayingBars : Grid
{
    private const int Bars = 4;
    private const double BarW = 3, MaxH = 16, Gap = 2.5;
    private readonly ScaleTransform[] _scale = new ScaleTransform[Bars];
    private readonly float[] _value = { 0.35f, 0.6f, 0.45f, 0.3f };
    private bool _subscribed;

    public NowPlayingBars()
    {
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x8C, 0, 0, 0));
        IsHitTestVisible = false;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Gap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Height = MaxH
        };
        for (int i = 0; i < Bars; i++)
        {
            _scale[i] = new ScaleTransform { ScaleY = _value[i], CenterY = MaxH };
            row.Children.Add(new Rectangle
            {
                Width = BarW,
                Height = MaxH,
                RadiusX = BarW / 2,
                RadiusY = BarW / 2,
                Fill = new SolidColorBrush(Colors.White),
                RenderTransform = _scale[i]
            });
        }
        Children.Add(row);

        Loaded += (s, e) => Subscribe();
        Unloaded += (s, e) => Unsubscribe();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        AudioEngineService.Instance.SpectrumChanged += OnSpectrum;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        AudioEngineService.Instance.SpectrumChanged -= OnSpectrum;
    }

    private void OnSpectrum(float[] bands)
    {
        if (Visibility != Visibility.Visible || bands == null || bands.Length < Bars) return;
        if (!DispatcherQueue.HasThreadAccess) return;

        // 把频谱的中低频部分（人耳最敏感、起伏最明显）分成 4 段
        int usable = Math.Max(Bars, bands.Length * 2 / 3);
        for (int i = 0; i < Bars; i++)
        {
            int a = i * usable / Bars, b = Math.Max(a + 1, (i + 1) * usable / Bars);
            float sum = 0;
            for (int k = a; k < b && k < bands.Length; k++) sum += bands[k];
            float target = Math.Clamp(0.18f + 0.82f * (sum / (b - a)), 0.18f, 1f);
            // 上升快、回落慢，看起来更有弹性
            _value[i] += (target - _value[i]) * (target > _value[i] ? 0.6f : 0.25f);
            _scale[i].ScaleY = _value[i];
        }
    }
}
