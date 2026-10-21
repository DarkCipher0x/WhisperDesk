# WhisperDesk add-on SDK

WhisperDesk (by darkcipher0x) can be extended with **add-ons**: small .NET libraries that receive transcript events —
live lines as they are spoken, session start/stop, and finished transcripts — and do something with them
(Word, a database, a home-automation system…).

WhisperDesk is local-first: users may keep very sensitive recordings in it. Add-ons therefore follow **least privilege**:
an add-on says what it needs, the user approves it (or less), and WhisperDesk gives it nothing else.

There are two ways to integrate:

| Approach | Best for | How |
|---|---|---|
| **No code** – built-in *Live captions for OBS & browsers* add-on | OBS overlays, web pages, scripts on this PC | Enable it in **Tools → Add-ons**, then use `http://localhost:7890/overlay`, `/events` (Server-Sent Events) or `/api/lines` (JSON) |
| **No code** – built-in *Webhook* add-on | Zapier, n8n, Home Assistant, your own server | Enable it and set a URL (https:// for internet addresses); WhisperDesk POSTs JSON |
| **No code** – built-in *Live transcript text file* | OBS *Text (GDI+) → Read from file*, tools that watch a file | Enable it and choose a file |
| **Code** – your own add-on | Anything else | Implement `IWhisperDeskAddon` and ship an `addon.json` (below) |

## Writing an add-on

1. Create a .NET 8 class library and reference `WhisperDesk.Addons.dll` (in this folder) with `Private=false`.
   See `SampleWordExport\SampleWordExport.csproj`.
2. Implement `IWhisperDeskAddon`:

```csharp
public sealed class MyAddon : IWhisperDeskAddon
{
    IAddonHost? _host;
    public string Id => "yourname.my-addon";          // unique and stable; must match addon.json
    public string Name => "My add-on";
    public string Description => "What it does.";
    public string Author => "Your name";
    public IReadOnlyList<AddonOption> Options { get; } = new[]
    {
        new AddonOption("target", "Where to send", "default value", "Help text shown under the box"),
        new AddonOption("apiKey", "API key", "", "Stored in Windows Credential Manager") { IsSecret = true },
    };

    public void Start(IAddonHost host)
    {
        _host = host;
        host.LineAdded += (s, e) => host.Log($"{e.Speaker}: {e.Text}");
    }

    public void Stop() { /* unsubscribe, close connections */ }
}
```

3. Write an **`addon.json`** next to the DLL. It is read *without loading your code*, shown to the user, and decides what
   your add-on receives:

```json
{
  "id": "yourname.my-addon",
  "name": "My add-on",
  "author": "Your name",
  "version": "1.0.0",
  "description": "What it does, in one or two sentences.",
  "entry": "MyAddon.dll",
  "capabilities": [ "transcript.live", "network", "credentials" ],
  "networkHosts": [ "api.example.com" ]
}
```

4. Put the DLL, its dependencies (**not** `WhisperDesk.Addons.dll`) and `addon.json` in **their own folder** under
   `%LOCALAPPDATA%\WhisperDesk\addons\` (**Tools → Add-ons → Open add-ons folder**), for example
   `addons\MyAddon\`. Restart WhisperDesk, open **Tools → Add-ons**, review the permissions and switch it on.

### Capabilities

| Capability | What the add-on gets |
|---|---|
| `transcript.live` | `LineAdded`, `SessionStarted`, `SessionStopped` (live sessions and file transcription) |
| `library.read` | `TranscriptSaved` (full text of finished transcripts) and `LineEdited` (corrections) |
| `network` | Internet and local-network access (Windows `internetClient` + `privateNetworkClientServer`) |
| `files` | Write access to **one folder the user picks** (`IAddonHostPermissions.ApprovedFolder`) |
| `credentials` | Its options marked `IsSecret` (kept in Windows Credential Manager, never in settings files) |
| `background` | Start automatically with WhisperDesk |
| `library.write` | Reserved; no API grants it yet |

Events for a capability that wasn't granted are simply never delivered. The user can untick any capability; design
your add-on to degrade gracefully. Cast the host to `IAddonHostPermissions` to see what was granted.

### The sandbox

Third-party add-ons never run inside WhisperDesk. Each runs in its own process (`WhisperDesk.exe --addon-host`) inside
a **Windows AppContainer**, the sandbox Windows uses for Store apps, plus a job object:

* no access to the user's files, the recordings library, settings, voiceprints or keys;
* write access only to `IAddonHost.DataFolder` (and the approved folder with `files`);
* no network unless `network` was granted;
* no child processes (it can't start other programs, including opening a document with `Process.Start`);
* at most 2 GB of memory; it is ended when WhisperDesk exits.

Events and options travel over a pipe as JSON. Log messages (`host.Log`) are shown in the Add-ons window, limited to
20 a second and 2,000 characters each.

**Approval is tied to your DLL's SHA-256.** When you ship an update, users are asked to approve it again.

An older add-on without `addon.json`, dropped straight into the add-ons folder, still works: it is offered only live
transcript lines and runs in the sandbox without network or file access.

### Events (`IAddonHost`)

| Event | When | Data |
|---|---|---|
| `LineAdded` | every new line (live: as spoken; files: when finished) | `TranscriptLineEvent` — `SessionId, IsLive, Timestamp, Start, End, Text, Speaker, Sex, Language` |
| `SessionStarted` / `SessionStopped` | a live session or a file starts/ends | `SessionEvent` — `SessionId, IsLive, Source, Timestamp` |
| `TranscriptSaved` | a file finished, a transcript was edited and saved, or a recording went into the library | `TranscriptSavedEvent` — `Title, Source, Folder, PlainText, Lines` |
| `LineEdited` | a user corrected a line (with a reason) | `LineEditedEvent` |

Other members: `GetOption(key)`, `Log(message)`, `DataFolder` (a private folder for your files) and `AppVersion`.

Events arrive on a **background thread**, one at a time, in order. Exceptions in your handlers are caught and written to
the add-on log, so a bug in an add-on cannot crash WhisperDesk.

### Treat everything as untrusted

Transcript text, titles and speaker names come from recordings and users: escape them for the format you write
(HTML, RTF, SQL, shell). Never build file paths from titles without removing path characters. Don't log transcript
text or secrets.

## Local caption server reference

* `GET /overlay` — transparent caption page. Parameters: `lines` (default 2), `size` (px, 42), `speaker` (1/0),
  `color` (hex, ffffff), `bg` (hex with alpha, 00000099), `font` (CSS font family), `align` (left/center/right).
* `GET /events` — `text/event-stream`. Events: `line` (JSON `TranscriptLineEvent`), `session` (new live session).
* `GET /api/lines` — JSON array of the current live session's lines.

The server listens on `localhost` only. Requests from web pages on other sites are refused (no CORS), and only
`localhost`/`127.0.0.1` host names are accepted, so a website open in your browser can't read the captions.
Programs on this PC that aren't browsers can still read them.
