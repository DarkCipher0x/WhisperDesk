using Whisper.net;
using Whisper.net.LibraryLoader;
using WhisperDesk.Services.Npu;

namespace WhisperDesk.Services;

public sealed record TranscribeOptions(string Language, bool Translate, int Threads, string Prompt = "", bool NoContext = false);

/// <param name="UseGpu">whisper.cpp: try the Vulkan GPU runtime (also used when the NPU engine falls back).</param>
/// <param name="Hardware">"Auto" | "NPU" | "GPU" | "CPU" (Settings → main window "Hardware").</param>
/// <param name="OnnxDir">Folder of the model's ONNX version when the NPU engine should be used, else null.</param>
public sealed record EngineOptions(bool UseGpu, int GpuDevice = 0, bool FlashAttention = false, string Hardware = "Auto", string? OnnxDir = null);

/// <summary>
/// Transcription engine. Runs Whisper on the NPU (ONNX Runtime + OpenVINO) when the PC has one and the model has an
/// ONNX version, otherwise whisper.cpp (Whisper.net), which prefers the Vulkan GPU runtime and falls back to CPU.
/// Any NPU error switches to whisper.cpp for good (this session), with the reason in the error log.
/// </summary>
public sealed class TranscriptionEngine : IDisposable
{
    private WhisperFactory? _factory;
    private string? _modelPath;
    private EngineOptions? _options;
    private OnnxWhisperEngine? _onnx;
    private string? _onnxKey;

    static TranscriptionEngine()
    {
        RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu };
    }

    /// <summary>whisper.cpp's native runtime ("Vulkan", "Cpu"), null until it has loaded.</summary>
    public static string? LoadedRuntime => RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>Where the last transcription ran, for About ("NPU (OpenVINO)", "GPU (Vulkan)", "CPU"); null before the first.</summary>
    public static string? LastBackend { get; private set; }

    /// <summary>Why the NPU wasn't used although it was wanted, else null.</summary>
    public string? NpuFallbackReason { get; private set; }

    /// <summary>The NPU problem of this app session, if any (shown in Settings and About; restart to try the NPU again).</summary>
    public static string? SessionNpuProblem { get; private set; }

    public bool IsLoaded => _factory != null || _onnx != null;
    public bool UsesNpu => _onnx != null;

    /// <summary>True while a Load is running (e.g. the NPU model compiling in the background).</summary>
    public bool IsPreparing => Volatile.Read(ref _loading) > 0;

    private readonly object _loadLock = new();
    private int _loading;
    private Task<string>? _fallbackModel;

    /// <summary>
    /// True when this hardware choice should run on the NPU engine: Auto or NPU, and the start-up probe found a working
    /// NPU (or the developer switch WHISPERDESK_NPU_DEVICE is set).
    /// </summary>
    public static async Task<bool> NpuWantedAsync(string hardware)
    {
        if (hardware is not ("Auto" or "NPU")) return false;
        if (NpuDetector.DeviceOverride != null) return true;
        return (await NpuDetector.ProbeAsync()).Available;
    }

    /// <summary>The encoder device the NPU engine uses ("NPU", or the developer override).</summary>
    public static string NpuEncoderDevice => NpuDetector.DeviceOverride ?? "NPU";

    /// <summary>The badge under the title bar: "engine: npu · openvino", "engine: gpu · vulkan", …</summary>
    public string Badge
    {
        get
        {
            if (_onnx != null) return _onnx.EncoderDevice == "NPU" ? "engine: npu · openvino" : $"engine: onnx · {_onnx.EncoderDevice.ToLowerInvariant()} (test)";
            var o = _options;
            var text = o is { UseGpu: false } ? "engine: cpu"
                     : _factory == null ? "engine: not loaded"
                     : LoadedRuntime == "Vulkan" ? "engine: gpu · vulkan" : "engine: cpu (no vulkan gpu)";
            // Never fall back silently: say so whenever the NPU was wanted but isn't the one running.
            if (o?.Hardware is "Auto" or "NPU" && NpuFallbackReason != null) return text + " · npu failed";
            if (o?.Hardware == "NPU" && o.OnnxDir == null) return text + " · npu not used";
            return text;
        }
    }

    /// <summary>
    /// Loads the engine for <paramref name="options"/>. Thread-safe and idempotent: a second call with the same model and
    /// options returns at once (also when it had to wait for a background preparation of the same thing).
    /// </summary>
    /// <param name="modelPath">whisper.cpp model, or null when it is still downloading (then pass <paramref name="fallbackModel"/>).</param>
    /// <param name="stage">Preparation steps for the status bar.</param>
    /// <param name="fallbackModel">The whisper.cpp model the NPU engine falls back to, if it is still being downloaded.</param>
    public void Load(string? modelPath, EngineOptions options, IProgress<string>? stage = null, Task<string>? fallbackModel = null)
    {
        Interlocked.Increment(ref _loading);
        try
        {
            lock (_loadLock) LoadLocked(modelPath, options, stage, fallbackModel);
        }
        finally { Interlocked.Decrement(ref _loading); }
    }

    private void LoadLocked(string? modelPath, EngineOptions options, IProgress<string>? stage, Task<string>? fallbackModel)
    {
        if (options.OnnxDir is { } dir && NpuFallbackReason == null)
        {
            var encoder = NpuEncoderDevice;
            var key = $"{dir}|{encoder}|{AppSettings.Current.NpuDecoderDevice}";
            _fallbackModel = fallbackModel ?? (modelPath != null ? Task.FromResult(modelPath) : null);
            _options = options;
            if (_onnx != null && _onnxKey == key) return;
            DisposeOnnx();
            try
            {
                _onnx = new OnnxWhisperEngine(dir, encoder, AppSettings.Current.NpuDecoderDevice, stage);
                _onnxKey = key;
                _factory?.Dispose();   // whisper.cpp loads again only if the NPU engine fails
                _factory = null;
                _modelPath = null;
                return;
            }
            catch (Exception ex)
            {
                FallBack("loading the model", ex, dir);
                stage?.Report("The NPU could not load this model; switching to " + (options.UseGpu ? "the GPU/CPU" : "the CPU") + "…");
            }
        }
        DisposeOnnx();
        modelPath ??= (fallbackModel ?? _fallbackModel)?.GetAwaiter().GetResult()
                      ?? throw new InvalidOperationException("No whisper.cpp model to load.");
        stage?.Report("Loading the model for " + (options.UseGpu ? "the GPU" : "the CPU") + "…");
        LoadWhisperCpp(modelPath, options);
    }

    private void LoadWhisperCpp(string modelPath, EngineOptions options)
    {
        if (_factory != null && _modelPath == modelPath && _options == options) return;

        _factory?.Dispose();
        _factory = null;
        _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions
        {
            UseGpu = options.UseGpu,
            GpuDevice = options.GpuDevice,
            UseFlashAttention = options.FlashAttention,
        });
        _modelPath = modelPath;
        _options = options;
    }

    private void FallBack(string during, Exception ex, string? dir)
    {
        NpuFallbackReason = $"NPU engine failed while {during}: {ex.Message}";
        SessionNpuProblem = NpuFallbackReason;
        App.Log($"NPU engine failed while {during}; using whisper.cpp instead", ex);
        if (dir != null) OnnxWhisperEngine.ClearCompiled(dir);   // a bad compiled blob is rebuilt next time
        DisposeOnnx();
    }

    private void DisposeOnnx()
    {
        _onnx?.Dispose();
        _onnx = null;
        _onnxKey = null;
    }

    private WhisperProcessor Build(TranscribeOptions options, Action<int>? onProgress)
    {
        if (_factory == null) throw new InvalidOperationException("No model loaded.");

        var builder = _factory.CreateBuilder().WithThreads(options.Threads)
            // whisper.cpp's fallback rules: retry at a higher temperature when the output looks like a loop
            // (high compression/entropy) or is unlikely, and treat confident "no speech" windows as silence.
            .WithTemperature(0f).WithTemperatureInc(0.2f)
            .WithEntropyThreshold(2.4f).WithLogProbThreshold(-1.0f).WithNoSpeechThreshold(0.6f)
            .WithProbabilities();   // per-segment confidence (shown on lines, used by the hallucination filter)
        if (onProgress != null) builder = builder.WithProgressHandler(p => onProgress(p));

        builder = options.Language == "auto"
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(options.Language);

        if (options.Translate) builder = builder.WithTranslate();
        if (!string.IsNullOrWhiteSpace(options.Prompt)) builder = builder.WithPrompt(options.Prompt);
        // A short memory of previous text keeps names/spelling consistent without letting a loop feed itself.
        builder = options.NoContext ? builder.WithNoContext() : builder.WithMaxLastTextTokens(64);
        return builder.Build();
    }

    /// <summary>Transcribes the whole buffer, or only the given speech regions (timestamps stay relative to the buffer).</summary>
    public async Task<List<SegmentData>> TranscribeAsync(
        float[] samples, TranscribeOptions options,
        Action<int>? onProgress, Action<SegmentData>? onSegment, CancellationToken ct,
        IReadOnlyList<SpeechRegion>? regions = null)
    {
        if (_onnx is { } onnx)
        {
            var emitted = new List<SegmentData>();
            try
            {
                var result = await RunAsync((s, p, seg, c) => onnx.TranscribeAsync(s, options, p, seg, c),
                    samples, onProgress, s => { emitted.Add(s); onSegment?.Invoke(s); }, ct, regions);
                LastBackend = onnx.EncoderDevice == "NPU" ? "NPU (OpenVINO)" : $"ONNX on {onnx.EncoderDevice} (developer test)";
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Carry on with whisper.cpp from where the NPU stopped; lines already shown are kept.
                FallBack("transcribing", ex, _onnxKey?.Split('|')[0]);
                var ggml = _fallbackModel != null ? await _fallbackModel : _modelPath
                           ?? throw new InvalidOperationException("The NPU failed and no GPU/CPU model is available.", ex);
                lock (_loadLock) LoadWhisperCpp(ggml, _options!);
                var resume = emitted.Count > 0 ? emitted.Max(s => s.End) : TimeSpan.Zero;
                var rest = await RunAsync(WhisperCppAsync(options), samples, onProgress,
                    s => { if (s.End > resume) onSegment?.Invoke(s); }, ct, regions);
                return emitted.Concat(rest.Where(s => s.End > resume)).ToList();
            }
        }
        if (_factory == null && _options != null && (_modelPath ?? (_fallbackModel != null ? await _fallbackModel : null)) is { } path)
            lock (_loadLock) LoadWhisperCpp(path, _options);
        var segments = await RunAsync(WhisperCppAsync(options), samples, onProgress, onSegment, ct, regions);
        LastBackend = _options is { UseGpu: false } || LoadedRuntime != "Vulkan" ? "CPU" : "GPU (Vulkan)";
        return segments;
    }

    private delegate Task<List<SegmentData>> Whole(float[] samples, Action<int>? onProgress, Action<SegmentData>? onSegment, CancellationToken ct);

    private Whole WhisperCppAsync(TranscribeOptions options) => async (samples, onProgress, onSegment, ct) =>
    {
        var list = new List<SegmentData>();
        using var processor = Build(options, onProgress);
        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            list.Add(segment);
            onSegment?.Invoke(segment);
        }
        return list;
    };

    /// <summary>Runs <paramref name="whole"/> on the buffer, or region by region with timestamps shifted back.</summary>
    private static async Task<List<SegmentData>> RunAsync(Whole whole, float[] samples,
        Action<int>? onProgress, Action<SegmentData>? onSegment, CancellationToken ct, IReadOnlyList<SpeechRegion>? regions)
    {
        if (regions == null) return await whole(samples, onProgress, onSegment, ct);

        var segments = new List<SegmentData>();
        const int Rate = AudioDecoder.SampleRate;
        double total = Math.Max(1, regions.Sum(r => r.Length)), done = 0;
        foreach (var region in regions)
        {
            ct.ThrowIfCancellationRequested();
            int a = Math.Clamp((int)(region.Start * Rate), 0, samples.Length);
            int b = Math.Clamp((int)(region.End * Rate), a, samples.Length);
            if (b - a < Rate / 2) { done += region.Length; continue; }

            var slice = samples.AsSpan(a, b - a).ToArray();
            var offset = TimeSpan.FromSeconds(a / (double)Rate);
            double before = done;
            await whole(slice, onProgress == null ? null : p => onProgress((int)((before + region.Length * p / 100.0) / total * 100)),
                s =>
                {
                    var shifted = new SegmentData(s.Text, s.Start + offset, s.End + offset, s.MinProbability, s.MaxProbability,
                        s.Probability, s.NoSpeechProbability, s.Language, s.Tokens);
                    segments.Add(shifted);
                    onSegment?.Invoke(shifted);
                }, ct);
            done += region.Length;
            onProgress?.Invoke((int)(done / total * 100));
        }
        return segments;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        DisposeOnnx();
    }
}
