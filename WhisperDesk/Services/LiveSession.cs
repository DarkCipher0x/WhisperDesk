using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows.Threading;
using NAudio.Wave;
using SherpaOnnx;

namespace WhisperDesk.Services;

/// <summary>
/// Live transcription: audio in → Silero VAD cuts it at pauses → each utterance is transcribed on the GPU,
/// given a speaker (known voice, someone already heard this session, or a new person) and added to the transcript.
/// The whole session is also recorded to a WAV file.
/// </summary>
public sealed partial class LiveSession : IDisposable
{
    private const int Rate = AudioDecoder.SampleRate;
    private const int VadWindow = 512;

    private readonly TranscriptionEngine _engine;
    private readonly SpeakerAnalyzer _analyzer;
    private readonly VoiceLibrary _voices;
    private readonly TranscribeOptions _options;
    private readonly bool _identify;
    private readonly Dispatcher _ui;
    private readonly TranscriptResult _result;
    private readonly VoiceActivityDetector _vad;
    private readonly IRecordingSink? _recording;   // null when "Keep audio recording" is off; encrypted when encryption is on
    private readonly Channel<(double Start, float[] Samples)> _queue = Channel.CreateUnbounded<(double, float[])>();
    private readonly Task _worker;
    private readonly List<float> _pending = new();
    private readonly List<(SpeakerInfo Info, float[] Centroid, int Count)> _speakers = new();
    private readonly object _feedLock = new();
    private SpeakerInfo? _lastSpeaker;
    private long _samplesFed;
    // A short line whose speaker was only guessed, kept so it can be corrected by the next line.
    private (TranscriptLine Line, double GapBefore)? _guessed;
    private double _lastEnd;

    /// <summary>Path of the WAV recording, or null when only the transcript is kept.</summary>
    public string? RecordingPath { get; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public TimeSpan Elapsed => TimeSpan.FromSeconds(_samplesFed / (double)Rate);
    public int PendingUtterances => _queue.Reader.Count;

    /// <summary>Raised on the UI thread with a status message (e.g. errors).</summary>
    public event Action<string>? Status;

    public LiveSession(TranscriptionEngine engine, SpeakerAnalyzer analyzer, VoiceLibrary voices, TranscribeOptions options,
                       bool identify, Dispatcher ui, TranscriptResult result, string? recordingPath)
    {
        _engine = engine; _analyzer = analyzer; _voices = voices; _options = options;
        _identify = identify; _ui = ui; _result = result;

        var s = AppSettings.Current;
        var config = new VadModelConfig { SampleRate = Rate, NumThreads = 1 };
        config.SileroVad.Model = SpeakerModels.VadModel;
        config.SileroVad.Threshold = 0.5f;
        config.SileroVad.MinSilenceDuration = s.LiveSilenceMs / 1000f;
        config.SileroVad.MinSpeechDuration = 0.25f;
        config.SileroVad.MaxSpeechDuration = s.LiveMaxUtteranceSeconds;
        config.SileroVad.WindowSize = VadWindow;
        _vad = new VoiceActivityDetector(config, bufferSizeInSeconds: 120);

        RecordingPath = recordingPath;
        if (recordingPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recordingPath)!);
            _recording = LiveRecording.Create(recordingPath);
        }

        _worker = Task.Run(WorkAsync);
    }

    /// <summary>Called from the audio thread with 16 kHz mono samples.</summary>
    public void Feed(float[] samples)
    {
        lock (_feedLock)
        {
            if (_stopped) return;
            WriteRecording(samples);
            _samplesFed += samples.Length;

            _pending.AddRange(samples);
            int full = _pending.Count / VadWindow * VadWindow;
            if (full == 0) return;
            var chunk = _pending.GetRange(0, full).ToArray();
            _pending.RemoveRange(0, full);
            _vad.AcceptWaveform(chunk);
            Drain();
        }
    }

    private const long MaxWavBytes = 3_900_000_000;   // WAV files can't exceed 4 GB (~34 h at 16 kHz mono)
    private long _lastFlush;
    private bool _recordingFailed, _stopped, _warnedBacklog;

    /// <summary>
    /// Appends to the WAV. Flushes every ~10 s so the file stays playable if the PC loses power. If the disk fills
    /// up (or the 4 GB WAV limit is reached) the audio stops being recorded but transcription carries on.
    /// </summary>
    private void WriteRecording(float[] samples)
    {
        if (_recording == null || _recordingFailed) return;
        try
        {
            if (_recording.Length + samples.Length * 2L > MaxWavBytes)
                throw new IOException("The recording reached the 4 GB WAV limit.");
            _recording.WriteSamples(samples);
            if (_samplesFed - _lastFlush > Rate * 10) { _recording.Flush(); _lastFlush = _samplesFed; }
        }
        catch (Exception ex)
        {
            _recordingFailed = true;
            try { _recording.Flush(); } catch { }
            WhisperDesk.App.Log("Live recording", ex);
            _ = _ui.BeginInvoke(() => Status?.Invoke($"⚠ Audio recording stopped ({ex.Message}). The transcript continues; audio up to now is kept."));
        }
    }

    private void Drain()
    {
        while (!_vad.IsEmpty())
        {
            var seg = _vad.Front();
            _queue.Writer.TryWrite((seg.Start / (double)Rate, seg.Samples));
            _vad.Pop();
        }
        if (!_warnedBacklog && _queue.Reader.Count > 30)
        {
            _warnedBacklog = true;
            _ = _ui.BeginInvoke(() => Status?.Invoke("⚠ Transcription is falling behind the audio (nothing is lost — it catches up in pauses). " +
                                                      "A smaller model or the GPU would keep up better."));
        }
    }

    /// <summary>Stops listening, finishes the remaining utterances and closes the recording.</summary>
    public async Task StopAsync()
    {
        lock (_feedLock)
        {
            _stopped = true;
            try { _vad.Flush(); Drain(); } catch (Exception ex) { WhisperDesk.App.Log("Live stop", ex); }
            try { _recording?.Flush(); } catch { }
        }
        _queue.Writer.TryComplete();
        await _worker;
        lock (_feedLock) { try { _recording?.Dispose(); } catch { } }
        // An encrypted recording gets its final WAV sizes now (plain WAVs are already complete).
        if (RecordingPath != null)
            try { await Task.Run(() => LiveRecording.Finish(RecordingPath)); }
            catch (Exception ex) { WhisperDesk.App.Log("Live recording finish", ex); }
    }

    /// <summary>Flushes the WAV header (used by the crash handler).</summary>
    public void FlushRecording()
    {
        try { lock (_feedLock) _recording?.Flush(); } catch { }
    }

    private async Task WorkAsync()
    {
        await foreach (var (start, samples) in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await ProcessUtteranceAsync(start, samples);
            }
            catch (Exception ex)
            {
                _ = _ui.BeginInvoke(() => Status?.Invoke("Live: " + ex.Message));
            }
        }
    }

    [GeneratedRegex(@"^\s*[\[\(].*[\]\)]\s*$")]
    private static partial Regex OnlyBracketed();

    private string? _lastText;
    private int _repeat;

    private async Task ProcessUtteranceAsync(double start, float[] samples)
    {
        double seconds = samples.Length / (double)Rate;
        float peak = samples.Max(Math.Abs);
        if (seconds < 0.3 || peak < 0.01f) return; // too short or too quiet to be speech

        // Whisper wants at least ~1 s; pad short utterances with silence.
        var input = samples.Length >= Rate * 1.1 ? samples : samples.Concat(new float[(int)(Rate * 1.1) - samples.Length]).ToArray();
        var segments = await _engine.TranscribeAsync(input, _options, null, null, CancellationToken.None);
        if (AppSettings.Current.FilterHallucinations) segments = HallucinationFilter.Clean(segments, input);
        var text = string.Join(" ", segments.Select(x => x.Text.Trim())).Trim();
        if (text.Length == 0 || OnlyBracketed().IsMatch(text)) return;   // e.g. [BLANK_AUDIO], (music)

        // A loop across utterances: the same text three times in a row is Whisper repeating itself, not a person.
        var norm = HallucinationFilter.Normalise(text);
        _repeat = norm == _lastText ? _repeat + 1 : 0;
        _lastText = norm;
        if (AppSettings.Current.FilterHallucinations && _repeat >= 2) return;

        SpeakerInfo? speaker = null;
        bool guessed = false;
        if (_identify) (speaker, guessed) = await IdentifyAsync(samples);

        var line = new TranscriptLine(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(start + seconds), text,
                                      segments.FirstOrDefault()?.Language, segments.Count > 0 ? segments.Average(x => x.Probability) : 0, speaker);
        var gapBefore = start - _lastEnd;
        var previousGuess = _guessed;
        await _ui.InvokeAsync(() =>
        {
            // A short line ("Thanks.", "Yes.") was given the previous speaker as a guess. If it sits closer to this line
            // than to the one before it, and this line is a confident different speaker, it was most likely theirs.
            if (!guessed && speaker != null && previousGuess is { } g && g.Line.Speaker != speaker
                && start - g.Line.End.TotalSeconds < g.GapBefore)
                g.Line.Speaker = speaker;

            _result.Lines.Add(line);
            if (speaker != null)
            {
                speaker.TalkTime += TimeSpan.FromSeconds(seconds);
                speaker.Refresh();
            }
        });
        _guessed = guessed ? (line, gapBefore) : null;
        _lastEnd = start + seconds;
    }

    /// <summary>Decides who is talking: someone already heard this session, a saved voice, or a new person.</summary>
    /// <returns>The speaker, and whether it was only a guess (utterance too short to recognise the voice).</returns>
    private async Task<(SpeakerInfo?, bool Guessed)> IdentifyAsync(float[] samples)
    {
        // Very short utterances don't carry enough voice; assume the previous speaker continues.
        if (samples.Length < Rate * 0.9) return (_lastSpeaker, true);

        var e = _analyzer.Embed(samples);
        if (e.Length == 0) return (_lastSpeaker, true);
        var s = AppSettings.Current;

        // 1. Someone already heard in this session?
        int bestIdx = -1; float best = 0;
        for (int i = 0; i < _speakers.Count; i++)
        {
            var score = VoiceLibrary.Cosine(e, _speakers[i].Centroid);
            if (score > best) { best = score; bestIdx = i; }
        }
        if (bestIdx >= 0 && best >= s.LiveSameSpeakerThreshold)
        {
            var (info, centroid, count) = _speakers[bestIdx];
            // Running mean of normalised voiceprints (normalising keeps loud and quiet lines equally weighted).
            var ne = VoiceLibrary.Normalize(e);
            var updated = new float[centroid.Length];
            for (int k = 0; k < updated.Length; k++) updated[k] = (centroid[k] * count + ne[k]) / (count + 1);
            updated = VoiceLibrary.Normalize(updated);
            _speakers[bestIdx] = (info, updated, count + 1);
            info.Embedding = updated;

            // Not recognised yet? The voiceprint gets more reliable with every line, so check the library again.
            if (string.IsNullOrEmpty(info.Recognized) && RecheckAt.Contains(count + 1))
                await RecheckLibraryAsync(info, updated);
            return (_lastSpeaker = info, false);
        }

        // 2. New person this session: maybe someone from the voice library.
        var detected = new DetectedSpeaker
        {
            Id = _speakers.Count,
            Embedding = e,
            MedianPitchHz = PitchEstimator.MedianPitch(samples, Rate),
            Clip = samples.Length > Rate * 6 ? samples[..(Rate * 6)] : samples,
        };
        var match = _voices.Match(new[] { detected });
        var closest = _voices.BestMatch(e);

        SpeakerInfo created = null!;
        await _ui.InvokeAsync(() =>
        {
            created = new SpeakerInfo(detected);
            if (match.TryGetValue(detected.Id, out var m) && !NameInUse(m.Voice.Name))
            {
                created.Name = m.Voice.Name;
                created.Sex = m.Voice.Sex;
                created.Recognized = $"✓ recognised ({m.Score * 100:0}%)";
            }
            else if (closest is { } c)
            {
                created.MatchHint = $"closest: {c.Voice.Name} {c.Score * 100:0}%";
            }
            _result.Speakers.Add(created);
        });
        _speakers.Add((created, VoiceLibrary.Normalize(e), 1));
        return (_lastSpeaker = created, false);
    }

    /// <summary>Line counts at which an unrecognised speaker is compared with the voice library again.</summary>
    private static readonly HashSet<int> RecheckAt = new() { 2, 3, 4, 6, 8, 12, 16, 24, 32 };

    private bool NameInUse(string name) =>
        _result.Speakers.Any(sp => string.Equals(sp.Name, name, StringComparison.OrdinalIgnoreCase));

    private async Task RecheckLibraryAsync(SpeakerInfo info, float[] centroid)
    {
        if (_voices.BestMatch(centroid) is not { } best) return;
        var threshold = AppSettings.Current.VoiceMatchThreshold;
        await _ui.InvokeAsync(() =>
        {
            if (!string.IsNullOrEmpty(info.Recognized)) return;
            if (best.Score >= threshold && !NameInUse(best.Voice.Name) && string.IsNullOrWhiteSpace(info.Name))
            {
                info.Name = best.Voice.Name;
                info.Sex = best.Voice.Sex;
                info.Recognized = $"✓ recognised ({best.Score * 100:0}%)";
                info.MatchHint = null;
            }
            else
            {
                info.MatchHint = $"closest: {best.Voice.Name} {best.Score * 100:0}%";
            }
        });
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _vad.Dispose();
        try { _recording?.Dispose(); } catch { }
    }
}
