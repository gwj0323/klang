using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Klang_WinUI.Models;

namespace Klang_WinUI.Controls;

public sealed partial class VinylCoverCard : UserControl
{
    private const int NUM_BARS = 24;
    private const double BAR_W = 3.4;
    private const double CANVAS_H = 32.0;
    private const double MAX_H = CANVAS_H - 6.0;
    private const double BASE_Y = CANVAS_H - 2.0;

    // 所有会持续或频繁更新的动画都放在合成层（跑在合成线程，跟显示器刷新率走），界面线程只做必要的逻辑：
    // 唱片自转 / 滑出、封面切换过渡用合成动画；频谱每帧只改合成层的缩放 / 位移，不触发布局。
    private readonly Visual _discVisual;
    private readonly Visual _jacketVisual;
    private readonly Visual _metaVisual;
    private readonly Compositor _compositor;
    private ScalarKeyFrameAnimation? _spin;

    private bool _isPlaying = false;

    private readonly Microsoft.UI.Xaml.Shapes.Rectangle[] _bars = new Microsoft.UI.Xaml.Shapes.Rectangle[NUM_BARS];
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse[] _dots = new Microsoft.UI.Xaml.Shapes.Ellipse[NUM_BARS];
    private readonly Visual[] _barVisuals = new Visual[NUM_BARS];
    private readonly Visual[] _dotVisuals = new Visual[NUM_BARS];
    private readonly float[] _targets = new float[NUM_BARS];
    private readonly float[] _values = new float[NUM_BARS];
    private readonly float[] _peaks = new float[NUM_BARS];
    private readonly float[] _peakVels = new float[NUM_BARS];
    private DateTime _lastSpectrumTick = DateTime.UtcNow;
    private bool _renderingHooked;

    public VinylCoverCard()
    {
        InitializeComponent();

        _discVisual = ElementCompositionPreview.GetElementVisual(VinylDisc);
        _jacketVisual = ElementCompositionPreview.GetElementVisual(JacketCard);
        _metaVisual = ElementCompositionPreview.GetElementVisual(MetaInfoPanel);
        _compositor = _discVisual.Compositor;
        ElementCompositionPreview.GetElementVisual(SpectrumCanvas).Opacity = 0f;   // 未播放时不显示频谱
        ElementCompositionPreview.SetIsTranslationEnabled(VinylDisc, true);
        ElementCompositionPreview.SetIsTranslationEnabled(MetaInfoPanel, true);
        _discVisual.CenterPoint = new Vector3(120f, 120f, 0f);   // 以圆心自转，绝不偏心
        _jacketVisual.CenterPoint = new Vector3(130f, 130f, 0f);

        // 频谱：柱子固定高度，用合成层的 Scale.Y 表示高度，峰值点用 Translation.Y 定位
        // 颜色在 Loaded / 主题变化时按元素的实际主题设置（Application.Current.Resources 只给浅色主题的值，深色下柱子会是深蓝色）
        for (int i = 0; i < NUM_BARS; i++)
        {
            _bars[i] = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = BAR_W, Height = MAX_H, RadiusX = 1.7, RadiusY = 1.7, Opacity = 0.35 };
            _dots[i] = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 2.8, Height = 2.8, Opacity = 0, IsHitTestVisible = false };
            SpectrumCanvas.Children.Add(_bars[i]);
            SpectrumCanvas.Children.Add(_dots[i]);
            Canvas.SetTop(_bars[i], BASE_Y - MAX_H);
            Canvas.SetTop(_dots[i], 0);
            ElementCompositionPreview.SetIsTranslationEnabled(_dots[i], true);
            _barVisuals[i] = ElementCompositionPreview.GetElementVisual(_bars[i]);
            _dotVisuals[i] = ElementCompositionPreview.GetElementVisual(_dots[i]);
            _barVisuals[i].CenterPoint = new Vector3((float)(BAR_W / 2), (float)MAX_H, 0f); // 以底边为基准伸缩
            _values[i] = 0.04f;
            _peaks[i] = 0.04f;
        }
        for (int i = 0; i < NUM_BARS; i++) ApplyBar(i, 0.04f, 0.04f);

        Loaded += (s, e) => ApplySpectrumBrush();
        ActualThemeChanged += (s, e) => ApplySpectrumBrush();
    }

    private void ApplySpectrumBrush()
    {
        var accent = Services.ThemeService.AccentFill(this);
        for (int i = 0; i < NUM_BARS; i++)
        {
            _bars[i].Fill = accent;
            _dots[i].Fill = accent;
        }
    }

    #region 唱片自转 / 滑出（合成动画）

    private void StartSpin()
    {
        if (_spin == null)
        {
            // 从“当前角度”起转一整圈，循环时无缝；每圈 12 秒
            _spin = _compositor.CreateScalarKeyFrameAnimation();
            _spin.InsertExpressionKeyFrame(0f, "this.StartingValue");
            _spin.InsertExpressionKeyFrame(1f, "this.StartingValue + 360", _compositor.CreateLinearEasingFunction());
            _spin.Duration = TimeSpan.FromSeconds(12);
            _spin.IterationBehavior = AnimationIterationBehavior.Forever;
        }
        _discVisual.StartAnimation("RotationAngleInDegrees", _spin);
    }

    private void StopSpin() => _discVisual.StopAnimation("RotationAngleInDegrees");

    private void SlideDisc(bool show)
    {
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        var dur = TimeSpan.FromMilliseconds(520);

        if (show) VinylDisc.Visibility = Visibility.Visible;

        var x = _compositor.CreateScalarKeyFrameAnimation();
        x.InsertExpressionKeyFrame(0f, "this.StartingValue");
        x.InsertKeyFrame(1f, show ? 48f : 0f, ease);
        x.Duration = dur;
        var op = _compositor.CreateScalarKeyFrameAnimation();
        op.InsertExpressionKeyFrame(0f, "this.StartingValue");
        op.InsertKeyFrame(1f, show ? 1f : 0f, ease);
        op.Duration = TimeSpan.FromMilliseconds(360);

        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _discVisual.StartAnimation("Translation.X", x);
        _discVisual.StartAnimation("Opacity", op);
        batch.End();
        batch.Completed += (s, e) =>
        {
            if (!_isPlaying && !show) VinylDisc.Visibility = Visibility.Collapsed;
        };
    }

    #endregion

    #region 频谱（每帧只改合成层属性）

    private void SpectrumCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = SpectrumCanvas.ActualWidth;
        if (w <= 0) return;
        // 柱子铺满与封面等宽的区域，左右边缘与封面、文字对齐
        double barW = Math.Clamp(w / NUM_BARS * 0.42, BAR_W, 5.0);
        double gap = (w - NUM_BARS * barW) / (NUM_BARS - 1);
        for (int i = 0; i < NUM_BARS; i++)
        {
            double x = i * (barW + gap);
            _bars[i].Width = barW;
            Canvas.SetLeft(_bars[i], x);
            Canvas.SetLeft(_dots[i], x + barW / 2.0 - 1.4);
            _barVisuals[i].CenterPoint = new Vector3((float)(barW / 2), (float)MAX_H, 0f);
        }
    }

    /// <summary>按 Qt 版的几何显示一根柱与它的峰值点：高度 = 缩放，透明度随能量变化。</summary>
    private void ApplyBar(int i, float val, float peak)
    {
        double bh = Math.Max(2.8, val * MAX_H);
        var v = _barVisuals[i];
        v.Scale = new Vector3(1f, (float)(bh / MAX_H), 1f);
        v.Opacity = (float)(0.28 + 0.64 * val);

        var d = _dotVisuals[i];
        if (peak > val + 0.04f)
        {
            d.Properties.InsertVector3("Translation", new Vector3(0f, (float)(Math.Max(1.5, BASE_Y - peak * MAX_H - 3.2) - 1.4), 0f));
            d.Opacity = 0.45f;
        }
        else
        {
            d.Opacity = 0f;
        }
    }

    private void HookRendering()
    {
        if (_renderingHooked) return;
        _renderingHooked = true;
        _lastSpectrumTick = DateTime.UtcNow;
        CompositionTarget.Rendering += OnRendering;   // 每个显示帧一次，跟刷新率走
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked) return;
        _renderingHooked = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e) => StepSpectrumPhysics();

    private void StepSpectrumPhysics()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastSpectrumTick).TotalSeconds, 0.002, 0.05);
        _lastSpectrumTick = now;

        double riseAlpha = 1.0 - Math.Exp(-22.0 * dt);
        double fallAlpha = 1.0 - Math.Exp(-7.5 * dt);
        bool hasActiveEnergy = false;

        for (int i = 0; i < NUM_BARS; i++)
        {
            float tgt = _isPlaying ? _targets[i] : 0.04f;
            float cur = _values[i];

            if (tgt > cur)
                cur += (float)((tgt - cur) * riseAlpha);
            else
                cur += (float)((tgt - cur) * fallAlpha);

            cur = Math.Clamp(cur, 0.04f, 1.0f);
            _values[i] = cur;

            // 峰值保持与重力回落计算
            if (cur >= _peaks[i])
            {
                _peaks[i] = cur;
                _peakVels[i] = 0f;
            }
            else
            {
                _peakVels[i] += (float)(1.5 * dt);
                _peaks[i] = Math.Max(cur, _peaks[i] - (float)(_peakVels[i] * dt));
            }

            if (cur > 0.06f || _peaks[i] > 0.1f) hasActiveEnergy = true;

            ApplyBar(i, cur, _peaks[i]);
        }

        if (!hasActiveEnergy && !_isPlaying) UnhookRendering();
    }

    #endregion

    public void SetPlaying(bool isPlaying)
    {
        _isPlaying = isPlaying;

        if (isPlaying)
        {
            StartSpin();
            HookRendering();
        }
        else
        {
            StopSpin();
            for (int i = 0; i < NUM_BARS; i++) _targets[i] = 0.04f;
        }

        SlideDisc(isPlaying);

        // 频谱只在播放时出现：暂停 / 没有曲目时淡出隐藏
        var specVisual = ElementCompositionPreview.GetElementVisual(SpectrumCanvas);
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertExpressionKeyFrame(0f, "this.StartingValue");
        fade.InsertKeyFrame(1f, isPlaying ? 1f : 0f);
        fade.Duration = TimeSpan.FromMilliseconds(isPlaying ? 300 : 220);
        specVisual.StartAnimation("Opacity", fade);
    }

    private string? _lastTrackId;
    private string? _lastCoverPath;
    private int _swapSeq;

    private ScalarKeyFrameAnimation Scalar(float from, float to, int ms, CompositionEasingFunction ease)
    {
        var a = _compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, from);
        a.InsertKeyFrame(1f, to, ease);
        a.Duration = TimeSpan.FromMilliseconds(ms);
        return a;
    }

    private Vector3KeyFrameAnimation Scale3(float from, float to, int ms, CompositionEasingFunction ease)
    {
        var a = _compositor.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0f, new Vector3(from, from, 1f));
        a.InsertKeyFrame(1f, new Vector3(to, to, 1f), ease);
        a.Duration = TimeSpan.FromMilliseconds(ms);
        return a;
    }

    /// <summary>
    /// 封面交叉过渡：旧封面盖在上面，轻微放大并淡出；新封面从略大的尺寸收回原位并淡入。
    /// 唱片封套整体只做很轻的缩放，不再整块变暗。
    /// </summary>
    private void CrossfadeCover(ImageSource? oldSource, bool newHasCover)
    {
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
        const int ms = 560;

        var newV = ElementCompositionPreview.GetElementVisual(CoverImage);
        var oldV = ElementCompositionPreview.GetElementVisual(OldCoverImage);
        var size = new Vector3((float)JacketCard.ActualWidth / 2f, (float)JacketCard.ActualHeight / 2f, 0f);
        newV.CenterPoint = size;
        oldV.CenterPoint = size;

        if (oldSource != null)
        {
            OldCoverImage.Source = oldSource;
            OldCoverImage.Visibility = Visibility.Visible;
            var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            oldV.StartAnimation("Opacity", Scalar(1f, 0f, ms, ease));
            oldV.StartAnimation("Scale", Scale3(1f, 1.06f, ms, ease));
            batch.End();
            int seq = _swapSeq;
            batch.Completed += (s, e) =>
            {
                if (seq != _swapSeq) return;
                OldCoverImage.Visibility = Visibility.Collapsed;
                OldCoverImage.Source = null;
            };
        }

        if (newHasCover)
        {
            newV.StartAnimation("Opacity", Scalar(0f, 1f, ms, ease));
            newV.StartAnimation("Scale", Scale3(1.04f, 1f, ms + 120, ease));
        }

        _jacketVisual.StartAnimation("Scale", Scale3(0.985f, 1f, ms, ease));
    }

    /// <summary>标题 / 歌手 / 专辑：旧文字上移淡出，换成新文字后从下方上浮淡入。</summary>
    private void RollMeta(Action swap)
    {
        var easeOut = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
        var easeIn = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        int seq = _swapSeq;

        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _metaVisual.StartAnimation("Opacity", Scalar(_metaVisual.Opacity, 0f, 150, easeOut));
        _metaVisual.StartAnimation("Translation.Y", Scalar(0f, -8f, 150, easeOut));
        batch.End();
        batch.Completed += (s, e) =>
        {
            if (seq != _swapSeq) return;   // 期间又换了歌：交给最新那次
            swap();
            _metaVisual.StartAnimation("Opacity", Scalar(0f, 1f, 420, easeIn));
            _metaVisual.StartAnimation("Translation.Y", Scalar(10f, 0f, 460, easeIn));
        };
    }
    /// <summary>质量徽标：格式信息，疑似假无损时加一个警示后缀并在提示里说明原因。</summary>
    public void UpdateTrackBadge(TrackItem track)
    {
        if (string.IsNullOrEmpty(track.FormatBadge)) { QualityBadgeBorder.Visibility = Visibility.Collapsed; return; }
        QualityBadgeText.Text = track.FormatBadge;
        ToolTipService.SetToolTip(QualityBadgeBorder, track.IsSuspectFake ? track.AuthText : null);
        QualityBadgeBorder.Visibility = Visibility.Visible;
    }

    public void UpdateTrack(TrackItem? track)
    {
        string? id = track?.Id;
        string? cover = track != null && !string.IsNullOrEmpty(track.CoverPath) && File.Exists(track.CoverPath) ? track.CoverPath : null;
        bool trackChanged = id != _lastTrackId;
        bool coverChanged = cover != _lastCoverPath;
        _lastTrackId = id;
        _lastCoverPath = cover;
        _swapSeq++;

        // ---- 文字：换歌时滚动替换；同一首歌（比如补全了信息）直接替换 ----
        void SwapMeta()
        {
            if (track == null)
            {
                TitleText.Text = L.T("暂无播放曲目");
                ArtistText.Text = L.T("点击左侧栏的按钮导入本地音乐");
                AlbumText.Text = "";
                QualityBadgeBorder.Visibility = Visibility.Collapsed;
                return;
            }
            TitleText.Text = string.IsNullOrWhiteSpace(track.Title) ? L.T("未命名曲目") : track.Title;
            ArtistText.Text = string.IsNullOrWhiteSpace(track.Artist) ? "未知艺术家" : track.Artist;
            AlbumText.Text = string.IsNullOrWhiteSpace(track.Album) ? "" : track.Album;
            UpdateTrackBadge(track);
        }
        if (trackChanged && IsLoaded) RollMeta(SwapMeta);
        else SwapMeta();

        // ---- 封面：旧封面盖在上面淡出，新封面淡入 ----
        if (!coverChanged && !trackChanged) return;
        ImageSource? oldSource = CoverImage.Visibility == Visibility.Visible ? CoverImage.Source : null;

        bool hasCover = false;
        if (cover != null)
        {
            try
            {
                CoverImage.Source = new BitmapImage(new Uri(cover));
                CoverImage.Visibility = Visibility.Visible;
                FallbackPanel.Visibility = Visibility.Collapsed;
                hasCover = true;
            }
            catch { }
        }
        if (!hasCover)
        {
            CoverImage.Visibility = Visibility.Collapsed;
            FallbackPanel.Visibility = Visibility.Visible;
        }

        if (IsLoaded && coverChanged) CrossfadeCover(oldSource, hasCover);
    }

    public void UpdateSpectrum(float[] bands)
    {
        if (bands == null || bands.Length == 0) return;

        // 将输入的 FFT 声学能量插值映射至 20 根物理律动柱
        for (int i = 0; i < NUM_BARS; i++)
        {
            double srcIdx = i * (bands.Length - 1) / (double)(NUM_BARS - 1);
            int idx0 = (int)srcIdx;
            int idx1 = Math.Min(bands.Length - 1, idx0 + 1);
            double frac = srcIdx - idx0;
            float val = (float)(bands[idx0] * (1.0 - frac) + bands[idx1] * frac);
            _targets[i] = Math.Clamp(val, 0.0f, 1.0f);
        }

        HookRendering();
    }
}
