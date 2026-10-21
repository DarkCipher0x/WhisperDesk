using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Whisper.net.Ggml;

namespace WhisperDesk.Services.Npu;

/// <summary>
/// The ONNX exports of the Whisper models that the NPU engine runs (onnx-community on Hugging Face, OpenAI's MIT-licensed
/// weights). Downloaded on first use into the models folder (<c>onnx\&lt;name&gt;\</c>), never shipped in the installer.
/// Only some models have an ONNX variant; the others always run on whisper.cpp. Every file is pinned to a revision and
/// checked against its SHA-256 before it is used, so a damaged or altered download is never loaded.
/// </summary>
public static class WhisperOnnxModels
{
    public const string Encoder = "encoder_model_fp16.onnx";
    public const string Decoder = "decoder_model_fp16.onnx";
    public const string DecoderWithPast = "decoder_with_past_model_fp16.onnx";

    /// <summary>Repo-relative source → local file name. fp16 weights: half the download, native precision on the NPU.</summary>
    private static readonly (string Source, string File)[] Files =
    {
        ("config.json", "config.json"),
        ("generation_config.json", "generation_config.json"),
        ("preprocessor_config.json", "preprocessor_config.json"),
        ("added_tokens.json", "added_tokens.json"),
        ("vocab.json", "vocab.json"),
        ("merges.txt", "merges.txt"),
        ("onnx/" + Encoder, Encoder),
        ("onnx/" + Decoder, Decoder),
        ("onnx/" + DecoderWithPast, DecoderWithPast),
    };

    private sealed record Repo(string Name, string Revision, string Size, string[] Sha256);

    /// <summary>Pinned revision and the SHA-256 of each entry of <see cref="Files"/> (same order).</summary>
    private static readonly Dictionary<GgmlType, Repo> Repos = new()
    {
        [GgmlType.Base] = new("onnx-community/whisper-base", "1846881b6b3a3024392c1eea3ad983695bc23925", "245 MB", new[]
        {
            "f4d0608f7d918166da7edb3e188de5ef1bfe70d9802e785d271fd88111e9cf4b", "61070cf8de25b1e9256e8e102ded49d8d24a8369ed36ef84fdf21549e68125a0",
            "a6a76d28c93edb273669eb9e0b0636a2bddbb1272c3261e47b7ca6dfdbac1b8d", "9715fd2243b6f06a5858b5e32950d2853f73dd5bc201aafcf76f5082a2d8acd1",
            "50d6a919f0a0601d56a04eb583c780d18553aa388254ba3158eb6a00f13e2c1a", "2df2990a395e35e8dfbc7511e08c12d56018d8d04691e0133e5d63b21e154dc6",
            "ffba51b20185377c1b89362b1128148d0132de936a32b200af973da2a11a5369", "8acaa9ed6e16d309df746b097a2ea1363bf90ceeee51813b8aa67aca289ba3fc",
            "a80df24ce32fef82b8415dce6e37c09e958004b4a5a9170b4628a5cfddd82e3d",
        }),
        [GgmlType.Small] = new("onnx-community/whisper-small", "36050c46d777d46dc4b5f43f6d90574fc38f8732", "765 MB", new[]
        {
            "457854d452f17661e197d74aee12b8e74fb75ba30ebfaa7426d0d61ea1e08a18", "f538b28220c6a6d6f1af1458d4141cacb4ef4963df3de98a19490440c412ddf0",
            "a6a76d28c93edb273669eb9e0b0636a2bddbb1272c3261e47b7ca6dfdbac1b8d", "9715fd2243b6f06a5858b5e32950d2853f73dd5bc201aafcf76f5082a2d8acd1",
            "50d6a919f0a0601d56a04eb583c780d18553aa388254ba3158eb6a00f13e2c1a", "2df2990a395e35e8dfbc7511e08c12d56018d8d04691e0133e5d63b21e154dc6",
            "5549cd8666ff4b694ceb128bfa48b95bdcceec29075cf2c2212f90002cc058de", "bbf06f4be0e38b76d6cf107da618a554f311c406a873866000e23383131e3ba5",
            "3a65cf85fdb5f77bda73bc22c360bc1de72b226f6c63dc3b0a0be17db640ac86",
        }),
        [GgmlType.LargeV3Turbo] = new("onnx-community/whisper-large-v3-turbo", "360ebcde2559d60bb474678be3c1de9ef347d01a", "1.9 GB", new[]
        {
            "35cd83669f75bc2867f3b3a4461850392d5e308cd6ea951c3700539883c28df1", "16f95291d2f47c944d3c2b19390bba7965666555c1ea2a0bdc850d1fab45612f",
            "7ccc62c6f2765af1f3b46c00c9b5894426835a05021c8b9c01eecb6dfb542711", "3c51f66c4c21f9e126970078f11ae77a78c74aee8df606ee9daba86e467108e0",
            "e2aa043ef015641d363d8288e7c241c85e36a5c761fb303598e0710233344387", "2df2990a395e35e8dfbc7511e08c12d56018d8d04691e0133e5d63b21e154dc6",
            "fdadc70836e6b028fd5e580417c312208dad073d2d01e509e2d127c1373399d8", "56026785e9d0ae5c93e76c5d5ac9560e0568051ff664fb56c3a9eb32745bd6c5",
            "e971827af2f12c152491e21594e9eecf5415ad568d6dc0d6fe794713dfbae815",
        }),
    };

    public static bool HasOnnx(ModelInfo model) => !model.IsCustom && model.Type is { } t && Repos.ContainsKey(t);

    /// <summary>Download size of the ONNX variant ("245 MB"), or null when the model has none.</summary>
    public static string? SizeFor(ModelInfo model) => HasOnnx(model) ? Repos[model.Type!.Value].Size : null;

    public static string DirFor(ModelInfo model) =>
        Path.Combine(ModelManager.ModelsDir, "onnx", Repos[model.Type!.Value].Name.Split('/')[1]);

    /// <summary>Compiled-model caches inside the ONNX folders: device- and driver-specific, so left out of backups.</summary>
    public static bool IsCompiledCache(string path) =>
        path.Replace('/', '\\').Contains("\\onnx\\", StringComparison.OrdinalIgnoreCase) &&
        path.Replace('/', '\\').Contains("\\compiled\\", StringComparison.OrdinalIgnoreCase);

    public static bool IsDownloaded(ModelInfo model) =>
        HasOnnx(model) && Files.All(f => File.Exists(Path.Combine(DirFor(model), f.File)));

    /// <summary>Downloads whatever is missing and returns the model folder. Progress: bytes so far / total bytes.</summary>
    public static async Task<string> EnsureAsync(ModelInfo model, IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        if (!HasOnnx(model)) throw new NotSupportedException($"{model.Name} has no ONNX (NPU) version.");
        var repo = Repos[model.Type!.Value];
        var dir = DirFor(model);
        Directory.CreateDirectory(dir);
        var missing = Files.Select((f, i) => (f.Source, f.File, Sha: repo.Sha256[i]))
            .Where(f => !File.Exists(Path.Combine(dir, f.File))).ToList();
        if (missing.Count == 0) return dir;

        if (!await Security.NetworkGate.ConfirmModelDownloadAsync($"the NPU version of {model.Name}", ModelManager.DownloadHost, repo.Size))
            throw new Security.NetworkBlockedException($"The NPU version of {model.Name} isn't downloaded, and the download wasn't allowed.");
        using var http = Security.NetworkGate.Create(Security.NetFeature.ModelDownload, Timeout.InfiniteTimeSpan);
        // Sizes first, so progress covers the whole set rather than restarting per file.
        long total = 0, done = 0;
        foreach (var f in missing)
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, Url(repo, f.Source));
            using var r = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            total += r.IsSuccessStatusCode ? r.Content.Headers.ContentLength ?? 0 : 0;
        }

        foreach (var f in missing)
        {
            var target = Path.Combine(dir, f.File);
            var partial = target + ".part";
            try
            {
                using var response = await http.GetAsync(Url(repo, f.Source), HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var src = await response.Content.ReadAsStreamAsync(ct))
                await using (var dst = File.Create(partial))
                {
                    var buf = new byte[1 << 20];
                    int read;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        hash.AppendData(buf, 0, read);
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                        done += read;
                        progress.Report((done, Math.Max(total, done)));
                    }
                }
                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actual, f.Sha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The downloaded {f.File} for {model.Name} failed its SHA-256 check (damaged or altered download); it was not kept.");
                File.Move(partial, target, overwrite: true);
            }
            catch
            {
                try { File.Delete(partial); } catch { }
                throw;
            }
        }
        return dir;
    }

    private static string Url(Repo repo, string path) => $"https://huggingface.co/{repo.Name}/resolve/{repo.Revision}/{path}";
}
