using System.IO;

namespace WhisperDesk.Services;

/// <summary>
/// Re-runs speaker detection on a finished transcript (after a file job, a live session, or from the library).
/// Every line keeps its text and edit history; only the speakers are rebuilt.
/// </summary>
public static class SpeakerReanalysis
{
    public static async Task<int> RunAsync(TranscriptResult result, SpeakerAnalyzer analyzer, VoiceLibrary voices,
        int speakerCount, bool compareWithLibrary, IProgress<string> progress, CancellationToken ct)
    {
        if (result.MediaPath is not { } media || !File.Exists(media))
            throw new InvalidOperationException("This transcript has no audio (it was saved as transcript only, or the file was moved), so speakers can't be re-analysed.");
        var ffmpeg = AudioDecoder.FindFfmpeg() ?? throw new InvalidOperationException("FFmpeg not found.");

        await SpeakerModels.EnsureAsync(progress, ct);
        progress.Report("Re-analysing speakers: reading audio…");
        var samples = await Task.Run(() => AudioDecoder.DecodeAsync(ffmpeg, media, ct), ct);

        progress.Report("Re-analysing speakers: finding voices…");
        var diarization = await Task.Run(() => analyzer.Analyze(samples, speakerCount, ct), ct);
        if (diarization.Speakers.Count == 0) throw new InvalidOperationException("No speech was found in the audio.");

        progress.Report("Re-analysing speakers: checking each line…");
        result.Speakers.Clear();
        foreach (var d in diarization.Speakers) result.Speakers.Add(new SpeakerInfo(d));
        var spans = result.Lines.Select(l => (l.Start.TotalSeconds, l.End.TotalSeconds)).ToList();
        var ids = await Task.Run(() => SpeakerAnalyzer.AssignLines(spans, diarization.Turns), ct);
        result.ApplySpeakerIds(ids);

        var lineInfo = spans.Select((l, i) => (l.Item1, l.Item2, ids[i])).ToList();
        var refined = await Task.Run(() => analyzer.Refine(samples, lineInfo, diarization.Speakers, ct), ct);
        result.ApplySpeakerIds(refined);

        if (compareWithLibrary) ApplyVoiceLibrary(result, diarization, voices);
        return result.Speakers.Count;
    }

    /// <summary>Fills in names and sexes for voices already in the voice library, and closest-match hints for the rest.</summary>
    public static void ApplyVoiceLibrary(TranscriptResult result, DiarizationResult diarization, VoiceLibrary voices)
    {
        var matches = voices.Match(diarization.Speakers);
        foreach (var (speakerId, (voice, score)) in matches)
        {
            var s = result.Speakers.First(x => x.Id == speakerId);
            s.Name = voice.Name;
            s.Sex = voice.Sex;
            s.Recognized = $"✓ recognised ({score * 100:0}%)";
        }
        foreach (var d in diarization.Speakers.Where(d => !matches.ContainsKey(d.Id)))
            if (voices.BestMatch(d.Embedding) is { } best)
                result.Speakers.First(x => x.Id == d.Id).MatchHint = $"closest: {best.Voice.Name} {best.Score * 100:0}%";
    }
}
