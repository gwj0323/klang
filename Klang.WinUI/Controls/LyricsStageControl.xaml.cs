using System;
using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Controls;

public sealed partial class LyricsStageControl : UserControl
{
    public ObservableCollection<LyricLine> Lyrics { get; } = new();

    private int _lastActiveIndex = -2;
    private double _currentScrollY = 0;
    private double _targetScrollY = 0;
    private double _scrollVel = 0;

    private bool _isPhysicsActive = false;
    private bool _isUserScrolling = false;
    private bool _isInternalScrollUpdate = false;

    private readonly DispatcherTimer _physicsTimer = new();
    private readonly DispatcherTimer _resumeAutoCenterTimer = new();
    private DateTime _lastTickTime = DateTime.UtcNow;

    public event EventHandler<double>? SeekRequested;

    private double _lyricFontSize = 22;
    public const int MinLyricFontSize = 14;
    public const int MaxLyricFontSize = 40;

    /// <summary>歌词字号（主歌词，14–40）。修改后立即应用到当前所有歌词行。</summary>
    public int LyricFontSize
    {
        get => (int)_lyricFontSize;
        set
        {
            _lyricFontSize = Math.Clamp(value, MinLyricFontSize, MaxLyricFontSize);
            foreach (var l in Lyrics) l.MainFontSize = _lyricFontSize;
            if (_lastActiveIndex >= 0) CalculateTargetScroll(_lastActiveIndex);
        }
    }

    private string _subMode = "trans";

    /// <summary>是否显示上下羽化遮罩（沉浸模式的彩色背景上要关掉）。</summary>
    public bool ShowFades
    {
        set
        {
            // 固定颜色的遮罩与半透明材质背景不协调，已弃用；歌词行本身按距离渐隐
        }
    }

    /// <summary>副行显示：trans 翻译 | roma 罗马音 | both 两者 | off 关闭。</summary>
    public string SubMode
    {
        get => _subMode;
        set
        {
            _subMode = value;
            foreach (var l in Lyrics) l.SubMode = value;
            if (_lastActiveIndex >= 0) CalculateTargetScroll(_lastActiveIndex);
        }
    }

    public LyricsStageControl()
    {
        InitializeComponent();
        LyricsItemsControl.ItemsSource = Lyrics;
        UpdateEmptyState();
        UpdateFadeBrushes();
        UiAnimations.AttachFade(ResumeCenterBtn, 6f);
        UiAnimations.AttachFade(EmptyLyricsPanel, 6f);

        this.ActualThemeChanged += (s, e) => UpdateFadeBrushes();
        this.SizeChanged += (s, e) =>
        {
            UpdateFadeBrushes();
            if (_lastActiveIndex >= 0)
            {
                CalculateTargetScroll(_lastActiveIndex);
            }
        };

        // 60fps 弹簧阻尼谐振与逐行视觉连续插值核心
        _physicsTimer.Interval = TimeSpan.FromMilliseconds(16);
        _physicsTimer.Tick += PhysicsTimer_Tick;

        // 用户滚动后 2.8 秒自动恢复居中吸附
        _resumeAutoCenterTimer.Interval = TimeSpan.FromMilliseconds(2800);
        _resumeAutoCenterTimer.Tick += (s, e) =>
        {
            _resumeAutoCenterTimer.Stop();
            _isUserScrolling = false;
            ResumeCenterBtn.Visibility = Visibility.Collapsed;

            if (_lastActiveIndex >= 0)
            {
                CalculateTargetScroll(_lastActiveIndex);
            }
        };

        LyricsScrollViewer.ViewChanged += LyricsScrollViewer_ViewChanged;
    }

    private void UpdateFadeBrushes()
    {
        bool isDark = (this.ActualTheme == ElementTheme.Dark);
        Color baseCol = isDark ? Color.FromArgb(255, 28, 31, 35) : Color.FromArgb(255, 246, 245, 241);

        var topGrad = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        topGrad.GradientStops.Add(new GradientStop { Color = baseCol, Offset = 0.0 });
        topGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(180, baseCol.R, baseCol.G, baseCol.B), Offset = 0.5 });
        topGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, baseCol.R, baseCol.G, baseCol.B), Offset = 1.0 });
        TopFadeBorder.Background = topGrad;

        var bottomGrad = new LinearGradientBrush
        {
            StartPoint = new Point(0, 1),
            EndPoint = new Point(0, 0)
        };
        bottomGrad.GradientStops.Add(new GradientStop { Color = baseCol, Offset = 0.0 });
        bottomGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(180, baseCol.R, baseCol.G, baseCol.B), Offset = 0.5 });
        bottomGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, baseCol.R, baseCol.G, baseCol.B), Offset = 1.0 });
        BottomFadeBorder.Background = bottomGrad;
    }

    private void LyricsScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isInternalScrollUpdate) return;

        // 用户正在手动使用滚轮或触控板滚动
        if (e.IsIntermediate || Math.Abs(LyricsScrollViewer.VerticalOffset - _currentScrollY) > 2.0)
        {
            _isUserScrolling = true;
            _currentScrollY = LyricsScrollViewer.VerticalOffset;
            _scrollVel = 0;

            ResumeCenterBtn.Visibility = Visibility.Visible;
            ResumeCenterBtn.Opacity = 1.0;

            _resumeAutoCenterTimer.Stop();
            _resumeAutoCenterTimer.Start();
        }
    }

    private void UpdateEmptyState()
    {
        bool hasLyrics = Lyrics.Count > 0;
        EmptyLyricsPanel.Visibility = hasLyrics ? Visibility.Collapsed : Visibility.Visible;
        LyricsScrollViewer.Visibility = hasLyrics ? Visibility.Visible : Visibility.Collapsed;
    }

    public void LoadLyrics(string? audioFilePath)
    {
        _lastActiveIndex = -2;
        _scrollVel = 0;
        _isUserScrolling = false;
        ResumeCenterBtn.Visibility = Visibility.Collapsed;

        Lyrics.Clear();
        var loaded = LyricsService.Instance.LoadLyrics(audioFilePath);
        foreach (var l in loaded)
        {
            l.MainFontSize = _lyricFontSize;
            l.SubMode = _subMode;
            Lyrics.Add(l);
        }

        UpdateEmptyState();
        _currentScrollY = 0;
        _targetScrollY = 0;
        LyricsScrollViewer.ChangeView(null, 0, null, true);
    }

    /// <summary>直接载入已解析好的歌词（云端歌词）。</summary>
    public void LoadLyricLines(IList<LyricLine> lines)
    {
        _lastActiveIndex = -2;
        _scrollVel = 0;
        _isUserScrolling = false;
        ResumeCenterBtn.Visibility = Visibility.Collapsed;

        Lyrics.Clear();
        foreach (var l in lines)
        {
            l.MainFontSize = _lyricFontSize;
            l.SubMode = _subMode;
            Lyrics.Add(l);
        }
        UpdateEmptyState();
        _currentScrollY = 0;
        _targetScrollY = 0;
        LyricsScrollViewer.ChangeView(null, 0, null, true);
    }

    public void UpdatePlaybackTime(double currentTimeSeconds)
    {
        if (Lyrics.Count == 0) return;

        int activeIndex = LyricsService.Instance.FindActiveIndex(Lyrics, currentTimeSeconds);

        // 逐字高亮：当前行随时间推进（每次 tick 更新）
        if (activeIndex >= 0 && Lyrics[activeIndex].Words is { } words)
        {
            int played = 0;
            while (played < words.Count && words[played].Start <= currentTimeSeconds) played++;
            Lyrics[activeIndex].PlayedWords = played;
        }

        if (activeIndex == _lastActiveIndex) return;

        _lastActiveIndex = activeIndex;

        // 换行时：已过的行全部点亮，未到的行清零
        for (int i = 0; i < Lyrics.Count; i++)
        {
            if (i == activeIndex || Lyrics[i].Words is not { } w) continue;
            Lyrics[i].PlayedWords = 0;
        }

        // 重新分配各行目标状态（距离当前行的指数衰减权重）
        for (int i = 0; i < Lyrics.Count; i++)
        {
            var line = Lyrics[i];
            int dist = Math.Abs(i - activeIndex);
            line.IsActive = (i == activeIndex);

            if (dist == 0)
            {
                line.TargetScale = 1.14;
                line.TargetOpacity = 1.0;
            }
            else if (dist == 1)
            {
                line.TargetScale = 1.0;
                line.TargetOpacity = 0.50;
            }
            else if (dist == 2)
            {
                line.TargetScale = 0.96;
                line.TargetOpacity = 0.28;
            }
            else if (dist == 3)
            {
                line.TargetScale = 0.93;
                line.TargetOpacity = 0.15;
            }
            else
            {
                line.TargetScale = 0.90;
                line.TargetOpacity = 0.08;
            }
        }

        StartPhysics();

        if (activeIndex >= 0 && !_isUserScrolling)
        {
            CalculateTargetScroll(activeIndex);
        }
    }

    private void CalculateTargetScroll(int index)
    {
        if (index < 0 || index >= Lyrics.Count) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (LyricsItemsControl.ContainerFromIndex(index) is FrameworkElement container)
            {
                try
                {
                    var transform = container.TransformToVisual(LyricsScrollViewer);
                    var point = transform.TransformPoint(new Point(0, 0));
                    double viewportH = LyricsScrollViewer.ActualHeight;

                    if (viewportH > 0)
                    {
                        double lineCenterInContent = LyricsScrollViewer.VerticalOffset + point.Y + (container.ActualHeight / 2.0);
                        _targetScrollY = Math.Max(0, lineCenterInContent - (viewportH / 2.0));
                        StartPhysics();
                    }
                }
                catch
                {
                    // 布局未完成安全保护
                }
            }
        });
    }

    private void StartPhysics()
    {
        _lastTickTime = DateTime.UtcNow;
        if (!_isPhysicsActive)
        {
            _isPhysicsActive = true;
            _physicsTimer.Start();
        }
    }

    private void PhysicsTimer_Tick(object? sender, object e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastTickTime).TotalSeconds, 0.002, 0.04);
        _lastTickTime = now;

        bool needsContinue = false;

        // 1. 视口阻尼谐振弹簧居中方程 (Apple Music / Qt6 级联弹簧阻尼)
        if (!_isUserScrolling)
        {
            double disp = _currentScrollY - _targetScrollY;
            if (Math.Abs(disp) > 0.35 || Math.Abs(_scrollVel) > 0.8)
            {
                const double omega = 9.8; // 弹簧自然频率 (~450ms 丝滑吸附)
                double accel = -(omega * omega) * disp - 2.0 * omega * _scrollVel;
                _scrollVel += accel * dt;
                _currentScrollY += _scrollVel * dt;

                _isInternalScrollUpdate = true;
                LyricsScrollViewer.ChangeView(null, _currentScrollY, null, true);
                _isInternalScrollUpdate = false;
                needsContinue = true;
            }
            else
            {
                _currentScrollY = _targetScrollY;
                _scrollVel = 0;
                _isInternalScrollUpdate = true;
                LyricsScrollViewer.ChangeView(null, _currentScrollY, null, true);
                _isInternalScrollUpdate = false;
            }
        }

        // 2. 逐行视觉平滑插值 (Scale 与 Opacity 自然平滑呼吸，杜绝突变跳帧)
        double alpha = 1.0 - Math.Exp(-12.5 * dt);
        int start = Math.Max(0, _lastActiveIndex - 6);
        int end = Math.Min(Lyrics.Count - 1, _lastActiveIndex + 6);

        for (int i = start; i <= end; i++)
        {
            var line = Lyrics[i];
            double ds = line.TargetScale - line.ScaleX;
            double @do = line.TargetOpacity - line.LineOpacity;

            if (Math.Abs(ds) > 0.002)
            {
                line.ScaleX += ds * alpha;
                line.ScaleY = line.ScaleX;
                needsContinue = true;
            }
            else
            {
                line.ScaleX = line.TargetScale;
                line.ScaleY = line.TargetScale;
            }

            if (Math.Abs(@do) > 0.005)
            {
                line.LineOpacity += @do * alpha;
                needsContinue = true;
            }
            else
            {
                line.LineOpacity = line.TargetOpacity;
            }
        }

        if (!needsContinue && (Math.Abs(_currentScrollY - _targetScrollY) <= 0.35))
        {
            _isPhysicsActive = false;
            _physicsTimer.Stop();
        }
    }

    private void LyricLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is LyricLine line)
        {
            _isUserScrolling = false;
            ResumeCenterBtn.Visibility = Visibility.Collapsed;
            SeekRequested?.Invoke(this, line.Time);
        }
    }

    private void ResumeCenterBtn_Click(object sender, RoutedEventArgs e)
    {
        _isUserScrolling = false;
        ResumeCenterBtn.Visibility = Visibility.Collapsed;
        if (_lastActiveIndex >= 0)
        {
            CalculateTargetScroll(_lastActiveIndex);
        }
    }
}
