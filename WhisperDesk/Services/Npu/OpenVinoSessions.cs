using System.IO;
using Microsoft.ML.OnnxRuntime;

namespace WhisperDesk.Services.Npu;

/// <summary>
/// The one place ONNX Runtime sessions are created on OpenVINO devices, shared by every NPU workload (Whisper and
/// speaker voiceprints). Compiling a model for the NPU is slow (minutes for Large-v3 Turbo), so on NPU/GPU devices the
/// compiled result is kept as an ONNX Runtime "EP context" model in a <c>compiled</c> folder next to the model and
/// loaded from there next time. The file name carries the device and the OpenVINO plugin version; a cached model that
/// no longer loads (new driver, damaged file) is deleted and compiled again. CPU-device sessions compile in seconds
/// and are not cached (their compiled form would only cost disk space).
/// </summary>
public static class OpenVinoSessions
{
    /// <summary>Developer-only device name: ONNX Runtime's own CPU provider (see <see cref="NpuDetector.DeviceOverride"/>).</summary>
    public const string OrtCpu = "ORT";

    /// <param name="model">The .onnx file.</param>
    /// <param name="device">"NPU", "CPU", "GPU" or <see cref="OrtCpu"/>.</param>
    /// <param name="ov">OpenVINO EP options (reshape_input, load_config).</param>
    /// <param name="what">Human name for progress messages ("speech encoder").</param>
    /// <param name="stage">Receives what is happening right now (for the status bar).</param>
    /// <param name="cacheName">Base name of the compiled copy (default: the model's file name). Keep it stable: a new
    /// name means compiling again on every PC that already has a prepared copy.</param>
    public static InferenceSession Create(string model, string device, IReadOnlyDictionary<string, string>? ov, string what,
                                          IProgress<string>? stage = null, string? cacheName = null)
    {
        if (device == OrtCpu)
        {
            stage?.Report($"Loading the {what} (ONNX Runtime CPU)…");
            return new InferenceSession(model, new SessionOptions());
        }

        string label = DeviceLabel(device);
        if (device.StartsWith("CPU", StringComparison.OrdinalIgnoreCase))
        {
            stage?.Report($"Loading the {what} on the {label}…");
            return new InferenceSession(model, Options(device, ov));
        }

        var cacheDir = CacheDirFor(model);
        var ctx = CachedPath(model, device, cacheName);
        if (File.Exists(ctx))
        {
            try
            {
                stage?.Report($"Loading the prepared {what} on the {label}…");
                return new InferenceSession(ctx, Options(device, ov));
            }
            catch (Exception ex)
            {
                App.Log($"NPU: the prepared {what} no longer loads on the {label}, preparing it again", ex);
                DeleteCached(ctx);
            }
        }

        stage?.Report($"Compiling the {what} for the {label}. This happens once and can take several minutes for large models…");
        try
        {
            Directory.CreateDirectory(cacheDir);
            var so = Options(device, ov);
            so.AddSessionConfigEntry("ep.context_enable", "1");
            so.AddSessionConfigEntry("ep.context_embed_mode", "0");
            so.AddSessionConfigEntry("ep.context_file_path", ctx);
            return new InferenceSession(model, so);
        }
        catch (Exception ex)
        {
            // Caching is an optimisation: if writing the prepared model fails, compile it the ordinary way.
            App.Log($"NPU: could not keep the compiled {what}; compiling it without a cache", ex);
            DeleteCached(ctx);
            stage?.Report($"Compiling the {what} for the {label} (without cache)…");
            return new InferenceSession(model, Options(device, ov));
        }
    }

    public static string DeviceLabel(string device) =>
        device.StartsWith("NPU", StringComparison.OrdinalIgnoreCase) ? "NPU"
        : device.StartsWith("GPU", StringComparison.OrdinalIgnoreCase) ? "Intel GPU"
        : device == OrtCpu ? "CPU (ONNX Runtime)" : "CPU";

    /// <summary>Folder that holds the compiled versions of the models in <paramref name="model"/>'s folder.</summary>
    public static string CacheDirFor(string model) => Path.Combine(Path.GetDirectoryName(model)!, "compiled");

    /// <summary>True when a prepared (compiled) copy of this model exists for the device, i.e. loading will be quick.</summary>
    public static bool IsPrepared(string model, string device, string? cacheName = null) =>
        device == OrtCpu || device.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) || File.Exists(CachedPath(model, device, cacheName));

    private static string CachedPath(string model, string device, string? cacheName) =>
        Path.Combine(CacheDirFor(model), $"{cacheName ?? Path.GetFileNameWithoutExtension(model)}.{device.ToLowerInvariant()}.ov{NpuDetector.PluginVersion}.onnx");

    /// <summary>Deletes every compiled model next to <paramref name="model"/> (after a failure, so the next run recompiles).</summary>
    public static void ClearCache(string model)
    {
        try { var d = CacheDirFor(model); if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
    }

    private static void DeleteCached(string ctx)
    {
        // The EP context model and its sidecar blob(s) share the base name.
        try
        {
            var dir = Path.GetDirectoryName(ctx)!;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileNameWithoutExtension(ctx) + "*")) File.Delete(f);
        }
        catch { }
    }

    private static SessionOptions Options(string device, IReadOnlyDictionary<string, string>? ov) =>
        NpuDetector.CreateSessionOptions(device, ov) ?? throw new InvalidOperationException($"OpenVINO device {device} is not available.");
}
