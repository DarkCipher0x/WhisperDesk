using System.IO;
using Microsoft.Win32;

namespace WhisperDesk.Services;

/// <summary>
/// Glue with the MSI: paths chosen in the installer (HKLM\Software\darkcipher0x\WhisperDesk) and the
/// "--cleanup" command the uninstaller runs when the user ticks data to remove.
/// </summary>
public static class InstallerIntegration
{
    private static string? Machine(string name)
    {
        if (AppPaths.DevIsolated) return null;   // tests / isolated debug runs never use the installed paths
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\darkcipher0x\WhisperDesk");
            return key?.GetValue(name) is string s && s.Trim().Length > 0 ? Environment.ExpandEnvironmentVariables(s.Trim().Trim('"')) : null;
        }
        catch { return null; }
    }

    public static string? FfmpegPath => Machine("FfmpegPath");
    public static string? DefaultLibraryPath => Machine("DefaultLibraryPath");
    public static string? DefaultModelsPath => Machine("DefaultModelsPath");

    /// <summary>ffmpeg.exe installed by winget (it lands on PATH only for programs started afterwards).</summary>
    public static string? WingetFfmpeg()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var link = Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        if (File.Exists(link)) return link;
        try
        {
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (!Directory.Exists(packages)) return null;
            return Directory.EnumerateDirectories(packages, "Gyan.FFmpeg*")
                .SelectMany(d => Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories))
                .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Runs "WhisperDesk.exe --cleanup library=1 voices= settings=1 models=" for the uninstaller. Only recording
    /// folders are deleted from the library (other files the user put there are left alone), and nothing outside
    /// WhisperDesk's own folders is touched.
    /// </summary>
    public static int Cleanup(IEnumerable<string> args)
    {
        var opts = args.Select(a => a.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim() == "1");
        bool Want(string k) => opts.TryGetValue(k, out var v) && v;

        // Resolve paths before anything (the library/models locations live in settings.json).
        var local = Path.Combine(WhisperDesk.Services.AppPaths.LocalData);
        string library = "", models = "";
        try { library = RecordingLibrary.Root; } catch { }
        try { models = ModelManager.ModelsDir; } catch { }
        var log = new List<string>();

        if (Want("library") && IsSafe(library))
        {
            foreach (var meta in SafeEnum(library, "metadata.json"))
            {
                var dir = Path.GetDirectoryName(meta)!;
                try { Directory.Delete(dir, true); log.Add("deleted " + dir); } catch (Exception ex) { log.Add($"kept {dir}: {ex.Message}"); }
            }
            TryDelete(Path.Combine(library, Security.Vault.LibraryCopyName), log);
            foreach (var f in new[] { "folders.json", "folders.json.wde" }) TryDelete(Path.Combine(library, f), log);
            DeleteEmptyDirs(library, deleteRoot: true);
        }
        if (Want("voices")) { TryDelete(Path.Combine(local, "voices.json"), log); TryDelete(Path.Combine(local, "voices.json.wde"), log); }
        if (Want("models") && IsSafe(models))
        {
            foreach (var f in Directory.Exists(models) ? Directory.EnumerateFiles(models, "ggml-*.bin").ToList() : new List<string>()) TryDelete(f, log);
            foreach (var sub in new[] { "speakers", "custom" })
                if (Directory.Exists(Path.Combine(models, sub))) try { Directory.Delete(Path.Combine(models, sub), true); } catch { }
            DeleteEmptyDirs(models, deleteRoot: true);
        }
        if (Want("settings"))
        {
            TryDelete(Path.Combine(local, "settings.json"), log);
            foreach (var f in new[] { "vault.json", "network-log.json", "network-log.json.wde" }) TryDelete(Path.Combine(local, f), log);
            try { Security.SecretStore.DeleteAll(); } catch { }
            try { WerRemoveExcludedApplication("WhisperDesk.exe", false); } catch { }   // undo the crash-report opt-out
            foreach (var sub in new[] { "logs", "live", "addons", "webview", "work", "forensics" })
                if (Directory.Exists(Path.Combine(local, sub))) try { Directory.Delete(Path.Combine(local, sub), true); } catch { }
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "WhisperDesk"), true); } catch { }
        }
        if (Directory.Exists(local)) DeleteEmptyDirs(local, deleteRoot: true);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("wer.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int WerRemoveExcludedApplication(string exeName, bool allUsers);

    private static IEnumerable<string> SafeEnum(string root, string file)
    {
        try { return Directory.Exists(root) ? Directory.EnumerateFiles(root, file + "*", LibraryStorage.NoLinks).Where(f => f.EndsWith(file, StringComparison.OrdinalIgnoreCase) || f.EndsWith(file + ".wde", StringComparison.OrdinalIgnoreCase)).ToList() : Enumerable.Empty<string>(); }
        catch { return Enumerable.Empty<string>(); }
    }

    /// <summary>Refuses drive roots and the user's main folders, in case a setting points somewhere unexpected.</summary>
    private static bool IsSafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        var full = Path.GetFullPath(path).TrimEnd('\\');
        if (full.Length <= 3) return false;
        var forbidden = new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
        }.Select(f => Environment.GetFolderPath(f).TrimEnd('\\'));
        return !forbidden.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryDelete(string file, List<string> log)
    {
        try { if (File.Exists(file)) { File.Delete(file); log.Add("deleted " + file); } } catch (Exception ex) { log.Add($"kept {file}: {ex.Message}"); }
    }

    private static void DeleteEmptyDirs(string root, bool deleteRoot)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.EnumerateDirectories(root, "*", LibraryStorage.NoLinks).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            if (deleteRoot && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch { }
    }
}
