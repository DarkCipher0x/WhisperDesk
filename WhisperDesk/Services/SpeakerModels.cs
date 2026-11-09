using System.IO;
using WhisperDesk.Services.Security;

namespace WhisperDesk.Services;

/// <summary>
/// The three small ONNX models used for speaker identification and voice detection (~31 MB together). Where each comes
/// from and its SHA-256 are in the model list (<see cref="ModelCatalog"/>, roles segmentation, embedding and vad): pinned
/// to a fixed revision or release, and a file that doesn't match is discarded. Tools → Models updates them.
/// </summary>
public static class SpeakerModels
{
    private static IEnumerable<CatalogModel> Files => new[] { "segmentation", "embedding", "vad" }.Select(ModelCatalog.Current.Role);

    public static string Dir => Path.Combine(ModelManager.ModelsDir, "speakers");
    public static string SegmentationModel => ModelCatalog.Current.Role("segmentation").LocalPath;
    public static string EmbeddingModel => ModelCatalog.Current.Role("embedding").LocalPath;
    public static string VadModel => ModelCatalog.Current.Role("vad").LocalPath;

    public static bool AreDownloaded => Files.All(f => File.Exists(f.LocalPath));

    public static async Task EnsureAsync(IProgress<string> status, CancellationToken ct)
    {
        OnnxRuntimeGuard.Require();   // speaker and voice-detection models run on ONNX Runtime
        var missing = Files.Where(f => !File.Exists(f.LocalPath)).ToList();
        if (missing.Count == 0) return;
        var hosts = missing.Select(f => f.Url.Host).Distinct().ToList();
        if (!await NetworkGate.ConfirmModelDownloadAsync("the speaker-recognition and voice-detection models", hosts,
                ModelManager.FormatSize(missing.Sum(f => f.Bytes))))
            throw new NetworkBlockedException("The speaker models aren't downloaded, and the download wasn't allowed " +
                                              "(Settings → Security & privacy → Network).");
        foreach (var m in missing)
            await ModelCatalog.DownloadAsync(m, new Progress<long>(done => status.Report($"Downloading speaker model… {done * 100 / m.Bytes}%")), ct, consented: true);
    }
}
