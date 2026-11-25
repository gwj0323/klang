using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services.Cloud;

/// <summary>
/// 把云端歌曲保存成曲库里的本地文件：复制下载好的音频（没下完就等它下完），
/// 写入标题 / 歌手 / 专辑、封面和歌词（内嵌 + 同名 .lrc / .trans.lrc），再加进曲库。
/// 文件放在“音乐\Klang”下，文件名“歌手 - 标题”。
/// </summary>
public static class CloudSaveService
{
    public sealed record Result(bool Ok, string Message, TrackItem? Track = null, bool AlreadyThere = false);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HashSet<string> Saving = new();

    public static string DefaultSaveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Klang");

    /// <summary>设置里选的位置；没选或那个文件夹已经不在了（比如移动硬盘拔了）就用默认的 音乐\Klang。</summary>
    public static string SaveFolder
    {
        get
        {
            var custom = StateService.Instance.State.CloudSaveFolder;
            try { if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom)) return custom; } catch { }
            return DefaultSaveFolder;
        }
    }

    /// <summary>保存一首云端歌曲。progress 会收到“正在下载…”之类的进度文字（界面线程调用即可）。</summary>
    public static async Task<Result> SaveAsync(TrackItem cloud, Action<string>? progress = null, CancellationToken ct = default)
    {
        var src = CloudSources.For(cloud);
        if (src == null || !cloud.IsOnline) return new Result(false, L.T("无法获取音源（不支持的云端来源）"));

        string key = ListeningService.KeyOf(cloud);
        lock (Saving) if (!Saving.Add(key)) return new Result(false, L.T("这首歌正在保存"));
        try
        {
            // 1. 取得完整的音频文件：已缓存直接用；正在边下边播就等它下完；否则开始下载
            progress?.Invoke(L.F("正在下载《{0}》…", cloud.Title));
            if (!await src.ResolveAsync(cloud, ct)) return new Result(false, L.F("无法获取音源（可能需要登录{0}）", src.DisplayName));
            if (ProgressiveDownload.Find(cloud.FilePath) is { } dl)
            {
                dl.IsPrefetch = true;   // 切到别的歌时不要把这次下载取消掉
                if (!await dl.Completion.WaitAsync(ct)) return new Result(false, L.T("下载失败，请稍后重试"));
            }
            string cached = cloud.FilePath;
            if (string.IsNullOrEmpty(cached) || !File.Exists(cached)) return new Result(false, L.T("下载失败，请稍后重试"));

            // 2. 目标路径
            string ext = Path.GetExtension(cached).ToLowerInvariant();
            string dir = SaveFolder;
            Directory.CreateDirectory(dir);
            string baseName = SafeName(string.IsNullOrWhiteSpace(cloud.Artist) ? cloud.Title : $"{cloud.Artist} - {cloud.Title}");
            string dest = Path.Combine(dir, baseName + ext);

            var lib = LibraryService.Instance;
            if (File.Exists(dest))
            {
                // 以前保存过：还在曲库里就直接返回；不在就重新加进去
                var existing = await lib.AddFileAsync(dest);
                return existing != null
                    ? new Result(true, L.F("《{0}》已经在曲库里了", cloud.Title), existing, AlreadyThere: true)
                    : new Result(false, L.T("文件已存在，但无法读取：") + dest);
            }

            // 3. 封面、歌词（失败都不影响保存）
            progress?.Invoke(L.F("正在写入《{0}》的标签…", cloud.Title));
            byte[]? cover = await GetCoverAsync(src, cloud, ct);
            LyricBundle lyrics = LyricBundle.Empty;
            try { lyrics = await src.GetLyricsAsync(cloud.CloudId, ct); } catch (OperationCanceledException) { throw; } catch { }
            bool synced = LyricsService.Instance.IsSynced(lyrics.Lrc);

            // 4. 复制到临时文件（扩展名保持不变，TagLib 按扩展名识别格式），写标签后改名
            string tmp = Path.Combine(dir, baseName + ".saving" + ext);
            await Task.Run(() =>
            {
                File.Copy(cached, tmp, true);
                try
                {
                    WriteTags(tmp, cloud, cover, synced ? lyrics.Lrc : null);
                    File.Move(tmp, dest, false);
                }
                catch
                {
                    try { File.Delete(tmp); } catch { }
                    throw;
                }
                if (synced)
                {
                    File.WriteAllText(Path.Combine(dir, baseName + ".lrc"), lyrics.Lrc, new UTF8Encoding(false));
                    if (LyricsService.Instance.IsSynced(lyrics.Trans))
                        File.WriteAllText(Path.Combine(dir, baseName + ".trans.lrc"), lyrics.Trans, new UTF8Encoding(false));
                }
            }, ct);

            // 5. 加进曲库
            var track = await lib.AddFileAsync(dest, cloud.CloudSource == "netease" ? cloud.CloudId : null);
            return track != null
                ? new Result(true, L.F("已保存到曲库：{0}", dest), track)
                : new Result(false, L.T("已保存，但加入曲库失败：") + dest);
        }
        catch (OperationCanceledException)
        {
            return new Result(false, L.T("已取消"));
        }
        catch (Exception ex)
        {
            App.LogError("Save cloud track", ex.Message);
            return new Result(false, L.T("保存失败：") + ex.Message);
        }
        finally
        {
            lock (Saving) Saving.Remove(key);
        }
    }

    private static void WriteTags(string path, TrackItem t, byte[]? cover, string? lrc)
    {
        using var f = TagLib.File.Create(path);
        var tag = f.Tag;
        if (!string.IsNullOrWhiteSpace(t.Title)) tag.Title = t.Title;
        if (!string.IsNullOrWhiteSpace(t.Artist)) tag.Performers = new[] { t.Artist };
        if (!string.IsNullOrWhiteSpace(t.Album)) tag.Album = t.Album;
        if (cover != null)
        {
            var pic = new TagLib.Picture(new TagLib.ByteVector(cover))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = IsPng(cover) ? "image/png" : "image/jpeg",
            };
            tag.Pictures = new TagLib.IPicture[] { pic };
        }
        if (!string.IsNullOrEmpty(lrc)) tag.Lyrics = lrc;
        f.Save();
    }

    /// <summary>尽量取大图（网易云 1200px、QQ 音乐 800px），不行就用播放时缓存的封面。</summary>
    private static async Task<byte[]?> GetCoverAsync(ICloudSource src, TrackItem t, CancellationToken ct)
    {
        string url = t.CoverUrl ?? "";
        if (url.Contains("music.126.net", StringComparison.OrdinalIgnoreCase))
        {
            int q = url.IndexOf('?');
            url = (q >= 0 ? url[..q] : url) + "?param=1200y1200";
        }
        else if (url.Contains("y.gtimg.cn", StringComparison.OrdinalIgnoreCase))
        {
            url = url.Replace("R500x500", "R800x800");
        }
        if (url.Length > 0 && src.IsTrustedUrl(url))
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url, ct);
                if (bytes.Length > 1024 && (IsJpeg(bytes) || IsPng(bytes))) return bytes;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
        try
        {
            var p = t.OriginalCoverPath;
            if (!string.IsNullOrEmpty(p) && File.Exists(p))
            {
                var bytes = await File.ReadAllBytesAsync(p, ct);
                if (IsJpeg(bytes) || IsPng(bytes)) return bytes;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return null;
    }

    private static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;
    private static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    private static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        s = s.Replace(" / ", ", ").Replace("/", ", ");   // 多位歌手“A / B”在文件名里写成“A, B”，而不是“A _ B”
        var name = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length > 120) name = name[..120].Trim();
        return name.Length == 0 ? "Untitled" : name;
    }
}
