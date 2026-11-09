using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhisperDesk.Services.Security;

namespace WhisperDesk.Services;

/// <summary>One downloadable model: a Whisper speech model, or one of the three speaker / voice-detection models.</summary>
/// <param name="Role">"whisper", "segmentation", "embedding" or "vad".</param>
/// <param name="Ggml">For the original six Whisper models: their Whisper.net type (keeps their file names and saved choice).</param>
/// <param name="File">Source file name (and the local name, except for the original six).</param>
public sealed record CatalogModel(string Id, string Role, string? Ggml, string Name, string Notes, string File, Uri Url, long Bytes, string Sha256)
{
    public bool IsWhisper => Role == "whisper";

    /// <summary>Path below the models folder: the original six keep their 1.x names (ggml-largev3turbo.bin…), the
    /// speaker models live in speakers\.</summary>
    public string LocalName => Role != "whisper" ? Path.Combine("speakers", File)
                             : Ggml != null ? $"ggml-{Ggml.ToLowerInvariant()}.bin" : File;

    public string LocalPath => Path.Combine(ModelManager.ModelsDir, LocalName);
    public string Size => ModelManager.FormatSize(Bytes);
}

/// <summary>The list of models.</summary>
public sealed record Catalog(int Sequence, string Published, IReadOnlyList<CatalogModel> Models, byte[] Bytes, string Source)
{
    public CatalogModel? Find(string id) => Models.FirstOrDefault(m => m.Id == id);
    public CatalogModel Role(string role) => Models.First(m => m.Role == role);
}

public enum ModelState { NotDownloaded, Checking, UpToDate, UpdateAvailable, Damaged }

/// <summary>
/// Which models WhisperDesk offers, where each comes from and its SHA-256: <c>models\catalog.json</c> in the source, built
/// into the app (works offline), and published with every release as <c>models.json</c> signed by the same two release
/// keys as updates (ECDSA P-256 + ML-DSA-65, <see cref="Updater"/>). <see cref="CheckAsync"/> fetches the newest signed
/// list from GitHub, so new or updated models reach every copy (the Microsoft Store one included) without an app
/// update; an unsigned, tampered or older list is never used. Model files themselves are data, never code: each download
/// must match the list's size and SHA-256, from a pinned Hugging Face revision or a GitHub release.
/// </summary>
public static class ModelCatalog
{
    public const string FileName = "models.json";
    public const int MaxCatalogBytes = 256 * 1024;
    private static readonly string[] Roles = ["whisper", "segmentation", "embedding", "vad"];

    public static string GitHubLatest => $"https://github.com/{Updater.GitHubRepo}/releases/latest/download/{FileName}";

    private static readonly object Gate = new();
    private static Catalog? _embedded, _current;

    /// <summary>The list built into this version.</summary>
    public static Catalog Embedded
    {
        get
        {
            lock (Gate)
            {
                if (_embedded != null) return _embedded;
                using var s = typeof(ModelCatalog).Assembly.GetManifestResourceStream("WhisperDesk.models.catalog.json")
                              ?? throw new InvalidOperationException("The built-in model list is missing.");
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var (cat, why) = Parse(ms.ToArray(), "built in");
                return _embedded = cat ?? throw new InvalidOperationException("The built-in model list is invalid: " + why);
            }
        }
    }

    /// <summary>The newest valid list: a signed one downloaded earlier (re-verified on every start), else the built-in one.</summary>
    public static Catalog Current
    {
        get
        {
            lock (Gate)
            {
                if (_current != null) return _current;
                _current = Embedded;
                try
                {
                    if (File.Exists(CachePath) && LoadSigned(File.ReadAllBytes(CachePath), ReadOrNull(CachePath + ".sig"), ReadOrNull(CachePath + ".pq.sig"), "downloaded") is { } cached
                        && cached.Sequence > _current.Sequence)
                        _current = cached;
                }
                catch (Exception ex) { App.Log("Model list cache", ex); }
                return _current;
            }
        }
    }

    /// <summary>Raised (on any thread) after a model file was downloaded, replaced or removed, or the list changed.</summary>
    public static event Action? Changed;
    internal static void RaiseChanged() { try { Changed?.Invoke(); } catch { } }

    /// <summary>Where a downloaded signed list is kept (the data folder, not the models folder: it doesn't move with it).</summary>
    public static string CachePath => Path.Combine(AppPaths.LocalData, "model-catalog", FileName);

    internal static void ResetForTests() { lock (Gate) { _current = null; } }

    private static byte[]? ReadOrNull(string p) { try { return File.Exists(p) ? File.ReadAllBytes(p) : null; } catch { return null; } }

    private static Catalog? LoadSigned(byte[] body, byte[]? sig, byte[]? pq, string source)
    {
        if (sig == null || pq == null || !Updater.VerifyManifest(body, sig) || !Updater.VerifyManifestPq(body, pq)) return null;
        return Parse(body, source).Catalog;
    }

    // ---------------------------------------------- parsing ----------------------------------------------

    private static readonly Regex IdRx = new(@"^[a-z0-9][a-z0-9._-]{0,63}$");
    private static readonly Regex FileRx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,119}\.(bin|gguf|onnx)$");
    private static readonly Regex ShaRx = new("^[0-9a-f]{64}$");
    private static readonly Regex HfPath = new(@"^/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/resolve/[0-9a-f]{40}/[A-Za-z0-9._/-]+$");
    private static readonly Regex GhPath = new(@"^/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/releases/download/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$");

    /// <summary>
    /// Strict: product, sequence, one entry per id and per local file, exactly one model for each speaker role and at
    /// least one Whisper model, https downloads only from a pinned Hugging Face revision or a GitHub release asset (no
    /// query, no other host), sizes and SHA-256 present. Anything else rejects the whole list.
    /// </summary>
    public static (Catalog? Catalog, string? Why) Parse(byte[] json, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 8 });
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return (null, "not a JSON object");
            if (Str(o, "product") != "WhisperDesk" || Str(o, "kind") != "model-catalog") return (null, "not a WhisperDesk model list");
            if (!o.TryGetProperty("sequence", out var seqEl) || !seqEl.TryGetInt32(out var seq) || seq < 1) return (null, "no valid sequence number");
            var published = Str(o, "published") is { } p && Regex.IsMatch(p, @"^\d{4}-\d{2}-\d{2}$") ? p : "";
            if (!o.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array) return (null, "no models");
            var list = new List<CatalogModel>();
            foreach (var m in arr.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) return (null, "a model entry isn't an object");
                var id = Str(m, "id") ?? "";
                if (!IdRx.IsMatch(id)) return (null, $"invalid model id '{id}'");
                var role = Str(m, "role") ?? "";
                if (!Roles.Contains(role)) return (null, $"{id}: unknown role '{role}'");
                var ggml = Str(m, "ggml");
                if (ggml != null && (role != "whisper" || !Enum.TryParse<Whisper.net.Ggml.GgmlType>(ggml, out _) || !Regex.IsMatch(ggml, "^[A-Za-z0-9]+$")))
                    return (null, $"{id}: invalid ggml type '{ggml}'");
                var name = Str(m, "name") ?? "";
                var notes = Str(m, "notes") ?? "";
                if (name.Length is 0 or > 80 || notes.Length > 200) return (null, $"{id}: name or notes missing or too long");
                var file = Str(m, "file") ?? "";
                if (!FileRx.IsMatch(file) || file.Contains("..")) return (null, $"{id}: invalid file name '{file}'");
                if (role == "whisper" ? file.EndsWith(".onnx", StringComparison.Ordinal) : !file.EndsWith(".onnx", StringComparison.Ordinal))
                    return (null, $"{id}: wrong file type for a {role} model");
                if (!Uri.TryCreate(Str(m, "url"), UriKind.Absolute, out var url) || !AllowedSource(url)) return (null, $"{id}: download address not allowed");
                if (!m.TryGetProperty("bytes", out var b) || !b.TryGetInt64(out var bytes) || bytes < 1 || bytes > 5_000_000_000) return (null, $"{id}: invalid size");
                var sha = Str(m, "sha256") ?? "";
                if (!ShaRx.IsMatch(sha)) return (null, $"{id}: invalid SHA-256");
                list.Add(new CatalogModel(id, role, ggml, name, notes, file, url, bytes, sha));
            }
            if (list.Select(x => x.Id).Distinct().Count() != list.Count) return (null, "duplicate model id");
            if (list.Select(x => x.LocalName.ToLowerInvariant()).Distinct().Count() != list.Count) return (null, "two models share a file name");
            if (list.Where(x => x.Ggml != null).Select(x => x.Ggml).Distinct().Count() != list.Count(x => x.Ggml != null)) return (null, "duplicate ggml type");
            if (!list.Any(x => x.IsWhisper)) return (null, "no Whisper model");
            foreach (var r in Roles.Skip(1))
                if (list.Count(x => x.Role == r) != 1) return (null, $"needs exactly one {r} model");
            return (new Catalog(seq, published, list, json, source), null);
        }
        catch (JsonException ex) { return (null, "malformed JSON: " + ex.Message); }
    }

    /// <summary>https from a pinned Hugging Face revision or a GitHub release asset; nothing else.</summary>
    public static bool AllowedSource(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo.Length == 0 && u.Query.Length == 0 && u.Fragment.Length == 0 &&
        ((u.Host == "huggingface.co" && HfPath.IsMatch(u.AbsolutePath)) || (u.Host == "github.com" && GhPath.IsMatch(u.AbsolutePath)));

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------------------------------------------- checking for a newer list ----------------------------------------------

    public sealed record CheckResult(bool Changed, string Message, IReadOnlyList<CatalogModel> NewModels, IReadOnlyList<CatalogModel> UpdatedModels);

    /// <summary>Where the newest list is fetched: GitHub's latest release, or next to update.json on your own update server.</summary>
    public static Uri Source => Updater.Enabled && Updater.CustomEndpoint is { } ep && Uri.TryCreate(ep, UriKind.Absolute, out var u)
        ? new Uri(u, FileName) : new Uri(GitHubLatest);

    /// <summary>Fetches the newest signed list; switches to it only when both signatures verify and it is newer.</summary>
    public static async Task<CheckResult> CheckAsync(CancellationToken ct, HttpMessageHandler? handler = null)
    {
        var src = Source;
        bool fromGitHub = src.Host == "github.com";
        using var http = handler != null ? new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(30) }
                                         : NetworkGate.Create(NetFeature.ModelCatalog, TimeSpan.FromSeconds(30), followRedirects: false);
        Func<Uri, bool>? allowed = fromGitHub ? Updater.GitHubHost : null;
        var (code, body, refused) = await Updater.FetchAsync(http, src, MaxCatalogBytes, allowed, ct);
        if (refused != null) return Fail("The model list was refused: " + refused + ".");
        if (code == 404) return Fail("No model list is published there yet; the built-in list is used.");
        if (body == null) return Fail($"The server answered HTTP {code}.");
        var (_, sig, sr) = await Updater.FetchAsync(http, new Uri(src.AbsoluteUri + ".sig"), 4096, allowed, ct);
        var (_, pq, pr) = await Updater.FetchAsync(http, new Uri(src.AbsoluteUri + ".pq.sig"), 8192, allowed, ct);
        if (sr != null || pr != null || sig == null || pq == null) return Fail("The model list has no signature, so it isn't used.");
        if (!Updater.VerifyManifest(body, sig) || !Updater.VerifyManifestPq(body, pq))
            return Fail("The model list's signatures don't match WhisperDesk's release keys (tampered with, or not ours), so it isn't used.");
        var (cat, why) = Parse(body, "downloaded " + DateTime.Now.ToString("yyyy-MM-dd"));
        if (cat == null) return Fail("The model list is signed but invalid (" + why + "), so it isn't used.");

        var old = Current;
        if (cat.Sequence < old.Sequence) return Fail($"Your model list (version {old.Sequence}) is newer than the published one ({cat.Sequence}); nothing changed.");
        if (cat.Sequence == old.Sequence)
            return cat.Bytes.AsSpan().SequenceEqual(old.Bytes)
                ? new CheckResult(false, $"Your model list is up to date (version {old.Sequence}).", [], [])
                : Fail("The published model list differs from yours but has the same version; nothing changed.");

        // Newer and verified: keep it (with its signatures, re-checked on every start) and switch to it.
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        foreach (var (path, data) in new[] { (CachePath + ".sig", sig), (CachePath + ".pq.sig", pq), (CachePath, body) })
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        lock (Gate) _current = cat;
        var added = cat.Models.Where(m => old.Find(m.Id) == null).ToList();
        var updated = cat.Models.Where(m => old.Find(m.Id) is { } o && o.Sha256 != m.Sha256 && File.Exists(m.LocalPath)).ToList();
        RaiseChanged();
        var parts = new List<string> { $"Model list updated to version {cat.Sequence} (signed)." };
        if (added.Count > 0) parts.Add($"New: {string.Join(", ", added.Select(m => m.Name))}.");
        if (updated.Count > 0) parts.Add($"Updates for your downloaded models: {string.Join(", ", updated.Select(m => m.Name))}.");
        return new CheckResult(true, string.Join(" ", parts), added, updated);

        static CheckResult Fail(string m) => new(false, m, [], []);
    }

    // ---------------------------------------------- files on disk ----------------------------------------------

    private static string VerifiedPath => Path.Combine(ModelManager.ModelsDir, "verified.json");
    private static readonly object FileLock = new();

    private static Dictionary<string, string> LoadVerified()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(VerifiedPath)) ?? new(); }
        catch { return new(); }
    }

    private static string Stamp(string file) { var i = new FileInfo(file); return $"{i.Length}|{i.LastWriteTimeUtc.Ticks}"; }

    /// <summary>The SHA-256 a file was verified to have, while it hasn't changed since; null when unknown.</summary>
    public static string? KnownSha(CatalogModel m)
    {
        var path = m.LocalPath;
        if (!File.Exists(path)) return null;
        string? rec;
        lock (FileLock) rec = LoadVerified().GetValueOrDefault(Key(m));
        if (rec == null) return null;
        var parts = rec.Split('|');
        if (parts.Length < 2 || $"{parts[0]}|{parts[1]}" != Stamp(path)) return null;
        // Records from before 1.9 have no hash: the file was checked against the pin of that time, the built-in list's.
        return parts.Length >= 3 ? parts[2] : Embedded.Find(m.Id)?.Sha256;
    }

    /// <summary>verified.json key: the file name for Whisper models (as 1.x wrote it), "speakers/x.onnx" for the others.</summary>
    private static string Key(CatalogModel m) => m.LocalName.Replace('\\', '/');

    public static void Record(CatalogModel m, string sha)
    {
        try
        {
            lock (FileLock)
            {
                var v = LoadVerified();
                v[Key(m)] = $"{Stamp(m.LocalPath)}|{sha.ToLowerInvariant()}";
                Directory.CreateDirectory(ModelManager.ModelsDir);
                File.WriteAllText(VerifiedPath, JsonSerializer.Serialize(v));
            }
        }
        catch { }
    }

    /// <summary>The state from what is already known (no hashing): Checking when the file still has to be hashed once.</summary>
    public static ModelState QuickState(CatalogModel m)
    {
        if (IsDownloading(m)) return ModelState.Checking;
        if (!File.Exists(m.LocalPath)) return ModelState.NotDownloaded;
        return KnownSha(m) is { } sha ? (sha == m.Sha256 ? ModelState.UpToDate : ModelState.UpdateAvailable) : ModelState.Checking;
    }

    /// <summary>
    /// The state, hashing the file once if needed (big models take a few seconds). Only hashes WhisperDesk trusts are
    /// remembered (this list's or the built-in list's; older trusted ones were recorded when they were downloaded): a
    /// file matching none of them is Damaged and is never loaded.
    /// </summary>
    public static async Task<ModelState> CheckFileAsync(CatalogModel m, CancellationToken ct)
    {
        var quick = QuickState(m);
        if (quick != ModelState.Checking || IsDownloading(m)) return quick;
        string sha;
        await using (var fs = new FileStream(m.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (sha == m.Sha256) { Record(m, sha); return ModelState.UpToDate; }
        if (sha == Embedded.Find(m.Id)?.Sha256) { Record(m, sha); return ModelState.UpdateAvailable; }
        return ModelState.Damaged;
    }

    /// <summary>Model files in the models folder that no model in the list uses any more (older versions, renamed ones).</summary>
    public static List<FileInfo> Unused()
    {
        var used = new HashSet<string>(Current.Models.Select(m => Path.GetFullPath(m.LocalPath)), StringComparer.OrdinalIgnoreCase);
        var list = new List<FileInfo>();
        // This folder only: custom\ holds imported models and onnx\ the NPU versions, neither is in the list.
        var here = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        try
        {
            var dir = ModelManager.ModelsDir;
            if (Directory.Exists(dir))
                list.AddRange(Directory.EnumerateFiles(dir, "ggml-*.*", here).Where(f => f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)).Select(f => new FileInfo(f)));
            var sp = Path.Combine(dir, "speakers");
            if (Directory.Exists(sp)) list.AddRange(Directory.EnumerateFiles(sp, "*.onnx", here).Select(f => new FileInfo(f)));
        }
        catch { }
        return list.Where(f => !used.Contains(f.FullName)).ToList();
    }

    // ---------------------------------------------- downloading ----------------------------------------------

    private static readonly Dictionary<string, Task<string>> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> Progress = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsDownloading(CatalogModel m)
    {
        lock (InFlight) return InFlight.TryGetValue(m.LocalPath, out var t) && !t.IsCompleted;
    }

    /// <summary>Bytes downloaded so far of a running download, else null.</summary>
    public static long? DownloadedBytes(CatalogModel m)
    {
        lock (InFlight) return IsDownloading(m) && Progress.TryGetValue(m.LocalPath, out var p) ? p : null;
    }

    /// <summary>
    /// Downloads (or replaces) a model file: asks first unless <paramref name="consented"/>, streams it into a .part file
    /// with its size capped, checks the exact size and SHA-256, then moves it into place. A download of the same file
    /// already running is joined. The old file stays in use until the new one is complete and verified.
    /// </summary>
    public static Task<string> DownloadAsync(CatalogModel m, IProgress<long>? progress, CancellationToken ct, bool consented = false)
    {
        var target = m.LocalPath;
        lock (InFlight)
        {
            if (InFlight.TryGetValue(target, out var running) && !running.IsCompleted) return running.WaitAsync(ct);
            var task = Task.Run(() => DownloadCoreAsync(m, target, progress, ct, consented), ct);
            InFlight[target] = task;
            Progress.Remove(target);   // reported from the first byte, not while the consent prompt is open
            return task;
        }
    }

    private static async Task<string> DownloadCoreAsync(CatalogModel m, string target, IProgress<long>? progress, CancellationToken ct, bool consented)
    {
        if (!consented && !await NetworkGate.ConfirmModelDownloadAsync($"the {m.Name} model", m.Url.Host, m.Size))
            throw new NetworkBlockedException($"The {m.Name} model isn't downloaded, and the download wasn't allowed. " +
                                              "Allow model downloads in Settings → Security & privacy, or import the model file.");
        lock (InFlight) Progress[target] = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".part";
        try
        {
            using var http = NetworkGate.Create(NetFeature.ModelDownload, Timeout.InfiniteTimeSpan);
            using var response = await http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var dest = File.Create(partial))
            {
                var buf = new byte[1 << 20];
                int read;
                while ((read = await source.ReadAsync(buf, ct)) > 0)
                {
                    if (total + read > m.Bytes) throw new InvalidDataException($"The {m.Name} download is larger than expected; it was discarded.");
                    hash.AppendData(buf, 0, read);
                    await dest.WriteAsync(buf.AsMemory(0, read), ct);
                    total += read;
                    lock (InFlight) Progress[target] = total;
                    progress?.Report(total);
                }
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (total != m.Bytes || actual != m.Sha256)
                throw new InvalidDataException($"The downloaded {m.Name} model failed its size/SHA-256 check (damaged or altered download); it was discarded.");
            File.Move(partial, target, overwrite: true);
            Record(m, actual);
            RaiseChanged();
            return target;
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
    }

    /// <summary>Deletes a downloaded model file (it downloads again when needed).</summary>
    public static void Remove(CatalogModel m)
    {
        if (IsDownloading(m)) throw new InvalidOperationException($"{m.Name} is downloading.");
        if (File.Exists(m.LocalPath)) File.Delete(m.LocalPath);
        lock (FileLock)
        {
            try
            {
                var v = LoadVerified();
                if (v.Remove(Key(m))) File.WriteAllText(VerifiedPath, JsonSerializer.Serialize(v));
            }
            catch { }
        }
        RaiseChanged();
    }

    internal static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
