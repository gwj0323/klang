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

    #region 主题感知的资源查找

    /// <summary>
    /// 在代码里取随主题变化的系统画刷。Application.Current.Resources[...] 只按“应用主题”取值，
    /// 窗口内容强制深 / 浅色时会取错（深色里出现黑字），这里按元素的实际主题查字典。
    /// </summary>
    public static Brush? Brush(FrameworkElement scope, string key)
    {
        string themeKey = scope.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        return Find(Application.Current.Resources, key, themeKey) as Brush;
    }

    private static object? Find(ResourceDictionary dict, string key, string themeKey)
    {
        if (dict.ThemeDictionaries.TryGetValue(themeKey, out var td) && td is ResourceDictionary themed)
        {
            if (themed.TryGetValue(key, out var v)) return v;
            foreach (var m in themed.MergedDictionaries)
            {
                var r = Find(m, key, themeKey);
                if (r != null) return r;
            }
        }
        if (dict.TryGetValue(key, out var direct)) return direct;
        foreach (var m in dict.MergedDictionaries)
        {
            var r = Find(m, key, themeKey);
            if (r != null) return r;
        }
        return null;
    }

    #endregion
}
