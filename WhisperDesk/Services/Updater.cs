using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WhisperDesk.Services.Security;

namespace WhisperDesk.Services;

/// <summary>What the update source says about a new version. Every field has passed <see cref="Updater.Parse"/>, and
/// the whole file has passed both signature checks, before one of these exists.</summary>
public sealed record UpdateInfo(Version Version, string Released, string Notes, string FileName, Uri Url, long Bytes, string Sha256)
{
    /// <summary>The GitHub release page of this version (where the portable zip is).</summary>
    public string ReleasePage => $"https://github.com/{Updater.GitHubRepo}/releases/tag/v{Version}";
}

public enum UpdateVerdict { Verified, WrongProduct, WrongVersion, HashMismatch, SizeMismatch, Failed }

public sealed record VerifyResult(UpdateVerdict Verdict, string Message)
{
    public bool Ok => Verdict == UpdateVerdict.Verified;
}

/// <summary>What Settings → Updates remembers (updates.json in WhisperDesk's data folder). Nothing in it is about your
/// recordings, and it is the same whether or not the library is encrypted.</summary>
public sealed class UpdateSettings
{
    /// <summary>"github" (default: the repository's latest release) or "url" (an https address of your own, <see cref="Endpoint"/>).</summary>
    public string Source { get; set; } = "github";
    public string Endpoint { get; set; } = "";
    public bool AutoCheck { get; set; }
    /// <summary>"weekly" (default) or "daily".</summary>
    public string Interval { get; set; } = "weekly";
    public bool AutoDownload { get; set; }
    public DateTime LastCheck { get; set; }
    /// <summary>Set just before the installer starts; the next start compares it with the running version.</summary>
    public string PendingVersion { get; set; } = "";
    public string PendingFrom { get; set; } = "";
}

/// <summary>
/// Signed in-app updates for the MSI and portable copies of WhisperDesk: the same scheme as DarkCipher ServerManagement
/// and CaseKeeper.
/// <para><b>When it runs.</b> Only when you click Check for updates in Settings → Updates, or on the daily or weekly
/// schedule you can switch on there (off by default). A Microsoft Store copy never uses this: the Store signs and
/// delivers its updates.</para>
/// <para><b>Where it looks.</b> The repository's latest GitHub release (<see cref="GitHubRepo"/>) by default, or an https
/// address of your own. Every request goes through <see cref="NetworkGate"/> (listed in Network activity) and is a plain
/// GET with a generic User-Agent, no cookies, no query string and nothing about you, this PC or your recordings.</para>
/// <para><b>Trust.</b> The update information (update.json) is only believed when it carries two valid signatures from
/// the WhisperDesk release keys pinned below: ECDSA P-256 (update.json.sig) and post-quantum ML-DSA-65
/// (update.json.pq.sig). The private keys never leave the release PC (tools\sign-update.ps1). GitHub only carries the
/// files: nothing a hijacked account or server changes can pass. The signed file then vouches for the installer's exact
/// size and SHA-256.</para>
/// <para><b>Checks.</b> Strict parsing; only a strictly newer x.y.z version (no downgrade, so an old signed release can't
/// be replayed as new); the file name tied to that version; the installer on the same https host as the update
/// information; a size cap; redirects followed by hand and only within GitHub. The download goes to a private temp
/// folder and must match the signed size and SHA-256 and be WhisperDesk's MSI (its UpgradeCode) at exactly that version.
/// It is checked again just before it runs. Installing always goes through you: a confirmation, then Windows Installer.</para>
/// </summary>
public static class Updater
{
    public const string ProductName = "WhisperDesk";
    /// <summary>The GitHub repository whose releases carry update.json, its two signatures and the MSI.</summary>
    public const string GitHubRepo = "DarkCipher0x/WhisperDesk";
    public static string GitHubLatestManifest => $"https://github.com/{GitHubRepo}/releases/latest/download/update.json";
    public static string ReleasesPage => $"https://github.com/{GitHubRepo}/releases/latest";
    /// <summary>The MSI's permanent identity (Installer\WhisperDesk.wxs).</summary>
    public const string UpgradeCode = "{3FC82978-8263-42A1-9D62-8B858D3C0A9B}";
    public const long MaxBytes = 600L << 20;
    private const int MaxManifestBytes = 256 * 1024;

    /// <summary>The WhisperDesk release key (ECDSA P-256 public point, hex), created once on 2026-11-08 by
    /// tools\sign-update.ps1 -NewKeys. Never change it: installed copies trust only this key.</summary>
    public const string ReleaseKeyX = "060486D551C809427CB2789E9C9375AF24A0E14F6D5BD3F721F9B0B369269936";
    public const string ReleaseKeyY = "BF4CED954CBBEB577B4E0BB59C7A1AF59603912CC8D6E23F29E1B6EEFD787CC2";
    public static bool ReleaseKeyPinned => ReleaseKeyX.Length == 64 && ReleaseKeyY.Length == 64;

    /// <summary>The post-quantum WhisperDesk release key (ML-DSA-65 public key, base64), created once on 2026-11-08 by
    /// tools\sign-update.ps1 -NewKeys. An update is offered only when update.json carries both signatures. Never change it.</summary>
    public const string ReleasePqKey = "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";
    public static bool ReleasePqKeyPinned => ReleasePqKey.Length > 2000;

    // ---- test seams (set only by WhisperDesk.Tests) -----------------------------------------------------------------

    /// <summary>Test release keys (ECDSA X / Y hex, ML-DSA-65 base64) in place of the pinned ones.</summary>
    internal static (string X, string Y, string Pq)? TestKeys { get; set; }
    /// <summary>The installed version, in place of this assembly's.</summary>
    internal static Version? InstalledOverride { get; set; }
    /// <summary>The temp folder for downloads, in place of %TEMP%\WhisperDesk-Update.</summary>
    internal static string? WorkFolderOverride { get; set; }
    /// <summary>Reads (UpgradeCode, ProductVersion) from an MSI, in place of Windows Installer.</summary>
    internal static Func<string, (string? UpgradeCode, string? Version)>? MsiReader { get; set; }
    /// <summary>Whether the tests treat the update source as enabled (the test process isn't a Store package anyway).</summary>
    private static bool KeysPinned => TestKeys != null || (ReleaseKeyPinned && ReleasePqKeyPinned);

    /// <summary>On for the MSI and portable copies once the release keys are pinned. Checks still happen only when you
    /// click, or on the schedule you opt into. A Microsoft Store copy never uses it.</summary>
    public static bool Enabled => KeysPinned && !PackageInfo.IsPackaged;

    public static string DisabledMessage => PackageInfo.IsPackaged
        ? "This copy was installed from the Microsoft Store, which checks, signs and delivers WhisperDesk's updates (Microsoft Store → Library → Get updates). Your library stays where it is."
        : "In-app updates are turned off in this build: no release key is pinned. Download new versions from the website or GitHub.";

    public static Version Installed => InstalledOverride ?? (Version.TryParse(AboutWindow.Version, out var v) ? v : new Version(0, 0, 0));

    /// <summary>
    /// This copy was installed by WhisperDesk's MSI (Windows Installer knows a product with its UpgradeCode, and this exe
    /// runs from Program Files), so a verified MSI can upgrade it in place. Otherwise it is the portable copy: an update is
    /// found and checked the same way, and the release page gives the new zip.
    /// </summary>
    public static bool InstalledByMsi { get; } = DetectMsi();

    private static bool DetectMsi()
    {
        if (PackageInfo.IsPackaged) return false;
        try
        {
            var buf = new StringBuilder(39);
            if (MsiEnumRelatedProductsW(UpgradeCode, 0, 0, buf) != 0) return false;
            var dir = Path.GetFullPath(AppContext.BaseDirectory);
            return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                .Select(Environment.GetFolderPath).Where(p => p.Length > 0)
                .Any(p => dir.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    // ---- settings ---------------------------------------------------------------------------------------------------

    private static readonly object Gate = new();
    private static UpdateSettings? _settings;
    private static string SettingsFile => Path.Combine(AppPaths.LocalData, "updates.json");

    public static UpdateSettings Settings
    {
        get
        {
            lock (Gate)
            {
                if (_settings != null) return _settings;
                try { _settings = File.Exists(SettingsFile) ? JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(SettingsFile)) : null; }
                catch (Exception ex) { App.Log("Update settings", ex); }
                return _settings ??= new UpdateSettings();
            }
        }
    }

    public static void SaveSettings()
    {
        lock (Gate)
        {
            var s = Settings;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            var tmp = SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, SettingsFile, true);
        }
    }

    /// <summary>Forgets the in-memory copy (tests).</summary>
    internal static void ReloadSettings() { lock (Gate) _settings = null; }

    public static bool FromGitHub => Settings.Source != "url";
    public static string? CustomEndpoint => Settings.Source == "url" && !string.IsNullOrWhiteSpace(Settings.Endpoint) ? Settings.Endpoint.Trim() : null;
    /// <summary>Where the update information is read from.</summary>
    public static string Endpoint => FromGitHub ? GitHubLatestManifest : CustomEndpoint ?? "(no address set)";
    public static TimeSpan CheckInterval => Settings.Interval == "daily" ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
    public static bool AutoCheckDue => Enabled && Settings.AutoCheck && DateTime.Now - Settings.LastCheck >= CheckInterval;

    /// <summary>Why an address is refused, or null: https only, no user name or password in it, no query string.</summary>
    public static string? ValidateEndpoint(string url) =>
        !Uri.TryCreate(url, UriKind.Absolute, out var u) ? "That isn't a valid address."
        : u.Scheme != Uri.UriSchemeHttps ? "The update address must start with https://."
        : u.UserInfo.Length > 0 ? "The update address can't contain a user name or password."
        : u.Query.Length > 0 || u.Fragment.Length > 0 ? "The update address can't have a query (?…) or a #fragment."
        : !u.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "The update address must point at the update information file (…/update.json)."
        : null;

    /// <summary>The host of the address chosen in Settings → Updates (the network gate allows it for update checks).</summary>
    public static bool IsCustomHost(Uri u) =>
        CustomEndpoint is { } e && Uri.TryCreate(e, UriKind.Absolute, out var c) && c.Host.Equals(u.Host, StringComparison.OrdinalIgnoreCase);

    // ---- fetching ---------------------------------------------------------------------------------------------------

    /// <summary>Through the network gate (policy, Network activity), with no automatic redirects: <see cref="FetchAsync"/>
    /// decides each hop. The tests pass their own handler.</summary>
    private static HttpClient NewHttp(HttpMessageHandler? h, TimeSpan timeout) =>
        h != null ? new HttpClient(h) { Timeout = timeout } : NetworkGate.Create(NetFeature.Updates, timeout, followRedirects: false);

    /// <summary>github.com and GitHub's release download hosts, nothing else (https only).</summary>
    public static bool GitHubHost(Uri u) => u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>A GET with a size cap, following at most 5 redirects and only while <paramref name="allowed"/> accepts
    /// every hop (https only). Returns the status, the body (2xx only) or why it was refused.</summary>
    internal static async Task<(int Status, byte[]? Body, string? Refused)> FetchAsync(HttpClient http, Uri url, long cap, Func<Uri, bool>? allowed, CancellationToken ct,
        IProgress<double>? progress = null, Stream? to = null)
    {
        for (int hop = 0; hop <= 5; hop++)
        {
            if (url.Scheme != Uri.UriSchemeHttps) return (0, null, "an address that isn't https");
            if (hop > 0 && (allowed == null || !allowed(url))) return (0, null, $"a redirect to {url.Host}, which isn't allowed");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)r.StatusCode;
            if (code is >= 300 and < 400)
            {
                if (allowed == null || r.Headers.Location is not { } loc) return (code, null, null);
                url = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                continue;
            }
            if (!r.IsSuccessStatusCode) return (code, null, null);
            if (r.Content.Headers.ContentLength > cap) return (code, null, "a larger file than allowed");
            await using var src = await r.Content.ReadAsStreamAsync(ct);
            var mem = to == null ? new MemoryStream() : null;
            var dst = to ?? mem!;
            var buf = new byte[81920]; long total = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                total += n;
                if (total > cap) return (code, null, "a larger file than announced");
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                progress?.Report((double)total / cap);
            }
            return (code, mem?.ToArray() ?? Array.Empty<byte>(), null);
        }
        return (0, null, "too many redirects");
    }

    /// <summary>Fetches the update information and both signatures, verifies them, and parses it. Returns (info, message)
    /// when a newer version is available, (null, message) otherwise. Never throws for network trouble.</summary>
    public static async Task<(UpdateInfo? Info, string Message)> CheckAsync(CancellationToken ct, HttpMessageHandler? handler = null)
    {
        if (!Enabled) return (null, DisabledMessage);
        try { return FromGitHub ? await CheckGitHubAsync(ct, handler) : await CheckUrlAsync(ct, handler); }
        catch (NetworkBlockedException ex) { return (null, "The update check was blocked: " + ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return (null, "Couldn't reach the update server: " + (ex is TaskCanceledException ? "it timed out." : ex.Message));
        }
    }

    private const string BadEcdsa = "The update information's signature doesn't match the WhisperDesk release key (tampered with, or not ours), so nothing is offered.";
    private const string BadPq = "The update information's post-quantum signature doesn't match the WhisperDesk release key (tampered with, or not ours), so nothing is offered.";

    /// <summary>Both signatures over the exact bytes: null when they pass, else why not.</summary>
    private static string? SignatureProblem(byte[] manifest, byte[]? sig, byte[]? pqSig, string where)
    {
        if (sig == null) return $"{where} has no signature (update.json.sig), so nothing is offered.";
        if (!VerifyManifest(manifest, sig)) return BadEcdsa;
        if (pqSig == null) return $"{where} has no post-quantum signature (update.json.pq.sig), so nothing is offered.";
        if (!VerifyManifestPq(manifest, pqSig)) return BadPq;
        return null;
    }

    private static async Task<(UpdateInfo? Info, string Message)> CheckUrlAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        if (CustomEndpoint is not { } url) return (null, "No update address is set (Settings → Updates).");
        if (ValidateEndpoint(url) is { } bad) return (null, bad);
        var u = new Uri(url);
        using var http = NewHttp(handler, TimeSpan.FromSeconds(30));
        var (code, body, refused) = await FetchAsync(http, u, MaxManifestBytes, null, ct);
        if (refused != null) return (null, "The update information was refused: " + refused + ".");
        if (code == 404) return (null, $"No update information is published at {u.Host} yet.");
        if (code is >= 300 and < 400) return (null, "The update address redirected somewhere else; redirects aren't followed. Check the address in Settings → Updates.");
        if (body == null) return (null, $"The update server answered HTTP {code}.");
        var (_, sig, sr) = await FetchAsync(http, new Uri(u.AbsoluteUri + ".sig"), 4096, null, ct);
        var (_, pqSig, pr) = await FetchAsync(http, new Uri(u.AbsoluteUri + ".pq.sig"), 8192, null, ct);
        if (SignatureProblem(body, sr == null ? sig : null, pr == null ? pqSig : null, "The update information at " + u.Host) is { } why) return (null, why);
        return Parse(Utf8(body), u);
    }

    private static async Task<(UpdateInfo? Info, string Message)> CheckGitHubAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        using var http = NewHttp(handler, TimeSpan.FromSeconds(30));
        var (code, body, refused) = await FetchAsync(http, new Uri(GitHubLatestManifest), MaxManifestBytes, GitHubHost, ct);
        if (refused != null) return (null, "The update information was refused: " + refused + ".");
        if (code == 404) return (null, $"No release with update information is published on GitHub yet (github.com/{GitHubRepo}/releases).");
        if (body == null) return (null, $"GitHub answered HTTP {code}.");
        // Only the version (digits and dots) from the still-untrusted file goes into the next address.
        var ver = VersionField(body);
        if (ver == null) return (null, "The update information on GitHub is malformed (no valid version).");
        var release = new Uri($"https://github.com/{GitHubRepo}/releases/download/v{ver}/update.json");
        var (_, sig, sr) = await FetchAsync(http, new Uri(release.AbsoluteUri + ".sig"), 4096, GitHubHost, ct);
        var (_, pqSig, pr) = await FetchAsync(http, new Uri(release.AbsoluteUri + ".pq.sig"), 8192, GitHubHost, ct);
        if (SignatureProblem(body, sr == null ? sig : null, pr == null ? pqSig : null, $"The GitHub release v{ver}") is { } why) return (null, why);
        return Parse(Utf8(body), release);
    }

    private static string? VersionField(byte[] json)
    {
        try
        {
            var v = (JsonNode.Parse(Utf8(json)) as JsonObject)?["version"]?.ToString();
            return v != null && Regex.IsMatch(v, @"^\d{1,4}\.\d{1,4}\.\d{1,6}$") ? v : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException) { return null; }
    }

    private static string Utf8(byte[] b) => Encoding.UTF8.GetString(b).TrimStart('﻿');

    // ---- signatures -------------------------------------------------------------------------------------------------

    /// <summary>True when <paramref name="sigFile"/> (base64 of an ECDSA P-256 / SHA-256 signature, r||s) signs exactly
    /// <paramref name="data"/> with the release key (or the given or test key).</summary>
    public static bool VerifyManifest(byte[] data, byte[] sigFile, string? keyX = null, string? keyY = null)
    {
        try
        {
            var sig = Convert.FromBase64String(Encoding.ASCII.GetString(sigFile).Trim());
            if (sig.Length != 64) return false;
            using var ec = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = Convert.FromHexString(keyX ?? TestKeys?.X ?? ReleaseKeyX), Y = Convert.FromHexString(keyY ?? TestKeys?.Y ?? ReleaseKeyY) },
            });
            return ec.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    /// <summary>True when <paramref name="pqSigFile"/> (base64 of an ML-DSA-65 signature) signs exactly <paramref name="data"/>
    /// with the post-quantum release key (or the given or test key, base64).</summary>
    public static bool VerifyManifestPq(byte[] data, byte[] pqSigFile, string? pubB64 = null)
    {
        try { return Pqc.Verify(Convert.FromBase64String(pubB64 ?? TestKeys?.Pq ?? ReleasePqKey), data, Convert.FromBase64String(Encoding.ASCII.GetString(pqSigFile).Trim())); }
        catch (FormatException) { return false; }
    }

    // ---- parsing ----------------------------------------------------------------------------------------------------

    public static string MsiFileName(Version v) => $"WhisperDesk-{v}-x64.msi";

    /// <summary>Strict parsing of update information that has already passed both signature checks. Never trusts an
    /// address, name or number it doesn't check.</summary>
    public static (UpdateInfo? Info, string Message) Parse(string json, Uri manifestUrl, Version? installed = null)
    {
        installed ??= Installed;
        JsonObject? o;
        try { o = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false }) as JsonObject; }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return (null, "The update information is malformed (not JSON)."); }
        // Duplicate keys or wrongly typed values only surface when read: the same answer as malformed JSON.
        try { return ParseChecked(o, manifestUrl, installed); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or JsonException) { return (null, "The update information is malformed."); }
    }

    private static (UpdateInfo? Info, string Message) ParseChecked(JsonObject? o, Uri manifestUrl, Version installed)
    {
        if (o == null || o["product"]?.ToString() != ProductName) return (null, "The update information is for a different product.");
        var vs = o["version"]?.ToString() ?? "";
        if (!Regex.IsMatch(vs, @"^\d{1,4}\.\d{1,4}\.\d{1,6}$") || !Version.TryParse(vs, out var v)) return (null, "The update information has no valid version (x.y.z).");
        if (v <= installed) return (null, v == installed ? $"You have the latest version ({installed})." : $"You have {installed}; the published version is {v}, which is older. Older versions are never offered.");
        var pkg = (o["packages"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(p => p["kind"]?.ToString() == "msi" && p["arch"]?.ToString() == "x64");
        if (pkg == null) return (null, "The update information lists no Windows x64 installer.");
        var file = pkg["file"]?.ToString() ?? "";
        if (file != MsiFileName(v)) return (null, "The installer's file name doesn't match the version, so nothing is offered.");
        if (!Uri.TryCreate(manifestUrl, pkg["url"]?.ToString() ?? file, out var url) || url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals(manifestUrl.Host, StringComparison.OrdinalIgnoreCase)
            || url.UserInfo.Length > 0 || !url.AbsolutePath.EndsWith("/" + file, StringComparison.Ordinal) || url.Query.Length > 0 || url.Fragment.Length > 0)
            return (null, "The installer must be on the same https server as the update information, so nothing is offered.");
        if (!long.TryParse(pkg["bytes"]?.ToString(), out var bytes) || bytes <= 0 || bytes > MaxBytes) return (null, "The installer's size is missing or implausible, so nothing is offered.");
        var sha = pkg["sha256"]?.ToString() ?? "";
        if (!Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$")) return (null, "The installer's checksum is missing, so nothing is offered.");
        var notes = o["notes"]?.ToString() ?? "";
        if (notes.Length > 8000) notes = notes[..8000] + "…";
        var released = o["released"]?.ToString() ?? "";
        if (!Regex.IsMatch(released, @"^\d{4}-\d{2}-\d{2}$")) released = "";
        return (new UpdateInfo(v, released, notes, file, url, bytes, sha.ToLowerInvariant()), $"Version {v} is available (you have {installed}).");
    }

    // ---- download and verification ----------------------------------------------------------------------------------

    /// <summary>%TEMP%\WhisperDesk-Update: downloads, and the staged installer while WhisperDesk closes.</summary>
    public static string WorkFolder => WorkFolderOverride ?? Path.Combine(Path.GetTempPath(), "WhisperDesk-Update");
    private static string StagingDir => Path.Combine(WorkFolder, "ready");

    /// <summary>Downloads into a new private folder with the size cap (redirects only within GitHub for a GitHub release,
    /// none otherwise), then verifies. A download that fails any check is deleted.</summary>
    public static async Task<(string? Path, VerifyResult Result)> DownloadAndVerifyAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        var dir = Path.Combine(WorkFolder, "dl-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, info.FileName);
        try
        {
            using var http = NewHttp(handler, TimeSpan.FromMinutes(20));
            int code; string? refused;
            await using (var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                (code, _, refused) = await FetchAsync(http, info.Url, Math.Min(info.Bytes, MaxBytes), GitHubHost(info.Url) ? GitHubHost : null, ct, progress, dst);
            if (refused != null)
            {
                TryDelete(dir);
                bool larger = refused.Contains("larger", StringComparison.Ordinal);
                return (null, new VerifyResult(larger ? UpdateVerdict.SizeMismatch : UpdateVerdict.Failed, larger ? "The download is larger than announced; it was stopped and deleted." : "The download was refused: " + refused + "."));
            }
            if (code is < 200 or >= 300) { TryDelete(dir); return (null, new VerifyResult(UpdateVerdict.Failed, $"The download failed: HTTP {code}.")); }
            var v = Verify(path, info);
            if (!v.Ok) { TryDelete(dir); return (null, v); }
            return (path, v);
        }
        catch (OperationCanceledException) { TryDelete(dir); throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or NetworkBlockedException)
        {
            TryDelete(dir);
            return (null, new VerifyResult(UpdateVerdict.Failed, "The download failed: " + ex.Message));
        }
    }

    private static void TryDelete(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    /// <summary>Every check on a downloaded installer, cheapest first: the size and SHA-256 the signed update information
    /// gave, an MSI at all, and WhisperDesk's MSI (its UpgradeCode) at exactly that version.</summary>
    public static VerifyResult Verify(string path, UpdateInfo info)
    {
        var len = new FileInfo(path).Length;
        if (len != info.Bytes) return new(UpdateVerdict.SizeMismatch, $"The file is {len:N0} bytes, not the {info.Bytes:N0} announced, so it was refused.");
        using (var fs = File.OpenRead(path))
        {
            if (!Convert.ToHexString(SHA256.HashData(fs)).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                return new(UpdateVerdict.HashMismatch, "The SHA-256 doesn't match the signed checksum (damaged or substituted), so it was refused.");
            fs.Position = 0;
            Span<byte> magic = stackalloc byte[8];
            if (fs.Read(magic) != 8 || !magic.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
                return new(UpdateVerdict.WrongProduct, "The download isn't a Windows Installer package, so it was refused.");
        }
        var (upgrade, version) = (MsiReader ?? MsiProps)(path);
        if (!string.Equals(upgrade, UpgradeCode, StringComparison.OrdinalIgnoreCase))
            return new(UpdateVerdict.WrongProduct, $"The installer is for a different product (UpgradeCode {upgrade ?? "missing"}), so it was refused.");
        if (!Version.TryParse(version, out var mv) || new Version(mv.Major, mv.Minor, Math.Max(0, mv.Build)) != info.Version)
            return new(UpdateVerdict.WrongVersion, $"The installer says version {version}, not {info.Version}, so it was refused.");
        return new(UpdateVerdict.Verified, $"Verified: the size and SHA-256 match the update information signed by both WhisperDesk release keys, and the installer is WhisperDesk {info.Version}.");
    }

    // ---- install and the check after it ------------------------------------------------------------------------------

    /// <summary>An update the opt-in background download fetched and verified this session. Never saved: Settings →
    /// Updates offers it, and it is checked again before it runs.</summary>
    public static (UpdateInfo Info, string Path)? Ready { get; set; }

    /// <summary>Starts Windows Installer for an already verified MSI. It is copied to its own folder and verified again
    /// there first. What is expected is saved, so the next start can say whether the update happened. The caller then
    /// closes WhisperDesk.</summary>
    public static void LaunchInstaller(string path, UpdateInfo info)
    {
        if (!InstalledByMsi) throw new InvalidOperationException("This copy wasn't installed with the installer, so it can't be upgraded in place. Download the new portable zip from the release page.");
        TryDelete(StagingDir);
        Directory.CreateDirectory(StagingDir);
        var staged = Path.Combine(StagingDir, info.FileName);
        File.Copy(path, staged, true);
        var again = Verify(staged, info);
        if (!again.Ok) { TryDelete(StagingDir); throw new InvalidOperationException(again.Message); }
        Settings.PendingVersion = info.Version.ToString();
        Settings.PendingFrom = Installed.ToString();
        SaveSettings();
        App.LogEvent($"Update: installer started, {Installed} → {info.Version}");
        // The normal installer UI (Windows asks for permission to install for all users).
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/i \"{staged}\"") { UseShellExecute = false, WorkingDirectory = StagingDir });
    }

    /// <summary>On start: if an update was started last time, compares the running version with what was expected and
    /// tidies the temp folder. Returns (message, completed), or null when nothing was pending.</summary>
    public static (string Message, bool Completed)? ConfirmPendingUpdate()
    {
        var s = Settings;
        if (string.IsNullOrEmpty(s.PendingVersion)) { CleanWorkFolder(); return null; }
        var expected = s.PendingVersion; var from = s.PendingFrom;
        s.PendingVersion = ""; s.PendingFrom = "";
        try { SaveSettings(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log("Update settings", ex); }
        CleanWorkFolder();
        if (Installed.ToString() == expected)
        {
            App.LogEvent($"Update: verified, {from} → {expected}");
            return ($"Update verified: WhisperDesk {expected} is now running (it was {from}).", true);
        }
        App.LogEvent($"Update: not completed, expected {expected}, running {Installed}");
        return ($"The update to {expected} didn't complete: WhisperDesk {Installed} is still running. Your library is unchanged.", false);
    }

    /// <summary>Deletes leftover downloads and the staged installer.</summary>
    public static void CleanWorkFolder() => TryDelete(WorkFolder);

    // ---- Windows Installer ------------------------------------------------------------------------------------------

    public static (string? UpgradeCode, string? Version) MsiProps(string path)
    {
        if (MsiOpenDatabaseW(path, IntPtr.Zero, out var db) != 0) return (null, null);
        try { return (Prop(db, "UpgradeCode"), Prop(db, "ProductVersion")); }
        finally { MsiCloseHandle(db); }
    }

    private static string? Prop(IntPtr db, string name)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z]+$") || MsiDatabaseOpenViewW(db, $"SELECT `Value` FROM `Property` WHERE `Property`='{name}'", out var view) != 0) return null;
        try
        {
            if (MsiViewExecute(view, IntPtr.Zero) != 0 || MsiViewFetch(view, out var rec) != 0) return null;
            try
            {
                var sb = new StringBuilder(256); int len = sb.Capacity;
                return MsiRecordGetStringW(rec, 1, sb, ref len) == 0 ? sb.ToString() : null;
            }
            finally { MsiCloseHandle(rec); }
        }
        finally { MsiCloseHandle(view); }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr handle);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenViewW(IntPtr db, string query, out IntPtr view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetStringW(IntPtr record, int field, StringBuilder value, ref int len);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(IntPtr handle);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiEnumRelatedProductsW(string upgradeCode, uint reserved, uint index, StringBuilder productCode);
}
