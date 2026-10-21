using SherpaOnnx;

namespace WhisperDesk.Services;

public sealed record SpeakerTurn(double Start, double End, int Speaker);

public sealed class DetectedSpeaker
{
    public int Id { get; init; }
    public double TalkSeconds { get; init; }
    public float[] Embedding { get; init; } = Array.Empty<float>();
    public double? MedianPitchHz { get; init; }
    public float[] Clip { get; init; } = Array.Empty<float>();
}

public sealed record DiarizationResult(List<SpeakerTurn> Turns, List<DetectedSpeaker> Speakers);

/// <summary>
/// Speaker diarization (who spoke when) with sherpa-onnx: pyannote segmentation + WeSpeaker embeddings,
/// plus a per-speaker voiceprint, median pitch and a short preview clip. Diarization runs on the CPU (sherpa-onnx);
/// voiceprints use the NPU when available (<see cref="Npu.NpuSpeakerEmbedder"/>).
/// </summary>
public sealed class SpeakerAnalyzer : IDisposable
{
    private const int Rate = AudioDecoder.SampleRate;
    private OfflineSpeakerDiarization? _diarizer;
    private SpeakerEmbeddingExtractor? _extractor;
    private int _threads = AppSettings.Current.SpeakerThreads;
    private readonly object _lock = new();

    private OfflineSpeakerDiarizationConfig BuildConfig(int numSpeakers)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = SpeakerModels.SegmentationModel;
        config.Segmentation.NumThreads = _threads;
        config.Embedding.Model = SpeakerModels.EmbeddingModel;
        config.Embedding.NumThreads = _threads;
        var s = AppSettings.Current;
        config.Clustering.NumClusters = numSpeakers > 0 ? numSpeakers : -1;
        config.Clustering.Threshold = s.ClusterThreshold;   // used only when the speaker count is automatic
        config.MinDurationOn = s.MinSpeechSeconds;
        config.MinDurationOff = s.MinSilenceSeconds;
        return config;
    }

    /// <param name="applySettings">Reload when the performance mode changed (only from Analyze, never while live
    /// sessions might be using the extractor mid-call).</param>
    private void EnsureLoaded(bool applySettings = false)
    {
        var wanted = AppSettings.Current.SpeakerThreads;
        if (_diarizer != null && (!applySettings || wanted == _threads)) return;
        _diarizer?.Dispose();
        _extractor?.Dispose();
        _threads = wanted;
        _diarizer = new OfflineSpeakerDiarization(BuildConfig(0));
        _extractor = new SpeakerEmbeddingExtractor(
            new SpeakerEmbeddingExtractorConfig { Model = SpeakerModels.EmbeddingModel, NumThreads = _threads });
        Npu.NpuSpeakerEmbedder.PrepareInBackground();   // voiceprints move to the NPU once it has passed its check
    }

    /// <param name="numSpeakers">Exact number of speakers, or 0 to detect automatically.</param>
    public DiarizationResult Analyze(float[] samples, int numSpeakers, CancellationToken ct, IProgress<string>? progress = null)
    {
        lock (_lock)   // one analysis at a time (file jobs, re-analysis and voice learning share this instance)
        {
            EnsureLoaded(applySettings: true);
            _diarizer!.SetConfig(BuildConfig(numSpeakers));

            var raw = _diarizer.Process(samples);
            ct.ThrowIfCancellationRequested();
            var turns = raw.Select(s => new SpeakerTurn(s.Start, s.End, s.Speaker)).OrderBy(t => t.Start).ToList();

            // Diarization over-splits long calls (one person on a different mic or in a different mood becomes
            // "Speaker 47"). Compare voiceprints built from several of each cluster's turns and merge look-alikes.
            if (numSpeakers <= 0 && turns.Count > 0)
            {
                progress?.Report("Comparing voices…");
                turns = MergeSimilar(samples, turns, ct);
            }

            // Renumber speakers in order of first appearance so "Speaker 1" is whoever talks first.
            var order = turns.Select(t => t.Speaker).Distinct().Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            turns = turns.Select(t => t with { Speaker = order[t.Speaker] }).ToList();

            var speakers = new List<DetectedSpeaker>();
            foreach (var group in turns.GroupBy(t => t.Speaker).OrderBy(g => g.Key))
            {
                ct.ThrowIfCancellationRequested();
                var longestFirst = group.OrderByDescending(t => t.End - t.Start).ToList();
                var pitchAudio = Gather(samples, longestFirst, maxSeconds: 8);
                speakers.Add(new DetectedSpeaker
                {
                    Id = group.Key,
                    TalkSeconds = group.Sum(t => t.End - t.Start),
                    Embedding = Voiceprint(samples, longestFirst),
                    MedianPitchHz = PitchEstimator.MedianPitch(pitchAudio, Rate),
                    Clip = MakeClip(samples, longestFirst[0], maxSeconds: 6),
                });
            }
            return new DiarizationResult(turns, speakers);
        }
    }

    /// <summary>
    /// A robust voiceprint: the average of separate embeddings of up to 8 of the longest turns (each 1.5–10 s),
    /// so one noisy turn can't dominate. Falls back to all audio concatenated for speakers with only short turns.
    /// </summary>
    public float[] Voiceprint(float[] samples, IReadOnlyList<SpeakerTurn> longestFirst)
    {
        var parts = new List<float[]>();
        foreach (var t in longestFirst.Where(t => t.End - t.Start >= 1.5).Take(8))
        {
            var e = Embed(Gather(samples, new[] { t }, maxSeconds: 10));
            if (e.Length > 0) parts.Add(e);
        }
        if (parts.Count > 0) return VoiceLibrary.Average(parts);
        return Embed(Gather(samples, longestFirst, maxSeconds: 30));
    }

    /// <summary>Merges clusters with similar voiceprints, absorbs tiny clusters and caps the speaker count.</summary>
    private List<SpeakerTurn> MergeSimilar(float[] samples, List<SpeakerTurn> turns, CancellationToken ct)
    {
        var s = AppSettings.Current;
        var groups = turns.GroupBy(t => t.Speaker).ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.End - t.Start).ToList());
        var prints = new Dictionary<int, float[]>();
        foreach (var (id, list) in groups)
        {
            ct.ThrowIfCancellationRequested();
            prints[id] = Voiceprint(samples, list);
        }
        double TalkOf(int id) => groups[id].Sum(t => t.End - t.Start);
        var map = groups.Keys.ToDictionary(k => k, k => k);
        double totalTalk = Math.Max(1, groups.Keys.Sum(TalkOf));

        void MergeInto(int from, int into)
        {
            double wa = TalkOf(into), wb = TalkOf(from);
            var pa = prints[into]; var pb = prints[from];
            if (pa.Length > 0 && pb.Length > 0)
            {
                var avg = new float[pa.Length];
                for (int i = 0; i < avg.Length; i++) avg[i] = (float)((pa[i] * wa + pb[i] * wb) / (wa + wb));
                prints[into] = VoiceLibrary.Normalize(avg);
            }
            else if (pa.Length == 0) prints[into] = pb;
            groups[into] = groups[into].Concat(groups[from]).OrderByDescending(t => t.End - t.Start).ToList();
            groups.Remove(from); prints.Remove(from);
            foreach (var k in map.Keys.ToList()) if (map[k] == from) map[k] = into;
        }

        (int A, int B, float Score) BestPair()
        {
            var ids = groups.Keys.ToList();
            (int, int, float) best = (-1, -1, -2f);
            for (int i = 0; i < ids.Count; i++)
                for (int j = i + 1; j < ids.Count; j++)
                {
                    float sc = prints[ids[i]].Length == 0 || prints[ids[j]].Length == 0 ? -1f : VoiceLibrary.Cosine(prints[ids[i]], prints[ids[j]]);
                    if (sc > best.Item3) best = (ids[i], ids[j], sc);
                }
            return best;
        }

        // 1. Same voice split into several clusters.
        while (groups.Count > 1)
        {
            ct.ThrowIfCancellationRequested();
            var (a, b, score) = BestPair();
            if (score < s.SpeakerMergeSimilarity) break;
            if (TalkOf(a) >= TalkOf(b)) MergeInto(b, a); else MergeInto(a, b);
        }

        // 2. Fragments: under 3 s of speech, or under 1.5 % of the talk time and somewhat similar to someone.
        foreach (var id in groups.Keys.OrderBy(TalkOf).ToList())
        {
            if (groups.Count <= 1 || !groups.ContainsKey(id)) continue;
            double talk = TalkOf(id);
            if (talk >= 3 && talk / totalTalk >= 0.015) continue;
            var others = groups.Keys.Where(k => k != id).ToList();
            var nearest = others.MaxBy(k => prints[k].Length == 0 || prints[id].Length == 0 ? -1f : VoiceLibrary.Cosine(prints[k], prints[id]));
            float sim = prints[nearest].Length == 0 || prints[id].Length == 0 ? -1f : VoiceLibrary.Cosine(prints[nearest], prints[id]);
            if (talk < 3 || sim > 0.3f) MergeInto(id, nearest);
        }

        // 3. Hard cap on the number of automatically found speakers.
        while (groups.Count > Math.Max(1, s.MaxAutoSpeakers))
        {
            var (a, b, _) = BestPair();
            if (a < 0) break;
            if (TalkOf(a) >= TalkOf(b)) MergeInto(b, a); else MergeInto(a, b);
        }

        return turns.Select(t => t with { Speaker = map[t.Speaker] }).OrderBy(t => t.Start).ToList();
    }

    /// <summary>
    /// For each line, the speaker whose turns overlap it most (or the nearest turn). Runs in O(lines × log turns) —
    /// safe on multi-hour recordings.
    /// </summary>
    public static int[] AssignLines(IReadOnlyList<(double Start, double End)> lines, IReadOnlyList<SpeakerTurn> turns)
    {
        var result = new int[lines.Count];
        if (turns.Count == 0) { Array.Fill(result, -1); return result; }
        var sorted = turns.OrderBy(t => t.Start).ToArray();
        var starts = sorted.Select(t => t.Start).ToArray();
        double maxLen = sorted.Max(t => t.End - t.Start);
        var overlap = new Dictionary<int, double>();
        for (int i = 0; i < lines.Count; i++)
        {
            var (a, b) = lines[i];
            overlap.Clear();
            // Turns that can overlap [a, b] start after a - maxLen and before b.
            int k = Array.BinarySearch(starts, a - maxLen);
            if (k < 0) k = ~k;
            int nearest = -1; double nearestGap = double.MaxValue;
            for (; k < sorted.Length && sorted[k].Start < b + 30; k++)
            {
                var t = sorted[k];
                double o = Math.Min(b, t.End) - Math.Max(a, t.Start);
                if (o > 0) overlap[t.Speaker] = overlap.GetValueOrDefault(t.Speaker) + o;
                double gap = Math.Min(Math.Abs(t.Start - b), Math.Abs(t.End - a));
                if (gap < nearestGap) { nearestGap = gap; nearest = t.Speaker; }
            }
            if (overlap.Count > 0) result[i] = overlap.MaxBy(x => x.Value).Key;
            else if (nearest >= 0) result[i] = nearest;
            else
            {
                int j = Math.Clamp(Array.BinarySearch(starts, a) is var p && p < 0 ? ~p : p, 0, sorted.Length - 1);
                result[i] = sorted[j > 0 && Math.Abs(sorted[j - 1].End - a) < Math.Abs(sorted[j].Start - a) ? j - 1 : j].Speaker;
            }
        }
        return result;
    }

    /// <summary>
    /// Second pass: compares each line's own audio with every speaker's voiceprint and moves the line when another
    /// speaker is a clearly better match. Fixes turns merged by diarization and timestamp mismatches with Whisper.
    /// </summary>
    /// <returns>New speaker id per line (same order as input).</returns>
    public int[] Refine(float[] samples, IReadOnlyList<(double Start, double End, int Speaker)> lines,
                        IReadOnlyList<DetectedSpeaker> speakers, CancellationToken ct)
    {
        const double MinLineSeconds = 2.0;   // shorter lines don't carry enough voice to judge reliably
        const float Margin = 0.12f;          // how much better the other speaker must match
        const float MinScore = 0.45f;        // and it must be a genuinely good match
        var result = lines.Select(l => l.Speaker).ToArray();
        var withVoice = speakers.Where(s => s.Embedding.Length > 0).ToList();
        if (withVoice.Count < 2) return result;

        lock (_lock)
        {
            EnsureLoaded();
            for (int i = 0; i < lines.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (start, end, current) = lines[i];
                if (end - start < MinLineSeconds) continue;

                var audio = Gather(samples, new[] { new SpeakerTurn(start, end, current) }, maxSeconds: 20);
                var e = Embed(audio);
                if (e.Length == 0) continue;

                var scores = withVoice.Select(s => (s.Id, Score: VoiceLibrary.Cosine(e, s.Embedding))).ToList();
                var best = scores.MaxBy(x => x.Score);
                var currentScore = scores.FirstOrDefault(x => x.Id == current).Score;
                if (best.Id == current || best.Score < MinScore || best.Score - currentScore < Margin) continue;

                // Don't flip a line between two neighbours that agree with the current speaker unless it's a clear win.
                bool neighboursAgree = i > 0 && i < lines.Count - 1 && result[i - 1] == current && lines[i + 1].Speaker == current;
                if (neighboursAgree && best.Score - currentScore < Margin * 2) continue;
                result[i] = best.Id;
            }
        }
        return result;
    }

    /// <summary>
    /// A voiceprint of the audio: on the NPU when it passed its check against the CPU (same result, less CPU and
    /// battery), otherwise with sherpa-onnx on the CPU.
    /// </summary>
    public float[] Embed(float[] audio)
    {
        EnsureLoaded();
        if (audio.Length < Rate / 2) return Array.Empty<float>();
        if (Npu.NpuSpeakerEmbedder.Active is { } npu && npu.TryEmbed(audio, out var onNpu)) return onNpu;
        Npu.NpuSpeakerEmbedder.PrepareInBackground();   // cheap once started; picks up a switch from "CPU only" to Auto
        return EmbedOnCpu(audio);
    }

    private float[] EmbedOnCpu(float[] audio)
    {
        using var stream = _extractor!.CreateStream();
        stream.AcceptWaveform(Rate, audio);
        stream.InputFinished();
        return _extractor.IsReady(stream) ? _extractor.Compute(stream) : Array.Empty<float>();
    }

    public static float[] Gather(float[] samples, IEnumerable<SpeakerTurn> turns, double maxSeconds)
    {
        var parts = new List<float>();
        int budget = (int)(maxSeconds * Rate);
        foreach (var t in turns)
        {
            int start = Math.Clamp((int)(t.Start * Rate), 0, samples.Length);
            int end = Math.Clamp((int)(t.End * Rate), start, samples.Length);
            int take = Math.Min(end - start, budget - parts.Count);
            if (take <= 0) break;
            parts.AddRange(new ArraySegment<float>(samples, start, take));
        }
        return parts.ToArray();
    }

    public static float[] MakeClip(float[] samples, SpeakerTurn turn, double maxSeconds)
    {
        double len = Math.Min(turn.End - turn.Start, maxSeconds);
        double mid = (turn.Start + turn.End) / 2;
        int start = Math.Clamp((int)((mid - len / 2) * Rate), 0, samples.Length);
        int count = Math.Clamp((int)(len * Rate), 0, samples.Length - start);
        return samples.AsSpan(start, count).ToArray();
    }

    public void Dispose()
    {
        _diarizer?.Dispose();
        _extractor?.Dispose();
    }
}

/// <summary>Simple autocorrelation pitch tracker used to suggest a likely sex for a voice.</summary>
public static class PitchEstimator
{
    public static double? MedianPitch(float[] audio, int rate)
    {
        int frame = rate / 25, hop = frame / 2;              // 40 ms frames, 20 ms hop
        int minLag = rate / 400, maxLag = rate / 60;          // 60–400 Hz
        if (audio.Length < frame + maxLag) return null;

        double globalRms = Math.Sqrt(audio.Average(s => (double)s * s));
        var pitches = new List<double>();
        for (int pos = 0; pos + frame + maxLag < audio.Length; pos += hop)
        {
            double energy = 0;
            for (int i = 0; i < frame; i++) energy += audio[pos + i] * audio[pos + i];
            if (Math.Sqrt(energy / frame) < globalRms * 0.6) continue; // skip quiet frames

            double best = 0; int bestLag = 0;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double r = 0, e2 = 0;
                for (int i = 0; i < frame; i++)
                {
                    r += audio[pos + i] * audio[pos + i + lag];
                    e2 += audio[pos + i + lag] * audio[pos + i + lag];
                }
                double norm = r / Math.Sqrt(energy * e2 + 1e-12);
                if (norm > best) { best = norm; bestLag = lag; }
            }
            if (best > 0.5 && bestLag > 0) pitches.Add((double)rate / bestLag);
        }
        if (pitches.Count < 10) return null;
        pitches.Sort();
        return pitches[pitches.Count / 2];
    }

    /// <summary>Typical adult ranges: male ~85–155 Hz, female ~165–255 Hz. In-between is left unspecified.
    /// Thresholds are adjustable in Settings.</summary>
    public static string SuggestSex(double? pitchHz)
    {
        var s = AppSettings.Current;
        if (pitchHz is not double hz) return Sexes.Unspecified;
        if (hz < s.MaleBelowHz) return Sexes.Male;
        if (hz > s.FemaleAboveHz) return Sexes.Female;
        return Sexes.Unspecified;
    }
}

public static class Sexes
{
    public const string Male = "Male";
    public const string Female = "Female";
    public const string Unspecified = "Unspecified";
    public static readonly string[] All = { Male, Female, Unspecified };
}
