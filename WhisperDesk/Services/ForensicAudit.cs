using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WhisperDesk.Services;

/// <summary>One entry in a recording's forensic audit log.</summary>
public sealed class AuditEntry
{
    public int Seq { get; set; }
    public DateTimeOffset At { get; set; }
    public string User { get; set; } = "";
    public string Computer { get; set; } = "";
    public string App { get; set; } = "";
    /// <summary>"Opened", "Measured", "Identified", "Note", "Exported", "Integrity check"…</summary>
    public string Action { get; set; } = "";
    /// <summary>Selection start/end in seconds (null = whole recording).</summary>
    public double? From { get; set; }
    public double? To { get; set; }
    public string Summary { get; set; } = "";
    public Dictionary<string, string> Details { get; set; } = new();
    /// <summary>SHA-256 of the media file when the entry was written: proves which audio was examined.</summary>
    public string MediaSha256 { get; set; } = "";
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";

    public string AtText => At.ToString("yyyy-MM-dd HH:mm:ss zzz");
    public string RangeText => From is { } f && To is { } t ? $"{Fmt(f)} – {Fmt(t)}" : "whole";
    private static string Fmt(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss\.f");
}

/// <summary>
/// Append-only, hash-chained audit log of every forensic action on a recording (forensics-log.jsonl next to the
/// recording). Each entry includes the SHA-256 of the previous entry, so changing, reordering or deleting any line
/// breaks the chain and <see cref="Verify"/> reports where.
/// </summary>
public sealed class ForensicAudit
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object _lock = new();

    public string LogPath { get; }
    public string MediaPath { get; }
    public string MediaSha256 { get; private set; } = "";
    public List<AuditEntry> Entries { get; } = new();

    public ForensicAudit(RecordingMeta? meta, string mediaPath)
    {
        MediaPath = mediaPath;
        string dir;
        if (meta != null && Directory.Exists(meta.Dir)) dir = meta.Dir;
        else
        {
            // Not in the library (e.g. an imported session): keep the log under the app data, keyed by the media path.
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(mediaPath).ToLowerInvariant())))[..16];
            dir = Path.Combine(AboutWindow.DataDir, "forensics", key);
            Directory.CreateDirectory(dir);
        }
        LogPath = Path.Combine(dir, "forensics-log.jsonl");
        // Encrypted with the library when encryption is on (the log names who examined what, and when).
        if (Security.SecureFile.Exists(LogPath))
            foreach (var line in Security.SecureFile.ReadAllLines(LogPath).Where(l => l.Trim().Length > 0))
                try { Entries.Add(JsonSerializer.Deserialize<AuditEntry>(line)!); } catch { Entries.Add(new AuditEntry { Action = "UNREADABLE LINE", Summary = line }); }
    }

    /// <summary>Hashes the media (once per session) so each entry records exactly which audio was examined.</summary>
    public async Task HashMediaAsync() => MediaSha256 = await Task.Run(() => BulkReport.Sha256(MediaPath));

    public AuditEntry Add(string action, string summary, double? from = null, double? to = null, Dictionary<string, string>? details = null)
    {
        lock (_lock)
        {
            var e = new AuditEntry
            {
                Seq = Entries.Count + 1, At = DateTimeOffset.Now, User = $"{Environment.UserDomainName}\\{Environment.UserName}",
                Computer = Environment.MachineName, App = $"WhisperDesk {AboutWindow.Version}", Action = action, Summary = summary,
                From = from, To = to, Details = details ?? new(), MediaSha256 = MediaSha256,
                PrevHash = Entries.Count > 0 ? Entries[^1].Hash : new string('0', 64),
            };
            e.Hash = Compute(e);
            Security.SecureFile.AppendAllText(LogPath, JsonSerializer.Serialize(e, Json) + "\n");
            Entries.Add(e);
            return e;
        }
    }

    private static string Compute(AuditEntry e)
    {
        var copy = JsonSerializer.Serialize(new
        {
            e.Seq, At = e.At.ToString("O"), e.User, e.Computer, e.App, e.Action, e.From, e.To, e.Summary,
            Details = e.Details.OrderBy(k => k.Key, StringComparer.Ordinal).ToList(), e.MediaSha256, e.PrevHash,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(copy))).ToLowerInvariant();
    }

    /// <summary>Checks every entry's hash and link. Returns (ok, message).</summary>
    public (bool Ok, string Message) Verify()
    {
        var prev = new string('0', 64);
        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            if (e.Seq != i + 1) return (false, $"Entry {i + 1}: sequence number is {e.Seq} (entries were removed or reordered).");
            if (e.PrevHash != prev) return (false, $"Entry {e.Seq}: link to the previous entry is broken (an earlier entry was changed or deleted).");
            if (Compute(e) != e.Hash) return (false, $"Entry {e.Seq} ({e.Action}, {e.AtText}) was modified after it was written.");
            prev = e.Hash;
        }
        return (true, Entries.Count == 0 ? "The log is empty." : $"All {Entries.Count} entries verified: the chain is intact (last hash {prev[..16]}…).");
    }
}
