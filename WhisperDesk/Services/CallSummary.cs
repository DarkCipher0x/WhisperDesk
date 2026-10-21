using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhisperDesk.Services;

public sealed record SpeakerStat(string Name, string Sex, TimeSpan Talk, double Share, int Words, int Turns);

/// <summary>One summarised item, linked back to where it was said.</summary>
public sealed record SummaryItem(TimeSpan At, string? Speaker, string Text)
{
    public string Time => At.ToString(@"hh\:mm\:ss");
    public string Display => Speaker != null ? $"[{Time}] {Speaker}: {Text}" : $"[{Time}] {Text}";
}

/// <summary>
/// Offline call summary (no AI service, nothing leaves the PC): overview, who spoke how much, key points, action items,
/// decisions and open questions — each linked to its timestamp and speaker.
/// </summary>
public sealed partial class CallSummary
{
    public string Title { get; init; } = "";
    public string Date { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public int Words { get; init; }
    public int Edits { get; init; }
    public List<SpeakerStat> Speakers { get; init; } = new();
    public List<SummaryItem> KeyPoints { get; init; } = new();
    public List<SummaryItem> ActionItems { get; init; } = new();
    public List<SummaryItem> Decisions { get; init; } = new();
    public List<SummaryItem> Questions { get; init; } = new();

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","but","if","then","so","to","of","in","on","at","for","with","as","by","from","up","about","into",
        "over","after","is","are","was","were","be","been","being","am","do","does","did","have","has","had","i","you","he","she",
        "it","we","they","me","him","her","us","them","my","your","his","its","our","their","this","that","these","those","there",
        "here","what","which","who","whom","when","where","why","how","all","any","both","each","few","more","most","other","some",
        "such","no","nor","not","only","own","same","than","too","very","can","will","just","don't","should","now","yeah","yes",
        "okay","ok","um","uh","like","well","also","get","got","go","going","know","think","really","right","oh","one","would",
        "could","let's","i'm","it's","that's","i'll","we'll","you're","we're","they're","gonna","thing","things","lot","bit",
        "make","sure","want","wanna","something","anything","everything","maybe","actually","basically","kind","sort","mean",
        "said","say","says","see","look","good","yeah","need","because","still","even","much","many","been","there's","what's",
    };

    [GeneratedRegex(@"\b(i'll|i will|we'll|we will|you'll|need to|needs to|have to|has to|going to|gonna|let's|let us|must|should|" +
                    @"take care of|follow up|follow-up|action item|to-do|todo|deadline|assign|responsible for|" +
                    @"by (monday|tuesday|wednesday|thursday|friday|saturday|sunday|tomorrow|tonight|next week|end of (the )?(day|week|month)))\b",
                    RegexOptions.IgnoreCase)]
    private static partial Regex ActionRx();

    [GeneratedRegex(@"\b(decided|decide to|agreed|agree to|we'll go with|let's go with|go ahead with|approved|sign(ed)? off|settled on|" +
                    @"final decision|conclusion|we will ship|we'll ship|confirmed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DecisionRx();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex WordRx();

    public static CallSummary Build(TranscriptResult r, string title, DateTime? date = null)
    {
        var lines = r.Lines.Where(l => l.Text.Trim().Length > 0).ToList();
        var sentences = lines.SelectMany(l => SentenceSplit().Split(l.Text.Trim())
                                .Where(s => s.Trim().Length > 0)
                                .Select(s => (Line: l, Text: s.Trim())))
                             .ToList();
        int WordCount(string s) => WordRx().Matches(s).Count;
        var totalWords = sentences.Sum(s => WordCount(s.Text));

        // Who spoke how much
        var total = lines.Sum(l => (l.End - l.Start).TotalSeconds);
        var stats = r.Speakers.Select(sp =>
        {
            var mine = lines.Where(l => l.Speaker == sp).ToList();
            var secs = mine.Sum(l => (l.End - l.Start).TotalSeconds);
            int turns = 0; SpeakerInfo? prev = null;
            foreach (var l in lines) { if (l.Speaker == sp && prev != sp) turns++; prev = l.Speaker; }
            return new SpeakerStat(sp.Label, sp.Sex, TimeSpan.FromSeconds(secs), total > 0 ? secs / total : 0,
                                   mine.Sum(l => WordCount(l.Text)), turns);
        }).Where(s => s.Words > 0).OrderByDescending(s => s.Talk).ToList();

        // Key points: score sentences by how many of the call's important words they contain.
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, text) in sentences)
            foreach (Match w in WordRx().Matches(text))
                if (w.Value.Length > 2 && !Stop.Contains(w.Value)) freq[w.Value] = freq.GetValueOrDefault(w.Value) + 1;

        double Score(string s)
        {
            var words = WordRx().Matches(s).Select(m => m.Value).ToList();
            if (words.Count < 6) return 0;
            var content = words.Where(w => freq.ContainsKey(w)).Distinct(StringComparer.OrdinalIgnoreCase).Sum(w => Math.Log(1 + freq[w]));
            return content / Math.Sqrt(words.Count);
        }
        int keyCount = Math.Clamp(sentences.Count / 6, 3, 12);
        var keyPoints = sentences.Select((s, i) => (s, i, score: Score(s.Text)))
                                 .Where(x => x.score > 0)
                                 .OrderByDescending(x => x.score).Take(keyCount)
                                 .OrderBy(x => x.i)
                                 .Select(x => Item(x.s.Line, x.s.Text)).ToList();

        var questions = sentences.Where(s => s.Text.EndsWith('?') && WordCount(s.Text) >= 4).Select(s => Item(s.Line, s.Text)).ToList();
        var actions = sentences.Where(s => !s.Text.EndsWith('?') && ActionRx().IsMatch(s.Text) && WordCount(s.Text) >= 4)
                               .Select(s => Item(s.Line, s.Text)).ToList();
        var decisions = sentences.Where(s => !s.Text.EndsWith('?') && DecisionRx().IsMatch(s.Text)).Select(s => Item(s.Line, s.Text)).ToList();

        var summary = new CallSummary
        {
            Title = title,
            Date = (date ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm"),
            Duration = lines.Count > 0 ? lines[^1].End : TimeSpan.Zero,
            Words = totalWords,
            Edits = lines.Sum(l => l.Edits.Count),
            Speakers = stats,
            KeyPoints = keyPoints,
            ActionItems = actions,
            Decisions = decisions,
            Questions = questions,
        };
        summary.Report = BuildOfflineReport(summary, lines, sentences, freq, Score);
        summary.AddBaseProvenance(r);
        return summary;

        static SummaryItem Item(TranscriptLine l, string text) => new(l.Start, l.Speaker?.Label, text);
    }

    /// <summary>The written report: an overview paragraph, then several paragraphs/sections. Plain text with "## " headings.</summary>
    public string Report { get; set; } = "";
    /// <summary>"offline" or e.g. "local AI · llama3.1:8b".</summary>
    public string ReportBy { get; set; } = "offline";

    /// <summary>
    /// Offline narrative report: overview, a chronological walk-through split into phases with the most telling quotes
    /// of each, and a participation paragraph. Built from word statistics — no AI, nothing leaves the PC.
    /// </summary>
    private static string BuildOfflineReport(CallSummary s, List<TranscriptLine> lines, List<(TranscriptLine Line, string Text)> sentences,
                                             Dictionary<string, int> freq, Func<string, double> score)
    {
        if (lines.Count == 0) return "Nothing was said.";
        var sb = new StringBuilder();
        string Names(IEnumerable<string> n)
        {
            var l = n.Distinct().ToList();
            return l.Count switch { 0 => "nobody identified", 1 => l[0], _ => string.Join(", ", l.Take(l.Count - 1)) + " and " + l[^1] };
        }
        string Topics(IEnumerable<(TranscriptLine Line, string Text)> part, int take)
        {
            var local = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, text) in part)
                foreach (Match w in WordRx().Matches(text))
                    if (w.Value.Length > 3 && !Stop.Contains(w.Value)) local[w.Value] = local.GetValueOrDefault(w.Value) + 1;
            var top = local.Where(kv => kv.Value >= 2 || local.Count < 8).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                           .Take(take).Select(kv => kv.Key.ToLowerInvariant()).ToList();
            return top.Count == 0 ? "general conversation" : string.Join(", ", top);
        }

        // 1. Overview
        var who = s.Speakers.Select(x => x.Name).ToList();
        sb.AppendLine("## Overview");
        sb.Append($"This {s.DurationText} recording has {s.Words:N0} words");
        sb.Append(who.Count > 0 ? $" spoken by {Names(who)}. " : ". ");
        if (s.Speakers.Count > 1)
            sb.Append($"{s.Speakers[0].Name} spoke the most ({s.Speakers[0].Share:P0} of the talking time). ");
        sb.Append($"The conversation centred on {Topics(sentences, 6)}. ");
        var counts = new List<string>();
        if (s.ActionItems.Count > 0) counts.Add($"{s.ActionItems.Count} possible action item{(s.ActionItems.Count == 1 ? "" : "s")}");
        if (s.Decisions.Count > 0) counts.Add($"{s.Decisions.Count} decision{(s.Decisions.Count == 1 ? "" : "s")}");
        if (s.Questions.Count > 0) counts.Add($"{s.Questions.Count} question{(s.Questions.Count == 1 ? "" : "s")}");
        if (counts.Count > 0) sb.Append($"Along the way there were {Names(counts)}, listed further below.");
        sb.AppendLine().AppendLine();

        // 2. Walk-through: phases of ~5 minutes (2–8 of them), each with its speakers, topics and best quotes.
        var total = lines[^1].End.TotalSeconds;
        int phases = Math.Clamp((int)Math.Round(total / 300), 1, 8);
        if (sentences.Count < 12) phases = 1;
        sb.AppendLine("## What was said");
        for (int p = 0; p < phases; p++)
        {
            double a = total * p / phases, b = total * (p + 1) / phases;
            var part = sentences.Where(x => x.Line.Start.TotalSeconds >= a && (x.Line.Start.TotalSeconds < b || p == phases - 1)).ToList();
            if (part.Count == 0) continue;
            var speakers = part.Where(x => x.Line.Speaker != null).GroupBy(x => x.Line.Speaker!.Label)
                               .OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            var quotes = part.Select((x, i) => (x, i, sc: score(x.Text))).Where(q => q.sc > 0)
                             .OrderByDescending(q => q.sc).Take(phases == 1 ? 4 : 2).OrderBy(q => q.i).Select(q => q.x).ToList();
            var from = TimeSpan.FromSeconds(part[0].Line.Start.TotalSeconds);
            var to = TimeSpan.FromSeconds(part[^1].Line.End.TotalSeconds);

            sb.Append(phases > 1 ? $"From {Fmt(from)} to {Fmt(to)}, " : "Throughout, ");
            sb.Append(speakers.Count switch
            {
                0 => "the talk",
                1 => $"{speakers[0]} did the talking and",
                _ => $"{Names(speakers)} were talking; {speakers[0]} led and the conversation",
            });
            sb.Append($" covered {Topics(part, 4)}.");
            foreach (var q in quotes)
                sb.Append($" {(q.Line.Speaker != null ? q.Line.Speaker.Label : "Someone")} said at {q.Line.Time}: “{Trim(q.Text)}”");
            var qs = part.Where(x => x.Text.EndsWith('?') && WordRx().Matches(x.Text).Count >= 4).ToList();
            if (qs.Count > 0) sb.Append($" {qs.Count} question{(qs.Count == 1 ? " was" : "s were")} raised in this part.");
            sb.AppendLine().AppendLine();
        }

        // 3. Participation
        if (s.Speakers.Count > 1)
        {
            sb.AppendLine("## Participation");
            sb.AppendLine(string.Join(" ", s.Speakers.Select(x =>
                $"{x.Name} spoke for {x.Talk:mm\\:ss} ({x.Share:P0}) over {x.Turns} turn{(x.Turns == 1 ? "" : "s")}, about {x.Words:N0} words.")));
            var dominant = s.Speakers[0].Share;
            sb.AppendLine(dominant > 0.65 ? "The conversation was largely one-sided."
                        : dominant < 0.45 ? "Talking time was fairly evenly shared." : "One person led, with others contributing regularly.");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();

        static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        static string Trim(string t) => t.Length > 220 ? t[..217].TrimEnd() + "…" : t;
    }

    /// <summary>
    /// The summary, with the report written by a local AI model (Ollama) when that is switched on in Settings.
    /// If the AI is off or unreachable, the offline report is kept and <see cref="ReportNote"/> says why.
    /// </summary>
    public static async Task<CallSummary> BuildAsync(TranscriptResult r, string title, DateTime? date, IProgress<string>? status, CancellationToken ct)
    {
        var s = Build(r, title, date);
        if (!AppSettings.Current.AiSummaryEnabled) return s;
        try
        {
            status?.Report($"Writing the report with {AiReport.ModelName} (local AI)…");
            var started = DateTime.Now;
            s.Report = await AiReport.WriteAsync(r, s, status, ct);
            s.ReportBy = "local AI · " + AiReport.ModelName;
            var (version, details) = await AiReport.DescribeAsync(ct);
            s.SetProvenance("Report written by", $"local large language model “{AiReport.ModelName}”" + (details != null ? $" ({details})" : ""));
            s.SetProvenance("AI runtime", "Ollama" + (version != null ? " " + version : "") + " at " + AiReport.ServerUrl);
            s.SetProvenance("AI processed on", AiReport.ServerLocation(AiReport.ServerUrl));
            s.SetProvenance("AI time taken", (DateTime.Now - started).ToString(@"m\:ss"));
            s.SetProvenance("Network", AiReport.ServerLocation(AiReport.ServerUrl).StartsWith("this PC")
                ? "local only (Ollama on this PC; nothing left the computer)"
                : $"transcript sent to {AiReport.ServerUrl} for the AI report");
            s.SetProvenance("Prompt", string.IsNullOrWhiteSpace(AppSettings.Current.AiPrompt) || AppSettings.Current.AiPrompt == AiReport.DefaultPrompt
                ? "built-in default" : "customised in Settings → Summary report");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            s.ReportNote = "Local AI was not available (" + ex.Message + "), so the offline report is shown.";
            s.SetProvenance("AI report", "requested but not available: " + ex.Message);
        }
        return s;
    }

    /// <summary>
    /// How this summary was produced — shown at the end of every format so readers can see exactly what processed the
    /// data, on which machine and for which Windows account.
    /// </summary>
    public List<(string Label, string Value)> Provenance { get; } = new();

    public void SetProvenance(string label, string value)
    {
        var i = Provenance.FindIndex(p => p.Label == label);
        if (i >= 0) Provenance[i] = (label, value); else Provenance.Add((label, value));
    }

    private void AddBaseProvenance(TranscriptResult r)
    {
        SetProvenance("Generated", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        SetProvenance("Software", $"WhisperDesk {AboutWindow.Version} by darkcipher0x");
        SetProvenance("Report written by", "WhisperDesk offline analysis (rule-based, no AI model)");
        SetProvenance("Computer", Environment.MachineName);
        SetProvenance("Windows account", $"{Environment.UserDomainName}\\{Environment.UserName}");
        SetProvenance("Operating system", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        if (r.Library?.Model is { Length: > 0 } model) SetProvenance("Transcribed with", $"Whisper {model} (whisper.cpp, on this PC)");
        SetProvenance("Source", r.Library != null ? $"library recording “{r.Library.Title}” ({r.Library.Id})" : System.IO.Path.GetFileName(r.SourceFile));
        SetProvenance("Network", "none (everything processed locally)");
    }

    /// <summary>Why the AI report wasn't used, if it was switched on.</summary>
    public string? ReportNote { get; set; }

    /// <summary>The report split into (heading or null, paragraph) blocks for Word/PDF.</summary>
    public IEnumerable<(bool IsHeading, string Text)> ReportBlocks()
    {
        foreach (var raw in Report.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim().Replace("**", "");
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) yield return (true, line.TrimStart('#', ' '));
            else yield return (false, line.StartsWith("* ") ? "•  " + line[2..] : line.StartsWith("- ") ? "•  " + line[2..] : line);
        }
    }

    public string DurationText => Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss");
    public string OverviewText =>
        $"{DurationText} · {Speakers.Count} speaker{(Speakers.Count == 1 ? "" : "s")} · {Words:N0} words" +
        (Edits > 0 ? $" · {Edits} edit{(Edits == 1 ? "" : "s")}" : "");

    public IEnumerable<(string Heading, List<SummaryItem> Items, string Empty)> Sections()
    {
        yield return ("Key points", KeyPoints, "—");
        yield return ("Action items", ActionItems, "No action items detected.");
        yield return ("Decisions", Decisions, "No decisions detected.");
        yield return ("Open questions", Questions, "No questions detected.");
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SUMMARY · {Title}").AppendLine($"{Date} · {OverviewText}").AppendLine();
        sb.AppendLine($"REPORT ({ReportBy})");
        foreach (var (isHeading, text) in ReportBlocks())
        {
            if (isHeading) sb.AppendLine().AppendLine(text.ToUpperInvariant());
            else sb.AppendLine(text);
        }
        sb.AppendLine();
        if (Speakers.Count > 0)
        {
            sb.AppendLine("WHO SPOKE");
            foreach (var s in Speakers)
                sb.AppendLine($"- {s.Name}{(s.Sex != Sexes.Unspecified ? $" ({s.Sex})" : "")}: {s.Talk:mm\\:ss} ({s.Share:P0}), {s.Turns} turns, {s.Words} words");
            sb.AppendLine();
        }
        foreach (var (heading, items, empty) in Sections())
        {
            sb.AppendLine(heading.ToUpperInvariant());
            if (items.Count == 0) sb.AppendLine($"  {empty}");
            foreach (var i in items) sb.AppendLine($"- {i.Display}");
            sb.AppendLine();
        }
        sb.AppendLine("HOW THIS SUMMARY WAS MADE");
        foreach (var (label, value) in Provenance) sb.AppendLine($"- {label}: {value}");
        sb.AppendLine().AppendLine("Detected and written automatically — check it against the transcript.");
        return sb.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        title = Title, date = Date, duration = DurationText, words = Words, edits = Edits, report = Report, reportBy = ReportBy,
        provenance = Provenance.ToDictionary(p => p.Label, p => p.Value),
        speakers = Speakers.Select(s => new { s.Name, s.Sex, talk = s.Talk.ToString(@"mm\:ss"), share = Math.Round(s.Share, 3), s.Turns, s.Words }),
        keyPoints = KeyPoints.Select(J), actionItems = ActionItems.Select(J), decisions = Decisions.Select(J), questions = Questions.Select(J),
        generator = $"WhisperDesk by darkcipher0x ({ReportBy})",
    }, new JsonSerializerOptions { WriteIndented = true });

    private static object J(SummaryItem i) => new { time = i.Time, speaker = i.Speaker, text = i.Text };
}
