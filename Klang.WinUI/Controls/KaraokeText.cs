using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Klang_WinUI.Models;

namespace Klang_WinUI.Controls;

/// <summary>
/// 一行歌词的文本。有逐字时间轴时按字拆成 Run，已唱到的字用强调色高亮（卡拉 OK 效果）；
/// 没有逐字信息时就是普通文本。字号 / 字重 / 前景色沿用控件自身的继承属性。
/// </summary>
public sealed class KaraokeText : UserControl
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(LyricLine), typeof(KaraokeText),
        new PropertyMetadata(null, (d, e) => ((KaraokeText)d).OnLineChanged((LyricLine?)e.OldValue, (LyricLine?)e.NewValue)));

    public LyricLine? Line
    {
        get => (LyricLine?)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    private readonly TextBlock _text = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private Run[]? _runs;
    private int _applied = -1;

    /// <summary>是否自动换行；桌面歌词等单行场景设为 false。</summary>
    public bool WrapText
    {
        get => _text.TextWrapping == TextWrapping.Wrap;
        set => _text.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
    }

    public KaraokeText()
    {
        Content = _text;
        IsTabStop = false;
    }

    private void OnLineChanged(LyricLine? oldLine, LyricLine? newLine)
    {
        if (oldLine != null) oldLine.PropertyChanged -= OnLinePropertyChanged;
        if (newLine != null) newLine.PropertyChanged += OnLinePropertyChanged;
        Rebuild();
    }

    private void OnLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricLine.PlayedWords)) ApplyProgress();
        else if (e.PropertyName == nameof(LyricLine.IsActive)) { _applied = -1; ApplyProgress(); }
        else if (e.PropertyName == nameof(LyricLine.Text)) Rebuild();
    }

    private void Rebuild()
    {
        _text.Inlines.Clear();
        _runs = null;
        _applied = -1;
        var line = Line;
        if (line == null) return;

        if (line.Words is { Count: > 0 } words)
        {
            _runs = new Run[words.Count];
            for (int i = 0; i < words.Count; i++)
            {
                _runs[i] = new Run { Text = words[i].Text };
                _text.Inlines.Add(_runs[i]);
            }
            ApplyProgress();
        }
        else
        {
            _text.Inlines.Add(new Run { Text = line.Text });
        }
    }

    private void ApplyProgress()
    {
        if (_runs == null || Line == null) return;
        int played = Math.Clamp(Line.PlayedWords, 0, _runs.Length);
        if (played == _applied) return;
        _applied = played;

        // 不用彩色：已唱的字是正文色（深色 = 纯白，浅色 = 近黑），未唱的字是同一颜色的淡化版（Apple Music 的做法）
        bool dark = ActualTheme == ElementTheme.Dark;
        byte c = dark ? (byte)255 : (byte)20;
        Brush sung = new SolidColorBrush(Windows.UI.Color.FromArgb(255, c, c, c));
        Brush unsung = new SolidColorBrush(Windows.UI.Color.FromArgb(dark ? (byte)110 : (byte)95, c, c, c));
        // 只有正在唱的这一行才分“已唱 / 未唱”；其余行整行统一，由行自身的透明度去淡化
        bool active = Line.IsActive;
        for (int i = 0; i < _runs.Length; i++)
        {
            if (!active) _runs[i].ClearValue(TextElement.ForegroundProperty);
            else _runs[i].Foreground = i < played ? sung : unsung;
        }
    }
}
