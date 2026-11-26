using System;
using System.Collections.Generic;
using System.Linq;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Klang_WinUI.Services.Ai;

/// <summary>
/// 下载 AI 组件：普通文件支持断点续传，下完校验大小和 SHA-256；
/// NuGet 包里的文件用 HTTP Range 只读 zip 目录和那一个文件（DirectML 的包有 200 MB，真正要的只有十几 MB），解压后校验 CRC。
/// 先写到 .part，校验通过才改名，所以正式文件存在就一定是完整的。
/// </summary>
public static class AiDownloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10, ConnectTimeout = TimeSpan.FromSeconds(20) })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Klang-Player");
        return h;
    }

    /// <summary>下载还没有的文件。progress 报告 (已完成字节, 总字节)。</summary>
    public static async Task DownloadAsync(IReadOnlyList<AiFile> files, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        long total = files.Sum(f => f.Size), doneBefore = 0;
        foreach (var f in files)
        {
            var dest = AiCatalog.PathOf(f);
            if (File.Exists(dest)) { doneBefore += f.Size; progress?.Report((doneBefore, total)); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            long baseDone = doneBefore;
            var p = new Progress<long>(n => progress?.Report((baseDone + Math.Min(n, f.Size), total)));
            if (f.ZipEntry != null) await DownloadZipEntryAsync(f, dest, p, ct).ConfigureAwait(false);
            else await DownloadFileAsync(f, dest, p, ct).ConfigureAwait(false);
            doneBefore += f.Size;
            progress?.Report((doneBefore, total));
        }
    }

    private static async Task DownloadFileAsync(AiFile f, string dest, IProgress<long> progress, CancellationToken ct)
    {
        var part = dest + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (f.Size > 0 && have > f.Size) { File.Delete(part); have = 0; }

        if (f.Size <= 0 || have < f.Size)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, f.Url);
            if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (have > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0;   // 服务器不支持续传：从头来
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buf = new byte[1 << 16];
            long done = have;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                progress.Report(done);
            }
        }

        var len = new FileInfo(part).Length;
        if (f.Size > 0 && len != f.Size)
        {
            File.Delete(part);
            throw new IOException(L.F("文件大小不对（{0}），已删除，请重试", Path.GetFileName(dest)));
        }
        if (f.Sha256.Length > 0)
        {
            string hash;
            await using (var fs = File.OpenRead(part))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
            if (!hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new IOException(L.F("文件校验失败（{0}），已删除，请重试", Path.GetFileName(dest)));
            }
        }
        File.Move(part, dest, true);
    }

    private static async Task<byte[]> GetRangeAsync(string url, long from, long toInclusive, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(from, toInclusive);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent) throw new IOException(L.T("服务器不支持分段下载"));
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> GetLengthAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return resp.Content.Headers.ContentLength ?? throw new IOException(L.T("无法获取文件大小"));
    }

    private static async Task DownloadZipEntryAsync(AiFile f, string dest, IProgress<long> progress, CancellationToken ct)
    {
        long length = await GetLengthAsync(f.Url, ct).ConfigureAwait(false);

        // 1. zip 末尾的目录结束记录（EOCD）
        int tailLen = (int)Math.Min(length, 66000);
        var tail = await GetRangeAsync(f.Url, length - tailLen, length - 1, ct).ConfigureAwait(false);
        int eocd = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new IOException(L.T("组件包格式不对"));
        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));

        // 2. 中央目录里找到要的文件
        var cd = await GetRangeAsync(f.Url, cdOffset, cdOffset + cdSize - 1, ct).ConfigureAwait(false);
        long localOffset = -1, compSize = 0, size = 0; int method = 0; uint crc = 0;
        for (int p = 0; p + 46 <= cd.Length;)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p)) != 0x02014b50) break;
            int m = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 10));
            uint c = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 16));
            long cs = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 20));
            long us = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 24));
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 28));
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 30));
            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 32));
            long lho = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 42));
            string name = Encoding.UTF8.GetString(cd, p + 46, nameLen);
            if (string.Equals(Uri.UnescapeDataString(name), f.ZipEntry, StringComparison.OrdinalIgnoreCase))
            {
                localOffset = lho; compSize = cs; size = us; method = m; crc = c;
                break;
            }
            p += 46 + nameLen + extraLen + commentLen;
        }
        if (localOffset < 0) throw new IOException(L.F("组件包里没有 {0}", f.ZipEntry));
        if (method != 0 && method != 8) throw new IOException(L.T("组件包的压缩方式不支持"));

        // 3. 本地文件头（文件名和扩展字段的长度可能和中央目录里不同）
        var lh = await GetRangeAsync(f.Url, localOffset, localOffset + 29, ct).ConfigureAwait(false);
        int lNameLen = BinaryPrimitives.ReadUInt16LittleEndian(lh.AsSpan(26));
        int lExtraLen = BinaryPrimitives.ReadUInt16LittleEndian(lh.AsSpan(28));
        long dataStart = localOffset + 30 + lNameLen + lExtraLen;

        // 4. 只下载这一个文件的压缩数据，边下边解压
        var part = dest + ".part";
        using (var req = new HttpRequestMessage(HttpMethod.Get, f.Url))
        {
            req.Headers.Range = new RangeHeaderValue(dataStart, dataStart + compSize - 1);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent) throw new IOException(L.T("服务器不支持分段下载"));
            await using var raw = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var counted = new CountingStream(raw, n => progress.Report((long)((double)n / Math.Max(1, compSize) * size)));
            await using Stream data = method == 8 ? new DeflateStream(counted, CompressionMode.Decompress) : counted;
            await using var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var crc32 = new Crc32();
            var buf = new byte[1 << 16];
            int n;
            while ((n = await data.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                crc32.Append(buf.AsSpan(0, n));
                await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            }
            await fs.FlushAsync(ct).ConfigureAwait(false);
            if (crc32.GetCurrentHashAsUInt32() != crc || fs.Length != size)
            {
                fs.Close();
                File.Delete(part);
                throw new IOException(L.F("文件校验失败（{0}），已删除，请重试", Path.GetFileName(dest)));
            }
        }
        File.Move(part, dest, true);
    }

    /// <summary>zip 用的 CRC-32（多项式 0xEDB88320）。</summary>
    private sealed class Crc32
    {
        private static readonly uint[] Table = BuildTable();
        private uint _crc = 0xFFFFFFFF;
        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }
        public void Append(ReadOnlySpan<byte> data)
        {
            uint c = _crc;
            foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            _crc = c;
        }
        public uint GetCurrentHashAsUInt32() => _crc ^ 0xFFFFFFFF;
    }

    /// <summary>读多少报多少，用来显示压缩数据的下载进度。</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long> _report;
        private long _count;
        public CountingStream(Stream inner, Action<long> report) { _inner = inner; _report = report; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            _count += n; _report(_count);
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            _count += n; _report(_count);
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
