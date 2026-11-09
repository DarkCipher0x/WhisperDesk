using System.IO;
using System.Runtime.InteropServices;

namespace WhisperDesk.Services;

/// <summary>
/// The Microsoft Visual C++ runtime that the native engines need (ONNX Runtime, whisper.cpp, OpenVINO). WhisperDesk ships
/// its own copy next to the exe (WhisperDesk.csproj, BundleVcRuntime) because a PC can have none at all (a fresh Windows,
/// the Store's test machines) or an older one in System32 that ONNX Runtime refuses: 1.8.1 from the Store then failed with
/// "onnxruntime.dll couldn't be loaded" and Live never started. Loading our copies by full path first means every later
/// native library binds to them, whatever is (or isn't) installed system-wide.
/// </summary>
public static class VcRuntime
{
    /// <summary>In dependency order: the C runtime first, then the C++ library and OpenMP that build on it.</summary>
    public static readonly string[] Files = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "vcomp140.dll"];

    /// <summary>The files that weren't next to the exe or wouldn't load (empty when all is well).</summary>
    public static List<string> Missing { get; } = [];

    /// <summary>Call once, first thing in Main, before any native engine is loaded.</summary>
    public static void Init()
    {
        foreach (var name in Files)
        {
            var path = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(path)) { Missing.Add(name); continue; }   // dev builds without the bundle: System32's is used
            try { NativeLibrary.Load(path); }
            catch (Exception ex) { Missing.Add(name); App.Log("VC++ runtime " + name, ex); }
        }
    }
}
