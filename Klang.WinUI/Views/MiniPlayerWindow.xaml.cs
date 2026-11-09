using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.Wave;
using Windows.Graphics;
using WinRT.Interop;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Views;

public sealed partial class MiniPlayerWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    private readonly IntPtr _hwnd;
    private bool _isUserDraggingSlider = false;

    public event EventHandler? RestoreRequested;
    public event EventHandler? PrevRequested;
    public event EventHandler? NextRequested;
    public event EventHandler<double>? SeekRequested;
    public event EventHandler? ClosedByUser;

    public MiniPlayerWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);

        // 配置始终置顶无边框紧凑窗口
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);

        const int winWidth = 368;
        const int winHeight = 120;
        AppWindow.Resize(new SizeInt32(winWidth, winHeight));

        // 默认定位在主屏幕右下角
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            int startX = displayArea.WorkArea.X + displayArea.WorkArea.Width - winWidth - 36;
            int startY = displayArea.WorkArea.Y + displayArea.WorkArea.Height - winHeight - 64;
            AppWindow.Move(new PointInt32(startX, startY));
        }

        // 挂载鼠标全局拖拽移动窗口
        WindowCard.PointerPressed += WindowCard_PointerPressed;

        // 挂载滑块无缝拖拽
        MiniTimelineSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) =>
        {
            _isUserDraggingSlider = true;
        }), true);

        MiniTimelineSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((s, e) =>
        {
            if (_isUserDraggingSlider)
            {
                _isUserDraggingSlider = false;
                SeekRequested?.Invoke(this, MiniTimelineSlider.Value);
            }
        }), true);

        MiniTimelineSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((s, e) =>
        {
            if (_isUserDraggingSlider)
            {
                _isUserDraggingSlider = false;
                SeekRequested?.Invoke(this, MiniTimelineSlider.Value);
            }
        }), true);

        // 监听播放状态
        UpdatePlayState(AudioEngineService.Instance.IsPlaying);
        AudioEngineService.Instance.StateChanged += (state) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdatePlayState(state == PlaybackState.Playing);
            });
        };
    }

    private void WindowCard_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var cur = dep;
            while (cur != null && cur != WindowCard)
            {
                if (cur is Microsoft.UI.Xaml.Controls.Button || cur is Microsoft.UI.Xaml.Controls.Slider) return;
                cur = VisualTreeHelper.GetParent(cur);
            }
        }

        ReleaseCapture();
        SendMessage(_hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    public void UpdateTrack(TrackItem? track)
    {
        if (track == null)
        {
            MiniTitleText.Text = "暂无播放曲目";
            MiniArtistText.Text = "Klang 发烧播放器";
            MiniCoverImage.Visibility = Visibility.Collapsed;
            MiniCoverFallback.Visibility = Visibility.Visible;
            return;
        }

        MiniTitleText.Text = string.IsNullOrWhiteSpace(track.Title) ? "未命名曲目" : track.Title;
        MiniArtistText.Text = string.IsNullOrWhiteSpace(track.Artist) ? "未知艺术家" : track.Artist;

        if (!string.IsNullOrEmpty(track.CoverPath) && File.Exists(track.CoverPath))
        {
            try
            {
                MiniCoverImage.Source = new BitmapImage(new Uri(track.CoverPath));
                MiniCoverImage.Visibility = Visibility.Visible;
                MiniCoverFallback.Visibility = Visibility.Collapsed;
            }
            catch
            {
                MiniCoverImage.Visibility = Visibility.Collapsed;
                MiniCoverFallback.Visibility = Visibility.Visible;
            }
        }
        else
        {
            MiniCoverImage.Visibility = Visibility.Collapsed;
            MiniCoverFallback.Visibility = Visibility.Visible;
        }
    }

    public void UpdateProgress(double currentSec, double totalSec)
    {
        if (_isUserDraggingSlider) return;

        MiniTimeText.Text = $"{FormatTime(currentSec)} / {FormatTime(totalSec)}";

        if (totalSec > 0)
        {
            MiniTimelineSlider.Maximum = totalSec;
            MiniTimelineSlider.Value = Math.Min(currentSec, totalSec);
        }
        else
        {
            MiniTimelineSlider.Maximum = 100;
            MiniTimelineSlider.Value = 0;
        }
    }

    public void UpdatePlayState(bool isPlaying)
    {
        MiniPlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
        MiniPlayPauseBtn.SetValue(Microsoft.UI.Xaml.Controls.ToolTipService.ToolTipProperty, isPlaying ? "暂停" : "播放");
    }

    private void MiniTimelineSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUserDraggingSlider)
        {
            MiniTimeText.Text = $"{FormatTime(e.NewValue)} / {FormatTime(MiniTimelineSlider.Maximum)}";
        }
    }

    private void RestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        AppWindow.Hide();
        RestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        AppWindow.Hide();
        ClosedByUser?.Invoke(this, EventArgs.Empty);
    }

    private void MiniPrevBtn_Click(object sender, RoutedEventArgs e)
    {
        PrevRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MiniPlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        AudioEngineService.Instance.TogglePlayPause();
    }

    private void MiniNextBtn_Click(object sender, RoutedEventArgs e)
    {
        NextRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }
}
