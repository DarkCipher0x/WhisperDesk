using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WhisperDesk.Services.Security;

/// <summary>
/// File access for WhisperDesk's own data (library, voice library, recovery files, forensic logs…). Callers use the
/// plaintext ("logical") name, e.g. <c>…\metadata.json</c>. When encryption is on the data is stored as
/// <c>metadata.json.wde</c> (<see cref="WdeFormat"/>) and a plaintext version is never written; when it is off, files are
/// ordinary files. Reading works for either form, so a library that is half-way through conversion keeps working.
/// Exports the user saves elsewhere do NOT go through this class: they are plaintext by the user's choice.
/// </summary>
public static class SecureFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static string EncryptedName(string logical) => logical + WdeFormat.Extension;

    /// <summary>True when protected data may be written now (encryption off, or on and unlocked).</summary>
    public static bool CanWrite => !Vault.IsEnabled || Vault.IsUnlocked;

    /// <summary>True when this file can be read now (plaintext, or encrypted and the vault is unlocked).</summary>
    public static bool CanRead(string logical) => !File.Exists(EncryptedName(logical)) || Vault.IsUnlocked;

    public static bool Exists(string logical) => File.Exists(logical) || File.Exists(EncryptedName(logical));

    public static bool IsEncrypted(string logical) => File.Exists(EncryptedName(logical));

    /// <summary>The file on disk for a logical name (the encrypted one when both exist).</summary>
    public static string Physical(string logical) => File.Exists(EncryptedName(logical)) ? EncryptedName(logical) : logical;

    /// <summary>Size of the stored file (ciphertext size for encrypted files), or 0.</summary>
    public static long StoredLength(string logical)
    {
        try { return new FileInfo(Physical(logical)).Length; } catch { return 0; }
    }

    public static DateTime LastWriteTime(string logical) => File.GetLastWriteTime(Physical(logical));

    /// <summary>
    /// The name bound into each encrypted file's authentication data: the path relative to the library or the app data
    /// folder. Moving the whole library keeps it; copying a file into another recording does not (that is detected).
    /// </summary>
    public static string Context(string logical)
    {
        var full = Path.GetFullPath(logical);
        string? Rel(string root)
        {
            try
            {
                var r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? full[r.Length..] : null;
            }
            catch { return null; }
        }
        if (Rel(RecordingLibrary.Root) is { } lib) return "lib/" + lib;
        if (Rel(Vault.LocalData) is { } app) return "app/" + app;
        return "ext/" + Path.GetFileName(full);
    }

    // ---------------- read ----------------

    public static Stream OpenRead(string logical, bool allowIncomplete = false)
    {
        var enc = EncryptedName(logical);
        if (File.Exists(enc))
        {
            var key = Vault.DataKey();
            var fs = new FileStream(enc, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            try { return new WdeReadStream(fs, key, Context(logical), allowIncomplete); }
            catch { fs.Dispose(); throw; }
        }
        return new FileStream(logical, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
    }

    /// <summary>Decrypts a file that was copied away from its place (a backup being restored), naming its original place.</summary>
    public static byte[] ReadWithContext(string physicalPath, string context)
    {
        using var r = new WdeReadStream(new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read), Vault.DataKey(), context, allowIncomplete: false);
        using var ms = new MemoryStream();
        r.CopyTo(ms);
        return ms.ToArray();
    }

    public static byte[] ReadAllBytes(string logical)
    {
        using var s = OpenRead(logical);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static string ReadAllText(string logical)
    {
        using var s = OpenRead(logical);
        using var r = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    public static string[] ReadAllLines(string logical) =>
        ReadAllText(logical).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    // ---------------- write ----------------

    /// <summary>
    /// A write stream that becomes the file when disposed (written to a temporary name first, so a crash never leaves a
    /// half-written file in place). Encrypted when encryption is on; throws if it is on but locked.
    /// </summary>
    public static CommitStream Create(string logical)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logical))!);
        if (Vault.IsEnabled)
        {
            var key = Vault.DataKey();
            var target = EncryptedName(logical);
            var tmp = target + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)) + ".tmp";
            var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            var w = new WdeWriteStream(fs, key, Context(logical));
            return new CommitStream(w, () =>
            {
                File.Move(tmp, target, overwrite: true);
                if (File.Exists(logical)) Wipe(logical);   // the old plaintext version must not stay behind
            }, () => { try { File.Delete(tmp); } catch { } });
        }
        else
        {
            var tmp = logical + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)) + ".tmp";
            var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            return new CommitStream(fs, () => File.Move(tmp, logical, overwrite: true), () => { try { File.Delete(tmp); } catch { } });
        }
    }

    public static void WriteAllBytes(string logical, ReadOnlySpan<byte> data)
    {
        using var s = Create(logical);
        s.Write(data);
        s.Commit();
    }

    public static void WriteAllText(string logical, string text, Encoding? encoding = null)
    {
        var enc = encoding ?? Utf8NoBom;
        var bytes = enc.GetPreamble().Concat(enc.GetBytes(text)).ToArray();
        try { WriteAllBytes(logical, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Appends text (encrypted files are rewritten whole; used for small logs only).</summary>
    public static void AppendAllText(string logical, string text)
    {
        if (!Vault.IsEnabled && !IsEncrypted(logical))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logical))!);
            File.AppendAllText(logical, text, Utf8NoBom);
            return;
        }
        var existing = Exists(logical) ? ReadAllText(logical) : "";
        WriteAllText(logical, existing + text);
    }

    /// <summary>An append-only encrypted stream for a long recording; readable while growing with allowIncomplete.</summary>
    public static WdeWriteStream CreateAppendOnly(string logical)
    {
        var key = Vault.DataKey();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logical))!);
        var fs = new FileStream(EncryptedName(logical), FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 1 << 16);
        return new WdeWriteStream(fs, key, Context(logical));
    }

    // ---------------- move, copy, delete ----------------

    /// <summary>Copies a file into protected storage (re-encrypting it for its new place, or encrypting a plaintext source).</summary>
    public static void Copy(string sourceLogical, string targetLogical)
    {
        if (!Vault.IsEnabled && !IsEncrypted(sourceLogical))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetLogical))!);
            File.Copy(sourceLogical, targetLogical, overwrite: true);
            return;
        }
        using var src = OpenRead(sourceLogical);
        using var dst = Create(targetLogical);
        src.CopyTo(dst, 1 << 20);
        dst.Commit();
    }

    /// <summary>Moves a file into protected storage. The source is removed only after the copy is complete.</summary>
    public static void Move(string sourceLogical, string targetLogical)
    {
        if (!Vault.IsEnabled && !IsEncrypted(sourceLogical))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetLogical))!);
            File.Move(sourceLogical, targetLogical);
            return;
        }
        Copy(sourceLogical, targetLogical);
        Delete(sourceLogical);
    }

    /// <summary>Copies an external, plaintext file (user's media) into protected storage.</summary>
    public static void Import(string externalPlainPath, string targetLogical)
    {
        if (!Vault.IsEnabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetLogical))!);
            File.Copy(externalPlainPath, targetLogical, overwrite: true);
            return;
        }
        using var src = new FileStream(externalPlainPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var dst = Create(targetLogical);
        src.CopyTo(dst, 1 << 20);
        dst.Commit();
    }

    /// <summary>Deletes both forms of a file; the plaintext form is overwritten first.</summary>
    public static void Delete(string logical)
    {
        if (File.Exists(EncryptedName(logical))) File.Delete(EncryptedName(logical));
        if (File.Exists(logical)) Wipe(logical);
    }

    /// <summary>
    /// Overwrites a plaintext file with zeros, then deletes it. Best effort: on SSDs and copy-on-write file systems the old
    /// blocks may survive (BitLocker covers that), but this removes the data from ordinary undelete tools.
    /// </summary>
    public static void Wipe(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0) { File.Delete(path); return; }   // never write through a link
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zeros = new byte[1 << 20];
                long left = fs.Length;
                while (left > 0) { int n = (int)Math.Min(zeros.Length, left); fs.Write(zeros, 0, n); left -= n; }
                fs.Flush(flushToDisk: true);
            }
        }
        catch { }
        File.Delete(path);
    }

    /// <summary>
    /// Encrypts an existing plaintext file in place: writes the encrypted version, decrypts it again and compares SHA-256,
    /// and only then wipes the plaintext. Returns false if there was nothing to do.
    /// </summary>
    public static bool EncryptInPlace(string logical)
    {
        if (!File.Exists(logical)) return false;
        if (!Vault.IsUnlocked) throw new VaultLockedException();
        byte[] expected;
        using (var src = new FileStream(logical, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var key = Vault.DataKey();
            var target = EncryptedName(logical);
            var tmp = target + ".migrate.tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var w = new WdeWriteStream(fs, key, Context(logical)))
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0) { hash.AppendData(buf, 0, n); w.Write(buf, 0, n); }
            }
            expected = hash.GetHashAndReset();
            // Verify before anything is deleted.
            using (var check = new WdeReadStream(new FileStream(tmp, FileMode.Open, FileAccess.Read), key, Context(logical), false))
            {
                var actual = SHA256.HashData(check);
                if (!VaultCrypto.Equal(actual, expected)) { File.Delete(tmp); throw new CryptographicException("The encrypted copy didn't verify; the original was kept."); }
            }
            File.Move(tmp, target, overwrite: true);
        }
        Wipe(logical);
        return true;
    }

    /// <summary>The reverse of <see cref="EncryptInPlace"/> (turning encryption off). Verified the same way.</summary>
    public static bool DecryptInPlace(string logical)
    {
        var enc = EncryptedName(logical);
        if (!File.Exists(enc)) return false;
        var tmp = logical + ".migrate.tmp";
        using (var src = OpenRead(logical))
        using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            src.CopyTo(dst, 1 << 20);
        File.Move(tmp, logical, overwrite: true);
        File.Delete(enc);
        return true;
    }

    /// <summary>
    /// A plaintext path that tools needing a real file (FFmpeg, the media player) can open. For encrypted files this is a
    /// decrypted working copy in <see cref="WorkDir"/>, deleted when WhisperDesk locks or closes.
    /// </summary>
    public static string Materialize(string logical) => IsEncrypted(logical) ? WorkDir.Decrypted(logical) : logical;

    /// <summary>
    /// A write stream that only becomes the file when <see cref="Commit"/> is called. Disposing without committing
    /// (an exception half-way) deletes the temporary file, so a partial file never replaces a good one.
    /// </summary>
    public sealed class CommitStream(Stream inner, Action commit, Action rollback) : Stream
    {
        private bool _done;

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        /// <summary>Finishes the file and puts it in place.</summary>
        public void Commit()
        {
            if (_done) return;
            _done = true;
            try { inner.Dispose(); }
            catch { rollback(); throw; }
            commit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_done)
            {
                _done = true;
                try { inner.Dispose(); } catch { }
                rollback();
            }
            base.Dispose(disposing);
        }

        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
