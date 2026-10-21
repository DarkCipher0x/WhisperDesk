using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhisperDesk.Services.Addons.Sandbox;

/// <summary>WhisperDesk's side of one sandboxed add-on: starts it, feeds it granted events, reads its logs.</summary>
internal sealed class SandboxedAddon : IDisposable
{
    private readonly AppContainerProcess _proc;
    private readonly object _sendLock = new();
    private volatile bool _closed;

    public event Action<SandboxProtocol.Hello>? Hello;
    public event Action<string>? Log;
    public event Action<string?>? Exited;

    private SandboxedAddon(AppContainerProcess proc) => _proc = proc;

    /// <summary>Starts the sandbox for an approved add-on. Throws if the DLL changed since it was approved.</summary>
    public static SandboxedAddon Start(AddonEntry entry, AddonApproval approval, Dictionary<string, string> options, string appVersion)
    {
        var dll = entry.DllPath ?? throw new InvalidOperationException("No add-on DLL.");
        if (!string.Equals(AddonManager.Sha256(dll), approval.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The add-on's DLL has changed since you approved it. Review its permissions again.");

        var sid = AppContainerProcess.ContainerSid(entry.Id);
        // What the sandbox may touch: read its own folder, write its data folder (and the approved folder). Nothing else.
        try { AppContainerProcess.Grant(sid, AppContext.BaseDirectory, write: false); } catch { /* Program Files: already readable by sandboxes */ }
        AppContainerProcess.Grant(sid, Path.GetDirectoryName(dll)!, write: false);
        var data = AddonManager.DataFolderFor(entry.Id);
        AppContainerProcess.Grant(sid, data, write: true);
        string? folder = null;
        if (approval.Capabilities.Contains(AddonCapabilities.Files) && !string.IsNullOrWhiteSpace(approval.FilesFolder))
        {
            // Checked again here: settings.json could have been edited to point the approval somewhere sensitive.
            if (AddonManager.FolderRefusal(approval.FilesFolder) is { } why) throw new InvalidOperationException($"The approved folder can't be used: {why}.");
            folder = approval.FilesFolder;
            AppContainerProcess.Grant(sid, folder, write: true);
        }

        var proc = AppContainerProcess.Start(sid, approval.Capabilities.Contains(AddonCapabilities.Network), data);
        var addon = new SandboxedAddon(proc);
        var reader = new Thread(addon.ReadLoop) { IsBackground = true, Name = "add-on " + entry.Id };
        reader.Start();
        addon.Send(SandboxProtocol.Message("init", new SandboxProtocol.Init(dll, data, folder, appVersion, options, approval.Capabilities, approval.Sha256)));
        return addon;
    }

    public void Send(string json)
    {
        if (_closed) return;
        lock (_sendLock)
        {
            try { _proc.Input.WriteLine(json); }
            catch (IOException) { _closed = true; }
            catch (ObjectDisposedException) { _closed = true; }
        }
    }

    public void SendEvent(string type, object e) => Send(SandboxProtocol.Message(type, new { e }));

    private void ReadLoop()
    {
        string? reason = null;
        try
        {
            string? line;
            int logs = 0;
            var window = DateTime.UtcNow;
            while ((line = ReadBoundedLine(_proc.Output, 1 << 20)) != null)
            {
                if (line.Length == 0) continue;
                JsonObject msg;
                try { msg = JsonNode.Parse(line)!.AsObject(); } catch { continue; }
                switch ((string?)msg["t"])
                {
                    case "hello":
                        var hello = msg.Deserialize<SandboxProtocol.Hello>(SandboxProtocol.Json);
                        if (hello != null) Hello?.Invoke(hello);
                        break;
                    case "log":
                        // At most 20 log lines a second, so a noisy add-on can't flood the window.
                        if ((DateTime.UtcNow - window).TotalSeconds >= 1) { window = DateTime.UtcNow; logs = 0; }
                        if (++logs <= 20) Log?.Invoke(Clip((string?)msg["msg"]));
                        break;
                    case "fatal":
                        reason = Clip((string?)msg["msg"]);
                        break;
                }
            }
        }
        catch { }
        _closed = true;
        Exited?.Invoke(reason);
    }

    /// <summary>
    /// One line from the add-on, at most <paramref name="max"/> characters: a longer line is read to its end and dropped
    /// (returned empty), so a misbehaving add-on can't make WhisperDesk allocate unbounded memory. Null at end of stream.
    /// </summary>
    private static string? ReadBoundedLine(StreamReader reader, int max)
    {
        var sb = new System.Text.StringBuilder();
        bool tooLong = false;
        while (true)
        {
            int c = reader.Read();
            if (c < 0) return sb.Length == 0 && !tooLong ? null : tooLong ? "" : sb.ToString();
            if (c == '\n') return tooLong ? "" : sb.ToString().TrimEnd('\r');
            if (sb.Length < max) sb.Append((char)c); else tooLong = true;
        }
    }

    private static string Clip(string? s) => s == null ? "" : s.Length > SandboxProtocol.MaxLogChars ? s[..SandboxProtocol.MaxLogChars] : s;

    public void Stop()
    {
        Send(SandboxProtocol.Message("stop"));
        _closed = true;
        var proc = _proc;
        // Give it a moment to finish (e.g. close a file), then the job object ends it.
        Task.Delay(1500).ContinueWith(_ => { proc.Kill(); proc.Dispose(); });
    }

    public void Dispose()
    {
        _closed = true;
        _proc.Kill();
        _proc.Dispose();
    }
}
