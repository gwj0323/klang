using System;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Klang_WinUI.Services.Ai;

/// <summary>
/// K 歌模式播放的音源：同时读分离出的伴奏和人声，按 <see cref="VocalGain"/> 实时混合
/// （0 = 纯伴奏，1 = 原曲）。拖动人声滑块时立即生效，不用重新分离。
/// </summary>
public sealed class KaraokeMixStream : WaveStream
{
    private static volatile float _vocalGain;
    /// <summary>保留的人声比例（0~1），所有正在播放的 K 歌音源共用。</summary>
    public static float VocalGain { get => _vocalGain; set => _vocalGain = Math.Clamp(value, 0f, 1f); }

    private readonly WaveFileReader _inst, _voc;
    private readonly WaveFormat _fmt;
    private byte[] _a = Array.Empty<byte>(), _b = Array.Empty<byte>();
    private float _lastGain = -1;

    private KaraokeMixStream(WaveFileReader inst, WaveFileReader voc)
    {
        _inst = inst;
        _voc = voc;
        _fmt = WaveFormat.CreateIeeeFloatWaveFormat(inst.WaveFormat.SampleRate, inst.WaveFormat.Channels);
    }

    /// <summary>路径是分离出的伴奏（*.inst.wav）且旁边有人声文件时，打开成混合音源；否则返回 null。</summary>
    public static KaraokeMixStream? TryOpen(string instPath)
    {
        const string suffix = ".inst.wav";
        if (!instPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        var vocPath = instPath[..^suffix.Length] + ".vocals.wav";
        if (!File.Exists(instPath) || !File.Exists(vocPath)) return null;
        WaveFileReader? a = null, b = null;
        try
        {
            a = new WaveFileReader(instPath);
            b = new WaveFileReader(vocPath);
            var f = a.WaveFormat;
            if (f.Encoding != WaveFormatEncoding.Pcm || f.BitsPerSample != 16 || !f.Equals(b.WaveFormat))
            {
                a.Dispose(); b.Dispose();
                return null;
            }
            return new KaraokeMixStream(a, b);
        }
        catch
        {
            a?.Dispose(); b?.Dispose();
            return null;
        }
    }

    public override WaveFormat WaveFormat => _fmt;
    public override long Length => _inst.Length * 2;

    public override long Position
    {
        get => _inst.Position * 2;
        set
        {
            long p = Math.Clamp(value / 2, 0, _inst.Length);
            p -= p % _inst.BlockAlign;
            _inst.Position = p;
            _voc.Position = Math.Min(p, _voc.Length);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int ch = _fmt.Channels;
        int frames = count / (4 * ch);
        if (frames <= 0) return 0;
        int inBytes = frames * ch * 2;
        if (_a.Length < inBytes) { _a = new byte[inBytes]; _b = new byte[inBytes]; }

        int na = ReadFull(_inst, _a, inBytes);
        na -= na % (ch * 2);
        if (na <= 0) return 0;
        int nb = ReadFull(_voc, _b, na);
        if (nb < na) Array.Clear(_b, nb, na - nb);

        var ia = MemoryMarshal.Cast<byte, short>(_a.AsSpan(0, na));
        var ib = MemoryMarshal.Cast<byte, short>(_b.AsSpan(0, na));
        var o = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, ia.Length * 4));

        // 人声比例变化时在这一块里线性过渡，拖滑块不会有咔哒声
        float g1 = VocalGain, g0 = _lastGain < 0 ? g1 : _lastGain;
        _lastGain = g1;
        const float scale = 1f / 32768f;
        if (g0 == g1)
        {
            for (int i = 0; i < ia.Length; i++) o[i] = (ia[i] + g1 * ib[i]) * scale;
        }
        else
        {
            int n = ia.Length / ch;
            for (int f = 0, i = 0; f < n; f++)
            {
                float g = g0 + (g1 - g0) * f / n;
                for (int c = 0; c < ch; c++, i++) o[i] = (ia[i] + g * ib[i]) * scale;
            }
        }
        return ia.Length * 4;
    }

    private static int ReadFull(WaveStream s, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _inst.Dispose(); _voc.Dispose(); }
        base.Dispose(disposing);
    }
}
