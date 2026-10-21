namespace WhisperDesk.Addons;

/// <summary>
/// Implement this in a .NET 8 class library. Put the DLL and an addon.json manifest (id, name, author, entry, capabilities)
/// in a folder under %LOCALAPPDATA%\WhisperDesk\addons\ (Tools → Add-ons → Open add-ons folder). WhisperDesk shows the
/// capabilities to the user and, once approved, runs the add-on in a separate sandboxed process (a Windows AppContainer)
/// that only receives the events and access it was granted. See sdk\README.md.
/// </summary>
public interface IWhisperDeskAddon
{
    /// <summary>Unique, stable id, e.g. "yourname.word-export".</summary>
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string Author { get; }

    /// <summary>Settings the user can edit in the Add-ons window. Values are read with <see cref="IAddonHost.GetOption"/>.</summary>
    IReadOnlyList<AddonOption> Options { get; }

    /// <summary>Called when the add-on is enabled (or on startup if it was left enabled). Subscribe to host events here.</summary>
    void Start(IAddonHost host);

    /// <summary>Called when the add-on is disabled or the app closes. Unsubscribe and release resources.</summary>
    void Stop();
}

/// <summary>One user-editable setting shown in the Add-ons window.</summary>
public sealed record AddonOption(string Key, string Label, string DefaultValue, string Help = "")
{
    /// <summary>
    /// A password, API key, token or URL containing one. Stored in Windows Credential Manager (never in settings files,
    /// backups or logs), shown masked, and passed to the add-on only if it was granted the "credentials" capability.
    /// </summary>
    public bool IsSecret { get; init; }
}

/// <summary>
/// Optional extra view of the host (cast <see cref="IAddonHost"/> to it): what the user approved for this add-on.
/// </summary>
public interface IAddonHostPermissions
{
    /// <summary>The capabilities granted, e.g. "transcript.live", "network", "files".</summary>
    IReadOnlyList<string> Capabilities { get; }

    /// <summary>The folder the user approved for the "files" capability (the only place outside <see cref="IAddonHost.DataFolder"/>
    /// the add-on can write), or null.</summary>
    string? ApprovedFolder { get; }
}

/// <summary>What WhisperDesk offers to add-ons. Events are raised on a background thread.</summary>
public interface IAddonHost
{
    string AppVersion { get; }

    /// <summary>A folder the add-on may use for its own files (the only folder a sandboxed add-on can always write to).</summary>
    string DataFolder { get; }

    string GetOption(string key);

    /// <summary>Writes a line to the Add-ons log (shown in the Add-ons window).</summary>
    void Log(string message);

    /// <summary>A new transcript line (live sessions and file transcription).</summary>
    event EventHandler<TranscriptLineEvent>? LineAdded;

    event EventHandler<SessionEvent>? SessionStarted;
    event EventHandler<SessionEvent>? SessionStopped;

    /// <summary>A complete transcript was written (file finished, speakers renamed, recording saved to the library).</summary>
    event EventHandler<TranscriptSavedEvent>? TranscriptSaved;

    /// <summary>A user corrected a line's text (with a required reason). Useful for audit trails.</summary>
    event EventHandler<LineEditedEvent>? LineEdited;
}

/// <summary>An audited correction of one line: who, when, why, before and after.</summary>
public sealed record LineEditedEvent(
    string SessionId,
    TimeSpan LineStart,
    string? Speaker,
    string OldText,
    string NewText,
    string Reason,
    string User,
    DateTimeOffset At);

public sealed record TranscriptLineEvent(
    string SessionId,
    bool IsLive,
    DateTime Timestamp,
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string? Speaker,
    string? Sex,
    string? Language)
{
    /// <summary>True when the text was corrected by a user (see <see cref="IAddonHost.LineEdited"/>).</summary>
    public bool IsEdited { get; init; }

    /// <summary>The text as originally transcribed, when edited.</summary>
    public string? OriginalText { get; init; }
}

public sealed record SessionEvent(string SessionId, bool IsLive, string Source, DateTime Timestamp);

public sealed record TranscriptSavedEvent(
    string SessionId,
    string Title,
    string Source,
    string? Folder,
    string PlainText,
    IReadOnlyList<TranscriptLineEvent> Lines);
