using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Whisper.net;

namespace WhisperDesk.Services.Npu;

/// <summary>
/// Whisper on ONNX Runtime + OpenVINO: the encoder runs on the NPU, the decoder on the CPU (or another OpenVINO
/// device). The decoding follows OpenAI's reference / whisper.cpp: 30-second windows, timestamp tokens, temperature
/// fallback on repetitive or unlikely output, "no speech" detection, and a short text context. Output is the same
/// <see cref="SegmentData"/> shape whisper.cpp produces (text with its leading space, start/end, probabilities, language).
/// </summary>
public sealed class OnnxWhisperEngine : IDisposable
{
    private const int SampleLen = 224, MaxCtx = 448, ContextTokens = 64;
    private static readonly float[] Temperatures = { 0f, 0.2f, 0.4f, 0.6f, 0.8f, 1.0f };

    private readonly InferenceSession _encoder, _decoder, _decoderWithPast;
    private readonly WhisperTokenizer _tok;
    private readonly LogMel _mel;
    private readonly string[] _decoderOutputs, _pastInputs, _pastOutputs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Random _random = new(1234);

    public string EncoderDevice { get; }
    public string DecoderDevice { get; }

    /// <param name="dir">Model folder from <see cref="WhisperOnnxModels.EnsureAsync"/>.</param>
    /// <param name="encoderDevice">OpenVINO device for the encoder ("NPU"; "CPU" for testing on a PC without an NPU).</param>
    /// <param name="decoderDevice">OpenVINO device for the decoder ("CPU" default; "NPU" is experimental, falls back to CPU).</param>
    /// <param name="stage">Receives each preparation step (compiling, loading, warming up) for the status bar.</param>
    public OnnxWhisperEngine(string dir, string encoderDevice, string decoderDevice, IProgress<string>? stage = null)
    {
        stage?.Report("Reading the model's vocabulary and settings…");
        _tok = new WhisperTokenizer(dir);
        using (var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "config.json"))))
            _mel = new LogMel(cfg.RootElement.TryGetProperty("num_mel_bins", out var m) ? m.GetInt32() : 80);

        try
        {
            // The NPU needs fixed shapes: one 30-second window of mel features.
            _encoder = OpenVinoSessions.Create(Path.Combine(dir, WhisperOnnxModels.Encoder), encoderDevice,
                new Dictionary<string, string> { ["reshape_input"] = $"input_features[1,{_mel.Mels},{LogMel.Frames}]" },
                "speech encoder", stage, cacheName: "encoder");   // "encoder": the name 1.6 previews already cached under
            EncoderDevice = encoderDevice;

            (_decoder, _decoderWithPast, DecoderDevice) = CreateDecoders(dir, decoderDevice, stage);
        }
        catch
        {
            _encoder?.Dispose();
            throw;
        }
        _decoderOutputs = _decoder.OutputMetadata.Keys.ToArray();
        _pastInputs = _decoderWithPast.InputMetadata.Keys.Where(n => n.StartsWith("past_key_values.")).ToArray();
        _pastOutputs = _decoderWithPast.OutputMetadata.Keys.ToArray();

        // The first inference on a freshly loaded NPU model allocates its buffers; do it now (one silent window) so the
        // first real window isn't slower, and so a device that loads but can't actually run fails here, not mid-file.
        stage?.Report($"Warming up the {OpenVinoSessions.DeviceLabel(encoderDevice)}…");
        using var zeros = OrtValue.CreateTensorValueFromMemory(new float[_mel.Mels * LogMel.Frames], new long[] { 1, _mel.Mels, LogMel.Frames });
        using var warm = _encoder.Run(new RunOptions(), new[] { "input_features" }, new[] { zeros }, new[] { "last_hidden_state" });
    }

    /// <summary>True when the encoder for this device is already compiled and cached (loading will be quick).</summary>
    public static bool IsPrepared(string dir, string encoderDevice) =>
        OpenVinoSessions.IsPrepared(Path.Combine(dir, WhisperOnnxModels.Encoder), encoderDevice, "encoder");

    // ---------- sessions ----------

    private (InferenceSession, InferenceSession, string) CreateDecoders(string dir, string device, IProgress<string>? stage)
    {
        string first = Path.Combine(dir, WhisperOnnxModels.Decoder), step = Path.Combine(dir, WhisperOnnxModels.DecoderWithPast);
        if (NpuDetector.DeviceOverride == OpenVinoSessions.OrtCpu) device = OpenVinoSessions.OrtCpu;
        if (device != OpenVinoSessions.OrtCpu && !device.StartsWith("CPU", StringComparison.OrdinalIgnoreCase))
        {
            InferenceSession? a = null;
            try
            {
                // Fixed batch 1 and bounded sequence lengths: accelerators (the NPU in particular) need bounded shapes.
                a = OpenVinoSessions.Create(first, device, new Dictionary<string, string> { ["reshape_input"] = Bounds(first) }, "text decoder (1/2)", stage);
                var b = OpenVinoSessions.Create(step, device, new Dictionary<string, string> { ["reshape_input"] = Bounds(step) }, "text decoder (2/2)", stage);
                return (a, b, device);
            }
            catch (Exception ex)
            {
                a?.Dispose();
                App.Log($"NPU engine: the text decoder doesn't run on {device}, using the CPU for it", ex);
                device = "CPU";
            }
        }
        var d1 = OpenVinoSessions.Create(first, device, null, "text decoder (1/2)", stage);
        try { return (d1, OpenVinoSessions.Create(step, device, null, "text decoder (2/2)", stage), device); }
        catch { d1.Dispose(); throw; }
    }

    /// <summary>reshape_input bounds for a decoder graph: batch 1, encoder length 1500, decoder length 1..448.</summary>
    private static string Bounds(string model)
    {
        using var probe = new InferenceSession(model, new SessionOptions());
        var parts = new List<string>();
        foreach (var (name, meta) in probe.InputMetadata)
        {
            var dims = meta.Dimensions.Select((d, i) =>
            {
                if (d > 0) return d.ToString();
                if (i == 0) return "1";
                var sym = i < meta.SymbolicDimensions.Length ? meta.SymbolicDimensions[i] : "";
                return sym.Contains("encoder") ? "1500" : $"1..{MaxCtx}";
            });
            parts.Add($"{name}[{string.Join(",", dims)}]");
        }
        return string.Join(",", parts);
    }

    /// <summary>Deletes every compiled model in a model folder (after an NPU failure, so the next run recompiles).</summary>
    public static void ClearCompiled(string dir) => OpenVinoSessions.ClearCache(Path.Combine(dir, WhisperOnnxModels.Encoder));

    // ---------- transcription ----------

    public async Task<List<SegmentData>> TranscribeAsync(float[] samples, TranscribeOptions options,
        Action<int>? onProgress, Action<SegmentData>? onSegment, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await Task.Run(() => Transcribe(samples, options, onProgress, onSegment, ct), ct); }
        finally { _gate.Release(); }
    }

    private List<SegmentData> Transcribe(float[] samples, TranscribeOptions options,
        Action<int>? onProgress, Action<SegmentData>? onSegment, CancellationToken ct)
    {
        var segments = new List<SegmentData>();
        int totalFrames = (samples.Length + LogMel.Hop - 1) / LogMel.Hop;
        double duration = samples.Length / (double)LogMel.SampleRate;
        var hint = string.IsNullOrWhiteSpace(options.Prompt) ? new List<int>() : _tok.Encode(" " + options.Prompt.Trim());
        if (hint.Count > MaxCtx / 2 - 1 - ContextTokens) hint = hint.Skip(hint.Count - (MaxCtx / 2 - 1 - ContextTokens)).ToList();
        var context = new List<int>();
        string? language = options.Language == "auto" ? null : options.Language;
        int seek = 0;

        while (seek < totalFrames)
        {
            ct.ThrowIfCancellationRequested();
            int segFrames = Math.Min(LogMel.Frames, totalFrames - seek);
            if (segFrames < 10) break;   // under 0.1 s left: nothing to decode
            double offset = seek * LogMel.Hop / (double)LogMel.SampleRate;

            var features = _mel.Compute(samples, seek * LogMel.Hop, segFrames * LogMel.Hop);
            using var feat = OrtValue.CreateTensorValueFromMemory(features, new long[] { 1, _mel.Mels, LogMel.Frames });
            using var encoded = _encoder.Run(new RunOptions(), new[] { "input_features" }, new[] { feat }, new[] { "last_hidden_state" });
            var hidden = encoded[0];

            language ??= DetectLanguage(hidden);
            if (!_tok.Languages.TryGetValue(language, out var langToken)) langToken = _tok.Languages.GetValueOrDefault("en", _tok.Sot + 1);
            int task = options.Translate ? _tok.Translate : _tok.Transcribe;

            Decoded result = default!;
            foreach (var t in Temperatures)
            {
                // Like OpenAI's reference: drop the running context once sampling gets hot, it may be what derailed it.
                var prev = hint.Concat(options.NoContext || t > 0.5f ? Enumerable.Empty<int>() : context).ToList();
                var prompt = new List<int>();
                if (prev.Count > 0) { prompt.Add(_tok.StartOfPrev); prompt.AddRange(prev); }
                int sotIndex = prompt.Count;
                prompt.AddRange(new[] { _tok.Sot, langToken, task });
                result = Decode(hidden, prompt, sotIndex, t);

                bool silent = result.NoSpeech > 0.6 && result.AvgLogProb < -1.0;
                if (silent || (result.Compression <= 2.4 && result.AvgLogProb >= -1.0)) break;
            }

            if (result.NoSpeech > 0.6 && result.AvgLogProb < -1.0)
            {
                seek += segFrames;   // confident silence: skip the window
                onProgress?.Invoke((int)(100L * Math.Min(seek, totalFrames) / totalFrames));
                continue;
            }

            int advance = Emit(result, offset, segFrames, duration, language, segments, onSegment);
            seek += advance > 0 ? advance : segFrames;
            if (!options.NoContext && result.Temperature <= 0.5f)
            {
                context.AddRange(result.Tokens.Where(id => !_tok.IsSpecial(id)));
                if (context.Count > ContextTokens) context.RemoveRange(0, context.Count - ContextTokens);
            }
            onProgress?.Invoke((int)(100L * Math.Min(seek, totalFrames) / totalFrames));
        }
        onProgress?.Invoke(100);
        return segments;
    }

    /// <summary>Splits decoded tokens into timestamped segments (OpenAI's rules); returns how many frames were consumed.</summary>
    private int Emit(Decoded r, double offset, int segFrames, double duration, string language,
        List<SegmentData> segments, Action<SegmentData>? onSegment)
    {
        var tokens = r.Tokens;
        int n = tokens.Count;
        bool Ts(int i) => _tok.IsTimestamp(tokens[i]);
        double Time(int id) => (id - _tok.TimestampBegin) * 0.02;

        bool singleTimestampEnding = n >= 2 && !Ts(n - 2) && Ts(n - 1);
        var cuts = Enumerable.Range(1, Math.Max(0, n - 1)).Where(i => Ts(i) && Ts(i - 1)).ToList();
        if (cuts.Count > 0)
        {
            if (singleTimestampEnding) cuts.Add(n);
            int last = 0;
            foreach (var cut in cuts)
            {
                var slice = Enumerable.Range(last, cut - last).ToList();
                if (slice.Count > 0 && Ts(slice[0]) && Ts(slice[^1]))
                    Add(slice, offset + Time(tokens[slice[0]]), offset + Time(tokens[slice[^1]]));
                last = cut;
            }
            if (singleTimestampEnding) return segFrames;
            return (tokens[last - 1] - _tok.TimestampBegin) * 2;   // resume at the last timestamp (0.02 s = 2 frames)
        }

        double length = segFrames * LogMel.Hop / (double)LogMel.SampleRate;
        var stamps = tokens.Where(_tok.IsTimestamp).ToList();
        if (stamps.Count > 0 && stamps[^1] != _tok.TimestampBegin) length = Time(stamps[^1]);
        double begin = n > 0 && Ts(0) && Time(tokens[0]) < length ? Time(tokens[0]) : 0;   // speech starting late in the window
        Add(Enumerable.Range(0, n).ToList(), offset + begin, offset + length);
        return segFrames;

        void Add(List<int> positions, double start, double end)
        {
            var text = positions.Where(i => !_tok.IsSpecial(tokens[i])).ToList();
            if (text.Count == 0) return;
            var s = _tok.Decode(text.Select(i => tokens[i]));
            if (string.IsNullOrWhiteSpace(s)) return;
            end = Math.Min(Math.Max(end, start), duration);
            start = Math.Min(start, end);
            var probs = text.Select(i => r.Probs[i]).ToList();
            long t0 = (long)Math.Round(start * 100), t1 = (long)Math.Round(end * 100);
            var wt = text.Select(i => new WhisperToken
            {
                Id = tokens[i], Probability = r.Probs[i], ProbabilityLog = MathF.Log(Math.Max(r.Probs[i], 1e-10f)),
                Text = _tok.TokenText(tokens[i]), Start = t0, End = t1, TimestampId = -1, DtwTimestamp = -1,
            }).ToArray();
            var seg = new SegmentData(s, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end),
                probs.Min(), probs.Max(), probs.Average(), (float)r.NoSpeech, language, wt);
            segments.Add(seg);
            onSegment?.Invoke(seg);
        }
    }

    private string DetectLanguage(OrtValue hidden)
    {
        using var ids = OrtValue.CreateTensorValueFromMemory(new long[] { _tok.Sot }, new long[] { 1, 1 });
        using var outs = _decoder.Run(new RunOptions(), new[] { "input_ids", "encoder_hidden_states" }, new[] { ids, hidden }, new[] { "logits" });
        var logits = outs[0].GetTensorDataAsSpan<float>();
        string best = "en";
        float bestLogit = float.NegativeInfinity;
        foreach (var (code, id) in _tok.Languages)
            if (id < logits.Length && logits[id] > bestLogit) { bestLogit = logits[id]; best = code; }
        return best;
    }

    private sealed record Decoded(List<int> Tokens, List<float> Probs, double AvgLogProb, double NoSpeech, double Compression, float Temperature);

    private Decoded Decode(OrtValue hidden, List<int> prompt, int sotIndex, float temperature)
    {
        var tokens = new List<int>();
        var probs = new List<float>();
        double sumLogProb = 0, noSpeech;
        int maxTokens = Math.Min(SampleLen, MaxCtx - prompt.Count);

        using var ids = OrtValue.CreateTensorValueFromMemory(prompt.Select(x => (long)x).ToArray(), new long[] { 1, prompt.Count });
        using var first = _decoder.Run(new RunOptions(), new[] { "input_ids", "encoder_hidden_states" }, new[] { ids, hidden }, _decoderOutputs);
        var firstByName = _decoderOutputs.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => first[x.i]);
        var logitsAll = firstByName["logits"].GetTensorDataAsSpan<float>();
        int vocab = logitsAll.Length / prompt.Count;

        var row = new float[vocab];
        logitsAll.Slice(sotIndex * vocab, vocab).CopyTo(row);
        noSpeech = Softmax(row)[_tok.NoSpeech];
        logitsAll.Slice((prompt.Count - 1) * vocab, vocab).CopyTo(row);

        IDisposableReadOnlyCollection<OrtValue>? step = null;
        try
        {
            for (int i = 0; i < maxTokens; i++)
            {
                ApplyRules(row, tokens);
                var logp = LogSoftmax(row);
                int next = temperature <= 0 ? ArgMax(row) : Sample(row, temperature);
                tokens.Add(next);
                probs.Add((float)Math.Exp(logp[next]));
                sumLogProb += logp[next];
                if (next == _tok.Eot) break;
                if (prompt.Count + tokens.Count >= MaxCtx) break;

                using var tokenValue = OrtValue.CreateTensorValueFromMemory(new long[] { next }, new long[] { 1, 1 });
                var inputs = new OrtValue[_pastInputs.Length + 1];
                inputs[0] = tokenValue;
                for (int k = 0; k < _pastInputs.Length; k++)
                {
                    var present = "present." + _pastInputs[k]["past_key_values.".Length..];
                    inputs[k + 1] = present.Contains(".encoder.") || step == null
                        ? firstByName[present]
                        : step[Array.IndexOf(_pastOutputs, present)];
                }
                var names = new[] { "input_ids" }.Concat(_pastInputs).ToArray();
                var nextStep = _decoderWithPast.Run(new RunOptions(), names, inputs, _pastOutputs);
                step?.Dispose();
                step = nextStep;
                step[Array.IndexOf(_pastOutputs, "logits")].GetTensorDataAsSpan<float>().Slice(0, vocab).CopyTo(row);
            }
        }
        finally { step?.Dispose(); }

        if (tokens.Count > 0 && tokens[^1] == _tok.Eot) { tokens.RemoveAt(tokens.Count - 1); probs.RemoveAt(probs.Count - 1); }
        var text = _tok.Decode(tokens);
        return new Decoded(tokens, probs, sumLogProb / Math.Max(1, tokens.Count + 1), noSpeech, Compression(text), temperature);
    }

    /// <summary>OpenAI's logit filters: suppressed tokens, and timestamp tokens that come in pairs and never go back.</summary>
    private void ApplyRules(float[] logits, List<int> sampled)
    {
        int tsBegin = _tok.TimestampBegin, n = sampled.Count;
        logits[_tok.NoTimestamps] = float.NegativeInfinity;
        foreach (var id in _tok.SuppressTokens) if (id < logits.Length) logits[id] = float.NegativeInfinity;
        if (n == 0) foreach (var id in _tok.BeginSuppressTokens) if (id < logits.Length) logits[id] = float.NegativeInfinity;

        bool lastTs = n >= 1 && _tok.IsTimestamp(sampled[n - 1]);
        bool penultimateTs = n < 2 || _tok.IsTimestamp(sampled[n - 2]);
        if (lastTs)
        {
            if (penultimateTs) Fill(logits, tsBegin, logits.Length);   // a pair just closed: text (or EOT) next
            else Fill(logits, 0, _tok.Eot);                            // an open timestamp: close it (or EOT)
        }

        int lastStamp = -1;
        for (int i = n - 1; i >= 0; i--) if (_tok.IsTimestamp(sampled[i])) { lastStamp = sampled[i]; break; }
        if (lastStamp >= 0)
        {
            int floor = lastTs && !penultimateTs ? lastStamp : lastStamp + 1;
            Fill(logits, tsBegin, Math.Min(floor, logits.Length));
        }

        if (n == 0)
        {
            Fill(logits, 0, tsBegin);   // the first token is always a timestamp
            Fill(logits, Math.Min(tsBegin + _tok.MaxInitialTimestampIndex + 1, logits.Length), logits.Length);
        }

        // If the timestamps together are likelier than any single text token, take a timestamp.
        var logp = LogSoftmax(logits);
        double maxText = double.NegativeInfinity, tsMass = double.NegativeInfinity;
        for (int i = 0; i < tsBegin; i++) if (logp[i] > maxText) maxText = logp[i];
        for (int i = tsBegin; i < logits.Length; i++) tsMass = LogAdd(tsMass, logp[i]);
        if (tsMass > maxText) Fill(logits, 0, tsBegin);
    }

    private static void Fill(float[] a, int from, int to)
    {
        for (int i = Math.Max(0, from); i < Math.Min(to, a.Length); i++) a[i] = float.NegativeInfinity;
    }

    private static double LogAdd(double a, double b) =>
        double.IsNegativeInfinity(a) ? b : double.IsNegativeInfinity(b) ? a : Math.Max(a, b) + Math.Log(1 + Math.Exp(-Math.Abs(a - b)));

    private static double[] LogSoftmax(float[] x)
    {
        double max = double.NegativeInfinity;
        foreach (var v in x) if (v > max) max = v;
        double sum = 0;
        foreach (var v in x) sum += Math.Exp(v - max);
        double log = max + Math.Log(sum);
        var r = new double[x.Length];
        for (int i = 0; i < x.Length; i++) r[i] = x[i] - log;
        return r;
    }

    private static double[] Softmax(float[] x) => LogSoftmax(x).Select(Math.Exp).ToArray();

    private static int ArgMax(float[] x)
    {
        int best = 0;
        for (int i = 1; i < x.Length; i++) if (x[i] > x[best]) best = i;
        return best;
    }

    private int Sample(float[] logits, float temperature)
    {
        var scaled = logits.Select(v => v / temperature).ToArray();
        var p = Softmax(scaled);
        double r = _random.NextDouble(), acc = 0;
        for (int i = 0; i < p.Length; i++) { acc += p[i]; if (acc >= r) return i; }
        return ArgMax(logits);
    }

    /// <summary>Text length / zlib-compressed length: high values mean the text repeats itself (a decoding loop).</summary>
    private static double Compression(string text)
    {
        if (text.Length == 0) return 0;
        var bytes = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(bytes);
        return bytes.Length / (double)Math.Max(1, ms.Length);
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _decoderWithPast.Dispose();
        _gate.Dispose();
    }
}
