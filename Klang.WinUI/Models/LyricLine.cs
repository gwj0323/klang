using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Windows.UI.Text;

namespace Klang_WinUI.Models;

public class LyricLine : INotifyPropertyChanged
{
    public int Index { get; set; }
    public double Time { get; set; }
    public double EndTime { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Sub { get; set; } = string.Empty;
    public bool HasSub => !string.IsNullOrWhiteSpace(Sub);
    public Visibility SubVisibility => HasSub ? Visibility.Visible : Visibility.Collapsed;

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

    public double TargetScale { get; set; } = 0.96;
    public double TargetOpacity { get; set; } = 0.15;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
