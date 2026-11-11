using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.Wave;
using Windows.Graphics;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Views;

/// <summary>迷你播放器：亚克力材质的小窗，封面 + 歌名歌手 + 当前歌词 + 进度与控制；鼠标移入才显示右上角按钮。</summary>
public sealed partial class MiniPlayerWindow : Window
{
    private bool _isUserDraggingSlider;

    public event EventHandler? RestoreRequested;
    public event EventHandler? PrevRequested;
    public event EventHandler? NextRequested;
    public event EventHandler<double>? SeekRequested;
    public event EventHandler? ClosedByUser;

    private static AppState State => StateService.Instance.State;

    public MiniPlayerWindow()
    {
        InitializeComponent();
        Title = L.T("迷你播放器");
        if (Content is FrameworkElement miniRoot) L.Attach(miniRoot, this);

        try { SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { }

        WindowHelpers.SetupOverlay(this, 420, 148);
        WindowHelpers.Place(AppWindow, State.MiniX, State.MiniY,
            (work, size) => new PointInt32(work.X + work.Width - size.Width - 32, work.Y + work.Height - size.Height - 32));
        WindowHelpers.TrackPosition(AppWindow, (x, y) =>
        {
            State.MiniX = x;
            State.MiniY = y;
            StateService.Instance.RequestSave();
        });
        WindowHelpers.EnableDrag(this, Card);

        TopButtons.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };

        // 滑块拖动：松开时才跳转
        MiniTimelineSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) => _isUserDraggingSlider = true), true);
        void EndDrag(object s, PointerRoutedEventArgs e)
        {
            if (!_isUserDraggingSlider) return;
            _isUserDraggingSlider = false;
            SeekRequested?.Invoke(this, MiniTimelineSlider.Value);
        }
        MiniTimelineSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(EndDrag), true);
        MiniTimelineSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(EndDrag), true);

        UpdatePlayState(AudioEngineService.Instance.IsPlaying);
        AudioEngineService.Instance.StateChanged += state =>
            DispatcherQueue.TryEnqueue(() => UpdatePlayState(state == PlaybackState.Playing));
    }

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        TopButtons.Opacity = 1;
        TopButtons.IsHitTestVisible = true;
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        TopButtons.Opacity = 0;
        TopButtons.IsHitTestVisible = false;
    }

    public void UpdateTrack(TrackItem? track)
    {
        if (track == null)
        {
            MiniTitleText.Text = L.T("暂无播放曲目");
            MiniArtistText.Text = "Klang";
            MiniLyricText.Text = "";
            ShowCover(null);
            return;
        }

        MiniTitleText.Text = string.IsNullOrWhiteSpace(track.Title) ? L.T("未命名曲目") : track.Title;
        MiniArtistText.Text = string.IsNullOrWhiteSpace(track.Artist) ? "未知歌手" : track.Artist;
        MiniLyricText.Text = "";
        ShowCover(track.CoverPath);
    }

    private void ShowCover(string? path)
    {
        bool has = !string.IsNullOrEmpty(path) && File.Exists(path);
        try
        {
            MiniCoverImage.Source = has ? new BitmapImage(new Uri(path!)) { DecodePixelWidth = 208 } : null;
        }
        catch
        {
            has = false;
        }
        MiniCoverImage.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        MiniCoverFallback.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>当前这句歌词（空表示没有）。</summary>
    public void UpdateLyric(string? text)
    {
        MiniLyricText.Text = text ?? "";
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
        MiniPlayPauseIcon.Glyph = isPlaying ? "" : "";
    }

    private void MiniTimelineSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUserDraggingSlider)
            MiniTimeText.Text = $"{FormatTime(e.NewValue)} / {FormatTime(MiniTimelineSlider.Maximum)}";
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

    private void MiniPrevBtn_Click(object sender, RoutedEventArgs e) => PrevRequested?.Invoke(this, EventArgs.Empty);
    private void MiniNextBtn_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);
    private void MiniPlayPauseBtn_Click(object sender, RoutedEventArgs e) => AudioEngineService.Instance.TogglePlayPause();

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
    }
}
