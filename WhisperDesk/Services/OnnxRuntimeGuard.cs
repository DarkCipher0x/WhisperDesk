using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WhisperDesk.Services;

/// <summary>
/// Makes sure WhisperDesk only ever uses its own onnxruntime.dll (the one next to the exe). Windows ships an older
/// onnxruntime.dll in System32; if ours is missing (a damaged install or an upgrade that skipped the file), the default DLL
/// search would load that one, and ONNX Runtime's managed wrapper then crashes the whole process with an access violation
/// that can't be caught. So: load ours by full path before anything uses it, refuse any other, and when it is missing switch
/// the features that need it (speaker recognition, voice detection, the NPU) off with a clear message instead of crashing.
/// </summary>
public static class OnnxRuntimeGuard
{
    private const int NeedMajor = 1, NeedMinor = 28;   // Microsoft.ML.OnnxRuntime 1.28 needs at least this native API
    private static IntPtr _handle;

    public static bool Available { get; private set; }

    /// <summary>Why ONNX Runtime can't be used (shown to the user), or null.</summary>
    public static string? Problem { get; private set; }

    /// <summary>Call once, first thing in Main, before any ONNX Runtime or sherpa-onnx type is touched.</summary>
    public static void Init()
    {
        var root = AppContext.BaseDirectory;
        var path = new[] { Path.Combine(root, "onnxruntime.dll"), Path.Combine(root, "runtimes", "win-x64", "native", "onnxruntime.dll") }
            .FirstOrDefault(File.Exists);
        try
        {
            if (path == null)
            {
                Problem = "Part of WhisperDesk is missing (onnxruntime.dll). Speaker recognition, voice detection and the NPU are switched off. " +
                          PackageInfo.RepairHint + " to get them back.";
                return;
            }
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            if (v.FileMajorPart < NeedMajor || (v.FileMajorPart == NeedMajor && v.FileMinorPart < NeedMinor))
            {
                Problem = $"WhisperDesk's onnxruntime.dll is too old ({v.FileVersion}). Speaker recognition, voice detection and the NPU are switched off. {PackageInfo.RepairHint}.";
                return;
            }
            // Loaded by full path: from now on every "onnxruntime" import in this process (managed or sherpa-onnx's native
            // library) gets this module, never System32's.
            _handle = NativeLibrary.Load(path);
            NativeLibrary.SetDllImportResolver(typeof(Microsoft.ML.OnnxRuntime.OrtEnv).Assembly, Resolve);
            Available = true;
        }
        catch (Exception ex)
        {
            Problem = $"WhisperDesk's onnxruntime.dll couldn't be loaded. Speaker recognition, voice detection and the NPU are switched off. {PackageInfo.RepairHint}.";
            App.Log("ONNX Runtime", ex);
        }
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? search) =>
        name.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase) && !name.Contains("providers", StringComparison.OrdinalIgnoreCase)
            ? _handle : IntPtr.Zero;

    /// <summary>Throws a clear, catchable error instead of letting a feature reach a missing or wrong native library.</summary>
    public static void Require()
    {
        if (!Available) throw new InvalidOperationException(Problem ?? "ONNX Runtime is not available.");
        if (_telemetryOff) return;
        _telemetryOff = true;
        // The first time a feature really uses ONNX Runtime (not at start-up): switch its telemetry events off.
        try { Microsoft.ML.OnnxRuntime.OrtEnv.Instance().DisableTelemetryEvents(); } catch { }
    }

    private static bool _telemetryOff;
}
