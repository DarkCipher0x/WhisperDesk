using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhisperDesk.Services;
using WhisperDesk.Services.Security;
using Xunit;

namespace WhisperDesk.Tests;

/// <summary>Vault tests share global state, so they run one at a time in this collection.</summary>
[CollectionDefinition("vault", DisableParallelization = true)]
public class VaultCollection { }

[Collection("vault")]
public class VaultTests : IDisposable
{
    private const string Pass = "correct horse battery staple";

    public VaultTests() => Reset();
    public void Dispose() => Reset();

    internal static void Reset()
    {
        Vault.ResetForTests();
        foreach (var f in new[] { Vault.FilePath, Path.Combine(RecordingLibrary.Root, Vault.LibraryCopyName) })
            if (File.Exists(f)) File.Delete(f);
        Vault.ResetForTests();
        Assert.Equal(VaultState.Disabled, Vault.State);
    }

    [Fact]
    public async Task PassphraseUnlocksAndWrongOneDoesNot()
    {
        var recovery = Vault.Create(Pass, new VaultPolicy());
        Assert.Equal(VaultState.Unlocked, Vault.State);
        Assert.Matches("^([A-Z2-7]{4}-){7}[A-Z2-7]{4}$", recovery);

        Vault.Lock();
        Assert.Equal(VaultState.Locked, Vault.State);
        Assert.Throws<VaultLockedException>(() => Vault.DeriveKey("x"));

        Assert.Equal(UnlockOutcome.Wrong, (await Vault.UnlockWithPassphraseAsync("wrong passphrase!!")).Outcome);
        Assert.Equal(1, Vault.Failures);
        Assert.Equal(UnlockOutcome.Unlocked, (await Vault.UnlockWithPassphraseAsync(Pass)).Outcome);
        Assert.Equal(0, Vault.Failures);
    }

    [Fact]
    public void VaultFileHoldsNoKeyOrPassphrase()
    {
        Vault.Create(Pass, new VaultPolicy());
        var json = File.ReadAllText(Vault.FilePath);
        Assert.DoesNotContain(Pass, json);
        Assert.DoesNotContain("horse", json);
        var dek = Convert.ToBase64String(Vault.DataKey());
        Assert.DoesNotContain(dek, json);
        // A copy travels with the library.
        Assert.True(File.Exists(Path.Combine(RecordingLibrary.Root, Vault.LibraryCopyName)));
    }

    [Fact]
    public async Task RepeatedFailuresImposeADelay()
    {
        Vault.Create(Pass, new VaultPolicy());
        Vault.Lock();
        for (int i = 0; i < 5; i++) await Vault.UnlockWithPassphraseAsync("nope nope nope");
        Assert.True(Vault.RetryDelay > TimeSpan.Zero);
        // Even the right passphrase waits.
        Assert.Equal(UnlockOutcome.Delayed, (await Vault.UnlockWithPassphraseAsync(Pass)).Outcome);
        // The delay survives a restart (it is stored in vault.json).
        Vault.ResetForTests();
        Assert.True(Vault.RetryDelay > TimeSpan.Zero);
    }

    [Fact]
    public async Task RecoveryKeyOpensAndPassphraseCanBeReplaced()
    {
        var recovery = Vault.Create(Pass, new VaultPolicy());
        Vault.Lock();
        Assert.Equal(UnlockOutcome.Wrong, Vault.UnlockWithRecovery("AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA").Outcome);
        Assert.Equal(UnlockOutcome.Unlocked, Vault.UnlockWithRecovery(recovery.ToLowerInvariant()).Outcome);
        Vault.ChangePassphrase("a brand new passphrase");
        Vault.Lock();
        Assert.Equal(UnlockOutcome.Wrong, (await Vault.UnlockWithPassphraseAsync(Pass)).Outcome);
        Assert.Equal(UnlockOutcome.Unlocked, (await Vault.UnlockWithPassphraseAsync("a brand new passphrase")).Outcome);
        // A replaced recovery key stops the old one from working.
        var fresh = Vault.NewRecoveryKey();
        Vault.Lock();
        Assert.Equal(UnlockOutcome.Wrong, Vault.UnlockWithRecovery(recovery).Outcome);
        Assert.Equal(UnlockOutcome.Unlocked, Vault.UnlockWithRecovery(fresh).Outcome);
    }

    [Fact]
    public async Task TotpIsRequiredAfterThePassphrase()
    {
        Vault.Create(Pass, new VaultPolicy());
        var (secret, uri) = Vault.NewTotpEnrollment("tester");
        Assert.StartsWith("otpauth://totp/WhisperDesk:", uri);
        Assert.False(Vault.EnableTotp(secret, WrongCode(secret)));
        Assert.True(Vault.EnableTotp(secret, Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow) - 1)));
        Assert.DoesNotContain(VaultCrypto.Base32(secret), File.ReadAllText(Vault.FilePath));   // stored encrypted

        Vault.Lock();
        Assert.Equal(UnlockOutcome.NeedsTotp, (await Vault.UnlockWithPassphraseAsync(Pass)).Outcome);
        Assert.Equal(VaultState.Locked, Vault.State);                  // not unlocked yet
        Assert.Equal(UnlockOutcome.Wrong, Vault.VerifyTotp(WrongCode(secret)).Outcome);
        Assert.Equal(UnlockOutcome.Unlocked, Vault.VerifyTotp(Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow))).Outcome);
    }

    /// <summary>A code that matches none of the accepted time steps.</summary>
    private static string WrongCode(byte[] secret)
    {
        var step = Totp.StepAt(DateTimeOffset.UtcNow);
        var valid = Enumerable.Range(-1, 3).Select(d => Totp.Code(secret, step + d)).ToHashSet();
        for (int i = 0; ; i++) { var c = i.ToString("D6"); if (!valid.Contains(c)) return c; }
    }

    [Fact]
    public async Task WeakenedPolicyInVaultFileIsDetected()
    {
        Vault.Create(Pass, new VaultPolicy { AutoLockMinutes = 5 });
        Vault.Lock();
        var node = JsonNode.Parse(File.ReadAllText(Vault.FilePath))!.AsObject();
        node["Policy"]!["AutoLockMinutes"] = 0;          // someone edits the file to switch auto-lock off
        File.WriteAllText(Vault.FilePath, node.ToJsonString());
        Vault.ResetForTests();
        Assert.Equal(UnlockOutcome.Unlocked, (await Vault.UnlockWithPassphraseAsync(Pass)).Outcome);
        Assert.True(Vault.PolicyWasTampered);
        Assert.NotEqual(0, Vault.Policy.AutoLockMinutes);
    }

    [Fact]
    public async Task RemovingTwoFactorByEditingTheFileIsDetected()
    {
        Vault.Create(Pass, new VaultPolicy());
        var (secret, _) = Vault.NewTotpEnrollment("t");
        Assert.True(Vault.EnableTotp(secret, Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow))));
        Vault.Lock();
        var node = JsonNode.Parse(File.ReadAllText(Vault.FilePath))!.AsObject();
        node.Remove("Totp");                               // someone who knows the passphrase strips 2FA from vault.json
        File.WriteAllText(Vault.FilePath, node.ToJsonString());
        Vault.ResetForTests();
        Assert.Equal(UnlockOutcome.Unlocked, (await Vault.UnlockWithPassphraseAsync(Pass)).Outcome);
        Assert.True(Vault.PolicyWasTampered);              // the user is told at once
    }

    [Fact]
    public async Task ImplausibleKdfSettingsAreRefused()
    {
        Vault.Create(Pass, new VaultPolicy());
        Vault.Lock();
        var node = JsonNode.Parse(File.ReadAllText(Vault.FilePath))!.AsObject();
        node["Slots"]![0]!["Iterations"] = 2_000_000_000;  // a doctored header that would hang the app
        File.WriteAllText(Vault.FilePath, node.ToJsonString());
        Vault.ResetForTests();
        Assert.Equal(UnlockOutcome.Error, (await Vault.UnlockWithPassphraseAsync(Pass)).Outcome);
    }

    [Fact]
    public void StartingOverKeepsTheEncryptedDataAndItsHeader()
    {
        Vault.Create(Pass, new VaultPolicy());
        var aside = Vault.SetAside();
        Assert.Equal(VaultState.Disabled, Vault.State);
        Assert.True(File.Exists(aside));
        Assert.False(File.Exists(Vault.FilePath));
        File.Move(aside, Vault.FilePath);                  // renaming it back makes the data openable again
        Vault.ResetForTests();
        Assert.Equal(VaultState.Locked, Vault.State);
    }

    [Fact]
    public void KeyStaysWhileARecordingHoldsIt()
    {
        Vault.Create(Pass, new VaultPolicy());
        var hold = Vault.Hold();
        Vault.Lock();
        Assert.Equal(VaultState.Unlocked, Vault.State);
        Assert.True(Vault.LockPending);
        hold.Dispose();
        Assert.Equal(VaultState.Locked, Vault.State);
    }
}

[Collection("vault")]
public class SecureFileTests : IDisposable
{
    public SecureFileTests() => VaultTests.Reset();
    public void Dispose() => VaultTests.Reset();

    private static string Dir()
    {
        var d = Path.Combine(RecordingLibrary.Root, "r", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void WithEncryptionOnNothingIsWrittenInPlaintext()
    {
        Vault.Create("correct horse battery staple", new VaultPolicy());
        var file = Path.Combine(Dir(), "transcript.json");
        SecureFile.WriteAllText(file, "Witness: I saw the car at 9pm.");
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(file + ".wde"));
        Assert.DoesNotContain("Witness", Encoding.UTF8.GetString(File.ReadAllBytes(file + ".wde")));
        Assert.Equal("Witness: I saw the car at 9pm.", SecureFile.ReadAllText(file));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp"));
    }

    [Fact]
    public void LockedVaultRefusesToWriteRatherThanWritePlaintext()
    {
        Vault.Create("correct horse battery staple", new VaultPolicy());
        var stored = Path.Combine(Dir(), "transcript.json");
        SecureFile.WriteAllText(stored, "secret");
        Vault.Lock();
        var file = Path.Combine(Dir(), "metadata.json");
        Assert.Throws<VaultLockedException>(() => SecureFile.WriteAllText(file, "secret"));
        Assert.False(File.Exists(file));
        Assert.False(SecureFile.CanRead(stored));
        Assert.Throws<VaultLockedException>(() => SecureFile.ReadAllText(stored));
    }

    [Fact]
    public void EncryptInPlaceVerifiesAndRemovesPlaintextThenDecryptsBack()
    {
        var dir = Dir();
        var file = Path.Combine(dir, "transcript.txt");
        File.WriteAllText(file, "plain transcript");
        Vault.Create("correct horse battery staple", new VaultPolicy());
        Assert.True(SecureFile.EncryptInPlace(file));
        Assert.False(File.Exists(file));
        Assert.Equal("plain transcript", SecureFile.ReadAllText(file));
        Assert.True(SecureFile.DecryptInPlace(file));
        Assert.Equal("plain transcript", File.ReadAllText(file));
        Assert.False(File.Exists(file + ".wde"));
    }

    [Fact]
    public void FailedWriteLeavesTheOldFileIntact()
    {
        Vault.Create("correct horse battery staple", new VaultPolicy());
        var file = Path.Combine(Dir(), "metadata.json");
        SecureFile.WriteAllText(file, "version 1");
        using (var s = SecureFile.Create(file))
        {
            s.Write(Encoding.UTF8.GetBytes("half-written version 2"));
            // no Commit(): an exception happened while writing
        }
        Assert.Equal("version 1", SecureFile.ReadAllText(file));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp"));
    }

    [Fact]
    public void MovedFileIsReEncryptedForItsNewPlace()
    {
        Vault.Create("correct horse battery staple", new VaultPolicy());
        var a = Path.Combine(Dir(), "audio.wav");
        var b = Path.Combine(Dir(), "audio.wav");
        SecureFile.WriteAllBytes(a, new byte[] { 1, 2, 3 });
        SecureFile.Move(a, b);
        Assert.False(SecureFile.Exists(a));
        Assert.Equal(new byte[] { 1, 2, 3 }, SecureFile.ReadAllBytes(b));
    }

    [Fact]
    public void SettingsEditedOutsideTheAppAreReset()
    {
        Vault.Create("correct horse battery staple", new VaultPolicy());
        var path = Path.Combine(AppPaths.LocalData, "settings.json");
        AppSettings.Current.Save();                                   // signed while unlocked
        // Someone edits settings.json to send transcripts to their server and pre-approve an add-on.
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["AllowRemoteAi"] = true;
        node["AiUrl"] = "https://collector.example.com";
        node["AddonsEnabled"] = new System.Text.Json.Nodes.JsonObject { ["evil.addon"] = true };
        File.WriteAllText(path, node.ToJsonString());

        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!;
        loaded.VerifySecurity();
        Assert.True(loaded.SecuritySettingsWereReset);
        Assert.False(loaded.AllowRemoteAi);
        Assert.Equal(AiReport.DefaultUrl, loaded.AiUrl);
        Assert.False(loaded.AddonsEnabled["evil.addon"]);

        // Settings saved by the app itself verify fine.
        var again = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!;
        again.VerifySecurity();
        Assert.False(again.SecuritySettingsWereReset);
        AppSettings.Current.Save();
    }

    [Fact]
    public void SecretSettingsAreNotStoredInPlaintext()
    {
        Vault.Create("correct horse battery staple", new VaultPolicy());
        var s = AppSettings.Current;
        s.InitialPrompt = "Names: Jane Roe, Acme Holdings";
        s.Save();
        var json = File.ReadAllText(Path.Combine(AppPaths.LocalData, "settings.json"));
        Assert.DoesNotContain("Jane Roe", json);
        s.ForgetProtected();
        Assert.Equal("", s.InitialPrompt);
        s.UnsealProtected();
        Assert.Equal("Names: Jane Roe, Acme Holdings", s.InitialPrompt);
        s.InitialPrompt = "";
        Vault.ResetForTests();
    }
}
