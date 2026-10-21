using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhisperDesk.Addons;

namespace WhisperDesk.Services.Addons.Sandbox;

/// <summary>
/// Runs inside the AppContainer (WhisperDesk.exe --addon-host). Loads one add-on DLL in its own load context, gives it an
/// <see cref="IAddonHost"/> that is fed from the pipe, and sends its log lines back. It has no access to the library,
/// settings, voices or keys: it only ever sees what the app chose to send.
/// </summary>
public static class SandboxHost
{
    private static readonly object WriteLock = new();
    private static StreamWriter? _out;

    public static int Run()
    {
        _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        IWhisperDeskAddon? addon = null;
        ProxyHost? host = null;
        try
        {
            string? line;
            while ((line = input.ReadLine()) != null)
            {
                if (line.Length > SandboxProtocol.MaxLineChars) continue;
                JsonObject msg;
                try { msg = JsonNode.Parse(line)!.AsObject(); } catch { continue; }
                var t = (string?)msg["t"];
                try
                {
                    switch (t)
                    {
                        case "init":
                            var init = msg.Deserialize<SandboxProtocol.Init>(SandboxProtocol.Json)!;
                            addon = Load(init.Dll, init.Sha256);
                            host = new ProxyHost(init, Send);
                            Send(SandboxProtocol.Message("hello", new SandboxProtocol.Hello(addon.Id, addon.Name, addon.Description, addon.Author,
                                addon.Options.Select(o => new SandboxProtocol.OptionInfo(o.Key, o.Label, o.DefaultValue, o.Help, o.IsSecret)).ToList())));
                            break;
                        case "start": addon?.Start(host!); break;
                        case "stop": try { addon?.Stop(); } catch { } return 0;
                        case "options": host?.SetOptions(msg.Deserialize<SandboxProtocol.Options>(SandboxProtocol.Json)!.Values); break;
                        case "line": host?.RaiseLine(msg["e"].Deserialize<TranscriptLineEvent>(SandboxProtocol.Json)!); break;
                        case "started": host?.RaiseStarted(msg["e"].Deserialize<SessionEvent>(SandboxProtocol.Json)!); break;
                        case "stopped": host?.RaiseStopped(msg["e"].Deserialize<SessionEvent>(SandboxProtocol.Json)!); break;
                        case "saved": host?.RaiseSaved(msg["e"].Deserialize<TranscriptSavedEvent>(SandboxProtocol.Json)!); break;
                        case "edited": host?.RaiseEdited(msg["e"].Deserialize<LineEditedEvent>(SandboxProtocol.Json)!); break;
                    }
                }
                catch (Exception ex) when (t != "init")
                {
                    Send(SandboxProtocol.Message("log", new SandboxProtocol.Text($"error in {t}: {ex.Message}")));
                }
            }
            try { addon?.Stop(); } catch { }
            return 0;
        }
        catch (Exception ex)
        {
            Send(SandboxProtocol.Message("fatal", new SandboxProtocol.Text(ex.GetType().Name + ": " + ex.Message)));
            return 2;
        }
    }

    private static void Send(string json)
    {
        lock (WriteLock) { try { _out!.WriteLine(json); } catch { } }
    }

    /// <summary>Loads the add-on in an isolated load context; only the contract assembly is shared with the host.</summary>
    private static IWhisperDeskAddon Load(string dll, string? sha256)
    {
        // The bytes that are checked are the bytes that are loaded: no window to swap the file in between.
        var bytes = File.ReadAllBytes(dll);
        if (sha256 != null && !string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The add-on's DLL doesn't match the one that was approved.");
        var ctx = new AddonLoadContext(dll);
        var asm = ctx.LoadFromStream(new MemoryStream(bytes));
        var type = asm.GetExportedTypes().FirstOrDefault(t => typeof(IWhisperDeskAddon).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }
                                                               && t.GetConstructor(Type.EmptyTypes) != null)
                   ?? throw new InvalidDataException("The DLL contains no WhisperDesk add-on (a public class implementing IWhisperDeskAddon).");
        return (IWhisperDeskAddon)Activator.CreateInstance(type)!;
    }

    private sealed class AddonLoadContext(string dll) : AssemblyLoadContext("addon", isCollectible: false)
    {
        // The .deps.json resolver isn't available in a self-contained / single-file host; then the add-on's own folder is probed.
        private readonly AssemblyDependencyResolver? _resolver = TryResolver(dll);

        private static AssemblyDependencyResolver? TryResolver(string dll)
        {
            try { return new AssemblyDependencyResolver(dll); } catch { return null; }
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IWhisperDeskAddon).Assembly.GetName().Name) return null;   // the shared contract
            var path = _resolver?.ResolveAssemblyToPath(name);
            if (path == null)
            {
                var local = Path.Combine(Path.GetDirectoryName(dll)!, name.Name + ".dll");
                if (File.Exists(local)) path = local;
            }
            return path != null ? LoadFromAssemblyPath(path) : null;
        }
    }

    /// <summary>The add-on's view of WhisperDesk inside the sandbox.</summary>
    private sealed class ProxyHost : IAddonHost, IAddonHostPermissions
    {
        private readonly Action<string> _send;
        private Dictionary<string, string> _options;
        private readonly Dictionary<string, string> _defaults = new();

        public ProxyHost(SandboxProtocol.Init init, Action<string> send)
        {
            _send = send;
            _options = init.Options;
            AppVersion = init.Version;
            DataFolder = init.Data;
            ApprovedFolder = init.Folder;
            Capabilities = init.Caps;
        }

        public string AppVersion { get; }
        public string DataFolder { get; }
        public string? ApprovedFolder { get; }
        public IReadOnlyList<string> Capabilities { get; }

        public string GetOption(string key) => _options.TryGetValue(key, out var v) ? v : _defaults.TryGetValue(key, out var d) ? d : "";
        public void SetOptions(Dictionary<string, string> values) => _options = values;

        public void Log(string message) =>
            _send(SandboxProtocol.Message("log", new SandboxProtocol.Text(message.Length > SandboxProtocol.MaxLogChars ? message[..SandboxProtocol.MaxLogChars] : message)));

        public event EventHandler<TranscriptLineEvent>? LineAdded;
        public event EventHandler<SessionEvent>? SessionStarted;
        public event EventHandler<SessionEvent>? SessionStopped;
        public event EventHandler<TranscriptSavedEvent>? TranscriptSaved;
        public event EventHandler<LineEditedEvent>? LineEdited;

        public void RaiseLine(TranscriptLineEvent e) => LineAdded?.Invoke(this, e);
        public void RaiseStarted(SessionEvent e) => SessionStarted?.Invoke(this, e);
        public void RaiseStopped(SessionEvent e) => SessionStopped?.Invoke(this, e);
        public void RaiseSaved(TranscriptSavedEvent e) => TranscriptSaved?.Invoke(this, e);
        public void RaiseEdited(LineEditedEvent e) => LineEdited?.Invoke(this, e);
    }
}
