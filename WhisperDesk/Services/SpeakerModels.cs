using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using WhisperDesk.Services.Security;

namespace WhisperDesk.Services;

/// <summary>
/// Downloads the three small ONNX models used for speaker identification and voice detection (~31 MB total). Each is pinned
/// to a fixed revision or release and checked against its SHA-256; a file that doesn't match is discarded.
/// </summary>
public static class SpeakerModels
{
    private static readonly (string File, string Url, long Size, string Sha256)[] Files =
    {
        ("pyannote-segmentation-3.0.onnx",
         "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/model.onnx",
         5_992_913, "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079"),
        // GitHub release assets (sherpa-onnx). The release publishes no digest for this file; the hash is the one first
        // downloaded and checked by WhisperDesk's developer, so any later change to the file is caught.
        ("wespeaker-voxceleb-resnet34-LM.onnx",
         "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx",
         26_530_550, "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012"),
        ("silero-vad.onnx",
         "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx",
         643_854, "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6"),
    };

    public static string VadModel => Path.Combine(Dir, Files[2].File);

    public static string Dir => Path.Combine(ModelManager.ModelsDir, "speakers");
    public static string SegmentationModel => Path.Combine(Dir, Files[0].File);
    public static string EmbeddingModel => Path.Combine(Dir, Files[1].File);

    public static bool AreDownloaded => Files.All(f => File.Exists(Path.Combine(Dir, f.File)));

    public static async Task EnsureAsync(IProgress<string> status, CancellationToken ct)
    {
        OnnxRuntimeGuard.Require();   // speaker and voice-detection models run on ONNX Runtime
        var missing = Files.Where(f => !File.Exists(Path.Combine(Dir, f.File))).ToList();
        if (missing.Count == 0) return;
        var hosts = missing.Select(f => new Uri(f.Url).Host).Distinct().ToList();
        if (!await NetworkGate.ConfirmModelDownloadAsync("the speaker-recognition and voice-detection models", hosts,
                ModelManager.FormatSize(missing.Sum(f => f.Size))))
            throw new NetworkBlockedException("The speaker models aren't downloaded, and the download wasn't allowed " +
                                              "(Settings → Security & privacy → Network).");
        Directory.CreateDirectory(Dir);
        using var http = NetworkGate.Create(NetFeature.ModelDownload, TimeSpan.FromMinutes(10));
        foreach (var (file, url, size, sha) in missing)
        {
            var target = Path.Combine(Dir, file);
            var partial = target + ".part";
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var src = await response.Content.ReadAsStreamAsync(ct))
                await using (var dst = File.Create(partial))
                {
                    var buf = new byte[1 << 20];
                    long done = 0;
                    int read;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        if (done + read > size) throw new InvalidDataException("A speaker model download is larger than expected; it was discarded.");
                        hash.AppendData(buf, 0, read);
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                        done += read;
                        status.Report($"Downloading speaker model… {done * 100 / size}%");
                    }
                }
                if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), sha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The downloaded {file} failed its SHA-256 check (damaged or altered download); it was discarded.");
                File.Move(partial, target, overwrite: true);
            }
            catch
            {
                try { File.Delete(partial); } catch { }
                throw;
            }
        }
    }
}
