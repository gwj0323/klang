using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Klang_WinUI.Services.Cloud;

/// <summary>
/// 边下边播：一首云端音源正在下载到缓存的临时文件里，同时可以从已经下载的部分开始播放。
/// 播放引擎按最终的缓存路径（还不存在）来找它：<see cref="IsActive"/> / <see cref="OpenReader"/>。
/// 下载是分段的：平时从头往后下；解码器要读还没下载的位置（拖动进度条）时，下载跳到那里接着下，
/// 到末尾后再回头补上跳过的部分。全部下完后临时文件改名成最终路径。
/// </summary>
public sealed class ProgressiveDownload
{
    private static readonly ConcurrentDictionary<string, ProgressiveDownload> ByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ProgressiveDownload> ByKey = new(StringComparer.Ordinal);

    /// <summary>已下载多少字节后就开始播放（文件更小时等它下完）。</summary>
    private const long StartBytes = 512 * 1024;
    /// <summary>要读的位置在正在下载的位置之后这么远以内时，不跳，等它下到。</summary>
    private const long JumpThreshold = 1024 * 1024;

    public string Key { get; }
    public string FinalPath { get; }
    public string PartPath { get; }
    public string Level { get; }
    public string Type { get; }
    public int Bitrate { get; }
    /// <summary>预缓冲发起的下载：切歌时不取消。</summary>
    public bool IsPrefetch { get; set; }
    public CancellationTokenSource Cts { get; } = new();

    private readonly object _lock = new();
    private long _length = -1;
    // 已下载的区间（按起点排序、互不重叠）
    private readonly System.Collections.Generic.List<(long Start, long End)> _have = new();
    private long _jumpTo = -1;      // 解码器要读、但还没下载的位置
    private bool _canJump = true;   // 服务器不支持 Range 时只能从头顺序下
    private bool _done, _ok;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProgressiveDownload(string key, string finalPath, string level, string type, int bitrate)
    {
        Key = key;
        FinalPath = finalPath;
        PartPath = finalPath + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
        Level = level; Type = type; Bitrate = bitrate;
    }

    /// <summary>登记为正在下载（同一路径或同一首歌已有下载时返回 false）。</summary>
    public bool Register()
    {
        if (!ByPath.TryAdd(FinalPath, this)) return false;
        if (!ByKey.TryAdd(Key, this)) { ByPath.TryRemove(FinalPath, out _); return false; }
        return true;
    }

    public static ProgressiveDownload? FindByKey(string key) => ByKey.TryGetValue(key, out var d) ? d : null;
    public static ProgressiveDownload? Find(string? path) => path != null && ByPath.TryGetValue(path, out var d) ? d : null;
    public static bool IsActive(string? path) => Find(path) != null;

    /// <summary>取消所有不是预缓冲发起的下载（开始播放另一首云端歌曲时，上一首没下完的就不要了）。</summary>
    public static void CancelPlaybackDownloads()
    {
        foreach (var d in ByPath.Values)
            if (!d.IsPrefetch) try { d.Cts.Cancel(); } catch { }
    }

    public Task<bool> Completion => _completion.Task;

    // ---------------- 已下载区间 ----------------

    /// <summary>pos 所在的已下载区间的末尾；pos 还没下载时返回 -1。调用方持有 _lock。</summary>
    private long EndOfRunAt(long pos)
    {
        foreach (var (s, e) in _have)
        {
            if (pos < s) break;
            if (pos < e) return e;
        }
        return -1;
    }

    /// <summary>从 from 开始第一个还没下载的位置；全部下完时返回 -1。调用方持有 _lock。</summary>
    private long FirstGapFrom(long from)
    {
        long p = from;
        foreach (var (s, e) in _have)
        {
            if (e <= p) continue;
            if (s > p) break;
            p = e;
        }
        return _length > 0 && p >= _length ? -1 : p;
    }

    private void AddRun(long start, long count)
    {
        lock (_lock)
        {
            long s = start, e = start + count;
            for (int i = _have.Count - 1; i >= 0; i--)
            {
                var r = _have[i];
                if (r.End < s || r.Start > e) continue;   // 不相交也不相邻
                s = Math.Min(s, r.Start); e = Math.Max(e, r.End);
                _have.RemoveAt(i);
            }
            int at = 0;
            while (at < _have.Count && _have[at].Start < s) at++;
            _have.Insert(at, (s, e));
        }
        long head;
        lock (_lock) head = EndOfRunAt(0);
        if (head >= StartBytes && CanStream()) _ready.TrySetResult(true);
    }

    private bool AllDone()
    {
        lock (_lock) return _length > 0 && _have.Count == 1 && _have[0].Start == 0 && _have[0].End >= _length;
    }

    /// <summary>
    /// 等到可以开始播放：开头已经下了足够多（且格式允许边下边播），或者已经下完。下载失败返回 false。
    /// </summary>
    public async Task<bool> WaitReadyAsync(CancellationToken ct)
    {
        try { return await _ready.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
    }

    /// <summary>下载结束：成功时临时文件已经改名成最终路径。</summary>
    private void Complete(bool ok)
    {
        lock (_lock) { _done = true; _ok = ok; }
        ByPath.TryRemove(new(FinalPath, this));
        ByKey.TryRemove(new(Key, this));
        _ready.TrySetResult(ok);
        _completion.TrySetResult(ok);
    }

    // m4a / mp4 的索引（moov）放在文件末尾时，解码器一打开就要读到末尾，没法边下边播
    private int _streamable = -1;
    private bool CanStream()
    {
        if (TotalLength <= 0) return false;
        if (_streamable >= 0) return _streamable == 1;
        if (Type is not ("m4a" or "mp4")) { _streamable = 1; return true; }
        _streamable = MoovFirst() ? 1 : 0;
        return _streamable == 1;
    }

    /// <summary>读顶层 box：moov 在 mdat 之前才算能边下边播。</summary>
    private bool MoovFirst()
    {
        try
        {
            long head;
            lock (_lock) head = EndOfRunAt(0);
            using var fs = new FileStream(PartPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[8];
            long pos = 0;
            for (int i = 0; i < 8 && pos + 8 <= head; i++)
            {
                fs.Position = pos;
                if (fs.Read(buf, 0, 8) < 8) return false;
                long size = (long)buf[0] << 24 | (long)buf[1] << 16 | (long)buf[2] << 8 | buf[3];
                string type = System.Text.Encoding.ASCII.GetString(buf, 4, 4);
                if (type == "moov") return true;
                if (type == "mdat") return false;
                if (size == 1)
                {
                    if (fs.Read(buf, 0, 8) < 8) return false;
                    size = 0; foreach (var b in buf) size = size << 8 | b;
                }
                if (size < 8) return false;
                pos += size;
            }
        }
        catch { }
        return false;
    }

    // ---------------- 给播放引擎 / 本机 HTTP 服务用 ----------------

    /// <summary>
    /// 播放引擎打开正在下载的文件：返回一个解码器，读到还没下载的位置时等待下载。
    /// 已经下完（或不存在这个下载）返回 null，调用方直接按普通文件打开。
    /// 解码器（Media Foundation）直接读本地的流时，打开前会把整个文件从头读一遍来判断格式，就没法边下边播；
    /// 所以经由本机回环地址上的一个小 HTTP 服务读取，它对 HTTP 来源只读开头、边缓冲边播，跳转时按 Range 请求。
    /// </summary>
    public static WaveStream? OpenReader(string path)
    {
        var d = Find(path);
        if (d == null) return null;
        return new MediaFoundationReader(LocalStreamServer.UrlFor(d));
    }

    /// <summary>现在应该从哪个文件读：下完后临时文件已经改名成最终路径。</summary>
    internal string CurrentFile { get { lock (_lock) return _done && _ok ? FinalPath : PartPath; } }

    internal long TotalLength { get { lock (_lock) return _length; } }

    /// <summary>从 pos 开始连续可读到哪里（不含）；pos 还没下载时返回 -1。</summary>
    internal long ReadableEnd(long pos)
    {
        lock (_lock) return _done && _ok ? _length : EndOfRunAt(pos);
    }

    /// <summary>
    /// 等到 pos 处的字节已下载（返回 true），或者下载已经结束、再也等不到了（返回 false）。
    /// pos 离正在下载的位置较远时，让下载跳过去。
    /// </summary>
    internal async Task<bool> WaitForAsync(long pos, CancellationToken ct)
    {
        bool asked = false;
        while (true)
        {
            lock (_lock)
            {
                if ((_done && _ok) || EndOfRunAt(pos) > pos) return true;
                if (_done) return false;
                if (!asked) { _jumpTo = pos; asked = true; }
            }
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    // ---------------- 下载 ----------------

    // 下载音源共用一个客户端：复用到 CDN 的连接，切歌时省掉每次重新握手（约几百毫秒）
    private static readonly System.Net.Http.HttpClient Downloader = new() { Timeout = TimeSpan.FromMinutes(5) };

    // 测试边下边播用：KLANG_TEST_DOWNLOAD_KBPS=1000 把下载限速到约 1 MB/s（字节 / 毫秒 ≈ KB/s）
    private static readonly long TestThrottleKBps =
        long.TryParse(Environment.GetEnvironmentVariable("KLANG_TEST_DOWNLOAD_KBPS"), out var kb) && kb > 0 ? kb : 0;

    /// <summary>下一段从哪里开始下：有待跳转的位置就去那里，否则从头找第一个空缺；全部下完返回 -1。</summary>
    private long NextStart()
    {
        lock (_lock)
        {
            if (_jumpTo >= 0)
            {
                long j = _jumpTo;
                _jumpTo = -1;
                if (_canJump && EndOfRunAt(j) < 0) return j;
            }
            return FirstGapFrom(0);
        }
    }

    /// <summary>正在下载到 pos 时，是否该停下这一段、去下别处（有远处的跳转请求，或已经接上下过的部分）。</summary>
    private bool ShouldBreak(long pos)
    {
        lock (_lock)
        {
            if (_length > 0 && pos >= _length) return true;
            if (EndOfRunAt(pos) > pos) return true;   // 接上了已经下载的部分
            if (_canJump && _jumpTo >= 0 && EndOfRunAt(_jumpTo) < 0 && (_jumpTo < pos || _jumpTo > pos + JumpThreshold)) return true;
            return false;
        }
    }

    /// <summary>
    /// 执行下载：分段写入临时文件，全部下完后改名成最终路径。makeRequest 每次生成一个新的请求（会加上 Range）；
    /// allowedHost 检查跟随重定向后的地址（必须还在该平台的域名下）。
    /// </summary>
    public async Task<bool> RunAsync(Func<System.Net.Http.HttpRequestMessage> makeRequest, Func<Uri?, bool> allowedHost)
    {
        var ct = Cts.Token;
        bool ok = false;
        try
        {
            await using var dst = new FileStream(PartPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[64 * 1024];
            long downloaded = 0;
            int stalls = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                long start = NextStart();
                if (start < 0) break;
                using var req = makeRequest();
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, null);
                using var rsp = await Downloader.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!rsp.IsSuccessStatusCode || !allowedHost(rsp.RequestMessage?.RequestUri)) return false;

                long offset = start;
                var cr = rsp.Content.Headers.ContentRange;
                if (rsp.StatusCode == System.Net.HttpStatusCode.PartialContent && cr?.From is long from)
                {
                    offset = from;
                    if (cr.Length is long total) lock (_lock) if (_length <= 0) _length = total;
                }
                else
                {
                    // 服务器不支持 Range：整个文件从头给，之后不能跳转
                    offset = 0;
                    lock (_lock)
                    {
                        _canJump = false;
                        if (_length <= 0 && rsp.Content.Headers.ContentLength is long len) _length = len;
                    }
                }

                await using var src = await rsp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    dst.Position = offset;
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    await dst.FlushAsync(ct).ConfigureAwait(false);   // 交给系统，正在播放的读取方才能读到
                    AddRun(offset, n);
                    offset += n;
                    downloaded += n;
                    if (TestThrottleKBps > 0 && clock.ElapsedMilliseconds < downloaded / TestThrottleKBps)
                        await Task.Delay((int)(downloaded / TestThrottleKBps - clock.ElapsedMilliseconds), ct).ConfigureAwait(false);
                    if (ShouldBreak(offset)) break;
                }
                if (TotalLength <= 0) { lock (_lock) _length = offset; }   // 没给长度：只能当作下完了
                if (!_canJump && n <= 0) break;
                // 连着几次请求都没下到东西（连接一开就断）：放弃
                if (offset == start) { if (++stalls >= 3) return false; }
                else stalls = 0;
            }
            // 被截断的下载不能当成有效缓存
            if (!AllDone() || TotalLength <= 10240) return false;
            dst.Close();
            File.Move(PartPath, FinalPath, true);
            ok = true;
            return true;
        }
        catch { return false; }
        finally
        {
            if (!ok) try { if (File.Exists(PartPath)) File.Delete(PartPath); } catch { }
            Complete(ok);
        }
    }

    /// <summary>启动时清掉上次退出时没下完的临时文件。</summary>
    public static void CleanStaleParts(string dir)
    {
        try { foreach (var f in Directory.EnumerateFiles(dir, "*.part")) try { File.Delete(f); } catch { } } catch { }
    }
}
/// <summary>
/// 云端缓存目录的快照：一次列出所有文件，按歌曲 id 分组（文件名是 “id_音质.扩展名” / “id_cover.jpg”）。
/// 以前列表里每首歌都单独扫一遍目录，上千首的歌单 × 缓存里成百上千个文件就很慢。
/// </summary>
internal sealed class CloudCacheIndex
{
    private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<FileInfo>> _audio = new();
    private readonly System.Collections.Generic.HashSet<string> _covers = new();

    public CloudCacheIndex(string dir)
    {
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
            {
                var name = f.Name;
                int us = name.IndexOf('_');
                if (us <= 0) continue;
                var id = name[..us];
                if (name.EndsWith("_cover.jpg", StringComparison.OrdinalIgnoreCase)) { _covers.Add(id); continue; }
                var ext = f.Extension.TrimStart('.').ToLowerInvariant();
                if (Array.IndexOf(NetGuard.AudioExtensions, ext) < 0 || f.Length <= 10240) continue;
                if (!_audio.TryGetValue(id, out var list)) _audio[id] = list = new();
                list.Add(f);
            }
        }
        catch { }
    }

    public bool HasCover(string id) => _covers.Contains(id);

    /// <summary>优先首选音质的文件，其次最近写入的。</summary>
    public string? FindAudio(string id, string quality)
    {
        if (!_audio.TryGetValue(id, out var list)) return null;
        var pref = $"{id}_{quality}.";
        FileInfo? best = null;
        foreach (var f in list)
        {
            if (best == null) { best = f; continue; }
            bool fp = f.Name.StartsWith(pref, StringComparison.Ordinal), bp = best.Name.StartsWith(pref, StringComparison.Ordinal);
            if (fp != bp ? fp : f.LastWriteTimeUtc > best.LastWriteTimeUtc) best = f;
        }
        return best?.FullName;
    }
}

/// <summary>
/// 只监听 127.0.0.1 的极简 HTTP 服务：把正在下载的音源按 HTTP（支持 Range）提供给解码器，
/// 读到还没下载的位置时等待。地址里是随机令牌，只能读到登记过的下载，不涉及任意文件路径。
/// </summary>
internal static class LocalStreamServer
{
    private static readonly object Gate = new();
    private static System.Net.Sockets.TcpListener? _listener;
    private static int _port;
    private static readonly ConcurrentDictionary<string, ProgressiveDownload> Tokens = new();
    private static readonly ConcurrentDictionary<ProgressiveDownload, string> TokenOf = new();

    public static string UrlFor(ProgressiveDownload d)
    {
        EnsureStarted();
        var token = TokenOf.GetOrAdd(d, _ =>
        {
            var t = Guid.NewGuid().ToString("N");
            Tokens[t] = d;
            return t;
        });
        // 保留最近的一些令牌：下完以后正在播放的解码器跳转时还会用同一个地址重新请求
        if (Tokens.Count > 64)
            foreach (var kv in Tokens)
                if (kv.Value != d && ProgressiveDownload.Find(kv.Value.FinalPath) == null && Tokens.Count > 32)
                {
                    Tokens.TryRemove(kv.Key, out _);
                    TokenOf.TryRemove(kv.Value, out _);
                }
        return $"http://127.0.0.1:{_port}/{token}.{d.Type}";
    }

    private static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_listener != null) return;
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            _port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            _listener = l;
            _ = Task.Run(() => AcceptLoopAsync(l));
        }
    }

    private static async Task AcceptLoopAsync(System.Net.Sockets.TcpListener l)
    {
        while (true)
        {
            System.Net.Sockets.TcpClient c;
            try { c = await l.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(c));
        }
    }

    private static async Task ServeAsync(System.Net.Sockets.TcpClient client)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            var ns = client.GetStream();
            string head = await ReadHeadAsync(ns).ConfigureAwait(false);
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "HEAD")) { await RespondAsync(ns, "405 Method Not Allowed"); return; }
            var name = parts[1].TrimStart('/');
            int dot = name.IndexOf('.');
            if (dot > 0) name = name[..dot];
            if (!Tokens.TryGetValue(name, out var d)) { await RespondAsync(ns, "404 Not Found"); return; }
            long total = d.TotalLength;
            if (total <= 0) { await RespondAsync(ns, "404 Not Found"); return; }

            long start = 0, end = total - 1;
            bool ranged = false;
            foreach (var line in lines)
            {
                if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                var m = System.Text.RegularExpressions.Regex.Match(line, @"bytes=(\d*)-(\d*)");
                if (!m.Success) break;
                ranged = true;
                if (m.Groups[1].Value.Length > 0) start = long.Parse(m.Groups[1].Value);
                if (m.Groups[2].Value.Length > 0) end = Math.Min(total - 1, long.Parse(m.Groups[2].Value));
                if (m.Groups[1].Value.Length == 0 && m.Groups[2].Value.Length > 0) { start = Math.Max(0, total - long.Parse(m.Groups[2].Value)); end = total - 1; }
            }
            if (start > end || start >= total) { await RespondAsync(ns, "416 Range Not Satisfiable", $"Content-Range: bytes */{total}\r\n"); return; }

            string type = d.Type switch { "flac" => "audio/flac", "mp3" => "audio/mpeg", "ogg" => "audio/ogg", _ => "audio/mp4" };
            var sb = new System.Text.StringBuilder();
            sb.Append(ranged ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            sb.Append("Content-Type: ").Append(type).Append("\r\nAccept-Ranges: bytes\r\n");
            if (ranged) sb.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(total).Append("\r\n");
            sb.Append("Content-Length: ").Append(end - start + 1).Append("\r\nConnection: close\r\n\r\n");
            await ns.WriteAsync(System.Text.Encoding.ASCII.GetBytes(sb.ToString())).ConfigureAwait(false);
            if (parts[0] == "HEAD") return;

            var buf = new byte[64 * 1024];
            long pos = start;
            FileStream? fs = null;
            string? openFile = null;
            try
            {
                while (pos <= end)
                {
                    // 还没下载到这里就等；下载失败 / 取消了就断开（解码器当作出错，停止播放）
                    if (!await d.WaitForAsync(pos, CancellationToken.None).ConfigureAwait(false)) return;
                    var file = d.CurrentFile;
                    if (fs == null || openFile != file)
                    {
                        fs?.Dispose();
                        fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        openFile = file;
                    }
                    long readable = d.ReadableEnd(pos);
                    if (readable <= pos) continue;
                    long avail = Math.Min(end + 1, readable);
                    int want = (int)Math.Min(buf.Length, avail - pos);
                    if (want <= 0) continue;
                    fs.Position = pos;
                    int n = await fs.ReadAsync(buf.AsMemory(0, want)).ConfigureAwait(false);
                    if (n <= 0) { await Task.Delay(30).ConfigureAwait(false); continue; }
                    await ns.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                    pos += n;
                }
            }
            finally { fs?.Dispose(); }
        }
        catch { /* 解码器跳转时会主动断开旧连接 */ }
    }

    private static async Task<string> ReadHeadAsync(System.Net.Sockets.NetworkStream ns)
    {
        var sb = new System.Text.StringBuilder();
        var one = new byte[1];
        while (sb.Length < 8192)
        {
            if (await ns.ReadAsync(one).ConfigureAwait(false) <= 0) break;
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') break;
        }
        return sb.ToString();
    }

    private static Task RespondAsync(System.Net.Sockets.NetworkStream ns, string status, string extra = "")
        => ns.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{extra}Content-Length: 0\r\nConnection: close\r\n\r\n")).AsTask();
}
