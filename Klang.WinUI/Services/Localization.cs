using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Klang_WinUI.Services;

namespace Klang_WinUI;

/// <summary>
/// 多语言：界面文字以简体中文原文为键，翻译放在 Languages/*.json（键 = 中文原文，值 = 译文）。
/// 没有译文的文字原样显示，所以新增文字不会因为缺翻译而出错。
/// 代码里的文字用 L.T / L.F；XAML 里写死的文字由 Apply 在运行时按同样的表替换（并能切回原文）。
/// 想加一门语言：把 en.json 复制一份改名（如 ja.json）并翻译，放进 Languages 文件夹即可。
/// </summary>
public static class L
{
    public const string Source = "zh-CN";

    public sealed record LangInfo(string Code, string Name);

    private static Dictionary<string, string> _map = new();

    /// <summary>当前界面语言代码（zh-CN 表示不翻译，直接显示原文）。</summary>
    public static string Code { get; private set; } = Source;
    public static bool IsSource => Code == Source;

    /// <summary>语言切换后触发（界面线程）。</summary>
    public static event Action? LanguageChanged;

    private static string LanguagesDir => Path.Combine(AppContext.BaseDirectory, "Languages");

    /// <summary>可选的语言：简体中文 + Languages 文件夹里的每个 json。</summary>
    public static IReadOnlyList<LangInfo> Available()
    {
        var list = new List<LangInfo> { new(Source, "简体中文") };
        try
        {
            if (Directory.Exists(LanguagesDir))
            {
                foreach (var f in Directory.GetFiles(LanguagesDir, "*.json").OrderBy(f => f))
                {
                    var code = Path.GetFileNameWithoutExtension(f);
                    string name = code;
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(f));
                        if (doc.RootElement.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } s) name = s;
                    }
                    catch { }
                    list.Add(new LangInfo(code, name));
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>启动时调用：按设置里保存的语言（auto = 跟随系统）加载。</summary>
    public static void Init()
    {
        Load(Resolve(StateService.Instance.State.Language));
    }

    private static string Resolve(string setting)
    {
        var avail = Available();
        if (!string.IsNullOrEmpty(setting) && setting != "auto")
            return avail.Any(a => a.Code.Equals(setting, StringComparison.OrdinalIgnoreCase)) ? setting : Source;

        // 跟随系统：完整代码优先，其次只比较语言部分（zh-TW 没有就退回 zh-CN，en-US 用 en）
        string sys = CultureInfo.CurrentUICulture.Name;
        var exact = avail.FirstOrDefault(a => a.Code.Equals(sys, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Code;
        string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var partial = avail.FirstOrDefault(a => a.Code.Split('-')[0].Equals(lang, StringComparison.OrdinalIgnoreCase));
        if (partial != null) return partial.Code;
        return lang == "zh" ? Source : (avail.Any(a => a.Code == "en") ? "en" : Source);
    }

    // 所有语言包里 “译文 → 中文原文” 的反查表：
    // 启动时代码里用 L.T 生成的文字已经是当时语言的译文了，切换语言时要能从译文找回原文再翻成新语言。
    private static Dictionary<string, string> _reverse = new(StringComparer.Ordinal);

    private static void BuildReverse()
    {
        var rev = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(LanguagesDir))
            {
                foreach (var f in Directory.GetFiles(LanguagesDir, "*.json"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(f));
                        if (!doc.RootElement.TryGetProperty("strings", out var strings)) continue;
                        foreach (var p in strings.EnumerateObject())
                            if (p.Value.GetString() is { Length: > 0 } v) rev.TryAdd(v, p.Name);
                    }
                    catch { }
                }
            }
        }
        catch { }
        _reverse = rev;
    }

    private static void Load(string code)
    {
        if (_reverse.Count == 0) BuildReverse();
        if (code != Source) _everTranslated = true;   // 启动语言不是中文：代码里生成的文字已是译文，之后切语言要能改回去
        var map = new Dictionary<string, string>();
        if (code != Source)
        {
            try
            {
                var path = Path.Combine(LanguagesDir, code + ".json");
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("strings", out var strings))
                    foreach (var p in strings.EnumerateObject())
                        if (p.Value.GetString() is { Length: > 0 } v) map[p.Name] = v;
            }
            catch
            {
                code = Source;
                map.Clear();
            }
        }
        _map = map;
        Code = code;
    }

    /// <summary>切换语言（setting 为语言代码或 "auto"），保存并通知界面刷新。</summary>
    public static void SetLanguage(string setting)
    {
        StateService.Instance.State.Language = setting;
        StateService.Instance.RequestSave();
        Load(Resolve(setting));
        LanguageChanged?.Invoke();
    }

    /// <summary>翻译一句。没有译文时原样返回。</summary>
    public static string T(string zh)
    {
        if (IsSource || string.IsNullOrEmpty(zh)) return zh;
        return _map.TryGetValue(zh, out var v) ? v : zh;
    }

    /// <summary>翻译带占位符的句子再格式化：L.F("共 {0} 首", n)。</summary>
    public static string F(string zhFormat, params object?[] args)
    {
        try { return string.Format(T(zhFormat), args); }
        catch (FormatException) { return string.Format(zhFormat, args); }
    }

    private static string? Lookup(string key)
    {
        if (_map.TryGetValue(key, out var v)) return v;
        var t = key.Trim();
        return t != key && _map.TryGetValue(t, out v) ? key.Replace(t, v) : null;
    }

    // =====================================================================
    //  XAML 里写死的文字：运行时遍历界面，按表替换（记住原文，可以切回去）
    // =====================================================================

    private static readonly ConditionalWeakTable<object, Dictionary<string, (string Orig, string Tr)>> Records = new();
    private static bool _everTranslated;

    private static void Do(object owner, string prop, string? cur, Action<string> set)
    {
        if (string.IsNullOrEmpty(cur)) return;
        Records.TryGetValue(owner, out var rec);
        (string Orig, string Tr) r = default;
        bool has = rec != null && rec.TryGetValue(prop, out r);

        // 还原成中文原文：优先用记录；否则如果当前文字是某个语言包里的译文，就反查回原文
        string key = has && cur == r.Tr ? r.Orig : (_reverse.TryGetValue(cur, out var zh) ? zh : cur);
        string target = IsSource ? key : (Lookup(key) ?? key);

        if (target != cur) set(target);
        if (target != key)
        {
            rec ??= Records.GetOrCreateValue(owner);
            rec[prop] = (key, target);
            _everTranslated = true;
        }
        else rec?.Remove(prop);
    }

    /// <summary>翻译（或还原）root 下面所有写死的文字。</summary>
    public static void Apply(object? root)
    {
        if (root == null) return;
        if (IsSource && !_everTranslated) return;   // 一直是中文，没有任何需要还原的
        try { Visit(root, new HashSet<object>(ReferenceEqualityComparer.Instance), 0); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Localization walk failed: " + ex.Message); }   // 翻译失败不能影响界面本身
    }

    private static void Visit(object? node, HashSet<object> seen, int depth)
    {
        if (node == null || depth > 80 || !seen.Add(node)) return;

        switch (node)
        {
            case Window w:
                Do(w, "Title", w.Title, s => w.Title = s);
                Visit(w.Content, seen, depth + 1);
                return;

            case ContentDialog dlg:
                if (dlg.Title is string t) Do(dlg, "Title", t, s => dlg.Title = s);
                Do(dlg, "Primary", dlg.PrimaryButtonText, s => dlg.PrimaryButtonText = s);
                Do(dlg, "Secondary", dlg.SecondaryButtonText, s => dlg.SecondaryButtonText = s);
                Do(dlg, "Close", dlg.CloseButtonText, s => dlg.CloseButtonText = s);
                Visit(dlg.Content, seen, depth + 1);
                return;

            // ---- 菜单（不在可视树里，要显式进入）----
            case MenuFlyout mf:
                foreach (var it in mf.Items) Visit(it, seen, depth + 1);
                return;
            case MenuFlyoutSubItem sub:
                Do(sub, "Text", sub.Text, s => sub.Text = s);
                foreach (var it in sub.Items) Visit(it, seen, depth + 1);
                return;
            case MenuFlyoutItem mi:
                Do(mi, "Text", mi.Text, s => mi.Text = s);
                return;
            case Flyout fl:
                Visit(fl.Content, seen, depth + 1);
                return;
        }

        if (node is not DependencyObject o) return;

        switch (o)
        {
            case TextBlock tb:
                Do(tb, "Text", tb.Text, s => tb.Text = s);
                foreach (var inl in tb.Inlines)
                    if (inl is Run run) Do(run, "Text", run.Text, s => run.Text = s);
                break;
            case TextBox box:
                Do(box, "Placeholder", box.PlaceholderText, s => box.PlaceholderText = s);
                if (box.Header is string th) Do(box, "Header", th, s => box.Header = s);
                break;
            case AutoSuggestBox asb:
                Do(asb, "Placeholder", asb.PlaceholderText, s => asb.PlaceholderText = s);
                break;
            case ComboBox cb:
                Do(cb, "Placeholder", cb.PlaceholderText, s => cb.PlaceholderText = s);
                if (cb.Header is string ch) Do(cb, "Header", ch, s => cb.Header = s);
                break;
            case ToggleSwitch ts:
                if (ts.OnContent is string on) Do(ts, "On", on, s => ts.OnContent = s);
                if (ts.OffContent is string off) Do(ts, "Off", off, s => ts.OffContent = s);
                if (ts.Header is string tsh) Do(ts, "Header", tsh, s => ts.Header = s);
                break;
            case Slider sl:
                if (sl.Header is string sh) Do(sl, "Header", sh, s => sl.Header = s);
                break;
            case SelectorBarItem sbi:
                Do(sbi, "Text", sbi.Text, s => sbi.Text = s);
                break;
            case ContentControl cc when cc.Content is string str:
                Do(cc, "Content", str, s => cc.Content = s);
                break;
        }

        // 提示文字 / 无障碍名称
        if (o is FrameworkElement fe)
        {
            if (ToolTipService.GetToolTip(fe) is string tip) Do(fe, "ToolTip", tip, s => ToolTipService.SetToolTip(fe, s));
            var an = AutomationProperties.GetName(fe);
            if (!string.IsNullOrEmpty(an)) Do(fe, "AutoName", an, s => AutomationProperties.SetName(fe, s));
        }

        // ---- 往下走：可视子树 + 还没加载时的逻辑子节点 ----
        // 还没加载进界面的元素问可视子节点会抛 COM 异常，这种情况下只走下面的逻辑子节点
        try
        {
            int n = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++) Visit(VisualTreeHelper.GetChild(o, i), seen, depth + 1);
        }
        catch (System.Runtime.InteropServices.COMException) { }
        catch (InvalidOperationException) { }

        switch (o)
        {
            case Panel p: foreach (var c in p.Children) Visit(c, seen, depth + 1); break;
            case Border b: Visit(b.Child, seen, depth + 1); break;
            case ContentControl cc2 when cc2.Content is not string: Visit(cc2.Content, seen, depth + 1); break;
            case NavigationView nv:
                foreach (var it in nv.MenuItems) Visit(it, seen, depth + 1);
                foreach (var it in nv.FooterMenuItems) Visit(it, seen, depth + 1);
                break;
        }
        if (o is ItemsControl ic)
            foreach (var it in ic.Items) if (it is DependencyObject) Visit(it, seen, depth + 1);

        if (o is Button btn && btn.Flyout != null) Visit(btn.Flyout, seen, depth + 1);
        if (o is UIElement ue && ue.ContextFlyout != null) Visit(ue.ContextFlyout, seen, depth + 1);
    }

    // ---- 自动刷新：界面布局变化后（防抖）再扫一遍，覆盖动态生成的内容 ----
    private static readonly HashSet<WeakReference<FrameworkElement>> Roots = new();

    /// <summary>让某个窗口 / 页面的根元素在语言切换和内容变化后自动翻译。</summary>
    public static void Attach(FrameworkElement root, Window? window = null)
    {
        Apply(root);
        if (window != null) Apply(window);

        var queue = root.DispatcherQueue;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? timer = null;
        void Schedule()
        {
            if (IsSource && !_everTranslated) return;
            timer ??= CreateTimer(queue, () => { Apply(root); if (window != null) Apply(window); });
            timer.Stop();
            timer.Start();
        }

        root.LayoutUpdated += (s, e) => Schedule();
        LanguageChanged += () =>
        {
            queue.SafeEnqueue(() => { Apply(root); if (window != null) Apply(window); });
        };
    }

    private static Microsoft.UI.Dispatching.DispatcherQueueTimer CreateTimer(Microsoft.UI.Dispatching.DispatcherQueue q, Action tick)
    {
        var t = q.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(250);
        t.IsRepeating = false;
        t.Tick += (s, e) => tick();
        return t;
    }
}
