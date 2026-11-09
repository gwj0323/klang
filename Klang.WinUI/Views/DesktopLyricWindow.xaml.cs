using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NAudio.Wave;
using Windows.Graphics;
using WinRT.Interop;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Views;

public sealed partial class DesktopLyricWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    private readonly IntPtr _hwnd;
    private IList<LyricLine> _lyrics = Array.Empty<LyricLine>();
    private int _lastActiveIndex = -1;

    public event EventHandler? PrevRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? ClosedByUser;

    public DesktopLyricWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);

        // 配置始终置顶无边框悬浮窗
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);

        const int winWidth = 740;
        const int winHeight = 114;
        AppWindow.Resize(new SizeInt32(winWidth, winHeight));

        // 默认定位在主屏幕底部正中央偏上
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            int startX = displayArea.WorkArea.X + (displayArea.WorkArea.Width - winWidth) / 2;
            int startY = displayArea.WorkArea.Y + displayArea.WorkArea.Height - winHeight - 64;
            AppWindow.Move(new PointInt32(startX, startY));
        }

        // 挂载鼠标全局拖拽移动窗口 (点按卡片任意空白区域即可拖动)
        WindowCard.PointerPressed += WindowCard_PointerPressed;

        // 同步播放状态图标
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
        // 若点击的是子控件按钮，则不触发窗口拖拽
        if (e.OriginalSource is DependencyObject dep)
        {
            var cur = dep;
            while (cur != null && cur != WindowCard)
            {
                if (cur is Microsoft.UI.Xaml.Controls.Button) return;
                cur = VisualTreeHelper.GetParent(cur);
            }
        }

        ReleaseCapture();
        SendMessage(_hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    public void UpdateLyrics(IList<LyricLine> lyrics)
    {
        _lyrics = lyrics;
        _lastActiveIndex = -1;
        if (_lyrics.Count == 0)
        {
            PrimaryLyricText.Text = "Klang 极简发烧音乐";
            SubLyricText.Visibility = Visibility.Collapsed;
        }
    }

    public void UpdatePlaybackTime(double seconds)
    {
        if (_lyrics.Count == 0) return;

        int activeIdx = LyricsService.Instance.FindActiveIndex(_lyrics, seconds);
        if (activeIdx != _lastActiveIndex)
        {
            _lastActiveIndex = activeIdx;
            if (activeIdx >= 0 && activeIdx < _lyrics.Count)
            {
                PrimaryLyricText.Text = _lyrics[activeIdx].Text;
                if (_lyrics[activeIdx].HasSub)
                {
                    SubLyricText.Text = _lyrics[activeIdx].Sub;
                    SubLyricText.Visibility = Visibility.Visible;
                }
                else
                {
                    SubLyricText.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    public void UpdatePlayState(bool isPlaying)
    {
        PlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
        PlayPauseBtn.SetValue(Microsoft.UI.Xaml.Controls.ToolTipService.ToolTipProperty, isPlaying ? "暂停" : "播放");
    }

    private void PrevBtn_Click(object sender, RoutedEventArgs e)
    {
        PrevRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        AudioEngineService.Instance.TogglePlayPause();
    }

    private void NextBtn_Click(object sender, RoutedEventArgs e)
    {
        NextRequested?.Invoke(this, EventArgs.Empty);
    }

    private void FontDecBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PrimaryLyricText.FontSize > 14)
        {
            PrimaryLyricText.FontSize -= 2;
        }
    }

    private void FontIncBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PrimaryLyricText.FontSize < 34)
        {
            PrimaryLyricText.FontSize += 2;
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        AppWindow.Hide();
        ClosedByUser?.Invoke(this, EventArgs.Empty);
    }
}
