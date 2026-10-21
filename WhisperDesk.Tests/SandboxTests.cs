using System.Collections.Concurrent;
using System.IO;
using System.Text.Json.Nodes;
using WhisperDesk.Addons;
using WhisperDesk.Services.Addons;
using WhisperDesk.Services.Addons.Sandbox;
using Xunit;
using Xunit.Abstractions;

namespace WhisperDesk.Tests;

/// <summary>
/// Runs the probe add-on in a real Windows AppContainer, exactly as WhisperDesk does, and checks what it could and
/// couldn't do. The probe only tries; nothing is sent anywhere when the sandbox works.
/// </summary>
[Collection("sandbox")]
public class SandboxTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>Leaves nothing behind on this PC: the test sandbox's folder access and its AppContainer profile.</summary>
    public void Dispose()
    {
        try
        {
            var sid = AppContainerProcess.ContainerSid("test.probe");
            AppContainerProcess.Revoke(sid, Path.GetDirectoryName(WhisperDeskExe())!);
        }
        catch { }
        AppContainerProcess.DeleteProfile("test.probe");
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "WhisperDesk.Tests.csproj"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(new[] { dir! }.Concat(parts).ToArray());
    }

    private static string ProbeDll()
    {
        var cfg = AppContext.BaseDirectory.Contains("\\Release\\") ? "Release" : "Debug";
        return RepoFile("ProbeAddon", "bin", cfg, "net8.0", "ProbeAddon.dll");
    }

    private static string WhisperDeskExe()
    {
        // WhisperDesk's own build output (self-contained: the runtime sits next to the exe), not the copy in the test folder.
        var cfg = AppContext.BaseDirectory.Contains("\\Release\\") ? "Release" : "Debug";
        return Path.GetFullPath(RepoFile("..", "WhisperDesk", "bin", cfg, "net8.0-windows10.0.19041.0", "win-x64", "WhisperDesk.exe"));
    }

    private List<string> Run(bool network, IEnumerable<string> caps, Dictionary<string, string> options, Action<AppContainerProcess>? afterStart = null, string? sha = null)
    {
        const string id = "test.probe";   // one fixed sandbox identity for the tests (no stray ACL entries per run)
        // The add-on lives in its own folder, like an installed one.
        var addonDir = Path.Combine(AddonManager.AddonsFolder, id + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(addonDir);
        var src = ProbeDll();
        Assert.True(File.Exists(src), "Build ProbeAddon first: " + src);
        File.Copy(src, Path.Combine(addonDir, "ProbeAddon.dll"), true);
        var data = AddonManager.DataFolderFor(id);

        var sid = AppContainerProcess.ContainerSid(id);
        var exe = WhisperDeskExe();
        AppContainerProcess.Grant(sid, Path.GetDirectoryName(exe)!, write: false);
        AppContainerProcess.Grant(sid, addonDir, write: false);
        AppContainerProcess.Grant(sid, data, write: true);

        var lines = new BlockingCollection<string>();
        using var proc = AppContainerProcess.Start(sid, network, data, exe);
        var reader = new Thread(() =>
        {
            try { string? l; while ((l = proc.Output.ReadLine()) != null) lines.Add(l); } catch { }
            lines.CompleteAdding();
        }) { IsBackground = true };
        reader.Start();
        try
        {
            proc.Input.WriteLine(SandboxProtocol.Message("init", new SandboxProtocol.Init(Path.Combine(addonDir, "ProbeAddon.dll"), data, null, "test", options, caps.ToList(), sha ?? AddonManager.Sha256(Path.Combine(addonDir, "ProbeAddon.dll")))));
            proc.Input.WriteLine(SandboxProtocol.Message("start"));
            afterStart?.Invoke(proc);
        }
        catch (IOException) { /* the process already exited: its output says why */ }

        var logs = new List<string>();
        var raw = new List<string>();
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until && lines.TryTake(out var line, TimeSpan.FromSeconds(60)))
        {
            output.WriteLine(line);
            if (!line.StartsWith('{')) { raw.Add(line); continue; }   // the .NET host's own error output
            var msg = JsonNode.Parse(line)!.AsObject();
            if ((string?)msg["t"] == "fatal") { if (sha != null) { logs.Add("FATAL " + msg["msg"]); break; } Assert.Fail("Sandbox host failed: " + msg["msg"]); }
            if ((string?)msg["t"] != "log") continue;
            logs.Add((string)msg["msg"]!);
            if (logs.Last() == "DONE" && afterStart == null) break;
            if (logs.Last().StartsWith("SAVED=")) break;
        }
        if (raw.Count > 0 && logs.Count == 0) Assert.Fail("The sandbox process did not start:\n" + string.Join("\n", raw));
        try { proc.Input.WriteLine(SandboxProtocol.Message("stop")); } catch (IOException) { }
        return logs;
    }

    [Fact]
    public void SandboxedAddonCantReadFilesNetworkOrStartPrograms()
    {
        // A file in the user's profile area that the add-on was not given.
        var secret = Path.Combine(TestEnvironment.Root, "not-for-addons.txt");
        File.WriteAllText(secret, "private");
        // Somewhere it must not write: the real user profile folder and the test folder outside its data folder.
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "wd-sandbox-probe-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        Environment.SetEnvironmentVariable("WD_TEST_SECRET", "s3cr3t-token");
        var logs = Run(network: false, caps: new[] { AddonCapabilities.TranscriptLive }, new() { ["secretFile"] = secret, ["writeTarget"] = target });
        Environment.SetEnvironmentVariable("WD_TEST_SECRET", null);
        Assert.False(File.Exists(target));

        Assert.Contains(logs, l => l.StartsWith("READ-DENIED"));
        Assert.Contains("WRITE-DATA-OK", logs);
        Assert.Contains(logs, l => l.StartsWith("WRITE-OUTSIDE-DENIED"));
        Assert.Contains(logs, l => l.StartsWith("PROC-DENIED"));
        Assert.Contains(logs, l => l.StartsWith("NET-DENIED"));
        Assert.Contains("TOKEN=absent", logs);
        Assert.Contains("ENV-SECRET=none", logs);   // WhisperDesk's own environment (here a planted fake secret) never reaches it
        Assert.Contains("CAPS=transcript.live", logs);
    }

    [Fact]
    public void AChangedDllIsNotLoaded()
    {
        var logs = Run(network: false, caps: new[] { AddonCapabilities.TranscriptLive }, new(), sha: new string('0', 64));
        Assert.Contains(logs, l => l.StartsWith("FATAL") && l.Contains("doesn't match"));
        Assert.DoesNotContain("DONE", logs);
    }

    [Fact]
    public void EventsArriveThroughThePipe()
    {
        var logs = Run(network: false, caps: new[] { AddonCapabilities.LibraryRead }, new(), proc =>
            proc.Input.WriteLine(SandboxProtocol.Message("saved", new
            {
                e = new TranscriptSavedEvent("s1", "Hearing notes", "File · x.mp3", null, "text", new List<TranscriptLineEvent>()),
            })));
        Assert.Contains("SAVED=Hearing notes", logs);
    }
}
