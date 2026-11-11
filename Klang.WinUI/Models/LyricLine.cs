using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Windows.UI.Text;

namespace Klang_WinUI.Models;

/// <summary>一个逐字片段（起止秒）。</summary>
public sealed class LyricWord
{
    public LyricWord(double start, double end, string text)
    {
        Start = start;
        End = end;
        Text = text;
    }

    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; }
}

public class LyricLine : INotifyPropertyChanged
{
    public int Index { get; set; }
    public double Time { get; set; }
    public double EndTime { get; set; }
    public string Text { get; set; } = string.Empty;
    /// <summary>翻译行。</summary>
    public string Sub { get; set; } = string.Empty;
    /// <summary>罗马音 / 注音行。</summary>
    public string Roma { get; set; } = string.Empty;

    /// <summary>逐字时间轴（增强 LRC / yrc）；没有则为 null。</summary>
    public List<LyricWord>? Words { get; set; }

    private string _subMode = "trans";
    /// <summary>副行显示模式：trans 翻译 | roma 罗马音 | both 两者 | off 关闭。</summary>
    public string SubMode
    {
        get => _subMode;
        set
        {
            if (_subMode == value) return;
            _subMode = value;
            OnPropertyChanged(nameof(DisplaySub));
            OnPropertyChanged(nameof(SubVisibility));
        }
    }

    public string DisplaySub => SubMode switch
    {
        "off" => "",
        "roma" => Roma,
        "both" => string.Join("\n", new[] { Roma, Sub }.Where(s => !string.IsNullOrWhiteSpace(s))),
        _ => Sub
    };

    public bool HasSub => !string.IsNullOrWhiteSpace(DisplaySub);
    public Visibility SubVisibility => HasSub ? Visibility.Visible : Visibility.Collapsed;

    private int _playedWords;
    /// <summary>已唱完/正在唱的字数（逐字高亮用）。</summary>
    public int PlayedWords
    {
        get => _playedWords;
        set
        {
            if (_playedWords != value)
            {
                _playedWords = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FontWeight));
            }
        }
    }

    public FontWeight FontWeight => IsActive ? FontWeights.Bold : FontWeights.Normal;

    private double _scaleX = 0.96;
    public double ScaleX
    {
        get => _scaleX;
        set
        {
            if (Math.Abs(_scaleX - value) > 0.001)
            {
                _scaleX = value;
                OnPropertyChanged();
            }
        }
    }

    private double _scaleY = 0.96;
    public double ScaleY
    {
        get => _scaleY;
        set
        {
            if (Math.Abs(_scaleY - value) > 0.001)
            {
                _scaleY = value;
                OnPropertyChanged();
            }
        }
    }

    private double _lineOpacity = 0.15;
    public double LineOpacity
    {
        get => _lineOpacity;
        set
        {
            if (Math.Abs(_lineOpacity - value) > 0.003)
            {
                _lineOpacity = value;
                OnPropertyChanged();
            }
        }
    }

    private double _mainFontSize = 22;
    public double MainFontSize
    {
        get => _mainFontSize;
        set
        {
            if (Math.Abs(_mainFontSize - value) > 0.01)
            {
                _mainFontSize = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SubFontSize));
            }
        }
    }

    /// <summary>翻译行字号随主歌词等比缩放（22 → 13.5）。</summary>
    public double SubFontSize => Math.Round(_mainFontSize * 13.5 / 22.0, 1);

    /// <summary>复制歌词内容（不含显示状态），供第二个歌词视图使用。</summary>
    public LyricLine CloneContent() => new()
    {
        Index = Index, Time = Time, EndTime = EndTime, Text = Text, Sub = Sub, Roma = Roma, Words = Words
    };

    public double TargetScale { get; set; } = 0.96;
    public double TargetOpacity { get; set; } = 0.15;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
