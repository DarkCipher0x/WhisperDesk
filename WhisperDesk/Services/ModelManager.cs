using System.IO;
using Whisper.net.Ggml;

namespace WhisperDesk.Services;

/// <param name="Type">Built-in Whisper model, or null for a custom (imported) model file.</param>
/// <param name="CustomPath">Full path of an imported model file.</param>
public sealed record ModelInfo(GgmlType? Type, string Name, string Size, string Notes, string? CustomPath = null)
{
    public bool IsCustom => CustomPath != null;
    /// <summary>Saved in settings to remember the choice.</summary>
    public string Key => Type?.ToString() ?? "custom:" + CustomPath;
    public override string ToString() => IsCustom ? $"★ {Name}  ({Size}) - custom" : $"{Name}  ({Size}) - {Notes}";
}

/// <summary>Downloads and stores Whisper GGML models under %LOCALAPPDATA%\WhisperDesk\models.</summary>
public static class ModelManager
{
    public static readonly ModelInfo[] Models =
    {
        new(GgmlType.Tiny,         "Tiny",           "75 MB",  "fastest, rough"),
        new(GgmlType.Base,         "Base",           "142 MB", "fast, basic"),
        new(GgmlType.Small,        "Small",          "466 MB", "good"),
        new(GgmlType.Medium,       "Medium",         "1.5 GB", "very good"),
        new(GgmlType.LargeV3Turbo, "Large-v3 Turbo", "1.6 GB", "best speed/quality (recommended)"),
        new(GgmlType.LargeV3,      "Large-v3",       "3.1 GB", "most accurate, slowest"),
    };

    public static string DefaultModelsDir => Path.Combine(WhisperDesk.Services.AppPaths.LocalData, "models");

    /// <summary>Current models folder: File → Settings → Tools &amp; storage, or the default.</summary>
    public static string ModelsDir =>
        !string.IsNullOrWhiteSpace(AppSettings.Current.ModelsPath) ? AppSettings.Current.ModelsPath.Trim()
        : InstallerIntegration.DefaultModelsPath ?? DefaultModelsDir;

    /// <summary>
    /// Moves every model file (Whisper + speakers) from the current folder to <paramref name="target"/>, then
    /// switches the setting over. Files that can't be deleted from the old folder (in use) are left behind and counted.
    /// </summary>
    public static async Task<(int Moved, long Bytes, int LeftBehind)> MigrateAsync(string target, IProgress<string> status, CancellationToken ct)
    {
        var source = Path.GetFullPath(ModelsDir);
        target = Path.GetFullPath(target);
        if (string.Equals(source.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That is already the models folder.");
        if (target.StartsWith(source.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The new folder can't be inside the current models folder.");

        Directory.CreateDirectory(target);
        var files = Directory.Exists(source)
            ? Directory.EnumerateFiles(source, "*", LibraryStorage.NoLinks).Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<string>();
        long total = files.Sum(f => new FileInfo(f).Length), done = 0;
        var drive = new DriveInfo(Path.GetPathRoot(target)!);
        if (drive.IsReady && drive.AvailableFreeSpace < total + (64L << 20))
            throw new IOException($"Not enough free space on {drive.Name} ({drive.AvailableFreeSpace / 1048576:N0} MB free, {total / 1048576:N0} MB needed).");

        int moved = 0, left = 0;
        var buf = new byte[4 << 20];
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tmp = dest + ".part";
            await using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            await using (var dst = File.Create(tmp))
            {
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;
                    status.Report($"Moving models… {Path.GetFileName(file)}  {done / 1048576:N0} / {total / 1048576:N0} MB");
                }
            }
            File.Move(tmp, dest, overwrite: true);
            moved++;
        }

        // Everything is safely copied: switch over, then clean up the old folder.
        AppSettings.Current.ModelsPath = target;
        AppSettings.Current.Save();
        foreach (var file in files)
        {
            try { File.Delete(file); } catch { left++; }
        }
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(source, "*", LibraryStorage.NoLinks).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            if (Directory.Exists(source) && !Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
        }
        catch { }
        return (moved, total, left);
    }

    public static string PathFor(ModelInfo model) => model.CustomPath ??
        Path.Combine(ModelsDir, $"ggml-{model.Type.ToString()!.ToLowerInvariant()}.bin");

    /// <summary>Folder for imported models (inside the models folder, so "Move models to…" takes them along).</summary>
    public static string CustomDir => Path.Combine(ModelsDir, "custom");

    public static readonly string[] CustomExtensions = { ".bin", ".gguf" };

    /// <summary>Built-in models, then imported ones (copied into <see cref="CustomDir"/> or linked in place).</summary>
    public static List<ModelInfo> All()
    {
        var list = Models.ToList();
        var paths = new List<string>();
        try
        {
            if (Directory.Exists(CustomDir))
                paths.AddRange(Directory.EnumerateFiles(CustomDir).Where(f => CustomExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
        }
        catch { }
        paths.AddRange(AppSettings.Current.CustomModelPaths.Where(File.Exists));
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Path.GetFileName))
        {
            long bytes = 0;
            try { bytes = new FileInfo(path).Length; } catch { }
            list.Add(new ModelInfo(null, Path.GetFileNameWithoutExtension(path), FormatSize(bytes), "custom", path));
        }
        return list;
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    /// <summary>
    /// Adds a whisper.cpp GGML model file. <paramref name="copy"/>: copy it into the models folder (survives the
    /// original being moved); otherwise use it where it is. Returns the new model's entry.
    /// </summary>
    public static async Task<ModelInfo> ImportAsync(string file, bool copy, IProgress<string>? status, CancellationToken ct)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Model file not found.", file);
        if (!LooksLikeWhisperModel(file))
            throw new InvalidDataException("This doesn't look like a whisper.cpp model (expected a GGML .bin, e.g. ggml-medium.en.bin). " +
                                           "Models from Hugging Face in PyTorch/safetensors format must be converted with whisper.cpp first.");
        string path = file;
        if (copy)
        {
            Directory.CreateDirectory(CustomDir);
            path = Path.Combine(CustomDir, Path.GetFileName(file));
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
            {
                var tmp = path + ".part";
                long total = new FileInfo(file).Length, done = 0;
                var buf = new byte[4 << 20];
                await using (var src = File.OpenRead(file))
                await using (var dst = File.Create(tmp))
                {
                    int read;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                        done += read;
                        status?.Report($"Importing model… {done / 1048576:N0} / {total / 1048576:N0} MB");
                    }
                }
                File.Move(tmp, path, overwrite: true);
            }
        }
        else if (!AppSettings.Current.CustomModelPaths.Contains(file, StringComparer.OrdinalIgnoreCase))
        {
            AppSettings.Current.CustomModelPaths.Add(file);
            AppSettings.Current.Save();
        }
        return All().First(m => string.Equals(m.CustomPath, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Removes an imported model: deletes a copied file, or just forgets a linked one.</summary>
    public static void RemoveCustom(ModelInfo model)
    {
        if (model.CustomPath is not { } path) return;
        if (AppSettings.Current.CustomModelPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
            AppSettings.Current.Save();
        else if (path.StartsWith(CustomDir, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
    }

    /// <summary>whisper.cpp model files start with the GGML magic ("lmgg" little-endian) or GGUF.</summary>
    public static bool LooksLikeWhisperModel(string file)
    {
        try
        {
            using var f = File.OpenRead(file);
            Span<byte> head = stackalloc byte[4];
            if (f.Read(head) < 4) return false;
            var magic = System.Text.Encoding.ASCII.GetString(head);
            return magic is "lmgg" or "ggml" or "GGUF" or "ggjt" or "tjgg";
        }
        catch { return false; }
    }

    public static bool IsDownloaded(ModelInfo model) => File.Exists(PathFor(model));

    private static readonly Dictionary<string, Task<string>> InFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Downloads the model if needed. A download of the same file already running (for example the GPU/CPU fallback
    /// fetched in the background while the NPU is used) is joined instead of started twice.
    /// </summary>
    public static Task<string> EnsureAsync(ModelInfo model, IProgress<long> bytesDownloaded, CancellationToken ct)
    {
        var target = PathFor(model);
        if (File.Exists(target)) return Task.FromResult(target);
        lock (InFlight)
        {
            if (InFlight.TryGetValue(target, out var running) && !running.IsCompleted) return running.WaitAsync(ct);
            var task = DownloadAsync(model, target, bytesDownloaded, ct);
            InFlight[target] = task;
            return task;
        }
    }

    /// <summary>True while this model is being downloaded (by any caller).</summary>
    public static bool IsDownloading(ModelInfo model)
    {
        lock (InFlight) return InFlight.TryGetValue(PathFor(model), out var t) && !t.IsCompleted;
    }

    /// <summary>
    /// whisper.cpp's official GGML files, pinned to one Hugging Face commit, with their SHA-256 (from the repository's LFS
    /// metadata). A download that doesn't match is discarded, so a damaged or altered file is never loaded.
    /// </summary>
    private const string GgmlRepo = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/";
    private static readonly Dictionary<GgmlType, (string File, long Size, string Sha256)> Pinned = new()
    {
        [GgmlType.Tiny] = ("ggml-tiny.bin", 77_691_713, "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"),
        [GgmlType.Base] = ("ggml-base.bin", 147_951_465, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        [GgmlType.Small] = ("ggml-small.bin", 487_601_967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        [GgmlType.Medium] = ("ggml-medium.bin", 1_533_763_059, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208"),
        [GgmlType.LargeV3Turbo] = ("ggml-large-v3-turbo.bin", 1_624_555_275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
        [GgmlType.LargeV3] = ("ggml-large-v3.bin", 3_095_033_483, "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2"),
    };

    public const string DownloadHost = "huggingface.co";

    /// <summary>Pinned SHA-256 of a built-in model, or null.</summary>
    public static string? ExpectedSha256(ModelInfo model) => model.Type is { } t && Pinned.TryGetValue(t, out var p) ? p.Sha256 : null;

    private static async Task<string> DownloadAsync(ModelInfo model, string target, IProgress<long> bytesDownloaded, CancellationToken ct)
    {
        if (model.IsCustom || model.Type is not { } type || !Pinned.TryGetValue(type, out var pin))
            throw new FileNotFoundException($"The custom model file was not found: {target}");
        if (!await Security.NetworkGate.ConfirmModelDownloadAsync($"the {model.Name} speech model", DownloadHost, model.Size))
            throw new Security.NetworkBlockedException($"The {model.Name} model isn't downloaded, and the download wasn't allowed. " +
                                                       "Allow model downloads in Settings → Security & privacy, or import the model file.");

        Directory.CreateDirectory(ModelsDir);
        var partial = target + ".part";
        try
        {
            using var http = Security.NetworkGate.Create(Security.NetFeature.ModelDownload, Timeout.InfiniteTimeSpan);
            using var response = await http.GetAsync(GgmlRepo + pin.File, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var dest = File.Create(partial))
            {
                var buf = new byte[1 << 20];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buf, ct)) > 0)
                {
                    if (total + read > pin.Size) throw new InvalidDataException($"The {model.Name} download is larger than expected; it was discarded.");
                    hash.AppendData(buf, 0, read);
                    await dest.WriteAsync(buf.AsMemory(0, read), ct);
                    total += read;
                    bytesDownloaded.Report(total);
                }
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(actual, pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The downloaded {model.Name} model failed its SHA-256 check (damaged or altered download); it was discarded.");
            File.Move(partial, target, overwrite: true);
            MarkVerified(target);
            return target;
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
    }

    // ---------------- integrity of models already on disk ----------------

    private static string VerifiedPath => Path.Combine(ModelsDir, "verified.json");

    private static Dictionary<string, string> LoadVerified()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(VerifiedPath)) ?? new(); }
        catch { return new(); }
    }

    private static string Stamp(string file) { var i = new FileInfo(file); return $"{i.Length}|{i.LastWriteTimeUtc.Ticks}"; }

    private static void MarkVerified(string file)
    {
        try
        {
            var v = LoadVerified();
            v[Path.GetFileName(file)] = Stamp(file);
            File.WriteAllText(VerifiedPath, System.Text.Json.JsonSerializer.Serialize(v));
        }
        catch { }
    }

    /// <summary>
    /// Checks a built-in model file against its pinned SHA-256 once (files downloaded by older versions were never checked),
    /// then remembers the result until the file changes. Throws when the file doesn't match.
    /// </summary>
    public static async Task VerifyAsync(ModelInfo model, IProgress<string>? status, CancellationToken ct)
    {
        if (ExpectedSha256(model) is not { } expected) return;   // imported models: the user's own file
        var path = PathFor(model);
        if (!File.Exists(path)) return;
        if (LoadVerified().TryGetValue(Path.GetFileName(path), out var stamp) && stamp == Stamp(path)) return;
        status?.Report($"Checking the {model.Name} model file (once)…");
        string actual;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {model.Name} model file doesn't match its official SHA-256 (damaged or replaced). " +
                                           $"Delete {Path.GetFileName(path)} from the models folder and it will be downloaded again.");
        MarkVerified(path);
    }
}
