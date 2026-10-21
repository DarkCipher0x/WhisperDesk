using System.IO;
using Microsoft.ML.OnnxRuntime;

namespace WhisperDesk.Services.Npu;

/// <summary>What the start-up probe found: the OpenVINO devices on this PC and whether the NPU passed a test inference.</summary>
public sealed record NpuProbe(bool Available, string Summary, string? Detail, IReadOnlyList<string> Devices)
{
    public static NpuProbe None(string summary, string? detail = null) => new(false, summary, detail, Array.Empty<string>());
}

/// <summary>
/// Finds an NPU through Intel's OpenVINO execution-provider plugin for ONNX Runtime. The plugin is registered once per
/// process; sessions pick a device from <see cref="Devices"/>. The probe runs in the background at start-up and is cheap
/// to await later: a PC without the plugin, without Intel hardware or without an NPU driver simply reports "no NPU".
/// </summary>
public static class NpuDetector
{
    private const string Registration = "whisperdesk_openvino";
    private static readonly object Gate = new();
    private static Task<NpuProbe>? _probe;
    private static bool _registered;
    private static IReadOnlyList<OrtEpDevice> _devices = Array.Empty<OrtEpDevice>();

    /// <summary>The probe result (starts the probe if nothing did yet).</summary>
    public static Task<NpuProbe> ProbeAsync()
    {
        lock (Gate) return _probe ??= Task.Run(Probe);
    }

    /// <summary>The result if the probe already finished, else null (the Settings line uses this; never blocks).</summary>
    public static NpuProbe? Result => _probe is { IsCompletedSuccessfully: true } t ? t.Result : null;

    /// <summary>Every OpenVINO device the plugin exposes ("CPU", "GPU", "NPU"…), empty when the plugin isn't loaded.</summary>
    public static IReadOnlyList<string> Devices => _devices.Select(OvDevice).ToList();

    /// <summary>The OpenVINO device name of an EP device entry: the plugin's "ov_device" metadata, else the hardware type.</summary>
    public static string OvDevice(OrtEpDevice d)
    {
        try
        {
            var meta = d.EpMetadata?.Entries;
            if (meta != null && meta.TryGetValue("ov_device", out var ov) && !string.IsNullOrEmpty(ov)) return ov;
        }
        catch { }
        return d.HardwareDevice.Type.ToString();
    }

    /// <summary>Session options that run on one OpenVINO device, or null if that device isn't available.</summary>
    /// <param name="device">"NPU", "CPU", "GPU" (or "GPU.1"…).</param>
    /// <param name="options">OpenVINO EP options, e.g. load_config / reshape_input.</param>
    public static SessionOptions? CreateSessionOptions(string device, IReadOnlyDictionary<string, string>? options = null)
    {
        if (!EnsureRegistered()) return null;
        var ep = _devices.FirstOrDefault(d => string.Equals(OvDevice(d), device, StringComparison.OrdinalIgnoreCase));
        if (ep == null) return null;
        var so = new SessionOptions();
        try
        {
            so.AppendExecutionProvider(OrtEnv.Instance(), new[] { ep }, options ?? new Dictionary<string, string>());
            return so;
        }
        catch
        {
            so.Dispose();
            throw;
        }
    }

    /// <summary>Loads the plugin and lists its devices. False when the plugin DLLs are missing or refuse to load.</summary>
    private static bool EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered) return true;
            var path = PluginPath;
            if (path == null) return false;
            try
            {
                var env = OrtEnv.Instance();
                env.RegisterExecutionProviderLibrary(Registration, path);
                _devices = env.GetEpDevices()
                    .Where(d => d.EpName.Contains("OpenVINO", StringComparison.OrdinalIgnoreCase) && !d.EpName.Contains("AUTO", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                _registered = true;
                return true;
            }
            catch (Exception ex)
            {
                WhisperDesk.App.Log("NPU plugin", ex);
                return false;
            }
        }
    }

    /// <summary>File version of the OpenVINO plugin ("1.8.0"), "none" when it isn't installed.</summary>
    public static string PluginVersion
    {
        get
        {
            try
            {
                if (PluginPath is not { } p) return "none";
                var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(p);
                return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
            }
            catch { return "unknown"; }
        }
    }

    /// <summary>
    /// Developer switch: WHISPERDESK_NPU_DEVICE=CPU (or GPU) runs the NPU engine on that OpenVINO device instead, so the
    /// whole pipeline can be tested on a PC without an NPU (OpenVINO only lists Intel hardware); =ORT runs it on ONNX
    /// Runtime's own CPU provider, for non-Intel PCs. Unset on users' PCs.
    /// </summary>
    public static string? DeviceOverride =>
        Environment.GetEnvironmentVariable("WHISPERDESK_NPU_DEVICE") is { Length: > 0 } d ? d.Trim().ToUpperInvariant() : null;

    /// <summary>The OpenVINO plugin next to the exe (single-file publish) or under runtimes\win-x64\native (dotnet build).</summary>
    private static string? PluginPath
    {
        get
        {
            const string Dll = "onnxruntime_providers_openvino_plugin.dll";
            var root = AppContext.BaseDirectory;
            foreach (var candidate in new[] { Path.Combine(root, Dll), Path.Combine(root, "runtimes", "win-x64", "native", Dll) })
                if (File.Exists(candidate)) return candidate;
            return null;
        }
    }

    /// <summary>
    /// True when Windows lists a present device in the "Neural processors" class (ComputeAccelerator), which is where
    /// Intel AI Boost and other NPUs appear. Uses SetupAPI only: nothing is loaded when there is no NPU.
    /// </summary>
    internal static bool NpuDevicePresent()
    {
        var cls = new Guid("f01a9d53-3ff6-48d2-9f97-c8a7004be10c");
        var set = SetupDiGetClassDevs(ref cls, null, IntPtr.Zero, 0x2 /* DIGCF_PRESENT */);
        if (set == new IntPtr(-1)) return true;   // can't tell: let the full probe decide
        try
        {
            var info = new SpDevInfoData { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<SpDevInfoData>() };
            return SetupDiEnumDeviceInfo(set, 0, ref info);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SpDevInfoData { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [System.Runtime.InteropServices.DllImport("setupapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);
    [System.Runtime.InteropServices.DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevInfoData data);
    [System.Runtime.InteropServices.DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    private static NpuProbe Probe()
    {
        try
        {
            // Most PCs have no NPU: ask Windows first and leave ONNX Runtime and OpenVINO unloaded when there is none.
            if (DeviceOverride == null && !NpuDevicePresent())
                return NpuProbe.None("No NPU in this PC");
            if (!WhisperDesk.Services.OnnxRuntimeGuard.Available)
                return NpuProbe.None("NPU not used: " + WhisperDesk.Services.OnnxRuntimeGuard.Problem);
            WhisperDesk.Services.OnnxRuntimeGuard.Require();
            if (!EnsureRegistered())
                return NpuProbe.None(PluginPath == null ? "NPU support not installed (OpenVINO plugin missing)" : "NPU: OpenVINO plugin failed to load (see error log)");
            var devices = Devices;
            if (!devices.Any(d => d.StartsWith("NPU", StringComparison.OrdinalIgnoreCase)))
                return new NpuProbe(false, devices.Count == 0 ? "No Intel NPU found" : $"No NPU found (OpenVINO sees: {string.Join(", ", devices)})", null, devices);

            // A device being listed isn't enough (a missing or old driver still lists it): run one tiny model on it.
            using var so = CreateSessionOptions("NPU") ?? throw new InvalidOperationException("NPU device vanished");
            using var session = new InferenceSession(TinyOnnxModel.Bytes, so);
            var input = new float[] { 1, 2, 3, 4 };
            using var x = OrtValue.CreateTensorValueFromMemory(input, new long[] { 1, 4 });
            using var results = session.Run(new RunOptions(), new[] { "x" }, new[] { x }, new[] { "y" });
            var y = results[0].GetTensorDataAsSpan<float>();
            for (int i = 0; i < 4; i++)
                if (Math.Abs(y[i] - input[i] * 2) > 1e-3f) throw new InvalidOperationException("NPU test inference returned wrong values");

            var npu = _devices.First(d => OvDevice(d).StartsWith("NPU", StringComparison.OrdinalIgnoreCase));
            var vendor = npu.HardwareDevice.Vendor;
            var name = string.IsNullOrWhiteSpace(vendor) ? "NPU" : $"{vendor} NPU";
            return new NpuProbe(true, $"{name} found, used for transcription", $"OpenVINO devices: {string.Join(", ", devices)}", devices);
        }
        catch (Exception ex)
        {
            WhisperDesk.App.Log("NPU probe", ex);
            return NpuProbe.None("NPU found but failed its test, not used (see error log)", ex.Message);
        }
    }
}

/// <summary>
/// A hand-written ONNX model (y = x + x, x: float[1,4]) used to check that a device really runs inference. Built as
/// protobuf bytes here so nothing has to ship as a file.
/// </summary>
internal static class TinyOnnxModel
{
    public static byte[] Bytes { get; } = Build();

    private static byte[] Build()
    {
        // TypeProto { tensor_type(1) { elem_type(1)=1 (float); shape(2) { dim(1){dim_value(1)=1}, dim(1){dim_value(1)=4} } } }
        var shape = Msg(Field(1, Msg(Varint(1, 1))), Field(1, Msg(Varint(1, 4))));
        var tensorType = Msg(Varint(1, 1), Field(2, shape));
        var type = Field(2, Field(1, tensorType));   // ValueInfoProto.type(2) = TypeProto { tensor_type(1) }
        // ValueInfoProto { name(1), type(2) }; NodeProto { input(1)*, output(2), name(3), op_type(4) }
        var inputInfo = Msg(Str(1, "x"), type);
        var outputInfo = Msg(Str(1, "y"), type);
        var node = Msg(Str(1, "x"), Str(1, "x"), Str(2, "y"), Str(3, "add"), Str(4, "Add"));
        // GraphProto { node(1), name(2), input(11), output(12) }
        var graph = Msg(Field(1, node), Str(2, "whisperdesk_probe"), Field(11, inputInfo), Field(12, outputInfo));
        // OperatorSetIdProto { domain(1), version(2) }; ModelProto { ir_version(1), graph(7), opset_import(8) }
        var opset = Msg(Str(1, ""), Varint(2, 13));
        return Msg(Varint(1, 8), Field(7, graph), Field(8, opset));
    }

    private static byte[] Msg(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] Field(int number, byte[] message) => Tag(number, 2).Concat(VarintBytes((ulong)message.Length)).Concat(message).ToArray();
    private static byte[] Str(int number, string s) => Field(number, System.Text.Encoding.UTF8.GetBytes(s));
    private static byte[] Varint(int number, long value) => Tag(number, 0).Concat(VarintBytes((ulong)value)).ToArray();
    private static byte[] Tag(int number, int wireType) => VarintBytes((ulong)(number << 3 | wireType));
    private static byte[] VarintBytes(ulong v)
    {
        var list = new List<byte>();
        do { byte b = (byte)(v & 0x7F); v >>= 7; list.Add(v != 0 ? (byte)(b | 0x80) : b); } while (v != 0);
        return list.ToArray();
    }
}
