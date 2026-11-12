using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Klang_WinUI.Services;

namespace Klang_WinUI.Dialogs;

/// <summary>“信号路径”面板：从文件到声卡的每一步，以及是不是比特完美，写得清清楚楚。</summary>
public static class SignalPathPanel
{
    public static FrameworkElement Build(SignalPathInfo info, FrameworkElement scope)
    {
        var secondary = ThemeService.Brush(scope, "TextFillColorSecondaryBrush");
        var tertiary = ThemeService.Brush(scope, "TextFillColorTertiaryBrush");
        var panel = new StackPanel { Width = 400, Spacing = 2 };

        panel.Children.Add(new TextBlock { Text = L.T("信号路径"), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });

        if (!info.Playing)
        {
            panel.Children.Add(new TextBlock { Text = L.T("当前未在播放。"), TextWrapping = TextWrapping.Wrap, Foreground = secondary });
            return panel;
        }

        void Step(string title, string value, bool last = false)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = tertiary, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            var v = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(v, 1);
            row.Children.Add(v);
            panel.Children.Add(row);

            if (!last)
                panel.Children.Add(new TextBlock { Text = "↓", FontSize = 12, Foreground = tertiary, Margin = new Thickness(76, 0, 0, 0) });
        }

        Step(L.T("源文件"), info.SourceFormat);
        Step(L.T("解码"), info.DecodeStep);
        Step(L.T("变速"), info.RateStep);
        Step(L.T("音效"), info.DspStep);
        Step(L.T("音量"), info.VolumeStep);
        Step(L.T("输出"), info.OutputStep, last: true);

        // 结论
        var good = Color.FromArgb(255, 70, 190, 110);
        var warn = Color.FromArgb(255, 232, 140, 30);
        var box = new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(28, info.IsBitPerfect ? good.R : warn.R, info.IsBitPerfect ? good.G : warn.G, info.IsBitPerfect ? good.B : warn.B))
        };
        var inner = new StackPanel { Spacing = 4 };
        inner.Children.Add(new TextBlock
        {
            Text = info.IsBitPerfect ? "✓ " + L.T("比特完美：音频数据未经修改，直接输出至声卡") : "⚠ " + L.T("当前不是比特完美"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(info.IsBitPerfect ? good : warn),
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var r in info.Reasons)
            inner.Children.Add(new TextBlock { Text = "• " + r, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = secondary });
        if (info.IsBitPerfect)
            inner.Children.Add(new TextBlock { Text = L.T("（每首歌开头约 25 毫秒的防爆音淡入除外）"), FontSize = 11, Foreground = tertiary });
        else
            inner.Children.Add(new TextBlock { Text = L.T("比特完美的条件：独占模式、绕过全部音效、音量 100%（设置 → 播放与音频）。"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = tertiary, Margin = new Thickness(0, 4, 0, 0) });
        box.Child = inner;
        panel.Children.Add(box);
        return panel;
    }
}
