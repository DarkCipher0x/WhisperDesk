using System.IO;
using Microsoft.ML.OnnxRuntime;
using SherpaOnnx;

namespace WhisperDesk.Services.Npu;

/// <summary>
/// Speaker voiceprints (WeSpeaker ResNet34, the model sherpa-onnx uses) on the NPU. Voiceprints are made for speaker
/// recognition, the line-by-line speaker check after diarization and every utterance of a live session with speakers on,
/// so in all-day use this is the busiest model after Whisper.
/// <para>
/// sherpa-onnx itself can't reach the NPU (it only supports ONNX Runtime's built-in OpenVINO provider, which the
/// ONNX Runtime we ship doesn't have), so this runs the same model through the shared OpenVINO setup with features
/// computed exactly like sherpa's (<see cref="KaldiFbank"/>). It is switched on only after its voiceprints for test
/// signals match sherpa's on the CPU: voiceprints saved in the voice library must keep matching. Anything it can't do
/// (clips over 30 s, any error) goes to sherpa on the CPU, which gives the same result.
/// </para>
/// </summary>
public sealed class NpuSpeakerEmbedder : IDisposable
{
    private const int MinFrames = 50, MaxFrames = 3000;   // 0.5–30 s; Embed() is never called with less than 0.5 s
    private const float MinAgreement = 0.985f;             // cosine to sherpa's CPU voiceprint (fp16 on the NPU)

    private static readonly object Gate = new();
    private static Task<NpuSpeakerEmbedder?>? _prepare;
    private static volatile NpuSpeakerEmbedder? _active;
    private static string _status = "Not checked yet (runs when speaker models are first used)";

    private readonly InferenceSession _session;
    private readonly KaldiFbank _fbank = new();
    private bool _broken;

    public string Device { get; }

    private NpuSpeakerEmbedder(InferenceSession session, string device)
    {
        _session = session;
        Device = device;
    }

    /// <summary>The validated NPU embedder when it should be used now (respects Settings → Speakers → Hardware).</summary>
    public static NpuSpeakerEmbedder? Active => AppSettings.Current.SpeakerHardware == "CPU" ? null : _active is { _broken: false } a ? a : null;

    /// <summary>Where voiceprints are computed, for Settings and About ("NPU", "CPU (…why…)").</summary>
    public static string Status => AppSettings.Current.SpeakerHardware == "CPU" ? "CPU (chosen in Settings)" : _status;

    /// <summary>Starts the one-time NPU preparation and check in the background (no-op if already started or not wanted).</summary>
    public static void PrepareInBackground()
    {
        if (AppSettings.Current.SpeakerHardware == "CPU") return;
        lock (Gate) _prepare ??= Task.Run(PrepareAsync);
    }

    private static async Task<NpuSpeakerEmbedder?> PrepareAsync()
    {
        var device = NpuDetector.DeviceOverride ?? "NPU";
        try
        {
            if (NpuDetector.DeviceOverride == null && !(await NpuDetector.ProbeAsync()).Available)
            {
                _status = "CPU (no NPU on this PC)";
                return null;
            }
            var model = SpeakerModels.EmbeddingModel;
            if (!File.Exists(model)) { _status = "CPU (speaker models not downloaded yet)"; lock (Gate) _prepare = null; return null; }

            _status = "Preparing the speaker model for the NPU…";
            var session = OpenVinoSessions.Create(model, device,
                new Dictionary<string, string> { ["reshape_input"] = $"feats[1,{MinFrames}..{MaxFrames},80]" }, "speaker model");
            var npu = new NpuSpeakerEmbedder(session, device);
            try
            {
                Validate(npu, model);
            }
            catch
            {
                npu.Dispose();
                throw;
            }
            _status = device == "NPU" ? "NPU (checked against the CPU result)" : $"{OpenVinoSessions.DeviceLabel(device)} (developer test)";
            _active = npu;
            return npu;
        }
        catch (Exception ex)
        {
            App.Log("NPU speaker model (voiceprints stay on the CPU)", ex);
            _status = $"CPU (the NPU can't run the speaker model on this PC: {ex.Message})";
            return null;
        }
    }

    /// <summary>Voiceprints of test signals (three lengths) must match sherpa-onnx's own CPU result.</summary>
    private static void Validate(NpuSpeakerEmbedder npu, string model)
    {
        using var reference = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig { Model = model, NumThreads = 1 });
        foreach (var seconds in new[] { 1.2, 4.0, 12.5 })
        {
            var audio = TestSignal(seconds);
            using var stream = reference.CreateStream();
            stream.AcceptWaveform(16000, audio);
            stream.InputFinished();
            var expected = reference.Compute(stream);
            var actual = npu.Compute(audio);
            var agreement = VoiceLibrary.Cosine(expected, actual);
            if (agreement < MinAgreement)
                throw new InvalidDataException($"its voiceprints differ from the CPU's (similarity {agreement:0.000} for a {seconds:0.#} s test)");
        }
    }

    /// <summary>A deterministic voice-like test signal: a gliding harmonic tone with syllable-rate loudness changes and a little noise.</summary>
    private static float[] TestSignal(double seconds)
    {
        int n = (int)(seconds * 16000);
        var x = new float[n];
        var rng = new Random(7);
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / 16000.0, f0 = 150 + 40 * Math.Sin(2 * Math.PI * 0.7 * t);
            phase += 2 * Math.PI * f0 / 16000;
            double v = 0;
            for (int h = 1; h <= 12; h++) v += Math.Sin(h * phase) / h * (1 + 0.6 * Math.Sin(2 * Math.PI * (0.3 + h * 0.11) * t));
            double env = 0.55 + 0.45 * Math.Sin(2 * Math.PI * 4 * t);
            x[i] = (float)(0.08 * v * env + 0.003 * (rng.NextDouble() - 0.5));
        }
        return x;
    }

    /// <summary>The voiceprint, or false when this clip should go to the CPU (too long, or the NPU failed).</summary>
    public bool TryEmbed(float[] audio, out float[] embedding)
    {
        embedding = Array.Empty<float>();
        if (_broken) return false;
        int frames = (audio.Length + 80) / 160;
        if (frames < MinFrames || frames > MaxFrames) return false;
        try
        {
            embedding = Compute(audio);
            return true;
        }
        catch (Exception ex)
        {
            _broken = true;   // don't retry every utterance; sherpa on the CPU takes over for this session
            _status = $"CPU (the NPU failed: {ex.Message}; restart WhisperDesk to try again)";
            App.Log("NPU speaker model failed; voiceprints continue on the CPU", ex);
            return false;
        }
    }

    private float[] Compute(float[] audio)
    {
        var (features, frames) = _fbank.Compute(audio);
        using var x = OrtValue.CreateTensorValueFromMemory(features, new long[] { 1, frames, _fbank.Bins });
        using var r = _session.Run(new RunOptions(), new[] { "feats" }, new[] { x }, new[] { "embs" });
        return r[0].GetTensorDataAsSpan<float>().ToArray();
    }

    public void Dispose() => _session.Dispose();
}
