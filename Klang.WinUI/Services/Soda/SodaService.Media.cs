using System.Text.Json;
using Klang_WinUI.Models;

namespace Klang_WinUI.Services.Soda;

public sealed partial class SodaService
{
    private const string IdPrefix = "soda_";
    private static readonly string[] PreferredQuality = { "highest", "lossless", "flac", "hires", "exhigh", "high", "medium", "low" };

    private static string StripId(string id) =>
        id.StartsWith(IdPrefix, StringComparison.Ordinal) ? id[IdPrefix.Length..] : id;

    private TrackItem? MapTrack(JsonElement track)
    {
        var id = GetString(track, "id");
        if (string.IsNullOrEmpty(id)) return null;
        var name = GetString(track, "name") ?? "未知曲目";
        double duration = track.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetDouble() / 1000.0 : 0;

        var artists = new List<string>();
        if (track.TryGetProperty("artists", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var artist in artistArray.EnumerateArray())
            {
                var artistName = GetString(artist, "name");
                if (!string.IsNullOrEmpty(artistName)) artists.Add(artistName);
            }
        }

        string album = "";
        string coverUrl = "";
        if (track.TryGetProperty("album", out var albumElement) && albumElement.ValueKind == JsonValueKind.Object)
        {
            album = GetString(albumElement, "name") ?? "";
            if (albumElement.TryGetProperty("url_cover", out var urlCover))
                coverUrl = BuildImageUrl(urlCover);
        }

        var gen = Generation;
        var account = Account;
        var item = new TrackItem
        {
            Id = IdPrefix + id,
            SodaId = id,
            Title = name,
            Artist = artists.Count > 0 ? string.Join(" / ", artists) : "未知歌手",
            Album = album,
            Duration = duration,
            CoverUrl = coverUrl,
            NeteaseId = "",
            FormatBadge = "",
            Bitrate = track.TryGetProperty("bit_rates", out var br) && br.ValueKind == JsonValueKind.Number
                ? (int)br.GetDouble() : 0,
        };
        if (coverUrl.Length > 0) _ = EnsureCoverAsync(item, coverUrl, account, gen);
        return item;
    }

    private TrackItem? MapMediaEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        if (entry.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
        {
            var mapped = MapTrack(media);
            if (mapped != null) return mapped;
        }
        foreach (var nested in FindObjectCandidates(entry))
        {
            if (nested.TryGetProperty("artists", out _) || nested.TryGetProperty("duration", out _))
            {
                var mapped = MapTrack(nested);
                if (mapped != null) return mapped;
            }
        }
        return MapTrack(entry);
    }

    /// <summary>
    /// 按官方 utils/image.ts 的 getImageUrl 规则拼接：
    /// urls[0] + uri + "~" + (template_prefix ? "{prefix}-crop-center:W:H.jpg" : "c5_WxH.jpg")。
    /// template_prefix 只是模板前缀，缺少 "-crop-center:…" 后缀会被 image 服务判 400。
    /// </summary>
    private static string BuildImageUrl(JsonElement urlInfo, int width = 400, int height = 400)
    {
        try
        {
            var uri = GetString(urlInfo, "uri") ?? "";
            var prefix = GetString(urlInfo, "template_prefix") ?? "";
            var template = GetString(urlInfo, "template") ?? "";
            string host = "";
            if (urlInfo.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
                host = urls.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
            if (host.Length == 0 || uri.Length == 0) return "";

            if (template.Length > 0)
            {
                var filled = template
                    .Replace("${width}", width.ToString())
                    .Replace("${height}", height.ToString())
                    .Replace("${quality}", "80");
                if (filled.Length > 0 && prefix.Length > 0) return $"{host}{uri}~{prefix}-{filled}";
            }

            var suffix = prefix.Length > 0
                ? $"{prefix}-crop-center:{width}:{height}.jpg"
                : $"c5_{width}x{height}.jpg";
            return $"{host}{uri}~{suffix}";
        }
        catch { return ""; }
    }

    private async Task EnsureCoverAsync(TrackItem track, string coverUrl, string account, int gen)
    {
        var key = StripId(track.Id);
        var path = _cache.PathFor("covers", account, key + "|" + coverUrl, ".img");
        if (File.Exists(path))
        {
            if (gen != Generation) return;
            track.CoverPath = path;
            CoverCached?.Invoke(track);
            return;
        }
        try
        {
            await using var stream = await Http.GetStreamAsync(coverUrl, CancellationToken.None).ConfigureAwait(false);
            var temp = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var file = File.Create(temp))
            {
                await stream.CopyToAsync(file).ConfigureAwait(false);
                await file.FlushAsync().ConfigureAwait(false);
            }
            if (gen != Generation) { TryDelete(temp); return; }
            File.Move(temp, path, true);
            track.CoverPath = path;
            CoverCached?.Invoke(track);
        }
        catch (Exception ex) { App.LogError("SodaCover", ex.Message); }
    }

    /// <summary>逐行/翻译歌词；按账号与曲目缓存，账号切换时丢弃结果。</summary>
    public async Task<SodaLyrics> GetLyricsAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var gen = Generation;
        var account = Account;
        var trackId = StripId(id);
        // v2 stores normalized LRC; older caches may still contain raw KRC timing tags.
        var cached = _cache.ReadJson("lyrics-v2", account, trackId);
        if (cached != null)
        {
            try
            {
                var value = JsonSerializer.Deserialize<SodaLyrics>(cached);
                if (value != null) return value;
            }
            catch { }
        }

        using var detail = await GetDetailAsync(trackId, account, gen, ct).ConfigureAwait(false);
        if (gen != Generation) throw new SodaException(SodaErrorKind.Auth, "账号已切换");
        var raw = "";
        var rawTrans = "";
        if (detail.RootElement.TryGetProperty("lyric", out var lyric) && lyric.ValueKind == JsonValueKind.Object)
        {
            raw = GetString(lyric, "content") ?? "";
            if (lyric.TryGetProperty("translations", out var translations) && translations.ValueKind == JsonValueKind.Object)
                rawTrans = GetString(translations, "cn") ?? "";
        }
        var (lrc, trans) = NormalizeLyrics(raw, rawTrans);
        var result = new SodaLyrics(lrc, trans);
        if (lrc.Length > 0) _cache.WriteJson("lyrics-v2", account, trackId, JsonSerializer.Serialize(result));
        return result;
    }

    public bool IsCached(TrackItem track) => File.Exists(AudioPath(Account, StripId(track.Id)));

    /// <summary>
    /// 解析授权音源：
    ///  - 无完整授权/试听/暂不可播 → false（不抛）
    ///  - 网络/签名/登录失效 → 抛脱敏的 SodaException
    ///  - 取消 → OperationCanceledException
    /// 加密音源仅支持已验证的 AAC-LC/CENC；其它加密编码明确拒绝。
    /// </summary>
    public async Task<bool> ResolveAsync(TrackItem track, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var gen = Generation;
        var account = Account;
        var trackId = StripId(track.Id);
        var audioPath = AudioPath(account, trackId);
        if (File.Exists(audioPath))
        {
            if (gen != Generation) throw new SodaException(SodaErrorKind.Auth, "账号已切换");
            ApplyCachedMetadata(track, audioPath);
            return true;
        }

        // 预取可能正占着锁：排队等待而不是直接判“不可播”；拿到锁后再看一次缓存。
        await _resolveGate.WaitAsync(ct).ConfigureAwait(false);
        string? temp = null;
        try
        {
            if (File.Exists(audioPath))
            {
                if (gen != Generation) throw new SodaException(SodaErrorKind.Auth, "账号已切换");
                ApplyCachedMetadata(track, audioPath);
                return true;
            }
            using var detail = await GetDetailAsync(trackId, account, gen, ct).ConfigureAwait(false);
            if (gen != Generation) throw new SodaException(SodaErrorKind.Auth, "账号已切换");
            // 只读回填收藏状态；不做任何收藏写。
            if (detail.RootElement.TryGetProperty("is_collected", out var collected) &&
                (collected.ValueKind == JsonValueKind.True || collected.ValueKind == JsonValueKind.False))
                track.IsFavorite = collected.GetBoolean();
            if (!IsFullyAuthorized(detail.RootElement)) return false;

            var (url, spade, _, bitrate) = await PickPlayInfoAsync(detail.RootElement, ct).ConfigureAwait(false);
            if (url.Length == 0) return false;

            temp = audioPath + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
            await DownloadAsync(url, temp, ct).ConfigureAwait(false);
            if (gen != Generation) throw new SodaException(SodaErrorKind.Auth, "账号已切换");

            var info = SodaMedia.Probe(temp);
            if (info == null) return false;

            if (spade.Length > 0)
            {
                if (info.Codec != "AAC-LC") { App.LogError("SodaResolve", $"加密编码不支持：{info.Codec}"); return false; }
                if (_bridge == null)
                    throw new SodaException(SodaErrorKind.Unavailable, "音频解密需要 Node 辅助进程");
                var decryptTarget = temp + ".dec";
                var decrypted = await _bridge.InvokeAsync("decode-file", new
                {
                    inPath = temp,
                    outPath = decryptTarget,
                    spade,
                    decryptorPath = Path.Combine(AppContext.BaseDirectory, "Runtime", "Soda", "ThirdParty", "qishui-api", "audioDecryptor.js"),
                }, 60000, ct).ConfigureAwait(false);
                if (gen != Generation) { TryDelete(decryptTarget); throw new SodaException(SodaErrorKind.Auth, "账号已切换"); }
                info = SodaMedia.Probe(decryptTarget);
                if (info == null) { TryDelete(decryptTarget); return false; }
                TryDelete(temp);
                temp = decryptTarget;
            }

            // 时长为 0 说明容器探测不出真实长度（可能是试听/截断），不能当作完整授权放行。
            if (info.Duration <= 0)
            {
                App.LogError("SodaResolve", "文件时长无效，无法确认完整授权");
                return false;
            }
            if (track.Duration > 0 && Math.Abs(info.Duration - track.Duration) > 3.0)
            {
                App.LogError("SodaResolve", $"时长不符：接口 {track.Duration:0.0}s，文件 {info.Duration:0.0}s");
                return false;
            }

            File.Move(temp, audioPath, true);
            temp = null;
            track.FilePath = audioPath;      // M4A/FLAC 原封装直接交给 MediaFoundationReader，不转码
            ApplyMetadata(track, audioPath, bitrate);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (SodaException ex) when (ex.Kind is SodaErrorKind.Network or SodaErrorKind.Verification or SodaErrorKind.Auth or SodaErrorKind.Protocol)
        {
            App.LogError("SodaResolve", $"{trackId}: {ex.Message}");
            throw;
        }
        catch (SodaException ex)
        {
            App.LogError("SodaResolve", $"{trackId}: {ex.Message}");
            return false;
        }
        finally
        {
            if (temp != null) TryDelete(temp);
            _resolveGate.Release();
        }
    }

    private static bool IsFullyAuthorized(JsonElement root)
    {
        if (!root.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object) return false;
        if (track.TryGetProperty("preview", out var preview) && preview.ValueKind == JsonValueKind.True) return false;
        if (track.TryGetProperty("limited_free_info", out var limited) &&
            limited.ValueKind == JsonValueKind.Object) return false;
        return true;
    }

    public void Prefetch(TrackItem track)
    {
        var id = StripId(track.Id);
        if (IsCached(track) || !_resolving.TryAdd(id, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await ResolveAsync(track, cts.Token).ConfigureAwait(false);
            }
            catch { }
            finally { _resolving.TryRemove(id, out _); }
        });
    }

    private async Task<JsonDocument> GetDetailAsync(string trackId, string account, int generation, CancellationToken ct)
    {
        // 详情里有 video_model / PlayAuth 这类短期播放授权，只放内存并设短 TTL，绝不落盘。
        var key = account + "|" + trackId;
        if (_detailCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Stamp < DetailTtl)
            return JsonDocument.Parse(cached.Json);

        using var doc = await Http.PostAsync("/luna/pc/track_v2", new
        {
            track_id = trackId,
            media_type = "track",
            queue_type = "",
        }, ct).ConfigureAwait(false);
        var json = doc.RootElement.GetRawText();
        if (generation == Generation)
        {
            _detailCache[key] = (json, DateTime.UtcNow);
            foreach (var stale in _detailCache.Where(kv => DateTime.UtcNow - kv.Value.Stamp > DetailTtl).Select(kv => kv.Key).Take(64))
                _detailCache.TryRemove(stale, out _);
        }
        return JsonDocument.Parse(json);
    }

    private async Task<(string Url, string Spade, string Quality, int Bitrate)> PickPlayInfoAsync(JsonElement root, CancellationToken ct)
    {
        var candidates = new List<(string Url, string Spade, string Quality, int Bitrate)>();
        if (!root.TryGetProperty("track_player", out var player) || player.ValueKind != JsonValueKind.Object)
            return ("", "", "", 0);

        if (player.TryGetProperty("video_model", out var model) && model.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var parsed = JsonDocument.Parse(model.GetString() ?? "{}");
                if (parsed.RootElement.TryGetProperty("video_list", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        var url = GetString(item, "main_url") ?? "";
                        if (url.Length == 0) continue;
                        var spade = item.TryGetProperty("encrypt_info", out var enc) ? GetString(enc, "spade_a") ?? "" : "";
                        var quality = "";
                        var bitrate = 0;
                        if (item.TryGetProperty("video_meta", out var meta))
                        {
                            quality = GetString(meta, "quality") ?? "";
                            if (meta.TryGetProperty("bitrate", out var b) && b.ValueKind == JsonValueKind.Number)
                                bitrate = (int)b.GetDouble();
                        }
                        candidates.Add((url, spade, quality, bitrate));
                    }
                }
            }
            catch (JsonException) { }
        }

        var fallback = GetString(player, "url_player_info") ?? "";
        if (candidates.Count == 0 && fallback.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var rpc = await Http.GetAbsoluteAsync(fallback, ct).ConfigureAwait(false);
            if (rpc.RootElement.TryGetProperty("Result", out var result)
                && result.TryGetProperty("Data", out var data)
                && data.TryGetProperty("PlayInfoList", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var url = GetString(item, "MainPlayUrl") ?? "";
                    if (url.Length == 0) continue;
                    var backup = GetString(item, "BackupPlayUrl") ?? "";
                    var auth = GetString(item, "PlayAuth") ?? "";
                    var quality = GetString(item, "Quality") ?? "";
                    var bitrate = item.TryGetProperty("Bitrate", out var b) && b.ValueKind == JsonValueKind.Number
                        ? (int)b.GetDouble() : 0;
                    candidates.Add((url, auth, quality, bitrate));
                    if (backup.Length > 0) candidates.Add((backup, auth, quality, bitrate));
                }
            }
        }

        if (candidates.Count == 0) return ("", "", "", 0);
        var ordered = candidates
            .OrderByDescending(c => Array.IndexOf(PreferredQuality, c.Quality) is var i && i >= 0 ? PreferredQuality.Length - i : 0)
            .ThenByDescending(c => c.Bitrate);
        return ordered.First();
    }

    private async Task DownloadAsync(string url, string path, CancellationToken ct)
    {
        try
        {
            await using var stream = await Http.GetStreamAsync(url, ct).ConfigureAwait(false);
            await using var file = File.Create(path);
            await stream.CopyToAsync(file, ct).ConfigureAwait(false);
            await file.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SodaException(SodaErrorKind.Network, "音源下载失败", 0, null, ex);
        }
    }

    /// <summary>
    /// 官方歌词有两种格式：LRC（[mm:ss.xxx]）与 KRC（[startMs,durationMs]文字&lt;start,dur,...&gt;…）。
    /// 统一归一化成 LRC，翻译按“非空源行序号”对齐，交给界面现成的 LRC 解析。
    /// </summary>
    private static (string Lrc, string Trans) NormalizeLyrics(string raw, string rawTrans)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", "");
        var sourceLines = raw.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var transLines = rawTrans.Length > 0
            ? rawTrans.Replace("\r\n", "\n").Split('\n')
            : Array.Empty<string>();

        var lrc = new List<string>();
        var trans = new List<string>();
        for (var i = 0; i < sourceLines.Count; i++)
        {
            var line = sourceLines[i];
            string stamp;
            string text;
            var krc = System.Text.RegularExpressions.Regex.Match(line, @"^\[(\d+),(\d+)\](.*)$");
            if (krc.Success)
            {
                var startMs = long.Parse(krc.Groups[1].Value);
                text = System.Text.RegularExpressions.Regex.Replace(krc.Groups[3].Value, "<[^>]*>", "").Trim();
                stamp = FormatStamp(startMs);
            }
            else
            {
                var tag = System.Text.RegularExpressions.Regex.Match(line, @"^(\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\])(.*)$");
                if (tag.Success)
                {
                    stamp = tag.Groups[1].Value;
                    text = tag.Groups[2].Value.Trim();
                }
                else
                {
                    continue;
                }
            }
            if (text.Length == 0) continue;
            lrc.Add(stamp + text);

            var translated = i < transLines.Length ? System.Text.RegularExpressions.Regex.Replace(transLines[i], @"^\[[^\]]+\]", "").Trim() : "";
            if (translated.Length > 0 && translated != text) trans.Add(stamp + translated);
        }
        return (string.Join("\n", lrc), string.Join("\n", trans));
    }

    private static string FormatStamp(long milliseconds)
    {
        var total = Math.Max(0, milliseconds);
        var minutes = total / 60000;
        var seconds = (total % 60000) / 1000;
        var centis = (total % 1000) / 10;
        return $"[{minutes:00}:{seconds:00}.{centis:00}]";
    }

    /// <summary>缓存命中时用文件本身恢复容器/编码/码率，避免回到界面变成空标签。</summary>
    private static void ApplyCachedMetadata(TrackItem track, string audioPath)
    {
        track.FilePath = audioPath;
        ApplyMetadata(track, audioPath, 0);
    }

    private static void ApplyMetadata(TrackItem track, string audioPath, int apiBitrate)
    {
        try
        {
            var info = SodaMedia.Probe(audioPath);
            if (info == null) return;
            track.FormatBadge = info.Container is "M4A" or "M4A-FLAC" ? info.Codec : info.Container;
            if (apiBitrate > 0) track.Bitrate = apiBitrate;
            else if (info.Duration > 0 && info.Size > 0)
                track.Bitrate = (int)Math.Round(info.Size * 8.0 / info.Duration / 1000.0);
        }
        catch { }
    }

    private string AudioPath(string account, string trackId) => _cache.PathFor("audio", account, trackId, ".bin");

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
