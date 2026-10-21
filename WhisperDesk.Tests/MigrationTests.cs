using System.IO;
using System.Text;
using WhisperDesk.Services;
using WhisperDesk.Services.Security;
using Xunit;

namespace WhisperDesk.Tests;

/// <summary>Turning encryption on and off for a library that already has recordings, folders, audio and voices.</summary>
[Collection("vault")]
public class MigrationTests : IDisposable
{
    public MigrationTests()
    {
        VaultTests.Reset();
        var root = RecordingLibrary.Root;
        foreach (var d in Directory.EnumerateDirectories(root)) Directory.Delete(d, true);
        foreach (var f in Directory.EnumerateFiles(root)) File.Delete(f);
        // Leftovers from other tests (encrypted with their own, now forgotten, keys).
        foreach (var sub in new[] { "live", "forensics" })
            if (Directory.Exists(Path.Combine(AppPaths.LocalData, sub))) Directory.Delete(Path.Combine(AppPaths.LocalData, sub), true);
        foreach (var f in Directory.EnumerateFiles(AppPaths.LocalData, "*.wde")) File.Delete(f);
    }

    public void Dispose() => VaultTests.Reset();

    private static RecordingMeta AddRecording(string title, string folder, string text, byte[]? audio)
    {
        var result = new TranscriptResult { SourceFile = "x" };
        result.Lines.Add(new TranscriptLine(TimeSpan.Zero, TimeSpan.FromSeconds(2), text, "en", 0.9f, null));
        string? media = null;
        if (audio != null)
        {
            media = Path.Combine(TestEnvironment.Root, Guid.NewGuid().ToString("N") + ".wav");
            File.WriteAllBytes(media, audio);
        }
        return RecordingLibrary.Save(result, new RecordingMeta { Title = title, Created = new DateTime(2026, 3, 4, 10, 0, 0) }, folder, null, media, null);
    }

    private static List<string> AllLibraryText() =>
        Directory.EnumerateFiles(RecordingLibrary.Root, "*", SearchOption.AllDirectories)
            .Select(f => f + "\n" + Encoding.UTF8.GetString(File.ReadAllBytes(f))).ToList();

    [Fact]
    public async Task EncryptingHidesEverythingAndDecryptingRestoresIt()
    {
        var audio = Encoding.ASCII.GetBytes("RIFF....WAVEfmt fake audio bytes for the test");
        AddRecording("Custody hearing", "Clients\\Doe family", "The witness said the child was safe.", audio);
        AddRecording("Board meeting", "", "Quarterly numbers are confidential.", null);
        RecordingLibrary.CreateFolder("", "Empty case folder");
        VoiceLibrary.Shared.Remember("Jane Roe", Sexes.Unspecified, new float[] { 0.1f, 0.2f, 0.3f });
        VoiceLibrary.Shared.Save();

        Vault.Create("correct horse battery staple", new VaultPolicy());
        var report = await VaultMigration.EncryptAllAsync(new Progress<string>(), CancellationToken.None);
        Assert.Equal(0, report.Failed);
        Assert.Equal(2, report.Recordings);

        // Nothing readable on disk: no titles, folder names, transcript text, audio or voice names.
        foreach (var leak in new[] { "Custody", "Doe family", "witness", "Board meeting", "Quarterly", "Empty case folder", "fake audio" })
            Assert.DoesNotContain(AllLibraryText(), t => t.Contains(leak, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Jane Roe", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(AppPaths.LocalData, "voices.json.wde"))));
        Assert.False(File.Exists(Path.Combine(AppPaths.LocalData, "voices.json")));
        Assert.Equal(0, RecordingLibrary.PlaintextCount());

        // While unlocked, everything works as before.
        var all = RecordingLibrary.All();
        Assert.Equal(2, all.Count);
        var hearing = all.Single(m => m.Title == "Custody hearing");
        Assert.Equal("Clients\\Doe family", hearing.Folder);
        Assert.Contains("Empty case folder", RecordingLibrary.Folders());
        Assert.Equal(audio, File.ReadAllBytes(hearing.MediaPath));                    // decrypted working copy
        Assert.StartsWith(Path.GetFullPath(WorkDir.Root), Path.GetFullPath(hearing.MediaPath));
        Assert.Contains("witness", RecordingLibrary.ReadTranscriptText(hearing));

        // Moving and renaming folders happens inside the encrypted index.
        RecordingLibrary.Move(hearing, "Archive");
        RecordingLibrary.RenameFolder("Archive", "Closed cases");
        Assert.Equal("Closed cases", RecordingLibrary.All().Single(m => m.Title == "Custody hearing").Folder);
        Assert.DoesNotContain(AllLibraryText(), t => t.Contains("Closed cases"));

        // Locked: nothing can be listed or read.
        Vault.Lock();
        Assert.Empty(RecordingLibrary.All());
        Assert.Equal(UnlockOutcome.Unlocked, (await Vault.UnlockWithPassphraseAsync("correct horse battery staple")).Outcome);
        WorkDir.WipeAll();

        // Turning it off brings back ordinary, readable files.
        var back = await VaultMigration.DecryptAllAsync(new Progress<string>(), CancellationToken.None);
        Assert.True(back.Failed == 0, string.Join(" | ", back.Problems));
        Assert.Equal(VaultState.Disabled, Vault.State);
        var plain = RecordingLibrary.All();
        Assert.Equal(2, plain.Count);
        var restored = plain.Single(m => m.Title == "Custody hearing");
        Assert.Equal(audio, File.ReadAllBytes(restored.MediaPath));
        Assert.Contains("witness", File.ReadAllText(Path.Combine(restored.Dir, "transcript.txt")));
        Assert.True(Directory.Exists(Path.Combine(RecordingLibrary.Root, "Empty case folder")));
        Assert.Empty(Directory.EnumerateFiles(RecordingLibrary.Root, "*.wde", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(AppPaths.LocalData, "voices.json")));
        VoiceLibrary.Shared.Forget();
    }

    [Fact]
    public void EncryptedLiveRecordingSurvivesACrash()
    {
        Vault.Create("correct horse battery staple", new VaultPolicy());
        var live = Path.Combine(AppPaths.LocalData, "live", "live-test.wav");
        var sink = LiveRecording.Create(live);
        var tone = Enumerable.Range(0, 16000 * 3).Select(i => (float)Math.Sin(i / 10.0) * 0.5f).ToArray();
        sink.WriteSamples(tone);
        sink.Flush();                          // the 10-second flush while recording
        // Crash: the sink is never disposed (no final chunk). Nothing plaintext is on disk.
        Assert.False(File.Exists(live));
        Assert.True(File.Exists(live + ".wde"));
        var growing = LiveRecording.ReadPcm16Mono(live);
        Assert.Equal(tone.Length, growing.Length);

        // Simulate the crash: keep the file exactly as it was on disk, without the final chunk.
        byte[] crashed;
        using (var fs = new FileStream(live + ".wde", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var ms = new MemoryStream()) { fs.CopyTo(ms); crashed = ms.ToArray(); }
        sink.Dispose();
        File.WriteAllBytes(live + ".wde", crashed);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => SecureFile.ReadAllBytes(live));

        // Recovery gives it a proper end and correct WAV sizes.
        Assert.True(LiveRecording.Finish(live));
        using var s = SecureFile.OpenRead(live);
        var header = new byte[44];
        s.ReadExactly(header);
        Assert.Equal(tone.Length * 2, BitConverter.ToInt32(header, 40));
    }
}
