using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Klang_WinUI.Services.Ai;

/// <summary>
/// GPT-2 风格的字节级 BPE（CLAP 的 RoBERTa 文本编码器、Whisper 都用它）：读 vocab.json + merges.txt。
/// </summary>
internal sealed class ByteBpeTokenizer
{
    private static readonly Regex PreTokenize = new(
        @"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+",
        RegexOptions.Compiled);

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<int, string> _inverse;
    private readonly Dictionary<(string, string), int> _ranks = new();
    private readonly Dictionary<string, int[]> _cache = new();
    private static readonly char[] ByteToChar = BuildByteToChar();
    private static readonly Dictionary<char, byte> CharToByte = ByteToChar.Select((c, i) => (c, (byte)i)).ToDictionary(x => x.c, x => x.Item2);

    public ByteBpeTokenizer(string vocabJson, string mergesTxt)
    {
        _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabJson)) ?? new();
        _inverse = new Dictionary<int, string>(_vocab.Count);
        foreach (var kv in _vocab) _inverse[kv.Value] = kv.Key;
        int rank = 0;
        foreach (var line in File.ReadLines(mergesTxt))
        {
            if (line.StartsWith("#version") || line.Length == 0) continue;
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            _ranks[(line[..sp], line[(sp + 1)..])] = rank++;
        }
    }

    private static char[] BuildByteToChar()
    {
        var bs = new List<int>();
        for (int b = '!'; b <= '~'; b++) bs.Add(b);
        for (int b = 0xA1; b <= 0xAC; b++) bs.Add(b);
        for (int b = 0xAE; b <= 0xFF; b++) bs.Add(b);
        var cs = new List<int>(bs);
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if (bs.Contains(b)) continue;
            bs.Add(b); cs.Add(256 + n); n++;
        }
        var map = new char[256];
        for (int i = 0; i < bs.Count; i++) map[bs[i]] = (char)cs[i];
        return map;
    }

    public List<int> Encode(string text)
    {
        var ids = new List<int>();
        foreach (Match m in PreTokenize.Matches(text))
        {
            var bytes = Encoding.UTF8.GetBytes(m.Value);
            var sb = new StringBuilder(bytes.Length);
            foreach (var b in bytes) sb.Append(ByteToChar[b]);
            string word = sb.ToString();
            if (!_cache.TryGetValue(word, out var pieceIds))
            {
                pieceIds = Bpe(word).Select(p => _vocab.TryGetValue(p, out var id) ? id : -1).Where(id => id >= 0).ToArray();
                if (_cache.Count < 50000) _cache[word] = pieceIds;
            }
            ids.AddRange(pieceIds);
        }
        return ids;
    }

    private List<string> Bpe(string word)
    {
        var parts = word.Select(c => c.ToString()).ToList();
        while (parts.Count > 1)
        {
            int best = -1, bestRank = int.MaxValue;
            for (int i = 0; i + 1 < parts.Count; i++)
                if (_ranks.TryGetValue((parts[i], parts[i + 1]), out int r) && r < bestRank) { bestRank = r; best = i; }
            if (best < 0) break;
            string a = parts[best], b = parts[best + 1];
            var merged = new List<string>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                if (i + 1 < parts.Count && parts[i] == a && parts[i + 1] == b) { merged.Add(a + b); i++; }
                else merged.Add(parts[i]);
            }
            parts = merged;
        }
        return parts;
    }

    /// <summary>把 token 序列还原成文本（跳过词表里没有的 id，如特殊 token）。</summary>
    public string Decode(IEnumerable<int> ids)
    {
        var bytes = new List<byte>();
        foreach (var id in ids)
        {
            if (!_inverse.TryGetValue(id, out var tok)) continue;
            foreach (var c in tok) if (CharToByte.TryGetValue(c, out var b)) bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public int? IdOf(string token) => _vocab.TryGetValue(token, out var id) ? id : null;
}

/// <summary>
/// SentencePiece Unigram（XLM-RoBERTa / multilingual-e5 用）：从 tokenizer.json 读词表和分数，用 Viterbi 切分。
/// 规范化用 NFKC 近似原版的 precompiled charsmap，对检索来说足够。
/// </summary>
internal sealed class UnigramTokenizer
{
    private readonly Dictionary<string, (int Id, float Score)> _pieces = new();
    private readonly int _unk, _bos, _eos, _maxLen;
    private readonly float _unkScore;

    public UnigramTokenizer(string tokenizerJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(tokenizerJson));
        var model = doc.RootElement.GetProperty("model");
        _unk = model.TryGetProperty("unk_id", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 3;
        int id = 0;
        float minScore = 0;
        foreach (var item in model.GetProperty("vocab").EnumerateArray())
        {
            string piece = item[0].GetString() ?? "";
            float score = (float)item[1].GetDouble();
            _pieces.TryAdd(piece, (id, score));
            if (score < minScore) minScore = score;
            id++;
        }
        _unkScore = minScore - 10f;
        _maxLen = _pieces.Keys.Max(k => k.Length);
        _bos = _pieces.TryGetValue("<s>", out var b) ? b.Id : 0;
        _eos = _pieces.TryGetValue("</s>", out var e) ? e.Id : 2;
    }

    public List<int> Encode(string text, int maxTokens = 512)
    {
        var norm = text.Normalize(NormalizationForm.FormKC);
        norm = Regex.Replace(norm, @"\s+", " ").Trim();
        var ids = new List<int> { _bos };
        foreach (var word in norm.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            ids.AddRange(Viterbi("▁" + word));
            if (ids.Count >= maxTokens - 1) break;
        }
        if (ids.Count > maxTokens - 1) ids.RemoveRange(maxTokens - 1, ids.Count - (maxTokens - 1));
        ids.Add(_eos);
        return ids;
    }

    private List<int> Viterbi(string s)
    {
        // 按 UTF-16 下标切分；代理对不会被拆开，因为词表里没有半个代理对，未知时整字符走 unk
        int n = s.Length;
        var best = new float[n + 1];
        var back = new int[n + 1];
        var backId = new int[n + 1];
        for (int i = 1; i <= n; i++) best[i] = float.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            if (float.IsNegativeInfinity(best[i])) continue;
            bool any = false;
            for (int len = 1; len <= _maxLen && i + len <= n; len++)
            {
                if (_pieces.TryGetValue(s.Substring(i, len), out var p))
                {
                    any |= len == 1 || char.IsHighSurrogate(s[i]) && len == 2;
                    float sc = best[i] + p.Score;
                    if (sc > best[i + len]) { best[i + len] = sc; back[i + len] = i; backId[i + len] = p.Id; }
                }
            }
            if (!any)
            {
                int len = char.IsHighSurrogate(s[i]) && i + 1 < n ? 2 : 1;
                float sc = best[i] + _unkScore;
                if (sc > best[i + len]) { best[i + len] = sc; back[i + len] = i; backId[i + len] = _unk; }
            }
        }
        var outIds = new List<int>();
        for (int pos = n; pos > 0; pos = back[pos]) outIds.Add(backId[pos]);
        outIds.Reverse();
        // 连续的 unk 合并成一个
        var merged = new List<int>(outIds.Count);
        foreach (var x in outIds) if (!(x == _unk && merged.Count > 0 && merged[^1] == _unk)) merged.Add(x);
        return merged;
    }
}
