using System.IO;
using System.Text.RegularExpressions;

namespace WhisperDesk.Services.Security;

/// <summary>
/// Makes error-log text safe to keep and to share for support: file paths lose their folders and names (only the drive
/// type and extension stay), the user's profile and library become placeholders, URLs lose their path and query, and
/// long token-like strings are masked. Transcript text, passphrases and keys are never given to the logger in the first
/// place; this is the second line of defence for what exception messages carry (FFmpeg errors, file-not-found…).
/// </summary>
public static partial class LogRedactor
{
    public const int MaxEntry = 8_000;

    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\)[^\r\n""'<>|*?:]*", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"\b(?:https?|wss?)://[^\s'""<>]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"[A-Za-z0-9+_\-]{32,}={0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenLike();

    [GeneratedRegex(@"(?i)\b(password|passphrase|secret|token|apikey|api_key|authorization|bearer)\b\s*[:=]\s*\S+")]
    private static partial Regex KeyValueSecret();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;
        s = KeyValueSecret().Replace(s, m => m.Groups[1].Value + "=<redacted>");
        s = Url().Replace(s, m => Uri.TryCreate(m.Value, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}/…" : "<url>");
        s = WindowsPath().Replace(s, m => PathPlaceholder(m.Value));
        s = TokenLike().Replace(s, "<redacted>");
        if (s.Length > MaxEntry) s = s[..MaxEntry] + " …(truncated)";
        return s;
    }

    /// <summary>"&lt;library&gt;\…\transcript.json", "%USERPROFILE%\…\*.mp3", "C:\…\*.wav".</summary>
    private static string PathPlaceholder(string path)
    {
        var p = path.TrimEnd(' ', '.', ',', ';', ')');
        var tail = path[p.Length..];
        string root;
        if (Under(p, SafeLibraryRoot())) root = "<library>";
        else if (Under(p, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))) root = "%LOCALAPPDATA%";
        else if (Under(p, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))) root = "%USERPROFILE%";
        else if (Under(p, AppContext.BaseDirectory)) root = "<app>";
        else root = p.StartsWith(@"\\") ? @"\\<server>" : p[..2];
        var name = Path.GetFileName(p);
        // WhisperDesk's own fixed file names are harmless and useful; anything else becomes its extension only.
        var known = name is "metadata.json" or "transcript.json" or "transcript.txt" or "transcript.srt" or "settings.json" or "voices.json"
                         or "vault.json" or "forensics-log.jsonl" or "network-log.json" || name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase)
                         || name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);
        var shown = known ? name : Path.GetExtension(name) is { Length: > 0 and <= 6 } ext ? "*" + ext : "…";
        return root + @"\…\" + shown + tail;
    }

    private static string? SafeLibraryRoot()
    {
        try { return RecordingLibrary.Root; } catch { return null; }
    }

    private static bool Under(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var r = root.TrimEnd('\\');
        return path.Equals(r, StringComparison.OrdinalIgnoreCase) || path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Deletes error logs older than <paramref name="days"/> days.</summary>
    public static void PruneLogs(string dir, int days = 14)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "errors-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-days)) File.Delete(f);
        }
        catch { }
    }
}
