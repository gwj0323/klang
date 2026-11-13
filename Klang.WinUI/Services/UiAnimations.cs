using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Klang_WinUI.Services;

/// <summary>统一的界面动画：显隐淡入淡出、内容切换的淡入上浮，用合成动画（不触发布局）。</summary>
public static class UiAnimations
{
    private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(140);

    private static CubicBezierEasingFunction EaseOut(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

    private static ScalarKeyFrameAnimation Fade(Compositor c, float from, float to, TimeSpan d)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, from);
        a.InsertKeyFrame(1f, to, EaseOut(c));
        a.Duration = d;
        return a;
    }

    private static ScalarKeyFrameAnimation Rise(Compositor c, float dy, TimeSpan d)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, dy);
        a.InsertKeyFrame(1f, 0f, EaseOut(c));
        a.Duration = d;
        return a;
    }

    /// <summary>给元素挂上隐式显隐动画：之后改 Visibility 就会自动淡入上浮 / 淡出。</summary>
    public static void AttachFade(UIElement element, float offsetY = 10f)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        var show = c.CreateAnimationGroup();
        var fadeIn = Fade(c, 0f, 1f, ShowDuration);
        fadeIn.Target = "Opacity";
        show.Add(fadeIn);
        if (offsetY != 0f)
        {
            var rise = Rise(c, offsetY, ShowDuration);
            rise.Target = "Translation.Y";
            show.Add(rise);
        }

        var fadeOut = Fade(c, 1f, 0f, HideDuration);
        fadeOut.Target = "Opacity";

        ElementCompositionPreview.SetImplicitShowAnimation(element, show);
        ElementCompositionPreview.SetImplicitHideAnimation(element, fadeOut);
    }

    /// <summary>元素整体淡入（毫秒）。</summary>
    public static void FadeIn(UIElement element, int ms)
    {
        var v = ElementCompositionPreview.GetElementVisual(element);
        v.StartAnimation("Opacity", Fade(v.Compositor, 0f, 1f, TimeSpan.FromMilliseconds(ms)));
    }

    /// <summary>元素整体淡出（毫秒），结束后保持透明。</summary>
    public static void FadeOut(UIElement element, int ms)
    {
        var v = ElementCompositionPreview.GetElementVisual(element);
        v.StartAnimation("Opacity", Fade(v.Compositor, v.Opacity, 0f, TimeSpan.FromMilliseconds(ms)));
    }

    /// <summary>
    /// 先淡出，淡出完成后执行 swap（替换内容），再淡入上浮。用于换歌时封面 / 标题等整块内容的过渡。
    /// </summary>
    public static void FadeThrough(UIElement element, Action swap, int outMs = 160, int inMs = 380, float offsetY = 10f)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Opacity", Fade(c, visual.Opacity, 0f, TimeSpan.FromMilliseconds(outMs)));
        batch.End();
        batch.Completed += (s, e) =>
        {
            swap();
            visual.StartAnimation("Opacity", Fade(c, 0f, 1f, TimeSpan.FromMilliseconds(inMs)));
            if (offsetY != 0f) visual.StartAnimation("Translation.Y", Rise(c, offsetY, TimeSpan.FromMilliseconds(inMs)));
        };
    }

    private static Visual Centered(FrameworkElement el)
    {
        var v = ElementCompositionPreview.GetElementVisual(el);
        v.CenterPoint = new Vector3((float)el.ActualWidth / 2f, (float)el.ActualHeight / 2f, 0f);
        return v;
    }

    /// <summary>弹跳：从 from 放大到 peak 再回到 1（点收藏时的红心）。</summary>
    public static void Bounce(FrameworkElement el, float from = 0.6f, float peak = 1.28f, int ms = 420)
    {
        var v = Centered(el);
        var c = v.Compositor;
        var a = c.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0f, new Vector3(from, from, 1f));
        a.InsertKeyFrame(0.45f, new Vector3(peak, peak, 1f), c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.3f, 1f)));
        a.InsertKeyFrame(0.75f, new Vector3(0.94f, 0.94f, 1f), c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f)));
        a.InsertKeyFrame(1f, Vector3.One, c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(ms);
        v.StartAnimation("Scale", a);
    }

    /// <summary>转一圈（切换循环模式）。</summary>
    public static void Spin(FrameworkElement el, float degrees = 360f, int ms = 480)
    {
        var v = Centered(el);
        var c = v.Compositor;
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, 0f);
        a.InsertKeyFrame(1f, degrees, c.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0.1f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(ms);
        v.StartAnimation("RotationAngleInDegrees", a);
    }

    /// <summary>从一侧滑入并淡入（切换随机 / 漫游模式时图标替换）。</summary>
    public static void SlideIn(FrameworkElement el, float dx = -8f, int ms = 300)
    {
        var v = ElementCompositionPreview.GetElementVisual(el);
        var c = v.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(el, true);
        var d = TimeSpan.FromMilliseconds(ms);
        var x = c.CreateScalarKeyFrameAnimation();
        x.InsertKeyFrame(0f, dx);
        x.InsertKeyFrame(1f, 0f, EaseOut(c));
        x.Duration = d;
        v.StartAnimation("Translation.X", x);
        v.StartAnimation("Opacity", Fade(c, 0f, 1f, d));
    }

    /// <summary>把元素缩放到指定倍率（按下 / 松开的反馈）。</summary>
    public static void ScaleTo(FrameworkElement el, float scale, int ms = 140)
    {
        var v = Centered(el);
        var c = v.Compositor;
        var a = c.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), EaseOut(c));
        a.Duration = TimeSpan.FromMilliseconds(ms);
        v.StartAnimation("Scale", a);
    }

    /// <summary>透明度过渡到指定值。</summary>
    public static void OpacityTo(UIElement el, float to, int ms = 160)
    {
        var v = ElementCompositionPreview.GetElementVisual(el);
        var c = v.Compositor;
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, to, EaseOut(c));
        a.Duration = TimeSpan.FromMilliseconds(ms);
        v.StartAnimation("Opacity", a);
    }

    /// <summary>延迟一段时间后淡入上浮（列表项依次出现）。</summary>
    public static void StaggerIn(UIElement element, int delayMs, float offsetY = 12f, int ms = 320)
    {
        var v = ElementCompositionPreview.GetElementVisual(element);
        var c = v.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var d = TimeSpan.FromMilliseconds(ms);
        var delay = TimeSpan.FromMilliseconds(delayMs);

        var op = Fade(c, 0f, 1f, d);
        op.DelayTime = delay;
        op.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var y = Rise(c, offsetY, d);
        y.DelayTime = delay;
        y.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Opacity", op);
        v.StartAnimation("Translation.Y", y);
    }

    /// <summary>内容整体换了（切换标签 / 打开歌单）时播放一次淡入上浮。</summary>
    public static void Pulse(UIElement element, float offsetY = 10f)
    {
        if (element.Visibility != Visibility.Visible) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.StartAnimation("Opacity", Fade(c, 0f, 1f, ShowDuration));
        if (offsetY != 0f) visual.StartAnimation("Translation.Y", Rise(c, offsetY, ShowDuration));
    }
}
