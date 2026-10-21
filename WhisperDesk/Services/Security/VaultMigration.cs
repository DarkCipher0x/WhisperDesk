using System.IO;

namespace WhisperDesk.Services.Security;

/// <summary>
/// Converts what is already stored when encryption is turned on (or off). Every file is written in its new form and
/// checked before the old form is removed, so an interruption never loses data; running it again finishes the job.
/// </summary>
public static class VaultMigration
{
    public sealed record Report(int Recordings, int Files, int Failed, List<string> Problems);

    private static string LocalData => AppPaths.LocalData;

    /// <summary>The app-data files that hold content (voices, forensic logs, live recordings and recovery, network log).</summary>
    private static IEnumerable<string> AppDataFiles()
    {
        var list = new List<string> { Path.Combine(LocalData, "voices.json"), Path.Combine(LocalData, "network-log.json") };
        foreach (var sub in new[] { "forensics", "live" })
        {
            var dir = Path.Combine(LocalData, sub);
            if (Directory.Exists(dir)) list.AddRange(Directory.EnumerateFiles(dir, "*", LibraryStorage.NoLinks).Where(f => !f.EndsWith(WdeFormat.Extension, StringComparison.OrdinalIgnoreCase)));
        }
        return list.Where(File.Exists);
    }

    /// <summary>Encrypts the library (into the opaque layout), the voice library, logs of examinations, live/recovery files and secrets.</summary>
    public static Task<Report> EncryptAllAsync(IProgress<string> status, CancellationToken ct) => Task.Run(() =>
    {
        if (!Vault.IsUnlocked) throw new VaultLockedException();
        int recordings = 0, files = 0, failed = 0;
        var problems = new List<string>();
        var root = RecordingLibrary.Root;
        var encRoot = Path.Combine(root, RecordingLibrary.EncryptedDir);

        // Folders first (so empty ones survive), then recordings, outermost first ("previous" versions travel with their recording).
        var plainFolders = RecordingLibrary.Folders();
        var todo = RecordingLibrary.All().Where(m => !m.IsEncrypted).OrderBy(m => m.Dir.Length).ToList();
        foreach (var m in todo)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(m.Dir)) continue;   // moved along with its parent
            status.Report($"Encrypting recordings… {recordings + 1} of {todo.Count}");
            try { RecordingLibrary.EncryptRecording(m); recordings++; }
            catch (Exception ex) { failed++; problems.Add($"{m.Title}: {ex.Message}"); App.Log("Encrypt recording", ex); }
        }
        // Remember the folder tree in the encrypted index, then remove the now-empty plaintext folders (their names are private too).
        foreach (var f in plainFolders.Where(f => f.Length > 0)) try { RecordingLibrary.CreateFolder(Path.GetDirectoryName(f) ?? "", Path.GetFileName(f)); } catch { }
        RemoveEmptyFolders(root, keep: encRoot);

        foreach (var file in AppDataFiles())
        {
            ct.ThrowIfCancellationRequested();
            status.Report($"Encrypting {Path.GetFileName(file)}…");
            try { if (SecureFile.EncryptInPlace(file)) files++; }
            catch (Exception ex) { failed++; problems.Add($"{Path.GetFileName(file)}: {ex.Message}"); App.Log("Encrypt file", ex); }
        }
        status.Report("Protecting settings and secrets…");
        AppSettings.Current.Save();
        try { SecretStore.Rewrap(); } catch (Exception ex) { problems.Add("Secrets: " + ex.Message); }
        // The old embedded-browser profile could hold pages shown before InPrivate mode existed.
        try { WorkDir.WipeTree(Path.Combine(LocalData, "webview")); } catch { }
        WorkDir.WipeAll();
        return new Report(recordings, files, failed, problems);
    }, ct);

    /// <summary>Turns encryption off: everything back to ordinary files, then the vault header and key are destroyed.</summary>
    public static Task<Report> DecryptAllAsync(IProgress<string> status, CancellationToken ct) => Task.Run(() =>
    {
        if (!Vault.IsUnlocked) throw new VaultLockedException();
        int recordings = 0, files = 0, failed = 0;
        var problems = new List<string>();
        var root = RecordingLibrary.Root;
        var folders = RecordingLibrary.Folders();
        var todo = RecordingLibrary.All().Where(m => m.IsEncrypted).ToList();
        foreach (var m in todo)
        {
            ct.ThrowIfCancellationRequested();
            status.Report($"Decrypting recordings… {recordings + 1} of {todo.Count}");
            try { RecordingLibrary.DecryptRecording(m); recordings++; }
            catch (Exception ex) { failed++; problems.Add($"{m.Title}: {ex.Message}"); App.Log("Decrypt recording", ex); }
        }
        foreach (var f in folders.Where(f => f.Length > 0)) Directory.CreateDirectory(Path.Combine(root, RecordingLibrary.CleanFolder(f)));
        foreach (var dir in new[] { root, LocalData })
            foreach (var enc in Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*" + WdeFormat.Extension, LibraryStorage.NoLinks).ToList() : new())
            {
                ct.ThrowIfCancellationRequested();
                var logical = enc[..^WdeFormat.Extension.Length];
                if (Path.GetFileName(logical).Equals("folders.json", StringComparison.OrdinalIgnoreCase)) continue;
                try { if (SecureFile.DecryptInPlace(logical)) files++; }
                catch (Exception ex) { failed++; problems.Add($"{Path.GetFileName(logical)}: {ex.Message}"); }
            }
        if (failed > 0) return new Report(recordings, files, failed, problems);   // keep the vault: some data is still encrypted

        // Secrets: read them with the key, then store them plainly (Credential Manager still protects them).
        var secrets = SecretStore.ReadAll();
        AppSettings.Current.UnsealProtected();
        try { File.Delete(Path.Combine(root, "folders.json.wde")); } catch { }
        try { var e = Path.Combine(root, RecordingLibrary.EncryptedDir); if (Directory.Exists(e) && !Directory.EnumerateFileSystemEntries(e).Any()) Directory.Delete(e); } catch { }
        Vault.Destroy();
        AppSettings.Current.Save();
        foreach (var (scope, key, value) in secrets) SecretStore.Set(scope, key, value);
        return new Report(recordings, files, 0, problems);
    }, ct);

    private static void RemoveEmptyFolders(string root, string keep)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root, "*", LibraryStorage.NoLinks).OrderByDescending(d => d.Length))
            {
                if (d.StartsWith(keep, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            }
        }
        catch { }
    }
}
