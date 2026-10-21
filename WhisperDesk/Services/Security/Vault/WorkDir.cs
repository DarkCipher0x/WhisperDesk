using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WhisperDesk.Services.Security;

/// <summary>
/// WhisperDesk's one folder for temporary plaintext: %LOCALAPPDATA%\WhisperDesk\work. Used instead of %TEMP% for shares,
/// opened .wdsession files, subtitle burning, restores and decrypted working copies of media. Windows' EFS encryption is
/// switched on for it where available (Pro/Enterprise, NTFS). It is wiped on lock, on exit and at the next start (in case
/// WhisperDesk was killed).
/// </summary>
public static class WorkDir
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, string> Decrypted_ = new(StringComparer.OrdinalIgnoreCase);

    public static string Root
    {
        get
        {
            var dir = Path.Combine(Vault.LocalData, "work");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                try { File.Encrypt(dir); } catch { }   // EFS: new files inside are encrypted by Windows too (not on Home editions)
            }
            return dir;
        }
    }

    /// <summary>A new empty folder for one job (deleted by <see cref="Release"/> or the next wipe).</summary>
    public static string NewFolder(string purpose)
    {
        var dir = Path.Combine(Root, $"{purpose}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Release(string? folder)
    {
        if (folder == null) return;
        try
        {
            var full = Path.GetFullPath(folder);
            if (!full.StartsWith(Path.GetFullPath(Root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return;   // only ever our own folder
            WipeTree(full);
        }
        catch { }
    }

    /// <summary>A decrypted copy of an encrypted library file (cached until the next wipe).</summary>
    public static string Decrypted(string logical)
    {
        var physical = SecureFile.Physical(logical);
        var info = new FileInfo(physical);
        var key = $"{Path.GetFullPath(physical)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        lock (Lock)
        {
            if (Decrypted_.TryGetValue(key, out var cached) && File.Exists(cached)) return cached;
            var dir = Path.Combine(Root, "media", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant());
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, Path.GetFileName(logical));
            var tmp = target + ".tmp";
            using (var src = SecureFile.OpenRead(logical))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                src.CopyTo(dst, 1 << 20);
            File.Move(tmp, target, overwrite: true);
            Decrypted_[key] = target;
            return target;
        }
    }

    /// <summary>Deletes every working file (on lock, exit and start). Files still open by a player are retried later.</summary>
    public static void WipeAll()
    {
        lock (Lock) Decrypted_.Clear();
        var root = Path.Combine(Vault.LocalData, "work");
        if (Directory.Exists(root))
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                try
                {
                    if (Directory.Exists(entry)) WipeTree(entry);
                    else SecureFile.Wipe(entry);
                }
                catch { }
            }
        // Older versions used %TEMP%: clear what they left (opened sessions, shares, restores).
        try
        {
            var temp = Path.GetTempPath();
            foreach (var d in Directory.EnumerateDirectories(temp, "wdsession-open-*").Concat(Directory.EnumerateDirectories(temp, "WhisperDesk-burn-*")))
                try { WipeTree(d); } catch { }
            foreach (var f in Directory.EnumerateFiles(temp, "wdrestore-*.zip").Concat(Directory.EnumerateFiles(temp, "wd-voices-*.json")))
                try { SecureFile.Wipe(f); } catch { }
            var share = Path.Combine(temp, "WhisperDesk");
            if (Directory.Exists(share)) WipeTree(share);
        }
        catch { }
    }

    /// <summary>Wipes and deletes a folder tree without following links or junctions.</summary>
    public static void WipeTree(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (!info.Exists) return;
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { info.Delete(); return; }
        foreach (var f in info.EnumerateFiles()) { try { SecureFile.Wipe(f.FullName); } catch { } }
        foreach (var d in info.EnumerateDirectories()) WipeTree(d.FullName);
        try { info.Delete(); } catch { }
    }
}
