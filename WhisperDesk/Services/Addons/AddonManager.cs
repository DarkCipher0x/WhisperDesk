using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Channels;
using System.Windows;
using WhisperDesk.Addons;
using WhisperDesk.Services.Addons.Sandbox;

namespace WhisperDesk.Services.Addons;

public enum AddonKind { BuiltIn, External }

/// <summary>One setting of an add-on, as shown in the Add-ons window.</summary>
public sealed record AddonOptionInfo(string Key, string Label, string DefaultValue, string Help, bool Secret);

/// <summary>An add-on (built-in or third-party), its requested and approved permissions and its state.</summary>
public sealed class AddonEntry : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required AddonKind Kind { get; init; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    /// <summary>"Built-in", or the add-on's folder / DLL name.</summary>
    public string Origin { get; init; } = "";
    /// <summary>Built-in add-ons only (run inside WhisperDesk).</summary>
    public IWhisperDeskAddon? Instance { get; init; }
    /// <summary>Third-party add-ons only (run in the sandbox).</summary>
    public string? DllPath { get; init; }
    public bool HasManifest { get; init; }
    public List<string> NetworkHosts { get; init; } = new();
    public List<string> Requested { get; init; } = new();
    public List<AddonOptionInfo> Options { get; set; } = new();
    public bool Enabled { get; set; }
    public string Status { get; set; } = "";

    internal AddonHost? Host { get; set; }
    internal SandboxedAddon? Sandbox { get; set; }

    public AddonApproval? Approval => AppSettings.Current.AddonApprovals.TryGetValue(Id, out var a) ? a : null;
    public string Title => Name;
    public string Subtitle => Kind == AddonKind.BuiltIn ? $"{Author} · built-in" : $"{Author} · {Origin} · sandboxed";

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

/// <summary>
/// Finds, approves, starts and stops add-ons and delivers events to them through one background queue, so a slow or
/// failing add-on can never freeze the app.
/// <para>Least privilege: every add-on declares capabilities (built-in ones in code, third-party ones in addon.json). The
/// user approves them, possibly fewer, before it runs, and only events for granted capabilities are delivered.
/// Third-party DLLs are never loaded into WhisperDesk: they run in an AppContainer sandbox process, network only if
/// granted, file access only to their own data folder and an approved folder. An approval is bound to the DLL's SHA-256.</para>
/// </summary>
public sealed class AddonManager : IDisposable
{
    private readonly Channel<Action> _events = Channel.CreateUnbounded<Action>();
    private readonly Task _pump;

    public static string AddonsFolder => Path.Combine(AppPaths.LocalData, "addons");

    /// <summary>Each add-on's own folder; ids that need cleaning get a fingerprint suffix so two ids can never share one.</summary>
    public static string DataFolderFor(string id)
    {
        var clean = RecordingLibrary.Sanitize(id);
        if (clean != id) clean += "-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..8].ToLowerInvariant();
        return Path.Combine(AddonsFolder, "data", clean);
    }

    /// <summary>
    /// Why a folder can't be given to an add-on, or null. Never WhisperDesk's own data (settings, keys, library, add-ons,
    /// models) or the program folder, nor anything that contains them, nor a drive root: write access there would let an
    /// add-on change WhisperDesk's settings or other add-ons.
    /// </summary>
    public static string? FolderRefusal(string folder)
    {
        string Full(string p) => Path.GetFullPath(p).TrimEnd('\\') + "\\";
        var f = Full(folder);
        if (f.Length <= 4) return "a whole drive can't be given to an add-on";
        foreach (var protectedDir in new[] { AppPaths.LocalData, RecordingLibrary.Root, ModelManager.ModelsDir, AppContext.BaseDirectory,
                                             Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
        {
            var d = Full(protectedDir);
            if (f.StartsWith(d, StringComparison.OrdinalIgnoreCase) && !string.Equals(d, Full(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), StringComparison.OrdinalIgnoreCase))
                return "it is inside WhisperDesk's own data, the recordings library, the models or the program folder";
            if (d.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                return "it contains WhisperDesk's data, the library or your whole user folder";
        }
        return null;
    }

    public ObservableCollection<AddonEntry> Addons { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public string AppVersion { get; }

    /// <summary>What the built-in add-ons need (they are first-party code and run inside WhisperDesk).</summary>
    private static readonly Dictionary<string, string[]> BuiltInCapabilities = new()
    {
        ["whisperdesk.caption-server"] = new[] { AddonCapabilities.TranscriptLive, AddonCapabilities.LocalServer, AddonCapabilities.Background },
        [BuiltIn.LiveTextFileAddon.AddonId] = new[] { AddonCapabilities.TranscriptLive, AddonCapabilities.Files, AddonCapabilities.Background },
        [BuiltIn.WebhookAddon.AddonId] = new[] { AddonCapabilities.TranscriptLive, AddonCapabilities.LibraryRead, AddonCapabilities.Network, AddonCapabilities.Credentials, AddonCapabilities.Background },
    };

    public AddonManager(string appVersion)
    {
        AppVersion = appVersion;
        _pump = Task.Run(PumpAsync);
    }

    public static string Sha256(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    // ---------------- discovery ----------------

    /// <summary>Lists add-ons and starts the approved, enabled ones. No third-party code is loaded to list them.</summary>
    public void LoadAll()
    {
        Directory.CreateDirectory(AddonsFolder);
        foreach (var addon in new IWhisperDeskAddon[] { new BuiltIn.CaptionServerAddon(), new BuiltIn.LiveTextFileAddon(), new BuiltIn.WebhookAddon() })
        {
            var entry = new AddonEntry
            {
                Id = addon.Id, Kind = AddonKind.BuiltIn, Name = addon.Name, Description = addon.Description, Author = addon.Author,
                Origin = "Built-in", Instance = addon, Requested = BuiltInCapabilities[addon.Id].ToList(),
                Options = addon.Options.Select(o => new AddonOptionInfo(o.Key, o.Label, o.DefaultValue, o.Help, o.IsSecret)).ToList(),
            };
            MigrateBuiltIn(entry);
            Register(entry);
        }

        // Third-party: one folder per add-on with addon.json (preferred), or a bare DLL in the add-ons folder (older add-ons).
        foreach (var dir in Directory.EnumerateDirectories(AddonsFolder))
        {
            if (Path.GetFileName(dir).Equals("data", StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            var manifestPath = Path.Combine(dir, "addon.json");
            if (!File.Exists(manifestPath)) continue;
            if (AddonManifest.TryRead(manifestPath) is not { } m) { WriteLog($"Ignored {Path.GetFileName(dir)}: its addon.json is not valid."); continue; }
            var dll = Path.GetFullPath(Path.Combine(dir, m.Entry));
            if (!dll.StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || !File.Exists(dll))
            { WriteLog($"Ignored {m.Name}: its entry DLL was not found in its folder."); continue; }
            Register(new AddonEntry
            {
                Id = m.Id, Kind = AddonKind.External, Name = m.Name.Length > 0 ? m.Name : m.Id, Description = m.Description, Author = m.Author,
                Origin = Path.GetFileName(dir), DllPath = dll, HasManifest = true, Requested = m.Capabilities, NetworkHosts = m.NetworkHosts,
            });
        }
        foreach (var dll in Directory.EnumerateFiles(AddonsFolder, "*.dll"))
        {
            var name = Path.GetFileName(dll);
            if (name.Equals("WhisperDesk.Addons.dll", StringComparison.OrdinalIgnoreCase)) continue;
            Register(new AddonEntry
            {
                Id = "legacy:" + name.ToLowerInvariant(), Kind = AddonKind.External, Name = Path.GetFileNameWithoutExtension(name),
                Description = "An older add-on without an addon.json, so WhisperDesk can't tell what it needs. It is offered only live " +
                              "transcript lines, runs in the sandbox, and has no network or file access.",
                Author = "unknown", Origin = name, DllPath = dll, Requested = new() { AddonCapabilities.TranscriptLive },
            });
        }

        foreach (var entry in Addons.Where(a => a.Enabled).ToList())
        {
            if (!IsApproved(entry, out var why)) { entry.Enabled = false; entry.Status = why; WriteLog($"{entry.Name} was not started: {why}"); }
            // Only add-ons allowed to run in the background start by themselves; others wait to be switched on each time.
            else if (entry.Approval?.Capabilities.Contains(AddonCapabilities.Background) != true) { entry.Enabled = false; entry.Status = "off until you switch it on (not allowed to start by itself)"; }
            else StartAddon(entry);
        }
    }

    /// <summary>
    /// Built-in add-ons that earlier versions had switched on were consented to by switching them on: they keep running
    /// with their declared capabilities. The webhook URL (often carrying a token) moves out of settings.json into Credential Manager.
    /// </summary>
    private static void MigrateBuiltIn(AddonEntry entry)
    {
        var s = AppSettings.Current;
        if (s.AddonsEnabled.TryGetValue(entry.Id, out var on) && on && !s.AddonApprovals.ContainsKey(entry.Id))
            s.AddonApprovals[entry.Id] = new AddonApproval { Capabilities = entry.Requested.ToList() };
        if (s.AddonOptions.TryGetValue(entry.Id, out var opts))
            foreach (var secret in entry.Options.Where(o => o.Secret))
                if (opts.TryGetValue(secret.Key, out var value) && Security.SecretStore.CanStore)
                {
                    if (!string.IsNullOrWhiteSpace(value)) Security.SecretStore.Set(SecretScope(entry.Id), secret.Key, value);
                    opts.Remove(secret.Key);
                    s.Save();
                }
    }

    public static string SecretScope(string id) => "addon:" + id;

    private void Register(AddonEntry entry)
    {
        if (Addons.Any(a => a.Id == entry.Id)) { WriteLog($"Skipped duplicate add-on id “{entry.Id}” from {entry.Origin}."); return; }
        entry.Enabled = AppSettings.Current.AddonsEnabled.TryGetValue(entry.Id, out var on) && on;
        Addons.Add(entry);
    }

    // ---------------- approval ----------------

    /// <summary>True when the add-on has an approval that still matches its DLL.</summary>
    public static bool IsApproved(AddonEntry entry, out string why)
    {
        why = "";
        if (entry.Approval is not { } a) { why = "not approved yet"; return false; }
        if (entry.Kind == AddonKind.External)
        {
            if (entry.DllPath == null || !File.Exists(entry.DllPath)) { why = "its DLL is missing"; return false; }
            if (!string.Equals(Sha256(entry.DllPath), a.Sha256, StringComparison.OrdinalIgnoreCase))
            { why = "its DLL changed since you approved it: review its permissions again"; return false; }
        }
        return true;
    }

    /// <summary>Stores the user's approval (capabilities possibly reduced) and, for third-party add-ons, the DLL fingerprint.</summary>
    public void Approve(AddonEntry entry, IEnumerable<string> granted, string? folder)
    {
        var caps = AddonCapabilities.Normalise(granted).Where(c => entry.Requested.Contains(c)).ToList();
        AppSettings.Current.AddonApprovals[entry.Id] = new AddonApproval
        {
            Capabilities = caps, FilesFolder = caps.Contains(AddonCapabilities.Files) ? folder : null,
            Sha256 = entry.Kind == AddonKind.External && entry.DllPath != null ? Sha256(entry.DllPath) : null,
        };
        AppSettings.Current.Save();
        WriteLog($"Approved {entry.Name}: {(caps.Count > 0 ? string.Join(", ", caps) : "no permissions")}.");
        entry.Changed();
    }

    /// <summary>Withdraws the approval and stops the add-on; the sandbox loses its folder access.</summary>
    public void Revoke(AddonEntry entry)
    {
        SetEnabled(entry, false);
        if (AppSettings.Current.AddonApprovals.Remove(entry.Id, out var old) && entry.Kind == AddonKind.External)
        {
            try
            {
                var sid = AppContainerProcess.ContainerSid(entry.Id);
                if (old.FilesFolder != null) AppContainerProcess.Revoke(sid, old.FilesFolder);
                AppContainerProcess.Revoke(sid, AppContext.BaseDirectory);
                AppContainerProcess.DeleteProfile(entry.Id);
            }
            catch { }
        }
        Security.SecretStore.DeleteScope(SecretScope(entry.Id));
        AppSettings.Current.Save();
        WriteLog($"Removed the permissions of {entry.Name}.");
        entry.Changed();
    }

    public void SetEnabled(AddonEntry entry, bool enabled)
    {
        if (enabled && !IsApproved(entry, out var why)) throw new InvalidOperationException($"{entry.Name} can't be switched on: {why}.");
        if (enabled == entry.Enabled && enabled == (entry.Host != null || entry.Sandbox != null)) return;
        entry.Enabled = enabled;
        AppSettings.Current.AddonsEnabled[entry.Id] = enabled;
        AppSettings.Current.Save();
        if (enabled) StartAddon(entry); else StopAddon(entry);
        entry.Changed();
    }

    /// <summary>Restarts an enabled add-on so it picks up changed options.</summary>
    public void Restart(AddonEntry entry)
    {
        if (!entry.Enabled) return;
        StopAddon(entry);
        StartAddon(entry);
    }

    // ---------------- running ----------------

    /// <summary>Option values for an add-on: plain ones from settings, secret ones from Credential Manager (only with "credentials").</summary>
    internal static Dictionary<string, string> OptionValues(AddonEntry entry)
    {
        var values = new Dictionary<string, string>();
        AppSettings.Current.AddonOptions.TryGetValue(entry.Id, out var saved);
        bool secrets = entry.Approval?.Capabilities.Contains(AddonCapabilities.Credentials) == true;
        foreach (var o in entry.Options)
        {
            if (o.Secret) { if (secrets && Security.SecretStore.Get(SecretScope(entry.Id), o.Key) is { } v) values[o.Key] = v; }
            else values[o.Key] = saved != null && saved.TryGetValue(o.Key, out var v) ? v : o.DefaultValue;
        }
        if (saved != null && entry.Kind == AddonKind.External)
            foreach (var (k, v) in saved) values.TryAdd(k, v);   // options known before the add-on reported its list
        return values;
    }

    private void StartAddon(AddonEntry entry)
    {
        try
        {
            if (entry.Kind == AddonKind.BuiltIn)
            {
                var host = new AddonHost(this, entry);
                entry.Instance!.Start(host);
                entry.Host = host;
            }
            else
            {
                var box = SandboxedAddon.Start(entry, entry.Approval!, OptionValues(entry), AppVersion);
                box.Hello += h => OnUi(() =>
                {
                    // The add-on's own description of itself is shown as reported, never used for decisions.
                    if (!entry.HasManifest) { entry.Name = Clip(h.Name, 80); entry.Description = Clip(h.Description, 1000); entry.Author = Clip(h.Author, 80); }
                    entry.Options = h.Options.Take(50).Select(o => new AddonOptionInfo(Clip(o.Key, 64), Clip(o.Label, 120), Clip(o.DefaultValue, 2000), Clip(o.Help, 500), o.Secret)).ToList();
                    entry.Changed();
                    // Options it declared secret are delivered now that we know them (only with "credentials").
                    box.Send(SandboxProtocol.Message("options", new SandboxProtocol.Options(OptionValues(entry))));
                    box.Send(SandboxProtocol.Message("start"));
                });
                box.Log += msg => WriteLog($"{entry.Name}: {msg}");
                box.Exited += reason => OnUi(() =>
                {
                    if (entry.Sandbox != box) return;
                    entry.Sandbox = null;
                    entry.Status = reason != null ? "stopped: " + reason : "stopped";
                    WriteLog($"{entry.Name} stopped{(reason != null ? ": " + reason : "")}.");
                    entry.Changed();
                });
                entry.Sandbox = box;
            }
            entry.Status = entry.Kind == AddonKind.BuiltIn ? "running" : "running in the sandbox";
            WriteLog($"Started {entry.Name}{(entry.Kind == AddonKind.External ? " (sandboxed)" : "")}.");
        }
        catch (Exception ex)
        {
            entry.Enabled = false;
            entry.Status = "failed to start: " + ex.Message;
            WriteLog($"{entry.Name} failed to start: {ex.Message}");
        }
        entry.Changed();
    }

    private static string Clip(string? s, int max) => s == null ? "" : s.Length > max ? s[..max] : s;

    private void StopAddon(AddonEntry entry)
    {
        if (entry.Host != null)
        {
            try { entry.Instance!.Stop(); } catch (Exception ex) { WriteLog($"{entry.Name} error while stopping: {ex.Message}"); }
            entry.Host = null;
        }
        if (entry.Sandbox is { } box) { entry.Sandbox = null; box.Stop(); }
        entry.Status = "off";
        WriteLog($"Stopped {entry.Name}.");
        entry.Changed();
    }

    // ----- Publishing (called by the app) -----

    public void PublishLine(TranscriptLineEvent e) => Post(AddonCapabilities.TranscriptLive, h => h.RaiseLine(e), "line", e);
    public void PublishSessionStarted(SessionEvent e) => Post(AddonCapabilities.TranscriptLive, h => h.RaiseStarted(e), "started", e);
    public void PublishSessionStopped(SessionEvent e) => Post(AddonCapabilities.TranscriptLive, h => h.RaiseStopped(e), "stopped", e);
    public void PublishSaved(TranscriptSavedEvent e) => Post(AddonCapabilities.LibraryRead, h => h.RaiseSaved(e), "saved", e);
    public void PublishLineEdited(LineEditedEvent e) => Post(AddonCapabilities.LibraryRead, h => h.RaiseLineEdited(e), "edited", e);

    /// <summary>Queues an event for every running add-on that was granted <paramref name="capability"/>.</summary>
    private void Post(string capability, Action<AddonHost> raise, string type, object payload)
    {
        // While WhisperDesk is locked, nothing is handed to add-ons (a running live session keeps recording, but its lines
        // don't leave the app until it is unlocked).
        if (Security.AppLock.IsLocked) return;
        var targets = Addons.Where(a => (a.Host != null || a.Sandbox != null) && a.Approval?.Capabilities.Contains(capability) == true).ToList();
        if (targets.Count == 0) return;
        _events.Writer.TryWrite(() =>
        {
            foreach (var a in targets)
            {
                try
                {
                    if (a.Host is { } h) raise(h);
                    else a.Sandbox?.SendEvent(type, payload);
                }
                catch (Exception ex) { WriteLog($"{a.Name}: {ex.Message}"); }
            }
        });
    }

    private async Task PumpAsync()
    {
        await foreach (var work in _events.Reader.ReadAllAsync()) work();
    }

    private static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    internal void WriteLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Log.Add(line);
            while (Log.Count > 300) Log.RemoveAt(0);
        });
    }

    public void Dispose()
    {
        foreach (var e in Addons) { try { StopAddon(e); } catch { } }
        _events.Writer.TryComplete();
    }

    // ----- Helpers for building events from the app's own types -----

    public static TranscriptLineEvent ToEvent(string sessionId, bool live, TranscriptLine l) =>
        new(sessionId, live, DateTime.Now, l.Start, l.End, l.Text, l.Speaker?.Label, l.Speaker?.Sex, l.Language)
        { IsEdited = l.IsEdited, OriginalText = l.IsEdited ? l.OriginalText : null };

    public static TranscriptSavedEvent ToSavedEvent(string sessionId, string title, string source, string? folder, TranscriptResult r) =>
        new(sessionId, title, source, folder, r.Render(), r.Lines.Select(l => ToEvent(sessionId, false, l)).ToList());
}

/// <summary>The <see cref="IAddonHost"/> handed to one built-in add-on (in-process, first-party).</summary>
internal sealed class AddonHost : IAddonHost, IAddonHostPermissions
{
    private readonly AddonManager _manager;
    private readonly AddonEntry _entry;

    public AddonHost(AddonManager manager, AddonEntry entry)
    {
        _manager = manager;
        _entry = entry;
        DataFolder = AddonManager.DataFolderFor(entry.Id);
        Directory.CreateDirectory(DataFolder);
    }

    public string AddonName => _entry.Name;
    public string AppVersion => _manager.AppVersion;
    public string DataFolder { get; }
    public IReadOnlyList<string> Capabilities => _entry.Approval?.Capabilities ?? new List<string>();
    public string? ApprovedFolder => _entry.Approval?.FilesFolder;

    public string GetOption(string key) => AddonManager.OptionValues(_entry).TryGetValue(key, out var v) ? v : "";

    public void Log(string message) => _manager.WriteLog($"{_entry.Name}: {message}");

    public event EventHandler<TranscriptLineEvent>? LineAdded;
    public event EventHandler<SessionEvent>? SessionStarted;
    public event EventHandler<SessionEvent>? SessionStopped;
    public event EventHandler<TranscriptSavedEvent>? TranscriptSaved;
    public event EventHandler<LineEditedEvent>? LineEdited;

    internal void RaiseLine(TranscriptLineEvent e) => LineAdded?.Invoke(this, e);
    internal void RaiseStarted(SessionEvent e) => SessionStarted?.Invoke(this, e);
    internal void RaiseStopped(SessionEvent e) => SessionStopped?.Invoke(this, e);
    internal void RaiseSaved(TranscriptSavedEvent e) => TranscriptSaved?.Invoke(this, e);
    internal void RaiseLineEdited(LineEditedEvent e) => LineEdited?.Invoke(this, e);
}
