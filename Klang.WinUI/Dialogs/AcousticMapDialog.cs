using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Dialogs;

/// <summary>
/// 曲库地图：把每首歌按声学特征摆在一张二维地图上（横轴：安静 → 动感，纵轴：沉稳 → 明亮），
/// 听起来相似的歌聚在一起。滚轮缩放、拖动平移，悬停看歌名，点一下就从那里开始放（并排上附近的歌）。
/// </summary>
public static class AcousticMapDialog
{
    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 76, 170, 255), Color.FromArgb(255, 255, 140, 90), Color.FromArgb(255, 110, 205, 130),
        Color.FromArgb(255, 200, 120, 230), Color.FromArgb(255, 250, 200, 70), Color.FromArgb(255, 90, 210, 210)
    };

    /// <param name="onPick">点击某个点：参数是被点的歌和它附近的歌（按距离排好）。</param>
    public static async Task ShowAsync(XamlRoot root, IReadOnlyList<TrackItem> library, Action<TrackItem, List<TrackItem>> onPick)
    {
        var svc = AcousticService.Instance;
        int analyzed = svc.AnalyzedCount(library);
        var fe = root.Content as FrameworkElement;
        bool dark = fe?.ActualTheme == ElementTheme.Dark;
        var secondary = fe != null ? ThemeService.Brush(fe, "TextFillColorSecondaryBrush") : null;

        var panel = new StackPanel { Spacing = 8, MinWidth = 720 };

        if (analyzed < 2)
        {
            panel.Children.Add(new TextBlock
            {
                Text = analyzed == 0
                    ? L.T("分析进行中，请稍后再试。")
                    : L.T("至少需要两首已完成分析的歌曲才能生成地图。"),
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            var points = svc.GetMap(library);
            panel.Children.Add(new TextBlock
            {
                Text = L.F("共 {0} 首，已分析 {1} 首。距离越近，声学特征越相似；点击即可播放。", points.Count, analyzed),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = secondary
            });

            var canvas = new CanvasControl { Height = 440, ClearColor = Colors.Transparent };
            float scale = 1f; Vector2 pan = Vector2.Zero;
            int hover = -1;
            bool dragging = false; Windows.Foundation.Point dragStart = default; Vector2 panStart = default; bool moved = false;

            Vector2 ToScreen(MapPoint p, float w, float h)
            {
                float pad = 46;
                float x = pad + (p.X + 1) / 2 * (w - pad * 2);
                float y = pad + (1 - (p.Y + 1) / 2) * (h - pad * 2);
                return new Vector2(w / 2 + (x - w / 2) * scale + pan.X, h / 2 + (y - h / 2) * scale + pan.Y);
            }

            canvas.Draw += (s, a) =>
            {
                var ds = a.DrawingSession;
                float w = (float)s.ActualWidth, h = (float)s.ActualHeight;
                var fg = dark ? Colors.White : Color.FromArgb(255, 30, 30, 30);
                var faint = Color.FromArgb(46, fg.R, fg.G, fg.B);
                var text = Color.FromArgb(160, fg.R, fg.G, fg.B);
                var fmt = new CanvasTextFormat { FontSize = 11 };

                ds.FillRoundedRectangle(0, 0, w, h, 8, 8, Color.FromArgb(14, fg.R, fg.G, fg.B));
                ds.DrawLine(w / 2 + pan.X, 8, w / 2 + pan.X, h - 8, faint, 1f);
                ds.DrawLine(8, h / 2 + pan.Y, w - 8, h / 2 + pan.Y, faint, 1f);
                ds.DrawText(L.T("安静"), 10, h / 2 + 4, text, fmt);
                ds.DrawText(L.T("动感"), w - 36, h / 2 + 4, text, fmt);
                ds.DrawText(L.T("明亮"), w / 2 + 6, 8, text, fmt);
                ds.DrawText(L.T("沉稳"), w / 2 + 6, h - 20, text, fmt);

                for (int i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    var c = Palette[p.Cluster % Palette.Length];
                    var pos = ToScreen(p, w, h);
                    bool cur = p.Track.IsCurrent;
                    float r = i == hover ? 9f : (cur ? 8f : 6.5f);
                    ds.FillCircle(pos, r, Color.FromArgb((byte)(i == hover || cur ? 255 : 215), c.R, c.G, c.B));
                    if (cur) ds.DrawCircle(pos, r + 3, fg, 1.5f);
                }

                if (hover >= 0)
                {
                    var p = points[hover];
                    var pos = ToScreen(p, w, h);
                    string label = p.Track.Title + (string.IsNullOrEmpty(p.Track.Artist) ? "" : " — " + p.Track.Artist);
                    var layout = new CanvasTextLayout(ds, label, new CanvasTextFormat { FontSize = 12 }, 300, 40);
                    var b = layout.LayoutBounds;
                    float lx = Math.Min(pos.X + 12, w - (float)b.Width - 12), ly = Math.Max(8, pos.Y - (float)b.Height - 14);
                    ds.FillRoundedRectangle(lx - 6, ly - 4, (float)b.Width + 12, (float)b.Height + 8, 6, 6, dark ? Color.FromArgb(235, 40, 40, 44) : Color.FromArgb(240, 250, 250, 252));
                    ds.DrawTextLayout(layout, lx, ly, fg);
                    layout.Dispose();
                }
            };

            int HitTest(Windows.Foundation.Point pt)
            {
                float w = (float)canvas.ActualWidth, h = (float)canvas.ActualHeight;
                int best = -1; float bd = 14 * 14;
                for (int i = 0; i < points.Count; i++)
                {
                    var pos = ToScreen(points[i], w, h);
                    float dx = pos.X - (float)pt.X, dy = pos.Y - (float)pt.Y, d = dx * dx + dy * dy;
                    if (d < bd) { bd = d; best = i; }
                }
                return best;
            }

            canvas.PointerMoved += (s, e) =>
            {
                var pt = e.GetCurrentPoint(canvas).Position;
                if (dragging)
                {
                    pan = panStart + new Vector2((float)(pt.X - dragStart.X), (float)(pt.Y - dragStart.Y));
                    if (Math.Abs(pt.X - dragStart.X) > 4 || Math.Abs(pt.Y - dragStart.Y) > 4) moved = true;
                    canvas.Invalidate();
                    return;
                }
                int h2 = HitTest(pt);
                if (h2 != hover) { hover = h2; canvas.Invalidate(); }
            };
            canvas.PointerPressed += (s, e) =>
            {
                dragging = true; moved = false;
                dragStart = e.GetCurrentPoint(canvas).Position; panStart = pan;
                canvas.CapturePointer(e.Pointer);
            };
            ContentDialog? dialogRef = null;
            canvas.PointerReleased += (s, e) =>
            {
                dragging = false;
                canvas.ReleasePointerCapture(e.Pointer);
                if (moved) return;
                int hit = HitTest(e.GetCurrentPoint(canvas).Position);
                if (hit < 0) return;
                var picked = points[hit].Track;
                var near = points.Where((p, i) => i != hit)
                    .OrderBy(p => (p.X - points[hit].X) * (p.X - points[hit].X) + (p.Y - points[hit].Y) * (p.Y - points[hit].Y))
                    .Take(30).Select(p => p.Track).ToList();
                dialogRef?.Hide();
                onPick(picked, near);
            };
            canvas.PointerExited += (s, e) => { if (hover != -1) { hover = -1; canvas.Invalidate(); } };
            canvas.PointerWheelChanged += (s, e) =>
            {
                int delta = e.GetCurrentPoint(canvas).Properties.MouseWheelDelta;
                float f = delta > 0 ? 1.18f : 1f / 1.18f;
                var pt = e.GetCurrentPoint(canvas).Position;
                float w = (float)canvas.ActualWidth, h = (float)canvas.ActualHeight;
                var center = new Vector2(w / 2, h / 2);
                var mouse = new Vector2((float)pt.X, (float)pt.Y);
                float ns = Math.Clamp(scale * f, 1f, 30f);
                float real = ns / scale;
                // 以鼠标位置为中心缩放
                pan = (mouse - center) * (1 - real) + pan * real;
                scale = ns;
                canvas.Invalidate();
                e.Handled = true;
            };

            panel.Children.Add(canvas);
            panel.Children.Add(new TextBlock { Text = L.T("滚轮缩放，拖动平移。颜色相同的歌曲声学特征相近。"), FontSize = 11, Foreground = secondary });

            var dlg = new ContentDialog
            {
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
                XamlRoot = root,
                Title = L.T("声学地图"),
                Content = panel,
                CloseButtonText = L.T("关闭"),
                DefaultButton = ContentDialogButton.Close
            };
            dlg.Resources["ContentDialogMaxWidth"] = 860.0;
            dialogRef = dlg;
            L.Apply(dlg);
            await dlg.ShowAsync();
            return;
        }

        var d2 = new ContentDialog
        {
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            XamlRoot = root,
            Title = L.T("声学地图"),
            Content = panel,
            CloseButtonText = L.T("关闭")
        };
        L.Apply(d2);
        await d2.ShowAsync();
    }
}
