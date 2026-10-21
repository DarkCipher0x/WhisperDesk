using System.IO;
using System.Text;
using WhisperDesk.Addons;

namespace WhisperDesk.Services.Addons.BuiltIn;

/// <summary>
/// Keeps a plain text file up to date with the live transcript. Works with anything that can watch a file:
/// OBS "Text (GDI+) → Read from file", stream widgets, Word/Notepad++ auto-reload, other tools.
/// </summary>
public sealed class LiveTextFileAddon : IWhisperDeskAddon
{
    private IAddonHost? _host;
    private readonly List<string> _recent = new();

    public const string AddonId = "whisperdesk.live-text-file";
    public string Id => AddonId;

    /// <summary>Default file: in Documents, or in %LOCALAPPDATA%\WhisperDesk when Documents is synced to the cloud.</summary>
    public static string DefaultPath => Path.Combine(LibraryStorage.LocalDocumentsFolder(), "WhisperDesk live captions.txt");

    /// <summary>The path the add-on writes to (the user's setting or the default).</summary>
    public static string CurrentPath() =>
        AppSettings.Current.AddonOptions.TryGetValue(AddonId, out var o) && o.TryGetValue("path", out var p) && !string.IsNullOrWhiteSpace(p) ? p : DefaultPath;
    public string Name => "Live transcript text file";
    public string Description =>
        "Writes live lines to a text file as they happen. Mode \"latest\" keeps only the last few lines (great for OBS Text source " +
        "→ Read from file); mode \"append\" keeps the whole session log.";
    public string Author => "darkcipher0x";

    public IReadOnlyList<AddonOption> Options { get; } = new[]
    {
        new AddonOption("path", "File path", DefaultPath),
        new AddonOption("mode", "Mode (latest / append)", "latest"),
        new AddonOption("lines", "Lines to keep in latest mode", "2"),
        new AddonOption("speaker", "Include speaker name (true / false)", "true"),
    };

    public void Start(IAddonHost host)
    {
        _host = host;
        host.LineAdded += OnLine;
        host.SessionStarted += OnSessionStarted;
        host.Log($"Writing to {host.GetOption("path")}");
        if (LibraryStorage.CloudSyncedBy(host.GetOption("path")) is { } cloud)
            host.Log($"Warning: that file is inside a {cloud} folder, so {cloud} uploads the live transcript as you speak. " +
                     "Choose a local path if captions must stay on this PC.");
    }

    public void Stop()
    {
        if (_host == null) return;
        _host.LineAdded -= OnLine;
        _host.SessionStarted -= OnSessionStarted;
    }

    private void OnSessionStarted(object? sender, SessionEvent e)
    {
        if (!e.IsLive || _host == null) return;
        _recent.Clear();
        var path = _host.GetOption("path");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, _host.GetOption("mode") == "append" ? $"# Live session {e.Timestamp:yyyy-MM-dd HH:mm}{Environment.NewLine}" : "",
                          new UTF8Encoding(false));
    }

    private void OnLine(object? sender, TranscriptLineEvent e)
    {
        if (!e.IsLive || _host == null) return;
        var who = _host.GetOption("speaker") != "false" && e.Speaker != null ? e.Speaker + ": " : "";
        var path = _host.GetOption("path");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (_host.GetOption("mode") == "append")
        {
            File.AppendAllText(path, $"[{e.Start:hh\\:mm\\:ss}] {who}{e.Text}{Environment.NewLine}", new UTF8Encoding(false));
        }
        else
        {
            _recent.Add(who + e.Text);
            var keep = int.TryParse(_host.GetOption("lines"), out var n) ? Math.Max(1, n) : 2;
            while (_recent.Count > keep) _recent.RemoveAt(0);
            // Write to a temp file and swap, so readers never see a half-written file.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, string.Join(Environment.NewLine, _recent), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
