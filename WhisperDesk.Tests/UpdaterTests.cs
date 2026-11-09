using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using WhisperDesk.Services;
using Xunit;

namespace WhisperDesk.Tests;

/// <summary>
/// Signed updates (Services/Updater.cs): both signatures, strict parsing, a fake GitHub (no network), and the checks on
/// the downloaded installer. Runs with throw-away test keys in place of the pinned release keys.
/// </summary>
[Collection("Updater")]
public sealed class UpdaterTests : IDisposable
{
    private readonly ECDsa _ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _pqSeed;
    private readonly string _work = Path.Combine(TestEnvironment.Root, "updates-" + Guid.NewGuid().ToString("N")[..6]);

    public UpdaterTests()
    {
        var p = _ec.ExportParameters(false);
        var (seed, pub) = Pqc.SigGenerate();
        _pqSeed = seed;
        Updater.TestKeys = (Convert.ToHexString(p.Q.X!), Convert.ToHexString(p.Q.Y!), Convert.ToBase64String(pub));
        Updater.InstalledOverride = new Version(1, 9, 0);
        Updater.WorkFolderOverride = _work;
        Updater.MsiReader = _ => (Updater.UpgradeCode, "1.9.1");
        Updater.ReloadSettings();
        Updater.Settings.Source = "github";
    }

    public void Dispose()
    {
        Updater.TestKeys = null; Updater.InstalledOverride = null; Updater.WorkFolderOverride = null; Updater.MsiReader = null;
        Updater.ReloadSettings();
        _ec.Dispose();
        try { Directory.Delete(_work, true); } catch { }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static readonly byte[] MsiMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] Installer = [.. MsiMagic, .. Encoding.ASCII.GetBytes("pretend this is WhisperDesk 1.9.1")];

    private static string Manifest(string version = "1.9.1", string? file = null, string? url = null, long? bytes = null, string? sha = null, string product = "WhisperDesk")
    {
        file ??= $"WhisperDesk-{version}-x64.msi";
        var pkg = $"{{\"kind\":\"msi\",\"arch\":\"x64\",\"file\":\"{file}\",\"bytes\":{bytes ?? Installer.Length},\"sha256\":\"{sha ?? Convert.ToHexString(SHA256.HashData(Installer)).ToLowerInvariant()}\"" +
                  (url != null ? $",\"url\":\"{url}\"" : "") + "}";
        return $"{{\"product\":\"{product}\",\"version\":\"{version}\",\"released\":\"2026-11-08\",\"notes\":\"Fixes.\",\"packages\":[{pkg}]}}";
    }

    private byte[] EcSig(byte[] data) => Encoding.ASCII.GetBytes(Convert.ToBase64String(_ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    private byte[] PqSig(byte[] data) => Encoding.ASCII.GetBytes(Convert.ToBase64String(Pqc.Sign(_pqSeed, data)));

    /// <summary>A fake github.com: the latest-release redirect, the versioned assets and their download host.</summary>
    private sealed class FakeGitHub(Dictionary<string, Func<HttpResponseMessage>> routes) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var u = request.RequestUri!.AbsoluteUri;
            Requested.Add(u);
            return Task.FromResult(routes.TryGetValue(u, out var f) ? f() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static Func<HttpResponseMessage> Ok(byte[] body) => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    private static Func<HttpResponseMessage> Redirect(string to) => () => { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new Uri(to); return r; };

    private const string Rel = "https://github.com/DarkCipher0x/WhisperDesk/releases/download/v1.9.1/";

    private Dictionary<string, Func<HttpResponseMessage>> Release(byte[] manifest, byte[]? sig = null, byte[]? pq = null, bool withSig = true, bool withPq = true)
    {
        var r = new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["https://github.com/DarkCipher0x/WhisperDesk/releases/latest/download/update.json"] = Redirect(Rel + "update.json"),
            [Rel + "update.json"] = Redirect("https://release-assets.githubusercontent.com/m/update.json"),
            ["https://release-assets.githubusercontent.com/m/update.json"] = Ok(manifest),
            [Rel + "WhisperDesk-1.9.1-x64.msi"] = Redirect("https://release-assets.githubusercontent.com/m/msi"),
            ["https://release-assets.githubusercontent.com/m/msi"] = Ok(Installer),
        };
        if (withSig) r[Rel + "update.json.sig"] = Ok(sig ?? EcSig(manifest));
        if (withPq) r[Rel + "update.json.pq.sig"] = Ok(pq ?? PqSig(manifest));
        return r;
    }

    // ---- the pinned keys -----------------------------------------------------------------------------------------------

    [Fact]
    public void PinnedKeysAreValidPublicKeys()
    {
        using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Convert.FromHexString(Updater.ReleaseKeyX), Y = Convert.FromHexString(Updater.ReleaseKeyY) } });
        Assert.Equal(256, ec.KeySize);
        Assert.Equal(1952, Convert.FromBase64String(Updater.ReleasePqKey).Length);
        Assert.True(Updater.ReleaseKeyPinned && Updater.ReleasePqKeyPinned);
    }

    // ---- signatures ----------------------------------------------------------------------------------------------------

    [Fact]
    public void SignaturesVerifyOnlyTheExactBytesWithTheRightKeys()
    {
        var data = Encoding.UTF8.GetBytes(Manifest());
        Assert.True(Updater.VerifyManifest(data, EcSig(data)));
        Assert.True(Updater.VerifyManifestPq(data, PqSig(data)));

        var changed = Encoding.UTF8.GetBytes(Manifest().Replace("1.9.1", "9.9.9"));
        Assert.False(Updater.VerifyManifest(changed, EcSig(data)));
        Assert.False(Updater.VerifyManifestPq(changed, PqSig(data)));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(Updater.VerifyManifest(data, Encoding.ASCII.GetBytes(Convert.ToBase64String(other.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)))));
        var (otherSeed, _) = Pqc.SigGenerate();
        Assert.False(Updater.VerifyManifestPq(data, Encoding.ASCII.GetBytes(Convert.ToBase64String(Pqc.Sign(otherSeed, data)))));

        Assert.False(Updater.VerifyManifest(data, "not base64"u8.ToArray()));
        Assert.False(Updater.VerifyManifestPq(data, Encoding.ASCII.GetBytes(Convert.ToBase64String(new byte[3309]))));
    }

    // ---- parsing -------------------------------------------------------------------------------------------------------

    private static readonly Uri ManifestUrl = new(Rel + "update.json");

    [Fact]
    public void ParseAcceptsANewerSignedRelease()
    {
        var (info, _) = Updater.Parse(Manifest(), ManifestUrl);
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 9, 1), info!.Version);
        Assert.Equal(new Uri(Rel + "WhisperDesk-1.9.1-x64.msi"), info.Url);
        Assert.Equal("https://github.com/DarkCipher0x/WhisperDesk/releases/tag/v1.9.1", info.ReleasePage);
    }

    [Theory]
    [InlineData("1.9.0")]   // the same version
    [InlineData("1.8.1")]   // a downgrade (an old signed release replayed)
    public void ParseNeverOffersTheSameOrAnOlderVersion(string version) =>
        Assert.Null(Updater.Parse(Manifest(version), ManifestUrl).Info);

    [Fact]
    public void ParseRefusesAnythingItCannotCheck()
    {
        Assert.Null(Updater.Parse(Manifest(product: "CaseKeeper"), ManifestUrl).Info);
        Assert.Null(Updater.Parse(Manifest(file: "WhisperDesk-1.9.2-x64.msi"), ManifestUrl).Info);        // name not tied to the version
        Assert.Null(Updater.Parse(Manifest(file: "evil.exe"), ManifestUrl).Info);
        Assert.Null(Updater.Parse(Manifest(url: "https://evil.example/WhisperDesk-1.9.1-x64.msi"), ManifestUrl).Info);   // another host
        Assert.Null(Updater.Parse(Manifest(url: "http://github.com/x/WhisperDesk-1.9.1-x64.msi"), ManifestUrl).Info);    // not https
        Assert.Null(Updater.Parse(Manifest(url: Rel + "WhisperDesk-1.9.1-x64.msi?x=1"), ManifestUrl).Info);
        Assert.Null(Updater.Parse(Manifest(bytes: 0), ManifestUrl).Info);
        Assert.Null(Updater.Parse(Manifest(bytes: Updater.MaxBytes + 1), ManifestUrl).Info);
        Assert.Null(Updater.Parse(Manifest(sha: "1234"), ManifestUrl).Info);
        Assert.Null(Updater.Parse(Manifest(version: "1.9.1.0"), ManifestUrl).Info);
        Assert.Null(Updater.Parse("{\"product\":\"WhisperDesk\",\"product\":\"WhisperDesk\",\"version\":\"1.9.1\"}", ManifestUrl).Info);   // duplicate key
        Assert.Null(Updater.Parse("not json", ManifestUrl).Info);
        Assert.Null(Updater.Parse("[]", ManifestUrl).Info);
    }

    // ---- the whole check against a fake GitHub ---------------------------------------------------------------------------

    [Fact]
    public async Task CheckFindsASignedReleaseOnGitHub()
    {
        var gh = new FakeGitHub(Release(Encoding.UTF8.GetBytes(Manifest())));
        var (info, msg) = await Updater.CheckAsync(CancellationToken.None, gh);
        Assert.True(info != null, msg);
        Assert.Equal(new Version(1, 9, 1), info!.Version);
    }

    [Fact]
    public async Task CheckOffersNothingWithoutBothValidSignatures()
    {
        var m = Encoding.UTF8.GetBytes(Manifest());
        Assert.Null((await Updater.CheckAsync(CancellationToken.None, new FakeGitHub(Release(m, withSig: false)))).Info);
        Assert.Null((await Updater.CheckAsync(CancellationToken.None, new FakeGitHub(Release(m, withPq: false)))).Info);
        var other = Encoding.UTF8.GetBytes(Manifest("1.9.2"));
        Assert.Null((await Updater.CheckAsync(CancellationToken.None, new FakeGitHub(Release(m, sig: EcSig(other))))).Info);
        Assert.Null((await Updater.CheckAsync(CancellationToken.None, new FakeGitHub(Release(m, pq: PqSig(other))))).Info);
    }

    [Fact]
    public async Task CheckRefusesARedirectOffGitHub()
    {
        var routes = Release(Encoding.UTF8.GetBytes(Manifest()));
        routes["https://github.com/DarkCipher0x/WhisperDesk/releases/latest/download/update.json"] = Redirect("https://evil.example/update.json");
        var gh = new FakeGitHub(routes);
        var (info, msg) = await Updater.CheckAsync(CancellationToken.None, gh);
        Assert.Null(info);
        Assert.Contains("evil.example", msg);
        Assert.DoesNotContain(gh.Requested, u => u.Contains("evil.example"));
    }

    [Fact]
    public async Task CheckFromYourOwnAddressNeedsTheSameSignaturesAndFollowsNoRedirects()
    {
        Updater.Settings.Source = "url";
        Updater.Settings.Endpoint = "https://updates.example/whisperdesk/update.json";
        var m = Encoding.UTF8.GetBytes(Manifest(url: "https://updates.example/whisperdesk/WhisperDesk-1.9.1-x64.msi"));
        var routes = new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["https://updates.example/whisperdesk/update.json"] = Ok(m),
            ["https://updates.example/whisperdesk/update.json.sig"] = Ok(EcSig(m)),
            ["https://updates.example/whisperdesk/update.json.pq.sig"] = Ok(PqSig(m)),
        };
        Assert.NotNull((await Updater.CheckAsync(CancellationToken.None, new FakeGitHub(routes))).Info);
        routes["https://updates.example/whisperdesk/update.json"] = Redirect("https://evil.example/update.json");
        Assert.Null((await Updater.CheckAsync(CancellationToken.None, new FakeGitHub(routes))).Info);
        Assert.NotNull(Updater.ValidateEndpoint("http://updates.example/update.json"));
        Assert.NotNull(Updater.ValidateEndpoint("https://user:pw@updates.example/update.json"));
        Assert.NotNull(Updater.ValidateEndpoint("https://updates.example/update.json?id=me"));
    }

    // ---- the downloaded installer ---------------------------------------------------------------------------------------

    [Fact]
    public async Task DownloadIsVerifiedAgainstTheSignedSizeHashAndMsiIdentity()
    {
        var m = Encoding.UTF8.GetBytes(Manifest());
        var gh = new FakeGitHub(Release(m));
        var (info, _) = await Updater.CheckAsync(CancellationToken.None, gh);
        var (path, v) = await Updater.DownloadAndVerifyAsync(info!, null, CancellationToken.None, gh);
        Assert.True(v.Ok, v.Message);
        Assert.True(File.Exists(path));
        Assert.StartsWith(_work, path);

        Updater.MsiReader = _ => ("{00000000-0000-0000-0000-000000000000}", "1.9.1");
        Assert.Equal(UpdateVerdict.WrongProduct, Updater.Verify(path!, info!).Verdict);
        Updater.MsiReader = _ => (Updater.UpgradeCode, "1.8.1");
        Assert.Equal(UpdateVerdict.WrongVersion, Updater.Verify(path!, info!).Verdict);
        Updater.MsiReader = _ => (Updater.UpgradeCode, "1.9.1");

        File.WriteAllBytes(path!, Installer.Select(b => (byte)(b ^ 1)).ToArray());
        Assert.Equal(UpdateVerdict.HashMismatch, Updater.Verify(path!, info!).Verdict);
        File.WriteAllBytes(path!, [.. Installer, 0]);
        Assert.Equal(UpdateVerdict.SizeMismatch, Updater.Verify(path!, info!).Verdict);
    }

    [Fact]
    public async Task ADownloadThatIsNotWhatWasSignedIsDeleted()
    {
        var m = Encoding.UTF8.GetBytes(Manifest());
        var routes = Release(m);
        var gh = new FakeGitHub(routes);
        var (info, _) = await Updater.CheckAsync(CancellationToken.None, gh);
        routes["https://release-assets.githubusercontent.com/m/msi"] = Ok([.. MsiMagic, .. Encoding.ASCII.GetBytes("pretend this is WhisperDesk 1.9.X")]);
        var (path, v) = await Updater.DownloadAndVerifyAsync(info!, null, CancellationToken.None, gh);
        Assert.Null(path);
        Assert.Equal(UpdateVerdict.HashMismatch, v.Verdict);
        Assert.Empty(Directory.Exists(_work) ? Directory.EnumerateFiles(_work, "*", SearchOption.AllDirectories) : []);

        routes["https://release-assets.githubusercontent.com/m/msi"] = Ok(new byte[Installer.Length + 100]);
        (path, v) = await Updater.DownloadAndVerifyAsync(info!, null, CancellationToken.None, gh);
        Assert.Null(path);
        Assert.Equal(UpdateVerdict.SizeMismatch, v.Verdict);
    }

    [Fact]
    public void TheReleaseScriptsSignaturesVerifyInTheApp()
    {
        // tools\sign-update.ps1 signed the last built update.json (if this PC has one): the app's own code, with the
        // pinned release keys (not the test keys), must accept both signatures, and refuse them for changed bytes.
        var dist = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "WhisperDesk", "dist"));
        var uj = Directory.Exists(dist) ? Directory.EnumerateFiles(dist, "update.json", SearchOption.AllDirectories).FirstOrDefault(f => File.Exists(f + ".pq.sig")) : null;
        if (uj == null) return;
        Updater.TestKeys = null;
        var data = File.ReadAllBytes(uj);
        Assert.True(Updater.VerifyManifest(data, File.ReadAllBytes(uj + ".sig")));
        Assert.True(Updater.VerifyManifestPq(data, File.ReadAllBytes(uj + ".pq.sig")));
        data[^2] ^= 1;
        Assert.False(Updater.VerifyManifest(data, File.ReadAllBytes(uj + ".sig")));
        Assert.False(Updater.VerifyManifestPq(data, File.ReadAllBytes(uj + ".pq.sig")));
    }

    [Fact]
    public void TheRealMsiCarriesThePinnedUpgradeCode()
    {
        // The last built MSI, when this PC has one: Windows Installer reads the same identity the updater checks.
        var dist = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "WhisperDesk", "dist"));
        var msi = Directory.Exists(dist) ? Directory.EnumerateFiles(dist, "WhisperDesk-*-x64.msi", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (msi == null) return;
        var (upgrade, version) = Updater.MsiProps(msi);
        Assert.Equal(Updater.UpgradeCode, upgrade, ignoreCase: true);
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
    }
}
