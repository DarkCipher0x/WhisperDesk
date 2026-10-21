using System.IO;
using System.Text.Json;

namespace WhisperDesk.Services.Addons;

/// <summary>
/// What an add-on may do. An add-on declares the capabilities it needs (addon.json, or in code for built-in add-ons); the
/// user sees them in plain language before enabling it, and WhisperDesk only gives it what was approved.
/// </summary>
public static class AddonCapabilities
{
    /// <summary>Receives transcript lines as they are made (live and file transcription) and session start/stop.</summary>
    public const string TranscriptLive = "transcript.live";
    /// <summary>Receives finished transcripts (full text) and line corrections from the library.</summary>
    public const string LibraryRead = "library.read";
    /// <summary>Reserved: change library content. No add-on API grants it yet.</summary>
    public const string LibraryWrite = "library.write";
    /// <summary>Connects to the internet or the local network.</summary>
    public const string Network = "network";
    /// <summary>Accepts connections from programs on this PC (localhost only).</summary>
    public const string LocalServer = "localserver";
    /// <summary>Writes files into one folder the user chooses.</summary>
    public const string Files = "files";
    /// <summary>Stores its own secrets (API keys, tokens) in Windows Credential Manager.</summary>
    public const string Credentials = "credentials";
    /// <summary>Starts automatically with WhisperDesk.</summary>
    public const string Background = "background";

    public static readonly string[] All = { TranscriptLive, LibraryRead, LibraryWrite, Network, LocalServer, Files, Credentials, Background };

    public static string Describe(string cap) => cap switch
    {
        TranscriptLive => "Read transcript lines as they are made (live sessions and file transcription)",
        LibraryRead => "Read finished transcripts from your library, including corrections",
        LibraryWrite => "Change recordings in your library",
        Network => "Connect to the internet or other computers (it can send anything it was given)",
        LocalServer => "Run a server that programs on this PC can connect to (localhost only)",
        Files => "Write files into a folder you choose",
        Credentials => "Keep its own passwords or API keys in Windows Credential Manager",
        Background => "Start automatically when WhisperDesk starts",
        _ => "Unknown permission: " + cap,
    };

    /// <summary>The combinations that can move your data off this PC.</summary>
    public static bool CanExfiltrate(IEnumerable<string> caps)
    {
        var set = caps.ToHashSet();
        return set.Contains(Network) && (set.Contains(TranscriptLive) || set.Contains(LibraryRead));
    }

    public static List<string> Normalise(IEnumerable<string>? caps) =>
        (caps ?? Enumerable.Empty<string>()).Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).Distinct().ToList();
}

/// <summary>The user's approval of an add-on. Bound to the DLL's SHA-256: a changed DLL needs approving again.</summary>
public sealed class AddonApproval
{
    public List<string> Capabilities { get; set; } = new();
    public string? Sha256 { get; set; }
    /// <summary>The folder approved for the "files" capability.</summary>
    public string? FilesFolder { get; set; }
    public DateTimeOffset ApprovedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>addon.json, next to a third-party add-on's DLL. Read without loading any of the add-on's code.</summary>
public sealed class AddonManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>The add-on DLL, relative to the manifest's folder.</summary>
    public string Entry { get; set; } = "";
    public List<string> Capabilities { get; set; } = new();
    /// <summary>Hosts it says it connects to (shown to the user; the sandbox can't restrict by host).</summary>
    public List<string> NetworkHosts { get; set; } = new();

    public static AddonManifest? TryRead(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 64 * 1024) return null;   // a manifest is small; refuse anything else
            var m = JsonSerializer.Deserialize<AddonManifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (m == null || string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Entry)) return null;
            if (m.Entry.Contains("..") || Path.IsPathRooted(m.Entry) || m.Entry.Contains(':')) return null;   // stays in its folder
            m.Capabilities = AddonCapabilities.Normalise(m.Capabilities);
            return m;
        }
        catch { return null; }
    }
}
