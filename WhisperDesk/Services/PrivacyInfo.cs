using System.IO;
using System.Net;
using System.Text;

namespace WhisperDesk.Services;

/// <summary>The privacy &amp; security statement shown in Help → Privacy &amp; security (and summarised in the guide).</summary>
public static class PrivacyInfo
{
    private static string LocalData => Path.Combine(WhisperDesk.Services.AppPaths.LocalData);

    /// <summary>
    /// The cloud service that syncs <paramref name="path"/> (OneDrive, Dropbox, Google Drive, iCloud, Box), or null.
    /// WhisperDesk uploads nothing itself, but a synced folder means the sync client uploads its files.
    /// </summary>
    public static string? CloudSyncedBy(string path) => LibraryStorage.CloudSyncedBy(path);

    /// <summary>The library warning on its own (start-up banner, Settings → Storage &amp; tools), or null when the library is local.</summary>
    public static string? LibraryCloudWarning(string library) =>
        CloudSyncedBy(library) is { } cloud
            ? $"Your recordings library ({library}) is inside a {cloud} folder, so {cloud} may upload your recordings, transcripts and " +
              "session data to its cloud, according to how it is set up. That is outside WhisperDesk's local-only privacy boundary: WhisperDesk itself sends nothing, " +
              "but it can't control what the sync service does. " +
              (Security.Vault.IsEnabled
                  ? $"Your library is encrypted, so {cloud} only receives encrypted files, but it still keeps a copy and can see how many files there are, their sizes and when they change. "
                  : "") +
              "Move the library to a local folder to keep recordings on this PC."
            : null;

    /// <summary>A warning for each WhisperDesk data folder or output file that a cloud client syncs (empty when none).</summary>
    public static List<string> CloudWarnings()
    {
        var list = new List<string>();
        if (LibraryCloudWarning(RecordingLibrary.Root) is { } lib) list.Add(lib + " (Settings → Storage & tools → Move library to a local folder.)");
        if (CloudSyncedBy(ModelManager.ModelsDir) is { } m)
            list.Add($"Your models folder ({ModelManager.ModelsDir}) is inside a {m} folder. Models contain no personal data, but they are large to upload.");
        var s = AppSettings.Current;
        if (s.AddonsEnabled.TryGetValue(Addons.BuiltIn.LiveTextFileAddon.AddonId, out var on) && on
            && Addons.BuiltIn.LiveTextFileAddon.CurrentPath() is { } live && CloudSyncedBy(live) is { } c)
            list.Add($"The live transcript text file ({live}) is inside a {c} folder, so live captions are uploaded as you speak. " +
                     "Change its path in Tools → Add-ons.");
        if (CloudSyncedBy(s.OutputFolder ?? "") is { } o)
            list.Add($"The output folder for transcribed files ({s.OutputFolder}) is inside a {o} folder, so exported transcripts are uploaded.");
        return list;
    }

    /// <summary>(Heading, paragraphs) — kept in one place so the window, the plain-text fallback and docs agree.</summary>
    public static IReadOnlyList<(string Heading, string[] Body)> Sections() => new (string, string[])[]
    {
        ("In short", new[]
        {
            "WhisperDesk runs entirely on this computer. Audio, transcripts, voiceprints, summaries and settings are stored locally. " +
            "There is no account, no cloud service, no telemetry, no analytics and no crash reporting to anyone.",
            "Speech is turned into text, and voices are recognised, on this PC's own hardware: its NPU when it has one (Intel Core Ultra), otherwise its graphics card or processor. " +
            "The NPU is just another chip inside the computer; no audio is sent anywhere to be transcribed.",
        }.Concat(CloudWarnings().Select(w => "⚠ " + w)).ToArray()),
        ("What is stored, and where", new[]
        {
            $"Recordings library (audio, transcripts, summaries, edit history): {RecordingLibrary.Root}",
            $"Voice library (voiceprints and names): {Path.Combine(LocalData, "voices.json")}",
            $"Settings, error logs, live-session recovery files and the guide viewer cache: {LocalData}",
            $"Speech and speaker models: {ModelManager.ModelsDir}",
            "Transcripts you export are written only where you choose. Files made for the Windows share sheet go to a temporary folder that is cleared after a day.",
        }),
        ("When WhisperDesk uses the network", new[]
        {
            "Nothing at start-up, no telemetry, no analytics, no crash reports, no update or model checks. Every connection is listed in Settings → Security & privacy → Network activity, with the feature that made it.",
            "Downloading a model you select (asks first unless you chose otherwise): Whisper models from Hugging Face (on a PC with an NPU, also its NPU version); speaker and voice-activity models from Hugging Face and GitHub. Every file is pinned to a fixed version and checked against its SHA-256. Only the file request is visible to the server: your IP address and the program name. Nothing about you or your recordings is sent.",
            "Local AI report (off by default): the transcript is sent to the Ollama server at the address in Settings → Summary & AI. " +
            "The default is http://localhost:11434, which is this computer. Another computer only works after you allow it there, and internet addresses must use https://. The summary always records which model and server wrote it.",
            "Add-ons you enable: the built-in caption server only answers this PC, and only its own pages can read the transcript. The webhook add-on posts transcript lines to the URL you set (https:// for internet addresses). " +
            "Third-party add-ons run in a Windows sandbox with only the permissions you approved; one you allow network access can send what it receives.",
            "Windows share sheet: only when you press Share, and only to the app you pick. Text WhisperDesk copies is kept out of clipboard history and cloud clipboard sync.",
            PackageInfo.IsPackaged
                ? "The Microsoft Store installs and updates WhisperDesk; WhisperDesk itself never checks for updates."
                : "The installer only downloads FFmpeg or Ollama (with winget) if you tick those boxes.",
        }),
        ("Voiceprints are biometric data", new[]
        {
            "A voiceprint is a numeric summary of how someone sounds, used to recognise them in later recordings. In many places this counts as biometric data " +
            "(for example GDPR Article 9 in the UK/EU, BIPA in Illinois). Get people's consent before saving their voice, keep only what you need, " +
            "and delete voices you no longer need (Tools → Voice library → Forget). Voiceprints are never included in exported or shared files.",
        }),
        ("Recording people", new[]
        {
            "Laws on recording calls and conversations differ by country and state (one-party vs all-party consent). Tell participants when you record or transcribe them. " +
            "You are responsible for how you use recordings and transcripts.",
        }),
        ("Security", new[]
        {
            Security.Vault.IsEnabled
                ? "Encryption is on: the library, transcripts, voiceprints, forensic logs, live recordings and recovery files are encrypted with AES-256-GCM using a key made from your passphrase " +
                  "(PBKDF2-SHA256, 600,000 rounds). The key exists only in memory while WhisperDesk is unlocked; titles and folder names are hidden on disk. Without the passphrase or the recovery key, nobody (including darkcipher0x) can recover the data."
                : "Encryption is off: library files are ordinary files protected only by your Windows account. Turn encryption on in Settings → Security & privacy, keep the library on a BitLocker-encrypted drive, " +
                  "and use File → Backup with a password (AES-256) for copies you store elsewhere.",
            "Two-factor sign-in (an authenticator app) protects the app's lock screen; it does not strengthen the encryption itself. Windows Hello can be required in addition to the passphrase, which ties the key to this PC's security chip.",
            "Error logs never contain transcript text, passphrases or keys, and file names and paths in them are shortened to placeholders. Logs are deleted after 14 days.",
            "WhisperDesk can't protect against malware running under your Windows account while it is unlocked, weak passphrases, or files you export. No software is completely secure; these protections reduce risk, they don't remove it.",
            "Every text correction is logged with who, when, why and the original wording, and shown as “edited” in exports — useful when transcripts are evidence.",
            "Integrity: reports (Tools → Report on recordings, Tools → Voice & audio analysis) include SHA-256 fingerprints of the audio, transcript and every generated file, so you can show nothing was altered afterwards. Backups store a SHA-256 for every file and are checked on restore.",
            PackageInfo.IsPackaged
                ? "WhisperDesk needs no administrator rights to run. The Store package contains only the program, its Start menu entry and the .wdsession file association."
                : "WhisperDesk needs no administrator rights to run. The installer writes only to Program Files, the Start menu and the .wdsession file association.",
        }),
        ("Removing your data", new[]
        {
            PackageInfo.IsPackaged
                ? "Uninstalling removes the program. Your library, voice library, settings and models are kept, so a reinstall finds them; " +
                  "to remove them, delete the folders listed above."
                : "Uninstalling removes the program. Your library, voice library, settings and models are kept unless you tick them in the uninstaller " +
                  "(or delete the folders listed above).",
        }),
    };

    public static string PlainText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var (h, body) in Sections())
            {
                sb.AppendLine(h.ToUpperInvariant());
                foreach (var p in body) sb.AppendLine("• " + p);
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    public static string Html(bool dark)
    {
        string bg = dark ? "#050909" : "#F1F6F6", fg = dark ? "#D2F2EF" : "#08191A", muted = dark ? "#5A8986" : "#4E6E6C",
               accent = dark ? "#36D2CB" : "#127A75", line = dark ? "#16302F" : "#BCD2D1";
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html><html><head><meta charset="utf-8"><title>Privacy & security</title><style>
            body{background:{{bg}};color:{{fg}};font:15px/1.6 'Segoe UI',sans-serif;margin:0;padding:32px 40px;max-width:860px}
            h1{font:600 24px 'Cascadia Mono',Consolas,monospace;color:{{accent}};margin:0 0 4px}
            .sub{color:{{muted}};margin-bottom:26px}
            h2{font-size:16px;margin:26px 0 8px;padding-bottom:6px;border-bottom:1px solid {{line}}}
            li{margin:6px 0} ul{padding-left:20px} code{font-family:'Cascadia Mono',Consolas,monospace;font-size:13px}
            </style></head><body><h1>Privacy &amp; security</h1><div class="sub">WhisperDesk {{WebUtility.HtmlEncode(AboutWindow.Version)}} · by darkcipher0x</div>
            """);
        foreach (var (h, body) in Sections())
        {
            sb.Append("<h2>").Append(WebUtility.HtmlEncode(h)).Append("</h2><ul>");
            foreach (var p in body) sb.Append("<li>").Append(WebUtility.HtmlEncode(p)).Append("</li>");
            sb.Append("</ul>");
        }
        return sb.Append("</body></html>").ToString();
    }
}
