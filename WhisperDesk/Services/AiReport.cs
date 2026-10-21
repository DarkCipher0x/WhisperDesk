using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace WhisperDesk.Services;

/// <summary>
/// Writes the summary report with a local AI model through Ollama (https://ollama.com) running on this PC.
/// The prompt is editable in File → Settings → Summary. Nothing is sent off the machine unless the user points the
/// address at another computer.
/// </summary>
public static class AiReport
{
    public const string DefaultUrl = "http://localhost:11434";
    public const string DefaultModel = "llama3.1:8b";

    public const string DefaultPrompt =
"""
You are writing a detailed report of a recorded conversation for someone who was not there.
Title: {title}
Date: {date}
Length: {duration}
Speakers: {speakers}

Write the report in plain text using these sections, each starting with a line "## <heading>":
## Overview — one paragraph: what this conversation was, who took part, its purpose and the outcome.
## What was said — several paragraphs walking through the conversation in order. Explain the context of each part,
what each person said or argued, and how the discussion moved on. Quote short key phrases with the speaker's name and the [time].
## Decisions and agreements
## Action items — who will do what, and by when if it was said.
## Open questions and concerns
## Tone and dynamics — one short paragraph on how the conversation went.

Only use what is in the transcript; if something is unclear, say so. Do not invent names, facts or numbers.

Transcript ([time] Speaker: text):
{transcript}
""";

    /// <summary>Transcripts longer than this are condensed in chunks first so they fit the model's context.</summary>
    private const int MaxChars = 40_000;

    private static readonly HttpClient LocalHttp = Security.NetworkGate.Create(Security.NetFeature.LocalAi, TimeSpan.FromMinutes(20));
    private static readonly HttpClient RemoteHttp = Security.NetworkGate.Create(Security.NetFeature.RemoteAi, TimeSpan.FromMinutes(20));

    /// <summary>
    /// The client for the configured server: this PC is "Local AI"; anything else is "AI server on another computer", which
    /// needs Settings → Summary &amp; AI → "Allow sending transcripts to another computer" (off by default).
    /// </summary>
    private static HttpClient Http => Uri.TryCreate(ServerUrl, UriKind.Absolute, out var u) && Security.NetworkGate.IsLoopback(u.Host) ? LocalHttp : RemoteHttp;

    /// <summary>True when the configured AI server is not this PC.</summary>
    public static bool IsRemote => !(Uri.TryCreate(ServerUrl, UriKind.Absolute, out var u) && Security.NetworkGate.IsLoopback(u.Host));

    public static async Task<string> WriteAsync(TranscriptResult r, CallSummary s, IProgress<string>? status, CancellationToken ct)
    {
        var settings = AppSettings.Current;
        var transcript = string.Join("\n", r.Lines.Where(l => l.Text.Trim().Length > 0)
            .Select(l => $"[{l.Time}] {(l.Speaker?.Label ?? "Unknown")}: {l.Text.Trim()}"));

        // Long recordings: summarise each part first (keeping times and names), then write the report from those notes.
        if (transcript.Length > MaxChars)
        {
            var chunks = Chunk(transcript, MaxChars);
            var notes = new StringBuilder();
            for (int i = 0; i < chunks.Count; i++)
            {
                status?.Report($"Local AI: reading part {i + 1} of {chunks.Count}…");
                notes.AppendLine(await GenerateAsync(
                    "Make detailed notes of this part of a conversation transcript, in order. Keep the [time] and speaker name for each point, " +
                    "keep short quotes of key phrases, decisions, action items and questions. Do not invent anything.\n\n" + chunks[i], ct));
                notes.AppendLine();
            }
            transcript = "(Condensed notes of a long recording, in order)\n" + notes;
            status?.Report("Local AI: writing the report…");
        }

        var prompt = (string.IsNullOrWhiteSpace(settings.AiPrompt) ? DefaultPrompt : settings.AiPrompt)
            .Replace("{title}", s.Title).Replace("{date}", s.Date).Replace("{duration}", s.DurationText)
            .Replace("{speakers}", s.Speakers.Count > 0 ? string.Join(", ", s.Speakers.Select(x => x.Name)) : "not identified")
            .Replace("{transcript}", transcript);
        if (!prompt.Contains(transcript)) prompt += "\n\nTranscript:\n" + transcript;   // prompt without {transcript}

        var text = (await GenerateAsync(prompt, ct)).Trim();
        if (text.Length == 0) throw new InvalidOperationException("the model returned an empty answer");
        return text;
    }

    private static List<string> Chunk(string text, int size)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (sb.Length + line.Length > size && sb.Length > 0) { chunks.Add(sb.ToString()); sb.Clear(); }
            sb.AppendLine(line);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }

    /// <summary>Sends any prompt to the configured local model (used by multi-recording reports).</summary>
    public static Task<string> AskAsync(string prompt, CancellationToken ct) => GenerateAsync(prompt, ct);

    private static async Task<string> GenerateAsync(string prompt, CancellationToken ct)
    {
        var s = AppSettings.Current;
        var url = (string.IsNullOrWhiteSpace(s.AiUrl) ? DefaultUrl : s.AiUrl.Trim()).TrimEnd('/') + "/api/generate";
        var body = new
        {
            model = string.IsNullOrWhiteSpace(s.AiModel) ? DefaultModel : s.AiModel.Trim(),
            prompt,
            stream = false,
            options = new { num_ctx = 16384, temperature = 0.2 },
        };
        HttpResponseMessage resp;
        try { resp = await Http.PostAsJsonAsync(url, body, ct); }
        catch (HttpRequestException) { throw new InvalidOperationException($"Ollama isn't running at {url.Replace("/api/generate", "")}"); }
        catch (Security.NetworkBlockedException ex) { throw new InvalidOperationException(ex.Message); }
        using (resp)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = TryGet(json, "error") ?? resp.ReasonPhrase;
                throw new InvalidOperationException(err?.Contains("not found") == true
                    ? $"model “{body.model}” isn't installed — run: ollama pull {body.model}" : $"Ollama error: {err}");
            }
            return TryGet(json, "response") ?? "";
        }
    }

    public static string ServerUrl =>
        (string.IsNullOrWhiteSpace(AppSettings.Current.AiUrl) ? DefaultUrl : AppSettings.Current.AiUrl.Trim()).TrimEnd('/');

    public static string ModelName =>
        string.IsNullOrWhiteSpace(AppSettings.Current.AiModel) ? DefaultModel : AppSettings.Current.AiModel.Trim();

    /// <summary>"this PC (localhost)" or "another computer: host" — where the transcript is processed.</summary>
    public static string ServerLocation(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            bool local = host is "localhost" or "127.0.0.1" or "::1" or "[::1]" || host.StartsWith("127.")
                         || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
            return local ? $"this PC ({host})" : $"another computer: {host}, the transcript was sent there";
        }
        catch { return url; }
    }

    /// <summary>Ollama version and the model's details (family, size, quantisation, digest) for the provenance record.</summary>
    public static async Task<(string? Version, string? Details)> DescribeAsync(CancellationToken ct)
    {
        string? version = null, details = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            version = TryGet(await Http.GetStringAsync(ServerUrl + "/api/version", cts.Token), "version");
            using var resp = await Http.PostAsJsonAsync(ServerUrl + "/api/show", new { model = ModelName }, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                var parts = new List<string>();
                if (doc.RootElement.TryGetProperty("details", out var d))
                {
                    foreach (var key in new[] { "family", "parameter_size", "quantization_level", "format" })
                        if (d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) parts.Add(s);
                }
                if (doc.RootElement.TryGetProperty("modified_at", out var m) && m.ValueKind == JsonValueKind.String) parts.Add("updated " + m.GetString()?[..10]);
                details = parts.Count > 0 ? string.Join(" · ", parts) : null;
            }
        }
        catch { }
        return (version, details);
    }

    /// <summary>Quick connection test for the Settings window.</summary>
    public static async Task<string> TestAsync(CancellationToken ct)
    {
        var reply = await GenerateAsync("Reply with exactly: OK", ct);
        return reply.Trim();
    }

    private static string? TryGet(string json, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(prop, out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }
}
