using System;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Windows.UI;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Dialogs;

/// <summary>“音质分析”对话框：给出无损真伪检测的结论，并把平均频谱画出来，让人自己对照判断。</summary>
public static class QualityDialog
{
    public static async Task ShowAsync(XamlRoot root, TrackItem track)
    {
        var holder = new Grid { MinWidth = 500, MinHeight = 260 };
        var ring = new ProgressRing { IsActive = true, Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        holder.Children.Add(ring);

        var dlg = new ContentDialog
        {
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            RequestedTheme = ThemeService.Current,
            XamlRoot = root,
            Title = L.T("音质分析"),
            Content = holder,
            CloseButtonText = L.T("关闭"),
            DefaultButton = ContentDialogButton.Close
        };

        // 对话框显示出来的同时在后台分析（三段各 8 秒，通常一两秒）
        _ = RunAsync(root, dlg, holder, track);
        await dlg.ShowAsync();
    }

    private static async Task RunAsync(XamlRoot root, ContentDialog dlg, Grid holder, TrackItem track)
    {
        AuthResult? r = null;
        string? error = null;
        try { r = await Task.Run(() => QualityAnalysisService.Analyze(track.FilePath, track.Duration, track.BitDepth)); }
        catch (Exception ex) { error = ex.Message; }

        holder.Children.Clear();
        if (r == null)
        {
            holder.Children.Add(new TextBlock
            {
                Text = error != null ? L.T("分析失败：") + error : L.T("文件过短或无法解码，无法分析。"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            return;
        }

        // 同步到曲库里的记录（这样列表上的警示和筛选也会更新）
        track.AuthCutoffHz = r.CutoffHz; track.AuthBits = r.EffectiveBits; track.AuthVerdict = r.Verdict;
        LibraryService.Instance.SaveLibrary();

        var panel = new StackPanel { Spacing = 8, MinWidth = 500 };
        var fe = root.Content as FrameworkElement;
        var secondary = fe != null ? ThemeService.Brush(fe, "TextFillColorSecondaryBrush") : null;

        bool suspect = r.Verdict != "ok";
        panel.Children.Add(new TextBlock
        {
            Text = (suspect ? "⚠ " : "✓ ") + track.AuthText,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = suspect && fe != null ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 232, 140, 30)) : null
        });

        string detail = L.F("文件标称：{0}", track.FormatBadge) + "\n"
            + L.F("采样率 {0} Hz，有效位深 {1} bit", r.SampleRate, r.EffectiveBits) + "\n"
            + (r.CutoffHz > 0 ? L.F("频谱在约 {0:0.0} kHz 处截止（奈奎斯特频率 {1:0.0} kHz）", r.CutoffHz / 1000f, r.NyquistHz / 1000f)
                              : L.F("频谱延伸至接近奈奎斯特频率（{0:0.0} kHz），未发现截断", r.NyquistHz / 1000f));
        panel.Children.Add(new TextBlock { Text = detail, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = secondary });

        bool dark = fe?.ActualTheme == ElementTheme.Dark;
        var canvas = new CanvasControl { Height = 190, ClearColor = Colors.Transparent };
        canvas.Draw += (s, a) => DrawSpectrum(a.DrawingSession, (float)s.ActualWidth, (float)s.ActualHeight, r, dark);
        panel.Children.Add(canvas);

        panel.Children.Add(new TextBlock
        {
            Text = L.T("横轴为频率。若高频在 16~20 kHz 附近陡然截断，通常是由 MP3 / AAC 转码而来。结果仅供参考。"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = secondary
        });
        holder.Children.Add(panel);
        L.Apply(holder);
    }

    private static void DrawSpectrum(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, float w, float h, AuthResult r, bool dark)
    {
        var fg = dark ? Colors.White : Color.FromArgb(255, 30, 30, 30);
        var grid = Color.FromArgb(40, fg.R, fg.G, fg.B);
        var text = Color.FromArgb(150, fg.R, fg.G, fg.B);
        float left = 34, right = 8, top = 8, bottom = 22;
        float pw = w - left - right, ph = h - top - bottom;

        // 纵轴：取数据里的最大值往下 100 dB
        float max = float.MinValue; foreach (var v in r.SpectrumDb) if (v > max) max = v;
        float min = max - 110f;
        float Y(float db) => top + ph * (1 - Math.Clamp((db - min) / (max - min), 0, 1));
        float X(float hz) => left + pw * Math.Clamp(hz / r.NyquistHz, 0, 1);

        var fmt = new CanvasTextFormat { FontSize = 10 };
        // 横向网格 + 频率刻度
        for (int k = 0; k <= 5; k++)
        {
            float hz = r.NyquistHz * k / 5f;
            ds.DrawLine(X(hz), top, X(hz), top + ph, grid, 1f);
            ds.DrawText($"{hz / 1000f:0.#}k", X(hz) - 10, top + ph + 4, text, fmt);
        }
        for (int k = 0; k <= 4; k++)
        {
            float db = max - (max - min) * k / 4f;
            ds.DrawLine(left, Y(db), left + pw, Y(db), grid, 1f);
            ds.DrawText($"{db - max:0}", 2, Y(db) - 6, text, fmt);
        }

        // 频谱曲线
        using var pb = new CanvasPathBuilder(ds);
        for (int i = 0; i < r.SpectrumDb.Length; i++)
        {
            float x = left + pw * i / (r.SpectrumDb.Length - 1);
            float y = Y(r.SpectrumDb[i]);
            if (i == 0) pb.BeginFigure(x, y); else pb.AddLine(x, y);
        }
        pb.EndFigure(CanvasFigureLoop.Open);
        using var geo = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(geo, Color.FromArgb(255, 76, 170, 255), 1.6f);

        // 检测到的截止位置
        if (r.CutoffHz > 0)
        {
            var warn = Color.FromArgb(255, 232, 140, 30);
            using var style = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            ds.DrawLine(X(r.CutoffHz), top, X(r.CutoffHz), top + ph, warn, 1.4f, style);
            ds.DrawText($"{r.CutoffHz / 1000f:0.0} kHz", Math.Min(X(r.CutoffHz) + 4, left + pw - 52), top + 2, warn, fmt);
        }
    }
}
