using System;
using NAudio.Wave;

namespace Klang_WinUI.Services.Dsp;

/// <summary>
/// 把 32 位浮点采样转成 16 / 24 / 32 位整数 PCM（给 WASAPI 独占模式用，声卡一般不收浮点）。
/// 不加抖动、只做四舍五入：源文件本身就是 ≤ 输出位深的整数 PCM 时，来回转换是逐位精确的。
/// </summary>
public sealed class FloatToPcmWaveProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private readonly int _bits;
    private float[] _buf = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public FloatToPcmWaveProvider(ISampleProvider source, WaveFormat outputFormat)
    {
        _source = source;
        WaveFormat = outputFormat;
        _bits = outputFormat.BitsPerSample;
        if (_bits is not (16 or 24 or 32)) throw new NotSupportedException("仅支持 16 / 24 / 32 位");
    }

    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public int Read(Span<byte> buffer)
    {
        int bytesPer = _bits / 8;
        int samples = buffer.Length / bytesPer;
        if (_buf.Length < samples) _buf = new float[samples];

        int read = _source.Read(_buf.AsSpan(0, samples));
        int o = 0;
        switch (_bits)
        {
            case 16:
                for (int i = 0; i < read; i++)
                {
                    int q = (int)Math.Clamp(MathF.Round(_buf[i] * 32768f), -32768f, 32767f);
                    buffer[o++] = (byte)q; buffer[o++] = (byte)(q >> 8);
                }
                break;
            case 24:
                for (int i = 0; i < read; i++)
                {
                    int q = (int)Math.Clamp(MathF.Round(_buf[i] * 8388608f), -8388608f, 8388607f);
                    buffer[o++] = (byte)q; buffer[o++] = (byte)(q >> 8); buffer[o++] = (byte)(q >> 16);
                }
                break;
            default:   // 32 位容器：24 位数据左移 8 位放进去（同样是精确的）
                for (int i = 0; i < read; i++)
                {
                    long q = (long)Math.Clamp(Math.Round(_buf[i] * 2147483648.0), -2147483648.0, 2147483647.0);
                    int v = (int)q;
                    buffer[o++] = (byte)v; buffer[o++] = (byte)(v >> 8); buffer[o++] = (byte)(v >> 16); buffer[o++] = (byte)(v >> 24);
                }
                break;
        }
        return read * bytesPer;
    }
}
