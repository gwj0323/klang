using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using NAudio.Wave;

namespace Klang_WinUI.Services.Ai;

/// <summary>CLAP（HTSAT 音频编码器 + RoBERTa 文本编码器）：音频和文字映射到同一个 512 维空间。</summary>
internal sealed class ClapModel : IDisposable
{
    private const int Sr = 48000, Len = Sr * 10, NFft = 1024, Hop = 480, Mels = 64, Frames = 1001;
    private static readonly float[][] Filters = AiDsp.MelFilters(Sr, NFft, Mels, 50, 14000);
    private readonly object _audioLock = new(), _textLock = new();
    private InferenceSession? _audio, _text;
    private ByteBpeTokenizer? _tok;
    public bool AudioOnGpu { get; private set; }
    /// <summary>实际用的音频模型文件名（显卡版 / CPU 版算出的向量不能混用，主进程按它决定要不要重算）。</summary>
    public string AudioModelName { get; private set; } = "";

    private InferenceSession Audio()
    {
        if (_audio == null)
        {
            var path = AiRuntime.Pick(AiCatalog.Clap, "audio");
            var (s, g) = AiRuntime.Create(path, npu: true);
            _audio = s; AudioOnGpu = g; AudioModelName = Path.GetFileName(path);
        }
        return _audio;
    }

    private InferenceSession Text()
    {
        // 文本编码器每次只算几句话，放 CPU 就够快，不占显存
        _text ??= AiRuntime.Create(AiRuntime.Pick(AiCatalog.Clap, "text"), allowGpu: false, npu: true).Session;
        _tok ??= new ByteBpeTokenizer(AiRuntime.Pick(AiCatalog.Clap, "vocab"), AiRuntime.Pick(AiCatalog.Clap, "merges"));
        return _text;
    }

    /// <summary>
    /// 一首歌的音频向量：在 25% / 50% / 75% 处各取 10 秒，分别编码后取平均（单位长度）。
    /// start / end 是 CUE 分轨在整轨文件里的范围（end ≤ 0 = 到结尾）。
    /// </summary>
    public float[]? EmbedAudio(string path, double start, double end, double duration, CancellationToken ct)
    {
        double total = end > start ? end - start : duration;
        var offsets = total <= 14 ? new[] { start } : new[] { 0.25, 0.5, 0.75 }.Select(p => start + Math.Max(0, total * p - 5)).ToArray();
        float[]? sum = null;
        foreach (var off in offsets)
        {
            ct.ThrowIfCancellationRequested();
            var x = AiDsp.DecodeMono(path, Sr, off, 10);
            if (x.Length < Sr) continue;
            var e = EmbedWave(x);
            if (sum == null) sum = e;
            else for (int i = 0; i < sum.Length; i++) sum[i] += e[i];
        }
        if (sum == null) return null;
        AiDsp.Normalize(sum);
        return sum;
    }

    private float[] EmbedWave(float[] x)
    {
        // 不足 10 秒：重复填充（repeatpad），再补零
        var w = new float[Len];
        if (x.Length >= Len) Array.Copy(x, w, Len);
        else
        {
            int rep = Len / x.Length, p = 0;
            for (int r = 0; r < rep; r++) { Array.Copy(x, 0, w, p, x.Length); p += x.Length; }
        }
        var mel = AiDsp.MelPower(w, NFft, Hop, Filters, Frames);
        var input = new float[Frames * Mels];
        for (int f = 0; f < Frames; f++)
            for (int m = 0; m < Mels; m++)
                input[f * Mels + m] = 10f * MathF.Log10(Math.Max(mel[f][m], 1e-10f));

        lock (_audioLock)
        {
            var s = Audio();
            var inputs = new List<NamedOnnxValue>();
            foreach (var name in s.InputMetadata.Keys)
            {
                if (name == "input_features") inputs.Add(AiRuntime.Input(name, input, 1, 1, Frames, Mels));
                else if (name == "is_longer") inputs.Add(AiRuntime.Input(name, new[] { false }, 1, 1));
            }
            using var res = s.Run(inputs);
            var (data, _) = AiRuntime.Output(res, "audio_embeds");
            AiDsp.Normalize(data);
            return data;
        }
    }

    public float[] EmbedText(string text)
    {
        lock (_textLock)
        {
            var s = Text();
            var ids = new List<long> { 0 };
            ids.AddRange(_tok!.Encode(text).Take(75).Select(i => (long)i));
            ids.Add(2);
            int n = ids.Count;
            var inputs = new List<NamedOnnxValue>();
            foreach (var name in s.InputMetadata.Keys)
            {
                if (name == "input_ids") inputs.Add(AiRuntime.Input(name, ids.ToArray(), 1, n));
                else if (name == "attention_mask") inputs.Add(AiRuntime.Input(name, Enumerable.Repeat(1L, n).ToArray(), 1, n));
            }
            using var res = s.Run(inputs);
            var (data, _) = AiRuntime.Output(res, "text_embeds");
            AiDsp.Normalize(data);
            return data;
        }
    }

    public void Dispose() { _audio?.Dispose(); _text?.Dispose(); _audio = _text = null; }
}

/// <summary>multilingual-e5-small：多语言句向量（384 维），歌词检索用。</summary>
internal sealed class E5Model : IDisposable
{
    private readonly object _lock = new();
    private InferenceSession? _s;
    private UnigramTokenizer? _tok;

    public float[][] Embed(IReadOnlyList<string> texts)
    {
        lock (_lock)
        {
            _s ??= AiRuntime.Create(AiRuntime.Pick(AiCatalog.TextEmbed, "model"), allowGpu: false, npu: true).Session;
            _tok ??= new UnigramTokenizer(AiRuntime.Pick(AiCatalog.TextEmbed, "tokenizer"));
            var outp = new float[texts.Count][];
            const int Batch = 32;
            for (int b = 0; b < texts.Count; b += Batch)
            {
                var chunk = texts.Skip(b).Take(Batch).Select(t => _tok.Encode(t, 128)).ToList();
                int bs = chunk.Count, len = chunk.Max(c => c.Count);
                var ids = new long[bs * len]; var mask = new long[bs * len];
                for (int i = 0; i < bs; i++)
                    for (int j = 0; j < len; j++)
                    {
                        ids[i * len + j] = j < chunk[i].Count ? chunk[i][j] : 1;   // 1 = <pad>
                        mask[i * len + j] = j < chunk[i].Count ? 1 : 0;
                    }
                var inputs = new List<NamedOnnxValue>();
                foreach (var name in _s.InputMetadata.Keys)
                {
                    if (name == "input_ids") inputs.Add(AiRuntime.Input(name, ids, bs, len));
                    else if (name == "attention_mask") inputs.Add(AiRuntime.Input(name, mask, bs, len));
                    else if (name == "token_type_ids") inputs.Add(AiRuntime.Input(name, new long[bs * len], bs, len));
                }
                using var res = _s.Run(inputs);
                var (h, dims) = AiRuntime.Output(res, "last_hidden_state");
                int dim = dims[2];
                for (int i = 0; i < bs; i++)
                {
                    var v = new float[dim];
                    int cnt = 0;
                    for (int j = 0; j < len; j++)
                    {
                        if (mask[i * len + j] == 0) continue;
                        cnt++;
                        int off = (i * len + j) * dim;
                        for (int d = 0; d < dim; d++) v[d] += h[off + d];
                    }
                    for (int d = 0; d < dim; d++) v[d] /= Math.Max(1, cnt);
                    AiDsp.Normalize(v);
                    outp[b + i] = v;
                }
            }
            return outp;
        }
    }

    public void Dispose() { _s?.Dispose(); _s = null; }
}

/// <summary>
/// MDX-Net 人声分离（UVR-MDX-NET-Inst_HQ_3，输出伴奏）：44.1 kHz 立体声，6144 点 STFT，
/// 每块 256 帧送进模型得到伴奏的频谱，逆变换后拼接；人声 = 原曲 − 伴奏。
/// </summary>
internal sealed class MdxSeparator : IDisposable
{
    private const int Sr = 44100, NFft = 6144, Hop = 1024, DimF = 3072, DimT = 256;
    private const int Chunk = Hop * (DimT - 1), Trim = NFft / 2, Gen = Chunk - 2 * Trim;
    private const float Compensate = 1.022f;
    private readonly object _lock = new();
    private InferenceSession? _s;
    public bool OnGpu { get; private set; }

    /// <summary>
    /// 分离出伴奏（和人声）。边算边写：开始时就建好完整长度的 WAV 文件，每算完一段（约 5.8 秒）写到对应位置并调用
    /// <paramref name="ready"/>（这一段的起止秒数），主进程可以跟在后面播放。处理顺序从 <paramref name="from"/> 秒所在的段开始，
    /// 到结尾后再回头补前面；处理中 <paramref name="priority"/> 返回新位置（≥ 0）时，下一段就跳过去（用户拖了进度）。
    /// 写的过程中旁边有一个 .partial 标记文件，全部写完才删掉。
    /// </summary>
    public void Separate(string path, double start, double end, string instOut, string? vocalsOut, double from,
        Func<double> priority, Action<double> progress, Action<double, double>? ready, CancellationToken ct)
    {
        var (l, r) = AiDsp.DecodeStereo(path, Sr, start, end > start ? end - start : 0);
        int n = l.Length;
        if (n < Sr) throw new InvalidOperationException(L.T("音频太短"));
        int pad = Gen - n % Gen;
        int total = Trim + n + pad + Trim;
        var mixL = new float[total]; var mixR = new float[total];
        Array.Copy(l, 0, mixL, Trim, n); Array.Copy(r, 0, mixR, Trim, n);

        var fft = new Fft(NFft);
        var win = AiDsp.Hann(NFft);
        var specIn = new float[4 * DimF * DimT];
        var re = new float[NFft]; var im = new float[NFft];
        var bufL = new float[Chunk]; var bufR = new float[Chunk];
        var outL = new float[Chunk]; var outR = new float[Chunk];
        var bytes = new byte[Gen * 4];

        string marker = instOut + ".partial";
        File.WriteAllText(marker, "");
        using var fi = CreateWav(instOut, n);
        using var fv = vocalsOut != null ? CreateWav(vocalsOut, n) : null;

        lock (_lock)
        {
            if (_s == null) { var (s, g) = AiRuntime.Create(AiRuntime.Pick(AiCatalog.Separator, "model")); _s = s; OnGpu = g; }
            string inName = _s.InputMetadata.Keys.First();
            int chunks = (n + pad) / Gen;
            var done = new bool[chunks];
            int ChunkAt(double sec) => Math.Clamp((int)(sec * Sr / Gen), 0, chunks - 1);
            int cursor = ChunkAt(from);
            for (int k = 0; k < chunks; k++)
            {
                ct.ThrowIfCancellationRequested();
                double p = priority();
                if (p >= 0) cursor = ChunkAt(p);
                // 从 cursor 往后找第一段没做的（到结尾就绕回开头）
                int c = cursor;
                while (done[c]) c = (c + 1) % chunks;
                int i = c * Gen;

                Array.Copy(mixL, i, bufL, 0, Chunk);
                Array.Copy(mixR, i, bufR, 0, Chunk);
                Stft(bufL, 0, specIn, fft, win, re, im);
                Stft(bufR, 1, specIn, fft, win, re, im);

                float[] specOut;
                using (var res = _s.Run(new[] { AiRuntime.Input(inName, specIn, 1, 4, DimF, DimT) }))
                    specOut = AiRuntime.Output(res).Data;

                Istft(specOut, 0, outL, fft, win, re, im);
                Istft(specOut, 1, outR, fft, win, re, im);

                // 写这一段：伴奏 = 模型输出 × 补偿；人声 = 原曲 − 伴奏
                int len = Math.Min(Gen, n - i);
                if (len > 0)
                {
                    long offset = 44 + (long)i * 4;
                    Pack(bytes, len, k2 => outL[Trim + k2] * Compensate, k2 => outR[Trim + k2] * Compensate);
                    fi.Position = offset; fi.Write(bytes, 0, len * 4); fi.Flush();
                    if (fv != null)
                    {
                        Pack(bytes, len, k2 => l[i + k2] - outL[Trim + k2] * Compensate, k2 => r[i + k2] - outR[Trim + k2] * Compensate);
                        fv.Position = offset; fv.Write(bytes, 0, len * 4); fv.Flush();
                    }
                }
                done[c] = true;
                cursor = (c + 1) % chunks;
                progress((k + 1.0) / chunks);
                if (len > 0) ready?.Invoke((double)i / Sr, (double)(i + len) / Sr);
            }
        }
        fi.Dispose();
        fv?.Dispose();
        File.Delete(marker);
    }

    /// <summary>建好完整长度的 16 位立体声 WAV（头里的长度一开始就写对，数据先全是 0），允许别的进程同时读。</summary>
    private static FileStream CreateWav(string path, int frames)
    {
        var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        int data = frames * 4;
        using (var w = new BinaryWriter(fs, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Sr); w.Write(Sr * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(data);
        }
        fs.SetLength(44L + data);
        fs.Flush();
        return fs;
    }

    private static void Pack(byte[] bytes, int len, Func<int, float> left, Func<int, float> right)
    {
        int pos = 0;
        for (int k = 0; k < len; k++)
        {
            short a = (short)Math.Clamp((int)MathF.Round(left(k) * 32767f), -32768, 32767);
            short b = (short)Math.Clamp((int)MathF.Round(right(k) * 32767f), -32768, 32767);
            bytes[pos++] = (byte)a; bytes[pos++] = (byte)(a >> 8);
            bytes[pos++] = (byte)b; bytes[pos++] = (byte)(b >> 8);
        }
    }

    /// <summary>一个声道的 STFT（center=True、反射填充），写进模型输入：通道顺序 左实、左虚、右实、右虚。</summary>
    private static void Stft(float[] x, int ch, float[] spec, Fft fft, float[] win, float[] re, float[] im)
    {
        int pad = NFft / 2;
        for (int t = 0; t < DimT; t++)
        {
            int s0 = t * Hop - pad;
            for (int i = 0; i < NFft; i++)
            {
                int j = s0 + i;
                if (j < 0) j = -j;
                if (j >= x.Length) j = 2 * x.Length - 2 - j;
                re[i] = x[j] * win[i]; im[i] = 0;
            }
            fft.Forward(re, im);
            int baseRe = (ch * 2) * DimF * DimT, baseIm = (ch * 2 + 1) * DimF * DimT;
            for (int f = 0; f < DimF; f++)
            {
                spec[baseRe + f * DimT + t] = re[f];
                spec[baseIm + f * DimT + t] = im[f];
            }
        }
    }

    /// <summary>逆 STFT：加窗重叠相加，再除以窗平方和（和 torch.istft 一致），去掉两端 center 填充。</summary>
    private static void Istft(float[] spec, int ch, float[] outp, Fft fft, float[] win, float[] re, float[] im)
    {
        int pad = NFft / 2, full = Chunk + 2 * pad;
        var acc = new float[full]; var norm = new float[full];
        int baseRe = (ch * 2) * DimF * DimT, baseIm = (ch * 2 + 1) * DimF * DimT;
        for (int t = 0; t < DimT; t++)
        {
            Array.Clear(re); Array.Clear(im);
            for (int f = 0; f < DimF; f++)
            {
                re[f] = spec[baseRe + f * DimT + t];
                im[f] = spec[baseIm + f * DimT + t];
            }
            // 第 3072 个频点（奈奎斯特）模型不输出，按 0；补上共轭对称的负频率
            for (int f = 1; f < NFft / 2; f++) { re[NFft - f] = re[f]; im[NFft - f] = -im[f]; }
            im[0] = 0;
            fft.Inverse(re, im);
            int s0 = t * Hop;
            for (int i = 0; i < NFft; i++)
            {
                acc[s0 + i] += re[i] * win[i];
                norm[s0 + i] += win[i] * win[i];
            }
        }
        for (int i = 0; i < Chunk; i++)
        {
            float w = norm[i + pad];
            outp[i] = w > 1e-8f ? acc[i + pad] / w : 0f;
        }
    }

    public void Dispose() { _s?.Dispose(); _s = null; }
}

/// <summary>Whisper small：从人声里识别带时间戳的歌词（贪心解码，按 30 秒一段往后推）。</summary>
internal sealed class WhisperModel : IDisposable
{
    private const int Sr = 16000, NFft = 400, Hop = 160, Mels = 80, Frames = 3000, Window = Sr * 30;
    private const int Eot = 50257, Sot = 50258, LangBegin = 50259, Transcribe = 50359, SotPrev = 50361, NoTimestamps = 50363, TsBegin = 50364;
    private const int MaxTokens = 224;
    private static readonly float[][] Filters = AiDsp.MelFilters(Sr, NFft, Mels, 0, 8000);

    public static readonly string[] Languages =
    {
        "en","zh","de","es","ru","ko","fr","ja","pt","tr","pl","ca","nl","ar","sv","it","id","hi","fi","vi","he","uk","el","ms","cs","ro","da","hu","ta","no",
        "th","ur","hr","bg","lt","la","mi","ml","cy","sk","te","fa","lv","bn","sr","az","sl","kn","et","mk","br","eu","is","hy","ne","mn","bs","kk","sq","sw",
        "gl","mr","pa","si","km","sn","yo","so","af","oc","ka","be","tg","sd","gu","am","yi","lo","uz","fo","ht","ps","tk","nn","mt","sa","lb","my","bo","tl",
        "mg","as","tt","haw","ln","ha","ba","jw","su"
    };

    private readonly object _lock = new();
    private InferenceSession? _enc, _dec;
    private ByteBpeTokenizer? _tok;
    public bool OnGpu { get; private set; }

    public sealed record Segment(double Start, double End, string Text);

    private void Ensure()
    {
        if (_enc == null) { var (s, g) = AiRuntime.Create(AiRuntime.Pick(AiCatalog.Whisper, "encoder")); _enc = s; OnGpu = g; }
        _dec ??= AiRuntime.Create(AiRuntime.Pick(AiCatalog.Whisper, "decoder")).Session;
        _tok ??= new ByteBpeTokenizer(AiRuntime.Pick(AiCatalog.Whisper, "vocab"), AiRuntime.Pick(AiCatalog.Whisper, "merges"));
    }

    /// <summary>
    /// 识别 x（16 kHz 单声道）。language 为空时自动判断。
    /// 只对有声音的部分解码：整段 30 秒都很安静（纯伴奏去掉人声后）就直接跳过，减少“幻听”。
    /// </summary>
    public (string Language, List<Segment> Segments) Run(float[] x, string? language, Action<double> progress, CancellationToken ct, bool refineOnsets = false)
    {
        lock (_lock)
        {
            Ensure();
            // 每 0.5 秒的能量，用来跳过静音
            int blk = Sr / 2;
            var rms = new float[(x.Length + blk - 1) / blk];
            for (int b = 0; b < rms.Length; b++)
            {
                double s = 0; int cnt = 0;
                for (int i = b * blk; i < Math.Min(x.Length, (b + 1) * blk); i++) { s += x[i] * x[i]; cnt++; }
                rms[b] = (float)Math.Sqrt(s / Math.Max(1, cnt));
            }
            float peak = rms.Length == 0 ? 0 : rms.OrderByDescending(v => v).Skip(rms.Length / 20).FirstOrDefault();
            float thr = Math.Max(0.004f, peak * 0.08f);
            bool Active(int sample) { int b = sample / blk; return b < rms.Length && rms[b] >= thr; }

            string lang = language ?? "";
            if (string.IsNullOrEmpty(lang))
            {
                // 用最响的那 30 秒判断语言
                int bestStart = 0; double bestE = -1;
                for (int s0 = 0; s0 < Math.Max(1, x.Length - Window / 2); s0 += Sr * 5)
                {
                    double e = 0;
                    for (int b = s0 / blk; b < Math.Min(rms.Length, (s0 + Window) / blk); b++) e += rms[b];
                    if (e > bestE) { bestE = e; bestStart = s0; }
                }
                var enc0 = Encode(x, bestStart);
                var logits = LastLogits(new long[] { Sot }, enc0);
                int best = 0;
                for (int i = 1; i < Languages.Length; i++) if (logits[LangBegin + i] > logits[LangBegin + best]) best = i;
                lang = Languages[best];
            }
            int langId = LangBegin + Math.Max(0, Array.IndexOf(Languages, lang));

            // 中文给个简体提示，避免输出繁体
            var prompt = new List<long>();
            if (lang == "zh") { prompt.Add(SotPrev); prompt.AddRange(_tok!.Encode("以下是普通话的歌词。").Select(i => (long)i)); }
            prompt.AddRange(new long[] { Sot, langId, Transcribe });

            var segs = new List<Segment>();
            int seek = 0;
            while (seek < x.Length)
            {
                ct.ThrowIfCancellationRequested();
                progress(Math.Min(1, (double)seek / x.Length));
                // 跳过开头的静音
                while (seek < x.Length && !Active(seek)) seek += blk;
                if (seek >= x.Length) break;
                bool any = false;
                for (int s = seek; s < Math.Min(x.Length, seek + Window); s += blk) if (Active(s)) { any = true; break; }
                if (!any) { seek += Window; continue; }

                var enc = Encode(x, seek);
                var (tokens, hitLimit) = Decode(prompt, enc, ct);
                double offset = (double)seek / Sr;
                var (windowSegs, lastEnd, open) = Parse(tokens);
                foreach (var sg in windowSegs)
                {
                    var text = sg.Text.Trim();
                    if (text.Length == 0 || IsNoiseTag(text)) continue;
                    // 同一句连续重复三次以上多半是解码打转，去掉
                    if (segs.Count >= 2 && segs[^1].Text == text && segs[^2].Text == text) continue;
                    segs.Add(new Segment(offset + sg.Start, offset + sg.End, text));
                }
                double advance = (hitLimit || open) && lastEnd > 1 ? lastEnd : 30;
                seek += (int)(Math.Max(1, advance) * Sr);
            }
            progress(1);
            return (lang, refineOnsets ? RefineOnsets(x, segs) : segs);
        }
    }

    /// <summary>
    /// Whisper 的句子开始时间常把前面的空白也算进来（上一句结束 = 这一句开始），比真正开口早零点几秒。
    /// 在纯人声上找每句里能量第一次明显起来的位置，把开始时间挪过去（只往后挪，最多 2 秒）。
    /// 只对分离后的人声用：混着伴奏时能量起伏不代表开口。
    /// </summary>
    private static List<Segment> RefineOnsets(float[] x, List<Segment> segs)
    {
        const int hop = Sr / 50;   // 20 ms
        int frames = x.Length / hop;
        var rms = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double s = 0;
            for (int i = f * hop; i < (f + 1) * hop; i++) s += x[i] * x[i];
            rms[f] = (float)Math.Sqrt(s / hop);
        }
        var outp = new List<Segment>(segs.Count);
        foreach (var sg in segs)
        {
            int a = Math.Clamp((int)(sg.Start * 50), 0, Math.Max(0, frames - 1));
            int b = Math.Clamp((int)(Math.Min(sg.End, sg.Start + 2.0) * 50), a, Math.Max(0, frames - 1));
            int c = Math.Clamp((int)(sg.End * 50), a, Math.Max(0, frames - 1));
            float peak = 0;
            for (int f = a; f <= c; f++) peak = Math.Max(peak, rms[f]);
            float thr = Math.Max(0.003f, peak * 0.2f);
            int onset = a;
            for (int f = a; f <= b; f++) if (rms[f] >= thr) { onset = f; break; }
            double start = Math.Min(onset / 50.0, sg.End - 0.2);
            outp.Add(new Segment(Math.Max(sg.Start, start), sg.End, sg.Text));
        }
        return outp;
    }

    private (float[] Data, int[] Dims) Encode(float[] x, int start)
    {
        var w = new float[Window];
        Array.Copy(x, start, w, 0, Math.Min(Window, x.Length - start));
        var mel = AiDsp.MelPower(w, NFft, Hop, Filters, Frames + 1);
        var input = new float[Mels * Frames];
        float max = float.MinValue;
        for (int f = 0; f < Frames; f++)
            for (int m = 0; m < Mels; m++)
            {
                float v = MathF.Log10(Math.Max(mel[f][m], 1e-10f));
                input[m * Frames + f] = v;
                if (v > max) max = v;
            }
        for (int i = 0; i < input.Length; i++) input[i] = (Math.Max(input[i], max - 8f) + 4f) / 4f;
        using var res = _enc!.Run(new[] { AiRuntime.Input(_enc.InputMetadata.Keys.First(), input, 1, Mels, Frames) });
        return AiRuntime.Output(res, "last_hidden_state");
    }

    /// <summary>
    /// 跑一步解码器，返回最后一个位置的 logits。merged 解码器（有 use_cache_branch 输入）带 KV 缓存：
    /// past = null 时从头算整段 ids，并返回各层的 present 供下一步用；之后每步只送新的一个 token。
    /// 普通解码器没有缓存，每步都要把整段 ids 重算一遍。
    /// </summary>
    private (float[] Logits, Dictionary<string, (float[] Data, int[] Dims)>? Present) Step(long[] ids, (float[] Data, int[] Dims) enc,
        Dictionary<string, (float[] Data, int[] Dims)>? past)
    {
        var meta = _dec!.InputMetadata;
        bool merged = meta.ContainsKey("use_cache_branch");
        var inputs = new List<NamedOnnxValue>();
        foreach (var (name, m) in meta)
        {
            if (name == "input_ids") inputs.Add(AiRuntime.Input(name, ids, 1, ids.Length));
            else if (name == "encoder_hidden_states") inputs.Add(AiRuntime.Input(name, enc.Data, enc.Dims));
            else if (name == "use_cache_branch") inputs.Add(AiRuntime.Input(name, new[] { past != null }, 1));
            else if (name.StartsWith("past_key_values."))
            {
                if (past != null && past.TryGetValue(name, out var pv)) inputs.Add(AiRuntime.Input(name, pv.Data, pv.Dims));
                else
                {
                    // 第一步没有缓存：送长度为 0 的占位（形状 [1, 头数, 0, 每头维度]）
                    var d = m.Dimensions;
                    inputs.Add(AiRuntime.Input(name, Array.Empty<float>(), 1, d.Length > 1 && d[1] > 0 ? d[1] : 12, 0, d.Length > 3 && d[3] > 0 ? d[3] : 64));
                }
            }
        }
        using var res = _dec.Run(inputs);
        var (logits, dims) = AiRuntime.Output(res, "logits");
        int vocab = dims[2];
        var last = new float[vocab];
        Array.Copy(logits, (dims[1] - 1) * vocab, last, 0, vocab);
        if (!merged) return (last, null);
        var present = new Dictionary<string, (float[] Data, int[] Dims)>();
        foreach (var r in res)
        {
            if (!r.Name.StartsWith("present.")) continue;
            var tensor = r.AsTensor<float>();
            var data = tensor is Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float> dt ? dt.Buffer.ToArray() : tensor.ToArray();
            present["past_key_values." + r.Name["present.".Length..]] = (data, tensor.Dimensions.ToArray());
        }
        // 带缓存的分支里编码器部分的 present 可能是空的：沿用第一步算出的
        if (past != null)
            foreach (var kv in past)
                if (kv.Key.Contains(".encoder.") && (!present.TryGetValue(kv.Key, out var cur) || cur.Data.Length == 0)) present[kv.Key] = kv.Value;
        return (last, present);
    }

    private float[] LastLogits(long[] ids, (float[] Data, int[] Dims) enc) => Step(ids, enc, null).Logits;

    private (List<int> Tokens, bool HitLimit) Decode(List<long> prompt, (float[] Data, int[] Dims) enc, CancellationToken ct)
    {
        var seq = new List<long>(prompt);
        var outp = new List<int>();
        int maxNew = MaxTokens - prompt.Count;
        Dictionary<string, (float[] Data, int[] Dims)>? cache = null;
        for (int step = 0; step < maxNew; step++)
        {
            ct.ThrowIfCancellationRequested();
            float[] logits;
            if (cache == null)
            {
                (logits, cache) = Step(seq.ToArray(), enc, null);
            }
            else
            {
                (logits, cache) = Step(new[] { seq[^1] }, enc, cache);
            }
            ApplyRules(logits, outp);
            int next = 0;
            for (int i = 1; i < logits.Length; i++) if (logits[i] > logits[next]) next = i;
            if (next == Eot) return (outp, false);
            outp.Add(next);
            seq.Add(next);
            if (Looping(outp)) return (outp, true);
        }
        return (outp, true);
    }
    /// <summary>时间戳规则（同 Whisper 原版）：开头必须是时间戳、时间戳成对出现、不能倒退；时间戳总概率更大时就出时间戳。</summary>
    private static void ApplyRules(float[] logits, List<int> outp)
    {
        int vocab = logits.Length;
        for (int i = Sot; i < TsBegin && i < vocab; i++) logits[i] = float.NegativeInfinity;   // 特殊 token（含“无时间戳”）
        if (outp.Count == 0)
        {
            for (int i = 0; i < TsBegin; i++) logits[i] = float.NegativeInfinity;           // 第一个必须是时间戳
            for (int i = TsBegin + 51; i < vocab; i++) logits[i] = float.NegativeInfinity;  // 且不晚于 1 秒
            return;
        }
        bool lastTs = outp[^1] >= TsBegin;
        bool penultTs = outp.Count < 2 || outp[^2] >= TsBegin;
        if (lastTs)
        {
            if (penultTs) for (int i = TsBegin; i < vocab; i++) logits[i] = float.NegativeInfinity;     // 刚出完一对：接文字
            else for (int i = 0; i < Eot; i++) logits[i] = float.NegativeInfinity;                     // 文字后的时间戳：接下一个时间戳或结束
        }
        int lastTsVal = -1;
        for (int i = outp.Count - 1; i >= 0; i--) if (outp[i] >= TsBegin) { lastTsVal = outp[i]; break; }
        if (lastTsVal >= 0)
        {
            int limit = lastTs && !penultTs ? lastTsVal : lastTsVal + 1;
            for (int i = TsBegin; i < Math.Min(limit, vocab); i++) logits[i] = float.NegativeInfinity;
        }
        // 时间戳整体概率 > 任何单个文字 token：选时间戳
        double maxTs = float.NegativeInfinity;
        for (int i = TsBegin; i < vocab; i++) if (logits[i] > maxTs) maxTs = logits[i];
        if (double.IsNegativeInfinity(maxTs)) return;
        double sum = 0;
        for (int i = TsBegin; i < vocab; i++) if (!float.IsNegativeInfinity(logits[i])) sum += Math.Exp(logits[i] - maxTs);
        double lse = maxTs + Math.Log(sum);
        double maxText = float.NegativeInfinity;
        for (int i = 0; i < TsBegin; i++) if (logits[i] > maxText) maxText = logits[i];
        if (lse > maxText) for (int i = 0; i < TsBegin; i++) logits[i] = float.NegativeInfinity;
    }

    /// <summary>
    /// Whisper 在没有人声的段落常输出“(音乐)”“[Music]”“♪♪”“字幕由…提供”这类标记，不是歌词。
    /// </summary>
    private static bool IsNoiseTag(string t)
    {
        var s = t.Trim();
        if (s.All(c => !char.IsLetterOrDigit(c))) return true;   // 只有符号（♪ …）
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[\(\[（【\*].{0,12}[\)\]）】\*]$")) return true;
        string[] junk = { "字幕", "订阅", "訂閱", "ご視聴", "チャンネル登録", "Thanks for watching", "Subtitles by", "Amara.org" };
        return junk.Any(j => s.Contains(j, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>最后 8 个以上的 token 在打转（同一段反复出现 4 次）。</summary>
    private static bool Looping(List<int> t)
    {
        for (int p = 2; p <= 12; p++)
        {
            if (t.Count < p * 4) continue;
            bool rep = true;
            for (int k = 1; k < 4 && rep; k++)
                for (int i = 0; i < p; i++)
                    if (t[t.Count - 1 - i] != t[t.Count - 1 - i - k * p]) { rep = false; break; }
            if (rep) return true;
        }
        return false;
    }

    /// <summary>
    /// 把 token 序列拆成句子：正常是「时间戳 文字 时间戳」一句、句与句之间两个时间戳相邻。
    /// open = 最后一句只有开始时间（被长度上限截断）。
    /// </summary>
    private (List<Segment> Segs, double LastEnd, bool Open) Parse(List<int> t)
    {
        var segs = new List<Segment>();
        double lastEnd = 0;
        bool open = false;
        int i = 0;
        while (i < t.Count)
        {
            if (t[i] < TsBegin) { i++; continue; }
            double s = (t[i] - TsBegin) * 0.02;
            int j = i + 1;
            var text = new List<int>();
            while (j < t.Count && t[j] < TsBegin) { text.Add(t[j]); j++; }
            if (j >= t.Count) { open = text.Count > 0; break; }
            double e = (t[j] - TsBegin) * 0.02;
            if (text.Count > 0) segs.Add(new Segment(s, Math.Max(e, s + 0.2), _tok!.Decode(text)));
            lastEnd = e;
            // 后面紧跟时间戳：那是下一句的开始；直接跟文字：这个结束时间兼作下一句的开始
            i = j + 1 < t.Count && t[j + 1] >= TsBegin ? j + 1 : (text.Count > 0 ? j : j + 1);
        }
        return (segs, lastEnd, open);
    }
    public void Dispose() { _enc?.Dispose(); _dec?.Dispose(); _enc = _dec = null; }
}
