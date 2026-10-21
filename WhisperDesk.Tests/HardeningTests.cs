using System.IO;
using System.IO.Compression;
using System.Text;
using WhisperDesk.Services;
using WhisperDesk.Services.Addons;
using WhisperDesk.Services.Addons.BuiltIn;
using WhisperDesk.Services.Security;
using Xunit;

namespace WhisperDesk.Tests;

public class NetworkPolicyTests
{
    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.4.5.6", true)]
    [InlineData("[::1]", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("example.com", false)]
    [InlineData("localhost.evil.com", false)]
    public void Loopback(string host, bool expected) => Assert.Equal(expected, NetworkGate.IsLoopback(host));

    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("172.32.1.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("nas.local", true)]
    [InlineData("example.com", false)]
    public void PrivateNetwork(string host, bool expected) => Assert.Equal(expected, NetworkGate.IsPrivateNetwork(host));

    [Fact]
    public void PlainHttpToTheInternetIsRefused()
    {
        Assert.NotNull(NetworkGate.Refusal(NetFeature.Webhook, new Uri("http://hooks.example.com/x")));
        Assert.Null(NetworkGate.Refusal(NetFeature.Webhook, new Uri("https://hooks.example.com/x")));
        Assert.Null(NetworkGate.Refusal(NetFeature.Webhook, new Uri("http://192.168.1.20:8123/api")));
        Assert.NotNull(NetworkGate.Refusal(NetFeature.Webhook, new Uri("file:///C:/Windows/win.ini")));
    }

    [Fact]
    public void ModelDownloadsNeedConsentAndCanBeBlocked()
    {
        var s = AppSettings.Current;
        var before = s.ModelDownloadPolicy;
        try
        {
            s.ModelDownloadPolicy = "Ask";
            Assert.NotNull(NetworkGate.Refusal(NetFeature.ModelDownload, new Uri("https://unapproved.example.org/model.bin")));
            s.ModelDownloadPolicy = "Block";
            Assert.NotNull(NetworkGate.Refusal(NetFeature.ModelDownload, new Uri("https://huggingface.co/x")));
            s.ModelDownloadPolicy = "Allow";
            Assert.Null(NetworkGate.Refusal(NetFeature.ModelDownload, new Uri("https://huggingface.co/x")));
        }
        finally { s.ModelDownloadPolicy = before; }
    }

    [Fact]
    public void TranscriptsGoToAnotherComputerOnlyWhenAllowed()
    {
        var s = AppSettings.Current;
        Assert.NotNull(NetworkGate.Refusal(NetFeature.LocalAi, new Uri("http://192.168.1.5:11434/api/generate")));
        Assert.Null(NetworkGate.Refusal(NetFeature.LocalAi, new Uri("http://localhost:11434/api/generate")));
        s.AllowRemoteAi = false;
        Assert.NotNull(NetworkGate.Refusal(NetFeature.RemoteAi, new Uri("http://192.168.1.5:11434/api/generate")));
        s.AllowRemoteAi = true;
        Assert.Null(NetworkGate.Refusal(NetFeature.RemoteAi, new Uri("http://192.168.1.5:11434/api/generate")));
        Assert.NotNull(NetworkGate.Refusal(NetFeature.RemoteAi, new Uri("http://ai.example.com/api/generate")));   // internet: https only
        s.AllowRemoteAi = false;
    }

    [Fact]
    public async Task BlockedRequestNeverLeavesAndIsLogged()
    {
        using var http = NetworkGate.Create(NetFeature.Webhook, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<NetworkBlockedException>(() => http.GetAsync("http://plain-http.example.com/secret?token=abc"));
        var last = NetworkGate.Activity.Last();
        Assert.Equal("plain-http.example.com", last.Host);
        Assert.StartsWith("blocked", last.Note);
        Assert.DoesNotContain("token", last.Path + last.Note);
    }
}

public class CaptionServerTests
{
    [Theory]
    [InlineData("localhost:7890", null, true)]                                 // OBS / scripts (no Origin)
    [InlineData("localhost:7890", "http://localhost:7890", true)]              // the overlay page itself
    [InlineData("127.0.0.1:7890", "http://127.0.0.1:7890", true)]
    [InlineData("localhost:7890", "https://evil.example", false)]              // another website in the browser
    [InlineData("localhost:7890", "http://localhost:3000", false)]             // another local web app
    [InlineData("localhost:7890", "null", false)]                              // sandboxed iframe / file page
    [InlineData("evil.example:7890", null, false)]                             // DNS rebinding
    [InlineData(null, null, false)]
    public void OnlyItsOwnPagesCanReadTheTranscript(string? host, string? origin, bool allowed) =>
        Assert.Equal(allowed, CaptionServerAddon.IsAllowedRequest(host, origin, 7890));

    [Theory]
    [InlineData("7890", 7890)]
    [InlineData("8080", 8080)]
    [InlineData("7890@evil.example", 7890)]
    [InlineData("80", 7890)]
    [InlineData("-1", 7890)]
    [InlineData("99999", 7890)]
    [InlineData(null, 7890)]
    public void PortCantSmuggleAUrl(string? value, int expected) => Assert.Equal(expected, CaptionServerAddon.ParsePort(value));
}

public class UntrustedInputTests
{
    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1", "_COM1")]
    [InlineData("..", "Untitled")]
    [InlineData("../../Windows", "-..-Windows")]
    [InlineData("a/b\\c", "a-b-c")]
    [InlineData("  .hidden", "hidden")]
    [InlineData("Meeting: 1/2", "Meeting- 1-2")]
    public void TitlesBecomeSafeNames(string input, string expected) => Assert.Equal(expected, RecordingLibrary.Sanitize(input));

    [Fact]
    public void AddOnsCantBeGivenWhisperDesksOwnFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotNull(AddonManager.FolderRefusal(AppPaths.LocalData));                                   // settings, vault, add-ons
        Assert.NotNull(AddonManager.FolderRefusal(Path.GetDirectoryName(AppPaths.LocalData)!));          // contains them
        Assert.NotNull(AddonManager.FolderRefusal(RecordingLibrary.Root));
        Assert.NotNull(AddonManager.FolderRefusal(Path.Combine(RecordingLibrary.Root, "sub")));
        Assert.NotNull(AddonManager.FolderRefusal(profile));
        Assert.NotNull(AddonManager.FolderRefusal(Path.GetPathRoot(profile)!));
        Assert.NotNull(AddonManager.FolderRefusal(AppContext.BaseDirectory));
        Assert.Null(AddonManager.FolderRefusal(Path.Combine(profile, "Documents", "Word exports")));
    }

    [Fact]
    public void AddOnDataFoldersNeverCollide()
    {
        Assert.NotEqual(AddonManager.DataFolderFor("a/b"), AddonManager.DataFolderFor("a-b"));
        Assert.NotEqual(AddonManager.DataFolderFor("x:y"), AddonManager.DataFolderFor("x?y"));
        Assert.Equal(Path.Combine(AddonManager.AddonsFolder, "data", "whisperdesk.webhook"), AddonManager.DataFolderFor("whisperdesk.webhook"));
        Assert.StartsWith(Path.Combine(AddonManager.AddonsFolder, "data"), AddonManager.DataFolderFor("../../evil"));
    }

    [Fact]
    public void ManifestEntryCantLeaveItsFolder()
    {
        var dir = Path.Combine(TestEnvironment.Root, "manifest-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "addon.json");
        foreach (var entry in new[] { "..\\\\evil.dll", "C:\\\\Windows\\\\evil.dll", "sub/../../evil.dll" })
        {
            File.WriteAllText(path, $"{{\"id\":\"x\",\"entry\":\"{entry}\"}}");
            Assert.Null(AddonManifest.TryRead(path));
        }
        File.WriteAllText(path, "{\"id\":\"x\",\"entry\":\"x.dll\",\"capabilities\":[\" Network \",\"library.read\"]}");
        Assert.Equal(new[] { "network", "library.read" }, AddonManifest.TryRead(path)!.Capabilities);
    }

    [Fact]
    public void SessionFileCantWriteOutsideOrSmuggleFiles()
    {
        var file = Path.Combine(TestEnvironment.Root, "evil-" + Guid.NewGuid().ToString("N")[..6] + ".wdsession");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            void Add(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
            Add("manifest.json", "{\"format\":\"whisperdesk-session\"}");
            Add("metadata.json", "{\"Title\":\"T\",\"ExternalMedia\":\"C:\\\\Users\\\\victim\\\\secret.wav\",\"MediaFile\":\"..\\\\..\\\\x.wav\"}");
            Add("transcript.json", "[]");
            Add("../../escape.txt", "x");
            Add("payload.exe", "MZ");
        }
        var (meta, _) = SessionPackage.Open(file);
        try
        {
            Assert.Null(meta.ExternalMedia);
            Assert.Null(meta.MediaFile);
            Assert.False(File.Exists(Path.Combine(meta.Dir, "payload.exe")));
            Assert.False(File.Exists(Path.Combine(TestEnvironment.Root, "escape.txt")));
            Assert.StartsWith(Path.GetFullPath(WorkDir.Root), Path.GetFullPath(meta.Dir));
        }
        finally { WorkDir.Release(meta.Dir); }
    }

    [Fact]
    public void ExportedHtmlCantBreakOutOfItsScript()
    {
        var safe = SessionPackage.ScriptSafe("{\"title\":\"</script><script>alert(1)</script><!--\"}");
        Assert.DoesNotContain("<", safe);
        Assert.DoesNotContain(">", safe);
    }

    [Fact]
    public async Task LibraryMoveIgnoresJunctions()
    {
        var src = Path.Combine(TestEnvironment.Root, "move-src-" + Guid.NewGuid().ToString("N")[..6]);
        var outside = Path.Combine(TestEnvironment.Root, "outside-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(src, "rec"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(src, "rec", "metadata.json"), "{}");
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "do not touch");
        // A directory junction (no admin rights needed) pointing outside the library.
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(src, "link")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false }))
            p!.WaitForExit();
        Assert.True(Directory.Exists(Path.Combine(src, "link", "")) && File.Exists(Path.Combine(src, "link", "precious.txt")));
        var files = Directory.EnumerateFiles(src, "*", LibraryStorage.NoLinks).ToList();
        Assert.DoesNotContain(files, f => f.EndsWith("precious.txt"));
        // Moving the library copies and deletes only real library files, never through the junction.
        var target = src + "-moved";
        await LibraryStorage.MoveAsync(src, target, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
        Assert.True(File.Exists(Path.Combine(target, "rec", "metadata.json")));
        Assert.False(File.Exists(Path.Combine(target, "link", "precious.txt")));
    }
}

public class LogRedactionTests
{
    [Fact]
    public void PathsUrlsAndTokensAreRedacted()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = $"Could not find file '{profile}\\Cases\\Smith v Jones\\call 2024-03-01.mp3'. " +
                   $"FFmpeg: D:\\Evidence\\Jane Roe interview.wav: Invalid data. " +
                   "POST https://hooks.example.com/services/T000/B000/XXXXSECRETXXXX?token=abc failed. " +
                   "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789ABCDEF " +
                   "key=sk_live_1234567890abcdefghijklmnopqrstuv";
        var r = LogRedactor.Redact(text);
        foreach (var leak in new[] { "Smith", "Jones", "Cases", "Jane Roe", "Evidence", "services", "token=abc", "abcdefghijklmnop", "sk_live", Environment.UserName })
            Assert.DoesNotContain(leak, r);
        Assert.Contains("%USERPROFILE%\\…\\*.mp3", r);
        Assert.Contains("https://hooks.example.com/…", r);
    }

    [Fact]
    public void LibraryFileNamesStayUseful()
    {
        var r = LogRedactor.Redact($"Failed to read '{Path.Combine(RecordingLibrary.Root, "Client A", "2026-01-01 – Divorce hearing", "transcript.json")}'");
        Assert.Contains("<library>\\…\\transcript.json", r);
        Assert.DoesNotContain("Divorce", r);
        Assert.DoesNotContain("Client A", r);
    }
}
