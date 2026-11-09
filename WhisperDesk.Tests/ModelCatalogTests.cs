using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WhisperDesk.Services;
using Xunit;

namespace WhisperDesk.Tests;

/// <summary>
/// The signed model list (Services/ModelCatalog.cs): the built-in list, the strict parser, fetching a newer signed list
/// from a fake GitHub (no network) with throw-away test keys, and the per-file states the Models window shows.
/// </summary>
[Collection("Updater")]
public sealed class ModelCatalogTests : IDisposable
{
    private readonly ECDsa _ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _pqSeed;
    private readonly string _models = Path.Combine(TestEnvironment.Root, "models-" + Guid.NewGuid().ToString("N")[..6]);
    private readonly string? _oldModelsPath;

    public ModelCatalogTests()
    {
        var p = _ec.ExportParameters(false);
        var (seed, pub) = Pqc.SigGenerate();
        _pqSeed = seed;
        Updater.TestKeys = (Convert.ToHexString(p.Q.X!), Convert.ToHexString(p.Q.Y!), Convert.ToBase64String(pub));
        Updater.ReloadSettings();
        Updater.Settings.Source = "github";
        _oldModelsPath = AppSettings.Current.ModelsPath;
        AppSettings.Current.ModelsPath = _models;
        Directory.CreateDirectory(_models);
        ClearCache();
    }

    public void Dispose()
    {
        ClearCache();
        AppSettings.Current.ModelsPath = _oldModelsPath!;
        Updater.TestKeys = null;
        Updater.ReloadSettings();
        _ec.Dispose();
        try { Directory.Delete(_models, true); } catch { }
    }

    private static void ClearCache()
    {
        foreach (var f in new[] { ModelCatalog.CachePath, ModelCatalog.CachePath + ".sig", ModelCatalog.CachePath + ".pq.sig" })
            try { File.Delete(f); } catch { }
        ModelCatalog.ResetForTests();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static string EmbeddedText => Encoding.UTF8.GetString(ModelCatalog.Embedded.Bytes);

    private static byte[] WithSequence(int seq, string? extraReplace = null, string? with = null)
    {
        var t = EmbeddedText.Replace("\"sequence\": 1,", $"\"sequence\": {seq},");
        if (extraReplace != null) t = t.Replace(extraReplace, with);
        return Encoding.UTF8.GetBytes(t);
    }

    private byte[] EcSig(byte[] data) => Encoding.ASCII.GetBytes(Convert.ToBase64String(_ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    private byte[] PqSig(byte[] data) => Encoding.ASCII.GetBytes(Convert.ToBase64String(Pqc.Sign(_pqSeed, data)));

    private sealed class FakeGitHub(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsoluteUri, out var b)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private FakeGitHub Published(byte[] body, byte[]? sig = null, byte[]? pq = null, bool signed = true)
    {
        var u = ModelCatalog.GitHubLatest;
        var d = new Dictionary<string, byte[]> { [u] = body };
        if (signed) { d[u + ".sig"] = sig ?? EcSig(body); d[u + ".pq.sig"] = pq ?? PqSig(body); }
        return new FakeGitHub(d);
    }

    private static string Mutate(Action<Dictionary<string, object?>> change, int index = 0)
    {
        // Rebuilds the built-in list with one model changed (keeps everything else valid).
        using var doc = JsonDocument.Parse(EmbeddedText);
        var models = doc.RootElement.GetProperty("models").EnumerateArray()
            .Select(m => m.EnumerateObject().ToDictionary(p => p.Name, p => (object?)(p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt64() : p.Value.GetString())))
            .ToList();
        change(models[index]);
        return JsonSerializer.Serialize(new { product = "WhisperDesk", kind = "model-catalog", sequence = 1, published = "2026-11-08", models });
    }

    private static string? Why(string json) => ModelCatalog.Parse(Encoding.UTF8.GetBytes(json), "test").Why;

    // ---- the built-in list ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheBuiltInListIsValidAndComplete()
    {
        var c = ModelCatalog.Embedded;
        Assert.True(c.Sequence >= 1);
        Assert.Equal(1, c.Models.Count(m => m.Role == "segmentation"));
        Assert.Equal(1, c.Models.Count(m => m.Role == "embedding"));
        Assert.Equal(1, c.Models.Count(m => m.Role == "vad"));
        Assert.True(c.Models.Count(m => m.IsWhisper) >= 6);
        Assert.All(c.Models, m => Assert.True(ModelCatalog.AllowedSource(m.Url), m.Id));
        // Hugging Face downloads are pinned to a commit, never a branch.
        Assert.All(c.Models.Where(m => m.Url.Host == "huggingface.co"), m => Assert.Matches("/resolve/[0-9a-f]{40}/", m.Url.AbsolutePath));
        // The built-in list as the app embeds it is byte-identical to the file in the repository (that's what gets signed).
        var repo = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WhisperDesk", "models", "catalog.json");
        if (File.Exists(repo)) Assert.Equal(File.ReadAllBytes(repo), c.Bytes);
    }

    [Theory]
    [InlineData("tiny", "ggml-tiny.bin")]
    [InlineData("base", "ggml-base.bin")]
    [InlineData("small", "ggml-small.bin")]
    [InlineData("medium", "ggml-medium.bin")]
    [InlineData("large-v3-turbo", "ggml-largev3turbo.bin")]
    [InlineData("large-v3", "ggml-largev3.bin")]
    public void TheOriginalSixKeepTheirFileNames(string id, string local)
    {
        // 1.x downloaded these as ggml-{GgmlType}.bin: existing downloads must still be found, not fetched again.
        Assert.Equal(local, ModelCatalog.Embedded.Find(id)!.LocalName);
    }

    [Fact]
    public void SpeakerModelsLiveInTheSpeakersFolder()
    {
        foreach (var role in new[] { "segmentation", "embedding", "vad" })
            Assert.StartsWith("speakers" + Path.DirectorySeparatorChar, ModelCatalog.Embedded.Role(role).LocalName);
        Assert.Equal("wespeaker-voxceleb-resnet34-LM.onnx", Path.GetFileName(SpeakerModels.EmbeddingModel));
    }

    [Fact]
    public void TheModelPickerListsEveryWhisperModel()
    {
        var keys = ModelManager.Models.Select(m => m.Key).ToList();
        Assert.Contains("LargeV3Turbo", keys);          // saved settings from 1.x still select the same model
        Assert.Contains("catalog:large-v3-turbo-q8_0", keys);
        Assert.Equal(ModelCatalog.Embedded.Models.Count(m => m.IsWhisper), keys.Count);
    }

    // ---- strict parsing --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheParserAcceptsAnUnchangedRebuild() => Assert.Null(Why(Mutate(_ => { })));

    [Theory]
    [InlineData("https://example.com/a/b/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-tiny.bin")]   // other host
    [InlineData("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin")]                    // a branch, not a commit
    [InlineData("http://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-tiny.bin")]   // not https
    [InlineData("https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-tiny.bin?x=1")]
    [InlineData("https://huggingface.co:8443/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-tiny.bin")]
    [InlineData("https://user@huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-tiny.bin")]
    [InlineData("https://github.com/someone/repo/archive/refs/heads/main.zip")]                                  // not a release asset
    public void DownloadAddressesMustBePinnedHuggingFaceOrGitHubReleases(string url) =>
        Assert.Contains("download address not allowed", Why(Mutate(m => m["url"] = url)));

    [Theory]
    [InlineData("file", "../ggml-tiny.bin")]
    [InlineData("file", "ggml-tiny.exe")]
    [InlineData("file", "ggml-tiny.onnx")]   // a Whisper model can't be an ONNX file
    [InlineData("sha256", "ABC")]
    [InlineData("role", "installer")]
    [InlineData("ggml", "NotAType")]
    [InlineData("id", "Bad Id")]
    public void BadFieldsRejectTheWholeList(string field, string value) => Assert.NotNull(Why(Mutate(m => m[field] = value)));

    [Fact]
    public void SizesMustBeSane()
    {
        Assert.NotNull(Why(Mutate(m => m["bytes"] = 0L)));
        Assert.NotNull(Why(Mutate(m => m["bytes"] = 6_000_000_000L)));
    }

    [Fact]
    public void DuplicatesAndMissingRolesAreRejected()
    {
        Assert.Contains("duplicate model id", Why(Mutate(m => m["id"] = "base")));
        Assert.Contains("share a file name", Why(Mutate(m => { m.Remove("ggml"); m["file"] = "ggml-large-v3-turbo-q8_0.bin"; })));
        var vadIndex = ModelCatalog.Embedded.Models.ToList().FindIndex(m => m.Role == "vad");
        Assert.Contains("needs exactly one", Why(Mutate(m => m["role"] = "embedding", vadIndex)));
        // A duplicate JSON property (two "url"s, the second one winning in some parsers) is refused outright.
        var dup = EmbeddedText.Replace("\"sequence\": 1,", "\"sequence\": 1, \"sequence\": 9,");
        Assert.NotNull(Why(dup));
        Assert.NotNull(Why("[]"));
        Assert.NotNull(Why("{\"product\":\"WhisperDesk\",\"kind\":\"update\"}"));
    }

    // ---- fetching a newer list --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANewerSignedListIsSavedAndUsed()
    {
        var newer = WithSequence(2, "\"notes\": \"fastest, rough\"", "\"notes\": \"fastest, rough (updated)\"");
        var r = await ModelCatalog.CheckAsync(CancellationToken.None, Published(newer));
        Assert.True(r.Changed, r.Message);
        Assert.Equal(2, ModelCatalog.Current.Sequence);
        Assert.Equal("fastest, rough (updated)", ModelCatalog.Current.Find("tiny")!.Notes);
        // Kept with both signatures and re-verified after a restart.
        ModelCatalog.ResetForTests();
        Assert.Equal(2, ModelCatalog.Current.Sequence);
        // ...but not if the saved copy is altered afterwards.
        File.WriteAllBytes(ModelCatalog.CachePath, WithSequence(3));
        ModelCatalog.ResetForTests();
        Assert.Equal(ModelCatalog.Embedded.Sequence, ModelCatalog.Current.Sequence);
    }

    [Fact]
    public async Task TheSameListIsUpToDate()
    {
        var r = await ModelCatalog.CheckAsync(CancellationToken.None, Published(ModelCatalog.Embedded.Bytes));
        Assert.False(r.Changed);
        Assert.Contains("up to date", r.Message);
    }

    [Fact]
    public async Task OlderOrSameNumberedDifferentListsAreRefused()
    {
        Assert.True((await ModelCatalog.CheckAsync(CancellationToken.None, Published(WithSequence(5)))).Changed);
        var older = await ModelCatalog.CheckAsync(CancellationToken.None, Published(WithSequence(4)));
        Assert.False(older.Changed);
        Assert.Contains("newer than the published one", older.Message);
        var same = await ModelCatalog.CheckAsync(CancellationToken.None, Published(WithSequence(5, "\"notes\": \"fastest, rough\"", "\"notes\": \"other\"")));
        Assert.False(same.Changed);
        Assert.Equal(5, ModelCatalog.Current.Sequence);
        Assert.Equal("fastest, rough", ModelCatalog.Current.Find("tiny")!.Notes);
    }

    [Fact]
    public async Task UnsignedOrWronglySignedListsAreNeverUsed()
    {
        var newer = WithSequence(7);
        Assert.Contains("no signature", (await ModelCatalog.CheckAsync(CancellationToken.None, Published(newer, signed: false))).Message);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrongEc = Encoding.ASCII.GetBytes(Convert.ToBase64String(other.SignData(newer, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        Assert.Contains("don't match", (await ModelCatalog.CheckAsync(CancellationToken.None, Published(newer, sig: wrongEc))).Message);
        var (otherSeed, _) = Pqc.SigGenerate();
        var wrongPq = Encoding.ASCII.GetBytes(Convert.ToBase64String(Pqc.Sign(otherSeed, newer)));
        Assert.Contains("don't match", (await ModelCatalog.CheckAsync(CancellationToken.None, Published(newer, pq: wrongPq))).Message);
        // Signed, but the signature covers different bytes.
        Assert.Contains("don't match", (await ModelCatalog.CheckAsync(CancellationToken.None, Published(newer, sig: EcSig(WithSequence(8))))).Message);
        Assert.Equal(ModelCatalog.Embedded.Sequence, ModelCatalog.Current.Sequence);
        Assert.False(File.Exists(ModelCatalog.CachePath));
    }

    [Fact]
    public async Task ASignedButInvalidListIsNotUsed()
    {
        var bad = WithSequence(9, "\"role\": \"vad\"", "\"role\": \"whisper\"");
        var r = await ModelCatalog.CheckAsync(CancellationToken.None, Published(bad));
        Assert.False(r.Changed);
        Assert.Contains("invalid", r.Message);
    }

    [Fact]
    public async Task NothingPublishedYetKeepsTheBuiltInList()
    {
        var r = await ModelCatalog.CheckAsync(CancellationToken.None, new FakeGitHub([]));
        Assert.False(r.Changed);
        Assert.Contains("built-in list", r.Message);
    }

    [Fact]
    public async Task AnUpdatedModelYouHaveIsReported()
    {
        var tiny = ModelCatalog.Embedded.Find("tiny")!;
        Directory.CreateDirectory(Path.GetDirectoryName(tiny.LocalPath)!);
        File.WriteAllBytes(tiny.LocalPath, [1, 2, 3]);
        var newSha = new string('a', 64);
        var r = await ModelCatalog.CheckAsync(CancellationToken.None, Published(WithSequence(2, tiny.Sha256, newSha)));
        Assert.True(r.Changed);
        Assert.Contains(r.UpdatedModels, m => m.Id == "tiny");
        Assert.Empty(r.NewModels);
    }

    // ---- files on disk --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FileStatesFollowTheirVerifiedHash()
    {
        var tiny = ModelCatalog.Current.Find("tiny")!;
        Assert.Equal(ModelState.NotDownloaded, ModelCatalog.QuickState(tiny));

        Directory.CreateDirectory(Path.GetDirectoryName(tiny.LocalPath)!);
        File.WriteAllBytes(tiny.LocalPath, Encoding.ASCII.GetBytes("not really a model"));
        Assert.Equal(ModelState.Checking, ModelCatalog.QuickState(tiny));
        // Doesn't match any trusted hash: damaged, and nothing is recorded for it.
        Assert.Equal(ModelState.Damaged, await ModelCatalog.CheckFileAsync(tiny, CancellationToken.None));
        Assert.Null(ModelCatalog.KnownSha(tiny));

        // A 1.x record (size|time, no hash) means it was checked against the built-in pin.
        var info = new FileInfo(tiny.LocalPath);
        File.WriteAllText(Path.Combine(_models, "verified.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { ["ggml-tiny.bin"] = $"{info.Length}|{info.LastWriteTimeUtc.Ticks}" }));
        Assert.Equal(tiny.Sha256, ModelCatalog.KnownSha(tiny));
        Assert.Equal(ModelState.UpToDate, ModelCatalog.QuickState(tiny));

        // Recorded with an older trusted hash: an update is available.
        ModelCatalog.Record(tiny, new string('b', 64));
        Assert.Equal(ModelState.UpdateAvailable, ModelCatalog.QuickState(tiny));

        // Changing the file invalidates the record.
        File.AppendAllText(tiny.LocalPath, "x");
        Assert.Equal(ModelState.Checking, ModelCatalog.QuickState(tiny));

        ModelCatalog.Remove(tiny);
        Assert.False(File.Exists(tiny.LocalPath));
        Assert.Equal(ModelState.NotDownloaded, ModelCatalog.QuickState(tiny));
    }

    [Fact]
    public void OnlyFilesNoModelUsesAreOldFiles()
    {
        var tiny = ModelCatalog.Current.Find("tiny")!;
        var vad = ModelCatalog.Current.Role("vad");
        foreach (var p in new[] { tiny.LocalPath, vad.LocalPath, Path.Combine(_models, "ggml-large-v2.bin"), Path.Combine(_models, "speakers", "old-vad.onnx"),
                                  Path.Combine(_models, "custom", "ggml-mine.bin"), Path.Combine(_models, "notes.txt") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, [0]);
        }
        var unused = ModelCatalog.Unused().Select(f => Path.GetRelativePath(_models, f.FullName)).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "ggml-large-v2.bin", Path.Combine("speakers", "old-vad.onnx") }, unused);
    }
}
