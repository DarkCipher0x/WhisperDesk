using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace WhisperDesk.Services;

/// <summary>
/// <c>WhisperDesk.exe --selftest &lt;report.txt&gt; [--cpu]</c>: checks, on this PC and inside this package, everything a
/// live session needs, with no window and without touching the user's data. Used for the Microsoft Store pre-flight in a
/// clean Windows Sandbox (Packaging\store-check\) and by support.
///
/// It runs in a throw-away data folder (deleted afterwards, models included): the bundled Visual C++ runtime is the
/// one loaded, ONNX Runtime loads, the speaker and voice-detection models download (pinned SHA-256) and load, the Tiny
/// Whisper model downloads and loads (GPU when there is one, else the CPU fallback), a sentence spoken by Windows'
/// own text-to-speech is transcribed directly and through a real <see cref="LiveSession"/> (voice detection → Whisper
/// → speaker identification, fed the way the microphone feeds it), and the microphone permission is reported (never
/// asked). Exit code 0 when every required check passed.
/// </summary>
internal static class SelfTest
{
    public const string Argument = "--selftest";
    private const string Sentence = "Hello, this is a test of live transcription. The quick brown fox jumps over the lazy dog.";
    private static readonly string[] Keywords = ["test", "transcription", "quick", "brown", "fox", "jumps", "lazy", "dog"];

    private sealed record Check(string Name, bool Ok, bool Required, string Detail, long Ms);
    private static readonly List<Check> Checks = [];
    private static readonly StringBuilder Log = new();
    private static string? _report;

    /// <summary>Before anything reads settings: every data path goes to a fresh throw-away folder.</summary>
    public static string PrepareIsolation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WhisperDesk-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        AppPaths.Override = dir;
        return dir;
    }

    public static int Run(string[] args, string dataDir)
    {
        _report = args.Length > 1 && !args[1].StartsWith("--") ? Path.GetFullPath(args[1]) : null;
        var cpuOnly = args.Contains("--cpu");
        if (_report != null) try { Directory.CreateDirectory(Path.GetDirectoryName(_report)!); File.WriteAllText(_report, ""); } catch { _report = null; }
        // A watchdog: a check that hangs ends the run with its name in the report instead of never finishing.
        using var watchdog = new Timer(_ =>
        {
            Add("self-test", false, true, $"timed out after {Limit.TotalMinutes:0} minutes during: {_step}", 0);
            Write();
            try { Directory.Delete(dataDir, true); } catch { }
            Environment.Exit(4);
        }, null, Limit, Timeout.InfiniteTimeSpan);
        int code;
        // On the thread pool (MTA), not the STA main thread, which has no message loop while it waits.
        try { code = Task.Run(() => RunAsync(dataDir, cpuOnly)).GetAwaiter().GetResult(); }
        catch (Exception ex) { Add("self-test", false, true, "crashed: " + ex, 0); code = 3; }
        watchdog.Change(Timeout.Infinite, Timeout.Infinite);
        Write();
        try { Directory.Delete(dataDir, true); } catch { }
        return code;
    }

    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(15);
    private static volatile string _step = "starting";
    private static void Step(string name) { _step = name; Line("… " + name); }

    private static async Task<int> RunAsync(string dataDir, bool cpuOnly)
    {
        var s = AppSettings.Current;
        s.ModelsPath = Path.Combine(dataDir, "models");   // never the user's (or the installer's) models folder
        s.ModelDownloadPolicy = "Allow";                    // in-memory only, inside the throw-away folder: no prompts
        Line($"WhisperDesk {AboutWindow.Version} self-test, {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Line($"Windows {Environment.OSVersion.Version}, {RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} logical CPUs, .NET {Environment.Version}");
        Line($"Packaged (Microsoft Store/MSIX): {PackageInfo.IsPackaged}");
        Line($"Program folder: {AppContext.BaseDirectory}");
        Line("");

        Step("Visual C++ runtime");
        // 1. The Visual C++ runtime: all present, and the copies loaded are WhisperDesk's own
        var sw = Stopwatch.StartNew();
        var loaded = VcRuntime.Files.Select(f => (File: f, Path: ModulePath(f))).ToList();
        var foreign = loaded.Where(l => l.Path == null || !l.Path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)).ToList();
        Add("Visual C++ runtime (bundled copy loaded)", VcRuntime.Missing.Count == 0 && foreign.Count == 0, true,
            VcRuntime.Missing.Count > 0 ? "missing: " + string.Join(", ", VcRuntime.Missing)
            : foreign.Count > 0 ? "not WhisperDesk's copy: " + string.Join(", ", foreign.Select(f => $"{f.File} = {f.Path ?? "not loaded"}"))
            : string.Join(", ", loaded.Select(l => $"{l.File} {FileVersionInfo.GetVersionInfo(l.Path!).FileVersion}")), sw.ElapsedMilliseconds);

        Step("ONNX Runtime");
        // 2. ONNX Runtime (speaker recognition, voice detection)
        sw.Restart();
        Add("ONNX Runtime loads", OnnxRuntimeGuard.Available, true,
            OnnxRuntimeGuard.Available ? $"onnxruntime.dll {FileVersionInfo.GetVersionInfo(ModulePath("onnxruntime.dll") ?? "").FileVersion} from {ModulePath("onnxruntime.dll")}" : OnnxRuntimeGuard.Problem ?? "?",
            sw.ElapsedMilliseconds);

        Step("text-to-speech");
        // 3. The speech to test with: Windows' own voice (no audio file shipped, nothing recorded)
        sw.Restart();
        float[]? speech = null;
        try
        {
            speech = await SpeakAsync(Sentence);
            Add("Test speech (Windows text-to-speech)", true, false, $"{speech.Length / (double)AudioDecoder.SampleRate:0.0} s at 16 kHz", sw.ElapsedMilliseconds);
        }
        catch (Exception ex) { Add("Test speech (Windows text-to-speech)", false, false, "no voice available, transcription checks skipped: " + ex.Message, sw.ElapsedMilliseconds); }

        Step("speaker and voice-detection model downloads");
        // 4. Models, downloaded exactly as the app does it (pinned revisions, SHA-256 checked)
        sw.Restart();
        bool speakerModels = false;
        try
        {
            await SpeakerModels.EnsureAsync(new Progress<string>(_ => { }), CancellationToken.None);
            speakerModels = SpeakerModels.AreDownloaded;
            Add("Speaker + voice-detection models (download, SHA-256)", speakerModels, true, SpeakerModels.Dir, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) { Add("Speaker + voice-detection models (download, SHA-256)", false, true, ex.Message, sw.ElapsedMilliseconds); }

        sw.Restart();
        Step("Whisper Tiny download");
        var tiny = ModelManager.Models.First(m => m.Type == Whisper.net.Ggml.GgmlType.Tiny);
        string? modelPath = null;
        try
        {
            modelPath = await ModelManager.EnsureAsync(tiny, new Progress<long>(_ => { }), CancellationToken.None);
            await ModelManager.VerifyAsync(tiny, null, CancellationToken.None);
            Add("Whisper Tiny model (download, SHA-256)", true, true, $"{new FileInfo(modelPath).Length / 1048576} MB", sw.ElapsedMilliseconds);
        }
        catch (Exception ex) { Add("Whisper Tiny model (download, SHA-256)", false, true, ex.Message, sw.ElapsedMilliseconds); }

        // 5. whisper.cpp: the GPU (Vulkan) when there is one, else the CPU fallback, then a real transcription
        using var engine = new TranscriptionEngine();
        var options = new TranscribeOptions("en", false, Math.Clamp(Environment.ProcessorCount / 2, 1, 8), NoContext: true);
        if (modelPath != null)
        {
            sw.Restart();
            try
            {
                Step("Whisper engine load");
                await Task.Run(() => engine.Load(modelPath, new EngineOptions(!cpuOnly, Hardware: cpuOnly ? "CPU" : "GPU")));
                Add("Whisper engine loads", engine.IsLoaded, true, $"{engine.Badge} (native runtime: {TranscriptionEngine.LoadedRuntime ?? "?"})", sw.ElapsedMilliseconds);
            }
            catch (Exception ex) { Add("Whisper engine loads", false, true, ex.ToString(), sw.ElapsedMilliseconds); }

            if (engine.IsLoaded && speech != null)
            {
                sw.Restart();
                try
                {
                    Step("whole-clip transcription");
                    var segments = await engine.TranscribeAsync(speech, options, null, null, CancellationToken.None);
                    var text = string.Join(" ", segments.Select(x => x.Text.Trim()));
                    var hits = Hits(text);
                    Add("Transcription (whole clip)", hits >= 5, true, $"{hits}/{Keywords.Length} key words: \"{text}\"", sw.ElapsedMilliseconds);
                }
                catch (Exception ex) { Add("Transcription (whole clip)", false, true, ex.ToString(), sw.ElapsedMilliseconds); }
            }
        }

        // 6. A real live session: voice detection cuts the audio, Whisper transcribes, speakers are identified
        if (engine.IsLoaded && speakerModels && OnnxRuntimeGuard.Available && speech != null)
        {
            sw.Restart();
            try
            {
                Step("live session");
                var (lines, speakers, status) = await LiveAsync(engine, options, speech);
                var text = string.Join(" ", lines);
                var hits = Hits(text);
                Add("Live session (voice detection → Whisper → speakers)", lines.Count > 0 && hits >= 5, true,
                    $"{lines.Count} line(s), {speakers} speaker(s), {hits}/{Keywords.Length} key words: \"{text}\"" + (status.Count > 0 ? " · status: " + string.Join(" | ", status) : ""),
                    sw.ElapsedMilliseconds);
            }
            catch (Exception ex) { Add("Live session (voice detection → Whisper → speakers)", false, true, ex.ToString(), sw.ElapsedMilliseconds); }
        }
        else Add("Live session (voice detection → Whisper → speakers)", false, true, "skipped: an earlier check failed", 0);

        Step("microphone and audio devices");
        // 7. Microphone: permission and devices are reported only (a test machine may have no microphone)
        sw.Restart();
        Add("Microphone permission (report only)", true, false, MicrophoneStatus(), sw.ElapsedMilliseconds);
        try
        {
            var devices = LiveAudioSource.ListDevices();
            Add("Audio inputs (report only)", true, false, devices.Count == 0 ? "none on this PC" : string.Join("; ", devices.Select(d => d.Name + (d.IsLoopback ? " (system audio)" : ""))), 0);
        }
        catch (Exception ex) { Add("Audio inputs (report only)", true, false, "couldn't list: " + ex.Message, 0); }

        var failed = Checks.Count(c => c.Required && !c.Ok);
        Line("");
        Line(failed == 0 ? "RESULT: PASS (every required check passed)" : $"RESULT: FAIL ({failed} required check(s) failed)");
        return failed == 0 ? 0 : 1;
    }

    private static int Hits(string text)
    {
        var t = text.ToLowerInvariant();
        return Keywords.Count(k => t.Contains(k));
    }

    private static async Task<(List<string> Lines, int Speakers, List<string> Status)> LiveAsync(TranscriptionEngine engine, TranscribeOptions options, float[] speech)
    {
        // The session reports to a UI thread: give it a real dispatcher on a thread of its own (no window).
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() => { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var ui = await ready.Task;
        var status = new List<string>();
        try
        {
            var result = new TranscriptResult { SourceFile = "Self-test live session" };
            using var analyzer = new SpeakerAnalyzer();
            using var session = new LiveSession(engine, analyzer, VoiceLibrary.Shared, options, identify: true, ui, result, recordingPath: null);
            session.Status += m => { lock (status) status.Add(m); };
            // Fed like the microphone: 100 ms blocks, with a second of silence before and after the speech.
            var silence = new float[AudioDecoder.SampleRate];
            var stream = silence.Concat(speech).Concat(silence).Concat(silence).ToArray();
            const int block = AudioDecoder.SampleRate / 10;
            for (int i = 0; i < stream.Length; i += block)
            {
                session.Feed(stream[i..Math.Min(stream.Length, i + block)]);
                await Task.Delay(5);
            }
            await session.StopAsync();
            return await ui.InvokeAsync(() => (result.Lines.Select(l => l.Text).ToList(), result.Speakers.Count, status.ToList()));
        }
        finally { ui.InvokeShutdown(); }
    }

    /// <summary>Windows' text-to-speech, as 16 kHz mono float samples (WAV parsed here: no Media Foundation needed).</summary>
    private static async Task<float[]> SpeakAsync(string text)
    {
        using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
        var voice = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("no English text-to-speech voice is installed");
        synth.Voice = voice;
        using var stream = await synth.SynthesizeTextToStreamAsync(text);
        var bytes = new byte[stream.Size];
        using (var input = stream.GetInputStreamAt(0).AsStreamForRead()) await input.ReadExactlyAsync(bytes);
        using var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(bytes));
        NAudio.Wave.ISampleProvider source = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader);
        if (source.WaveFormat.Channels == 2) source = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(source);
        if (source.WaveFormat.SampleRate != AudioDecoder.SampleRate) source = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(source, AudioDecoder.SampleRate);
        var output = new List<float>();
        var buf = new float[16000];
        int n;
        while ((n = source.Read(buf, 0, buf.Length)) > 0) output.AddRange(buf.AsSpan(0, n));
        return [.. output];
    }

    private static string MicrophoneStatus()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return "Windows is older than 1903: no per-app microphone permission";
        if (!PackageInfo.IsPackaged) return "not packaged: Windows' per-app prompt doesn't apply (Settings → Privacy & security → Microphone → desktop apps)";
        try { return "Windows says: " + Windows.Security.Authorization.AppCapabilityAccess.AppCapability.Create("microphone").CheckAccess(); }
        catch (Exception ex) { return "couldn't ask Windows: " + ex.Message; }
    }

    private static string? ModulePath(string name)
    {
        var h = GetModuleHandleW(name);
        if (h == IntPtr.Zero) return null;
        var sb = new StringBuilder(1024);
        return GetModuleFileNameW(h, sb, sb.Capacity) > 0 ? sb.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetModuleFileNameW(IntPtr module, StringBuilder path, int size);

    private static void Add(string name, bool ok, bool required, string detail, long ms)
    {
        Checks.Add(new Check(name, ok, required, detail, ms));
        Line($"[{(ok ? "PASS" : required ? "FAIL" : "WARN")}] {name} ({ms} ms): {detail}");
    }

    private static void Line(string text)
    {
        lock (Log)
        {
            Log.AppendLine(text);
            try { Console.Out.WriteLine(text); } catch { }
            // As it goes, so a run that is stopped or hangs still shows how far it got.
            if (_report != null) try { File.AppendAllText(_report, text + Environment.NewLine); } catch { }
        }
    }

    private static void Write()
    {
        if (_report == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_report)!);
            lock (Log) File.WriteAllText(_report, Log.ToString());
            File.WriteAllText(Path.ChangeExtension(_report, ".json"), JsonSerializer.Serialize(new
            {
                version = AboutWindow.Version, packaged = PackageInfo.IsPackaged, passed = Checks.All(c => c.Ok || !c.Required),
                checks = Checks.Select(c => new { c.Name, c.Ok, c.Required, c.Detail, c.Ms }),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
