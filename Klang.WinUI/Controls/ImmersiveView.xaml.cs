using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Controls;

/// <summary>沉浸式全屏：封面取色的流动网格渐变背景 + 大封面 + 大号歌词 + Apple Music 风格播放控制。</summary>
public sealed partial class ImmersiveView : UserControl
{
    public event EventHandler? CloseRequested;
    public event EventHandler<double>? SeekRequested;
    public event EventHandler? PrevRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler<float>? VolumeChanged;

    private bool _updatingVolume;
    private bool _updatingSlider;
    private bool _dragging;
    private double _total;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _idleTimer;
    private string _lastTrackId = "";
    private string _lyricsKey = "";
    private string? _cover;

    public ImmersiveView()
    {
        InitializeComponent();
        Lyrics.ShowFades = false;
        Lyrics.SeekRequested += (s, t) => SeekRequested?.Invoke(this, t);

        // 拖动进度条：松开时才跳转
        ProgressSlider.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((s, e) => _dragging = true), true);
        ProgressSlider.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((s, e) =>
        {
            _dragging = false;
            SeekRequested?.Invoke(this, ProgressSlider.Value);
        }), true);
        ProgressSlider.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((s, e) =>
        {
            if (_dragging) { _dragging = false; SeekRequested?.Invoke(this, ProgressSlider.Value); }
        }), true);

        Dock.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(300) };
        PointerMoved += (s, e) => { if (IsOpen) WakeDock(); };
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>封面所在的元素：进入 / 退出沉浸模式时，封面在它和主界面封面之间做连贯转场。</summary>
    public UIElement CoverTarget => CoverFrame;

    public void Open()
    {
        Visibility = Visibility.Visible;
        UiAnimations.FadeIn(RootGrid, 320);   // 背景淡入，封面由连贯转场“飞”过来
        Fluid.Start();
        Fluid.SetCover(_cover);
        WakeDock();
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _idleTimer?.Stop();
        Fluid.Stop();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>低频能量（0~1），背景随节奏轻微脉动。</summary>
    public void SetEnergy(float energy) => Fluid.SetEnergy(energy);

    #region 播放控制

    public void SetVolume(double volume01)
    {
        _updatingVolume = true;
        VolumeSlider.Value = Math.Clamp(volume01, 0, 1) * 100;
        _updatingVolume = false;
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingVolume) return;
        VolumeChanged?.Invoke(this, (float)(e.NewValue / 100.0));
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => PrevRequested?.Invoke(this, EventArgs.Empty);
    private void Next_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);
    private void PlayPause_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);

    private static string Fmt(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    public void SetPlaying(bool playing) => PlayIcon.Glyph = playing ? "" : "";

    public void UpdateProgress(double position, double total)
    {
        _total = total;
        if (_dragging) return;
        PosText.Text = Fmt(position);
        DurText.Text = "-" + Fmt(total - position);
        _updatingSlider = true;
        ProgressSlider.Maximum = Math.Max(1, total);
        ProgressSlider.Value = Math.Clamp(position, 0, ProgressSlider.Maximum);
        _updatingSlider = false;
    }

    private void ProgressSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSlider) return;
        PosText.Text = Fmt(e.NewValue);
        DurText.Text = "-" + Fmt(_total - e.NewValue);
    }

    /// <summary>鼠标移动时显示控制条，静止 3 秒后淡出。</summary>
    private void WakeDock()
    {
        Dock.Opacity = 1;
        Dock.IsHitTestVisible = true;
        _idleTimer ??= DispatcherQueue.CreateTimer();
        _idleTimer.Interval = TimeSpan.FromSeconds(3);
        _idleTimer.IsRepeating = false;
        _idleTimer.Tick -= IdleTick;
        _idleTimer.Tick += IdleTick;
        _idleTimer.Start();
    }

    private void IdleTick(Microsoft.UI.Dispatching.DispatcherQueueTimer s, object e)
    {
        if (_dragging) { WakeDock(); return; }
        Dock.Opacity = 0;
        Dock.IsHitTestVisible = false;
    }

    #endregion

    #region 曲目 / 歌词

    public void SetTrack(TrackItem? track)
    {
        string? cover = track?.CoverPath;
        string id = track?.Id ?? "";
        bool sameTrack = id == _lastTrackId;
        _lastTrackId = id;

        void Apply()
        {
            TitleText.Text = track?.Title ?? "";
            ArtistText.Text = track?.Artist ?? "";
            try
            {
                CoverImage.Source = !string.IsNullOrEmpty(cover) && System.IO.File.Exists(cover)
                    ? new BitmapImage(new Uri(cover)) { DecodePixelWidth = 760 }
                    : null;
            }
            catch { CoverImage.Source = null; }
        }

        // 换了一首歌：封面 / 标题整体淡出 → 换内容 → 淡入；同一首歌只是封面补全到了，就直接替换
        if (IsOpen && !sameTrack) UiAnimations.FadeThrough(InfoPanel, Apply);
        else Apply();

        // 背景：新旧色块图在着色逻辑里 1.2 秒交叉淡入
        if (cover != _cover)
        {
            _cover = cover;
            if (IsOpen) Fluid.SetCover(cover);
        }
    }

    public void SetLyrics(IEnumerable<LyricLine> lines)
    {
        var list = lines.Select(l => l.CloneContent()).ToList();
        // 歌词内容变了（换歌 / 云端歌词到达）：新歌词淡入
        string key = list.Count + "|" + (list.Count > 0 ? list[0].Text : "");
        bool changed = key != _lyricsKey;
        _lyricsKey = key;

        Lyrics.LoadLyricLines(list);
        if (changed && IsOpen) UiAnimations.Pulse(Lyrics, 14f);
    }

    public void UpdateTime(double seconds)
    {
        if (IsOpen) Lyrics.UpdatePlaybackTime(seconds);
    }

    public string SubMode
    {
        get => Lyrics.SubMode;
        set => Lyrics.SubMode = value;
    }

    public int LyricFontSize
    {
        get => Lyrics.LyricFontSize;
        set => Lyrics.LyricFontSize = value;
    }

    #endregion
}
