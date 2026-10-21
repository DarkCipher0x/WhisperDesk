using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace WhisperDesk.Services;

public sealed class BulkReportOptions
{
    public string Title { get; set; } = "Recordings report";
    public string PreparedBy { get; set; } = Environment.UserName;
    public string Reference { get; set; } = "";
    public string Purpose { get; set; } = "";
    /// <summary>True: one document covering every recording. False: one document per recording (plus an index).</summary>
    public bool Combined { get; set; } = true;
    public bool Overview { get; set; } = true;
    public bool Summaries { get; set; } = true;
    public bool AiPerRecording { get; set; }
    public bool AiOverall { get; set; }
    public string AiPrompt { get; set; } = BulkReport.DefaultAiPrompt;
    public bool Participation { get; set; } = true;
    public bool Transcripts { get; set; } = true;
    public bool EditLog { get; set; } = true;
    public bool Integrity { get; set; } = true;
    /// <summary>Comma-separated words or phrases; lines containing any are listed (empty = off).</summary>
    public string Keywords { get; set; } = "";
    /// <summary>Only include transcript lines spoken by these people (comma-separated; empty = everyone).</summary>
    public string OnlySpeakers { get; set; } = "";
    public List<string> Formats { get; set; } = new() { ".pdf" };
    public bool Zip { get; set; }
}

/// <summary>
/// Reports across several library recordings: an overview table, per-recording summaries, participation across
/// recordings, keyword hits, transcripts, the edit (audit) log, SHA-256 integrity fingerprints and an optional
/// consolidated report written by a local AI model. Every report ends with how and by whom it was produced, and a
/// manifest of SHA-256 hashes is written next to the generated files.
/// </summary>
public static class BulkReport
{
    public const string DefaultAiPrompt =
"""
You are given notes on {count} recorded conversations, in date order. Write one consolidated report for someone who
was not there. Use these sections, each starting with "## <heading>":
## Overview — what these recordings are about and who is involved.
## Timeline — the key events and statements in date order, citing the recording title, [time] and speaker.
## Recurring themes
## Commitments, decisions and action items — who said they would do what, and in which recording.
## Inconsistencies or changes — where people said different things in different recordings (quote both, with references).
## Open questions
Only use what is in the notes. Do not invent names, dates or facts. If something is unclear, say so.

Notes:
{notes}
""";

    public static async Task<List<string>> GenerateAsync(IReadOnlyList<RecordingMeta> recordings, BulkReportOptions o, string folder,
                                                         IProgress<string> status, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        var items = recordings.OrderBy(r => r.Created).ToList();
        var loaded = new List<(RecordingMeta Meta, TranscriptResult Result, CallSummary Summary)>();
        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            status.Report($"Reading {i + 1} of {items.Count}: {items[i].Title}…");
            var r = await Task.Run(() => RecordingLibrary.Load(items[i]), ct);
            var s = o.AiPerRecording
                ? await CallSummary.BuildAsync(r, items[i].Title, items[i].Created, status, ct)
                : await Task.Run(() => CallSummary.Build(r, items[i].Title, items[i].Created), ct);
            loaded.Add((items[i], r, s));
        }

        string? aiOverall = null, aiNote = null;
        if (o.AiOverall)
        {
            try
            {
                status.Report($"Local AI ({AiReport.ModelName}): writing the consolidated report…");
                var notes = new StringBuilder();
                foreach (var (m, _, s) in loaded)
                {
                    notes.AppendLine($"### {m.Title} — {m.Created:yyyy-MM-dd HH:mm} — {m.Duration} — speakers: {m.SpeakerNames}");
                    notes.AppendLine(Trim(s.Report, 6000));
                    foreach (var (heading, list, _) in s.Sections())
                        foreach (var it in list.Take(12)) notes.AppendLine($"- {heading}: {it.Display}");
                    notes.AppendLine();
                }
                var prompt = (string.IsNullOrWhiteSpace(o.AiPrompt) ? DefaultAiPrompt : o.AiPrompt)
                    .Replace("{count}", loaded.Count.ToString()).Replace("{notes}", Trim(notes.ToString(), 60_000));
                aiOverall = (await AiReport.AskAsync(prompt, ct)).Trim();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { aiNote = "The local AI report could not be written: " + ex.Message; }
        }

        Directory.CreateDirectory(folder);
        var written = new List<string>();
        var baseName = RecordingLibrary.Sanitize(o.Title);
        var provenance = await ProvenanceAsync(o, loaded.Count, started, aiOverall != null);

        if (o.Combined)
        {
            status.Report("Writing the report…");
            var doc = NewDoc(o, loaded.Count);
            AddFront(doc, o, loaded, aiOverall, aiNote);
            foreach (var x in loaded) { doc.Break(); AddRecording(doc, o, x.Meta, x.Result, x.Summary, level: 1); }
            AddProvenance(doc, provenance);
            written.AddRange(await SaveAllAsync(doc, Path.Combine(folder, baseName), o.Formats, ct));
        }
        else
        {
            var index = NewDoc(o, loaded.Count);
            AddFront(index, o, loaded, aiOverall, aiNote);
            AddProvenance(index, provenance);
            written.AddRange(await SaveAllAsync(index, Path.Combine(folder, baseName + " - index"), o.Formats, ct));
            int n = 0;
            foreach (var x in loaded)
            {
                ct.ThrowIfCancellationRequested();
                status.Report($"Writing report {++n} of {loaded.Count}…");
                var doc = new ReportDocument
                {
                    Title = x.Meta.Title,
                    Subtitle = $"{x.Meta.Created:yyyy-MM-dd HH:mm} · {x.Meta.Duration} · part of “{o.Title}”" + (o.Reference.Length > 0 ? $" · ref {o.Reference}" : ""),
                };
                AddRecording(doc, o, x.Meta, x.Result, x.Summary, level: 1);
                AddProvenance(doc, provenance);
                written.AddRange(await SaveAllAsync(doc, Path.Combine(folder, $"{n:00} {RecordingLibrary.Sanitize(x.Meta.Title)}"), o.Formats, ct));
            }
        }

        // Chain of custody: SHA-256 of every generated file.
        var manifest = Path.Combine(folder, baseName + " - manifest.sha256.txt");
        var sb = new StringBuilder($"# SHA-256 of files generated by WhisperDesk {AboutWindow.Version} on {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n" +
                                   $"# {Environment.MachineName} · {Environment.UserDomainName}\\{Environment.UserName}\n");
        foreach (var f in written) sb.Append(Sha256(f)).Append("  ").Append(Path.GetFileName(f)).Append('\n');
        await File.WriteAllTextAsync(manifest, sb.ToString(), ct);
        written.Add(manifest);

        if (o.Zip)
        {
            status.Report("Bundling into a .zip…");
            var zip = Path.Combine(folder, baseName + ".zip");
            if (File.Exists(zip)) File.Delete(zip);
            await Task.Run(() =>
            {
                using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
                foreach (var f in written) z.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Optimal);
            }, ct);
            written.Add(zip);
        }
        return written;
    }

    private static ReportDocument NewDoc(BulkReportOptions o, int count) => new()
    {
        Title = o.Title,
        Subtitle = $"{count} recording(s) · prepared {DateTime.Now:yyyy-MM-dd HH:mm} by {o.PreparedBy}" + (o.Reference.Length > 0 ? $" · ref {o.Reference}" : ""),
    };

    private static void AddFront(ReportDocument doc, BulkReportOptions o, List<(RecordingMeta Meta, TranscriptResult Result, CallSummary Summary)> loaded,
                                 string? aiOverall, string? aiNote)
    {
        if (o.Purpose.Length > 0) doc.H1("Purpose").P(o.Purpose);

        if (o.Overview)
        {
            doc.H1("Recordings");
            doc.T(new[] { "#", "Title", "Date", "Length", "Speakers", "Words", "Edits" },
                loaded.Select((x, i) => new[]
                {
                    (i + 1).ToString(), x.Meta.Title, x.Meta.Created.ToString("yyyy-MM-dd HH:mm"), x.Meta.Duration, x.Meta.SpeakerNames,
                    x.Summary.Words.ToString("N0"), x.Summary.Edits.ToString(),
                }), new[] { 0.4, 3, 1.6, 0.9, 3, 0.8, 0.6 });
            var total = TimeSpan.FromSeconds(loaded.Sum(x => x.Meta.DurationSeconds));
            doc.P($"Total: {total:h\\:mm\\:ss} of recordings, {loaded.Sum(x => x.Summary.Words):N0} words, {loaded.Sum(x => x.Summary.Edits)} manual correction(s).", muted: true);
        }

        if (aiOverall != null)
        {
            doc.H1($"Consolidated report (local AI · {AiReport.ModelName})");
            doc.Markdown(aiOverall);
            doc.P("Written by a local AI model from the recordings' notes — check it against the transcripts.", muted: true);
        }
        else if (aiNote != null) doc.P(aiNote, muted: true);

        if (o.Participation)
        {
            var people = loaded.SelectMany(x => x.Summary.Speakers.Select(s => (Rec: x.Meta.Id, s)))
                .GroupBy(p => p.s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new[]
                {
                    g.Key, g.Select(p => p.Rec).Distinct().Count().ToString(),
                    TimeSpan.FromSeconds(g.Sum(p => p.s.Talk.TotalSeconds)).ToString(@"h\:mm\:ss"),
                    g.Sum(p => p.s.Words).ToString("N0"), g.Sum(p => p.s.Turns).ToString(),
                }).OrderByDescending(r => r[2]).ToList();
            if (people.Count > 0)
            {
                doc.H1("Participation across recordings");
                doc.T(new[] { "Person", "Recordings", "Talk time", "Words", "Turns" }, people, new[] { 3, 1, 1.2, 1, 0.8 });
            }
        }

        var keywords = o.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keywords.Length > 0)
        {
            var hits = loaded.SelectMany(x => x.Result.Lines
                    .Where(l => keywords.Any(k => l.Text.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    .Select(l => new[] { x.Meta.Title, l.Time, l.Speaker?.Label ?? "", l.Text.Trim() + (l.IsEdited ? " [edited]" : "") }))
                .ToList();
            doc.H1($"Keyword hits: {string.Join(", ", keywords)}");
            if (hits.Count == 0) doc.P("No lines contain these words.", muted: true);
            else doc.T(new[] { "Recording", "Time", "Speaker", "Text" }, hits, new[] { 2.2, 1, 1.6, 6 });
        }
    }

    private static void AddRecording(ReportDocument doc, BulkReportOptions o, RecordingMeta m, TranscriptResult r, CallSummary s, int level)
    {
        doc.H1(m.Title);
        doc.KV(new (string, string)[]
        {
            ("Date", m.Created.ToString("yyyy-MM-dd HH:mm")), ("Length", m.Duration), ("Type", m.Type), ("Source", m.Source),
            ("Speakers", m.SpeakerNames), ("Transcribed with", m.Model ?? "unknown"), ("Language", m.Language ?? "unknown"),
            ("Library folder", m.FolderDisplay), ("Tags", m.TagText), ("Notes", m.Notes),
        }.Where(x => x.Item2.Length > 0));

        if (o.Summaries)
        {
            doc.H2($"Summary ({s.ReportBy})");
            doc.Markdown(s.Report, level: 2);
            foreach (var (heading, list, _) in s.Sections().Skip(1))
                if (list.Count > 0)
                {
                    doc.H2(heading);
                    foreach (var it in list.Take(25)) doc.P("•  " + it.Display);
                }
        }

        if (o.Integrity)
        {
            doc.H2("Integrity (SHA-256)");
            var rows = new List<string[]>();
            bool anyEncrypted = false;
            foreach (var (label, path) in new[] { ("Audio / video", m.HasMedia ? m.MediaLogical : ""), ("Transcript", Path.Combine(m.Dir, "transcript.json")),
                                                  ("Metadata", Path.Combine(m.Dir, "metadata.json")) })
                if (path.Length > 0 && Security.SecureFile.Exists(path))
                {
                    // Fingerprints are always of the content itself, also when it is stored encrypted.
                    bool enc = Security.SecureFile.IsEncrypted(path);
                    anyEncrypted |= enc;
                    using var content = Security.SecureFile.OpenRead(path);
                    using var counter = new CountingStream(content);
                    var hash = Convert.ToHexString(SHA256.HashData(counter)).ToLowerInvariant();
                    rows.Add(new[] { label, Path.GetFileName(path) + (enc ? " (encrypted on disk)" : ""), counter.Count.ToString("N0") + " bytes", hash });
                }
            doc.T(new[] { "Item", "File", "Size", "SHA-256" }, rows, new[] { 1.4, 2, 1.4, 5.2 });
            doc.P(anyEncrypted
                ? "Fingerprints are of the decrypted content. The library is encrypted on disk, so recompute them from an export (Share → .wdsession or the original files)."
                : "Recompute these fingerprints (e.g. PowerShell: Get-FileHash -Algorithm SHA256) to show the files were not altered after this report.", muted: true);
        }

        if (o.Transcripts)
        {
            var only = o.OnlySpeakers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            doc.H2(only.Length > 0 ? $"Transcript (only {string.Join(", ", only)})" : "Transcript");
            foreach (var l in r.Lines.Where(l => l.Text.Trim().Length > 0))
            {
                if (only.Length > 0 && !only.Any(n => string.Equals(n, l.Speaker?.Label, StringComparison.OrdinalIgnoreCase))) continue;
                doc.L(l.Time, l.Speaker?.Tag, l.Speaker != null ? DocumentExporter.PrintHex(l.Speaker) : null, l.Text.Trim(), l.IsEdited);
            }
        }

        if (o.EditLog)
        {
            var edits = r.Lines.SelectMany(l => l.Edits.Select(e => (l, e))).OrderBy(x => x.e.At).ToList();
            if (edits.Count > 0)
            {
                doc.H2($"Edit log ({edits.Count})");
                doc.T(new[] { "When", "By", "Line", "Reason", "Before", "After" },
                    edits.Select(x => new[] { x.e.AtText, x.e.User, $"[{x.l.Time}] {x.l.Speaker?.Label}", x.e.Reason, x.e.OldText, x.e.NewText }),
                    new[] { 1.6, 1.2, 1.4, 1.6, 2.2, 2.2 });
            }
        }
    }

    private static async Task<List<(string, string)>> ProvenanceAsync(BulkReportOptions o, int count, DateTimeOffset started, bool aiUsed)
    {
        var rows = new List<(string, string)>
        {
            ("Generated", $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} (took {(DateTimeOffset.Now - started):m\\:ss})"),
            ("Prepared by", o.PreparedBy),
            ("Windows account", $"{Environment.UserDomainName}\\{Environment.UserName}"),
            ("Computer", Environment.MachineName),
            ("Operating system", System.Runtime.InteropServices.RuntimeInformation.OSDescription),
            ("Software", $"WhisperDesk {AboutWindow.Version} by darkcipher0x"),
            ("Recordings", count.ToString()),
            ("Summaries written by", o.AiPerRecording ? $"local AI ({AiReport.ModelName})" : "WhisperDesk offline analysis (no AI)"),
        };
        if (aiUsed || o.AiPerRecording)
        {
            var (version, details) = await AiReport.DescribeAsync(CancellationToken.None);
            rows.Add(("AI model", AiReport.ModelName + (details != null ? $" ({details})" : "")));
            rows.Add(("AI runtime", "Ollama" + (version != null ? " " + version : "") + " at " + AiReport.ServerUrl));
            rows.Add(("AI processed on", AiReport.ServerLocation(AiReport.ServerUrl)));
        }
        else rows.Add(("Network", "none (everything processed on this PC)"));
        rows.Add(("Integrity", "SHA-256 of every generated file is in the accompanying manifest"));
        return rows;
    }

    private static void AddProvenance(ReportDocument doc, List<(string, string)> rows)
    {
        doc.H1("How this report was made");
        doc.KV(rows);
        doc.P("Summaries and keyword detection are automatic — check them against the transcripts and audio.", muted: true);
    }

    private static async Task<List<string>> SaveAllAsync(ReportDocument doc, string basePath, List<string> formats, CancellationToken ct)
    {
        var files = new List<string>();
        foreach (var ext in formats.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var path = basePath + ext;
            await Task.Run(() => doc.Save(path), ct);
            files.Add(path);
        }
        return files;
    }

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "\n…(truncated)";
}

/// <summary>Passes a stream through while counting the bytes (size of decrypted content for reports).</summary>
internal sealed class CountingStream(Stream inner) : Stream
{
    public long Count { get; private set; }
    public override int Read(byte[] buffer, int offset, int count) { int n = inner.Read(buffer, offset, count); Count += n; return n; }
    public override int Read(Span<byte> buffer) { int n = inner.Read(buffer); Count += n; return n; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => Count; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}