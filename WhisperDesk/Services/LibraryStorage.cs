using System.IO;
using System.Security.Cryptography;

namespace WhisperDesk.Services;

/// <summary>
/// Where the recordings library lives by default, whether a folder is uploaded by a cloud-sync client, and a safe
/// (copy → verify → delete) library move. No UI and no settings access, so it can be tested on its own.
/// </summary>
public static class LibraryStorage
{
    /// <summary>Default for new profiles: %LOCALAPPDATA%\WhisperDesk\Library. Local AppData never roams or syncs.</summary>
    public static string LocalDefault =>
        Path.Combine(WhisperDesk.Services.AppPaths.LocalData, "Library");

    /// <summary>Default up to 1.5.1: Documents\WhisperDesk Library (inside OneDrive when Windows backs up Documents).</summary>
    public static string LegacyDefault =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WhisperDesk Library");

    /// <summary>Documents, unless a cloud client syncs it (OneDrive folder backup): then %LOCALAPPDATA%\WhisperDesk.</summary>
    public static string LocalDocumentsFolder()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return CloudSyncedBy(docs) == null && docs.Length > 0
            ? docs : Path.Combine(WhisperDesk.Services.AppPaths.LocalData);
    }

    /// <summary>
    /// The cloud service that syncs <paramref name="path"/> (OneDrive, Dropbox, Google Drive, iCloud, Box), or null.
    /// WhisperDesk uploads nothing itself, but a synced folder means the sync client uploads its files.
    /// </summary>
    public static string? CloudSyncedBy(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            // The sync roots Windows reports for OneDrive (personal and work/school).
            foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
                if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } root && IsUnder(full, root))
                    return "OneDrive";
            // Sync roots registered with Windows' Cloud Files API (OneDrive, Dropbox, Google Drive, iCloud, Box, others).
            foreach (var (root, provider) in RegisteredSyncRoots())
                if (IsUnder(full, root)) return provider;
            // Otherwise compare folder names (never path fragments), so any folder inside a sync root is caught.
            foreach (var segment in full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                if (SyncFolderName(segment) is { } name) return name;
        }
        catch { }
        return null;
    }

    /// <summary>The cloud service a folder of this name belongs to ("OneDrive", "OneDrive - Contoso", "Dropbox (Team)"…), or null.</summary>
    private static string? SyncFolderName(string segment)
    {
        bool Is(string n) => segment.Equals(n, StringComparison.OrdinalIgnoreCase);
        bool Starts(string n) => segment.StartsWith(n, StringComparison.OrdinalIgnoreCase);
        if (Is("OneDrive") || Starts("OneDrive - ")) return "OneDrive";
        if (Is("Dropbox") || Starts("Dropbox (")) return "Dropbox";
        if (Is("Google Drive") || Is("My Drive")) return "Google Drive";
        if (Is("iCloudDrive") || Is("iCloud Drive")) return "iCloud Drive";
        if (Is("Box")) return "Box";
        return null;
    }

    private static (DateTime At, List<(string Root, string Provider)> Roots) _syncRoots = (DateTime.MinValue, new());

    /// <summary>
    /// Folders that sync clients registered with Windows (HKLM\…\Explorer\SyncRootManager\&lt;provider!SID!account&gt;\UserSyncRoots)
    /// for this user, plus OneDrive's own account folders. Cached for a minute.
    /// </summary>
    internal static List<(string Root, string Provider)> RegisteredSyncRoots()
    {
        if (DateTime.UtcNow - _syncRoots.At < TimeSpan.FromMinutes(1)) return _syncRoots.Roots;
        var list = new List<(string, string)>();
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
            using var mgr = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            foreach (var name in mgr?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var roots = mgr!.OpenSubKey(name + @"\UserSyncRoots");
                if (roots == null) continue;
                foreach (var v in roots.GetValueNames())
                    if (v.Equals(sid, StringComparison.OrdinalIgnoreCase) && roots.GetValue(v) is string path && path.Length > 3)
                        list.Add((path, ProviderName(name)));
            }
        }
        catch { }
        try
        {
            using var accounts = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Path.Combine("Software", "Microsoft", "OneDrive", "Accounts"));
            foreach (var a in accounts?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var k = accounts!.OpenSubKey(a);
                if (k?.GetValue("UserFolder") is string folder && folder.Length > 3) list.Add((folder, "OneDrive"));
            }
        }
        catch { }
        _syncRoots = (DateTime.UtcNow, list);
        return list;
    }

    private static string ProviderName(string key)
    {
        var id = key.Split('!')[0];
        if (id.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) return "OneDrive";
        if (id.Contains("Dropbox", StringComparison.OrdinalIgnoreCase)) return "Dropbox";
        if (id.Contains("Google", StringComparison.OrdinalIgnoreCase)) return "Google Drive";
        if (id.Contains("iCloud", StringComparison.OrdinalIgnoreCase)) return "iCloud Drive";
        if (id.StartsWith("Box", StringComparison.OrdinalIgnoreCase)) return "Box";
        return id.Length > 0 ? id : "a cloud sync service";
    }

    private static bool IsUnder(string full, string root)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var f = full.TrimEnd(Path.DirectorySeparatorChar);
        return f.Equals(r, StringComparison.OrdinalIgnoreCase) || f.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Number of recordings (folders with metadata.json) under <paramref name="dir"/>; 0 if it doesn't exist.</summary>
    public static int CountRecordings(string? dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "metadata.json*", NoLinks).Count(f => f.EndsWith("metadata.json", StringComparison.OrdinalIgnoreCase) || f.EndsWith("metadata.json.wde", StringComparison.OrdinalIgnoreCase)) : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// For a profile that already used an older WhisperDesk with no library path set: the folder that version used
    /// (installer default, else Documents\WhisperDesk Library) if it holds recordings, so they keep working where they are.
    /// </summary>
    public static string? PreviousDefault(string? installerDefault, string legacyDefault, Func<string, int>? count = null)
    {
        count ??= CountRecordings;
        foreach (var dir in new[] { installerDefault, legacyDefault })
            if (!string.IsNullOrWhiteSpace(dir) && count(dir) > 0) return dir;
        return null;
    }

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool IsInside(string child, string parent) =>
        Path.GetFullPath(child).TrimEnd('\\').StartsWith(Path.GetFullPath(parent).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Recursive enumeration that never enters symbolic links or junctions. A link inside the library pointing elsewhere
    /// (planted, or made by a sync tool) must never make a move, backup or clean-up copy or delete files outside it.
    /// </summary>
    public static EnumerationOptions NoLinks => new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };

    public sealed record MoveResult(int Copied, int Skipped, long Bytes, int Conflicts, int LeftBehind);

    /// <summary>
    /// Moves every file under <paramref name="source"/> into <paramref name="target"/> (merging with what is there):
    /// each file is copied to a temporary name, its SHA-256 compared with the original, then renamed into place.
    /// Originals are deleted only after every file has been copied and verified. A file that already exists in the
    /// target with different content is never overwritten: it stays in the source and is counted as a conflict.
    /// Throws (with nothing deleted) if any copy fails or doesn't verify.
    /// </summary>
    public static async Task<MoveResult> MoveAsync(string source, string target, IProgress<string>? status, CancellationToken ct)
    {
        source = Path.GetFullPath(source).TrimEnd('\\');
        target = Path.GetFullPath(target).TrimEnd('\\');
        if (SamePath(source, target)) throw new InvalidOperationException("That is already the library folder.");
        if (IsInside(target, source)) throw new InvalidOperationException("The new folder can't be inside the current library folder.");
        if (IsInside(source, target)) throw new InvalidOperationException("The new folder can't contain the current library folder.");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("The library folder doesn't exist: " + source);

        var files = Directory.EnumerateFiles(source, "*", NoLinks)
            .Where(f => !f.EndsWith(".wdmove", StringComparison.OrdinalIgnoreCase)).ToList();
        long total = files.Sum(f => new FileInfo(f).Length), done = 0;
        Directory.CreateDirectory(target);
        var drive = new DriveInfo(Path.GetPathRoot(target)!);
        if (drive.IsReady && drive.AvailableFreeSpace < total + (64L << 20))
            throw new IOException($"Not enough free space on {drive.Name} ({drive.AvailableFreeSpace / 1048576:N0} MB free, {total / 1048576:N0} MB needed).");

        var verified = new List<string>();
        int copied = 0, skipped = 0, conflicts = 0;
        var buf = new byte[1 << 20];
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            status?.Report($"Moving library… {rel}  ({done / 1048576:N0} / {total / 1048576:N0} MB)");

            // Read the original once (this also downloads cloud-only placeholders), hashing as we copy.
            byte[] srcHash;
            var tmp = dest + ".wdmove";
            if (File.Exists(dest))
            {
                srcHash = await HashAsync(file, ct);
                var existing = await HashAsync(dest, ct);
                if (srcHash.SequenceEqual(existing)) { verified.Add(file); skipped++; }
                else conflicts++;
                done += new FileInfo(file).Length;
                continue;
            }
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    int read;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        sha.AppendData(buf, 0, read);
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                        done += read;
                    }
                }
                srcHash = sha.GetHashAndReset();
            }
            var copyHash = await HashAsync(tmp, ct);
            if (!srcHash.SequenceEqual(copyHash))
            {
                try { File.Delete(tmp); } catch { }
                throw new IOException($"Verification failed for {rel}: the copy differs from the original. Nothing was deleted.");
            }
            File.Move(tmp, dest);
            try { File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file)); } catch { }
            verified.Add(file);
            copied++;
        }

        // Everything is copied and verified: only now remove the originals.
        int left = 0;
        foreach (var file in verified)
            try { File.Delete(file); } catch { left++; }
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(source, "*", NoLinks).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
        }
        catch { }
        status?.Report($"Moved {copied + skipped} file(s) to {target}.");
        return new MoveResult(copied, skipped, total, conflicts, left);
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await SHA256.HashDataAsync(fs, ct);
    }
}
