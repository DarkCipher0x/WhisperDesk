using System.Text.RegularExpressions;
using SherpaOnnx;
using Whisper.net;

namespace WhisperDesk.Services;

/// <summary>A stretch of audio that contains speech (seconds).</summary>
public readonly record struct SpeechRegion(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>
/// Finds where people are actually speaking (Silero VAD) so Whisper never sees long silence, hold music or
/// background noise — the main cause of "Thank you." / "Thanks for watching" hallucinations and repeat loops.
/// </summary>
public static class SpeechDetector
{
    private const int Rate = AudioDecoder.SampleRate;
    private const int Window = 512;

    /// <summary>Speech regions, padded and merged into chunks of at most <paramref name="maxChunkSeconds"/>.</summary>
    public static List<SpeechRegion> Detect(float[] samples, CancellationToken ct, double maxChunkSeconds = 300)
    {
        OnnxRuntimeGuard.Require();
        var config = new VadModelConfig { SampleRate = Rate, NumThreads = 1 };
        config.SileroVad.Model = SpeakerModels.VadModel;
        config.SileroVad.Threshold = 0.45f;
        config.SileroVad.MinSilenceDuration = 0.6f;
        config.SileroVad.MinSpeechDuration = 0.25f;
        config.SileroVad.MaxSpeechDuration = 30f;
        config.SileroVad.WindowSize = Window;
        using var vad = new VoiceActivityDetector(config, bufferSizeInSeconds: 300);

        var raw = new List<SpeechRegion>();
        void Drain()
        {
            while (!vad.IsEmpty())
            {
                var seg = vad.Front();
                double start = seg.Start / (double)Rate;
                raw.Add(new SpeechRegion(start, start + seg.Samples.Length / (double)Rate));
                vad.Pop();
            }
        }

        var block = new float[Window * 64];   // ~2 s per call
        for (int pos = 0; pos < samples.Length; pos += block.Length)
        {
            if ((pos / block.Length) % 50 == 0) ct.ThrowIfCancellationRequested();
            int n = Math.Min(block.Length, samples.Length - pos) / Window * Window;
            if (n == 0) break;
            if (n != block.Length) block = new float[n];
            Array.Copy(samples, pos, block, 0, n);
            vad.AcceptWaveform(block);
            Drain();
        }
        vad.Flush();
        Drain();
        return Merge(raw, samples.Length / (double)Rate, maxChunkSeconds);
    }

    /// <summary>Pads each region a little, joins regions separated by short gaps and caps chunk length.</summary>
    public static List<SpeechRegion> Merge(List<SpeechRegion> raw, double total, double maxChunkSeconds)
    {
        const double Pad = 0.25, JoinGap = 1.5;
        var result = new List<SpeechRegion>();
        foreach (var r in raw.OrderBy(r => r.Start))
        {
            var p = new SpeechRegion(Math.Max(0, r.Start - Pad), Math.Min(total, r.End + Pad));
            if (result.Count > 0)
            {
                var last = result[^1];
                if (p.Start - last.End < JoinGap && p.End - last.Start <= maxChunkSeconds)
                {
                    result[^1] = new SpeechRegion(last.Start, Math.Max(last.End, p.End));
                    continue;
                }
            }
            result.Add(p);
        }
        return result;
    }
}

/// <summary>Removes the classic Whisper hallucinations and repetition loops from a segment list.</summary>
public static class HallucinationFilter
{
    // Phrases Whisper invents on silence, music or noise (normalised: lower case, no punctuation).
    private static readonly HashSet<string> Suspects = new(StringComparer.Ordinal)
    {
        "thank you", "thank you very much", "thanks", "thank you so much", "thanks for watching", "thank you for watching",
        "thanks for watching and see you next time", "please subscribe", "like and subscribe", "subscribe",
        "bye", "bye bye", "you", "so", "okay", "oh", "uh", "um", "hmm", "the end", "music",
        "subtitles by the amaraorg community", "transcription by castingwords", "see you next time",
        "i'll see you next time", "see you in the next video", "thank you for listening",
    };

    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}' ]+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Bracketed = new(@"^\s*[\[\(♪*].*[\]\)♪*]\s*$", RegexOptions.Compiled);

    public static string Normalise(string text) => Spaces.Replace(NonWord.Replace(text.ToLowerInvariant(), " "), " ").Trim();

    public static bool IsSuspect(string text) => Suspects.Contains(Normalise(text));

    /// <summary>
    /// Drops: empty or bracket-only segments ([MUSIC], (silence)); suspect phrases that are quiet or low-confidence;
    /// and the third and later copies of the same line in a row (repeat loops).
    /// </summary>
    public static List<SegmentData> Clean(IReadOnlyList<SegmentData> segments, float[]? samples)
    {
        var kept = new List<SegmentData>(segments.Count);
        string? previous = null;
        int run = 0;
        foreach (var s in segments)
        {
            var norm = Normalise(s.Text);
            if (norm.Length == 0 || Bracketed.IsMatch(s.Text)) continue;
            if (Suspects.Contains(norm) && (s.Probability < 0.75f || IsQuiet(samples, s.Start.TotalSeconds, s.End.TotalSeconds))) continue;

            run = norm == previous ? run + 1 : 0;
            previous = norm;
            if (run >= 2) continue;   // keep at most two identical lines in a row
            kept.Add(s);
        }
        return kept;
    }

    /// <summary>True when the audio under a segment is very quiet (RMS below about −42 dBFS).</summary>
    public static bool IsQuiet(float[]? samples, double start, double end)
    {
        if (samples == null) return false;
        int a = Math.Clamp((int)(start * AudioDecoder.SampleRate), 0, samples.Length);
        int b = Math.Clamp((int)(end * AudioDecoder.SampleRate), a, samples.Length);
        if (b - a < 160) return true;
        double sum = 0;
        for (int i = a; i < b; i++) sum += samples[i] * samples[i];
        return Math.Sqrt(sum / (b - a)) < 0.008;
    }
}
