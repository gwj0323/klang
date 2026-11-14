using System.Buffers.Binary;
using System.Text;

namespace Klang_WinUI.Services.Soda;

/// <summary>容器 + 编码 + 时长探测。只读文件头与必要 box，不做转码决策。</summary>
internal static class SodaMedia
{
    public sealed record Info(string Container, string Codec, double Duration, long Size);

    public static Info? Probe(string path, long maxBytes = 200L * 1024 * 1024)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0 || file.Length > maxBytes) return null;
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[12];
            if (stream.Read(head) < 8) return null;

            if (head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C')
                return new Info("FLAC", "FLAC", ReadFlacDuration(stream), file.Length);
            if (head[0] == 'I' && head[1] == 'D' && head[2] == '3')
                return new Info("MP3", "MP3", 0, file.Length);
            if (head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')
                return ProbeMp4(stream, file.Length);
            if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)
                return new Info("MP3", "MP3", 0, file.Length);
            return null;
        }
        catch { return null; }
    }

    private sealed record Box(string Type, long Offset, long Size, long Header);

    private static IEnumerable<Box> Boxes(Stream stream, long begin, long end)
    {
        var header = new byte[16];
        var position = begin;
        while (position + 8 <= end)
        {
            stream.Position = position;
            if (stream.Read(header, 0, 8) < 8) yield break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(header, 4, 4);
            long boxHeader = 8;
            if (size == 1)
            {
                if (stream.Read(header, 8, 8) < 8) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                boxHeader = 16;
            }
            else if (size == 0) size = end - position;
            if (size < boxHeader || position + size > end) yield break;
            yield return new Box(type, position, size, boxHeader);
            position += size;
        }
    }

    private static Info? ProbeMp4(Stream stream, long length)
    {
        var moov = Boxes(stream, 0, length).FirstOrDefault(b => b.Type == "moov");
        if (moov == null) return null;
        var buffer = new byte[Math.Min(moov.Size, 4L * 1024 * 1024)];
        stream.Position = moov.Offset;
        stream.ReadExactly(buffer, 0, buffer.Length);

        double duration = 0;
        string codec = "";
        foreach (var box in BoxesIn(buffer, (int)moov.Header, buffer.Length))
        {
            if (box.Type == "mvhd")
            {
                duration = ParseMvhd(buffer, (int)box.Offset + 4);
            }
            else if (box.Type == "trak")
            {
                foreach (var trakChild in BoxesIn(buffer, (int)(box.Offset + box.Header), (int)(box.Offset + box.Size)))
                {
                    if (trakChild.Type != "mdia") continue;
                    var mediaChildren = BoxesIn(buffer, (int)(trakChild.Offset + trakChild.Header), (int)(trakChild.Offset + trakChild.Size)).ToArray();
                    var handler = mediaChildren.FirstOrDefault(b => b.Type == "hdlr");
                    if (handler != null && handler.Size >= handler.Header + 12 &&
                        Encoding.ASCII.GetString(buffer, (int)(handler.Offset + handler.Header) + 8, 4) != "soun") continue;
                    foreach (var mdiaChild in mediaChildren)
                    {
                        if (mdiaChild.Type != "minf") continue;
                        foreach (var minfChild in BoxesIn(buffer, (int)(mdiaChild.Offset + mdiaChild.Header), (int)(mdiaChild.Offset + mdiaChild.Size)))
                        {
                            if (minfChild.Type != "stbl") continue;
                            foreach (var stblChild in BoxesIn(buffer, (int)(minfChild.Offset + minfChild.Header), (int)(minfChild.Offset + minfChild.Size)))
                            {
                                if (stblChild.Type != "stsd") continue;
                                codec = ReadStsdCodec(buffer, (int)stblChild.Offset, (int)stblChild.Size);
                            }
                        }
                    }
                }
            }
        }

        if (codec.Length == 0) codec = "UNKNOWN";
        var container = codec == "FLAC" ? "M4A-FLAC" : "M4A";
        return new Info(container, codec, duration, length);
    }

    private static IEnumerable<Box> BoxesIn(byte[] data, int begin, int end)
    {
        var position = begin;
        while (position + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, 4));
            var type = Encoding.ASCII.GetString(data, position + 4, 4);
            long header = 8;
            if (size == 1 && position + 16 <= end)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(position + 8, 8));
                header = 16;
            }
            else if (size == 0) size = end - position;
            if (size < header || position + size > end) yield break;
            yield return new Box(type, position, size, header);
            position += (int)size;
        }
    }

    private static string ReadStsdCodec(byte[] data, int offset, int size)
    {
        // stsd: version/flags(4) + entry_count(4) + sample entry
        var entry = offset + 16;
        if (entry + 8 > offset + size) return "";
        var type = Encoding.ASCII.GetString(data, entry + 4, 4);
        if (type == "enca")
        {
            // Encryption changes the sample-entry type. sinf/frma preserves its original codec.
            int end = offset + size;
            foreach (var child in BoxesIn(data, entry + 36, end))
            {
                if (child.Type != "sinf") continue;
                var original = BoxesIn(data, (int)(child.Offset + child.Header), (int)(child.Offset + child.Size))
                    .FirstOrDefault(b => b.Type == "frma" && b.Size >= b.Header + 4);
                if (original != null) type = Encoding.ASCII.GetString(data, (int)(original.Offset + original.Header), 4);
            }
        }
        return type switch
        {
            "mp4a" => ReadAacProfile(data, entry) ? "AAC-LC" : "AAC-OTHER",
            "alac" => "ALAC",
            "fLaC" => "FLAC",
            "ac-3" => "AC3",
            "ec-3" => "EAC3",
            _ => type.Trim().ToUpperInvariant(),
        };
    }

    private static bool ReadAacProfile(byte[] data, int entry)
    {
        int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry, 4)));
        int end = checked(entry + length);
        if (length < 36 || end > data.Length || BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(entry + 16, 2)) != 0)
            return false;
        var esds = BoxesIn(data, entry + 36, end).FirstOrDefault(b => b.Type == "esds");
        if (esds == null || esds.Size < esds.Header + 4) return false;
        try
        {
            var asc = FindAudioSpecificConfig(data, (int)(esds.Offset + esds.Header) + 4,
                (int)(esds.Offset + esds.Size), 0);
            return asc is { Length: >= 2 } && (asc[0] >> 3) == 2;
        }
        catch (InvalidDataException) { return false; }
    }

    private static byte[]? FindAudioSpecificConfig(byte[] data, int begin, int end, int depth)
    {
        if (depth > 3) throw new InvalidDataException();
        for (int p = begin; p < end;)
        {
            byte tag = data[p++];
            int size = 0, count = 0;
            byte part;
            do
            {
                if (p >= end || ++count > 4) throw new InvalidDataException();
                part = data[p++]; size = (size << 7) | (part & 127);
            } while ((part & 128) != 0);
            if (size > end - p) throw new InvalidDataException();
            int next = p + size;
            if (tag == 5) return data.AsSpan(p, size).ToArray();
            int children = next;
            if (tag == 3)
            {
                if (size < 3) throw new InvalidDataException();
                byte flags = data[p + 2]; children = p + 3;
                if ((flags & 128) != 0) children += 2;
                if ((flags & 64) != 0) { if (children >= next) throw new InvalidDataException(); children += data[children] + 1; }
                if ((flags & 32) != 0) children += 2;
            }
            else if (tag == 4) children = p + 13;
            if (children > next) throw new InvalidDataException();
            if (children < next && FindAudioSpecificConfig(data, children, next, depth + 1) is { } asc) return asc;
            p = next;
        }
        return null;
    }

    private static double ReadFlacDuration(Stream stream)
    {
        // fLaC 之后是 4 字节 block 头，STREAMINFO 正文从其第 4 字节开始：
        // rate = STREAMINFO[10..12] → 绝对 14..16；total = （低 4 位 + 4 字节）→ 绝对 17..21
        var buffer = new byte[4 + 34];
        stream.Position = 4;
        if (stream.Read(buffer, 0, buffer.Length) < buffer.Length) return 0;
        var rate = (buffer[14] << 12) | (buffer[15] << 4) | (buffer[16] >> 4);
        // After 20 rate bits, 3 channel bits and 5 depth bits, 36 sample-count bits remain.
        long total = ((long)(buffer[17] & 0x0F) << 32) | ((long)buffer[18] << 24) | ((long)buffer[19] << 16)
                     | ((long)buffer[20] << 8) | buffer[21];
        return rate > 0 ? (double)total / rate : 0;
    }

    private static double ParseMvhd(byte[] data, int boxStart)
    {
        // boxStart 指向 box 类型位置（偏移 +4），box 长度在前 4 字节
        var content = boxStart + 4;
        if (content + 20 > data.Length) return 0;
        var version = data[content];
        if (version == 1)
        {
            var timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(content + 20, 4));
            var duration = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(content + 24, 8));
            return timescale > 0 ? (double)duration / timescale : 0;
        }
        var scale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(content + 12, 4));
        var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(content + 16, 4));
        return scale > 0 ? (double)length / scale : 0;
    }
}
