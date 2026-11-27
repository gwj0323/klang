using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Klang_WinUI.Services;

public static class ThemeService
{
    public static ElementTheme ToElementTheme(string theme) => theme switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    /// <summary>
    /// 当前设置的主题。代码里新建的 ContentDialog 显示在弹出层，不继承窗口内容的 RequestedTheme，
    /// 必须把它设成这个值，否则系统是浅色、应用设成深色时对话框仍是白底。
    /// </summary>
    public static ElementTheme Current => ToElementTheme(StateService.Instance.State.Theme);

    /// <summary>把保存的主题 (system | light | dark) 应用到窗口内容，并同步标题栏按钮颜色。</summary>
    public static void Apply(Window? window, string theme)
    {
        if (window?.Content is not FrameworkElement root) return;
        root.RequestedTheme = ToElementTheme(theme);
        ApplyTitleBarColors(window, root);

        // 跟随系统时，系统明暗切换也要刷新标题栏按钮
        if (!_hooked.Contains(window))
        {
            _hooked.Add(window);
            root.ActualThemeChanged += (s, e) => ApplyTitleBarColors(window, root);
        }
    }

    private static readonly HashSet<Window> _hooked = new();

    /// <summary>
    /// 自绘标题栏时，最小化 / 最大化 / 关闭按钮的颜色由系统按“应用主题”决定，
    /// 与窗口内容的 RequestedTheme 无关，所以要按实际主题手动设置，否则深色下按钮会和背景同色。
    /// </summary>
    private static void ApplyTitleBarColors(Window window, FrameworkElement root)
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;
            var tb = window.AppWindow.TitleBar;
            bool dark = root.ActualTheme == ElementTheme.Dark;

            Color fg = dark ? Colors.White : Color.FromArgb(255, 26, 26, 26);
            Color fgInactive = dark ? Color.FromArgb(255, 140, 140, 140) : Color.FromArgb(255, 150, 150, 150);
            Color hoverBg = dark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
            Color pressedBg = dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0);

            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor = fg;
            tb.ButtonInactiveForegroundColor = fgInactive;
            tb.ButtonHoverForegroundColor = fg;
            tb.ButtonHoverBackgroundColor = hoverBg;
            tb.ButtonPressedForegroundColor = fg;
            tb.ButtonPressedBackgroundColor = pressedBg;
        }
        catch { }
    }

    private static readonly Windows.UI.ViewManagement.UISettings _ui = new();

    /// <summary>强调色文字：深色主题用浅一档的系统强调色，浅色主题用深一档，保证对比度。</summary>
    public static Brush AccentText(FrameworkElement scope)
        => new SolidColorBrush(AccentShade(scope.ActualTheme == ElementTheme.Dark ? 6 : 2));

    /// <summary>
    /// 强调色填充（频谱柱等色块）：深色主题用浅一档（Light2），浅色主题用深一档（Dark1），和系统控件的强调色一致。
    /// 直接按 UISettings 取，不依赖资源字典里的画刷（那个在代码里取出来会是浅色主题的值）。
    /// </summary>
    public static Brush AccentFill(FrameworkElement scope)
        => new SolidColorBrush(AccentShade(scope.ActualTheme == ElementTheme.Dark ? 5 : 2));

    #region 临时强调色（放初音的歌时换成葱绿色）

    // 七档强调色：0 Dark3、1 Dark2、2 Dark1、3 Accent、4 Light1、5 Light2、6 Light3（和 UISettings 的档位对应）
    private static readonly Windows.UI.ViewManagement.UIColorType[] ShadeTypes =
    {
        Windows.UI.ViewManagement.UIColorType.AccentDark3, Windows.UI.ViewManagement.UIColorType.AccentDark2,
        Windows.UI.ViewManagement.UIColorType.AccentDark1, Windows.UI.ViewManagement.UIColorType.Accent,
        Windows.UI.ViewManagement.UIColorType.AccentLight1, Windows.UI.ViewManagement.UIColorType.AccentLight2,
        Windows.UI.ViewManagement.UIColorType.AccentLight3,
    };

    private static Color[]? _overrideShades;   // 目标强调色的七档；null = 用系统强调色
    private static Color[]? _fromShades;       // 渐变起点（切换那一刻的颜色）
    private static double _blend = 1;          // 0 → 1：从起点渐变到目标
    private static readonly System.Diagnostics.Stopwatch _blendClock = new();
    private const double BlendSeconds = 0.6;
    private static bool _blendHooked;

    /// <summary>临时强调色变了（渐变中每帧都会触发）：自己画强调色的控件要重新取色。</summary>
    public static event Action? AccentChanged;

    /// <summary>当前第 i 档强调色（渐变中取中间色）。</summary>
    private static Color AccentShade(int i)
    {
        var target = _overrideShades?[i] ?? _ui.GetColorValue(ShadeTypes[i]);
        if (_blend >= 1 || _fromShades == null) return target;
        return Lerp(_fromShades[i], target, _blend);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = t * t * (3 - 2 * t);
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    /// <summary>由一个颜色推出七档：往白色混是浅档，往黑色混是深档。</summary>
    private static Color[] ShadesOf(Color c)
    {
        Color Mix(Color to, double k) => Color.FromArgb(255,
            (byte)Math.Round(c.R + (to.R - c.R) * k), (byte)Math.Round(c.G + (to.G - c.G) * k), (byte)Math.Round(c.B + (to.B - c.B) * k));
        return new[]
        {
            Mix(Colors.Black, 0.55), Mix(Colors.Black, 0.38), Mix(Colors.Black, 0.2), c,
            Mix(Colors.White, 0.2), Mix(Colors.White, 0.38), Mix(Colors.White, 0.56),
        };
    }

    /// <summary>
    /// 把强调色临时换成 <paramref name="color"/>（null = 恢复系统强调色），0.6 秒渐变。
    /// 系统控件用的强调色画刷（滑块、开关、进度条、选中条等）都是资源字典里共用的同一批画刷对象，
    /// 直接改这些画刷的颜色就能全局生效；自己画的部分通过 <see cref="AccentChanged"/> 重新取色。
    /// </summary>
    public static void SetAccentOverride(Color? color)
    {
        var target = color is { } c ? ShadesOf(c) : null;
        if (target == null && _overrideShades == null) return;
        if (target != null && _overrideShades != null && target[3] == _overrideShades[3]) return;
        CollectAccentBrushes();
        var from = new Color[7];
        for (int i = 0; i < 7; i++) from[i] = AccentShade(i);
        _fromShades = from;
        _overrideShades = target;
        _blend = 0;
        _blendClock.Restart();
        if (!_blendHooked)
        {
            _blendHooked = true;
            CompositionTarget.Rendering += OnBlendFrame;
        }
    }

    private static void OnBlendFrame(object? sender, object e)
    {
        _blend = Math.Min(1, _blendClock.Elapsed.TotalSeconds / BlendSeconds);
        ApplyAccentBrushes();
        AccentChanged?.Invoke();
        if (_blend >= 1)
        {
            _blendHooked = false;
            CompositionTarget.Rendering -= OnBlendFrame;
            _fromShades = null;
        }
    }

    // 资源字典里颜色等于某档系统强调色的画刷，以及它是第几档
    private static List<(SolidColorBrush Brush, int Shade)>? _accentBrushes;

    // 系统控件用到的强调色画刷（Fluent 资源里的名字）
    private static readonly string[] AccentBrushKeys =
    {
        "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
        "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush",
        "AccentAAFillColorDefaultBrush", "AccentAAFillColorSecondaryBrush", "AccentAAFillColorTertiaryBrush",
        "SystemControlHighlightAccentBrush", "SystemControlForegroundAccentBrush", "SystemControlBackgroundAccentBrush",
        "SystemControlHighlightAltAccentBrush", "SystemControlHyperlinkTextBrush", "SystemControlHighlightListAccentLowBrush",
        "SystemControlHighlightListAccentMediumBrush", "SystemControlHighlightListAccentHighBrush",
    };

    /// <summary>
    /// 按名字在浅色、深色两套主题字典里取强调色画刷，记下它是第几档。
    /// 注意不能遍历整个资源字典：那样会把当时的主题资源全部实例化并固定住，之后切换深浅色时
    /// 对话框背景等不再跟着变（实测：浅色切深色后设置对话框还是白底）。按名字 TryGetValue 没有这个问题。
    /// </summary>
    private static void CollectAccentBrushes()
    {
        if (_accentBrushes != null) return;
        _accentBrushes = new();
        var shades = new Color[7];
        for (int i = 0; i < 7; i++) shades[i] = _ui.GetColorValue(ShadeTypes[i]);
        var seen = new HashSet<SolidColorBrush>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var key in AccentBrushKeys)
        {
            if (FindThemed(Application.Current.Resources, key, theme, 0) is not SolidColorBrush b || !seen.Add(b)) continue;
            for (int i = 0; i < 7; i++)
                if (b.Color.R == shades[i].R && b.Color.G == shades[i].G && b.Color.B == shades[i].B) { _accentBrushes.Add((b, i)); break; }
        }
    }

    /// <summary>只在指定主题的字典里按名字找（不走字典自己按“应用主题”的取值）。</summary>
    private static object? FindThemed(ResourceDictionary dict, string key, string themeKey, int depth)
    {
        if (depth > 8) return null;
        foreach (var k in themeKey == "Dark" ? new[] { "Dark", "Default" } : new[] { themeKey })
        {
            if (dict.ThemeDictionaries.TryGetValue(k, out var td) && td is ResourceDictionary themed)
            {
                if (themed.TryGetValue(key, out var v)) return v;
                foreach (var m in themed.MergedDictionaries)
                    if (FindThemed(m, key, themeKey, depth + 1) is { } r) return r;
            }
        }
        foreach (var m in dict.MergedDictionaries)
            if (FindThemed(m, key, themeKey, depth + 1) is { } r) return r;
        return null;
    }

    private static void ApplyAccentBrushes()
    {
        if (_accentBrushes == null) return;
        var now = new Color[7];
        for (int i = 0; i < 7; i++) now[i] = AccentShade(i);
        foreach (var (b, i) in _accentBrushes)
        {
            // 保留画刷原来的透明度（有的强调色画刷带 alpha）
            var c = now[i];
            b.Color = Color.FromArgb(b.Color.A, c.R, c.G, c.B);
        }
    }

    #endregion

    #region 主题感知的资源查找

    /// <summary>
    /// 在代码里取随主题变化的系统画刷。Application.Current.Resources[...] 只按“应用主题”取值，
    /// 窗口内容强制深 / 浅色时会取错（深色里出现黑字），这里按元素的实际主题查字典。
    /// </summary>
    public static Brush? Brush(FrameworkElement scope, string key)
    {
        string themeKey = scope.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";

        // 优先取“颜色”本身：主题字典里的 XxxBrush 画刷，它的颜色是 {ThemeResource Xxx} 引用，
        // 从字典里取出来时会按“应用主题”（系统浅色时是浅色）去解析——于是窗口强制深色时拿到黑色。
        // 直接取字典里写死的颜色值再自己建画刷，就不受应用主题影响。
        string colorKey = key.EndsWith("Brush", StringComparison.Ordinal) ? key[..^5] : key;
        if (Find(Application.Current.Resources, colorKey, themeKey) is Windows.UI.Color c)
            return new SolidColorBrush(c);

        return Find(Application.Current.Resources, key, themeKey) as Brush;
    }

    private static object? Find(ResourceDictionary dict, string key, string themeKey)
    {
        // WinUI 的主题字典键是 Default（= 深色）/ Light / HighContrast，并没有叫 "Dark" 的字典
        foreach (var k in themeKey == "Dark" ? new[] { "Dark", "Default" } : new[] { themeKey })
        {
            if (dict.ThemeDictionaries.TryGetValue(k, out var td) && td is ResourceDictionary themed)
            {
                if (themed.TryGetValue(key, out var v)) return v;
                foreach (var m in themed.MergedDictionaries)
                {
                    var r = Find(m, key, themeKey);
                    if (r != null) return r;
                }
            }
        }
        // 先把合并进来的字典（包括系统控件资源）里各自的“主题字典”都找一遍；
        // 字典自己的 TryGetValue 会按“应用主题”取值（系统浅色时拿到浅色值），所以只能放在最后兜底。
        foreach (var m in dict.MergedDictionaries)
        {
            var r = Find(m, key, themeKey);
            if (r != null) return r;
        }
        if (dict.TryGetValue(key, out var direct)) return direct;
        return null;
    }

    #endregion
}
