using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Dialogs;

/// <summary>歌词海报：选几行歌词，配上封面与主色渐变，导出为 PNG。</summary>
public sealed partial class PosterDialog : ContentDialog
{
    private readonly List<LyricLine> _lines;
    private bool _updating;

    public PosterDialog(TrackItem track, IList<LyricLine> lyrics, int activeIndex)
    {
        InitializeComponent();
        L.Apply(this);
        Loaded += (s, e) => L.Apply(this);
        // 去掉“暂无歌词”之类的占位行
        _lines = lyrics.Where(l => !string.IsNullOrWhiteSpace(l.Text) && l.EndTime < 9000).ToList();

        PosterTitle.Text = track.Title;
        PosterArtist.Text = track.Artist;

        foreach (var l in _lines) LineList.Items.Add(l.Text);

        // 默认选中当前行及其后一行
        int start = Math.Max(0, _lines.FindIndex(l => l.Index == activeIndex));
        _updating = true;
        for (int i = start; i < Math.Min(start + 2, _lines.Count); i++) LineList.SelectedItems.Add(LineList.Items[i]);
        _updating = false;
        UpdateLyricText();
        if (start < _lines.Count) LineList.ScrollIntoView(LineList.Items[start]);

        _ = LoadCoverAsync(track);
    }

    private async System.Threading.Tasks.Task LoadCoverAsync(TrackItem track)
    {
        try
        {
            if (!string.IsNullOrEmpty(track.CoverPath) && File.Exists(track.CoverPath))
                PosterCover.Source = new BitmapImage(new Uri(track.CoverPath)) { DecodePixelWidth = 240 };
            var (p, s) = await DominantColor.GetAsync(track.CoverPath);
            PStop0.Color = DominantColor.Darken(p, 0.7);
            PStop1.Color = DominantColor.Darken(s, 0.35);
        }
        catch { }
    }

    private void LineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (LineList.SelectedItems.Count > 8)
        {
            _updating = true;
            foreach (var added in e.AddedItems) LineList.SelectedItems.Remove(added);
            _updating = false;
        }
        UpdateLyricText();
    }

    private void UpdateLyricText()
    {
        var picked = LineList.SelectedItems
            .Select(it => LineList.Items.IndexOf(it)).OrderBy(i => i)
            .Select(i => _lines[i].Text);
        var text = string.Join("\n", picked);
        PosterLyric.Text = text.Length > 0 ? text : L.T("（请选择歌词）");
    }

    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // 保存后保持对话框打开，便于继续调整
        var deferral = args.GetDeferral();
        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(PosterCard);
            var pixels = await rtb.GetPixelsAsync();

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Klang");
            Directory.CreateDirectory(dir);
            var safeTitle = string.Concat(PosterTitle.Text.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(dir, $"{safeTitle}_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            using var ms = new InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
            enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels.ToArray());
            await enc.FlushAsync();
            ms.Seek(0);
            var bytes = new byte[ms.Size];
            await ms.ReadAsync(bytes.AsBuffer(), (uint)ms.Size, InputStreamOptions.None);
            await File.WriteAllBytesAsync(path, bytes);

            StatusText.Text = L.T("已保存：") + path;
        }
        catch (Exception ex)
        {
            StatusText.Text = L.T("保存失败：") + ex.Message;
        }
        finally
        {
            deferral.Complete();
        }
    }
}
