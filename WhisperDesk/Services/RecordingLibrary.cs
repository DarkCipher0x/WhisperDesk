using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperDesk.Services.Security;

namespace WhisperDesk.Services;

public sealed class SpeakerMeta
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Sex { get; set; } = Sexes.Unspecified;
    public double TalkSeconds { get; set; }
    public double? PitchHz { get; set; }
    public float[]? Voiceprint { get; set; }
}

public sealed class LineMeta
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public int Speaker { get; set; } = -1;
    public string? Language { get; set; }
    /// <summary>Text as originally transcribed (only when edited).</summary>
    public string? OriginalText { get; set; }
    /// <summary>Audit trail of corrections (only when edited).</summary>
    public List<WhisperDesk.LineEdit>? Edits { get; set; }
}

/// <summary>metadata.json for one recording in the library.</summary>
public sealed class RecordingMeta
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Type { get; set; } = "Other";
    public DateTime Created { get; set; } = DateTime.Now;
    public double DurationSeconds { get; set; }
    /// <summary>"Live · VoiceMeeter Out B1" or "File · C:\…\meeting.mp4".</summary>
    public string Source { get; set; } = "";
    public string? Language { get; set; }
    public string? Model { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Notes { get; set; } = "";
    /// <summary>Audio/video file name inside the recording folder, or null when only the transcript was kept.</summary>
    public string? MediaFile { get; set; }
    /// <summary>Original media path when it was referenced rather than copied.</summary>
    public string? ExternalMedia { get; set; }
    public List<SpeakerMeta> Speakers { get; set; } = new();
    public int LineCount { get; set; }
    public string App { get; set; } = "WhisperDesk by darkcipher0x";
    /// <summary>
    /// Encrypted libraries only: the library folder this recording is filed in. On disk every encrypted recording sits in
    /// r\&lt;random id&gt;, so folder names and titles aren't visible; the folder lives here, inside the encrypted metadata.
    /// </summary>
    public string? LibraryFolder { get; set; }

    // Filled when loaded — not saved.
    [JsonIgnore] public string Dir { get; set; } = "";
    [JsonIgnore] public string Folder { get; set; } = "";
    [JsonIgnore] public string Duration => TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    [JsonIgnore] public string SpeakerNames => string.Join(", ", Speakers.Select(s => string.IsNullOrWhiteSpace(s.Name) ? $"Speaker {s.Id + 1}" : s.Name));
    [JsonIgnore] public string TagText => string.Join(", ", Tags);
    /// <summary>
    /// The media as stored (plaintext name; may be stored encrypted). MediaFile comes from a file and is untrusted, so
    /// only a bare file name inside the recording folder is accepted.
    /// </summary>
    [JsonIgnore] public string MediaLogical =>
        MediaFile != null && MediaFile.Length > 0 && Path.GetFileName(MediaFile) == MediaFile && MediaFile is not ("." or "..")
            ? Path.Combine(Dir, MediaFile) : ExternalMedia ?? "";
    /// <summary>A plaintext path to the media for players and FFmpeg (a decrypted working copy for encrypted recordings).</summary>
    [JsonIgnore] public string MediaPath => MediaLogical.Length > 0 && SecureFile.IsEncrypted(MediaLogical) ? SecureFile.Materialize(MediaLogical) : MediaLogical;
    [JsonIgnore] public bool HasMedia => MediaLogical.Length > 0 && SecureFile.Exists(MediaLogical);
    [JsonIgnore] public string MediaText => MediaFile != null ? "✓" : ExternalMedia != null ? "linked" : "—";
    [JsonIgnore] public string FolderDisplay => Folder.Length == 0 ? "(root)" : Folder.Replace("\\", " / ");
    /// <summary>True for recordings stored in the encrypted layout.</summary>
    [JsonIgnore] public bool IsEncrypted => SecureFile.IsEncrypted(Path.Combine(Dir, "metadata.json"));
}

/// <summary>
/// Saved recordings. Two layouts, both read at any time (a library half-way through conversion keeps working):
/// <code>
/// plain:      &lt;Library&gt;\&lt;Folder&gt;\&lt;yyyy-MM-dd HH.mm – Title&gt;\{metadata.json, transcript.json, transcript.txt, transcript.srt, audio.wav}
/// encrypted:  &lt;Library&gt;\r\&lt;random id&gt;\{metadata.json.wde, transcript.json.wde, …}   + folders.json.wde (the folder tree)
/// </code>
/// With encryption on, new recordings use the encrypted layout, so titles and folder names are never on disk in plaintext.
/// </summary>
public static class RecordingLibrary
{
    public static readonly string[] Types = { "Meeting", "Interview", "Podcast", "Call", "Lecture", "Live", "Voice note", "Video", "Other" };

    private const string MetaFile = "metadata.json";
    private const string TranscriptFile = "transcript.json";
    public const string EncryptedDir = "r";
    private const string FolderIndex = "folders.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Root
    {
        get
        {
            var root = ResolveRoot(AppSettings.Current);
            Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>
    /// Settings → installer choice → %LOCALAPPDATA%\WhisperDesk\Library. Until <see cref="PinPreviousDefault"/> has run, a
    /// profile from an older version keeps using the folder that version used, so its recordings never vanish.
    /// </summary>
    private static string ResolveRoot(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.LibraryPath)) return Environment.ExpandEnvironmentVariables(s.LibraryPath.Trim().Trim('"'));
        if (!s.LibraryDefaultChecked && AppSettings.ExistingProfile
            && LibraryStorage.PreviousDefault(InstallerIntegration.DefaultLibraryPath, LibraryStorage.LegacyDefault) is { } previous)
            return previous;
        return InstallerIntegration.DefaultLibraryPath ?? LibraryStorage.LocalDefault;
    }

    /// <summary>
    /// Run once per profile at start-up. An existing profile whose library was the old default (Documents\WhisperDesk Library,
    /// or the installer's folder) gets that path written into settings, so it stays exactly where it is. A new profile gets the
    /// local default; recordings that appear in Documents later (e.g. synced down by OneDrive) are offered, never adopted.
    /// </summary>
    public static void PinPreviousDefault()
    {
        var s = AppSettings.Current;
        if (s.LibraryDefaultChecked) return;
        if (string.IsNullOrWhiteSpace(s.LibraryPath) && AppSettings.ExistingProfile
            && LibraryStorage.PreviousDefault(InstallerIntegration.DefaultLibraryPath, LibraryStorage.LegacyDefault) is { } previous)
            s.LibraryPath = previous;
        s.LibraryDefaultChecked = true;
        s.Save();
    }

    /// <summary>
    /// Recordings in the old default folder (Documents\WhisperDesk Library) that are not part of the current library, e.g. synced
    /// down from another PC by OneDrive. Null when there are none or the user dismissed the notice for that folder.
    /// </summary>
    public static (string Dir, int Count)? StrayLibrary()
    {
        var legacy = LibraryStorage.LegacyDefault;
        if (LibraryStorage.SamePath(legacy, Root) || AppSettings.Current.DismissedStorageWarnings.Contains("stray|" + legacy)) return null;
        var n = LibraryStorage.CountRecordings(legacy);
        return n > 0 ? (legacy, n) : null;
    }

    /// <summary>
    /// Moves the whole library (copy, verify, then delete) to <paramref name="target"/> and switches the setting to it.
    /// Nothing is switched or deleted if a copy fails. Encrypted files stay valid: they are bound to their path inside the library.
    /// </summary>
    public static async Task<LibraryStorage.MoveResult> MoveLibraryAsync(string target, IProgress<string>? status, CancellationToken ct)
    {
        var source = Root;
        var result = await LibraryStorage.MoveAsync(source, target, status, ct);
        AppSettings.Current.LibraryPath = target;
        AppSettings.Current.LibraryDefaultChecked = true;
        AppSettings.Current.Save();
        return result;
    }

    public static bool IsRecordingDir(string dir) => SecureFile.Exists(Path.Combine(dir, MetaFile));

    private static bool UseEncryptedLayout => Vault.IsEnabled;

    // ---------------- folders ----------------

    private static string FolderIndexPath => Path.Combine(Root, FolderIndex);

    /// <summary>The encrypted layout's folder tree (empty folders included).</summary>
    private static List<string> ReadFolderIndex()
    {
        try { return SecureFile.Exists(FolderIndexPath) ? JsonSerializer.Deserialize<List<string>>(SecureFile.ReadAllText(FolderIndexPath)) ?? new() : new(); }
        catch (VaultLockedException) { throw; }
        catch { return new(); }
    }

    private static void WriteFolderIndex(IEnumerable<string> folders) =>
        SecureFile.WriteAllText(FolderIndexPath, JsonSerializer.Serialize(folders.Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()));

    /// <summary>A safe relative folder path ("Clients\Acme"): each part sanitised, never rooted or climbing out.</summary>
    public static string CleanFolder(string? folder) =>
        string.Join("\\", (folder ?? "").Split('\\', '/').Where(p => p.Trim().Length > 0 && p.Trim() is not ("." or "..")).Select(Sanitize));

    /// <summary>All user folders (relative paths), excluding recording folders themselves.</summary>
    public static List<string> Folders()
    {
        var root = Root;
        var result = new List<string>();
        void Walk(string dir)
        {
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                if (IsRecordingDir(sub)) continue;
                if (dir == root && Path.GetFileName(sub).Equals(EncryptedDir, StringComparison.OrdinalIgnoreCase)) continue;   // encrypted store
                result.Add(Path.GetRelativePath(root, sub));
                Walk(sub);
            }
        }
        Walk(root);
        if (SecureFile.Exists(FolderIndexPath) || UseEncryptedLayout)
        {
            var virtualFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (SecureFile.CanRead(FolderIndexPath)) foreach (var f in ReadFolderIndex()) virtualFolders.Add(f);
            foreach (var m in AllEncrypted()) if (!string.IsNullOrEmpty(m.Folder)) virtualFolders.Add(m.Folder);
            // Parent folders of every folder exist too.
            foreach (var f in virtualFolders.ToList())
                for (var p = Path.GetDirectoryName(f); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p)) virtualFolders.Add(p);
            result = result.Union(virtualFolders, StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        return result;
    }

    public static string CreateFolder(string parent, string name)
    {
        var rel = CleanFolder(Path.Combine(parent ?? "", Sanitize(name)));
        if (UseEncryptedLayout)
        {
            WriteFolderIndex(ReadFolderIndex().Append(rel));
            return rel;
        }
        var dir = Path.Combine(Root, rel);
        Directory.CreateDirectory(dir);
        return Path.GetRelativePath(Root, dir);
    }

    /// <summary>Renames a folder (and moves everything filed in it). Returns the new relative path.</summary>
    public static string RenameFolder(string folder, string newName)
    {
        folder = CleanFolder(folder);
        var parent = Path.GetDirectoryName(folder) ?? "";
        var target = CleanFolder(Path.Combine(parent, Sanitize(newName)));
        if (folder.Length == 0 || string.Equals(folder, target, StringComparison.OrdinalIgnoreCase)) return folder;
        var plain = Path.Combine(Root, folder);
        if (Directory.Exists(plain)) Directory.Move(plain, Path.Combine(Root, target));
        if (SecureFile.Exists(FolderIndexPath) || UseEncryptedLayout)
        {
            string Re(string f) => f.Equals(folder, StringComparison.OrdinalIgnoreCase) ? target
                : f.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) ? target + f[folder.Length..] : f;
            WriteFolderIndex(ReadFolderIndex().Select(Re));
            foreach (var m in AllEncrypted().Where(m => m.Folder.Length > 0 && Re(m.Folder) != m.Folder))
            {
                m.LibraryFolder = Re(m.Folder);
                m.Folder = m.LibraryFolder;
                SaveMeta(m);
            }
        }
        return target;
    }

    public static void DeleteFolder(string folder)
    {
        folder = CleanFolder(folder);
        if (folder.Length == 0) return;
        var dir = Path.Combine(Root, folder);
        if (Directory.Exists(dir)) DeleteTree(dir);
        if (SecureFile.Exists(FolderIndexPath) || UseEncryptedLayout)
        {
            bool Inside(string f) => f.Equals(folder, StringComparison.OrdinalIgnoreCase) || f.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
            foreach (var m in AllEncrypted().Where(m => Inside(m.Folder))) Delete(m);
            WriteFolderIndex(ReadFolderIndex().Where(f => !Inside(f)));
        }
    }

    // ---------------- recordings ----------------

    public static List<RecordingMeta> All()
    {
        var root = Root;
        var list = new List<RecordingMeta>();
        var encRoot = Path.Combine(root, EncryptedDir);
        foreach (var meta in Directory.EnumerateFiles(root, MetaFile, LibraryStorage.NoLinks))
        {
            try
            {
                var dir = Path.GetDirectoryName(meta)!;
                if (dir.StartsWith(encRoot + "\\", StringComparison.OrdinalIgnoreCase) && SecureFile.IsEncrypted(meta)) continue;
                var m = JsonSerializer.Deserialize<RecordingMeta>(File.ReadAllText(meta));
                if (m == null) continue;
                m.Dir = dir;
                var parent = Path.GetDirectoryName(m.Dir)!;
                m.Folder = Path.GetRelativePath(root, parent) is "." ? "" : Path.GetRelativePath(root, parent);
                list.Add(m);
            }
            catch { /* skip unreadable entries */ }
        }
        list.AddRange(AllEncrypted());
        return list.OrderByDescending(m => m.Created).ToList();
    }

    /// <summary>Recordings in the encrypted layout (none while locked: they can't be read).</summary>
    private static List<RecordingMeta> AllEncrypted()
    {
        var list = new List<RecordingMeta>();
        var encRoot = Path.Combine(Root, EncryptedDir);
        if (!Directory.Exists(encRoot) || !Vault.IsUnlocked) return list;
        foreach (var dir in Directory.EnumerateDirectories(encRoot))
        {
            var meta = Path.Combine(dir, MetaFile);
            if (!SecureFile.Exists(meta)) continue;
            try
            {
                var m = JsonSerializer.Deserialize<RecordingMeta>(SecureFile.ReadAllText(meta));
                if (m == null) continue;
                m.Dir = dir;
                m.Folder = CleanFolder(m.LibraryFolder);
                list.Add(m);
            }
            catch { /* damaged or from another vault: skipped */ }
        }
        return list;
    }

    /// <summary>Plaintext recordings still in the library while encryption is on (to be converted).</summary>
    public static int PlaintextCount()
    {
        try { return Directory.EnumerateFiles(Root, MetaFile, LibraryStorage.NoLinks).Count(); }
        catch { return 0; }
    }

    /// <summary>Saves a transcript (and optionally its media) as a new recording. Returns the stored metadata.</summary>
    public static RecordingMeta Save(TranscriptResult result, RecordingMeta meta, string folder,
                                     string? mediaToMove, string? mediaToCopy, string? mediaToLink)
    {
        folder = CleanFolder(folder);
        string dir;
        if (UseEncryptedLayout)
        {
            // Opaque name: no title, date or folder on disk.
            dir = Path.Combine(Root, EncryptedDir, meta.Id);
            for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(Root, EncryptedDir, Guid.NewGuid().ToString("N"));
            meta.LibraryFolder = folder;
            if (folder.Length > 0) WriteFolderIndex(ReadFolderIndex().Append(folder));
        }
        else
        {
            var parent = Path.Combine(Root, folder);
            Directory.CreateDirectory(parent);
            var baseName = $"{meta.Created:yyyy-MM-dd HH.mm} – {Sanitize(meta.Title)}";
            dir = Path.Combine(parent, baseName);
            for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(parent, $"{baseName} ({i})");
        }
        Directory.CreateDirectory(dir);

        if (mediaToMove != null && SecureFile.Exists(mediaToMove))
        {
            meta.MediaFile = "audio" + Path.GetExtension(mediaToMove);
            SecureFile.Move(mediaToMove, Path.Combine(dir, meta.MediaFile));   // re-encrypted for its new place (or a plain move)
        }
        else if (mediaToCopy != null && File.Exists(mediaToCopy))
        {
            meta.MediaFile = "media" + Path.GetExtension(mediaToCopy);
            SecureFile.Import(mediaToCopy, Path.Combine(dir, meta.MediaFile));
        }
        else if (mediaToLink != null)
        {
            meta.ExternalMedia = mediaToLink;
        }

        meta.Dir = dir;
        meta.Folder = folder;
        WriteTranscript(meta, result);
        result.Library = meta;
        result.OutputDir = dir;
        if (meta.HasMedia) result.MediaPath = meta.MediaLogical;
        return meta;
    }

    /// <summary>Rewrites metadata + transcript files (after renaming speakers, editing details…).</summary>
    public static void WriteTranscript(RecordingMeta meta, TranscriptResult result)
    {
        meta.Speakers = result.Speakers.Select(s => new SpeakerMeta
        {
            Id = s.Id, Name = s.Name, Sex = s.Sex, TalkSeconds = s.TalkTime.TotalSeconds,
            PitchHz = s.PitchHz, Voiceprint = s.Embedding.Length > 0 ? s.Embedding : null,
        }).ToList();
        meta.LineCount = result.Lines.Count;
        meta.Language ??= result.Lines.FirstOrDefault(l => l.Language != null)?.Language;
        if (meta.DurationSeconds <= 0 && result.Lines.Count > 0) meta.DurationSeconds = result.Lines[^1].End.TotalSeconds;

        var lines = result.Lines.Select(l => new LineMeta
        {
            Start = l.Start.TotalSeconds, End = l.End.TotalSeconds, Text = l.Text, Speaker = l.Speaker?.Id ?? -1, Language = l.Language,
            OriginalText = l.IsEdited ? l.OriginalText : null, Edits = l.IsEdited ? l.Edits.ToList() : null,
        }).ToList();
        SecureFile.WriteAllText(Path.Combine(meta.Dir, TranscriptFile), JsonSerializer.Serialize(lines, Json));
        SaveMeta(meta);

        // Human-readable copies next to it (encrypted too when encryption is on).
        SecureFile.WriteAllText(Path.Combine(meta.Dir, "transcript.txt"), result.Render().Replace("\n", Environment.NewLine));
        SecureFile.WriteAllText(Path.Combine(meta.Dir, "transcript.srt"), TranscriptWriter.ToSrt(result));
    }

    /// <summary>Reads a recording folder (library entry or an unpacked .wdsession).</summary>
    public static RecordingMeta ReadMeta(string dir)
    {
        var m = JsonSerializer.Deserialize<RecordingMeta>(SecureFile.ReadAllText(Path.Combine(dir, MetaFile)))
                ?? throw new InvalidDataException("metadata.json is empty.");
        m.Dir = dir;
        return m;
    }

    public static void SaveMeta(RecordingMeta meta)
    {
        // Encrypted layout: the folder is kept inside the (encrypted) metadata.
        if (SecureFile.IsEncrypted(Path.Combine(meta.Dir, MetaFile)) || (UseEncryptedLayout && IsInEncryptedStore(meta.Dir)))
            meta.LibraryFolder = CleanFolder(meta.Folder);
        SecureFile.WriteAllText(Path.Combine(meta.Dir, MetaFile), JsonSerializer.Serialize(meta, Json));
    }

    private static bool IsInEncryptedStore(string dir) =>
        Path.GetFullPath(dir).StartsWith(Path.GetFullPath(Path.Combine(Root, EncryptedDir)) + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The transcript as JSON text (for search, reports and the "edited" count).</summary>
    public static string ReadTranscriptJson(RecordingMeta meta) => SecureFile.ReadAllText(Path.Combine(meta.Dir, TranscriptFile));

    /// <summary>The plain-text transcript copy, or "" when there is none.</summary>
    public static string ReadTranscriptText(RecordingMeta meta)
    {
        var path = Path.Combine(meta.Dir, "transcript.txt");
        return SecureFile.Exists(path) ? SecureFile.ReadAllText(path) : "";
    }

    /// <summary>Loads a stored recording back into an editable transcript.</summary>
    public static TranscriptResult Load(RecordingMeta meta)
    {
        var result = new TranscriptResult
        {
            SourceFile = Path.Combine(meta.Dir, "transcript.json"), OutputDir = meta.Dir, Library = meta,
            MediaPath = meta.HasMedia ? meta.MediaLogical : null,
        };
        foreach (var s in meta.Speakers)
        {
            var info = new SpeakerInfo(new DetectedSpeaker
            {
                Id = s.Id, TalkSeconds = s.TalkSeconds, MedianPitchHz = s.PitchHz, Embedding = s.Voiceprint ?? Array.Empty<float>(),
            });
            info.Name = s.Name;
            info.Sex = s.Sex;
            result.Speakers.Add(info);
        }
        var lines = JsonSerializer.Deserialize<List<LineMeta>>(ReadTranscriptJson(meta)) ?? new();
        foreach (var l in lines)
        {
            var line = new TranscriptLine(TimeSpan.FromSeconds(l.Start), TimeSpan.FromSeconds(l.End), l.Text, l.Language, 0,
                                          result.Speakers.FirstOrDefault(s => s.Id == l.Speaker));
            if (l.Edits is { Count: > 0 }) line.RestoreHistory(l.OriginalText ?? l.Edits[0].OldText, l.Edits);
            result.Lines.Add(line);
        }
        return result;
    }

    public static void Move(RecordingMeta meta, string folder)
    {
        folder = CleanFolder(folder);
        if (meta.IsEncrypted || IsInEncryptedStore(meta.Dir))
        {
            meta.Folder = folder;
            meta.LibraryFolder = folder;
            SaveMeta(meta);
            if (folder.Length > 0) WriteFolderIndex(ReadFolderIndex().Append(folder));
            return;
        }
        var target = Path.Combine(Root, folder, Path.GetFileName(meta.Dir));
        if (string.Equals(target, meta.Dir, StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(meta.Dir, target);
        meta.Dir = target;
        meta.Folder = folder;
    }

    public static void Delete(RecordingMeta meta)
    {
        if (IsRecordingDir(meta.Dir)) DeleteTree(meta.Dir);
    }

    /// <summary>Deletes a folder tree: plaintext files are overwritten first, links are removed without being followed.</summary>
    private static void DeleteTree(string dir) => WorkDir.WipeTree(dir);

    // ---------------- converting between layouts ----------------

    /// <summary>
    /// Moves one plaintext recording into the encrypted layout: every file is encrypted into r\&lt;id&gt;, checked by decrypting
    /// it again, and only then is the plaintext folder wiped. Safe to repeat after an interruption.
    /// </summary>
    public static void EncryptRecording(RecordingMeta meta)
    {
        if (!Vault.IsUnlocked) throw new VaultLockedException();
        if (IsInEncryptedStore(meta.Dir)) { foreach (var f in Directory.EnumerateFiles(meta.Dir, "*", LibraryStorage.NoLinks).Where(f => !f.EndsWith(WdeFormat.Extension))) SecureFile.EncryptInPlace(f); return; }
        var target = Path.Combine(Root, EncryptedDir, meta.Id);
        if (SecureFile.Exists(Path.Combine(target, MetaFile))) target = Path.Combine(Root, EncryptedDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(meta.Dir, "*", LibraryStorage.NoLinks))
        {
            var rel = Path.GetRelativePath(meta.Dir, file);
            if (rel.EndsWith(WdeFormat.Extension, StringComparison.OrdinalIgnoreCase) || rel.Equals(MetaFile, StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(target, rel);
            SecureFile.Import(file, dest);
            // Verify: the encrypted copy must decrypt to exactly the original bytes.
            using (var a = File.OpenRead(file)) using (var b = SecureFile.OpenRead(dest))
                if (!VaultCrypto.Equal(System.Security.Cryptography.SHA256.HashData(a), System.Security.Cryptography.SHA256.HashData(b)))
                    throw new IOException($"Encrypting {rel} didn't verify; the original was kept.");
        }
        var folder = meta.Folder;
        var oldDir = meta.Dir;
        meta.Dir = target;
        meta.Folder = folder;
        meta.LibraryFolder = CleanFolder(folder);
        SaveMeta(meta);   // last: the recording only "exists" in the new place once its metadata is there
        if (folder.Length > 0) WriteFolderIndex(ReadFolderIndex().Append(CleanFolder(folder)));
        DeleteTree(oldDir);
    }

    /// <summary>The reverse, when encryption is turned off: back to a readable folder named after the date and title.</summary>
    public static void DecryptRecording(RecordingMeta meta)
    {
        if (!IsInEncryptedStore(meta.Dir)) return;
        var parent = Path.Combine(Root, CleanFolder(meta.Folder));
        Directory.CreateDirectory(parent);
        var baseName = $"{meta.Created:yyyy-MM-dd HH.mm} – {Sanitize(meta.Title)}";
        var target = Path.Combine(parent, baseName);
        for (int i = 2; Directory.Exists(target); i++) target = Path.Combine(parent, $"{baseName} ({i})");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(meta.Dir, "*", LibraryStorage.NoLinks))
        {
            var logical = file.EndsWith(WdeFormat.Extension, StringComparison.OrdinalIgnoreCase) ? file[..^WdeFormat.Extension.Length] : file;
            var rel = Path.GetRelativePath(meta.Dir, logical);
            if (rel.Equals(MetaFile, StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using (var src = SecureFile.OpenRead(logical))
            using (var dst = File.Create(dest))
                src.CopyTo(dst, 1 << 20);
        }
        var oldDir = meta.Dir;
        meta.LibraryFolder = null;
        meta.Dir = target;
        File.WriteAllText(Path.Combine(target, MetaFile), JsonSerializer.Serialize(meta, Json));
        Directory.Delete(oldDir, recursive: true);
    }

    /// <summary>
    /// A safe file/folder name from untrusted text (titles, add-on ids, shared session names): no path separators or
    /// invalid characters, no "." / ".." / leading dots, no reserved device names (CON, NUL, COM1…), at most 80 characters.
    /// </summary>
    public static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "").Select(c => bad.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim().Trim('.').Trim();
        if (clean.Length > 80) clean = clean[..80].TrimEnd('.', ' ');
        if (clean.Length == 0) return "Untitled";
        var stem = clean.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            clean = "_" + clean;
        return clean;
    }
}
