using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhisperDesk.Services.Npu;

/// <summary>
/// Whisper's GPT-2 style byte-level BPE (vocab.json + merges.txt) and its special tokens (added_tokens.json,
/// generation_config.json). All ids are read from the model folder: multilingual base/small and large-v3-turbo
/// number their special tokens differently.
/// </summary>
public sealed class WhisperTokenizer
{
    private static readonly Regex Pretokenize = new(
        @"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+", RegexOptions.Compiled);

    private readonly Dictionary<string, int> _vocab;
    private readonly string[] _idToToken;
    private readonly Dictionary<(string, string), int> _ranks = new();
    private readonly Dictionary<string, int[]> _bpeCache = new();
    private static readonly char[] ByteToChar = BuildByteToChar();
    private static readonly Dictionary<char, byte> CharToByte = ByteToChar.Select((c, b) => (c, (byte)b)).ToDictionary(x => x.c, x => x.Item2);

    public int Eot { get; }
    public int Sot { get; }
    public int Transcribe { get; }
    public int Translate { get; }
    public int StartOfPrev { get; }
    public int NoTimestamps { get; }
    public int NoSpeech { get; }
    public int TimestampBegin { get; }
    public int VocabSize { get; }
    public int MaxInitialTimestampIndex { get; }
    public IReadOnlyDictionary<string, int> Languages { get; }
    public IReadOnlyList<int> SuppressTokens { get; }
    public IReadOnlyList<int> BeginSuppressTokens { get; }

    public WhisperTokenizer(string dir)
    {
        _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(dir, "vocab.json")))!;
        var added = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(dir, "added_tokens.json")))!;
        foreach (var (token, id) in added) _vocab[token] = id;
        VocabSize = _vocab.Values.Max() + 1;
        _idToToken = new string[VocabSize];
        foreach (var (token, id) in _vocab) _idToToken[id] = token;

        int rank = 0;
        foreach (var line in File.ReadLines(Path.Combine(dir, "merges.txt")))
        {
            if (line.StartsWith("#version") || line.Length == 0) continue;
            int sp = line.IndexOf(' ');
            if (sp > 0) _ranks[(line[..sp], line[(sp + 1)..])] = rank++;
        }

        using var gen = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "generation_config.json")));
        var g = gen.RootElement;
        int Id(string token) => _vocab.TryGetValue(token, out var v) ? v : throw new InvalidDataException($"Token {token} missing from the model.");
        Eot = g.TryGetProperty("eos_token_id", out var eos) ? eos.GetInt32() : Id("<|endoftext|>");
        Sot = g.TryGetProperty("decoder_start_token_id", out var sot) ? sot.GetInt32() : Id("<|startoftranscript|>");
        NoTimestamps = g.TryGetProperty("no_timestamps_token_id", out var nts) ? nts.GetInt32() : Id("<|notimestamps|>");
        StartOfPrev = g.TryGetProperty("prev_sot_token_id", out var prev) ? prev.GetInt32() : Id("<|startofprev|>");
        Transcribe = Id("<|transcribe|>");
        Translate = Id("<|translate|>");
        NoSpeech = _vocab.TryGetValue("<|nospeech|>", out var ns) ? ns : Id("<|nocaptions|>");
        TimestampBegin = Id("<|0.00|>");
        MaxInitialTimestampIndex = g.TryGetProperty("max_initial_timestamp_index", out var mit) ? mit.GetInt32() : 50;

        var langs = new Dictionary<string, int>();
        if (g.TryGetProperty("lang_to_id", out var l2i))
            foreach (var p in l2i.EnumerateObject()) langs[p.Name.Trim('<', '>', '|')] = p.Value.GetInt32();
        Languages = langs;
        SuppressTokens = g.TryGetProperty("suppress_tokens", out var st) ? st.EnumerateArray().Select(e => e.GetInt32()).ToList() : new List<int>();
        BeginSuppressTokens = g.TryGetProperty("begin_suppress_tokens", out var bst)
            ? bst.EnumerateArray().Select(e => e.GetInt32()).ToList() : new List<int> { Id("Ġ"), Eot };
    }

    public bool IsTimestamp(int id) => id >= TimestampBegin;
    public bool IsSpecial(int id) => id >= Eot;

    /// <summary>Language code ("en") of a language token id.</summary>
    public string? LanguageOf(int id) => Languages.FirstOrDefault(kv => kv.Value == id).Key;

    /// <summary>UTF-8 text of the given (non-special) tokens; special and timestamp tokens are skipped.</summary>
    public string Decode(IEnumerable<int> ids)
    {
        var bytes = new List<byte>();
        foreach (var id in ids)
        {
            if (id < 0 || id >= _idToToken.Length || IsSpecial(id) || _idToToken[id] is not { } token) continue;
            foreach (var c in token)
                if (CharToByte.TryGetValue(c, out var b)) bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public string TokenText(int id) => IsSpecial(id) ? (id < _idToToken.Length ? _idToToken[id] ?? "" : "") : Decode(new[] { id });

    /// <summary>Byte-level BPE encoding of plain text (used for the vocabulary hint).</summary>
    public List<int> Encode(string text)
    {
        var ids = new List<int>();
        foreach (Match m in Pretokenize.Matches(text))
        {
            var word = new string(Encoding.UTF8.GetBytes(m.Value).Select(b => ByteToChar[b]).ToArray());
            if (!_bpeCache.TryGetValue(word, out var pieces)) _bpeCache[word] = pieces = Bpe(word);
            ids.AddRange(pieces);
        }
        return ids;
    }

    private int[] Bpe(string word)
    {
        var parts = word.Select(c => c.ToString()).ToList();
        while (parts.Count > 1)
        {
            int best = -1, bestRank = int.MaxValue;
            for (int i = 0; i < parts.Count - 1; i++)
                if (_ranks.TryGetValue((parts[i], parts[i + 1]), out var r) && r < bestRank) { bestRank = r; best = i; }
            if (best < 0) break;
            parts[best] += parts[best + 1];
            parts.RemoveAt(best + 1);
        }
        return parts.Select(p => _vocab.TryGetValue(p, out var id) ? id : -1).Where(id => id >= 0).ToArray();
    }

    /// <summary>GPT-2's reversible byte → printable character map.</summary>
    private static char[] BuildByteToChar()
    {
        var map = new char[256];
        var direct = Enumerable.Range('!', '~' - '!' + 1).Concat(Enumerable.Range('¡', '¬' - '¡' + 1)).Concat(Enumerable.Range('®', 'ÿ' - '®' + 1)).ToHashSet();
        int extra = 0;
        for (int b = 0; b < 256; b++) map[b] = direct.Contains(b) ? (char)b : (char)(256 + extra++);
        return map;
    }
}
