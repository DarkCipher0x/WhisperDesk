using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WhisperDesk.Services;

public sealed record BackupItem(string Key, string Label, string Path, bool IsFolder, string? Note = null)
{
    public long Size => IsFolder ? (Directory.Exists(Path) ? Backup.FolderSize(Path) : 0) : (File.Exists(Path) ? new FileInfo(Path).Length : 0);
}

/// <summary>
/// Full backups of the recordings library, voice library, settings, add-ons and (optionally) models — as a plain .zip or
/// an encrypted .wdbackup (AES-256-GCM in 1 MB chunks, key from the password with PBKDF2-SHA256, 600 000 iterations).
/// A manifest with the SHA-256 of every file is stored inside, so a restore can verify nothing was altered.
/// </summary>
public static class Backup
{
    public const string EncryptedExtension = ".wdbackup";
    private static readonly byte[] Magic = "WDBK1"u8.ToArray();
    private const int Iterations = 600_000, ChunkSize = 1 << 20, SaltSize = 16, NonceSize = 12, TagSize = 16;

    private static string LocalData => System.IO.Path.Combine(WhisperDesk.Services.AppPaths.LocalData);

    public static List<BackupItem> Items()
    {
        var list = new List<BackupItem>
        {
            new("library", "Recordings library (transcripts, audio, summaries)", RecordingLibrary.Root, true),
            new("voices", "Voice library (voiceprints — biometric data)", Security.SecureFile.Physical(System.IO.Path.Combine(LocalData, "voices.json")), false),
            new("settings", "Settings", System.IO.Path.Combine(LocalData, "settings.json"), false),
            new("addons", "Add-ons (program code: restore only from backups you made)", Addons.AddonManager.AddonsFolder, true),
            new("models", "Models (large; can be downloaded again)", ModelManager.ModelsDir, true, "Usually not needed."),
        };
        // With encryption on, library and voices are stored encrypted, so the backup holds ciphertext. The vault header
        // (salts and wrapped keys, never a key itself) is needed to open them on another PC.
        if (Security.Vault.IsEnabled)
            list.Insert(2, new("vault", "Encryption header (wrapped keys only; needed to open the encrypted library elsewhere)", Security.Vault.FilePath, false));
        return list;
    }

    public static long FolderSize(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", LibraryStorage.NoLinks).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }); }
        catch { return 0; }
    }

    // ---------------- create ----------------

    public static async Task<(int Files, long Bytes)> CreateAsync(string target, IReadOnlyList<BackupItem> items, string? password,
                                                                  IProgress<string> status, CancellationToken ct)
    {
        var tmp = target + ".part";
        int files = 0;
        long bytes = 0;
        var manifest = new List<object>();
        try
        {
            await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                Stream sink = file;
                GcmWriteStream? gcm = null;
                if (!string.IsNullOrEmpty(password))
                {
                    status.Report("Deriving the encryption key…");
                    var salt = RandomNumberGenerator.GetBytes(SaltSize);
                    var key = await Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32), ct);
                    file.Write(Magic);
                    file.Write(BitConverter.GetBytes(Iterations));
                    file.Write(salt);
                    sink = gcm = new GcmWriteStream(file, key);
                }
                using (var zip = new ZipArchive(sink, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var item in items)
                    {
                        var paths = item.IsFolder
                            ? (Directory.Exists(item.Path) ? Directory.EnumerateFiles(item.Path, "*", LibraryStorage.NoLinks)
                                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !Npu.WhisperOnnxModels.IsCompiledCache(f))
                                .ToList() : new List<string>())
                            : (File.Exists(item.Path) ? new List<string> { item.Path } : new List<string>());
                        foreach (var path in paths)
                        {
                            ct.ThrowIfCancellationRequested();
                            var rel = item.IsFolder ? System.IO.Path.GetRelativePath(item.Path, path) : System.IO.Path.GetFileName(path);
                            var name = $"{item.Key}/{rel.Replace('\\', '/')}";
                            bool compress = !IsMedia(path);
                            var entry = zip.CreateEntry(name, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                            entry.LastWriteTime = File.GetLastWriteTime(path);
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            long size = 0;
                            await using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
                            await using (var dst = entry.Open())
                            {
                                var buf = new byte[1 << 20];
                                int n;
                                while ((n = await src.ReadAsync(buf, ct)) > 0)
                                {
                                    hash.AppendData(buf, 0, n);
                                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                                    size += n;
                                    bytes += n;
                                }
                            }
                            manifest.Add(new { path = name, size, sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() });
                            files++;
                            if (files % 5 == 0) status.Report($"Backing up… {files:N0} files, {bytes / 1048576.0:N0} MB");
                        }
                    }
                    var info = new
                    {
                        app = $"WhisperDesk {AboutWindow.Version}", created = DateTimeOffset.Now, computer = Environment.MachineName,
                        account = $"{Environment.UserDomainName}\\{Environment.UserName}", encrypted = gcm != null,
                        items = items.Select(i => new { i.Key, i.Label, source = i.Path }), files = manifest,
                    };
                    var m = zip.CreateEntry("backup-manifest.json", CompressionLevel.Optimal);
                    await using (var w = m.Open())
                        await JsonSerializer.SerializeAsync(w, info, new JsonSerializerOptions { WriteIndented = true }, ct);
                }
                gcm?.FinishWrite();
            }
            File.Move(tmp, target, overwrite: true);
            return (files, bytes);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static bool IsMedia(string path) =>
        AudioDecoder.IsSupported(path) || path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    // ---------------- restore ----------------

    public static bool IsEncrypted(string path)
    {
        using var f = File.OpenRead(path);
        var head = new byte[Magic.Length];
        return f.Read(head) == head.Length && head.AsSpan().SequenceEqual(Magic);
    }

    /// <summary>Opens a backup as a readable zip (decrypting an encrypted one to a temporary file first).</summary>
    public static async Task<(ZipArchive Zip, string? TempFile)> OpenAsync(string path, string? password, IProgress<string> status, CancellationToken ct)
    {
        if (!IsEncrypted(path)) return (ZipFile.OpenRead(path), null);
        if (string.IsNullOrEmpty(password)) throw new UnauthorizedAccessException("This backup is encrypted: enter its password.");
        var tmp = System.IO.Path.Combine(Security.WorkDir.NewFolder("restore"), "backup.zip");
        try
        {
            await using (var src = File.OpenRead(path))
            {
                src.Position = Magic.Length;
                var iter = new byte[4]; src.ReadExactly(iter);
                var salt = new byte[SaltSize]; src.ReadExactly(salt);
                status.Report("Checking the password…");
                var key = await Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(password, salt, BitConverter.ToInt32(iter), HashAlgorithmName.SHA256, 32), ct);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                await Task.Run(() => GcmDecrypt(src, dst, key, status, ct), ct);
            }
            return (ZipFile.OpenRead(tmp), tmp);
        }
        catch (AuthenticationTagMismatchException)
        {
            try { File.Delete(tmp); } catch { }
            throw new UnauthorizedAccessException("Wrong password, or the backup file is damaged.");
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    public sealed record RestoreResult(int Restored, int Skipped, int Verified, int Mismatched, List<string> Notes);

    /// <summary>
    /// Restores the chosen parts. Library: recordings that already exist (same folder name) are skipped unless
    /// <paramref name="overwrite"/>. Voices are merged into the current voice library. Settings replace the current ones.
    /// Every file is checked against the manifest's SHA-256.
    /// </summary>
    public static async Task<RestoreResult> RestoreAsync(ZipArchive zip, IReadOnlyCollection<string> keys, bool overwrite, string libraryTarget,
                                                         IProgress<string> status, CancellationToken ct)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (zip.GetEntry("backup-manifest.json") is { } me)
        {
            await using var s = me.Open();
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("files", out var arr))
                foreach (var f in arr.EnumerateArray())
                    expected[f.GetProperty("path").GetString()!] = f.GetProperty("sha256").GetString()!;
        }
        int restored = 0, skipped = 0, verified = 0, mismatched = 0;
        var notes = new List<string>();
        var existingRecordings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The backup's vault header decides whether its encrypted files can be opened here.
        string? backupVaultId = null;
        if (zip.GetEntry("vault/vault.json") is { } ve && ve.Length < 1 << 20)
        {
            var tmpVault = System.IO.Path.Combine(Security.WorkDir.NewFolder("restore"), "vault.json");
            ve.ExtractToFile(tmpVault, overwrite: true);
            backupVaultId = Security.Vault.ReadHeader(tmpVault)?.Id;
            if (keys.Contains("vault") && backupVaultId != null && !Security.Vault.IsEnabled)
            {
                Security.Vault.Adopt(tmpVault);
                notes.Add("Encryption header restored: WhisperDesk is now locked. Unlock it with the passphrase (or recovery key) that protected this backup's library.");
            }
            Security.WorkDir.Release(System.IO.Path.GetDirectoryName(tmpVault));
        }
        bool encryptedReadable = backupVaultId != null && backupVaultId == Security.Vault.Id;
        int unreadable = 0;

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var slash = entry.FullName.IndexOf('/');
            if (slash < 0 || entry.FullName.EndsWith('/')) continue;
            var key = entry.FullName[..slash];
            var rel = entry.FullName[(slash + 1)..].Replace('/', System.IO.Path.DirectorySeparatorChar);
            if (!keys.Contains(key) || key == "vault") continue;
            bool encrypted = rel.EndsWith(Security.WdeFormat.Extension, StringComparison.OrdinalIgnoreCase);
            if (encrypted && key is "library" or "voices" && !encryptedReadable) { unreadable++; skipped++; continue; }

            string dest = key switch
            {
                "library" => System.IO.Path.Combine(libraryTarget, rel),
                "addons" => System.IO.Path.Combine(Addons.AddonManager.AddonsFolder, rel),
                "models" => System.IO.Path.Combine(ModelManager.ModelsDir, rel),
                "voices" or "settings" => System.IO.Path.Combine(Security.WorkDir.NewFolder("restore"), System.IO.Path.GetFileName(rel)),
                _ => "",
            };
            if (dest.Length == 0) continue;
            dest = System.IO.Path.GetFullPath(dest);
            // Never write outside the target folders (zip-slip protection).
            var root = key switch
            {
                "library" => libraryTarget, "addons" => Addons.AddonManager.AddonsFolder, "models" => ModelManager.ModelsDir,
                _ => System.IO.Path.GetDirectoryName(dest)!,
            };
            if (!dest.StartsWith(System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }

            if (key == "library" && !overwrite)
            {
                // Skip whole recordings that already exist.
                var recDir = System.IO.Path.GetDirectoryName(dest)!;
                bool isMeta = rel.EndsWith("metadata.json", StringComparison.OrdinalIgnoreCase) || rel.EndsWith("metadata.json.wde", StringComparison.OrdinalIgnoreCase);
                if (RecordingLibrary.IsRecordingDir(recDir) && !existingRecordings.Contains(recDir)) { skipped++; continue; }
                if (isMeta) existingRecordings.Add(recDir);
            }
            if (key is "addons" or "models" && File.Exists(dest) && !overwrite) { skipped++; continue; }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = entry.Open())
            await using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0) { hash.AppendData(buf, 0, n); await dst.WriteAsync(buf.AsMemory(0, n), ct); }
            }
            File.SetLastWriteTime(dest, entry.LastWriteTime.LocalDateTime);
            if (expected.TryGetValue(entry.FullName, out var sha))
            {
                if (string.Equals(sha, Convert.ToHexString(hash.GetHashAndReset()), StringComparison.OrdinalIgnoreCase)) verified++;
                else { mismatched++; notes.Add("Checksum mismatch: " + entry.FullName); }
            }
            if (key == "voices")
            {
                try
                {
                    var json = encrypted
                        ? System.Text.Encoding.UTF8.GetString(Security.SecureFile.ReadWithContext(dest, "app/voices.json"))
                        : await File.ReadAllTextAsync(dest, ct);
                    var added = VoiceLibrary.Shared.MergeFromJson(json);
                    notes.Add($"Voice library: {added} voice(s) added or updated.");
                }
                catch (Exception ex) { notes.Add("Voice library could not be merged: " + ex.Message); }
                Security.WorkDir.Release(System.IO.Path.GetDirectoryName(dest));
            }
            if (key == "settings")
            {
                notes.AddRange(RestoreSettings(dest));
                Security.WorkDir.Release(System.IO.Path.GetDirectoryName(dest));
            }
            if (key == "addons" && dest.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !notes.Any(n => n.StartsWith("Add-ons:")))
                notes.Add("Add-ons: restored add-ons stay off until you review and approve them again in Tools → Add-ons.");
            restored++;
            if (restored % 5 == 0) status.Report($"Restoring… {restored:N0} files");
        }
        if (unreadable > 0)
            notes.Add($"{unreadable:N0} encrypted file(s) were skipped: they belong to a different encryption vault than the one on this PC. " +
                      "Restore them on a PC without encryption set up (with the encryption header), or turn encryption off here first.");
        return new RestoreResult(restored, skipped, verified, mismatched, notes);
    }

    /// <summary>
    /// Restores settings from a backup without trusting it with security decisions: add-on approvals and on/off states,
    /// the remote-AI permission and the encrypted settings blob are kept from this PC, and a non-local AI server address
    /// is reset to this PC. Applies on the next start.
    /// </summary>
    private static List<string> RestoreSettings(string path)
    {
        var notes = new List<string>();
        var restored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("The settings in the backup can't be read.");
        var current = AppSettings.Current;
        restored.AddonApprovals = current.AddonApprovals;
        restored.AddonsEnabled = current.AddonsEnabled;
        restored.AllowRemoteAi = current.AllowRemoteAi;
        restored.Protected = current.Protected;
        restored.LibraryPath = current.LibraryPath;
        restored.LibraryDefaultChecked = true;
        if (Uri.TryCreate(restored.AiUrl, UriKind.Absolute, out var ai) && !Security.NetworkGate.IsLoopback(ai.Host))
        {
            notes.Add($"The backup's AI server address ({ai.Host}) is not this PC, so it was reset to {AiReport.DefaultUrl}.");
            restored.AiUrl = AiReport.DefaultUrl;
        }
        // This PC's encrypted settings stay; Save() writes the file (and, with encryption on, signs it).
        restored.InitialPrompt = current.InitialPrompt;
        restored.ExpandedFolders = current.ExpandedFolders;
        restored.LastLibraryFolder = current.LastLibraryFolder;
        restored.ModelDownloadPolicy = current.ModelDownloadPolicy;
        restored.Save();
        notes.Add("Settings restored (add-on approvals, security choices and the library location were kept): restart WhisperDesk to apply them.");
        return notes;
    }

    // ---------------- AES-256-GCM chunked stream ----------------

    /// <summary>Each 1 MB chunk: [4-byte length][ciphertext][16-byte tag]; nonce = 4 random bytes + 8-byte counter.
    /// The final chunk is marked by a length with the top bit set, so truncation is detected.</summary>
    private sealed class GcmWriteStream : Stream
    {
        private readonly Stream _out;
        private readonly AesGcm _aes;
        private readonly byte[] _prefix = RandomNumberGenerator.GetBytes(4);
        private readonly byte[] _buf = new byte[ChunkSize];
        private int _fill;
        private ulong _counter;
        private bool _finished;

        public GcmWriteStream(Stream output, byte[] key)
        {
            _out = output;
            _aes = new AesGcm(key, TagSize);
            _out.Write(_prefix);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                int take = Math.Min(data.Length, ChunkSize - _fill);
                data[..take].CopyTo(_buf.AsSpan(_fill));
                _fill += take;
                data = data[take..];
                if (_fill == ChunkSize) Emit(final: false);
            }
        }

        private void Emit(bool final)
        {
            var nonce = new byte[NonceSize];
            _prefix.CopyTo(nonce, 0);
            BitConverter.TryWriteBytes(nonce.AsSpan(4), _counter++);
            var cipher = new byte[_fill];
            var tag = new byte[TagSize];
            uint len = (uint)_fill | (final ? 0x8000_0000u : 0);
            _aes.Encrypt(nonce, _buf.AsSpan(0, _fill), cipher, tag, BitConverter.GetBytes(len));
            _out.Write(BitConverter.GetBytes(len));
            _out.Write(cipher);
            _out.Write(tag);
            _fill = 0;
        }

        public void FinishWrite()
        {
            if (_finished) return;
            Emit(final: true);
            _finished = true;
            _out.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { FinishWrite(); _aes.Dispose(); }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static void GcmDecrypt(Stream src, Stream dst, byte[] key, IProgress<string> status, CancellationToken ct)
    {
        using var aes = new AesGcm(key, TagSize);
        var prefix = new byte[4];
        src.ReadExactly(prefix);
        var lenBuf = new byte[4];
        var tag = new byte[TagSize];
        var nonce = new byte[NonceSize];
        prefix.CopyTo(nonce, 0);
        ulong counter = 0;
        long done = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (src.Read(lenBuf) < 4) throw new InvalidDataException("The backup file is incomplete (it was cut short).");
            uint len = BitConverter.ToUInt32(lenBuf);
            bool final = (len & 0x8000_0000u) != 0;
            int size = (int)(len & 0x7FFF_FFFFu);
            if (size > ChunkSize) throw new InvalidDataException("The backup file is damaged.");
            var cipher = new byte[size];
            src.ReadExactly(cipher);
            src.ReadExactly(tag);
            BitConverter.TryWriteBytes(nonce.AsSpan(4), counter++);
            var plain = new byte[size];
            aes.Decrypt(nonce, cipher, tag, plain, lenBuf);
            dst.Write(plain);
            done += size;
            if (counter % 64 == 0) status.Report($"Decrypting… {done / 1048576.0:N0} MB");
            if (final) break;
        }
    }
}
