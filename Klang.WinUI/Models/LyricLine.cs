using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Text;
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
                OnPropertyChanged(nameof(MainFontSize));
                OnPropertyChanged(nameof(SubFontSize));
                OnPropertyChanged(nameof(FontWeight));
                OnPropertyChanged(nameof(LineOpacity));
                OnPropertyChanged(nameof(ScaleX));
                OnPropertyChanged(nameof(ScaleY));
            }
        }
    }

    private double _distanceFromActive = 99;
    public double DistanceFromActive
    {
        get => _distanceFromActive;
        set
        {
            if (Math.Abs(_distanceFromActive - value) > 0.05)
            {
                _distanceFromActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LineOpacity));
            }
        }
    }

    public double MainFontSize => IsActive ? 26.0 : 18.0;
    public double SubFontSize => IsActive ? 15.0 : 13.0;
    public FontWeight FontWeight => IsActive ? FontWeights.Bold : FontWeights.Normal;
    public double ScaleX => IsActive ? 1.05 : 1.0;
    public double ScaleY => IsActive ? 1.05 : 1.0;

    public double LineOpacity
    {
        get
        {
            if (IsActive) return 1.0;
            if (DistanceFromActive <= 1) return 0.55;
            if (DistanceFromActive <= 2) return 0.32;
            if (DistanceFromActive <= 3) return 0.18;
            return 0.10;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
