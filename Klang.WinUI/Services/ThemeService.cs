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
    {
        var c = _ui.GetColorValue(scope.ActualTheme == ElementTheme.Dark
            ? Windows.UI.ViewManagement.UIColorType.AccentLight3
            : Windows.UI.ViewManagement.UIColorType.AccentDark1);
        return new SolidColorBrush(c);
    }

    /// <summary>
    /// 强调色填充（频谱柱等色块）：深色主题用浅一档（Light2），浅色主题用深一档（Dark1），和系统控件的强调色一致。
    /// 直接按 UISettings 取，不依赖资源字典里的画刷（那个在代码里取出来会是浅色主题的值）。
    /// </summary>
    public static Brush AccentFill(FrameworkElement scope)
    {
        var c = _ui.GetColorValue(scope.ActualTheme == ElementTheme.Dark
            ? Windows.UI.ViewManagement.UIColorType.AccentLight2
            : Windows.UI.ViewManagement.UIColorType.AccentDark1);
        return new SolidColorBrush(c);
    }

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
