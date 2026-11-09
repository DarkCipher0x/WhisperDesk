using System.IO;
using Whisper.net.Ggml;

namespace WhisperDesk.Services;

/// <param name="Type">Built-in Whisper model, or null for a custom (imported) model file.</param>
/// <param name="CustomPath">Full path of an imported model file.</param>
/// <param name="CatalogId">The model's id in the model list (<see cref="ModelCatalog"/>), null for a custom model.</param>
public sealed record ModelInfo(GgmlType? Type, string Name, string Size, string Notes, string? CustomPath = null, string? CatalogId = null)
{
    public bool IsCustom => CustomPath != null;
    /// <summary>Saved in settings to remember the choice (the original six keep their 1.x keys).</summary>
    public string Key => Type?.ToString() ?? (CatalogId != null ? "catalog:" + CatalogId : "custom:" + CustomPath);
    /// <summary>Its entry in the current model list.</summary>
    public CatalogModel? Entry => CatalogId != null ? ModelCatalog.Current.Find(CatalogId) : null;
    public override string ToString() => IsCustom ? $"★ {Name}  ({Size}) - custom" : $"{Name}  ({Size}) - {Notes}";
}

/// <summary>Downloads and stores Whisper GGML models under %LOCALAPPDATA%\WhisperDesk\models.</summary>
public static class ModelManager
{
    /// <summary>The Whisper models of the current model list (<see cref="ModelCatalog"/>), in its order.</summary>
    public static IReadOnlyList<ModelInfo> Models => ModelCatalog.Current.Models.Where(m => m.IsWhisper)
        .Select(m => new ModelInfo(m.Ggml != null ? Enum.Parse<GgmlType>(m.Ggml) : null, m.Name, m.Size, m.Notes, null, m.Id)).ToList();

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

    public static string PathFor(ModelInfo model) => model.CustomPath ?? model.Entry?.LocalPath
        ?? Path.Combine(ModelsDir, $"ggml-{model.Type.ToString()!.ToLowerInvariant()}.bin");

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

    /// <summary>
    /// Downloads the model if needed (pinned source, exact size and SHA-256: <see cref="ModelCatalog.DownloadAsync"/>). A
    /// download of the same file already running (for example the GPU/CPU fallback fetched in the background while the
    /// NPU is used, or one started in Tools → Models) is joined instead of started twice.
    /// </summary>
    public static Task<string> EnsureAsync(ModelInfo model, IProgress<long> bytesDownloaded, CancellationToken ct)
    {
        var target = PathFor(model);
        if (File.Exists(target)) return Task.FromResult(target);
        if (model.Entry is not { } entry) throw new FileNotFoundException($"The custom model file was not found: {target}");
        return ModelCatalog.DownloadAsync(entry, bytesDownloaded, ct);
    }

    /// <summary>True while this model is being downloaded (by any caller).</summary>
    public static bool IsDownloading(ModelInfo model) => model.Entry is { } e && ModelCatalog.IsDownloading(e);

    public const string DownloadHost = "huggingface.co";

    /// <summary>The SHA-256 the current model list expects, or null for a custom model.</summary>
    public static string? ExpectedSha256(ModelInfo model) => model.Entry?.Sha256;

    /// <summary>
    /// Checks a downloaded model before it is loaded: once per file version (remembered until the file changes). A file
    /// from an older model list that WhisperDesk verified is still used (Tools → Models offers the update); a file that
    /// matches no trusted SHA-256 is refused.
    /// </summary>
    public static async Task VerifyAsync(ModelInfo model, IProgress<string>? status, CancellationToken ct)
    {
        if (model.Entry is not { } entry || !File.Exists(entry.LocalPath)) return;   // custom models: the user's own file
        if (ModelCatalog.QuickState(entry) == ModelState.Checking) status?.Report($"Checking the {model.Name} model file (once)…");
        if (await ModelCatalog.CheckFileAsync(entry, ct) == ModelState.Damaged)
            throw new InvalidDataException($"The {model.Name} model file doesn't match its official SHA-256 (damaged or replaced). " +
                                           "Open Tools → Models and download it again.");
    }
}
