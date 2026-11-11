using System;
using NAudio.Wave;

namespace Klang_WinUI.Services.Dsp;

/// <summary>
/// 保持音高的变速播放（WSOLA：波形相似叠加）。Rate = 1.0 时完全旁路，不改动任何样本。
/// 帧长约 46ms、50% 重叠 Hann 窗；在 ±N/4 范围内搜索与上一帧自然延续相关性最高的起点，
/// 避免相位错位产生的颤音。
/// </summary>
public sealed class TimeStretchSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _ch;
    private readonly int _n;      // 帧长（每声道样本数）
    private readonly int _hs;     // 合成步长 = n / 2
    private readonly int _tol;    // 搜索容差
    private readonly float[] _win;

    private float[] _in;          // 交错输入缓冲
    private int _inFrames;        // 缓冲中有效帧数
    private long _inBase;         // 缓冲第 0 帧对应的绝对输入帧号
    private bool _sourceEnded;

    private double _nominal;      // 下一帧名义起点（绝对输入帧号）
    private long _prevPos;        // 上一帧实际起点
    private bool _hasPrev;
    private readonly float[] _tail; // 上一帧后半段（已乘窗）交错，长度 hs * ch
    private readonly float[] _tmpl; // 上一帧后半段原始样本（相关模板，单声道）
    private float[] _outBuf;      // 已合成待取走的交错输出
    private int _outCount, _outPos;
    private bool _flushed;

    public double Rate { get; set; } = 1.0;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public TimeStretchSampleProvider(ISampleProvider source)
    {
        _source = source;
        _ch = source.WaveFormat.Channels;
        int sr = source.WaveFormat.SampleRate;
        int n = 1024;
        while (n < sr * 0.046) n <<= 1;
        _n = n;
        _hs = n / 2;
        _tol = n / 4;
        _win = new float[n];
        for (int i = 0; i < n; i++)
            _win[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / n)); // 周期 Hann，50% 重叠和为 1

        _in = new float[(n * 4) * _ch];
        _tail = new float[_hs * _ch];
        _tmpl = new float[_hs];
        _outBuf = new float[_hs * _ch * 2];
    }

    /// <summary>清空内部状态（Seek / 切歌 / 速率回到 1.0 时调用）。</summary>
    public void Reset()
    {
        _inFrames = 0;
        _inBase = 0;
        _sourceEnded = false;
        _nominal = 0;
        _hasPrev = false;
        _prevPos = 0;
        _outCount = _outPos = 0;
        _flushed = false;
        Array.Clear(_tail);
    }

    public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public int Read(Span<float> buffer)
    {
        if (Math.Abs(Rate - 1.0) < 0.001)
        {
            if (_inFrames > 0 || _hasPrev) Reset();
            return _source.Read(buffer);
        }

        int written = 0;
        while (written < buffer.Length)
        {
            if (_outPos >= _outCount)
            {
                if (!ProduceChunk()) break;
            }
            int take = Math.Min(buffer.Length - written, _outCount - _outPos);
            _outBuf.AsSpan(_outPos, take).CopyTo(buffer.Slice(written));
            _outPos += take;
            written += take;
        }
        return written;
    }

    private bool ProduceChunk()
    {
        // 需要的输入范围： [nominal - tol, nominal + tol + n)
        long need = (long)_nominal + _tol + _n + 8;
        FillInput(need);

        long nomPos = (long)Math.Round(_nominal);
        long endAbs = _inBase + _inFrames;

        // 输入已耗尽且名义起点越过末尾 → 先吐出剩余尾巴，然后结束
        if (_sourceEnded && nomPos >= endAbs)
        {
            if (_flushed) return false;
            _flushed = true;
            EnsureOut(_hs * _ch);
            Array.Copy(_tail, 0, _outBuf, 0, _hs * _ch);
            _outCount = _hs * _ch;
            _outPos = 0;
            return true;
        }

        long pos = nomPos;
        if (_hasPrev)
        {
            // 在 [nomPos - tol, nomPos + tol] 搜索与模板最相似的位置
            long lo = Math.Max(nomPos - _tol, _inBase);
            long hi = nomPos + _tol;
            double best = double.NegativeInfinity;
            long bestPos = nomPos;
            for (long cand = lo; cand <= hi; cand++)
            {
                double c = 0;
                for (int i = 0; i < _hs; i += 4)
                {
                    long idx = cand + i - _inBase;
                    float s = idx >= 0 && idx < _inFrames ? _in[idx * _ch] : 0f;
                    c += s * _tmpl[i];
                }
                if (c > best) { best = c; bestPos = cand; }
            }
            pos = bestPos;
        }

        EnsureOut(_hs * _ch);
        for (int i = 0; i < _hs; i++)
        {
            long idx = pos + i - _inBase;
            for (int c = 0; c < _ch; c++)
            {
                float s = idx >= 0 && idx < _inFrames ? _in[idx * _ch + c] : 0f;
                _outBuf[i * _ch + c] = _tail[i * _ch + c] + s * _win[i];
            }
        }
        // 新的尾巴（后半段）与模板
        for (int i = 0; i < _hs; i++)
        {
            long idx = pos + _hs + i - _inBase;
            for (int c = 0; c < _ch; c++)
            {
                float s = idx >= 0 && idx < _inFrames ? _in[idx * _ch + c] : 0f;
                _tail[i * _ch + c] = s * _win[_hs + i];
                if (c == 0) _tmpl[i] = s;
            }
        }

        _outCount = _hs * _ch;
        _outPos = 0;
        _prevPos = pos;
        _hasPrev = true;
        _nominal += _hs * Rate;

        // 丢弃不再需要的旧输入
        long keepFrom = Math.Min(_prevPos, (long)_nominal - _tol) - 8;
        TrimInput(keepFrom);
        return true;
    }

    private void FillInput(long needAbsEnd)
    {
        while (!_sourceEnded && _inBase + _inFrames < needAbsEnd)
        {
            int chunkFrames = 4096;
            int freeFrames = _in.Length / _ch - _inFrames;
            if (freeFrames < chunkFrames)
            {
                Array.Resize(ref _in, _in.Length * 2);
            }
            int read = _source.Read(_in.AsSpan(_inFrames * _ch, chunkFrames * _ch));
            if (read <= 0)
            {
                _sourceEnded = true;
                break;
            }
            _inFrames += read / _ch;
        }
    }

    private void TrimInput(long keepFromAbs)
    {
        long drop = keepFromAbs - _inBase;
        if (drop <= 0) return;
        if (drop > _inFrames) drop = _inFrames;
        int remain = _inFrames - (int)drop;
        if (remain > 0)
            Array.Copy(_in, (int)drop * _ch, _in, 0, remain * _ch);
        _inFrames = remain;
        _inBase += drop;
    }

    private void EnsureOut(int samples)
    {
        if (_outBuf.Length < samples) _outBuf = new float[samples];
    }
}
