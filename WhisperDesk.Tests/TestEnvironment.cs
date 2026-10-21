using System.IO;
using System.Runtime.CompilerServices;
using WhisperDesk.Services;

namespace WhisperDesk.Tests;

/// <summary>
/// Runs before any test: every WhisperDesk data path (settings, vault, library, voices, logs) is redirected to a fresh
/// temporary folder, so the tests can never read or change the real library or settings on this PC.
/// </summary>
internal static class TestEnvironment
{
    public static string Root { get; private set; } = "";

    [ModuleInitializer]
    internal static void Init()
    {
        // The test host is often killed before ProcessExit runs: clear earlier runs' folders first.
        foreach (var old in Directory.EnumerateDirectories(Path.GetTempPath(), "wd-tests-*"))
            if (Directory.GetCreationTimeUtc(old) < DateTime.UtcNow.AddMinutes(-30)) try { Directory.Delete(old, true); } catch { }
        Root = Path.Combine(Path.GetTempPath(), "wd-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        AppPaths.Override = Path.Combine(Root, "appdata");
        Directory.CreateDirectory(AppPaths.Override);
        AppSettings.Current.LibraryPath = Path.Combine(Root, "library");
        AppSettings.Current.LibraryDefaultChecked = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Root, true); } catch { } };
    }
}
