using System.Diagnostics;
using System.Net.Http;
using WhisperDesk.Addons;

namespace ProbeAddon;

/// <summary>Reports what the sandbox lets it do: read a file it wasn't given, write its data folder, use the network, start a program.</summary>
public sealed class Probe : IWhisperDeskAddon
{
    public string Id => "test.probe";
    public string Name => "Probe";
    public string Description => "Sandbox test";
    public string Author => "tests";
    public IReadOnlyList<AddonOption> Options { get; } = new[]
    {
        new AddonOption("secretFile", "File to try to read", ""),
        new AddonOption("writeTarget", "File to try to write", ""),
        new AddonOption("token", "Secret", "") { IsSecret = true },
    };

    public void Start(IAddonHost host)
    {
        Try(host, "READ", () => File.ReadAllText(host.GetOption("secretFile")));
        Try(host, "WRITE-DATA", () => File.WriteAllText(Path.Combine(host.DataFolder, "probe.txt"), "ok"));
        Try(host, "WRITE-OUTSIDE", () => File.WriteAllText(host.GetOption("writeTarget"), "x"));
        host.Log("PROFILE=" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Try(host, "PROC", () => { using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true }); p!.WaitForExit(); });
        Try(host, "NET", () => { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; h.GetAsync("https://example.com/").GetAwaiter().GetResult(); });
        host.Log("TOKEN=" + (host.GetOption("token").Length > 0 ? "present" : "absent"));
        host.Log("ENV-SECRET=" + (Environment.GetEnvironmentVariable("WD_TEST_SECRET") ?? "none"));
        host.Log("CAPS=" + string.Join(",", (host as IAddonHostPermissions)?.Capabilities ?? Array.Empty<string>()));
        host.TranscriptSaved += (_, e) => host.Log("SAVED=" + e.Title);
        host.LineAdded += (_, e) => host.Log("LINE=" + e.Text);
        host.Log("DONE");
    }

    private static void Try(IAddonHost host, string what, Action a)
    {
        try { a(); host.Log(what + "-OK"); }
        catch (Exception ex) { host.Log(what + "-DENIED " + ex.GetType().Name); }
    }

    public void Stop() { }
}
