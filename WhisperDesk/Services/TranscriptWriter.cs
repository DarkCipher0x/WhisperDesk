using System.IO;
using System.Text;
using System.Text.Json;

namespace WhisperDesk.Services;

/// <summary>
/// Writes transcripts as .txt, .srt, .vtt, .json, and (when enabled in "Save as") Word .docx and .pdf,
/// tagging every line with its speaker when known.
/// </summary>
public static class TranscriptWriter
{
    // Text formats get a UTF-8 BOM so Notepad, video players and OBS never mis-detect the encoding.
    private static readonly UTF8Encoding Utf8Bom = new(true);
    private static readonly UTF8Encoding Utf8 = new(false);

    public static async Task<List<string>> WriteAsync(TranscriptResult result, string outputDir,
        bool txt, bool srt, bool vtt, bool json)
    {
        Directory.CreateDirectory(outputDir);
        var baseName = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(result.SourceFile));
        var title = Path.GetFileNameWithoutExtension(result.SourceFile);
        var written = new List<string>();

        async Task Save(string ext, string content, Encoding enc)
        {
            var path = baseName + ext;
            await File.WriteAllTextAsync(path, content, enc);
            written.Add(path);
        }

        if (txt) await Save(".txt", ToText(result), Utf8Bom);
        if (srt) await Save(".srt", ToSrt(result), Utf8Bom);
        if (vtt) await Save(".vtt", ToVtt(result), Utf8);   // WebVTT spec: no BOM required, UTF-8 assumed
        if (json) await Save(".json", ToJson(result), Utf8);
        if (AppSettings.Current.Docx) { await Task.Run(() => DocumentExporter.WriteDocx(result, title, baseName + ".docx")); written.Add(baseName + ".docx"); }
        if (AppSettings.Current.Pdf) { await Task.Run(() => DocumentExporter.WritePdf(result, title, baseName + ".pdf")); written.Add(baseName + ".pdf"); }
        if (AppSettings.Current.Summary) written.AddRange(await WriteSummaryAllAsync(result, title, baseName + ".summary"));
        result.OutputDir = outputDir;
        return written;
    }

    /// <summary>Writes the call summary as .txt, .docx, .pdf and .json next to <paramref name="basePath"/>.</summary>
    public static async Task<List<string>> WriteSummaryAllAsync(TranscriptResult result, string title, string basePath, CallSummary? summary = null)
    {
        summary ??= await CallSummary.BuildAsync(result, title, null, null, CancellationToken.None);
        var files = new List<string> { basePath + ".txt", basePath + ".docx", basePath + ".pdf", basePath + ".json" };
        await File.WriteAllTextAsync(files[0], summary.ToText(), Utf8Bom);
        await Task.Run(() => DocumentExporter.WriteSummaryDocx(summary, files[1]));
        await Task.Run(() => DocumentExporter.WriteSummaryPdf(summary, files[2]));
        await File.WriteAllTextAsync(files[3], summary.ToJson(), Utf8);
        return files;
    }

    /// <summary>Writes one file in the format implied by its extension (used by Export…).</summary>
    public static void ExportTo(TranscriptResult r, string path, string title)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".docx": DocumentExporter.WriteDocx(r, title, path); break;
            case ".pdf": DocumentExporter.WritePdf(r, title, path); break;
            case ".srt": File.WriteAllText(path, ToSrt(r), Utf8Bom); break;
            case ".vtt": File.WriteAllText(path, ToVtt(r), Utf8); break;
            case ".json": File.WriteAllText(path, ToJson(r), Utf8); break;
            default: File.WriteAllText(path, r.Render().Replace("\n", Environment.NewLine), Utf8Bom); break;
        }
    }

    private static IEnumerable<TranscriptLine> Spoken(TranscriptResult r) => r.Lines.Where(l => l.Text.Trim().Length > 0);

    /// <summary>With speakers: a timestamped log, "[00:01:12] Anna (Female): text". Without: plain text.</summary>
    public static string ToText(TranscriptResult r) =>
        r.HasSpeakers || AppSettings.Current.AlwaysTimestampTxt
            ? r.Render().Replace("\n", Environment.NewLine)
            : string.Join(Environment.NewLine, Spoken(r).Select(l => l.Text.Trim())) + Environment.NewLine;

    public static string ToSrt(TranscriptResult r)
    {
        var sb = new StringBuilder();
        int i = 1;
        foreach (var l in Spoken(r))
        {
            var who = l.Speaker is { } s && AppSettings.Current.SpeakerInSubtitles ? s.Tag + ": " : "";
            sb.Append(i++).Append('\n')
              .Append(Stamp(l.Start, ',')).Append(" --> ").Append(Stamp(l.End, ',')).Append('\n')
              .Append(who).Append(l.Text.Trim()).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ToVtt(TranscriptResult r)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var l in Spoken(r))
        {
            // WebVTT voice span: <v Anna (Female)>text
            var text = l.Speaker is { } s && AppSettings.Current.SpeakerInSubtitles ? $"<v {s.Tag}>{l.Text.Trim()}" : l.Text.Trim();
            sb.Append(Stamp(l.Start, '.')).Append(" --> ").Append(Stamp(l.End, '.')).Append('\n')
              .Append(text).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ToJson(TranscriptResult r)
    {
        var doc = new
        {
            generator = "WhisperDesk by darkcipher0x",
            source = Path.GetFileName(r.SourceFile),
            speakers = r.Speakers.Select(s => new
            {
                id = s.Id + 1,
                name = s.Label,
                sex = s.Sex,
                talkSeconds = Math.Round(s.TalkTime.TotalSeconds, 1),
            }),
            segments = Spoken(r).Select(l => new
            {
                start = Math.Round(l.Start.TotalSeconds, 3),
                end = Math.Round(l.End.TotalSeconds, 3),
                speaker = l.Speaker?.Label,
                sex = l.Speaker?.Sex,
                text = l.Text.Trim(),
                language = l.Language,
                probability = Math.Round(l.Probability, 4),
                edited = l.IsEdited,
                originalText = l.IsEdited ? l.OriginalText : null,
                edits = l.IsEdited
                    ? l.Edits.Select(e => new { at = e.At.ToString("o"), user = e.User, reason = e.Reason, before = e.OldText, after = e.NewText })
                    : null,
            }),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Stamp(TimeSpan t, char msSeparator) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{msSeparator}{t.Milliseconds:000}";
}
