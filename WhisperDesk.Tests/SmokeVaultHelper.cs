using System.IO;
using WhisperDesk.Services;
using WhisperDesk.Services.Security;
using Xunit;

namespace WhisperDesk.Tests;

/// <summary>Helper for manual smoke tests only: creates an encrypted vault in WD_MAKE_VAULT_DIR. Does nothing otherwise.</summary>
public class SmokeVaultHelper
{
    [Fact]
    public void MakeVaultForSmokeTest()
    {
        var dir = Environment.GetEnvironmentVariable("WD_MAKE_VAULT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        AppPaths.Override = dir;
        AppSettings.Current.LibraryPath = Path.Combine(dir, "Library");
        AppSettings.Current.Save();
        Vault.ResetForTests();
        Vault.Create("smoke test passphrase", new VaultPolicy());
        Vault.Lock();
    }
}
