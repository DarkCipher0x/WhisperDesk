using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhisperDesk.Services;

/// <summary>Acoustic profile of one voice (measured on the speaker's own lines).</summary>
public sealed class VoiceProfile
{
    public string Speaker { get; init; } = "";
    public double SpeechSeconds { get; set; }
    public int Lines { get; set; }
    public int Words { get; set; }
    public double WordsPerMinute { get; set; }
    public double SyllablesPerSecond { get; set; }
    public double F0Mean { get; set; }
    public double F0Median { get; set; }
    public double F0Min { get; set; }
    public double F0Max { get; set; }
    public double F0StdSemitones { get; set; }
    public double F0RangeSemitones { get; set; }
    public double JitterPercent { get; set; }
    public double ShimmerPercent { get; set; }
    public double HnrDb { get; set; }
    public double F1 { get; set; }
    public double F2 { get; set; }
    public double F3 { get; set; }
    public double VoicedFraction { get; set; }
    public double LoudnessDb { get; set; }
    public double LoudnessStdDb { get; set; }
    public double SpectralCentroidHz { get; set; }
    public string SuggestedSex { get; set; } = Sexes.Unspecified;
    public List<(double Time, double Hz)> PitchTrack { get; } = new();
}

public sealed record VoiceComparison(string A, string B, float Score, float Min, float Max, int Pairs, string Verdict, string Explanation);

public sealed class IntegrityReport
{
    public List<(string Label, string Value)> File { get; } = new();
    public List<(string Label, string Value)> Signal { get; } = new();
    public List<(double Time, string Finding)> Events { get; } = new();
    public string Summary { get; set; } = "";
}

/// <summary>
/// Voice biometrics and audio forensics helpers. These are investigative indicators, not proof: similarity scores
/// come from a speaker-embedding model (WeSpeaker ResNet34) and acoustic measures are standard approximations
/// (autocorrelation pitch, Boersma-style HNR, LPC formants). An accredited forensic examination is needed for court use.
/// </summary>
public static class VoiceForensics
{
    private const int Rate = AudioDecoder.SampleRate;

    public const string Disclaimer =
        "These are automated indicators, not proof of identity or authenticity. Voice similarity depends on microphones, channels, " +
        "emotion and health; acoustic measures are approximations. For legal use, have an accredited forensic examiner review the original recordings.";

    // ---------------- Voice comparison ----------------

    /// <summary>
    /// Compares two sets of voiceprint samples. Bands are calibrated for WeSpeaker cosine scores on conversational audio
    /// (same-speaker pairs typically 0.55–0.85, different speakers typically below 0.35).
    /// </summary>
    public static VoiceComparison Compare(string a, IReadOnlyList<float[]> sa, string b, IReadOnlyList<float[]> sb)
    {
        static bool Ok(float[] x, float[] y) => x.Length > 0 && x.Length == y.Length;
        var between = (from x in sa from y in sb where Ok(x, y) select VoiceLibrary.Cosine(x, y)).ToList();
        if (between.Count == 0) return new VoiceComparison(a, b, 0, 0, 0, 0, "No data", "One of the voices has no voiceprint (it needs audio to analyse).");
        if (a == b)
            return new VoiceComparison(a, b, between.Average(), between.Min(), between.Max(), between.Count, "Self-consistency",
                "How alike this voice's own samples are (the baseline for comparisons).");

        // Same recording conditions make every voice look alike in absolute terms (same phone, room and codec), so the
        // decision is relative: are A and B as alike as each voice is with itself?
        List<float> Within(IReadOnlyList<float[]> s)
        {
            var w = new List<float>();
            for (int i = 0; i < s.Count; i++) for (int j = i + 1; j < s.Count; j++) if (Ok(s[i], s[j])) w.Add(VoiceLibrary.Cosine(s[i], s[j]));
            return w;
        }
        var within = Within(sa).Concat(Within(sb)).ToList();
        float mean = between.Average();
        static double Var(List<float> v) { var m = v.Average(); return v.Sum(x => (x - m) * (x - m)) / Math.Max(1, v.Count - 1); }

        string verdict, why;
        if (mean < 0.35f) (verdict, why) = ("Different voices", $"Average similarity {mean:0.00} is in the range typical of different speakers.");
        else if (within.Count < 3)
            (verdict, why) = mean >= 0.70f ? ("Similar (limited data)", "Very alike, but too little audio per voice to set a baseline; collect more speech.")
                           : ("Inconclusive (limited data)", "Too little audio per voice to decide reliably.");
        else
        {
            double w = within.Average(), d = (w - mean) / Math.Sqrt((Var(within) + Var(between)) / 2 + 1e-6);
            (verdict, why) = d switch
            {
                < 0.5 => ("Consistent with the same speaker", $"A and B are as alike as each voice is with itself (within {w:0.00} vs between {mean:0.00}). They may be one person split in two."),
                < 1.5 => ("Inconclusive", $"Between-voice similarity {mean:0.00} is somewhat lower than within-voice {w:0.00} (separation {d:0.0} σ)."),
                _ => ("Distinct voices", $"Clearly less alike than each voice with itself (within {w:0.00} vs between {mean:0.00}, separation {d:0.0} σ)."),
            };
        }
        return new VoiceComparison(a, b, mean, between.Min(), between.Max(), between.Count, verdict, why);
    }

    /// <summary>Separate voiceprints from up to 12 of a speaker's longest lines (for comparison statistics).</summary>
    public static List<float[]> Samples(SpeakerAnalyzer analyzer, float[] samples, IEnumerable<(double Start, double End)> turns)
    {
        var list = new List<float[]>();
        foreach (var (s, e) in turns.Where(t => t.End - t.Start >= 1.5).OrderByDescending(t => t.End - t.Start).Take(12))
        {
            var emb = analyzer.Embed(SpeakerAnalyzer.Gather(samples, new[] { new SpeakerTurn(s, e, 0) }, 10));
            if (emb.Length > 0) list.Add(emb);
        }
        return list;
    }

    // ---------------- Voice profile ----------------

    public static VoiceProfile Profile(string speaker, float[] samples, IReadOnlyList<TranscriptLine> lines, CancellationToken ct)
    {
        var p = new VoiceProfile { Speaker = speaker, Lines = lines.Count };
        p.SpeechSeconds = lines.Sum(l => (l.End - l.Start).TotalSeconds);
        p.Words = lines.Sum(l => Regex.Matches(l.Text, @"[\p{L}\p{N}']+").Count);
        p.WordsPerMinute = p.SpeechSeconds > 0 ? p.Words / (p.SpeechSeconds / 60) : 0;
        var syllables = lines.Sum(l => Regex.Matches(l.Text.ToLowerInvariant(), "[aeiouy]+").Count);
        p.SyllablesPerSecond = p.SpeechSeconds > 0 ? syllables / p.SpeechSeconds : 0;

        int frame = Rate / 25, hop = Rate / 100;   // 40 ms frames, 10 ms hop
        int minLag = Rate / 500, maxLag = Rate / 60;
        var f0 = new List<double>(); var amps = new List<double>(); var periods = new List<double>(); var hnr = new List<double>();
        var loud = new List<double>(); var f1 = new List<double>(); var f2 = new List<double>(); var f3 = new List<double>(); var centroids = new List<double>();
        int frames = 0, voiced = 0;
        var buf = new double[frame];

        foreach (var l in lines.OrderBy(l => l.Start))
        {
            ct.ThrowIfCancellationRequested();
            int a = Math.Clamp((int)(l.Start.TotalSeconds * Rate), 0, samples.Length), b = Math.Clamp((int)(l.End.TotalSeconds * Rate), a, samples.Length);
            double prevPeriod = 0, prevAmp = 0;
            for (int pos = a; pos + frame + maxLag < b; pos += hop)
            {
                frames++;
                double energy = 0, peak = 0;
                for (int i = 0; i < frame; i++) { double v = samples[pos + i]; buf[i] = v; energy += v * v; peak = Math.Max(peak, Math.Abs(v)); }
                double rms = Math.Sqrt(energy / frame);
                if (rms < 0.004) { prevPeriod = 0; continue; }
                loud.Add(20 * Math.Log10(rms + 1e-9));

                // Normalised autocorrelation pitch, refined to a fraction of a sample (parabolic interpolation)
                double best = 0; int bestLag = 0;
                for (int lag = minLag; lag <= maxLag; lag++)
                {
                    double norm = Ac(samples, pos, frame, lag, energy);
                    if (norm > best) { best = norm; bestLag = lag; }
                }
                if (best < 0.55 || bestLag <= minLag || bestLag >= maxLag) { prevPeriod = 0; continue; }
                double y0 = Ac(samples, pos, frame, bestLag - 1, energy), y2 = Ac(samples, pos, frame, bestLag + 1, energy);
                double denom = y0 - 2 * best + y2;
                double lagF = bestLag + (Math.Abs(denom) > 1e-9 ? Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5) : 0);
                voiced++;
                double hz = Rate / lagF;
                f0.Add(hz);
                if (p.PitchTrack.Count < 4000 && frames % 3 == 0) p.PitchTrack.Add((pos / (double)Rate, hz));
                double r1 = Math.Min(0.999, best);
                hnr.Add(10 * Math.Log10(r1 / (1 - r1)));
                double period = 1.0 / hz;
                if (prevPeriod > 0)
                {
                    double dj = Math.Abs(period - prevPeriod) / ((period + prevPeriod) / 2);
                    if (dj < 0.2) periods.Add(dj);   // larger jumps are octave errors or new syllables, not jitter
                    double ds = Math.Abs(peak - prevAmp) / ((peak + prevAmp) / 2 + 1e-9);
                    if (ds < 1) amps.Add(ds);
                }
                prevPeriod = period; prevAmp = peak;

                if (voiced % 4 == 0)   // formants + spectral centroid on every 4th voiced frame (cost)
                {
                    var (ff1, ff2, ff3, centroid) = Formants(buf);
                    if (ff1 > 0) f1.Add(ff1);
                    if (ff2 > 0) f2.Add(ff2);
                    if (ff3 > 0) f3.Add(ff3);
                    centroids.Add(centroid);
                }
            }
        }

        p.VoicedFraction = frames > 0 ? (double)voiced / frames : 0;
        if (f0.Count > 10)
        {
            f0.Sort();
            p.F0Mean = f0.Average(); p.F0Median = f0[f0.Count / 2];
            p.F0Min = f0[(int)(f0.Count * 0.05)]; p.F0Max = f0[(int)(f0.Count * 0.95)];
            var st = f0.Select(h => 12 * Math.Log2(h / p.F0Median)).ToList();
            var mean = st.Average();
            p.F0StdSemitones = Math.Sqrt(st.Sum(x => (x - mean) * (x - mean)) / st.Count);
            p.F0RangeSemitones = 12 * Math.Log2(p.F0Max / Math.Max(1, p.F0Min));
            p.SuggestedSex = PitchEstimator.SuggestSex(p.F0Median);
        }
        p.JitterPercent = periods.Count > 0 ? periods.Average() * 100 : 0;
        p.ShimmerPercent = amps.Count > 0 ? amps.Average() * 100 : 0;
        p.HnrDb = hnr.Count > 0 ? Median(hnr) : 0;
        p.F1 = f1.Count > 0 ? Median(f1) : 0; p.F2 = f2.Count > 0 ? Median(f2) : 0; p.F3 = f3.Count > 0 ? Median(f3) : 0;
        p.SpectralCentroidHz = centroids.Count > 0 ? Median(centroids) : 0;
        if (loud.Count > 0)
        {
            p.LoudnessDb = loud.Average();
            p.LoudnessStdDb = Math.Sqrt(loud.Sum(x => (x - p.LoudnessDb) * (x - p.LoudnessDb)) / loud.Count);
        }
        return p;
    }

    private static double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }

    private static double Ac(float[] s, int pos, int frame, int lag, double energy)
    {
        double r = 0, e2 = 0;
        for (int i = 0; i < frame; i += 2) { r += s[pos + i] * s[pos + i + lag]; e2 += s[pos + i + lag] * s[pos + i + lag]; }
        return r / Math.Sqrt((energy / 2) * e2 + 1e-12);
    }

    /// <summary>First three formants from a 14th-order LPC envelope (pre-emphasised, Hamming window), plus the spectral centroid.</summary>
    private static (double F1, double F2, double F3, double Centroid) Formants(double[] x)
    {
        int n = x.Length, order = 14;
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = (i == 0 ? x[0] : x[i] - 0.97 * x[i - 1]) * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (n - 1)));
        var r = new double[order + 1];
        for (int k = 0; k <= order; k++) { double s = 0; for (int i = k; i < n; i++) s += w[i] * w[i - k]; r[k] = s; }
        if (r[0] <= 0) return (0, 0, 0, 0);
        // Levinson-Durbin
        var a = new double[order + 1]; a[0] = 1; double err = r[0];
        for (int i = 1; i <= order; i++)
        {
            double acc = r[i];
            for (int j = 1; j < i; j++) acc += a[j] * r[i - j];
            double k = -acc / err;
            var prev = (double[])a.Clone();
            for (int j = 1; j < i; j++) a[j] = prev[j] + k * prev[i - j];
            a[i] = k;
            err *= 1 - k * k;
            if (err <= 0) return (0, 0, 0, 0);
        }
        // Envelope peaks between 200 Hz and 4 kHz
        int bins = 400; double maxHz = 4000;
        var env = new double[bins];
        for (int b = 0; b < bins; b++)
        {
            double wr = 2 * Math.PI * (b * maxHz / bins) / Rate, re = 0, im = 0;
            for (int k = 0; k <= order; k++) { re += a[k] * Math.Cos(wr * k); im -= a[k] * Math.Sin(wr * k); }
            env[b] = 1 / Math.Sqrt(re * re + im * im + 1e-12);
        }
        var peaks = new List<double>();
        for (int b = 2; b < bins - 1 && peaks.Count < 3; b++)
        {
            double hz = b * maxHz / bins;
            if (hz < 200) continue;
            if (env[b] > env[b - 1] && env[b] >= env[b + 1]) peaks.Add(hz);
        }
        // Spectral centroid of the raw frame (coarse DFT, 64 bands up to 8 kHz)
        double num = 0, den = 0;
        for (int b = 1; b <= 64; b++)
        {
            double hz = b * 8000.0 / 64, wr = 2 * Math.PI * hz / Rate, re = 0, im = 0;
            for (int i = 0; i < n; i += 2) { re += w[i] * Math.Cos(wr * i); im -= w[i] * Math.Sin(wr * i); }
            double mag = Math.Sqrt(re * re + im * im);
            num += hz * mag; den += mag;
        }
        return (peaks.ElementAtOrDefault(0), peaks.ElementAtOrDefault(1), peaks.ElementAtOrDefault(2), den > 0 ? num / den : 0);
    }

    // ---------------- Audio integrity ----------------

    public static async Task<IntegrityReport> CheckIntegrityAsync(string mediaPath, float[] samples, CancellationToken ct)
    {
        var rep = new IntegrityReport();
        var fi = new FileInfo(mediaPath);
        rep.File.Add(("File", fi.Name));
        rep.File.Add(("Size", $"{fi.Length:N0} bytes"));
        rep.File.Add(("SHA-256", await Task.Run(() => BulkReport.Sha256(mediaPath), ct)));
        rep.File.Add(("File created (file system)", fi.CreationTime.ToString("yyyy-MM-dd HH:mm:ss")));
        rep.File.Add(("File modified (file system)", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")));
        foreach (var kv in await ProbeAsync(mediaPath, ct)) rep.File.Add(kv);

        await Task.Run(() => AnalyseSignal(samples, rep, ct), ct);
        int suspicious = rep.Events.Count(e => !e.Finding.StartsWith("Note"));
        rep.Summary = suspicious == 0
            ? "No splice indicators were found by these automated checks."
            : $"{suspicious} point(s) worth listening to closely were found (listed below). They can have innocent causes (mute buttons, network drop-outs, room changes).";
        return rep;
    }

    /// <summary>Container and stream details from ffprobe (codec, sample rate, encoder, creation time tags).</summary>
    private static async Task<List<(string, string)>> ProbeAsync(string path, CancellationToken ct)
    {
        var list = new List<(string, string)>();
        var ffmpeg = AudioDecoder.FindFfmpeg();
        var probe = ffmpeg == null ? null : Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        if (probe == null || !File.Exists(probe)) { list.Add(("Container details", "ffprobe not found next to FFmpeg")); return list; }
        try
        {
            var psi = new ProcessStartInfo(probe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", path }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var json = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var fmt = doc.RootElement.GetProperty("format");
            string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ToString() : null;
            list.Add(("Container", S(fmt, "format_long_name") ?? S(fmt, "format_name") ?? "?"));
            if (S(fmt, "duration") is { } d && double.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs))
                list.Add(("Duration", TimeSpan.FromSeconds(secs).ToString(@"h\:mm\:ss\.fff")));
            if (S(fmt, "bit_rate") is { } br) list.Add(("Overall bit rate", $"{long.Parse(br) / 1000:N0} kb/s"));
            if (fmt.TryGetProperty("tags", out var tags))
                foreach (var t in tags.EnumerateObject()) list.Add(("Tag: " + t.Name, t.Value.ToString()));
            foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
            {
                var type = S(s, "codec_type");
                var desc = type == "audio"
                    ? $"{S(s, "codec_long_name") ?? S(s, "codec_name")}, {S(s, "sample_rate")} Hz, {S(s, "channels")} ch" + (S(s, "bit_rate") is { } b ? $", {long.Parse(b) / 1000} kb/s" : "")
                    : $"{S(s, "codec_long_name") ?? S(s, "codec_name")}" + (type == "video" ? $", {S(s, "width")}×{S(s, "height")}" : "");
                list.Add(($"Stream {S(s, "index")} ({type})", desc));
                if (s.TryGetProperty("tags", out var st))
                    foreach (var t in st.EnumerateObject().Where(t => t.Name is "handler_name" or "encoder" or "creation_time" or "language"))
                        list.Add(($"Stream {S(s, "index")} {t.Name}", t.Value.ToString()));
            }
        }
        catch (Exception ex) { list.Add(("Container details", "could not be read: " + ex.Message)); }
        return list;
    }

    private static void AnalyseSignal(float[] x, IntegrityReport rep, CancellationToken ct)
    {
        long clipped = 0; double dc = 0;
        for (int i = 0; i < x.Length; i++) { if (Math.Abs(x[i]) >= 0.999f) clipped++; dc += x[i]; }
        rep.Signal.Add(("Analysed", $"{x.Length / (double)Rate:N0} s at {Rate} Hz (mono mix)"));
        rep.Signal.Add(("Clipped samples", $"{clipped:N0} ({100.0 * clipped / Math.Max(1, x.Length):0.000} %)"));
        rep.Signal.Add(("DC offset", $"{dc / Math.Max(1, x.Length):0.00000}"));

        // Digital silence: runs of exact zeros ≥ 40 ms are rare in real microphone audio and can indicate inserted or muted parts.
        int run = 0, zeroRuns = 0;
        for (int i = 0; i < x.Length; i++)
        {
            if (x[i] == 0f) { run++; continue; }
            if (run >= Rate / 25) { zeroRuns++; if (zeroRuns <= 40) rep.Events.Add(((i - run) / (double)Rate, $"Digital silence (exact zeros) for {run * 1000 / Rate} ms")); }
            run = 0;
        }
        rep.Signal.Add(("Digital-silence gaps (≥ 40 ms)", zeroRuns.ToString("N0")));

        // Background noise floor per second (10th percentile of 20 ms frame levels). Sudden jumps can mark splices or a change of room/device.
        int win = Rate, sub = Rate / 50;
        var floors = new List<double>();
        for (int pos = 0; pos + win <= x.Length; pos += win)
        {
            ct.ThrowIfCancellationRequested();
            var levels = new List<double>();
            for (int f = pos; f + sub <= pos + win; f += sub)
            {
                double e = 0;
                for (int i = f; i < f + sub; i++) e += x[i] * x[i];
                levels.Add(10 * Math.Log10(e / sub + 1e-12));
            }
            levels.Sort();
            floors.Add(levels[levels.Count / 10]);
        }
        if (floors.Count > 3)
        {
            var sorted = floors.OrderBy(v => v).ToList();
            rep.Signal.Add(("Noise floor (median)", $"{sorted[sorted.Count / 2]:0.0} dBFS"));
            // A splice or device change shows as a step: steady before, steady after, and a big difference between them.
            // Speech pauses (brief dips) are not steps, so both sides must be stable for 4 seconds.
            int jumps = 0;
            for (int i = 4; i < floors.Count - 4; i++)
            {
                var before = floors.GetRange(i - 4, 4); var after = floors.GetRange(i, 4);
                double b = before.Average(), a = after.Average();
                bool steady = before.Max() - before.Min() <= 6 && after.Max() - after.Min() <= 6;
                if (steady && Math.Abs(a - b) >= 15 && Math.Min(a, b) > -95)
                {
                    jumps++;
                    if (jumps <= 40) rep.Events.Add((i, $"Background noise steps by {a - b:+0;-0} dB and stays there"));
                    i += 3;
                }
            }
            rep.Signal.Add(("Background-level steps (≥ 15 dB, sustained)", jumps.ToString("N0")));
        }

        // Electrical network frequency (mains hum at 50 Hz in the UK/EU, 60 Hz in the Americas): presence and stability.
        var (hz, strength, stdMilli, windows) = Enf(x, ct);
        rep.Signal.Add(("Mains hum (ENF)", strength >= 6
            ? $"present at ~{hz:0.00} Hz ({strength:0} dB above neighbours), variation {stdMilli:0} mHz over {windows} windows"
            : "not detected (common with headsets, phones and online calls)"));
        if (strength >= 6) rep.Events.Add((0, "Note: mains hum allows an ENF comparison against grid-frequency records by a specialist."));
        rep.Events.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    /// <summary>Strength of the 50/60 Hz hum and the standard deviation of its frequency across 8-second windows.</summary>
    private static (double Hz, double StrengthDb, double StdMilliHz, int Windows) Enf(float[] x, CancellationToken ct)
    {
        int n = Rate * 8;
        if (x.Length < n) return (0, 0, 0, 0);
        double Power(int start, double f)
        {
            double re = 0, im = 0, w = 2 * Math.PI * f / Rate;
            for (int i = 0; i < n; i += 4) { re += x[start + i] * Math.Cos(w * i); im += x[start + i] * Math.Sin(w * i); }
            return re * re + im * im;
        }
        double best = 0, bestStrength = -99;
        foreach (var mains in new[] { 50.0, 60.0 })
        {
            int start = Math.Min(x.Length - n, x.Length / 2);
            double peak = Power(start, mains), side = (Power(start, mains - 3) + Power(start, mains + 3)) / 2;
            double s = 10 * Math.Log10((peak + 1e-12) / (side + 1e-12));
            if (s > bestStrength) { bestStrength = s; best = mains; }
        }
        if (bestStrength < 6) return (best, bestStrength, 0, 0);
        var track = new List<double>();
        for (int start = 0; start + n <= x.Length && track.Count < 200; start += n)
        {
            ct.ThrowIfCancellationRequested();
            double bf = best, bp = 0;
            for (double f = best - 0.3; f <= best + 0.3; f += 0.01) { var pw = Power(start, f); if (pw > bp) { bp = pw; bf = f; } }
            track.Add(bf);
        }
        var mean = track.Average();
        var std = Math.Sqrt(track.Sum(v => (v - mean) * (v - mean)) / track.Count);
        return (mean, bestStrength, std * 1000, track.Count);
    }

    // ---------------- Report ----------------

    public static ReportDocument BuildReport(string title, RecordingMeta? meta, IReadOnlyList<VoiceProfile> profiles,
                                             IReadOnlyList<VoiceComparison> comparisons, IntegrityReport? integrity)
    {
        var doc = new ReportDocument
        {
            Title = title,
            Subtitle = $"Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} by {Environment.UserDomainName}\\{Environment.UserName} on {Environment.MachineName} · WhisperDesk {AboutWindow.Version}",
        };
        doc.P(Disclaimer, muted: true);
        if (meta != null)
            doc.H1("Recording").KV(new (string, string)[]
            {
                ("Title", meta.Title), ("Date", meta.Created.ToString("yyyy-MM-dd HH:mm")), ("Length", meta.Duration), ("Source", meta.Source),
                ("Library ID", meta.Id), ("Transcribed with", meta.Model ?? "unknown"),
            });

        if (profiles.Count > 0)
        {
            doc.H1("Voice profiles");
            doc.T(new[] { "Measure" }.Concat(profiles.Select(p => p.Speaker)).ToArray(), ProfileRows(profiles));
            doc.P("F0 = fundamental frequency (pitch). Jitter / shimmer = cycle-to-cycle variation of pitch / loudness (frame-level approximation). " +
                  "HNR = harmonics-to-noise ratio. F1–F3 = formants (vocal-tract resonances, LPC estimate; vowel-dependent). " +
                  "Typical adult speech: jitter < 1–2 %, shimmer < 5–10 %, HNR 10–25 dB.", muted: true);
        }
        if (comparisons.Count > 0)
        {
            doc.H1("Voice comparisons");
            doc.T(new[] { "Voice A", "Voice B", "Similarity", "Range", "Pairs", "Assessment" },
                comparisons.Select(c => new[] { c.A, c.B, c.Score.ToString("0.00"), $"{c.Min:0.00}–{c.Max:0.00}", c.Pairs.ToString(), c.Verdict }),
                new[] { 2, 2, 1, 1.2, 0.7, 1.6 });
            foreach (var c in comparisons) doc.P($"{c.A} vs {c.B}: {c.Verdict}. {c.Explanation}");
            doc.P("Similarity is the mean cosine similarity between voiceprints (WeSpeaker ResNet34). Typical: same speaker 0.55–0.85, different speakers below 0.35.", muted: true);
        }
        if (integrity != null)
        {
            doc.H1("Audio integrity");
            doc.P(integrity.Summary, bold: true);
            doc.H2("File and container").KV(integrity.File);
            doc.H2("Signal").KV(integrity.Signal);
            if (integrity.Events.Count > 0)
            {
                doc.H2("Points to review");
                doc.T(new[] { "Time", "Finding" }, integrity.Events.Select(e => new[] { TimeSpan.FromSeconds(e.Time).ToString(@"hh\:mm\:ss\.f"), e.Finding }), new[] { 1.0, 5.0 });
            }
        }
        return doc;
    }

    private static IEnumerable<string[]> ProfileRows(IReadOnlyList<VoiceProfile> ps)
    {
        string[] Row(string label, Func<VoiceProfile, string> f) => new[] { label }.Concat(ps.Select(f)).ToArray();
        yield return Row("Speech analysed", p => $"{p.SpeechSeconds:0} s · {p.Lines} lines");
        yield return Row("Speaking rate", p => $"{p.WordsPerMinute:0} wpm · {p.SyllablesPerSecond:0.0} syl/s");
        yield return Row("F0 median (mean)", p => $"{p.F0Median:0} Hz ({p.F0Mean:0})");
        yield return Row("F0 5–95 % range", p => $"{p.F0Min:0}–{p.F0Max:0} Hz ({p.F0RangeSemitones:0.0} st)");
        yield return Row("F0 variability", p => $"{p.F0StdSemitones:0.0} semitones");
        yield return Row("Jitter (local)", p => $"{p.JitterPercent:0.00} %");
        yield return Row("Shimmer (local)", p => $"{p.ShimmerPercent:0.0} %");
        yield return Row("HNR", p => $"{p.HnrDb:0.0} dB");
        yield return Row("Formants F1 / F2 / F3", p => $"{p.F1:0} / {p.F2:0} / {p.F3:0} Hz");
        yield return Row("Spectral centroid", p => $"{p.SpectralCentroidHz:0} Hz");
        yield return Row("Loudness (variation)", p => $"{p.LoudnessDb:0.0} dBFS (±{p.LoudnessStdDb:0.0})");
        yield return Row("Voiced frames", p => $"{p.VoicedFraction:P0}");
        yield return Row("Pitch suggests", p => p.SuggestedSex);
    }
}
