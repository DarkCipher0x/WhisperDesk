using System.Text;
using WhisperDesk.Addons;

namespace SampleWordExport;

/// <summary>
/// Sample add-on: every time a transcript is finished or saved, write it as a Word document (.rtf opens in Word,
/// LibreOffice and WordPad) with bold speaker names and timestamps.
/// </summary>
public sealed class WordExportAddon : IWhisperDeskAddon
{
    private IAddonHost? _host;

    public string Id => "sample.word-export";
    public string Name => "Word export (sample)";
    public string Description => "Saves every finished transcript as a Word document with bold speaker names. " +
                                 "Source code is in the sdk\\SampleWordExport folder — use it as a starting point for your own add-ons.";
    public string Author => "darkcipher0x";

    // No folder option: the add-on writes only to the folder the user approved for its "files" permission (addon.json),
    // which is the only folder outside its own data folder that the sandbox lets it write to.
    public IReadOnlyList<AddonOption> Options { get; } = Array.Empty<AddonOption>();

    /// <summary>The approved folder, or the add-on's private data folder when "files" wasn't granted.</summary>
    private string Folder => (_host as IAddonHostPermissions)?.ApprovedFolder is { Length: > 0 } f ? f : _host!.DataFolder;

    public void Start(IAddonHost host)
    {
        _host = host;
        host.TranscriptSaved += OnSaved;
        host.Log($"Exporting to {Folder}");
        if (IsCloudSynced(Folder))
            host.Log("Warning: that folder is synced to the cloud (OneDrive/Dropbox/Google Drive/iCloud), so every export is uploaded. " +
                     "Choose a local folder if transcripts must stay on this PC.");
    }


    private static bool IsCloudSynced(string path)
    {
        try
        {
            var sep = Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path).TrimEnd(sep);
            // Inside a OneDrive sync root Windows reports (personal or work/school)?
            foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
                if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } root)
                {
                    var r = Path.GetFullPath(root).TrimEnd(sep);
                    if (full.Equals(r, StringComparison.OrdinalIgnoreCase) || full.StartsWith(r + sep, StringComparison.OrdinalIgnoreCase)) return true;
                }
            // Otherwise compare folder names along the path.
            var names = new[] { "OneDrive", "Dropbox", "Google Drive", "My Drive", "iCloudDrive", "iCloud Drive" };
            return full.Split(sep).Any(s => names.Any(n => s.Equals(n, StringComparison.OrdinalIgnoreCase))
                                            || s.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public void Stop()
    {
        if (_host != null) _host.TranscriptSaved -= OnSaved;
    }

    private void OnSaved(object? sender, TranscriptSavedEvent e)
    {
        var folder = Folder;
        Directory.CreateDirectory(folder);
        // Titles are untrusted text: no path characters, no leading dots, bounded length.
        var safe = string.Concat(e.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim().TrimStart('.');
        if (safe.Length == 0) safe = "Transcript";
        if (safe.Length > 80) safe = safe[..80];
        var path = Path.Combine(folder, safe + ".rtf");

        var rtf = new StringBuilder();
        rtf.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Calibri;}}\f0\fs22 ");
        rtf.Append(@"{\b\fs32 ").Append(Escape(e.Title)).Append(@"}\par ");
        rtf.Append(@"{\fs18 ").Append(Escape(e.Source)).Append(@"}\par\par ");
        foreach (var l in e.Lines)
        {
            rtf.Append(@"{\fs18 [").Append(l.Start.ToString(@"hh\:mm\:ss")).Append(@"]} ");
            if (l.Speaker != null) rtf.Append(@"{\b ").Append(Escape(l.Speaker)).Append(@":} ");
            rtf.Append(Escape(l.Text)).Append(@"\par ");
        }
        rtf.Append('}');
        File.WriteAllText(path, rtf.ToString(), Encoding.ASCII);
        _host!.Log($"Wrote {Path.GetFileName(path)}");
    }

    /// <summary>RTF-escapes text, including non-ASCII characters as \uN?.</summary>
    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c is '\\' or '{' or '}') sb.Append('\\').Append(c);
            else if (c > 127) sb.Append(@"\u").Append((short)c).Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
