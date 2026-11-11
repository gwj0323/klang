using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NAudio.Wave;
using Windows.Graphics;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Views;

/// <summary>
/// 桌面歌词：置顶透明悬浮窗，只显示一张半透明深色卡片。
/// 当前句逐字高亮，下面一行是译文（没有译文时显示下一句）；鼠标移入才浮现工具栏。
/// </summary>
public sealed partial class DesktopLyricWindow : Window
{
    private IList<LyricLine> _lyrics = Array.Empty<LyricLine>();
    private TrackItem? _track;
    private int _lastActiveIndex = -2;

    public event EventHandler? PrevRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? ClosedByUser;

    private static AppState State => StateService.Instance.State;

    public DesktopLyricWindow()
    {
        InitializeComponent();
        Title = "桌面歌词";

        // 整窗亚克力材质 + 深色着色，圆角由系统绘制
        try { SystemBackdrop = new DesktopAcrylicBackdrop(); } catch { }

        // 宽度随显示器：最宽 1000 DIP，至少 560
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        int width = area == null ? 880 : Math.Clamp((int)(area.WorkArea.Width * 0.55), 560, 1000);
        WindowHelpers.SetupOverlay(this, width, 124);
        WindowHelpers.Place(AppWindow, State.DesktopLyricX, State.DesktopLyricY,
            (work, size) => new PointInt32(work.X + (work.Width - size.Width) / 2, work.Y + work.Height - size.Height - 72));
        WindowHelpers.TrackPosition(AppWindow, (x, y) =>
        {
            State.DesktopLyricX = x;
            State.DesktopLyricY = y;
            StateService.Instance.RequestSave();
        });
        WindowHelpers.EnableDrag(this, WindowCard);

        MainLine.FontSize = Math.Clamp(State.DesktopLyricFontSize, 18, 56);
        SetSingle("Klang");

        Toolbar.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };
        UpdatePlayState(AudioEngineService.Instance.IsPlaying);
        AudioEngineService.Instance.StateChanged += state =>
            DispatcherQueue.TryEnqueue(() => UpdatePlayState(state == PlaybackState.Playing));
    }

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        Toolbar.Opacity = 1;
        Toolbar.IsHitTestVisible = true;
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        Toolbar.Opacity = 0;
        Toolbar.IsHitTestVisible = false;
    }

    #region 歌词显示

    private void SetSingle(string text)
    {
        MainLine.Line = new LyricLine { Text = text };
        SubLine.Text = "";
        SubLine.Visibility = Visibility.Collapsed;
    }

    private bool HasRealLyrics => _lyrics.Count > 0 && !(_lyrics.Count == 1 && _lyrics[0].EndTime >= 9000);

    /// <summary>没有歌词时显示“歌名 — 歌手”。</summary>
    private void ShowTrackInfo()
    {
        if (_track == null) SetSingle("Klang");
        else SetSingle($"{_track.Title} — {_track.Artist}");
    }

    public void UpdateTrack(TrackItem? track)
    {
        _track = track;
        if (!HasRealLyrics) ShowTrackInfo();
    }

    public void UpdateLyrics(IList<LyricLine> lyrics)
    {
        _lyrics = lyrics;
        _lastActiveIndex = -2;
        if (!HasRealLyrics) ShowTrackInfo();
    }

    public void UpdatePlaybackTime(double seconds)
    {
        if (!HasRealLyrics) return;

        int idx = LyricsService.Instance.FindActiveIndex(_lyrics, seconds);
        if (idx == _lastActiveIndex) return;
        _lastActiveIndex = idx;

        if (idx < 0)
        {
            // 还没唱到第一句：先显示歌名
            ShowTrackInfo();
            return;
        }

        var line = _lyrics[idx];
        MainLine.Line = line;

        string sub = line.HasSub ? line.DisplaySub : "";
        if (string.IsNullOrWhiteSpace(sub) && idx + 1 < _lyrics.Count) sub = _lyrics[idx + 1].Text;
        SubLine.Text = sub.Replace('\n', ' ');
        SubLine.Visibility = string.IsNullOrWhiteSpace(sub) ? Visibility.Collapsed : Visibility.Visible;
    }

    #endregion

    #region 按钮

    public void UpdatePlayState(bool isPlaying)
    {
        PlayPauseIcon.Glyph = isPlaying ? "" : "";
    }

    private void PrevBtn_Click(object sender, RoutedEventArgs e) => PrevRequested?.Invoke(this, EventArgs.Empty);
    private void NextBtn_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);
    private void PlayPauseBtn_Click(object sender, RoutedEventArgs e) => AudioEngineService.Instance.TogglePlayPause();

    private void ChangeFont(int delta)
    {
        int size = Math.Clamp((int)MainLine.FontSize + delta, 18, 56);
        MainLine.FontSize = size;
        State.DesktopLyricFontSize = size;
        StateService.Instance.RequestSave();
    }

    private void FontDecBtn_Click(object sender, RoutedEventArgs e) => ChangeFont(-2);
    private void FontIncBtn_Click(object sender, RoutedEventArgs e) => ChangeFont(2);

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        AppWindow.Hide();
        ClosedByUser?.Invoke(this, EventArgs.Empty);
    }

    #endregion
}
