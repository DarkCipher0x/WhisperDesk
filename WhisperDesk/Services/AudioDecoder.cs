using System.Diagnostics;
using System.IO;

namespace WhisperDesk.Services;

/// <summary>Turns any audio/video file into 16 kHz mono float samples for Whisper (FFmpeg, or Windows' decoders without it).</summary>
public static class AudioDecoder
{
    public const int SampleRate = 16000;

    public static readonly string[] SupportedExtensions =
    {
        ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".opus", ".aac", ".wma", ".aiff",
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".ts", ".mts"
    };

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Finds ffmpeg.exe: the path set in Settings, next to the app, the installer's choice, winget's install, and only then
    /// PATH. PATH comes last and only absolute folders outside Temp/Downloads count, so an ffmpeg.exe dropped into the
    /// current folder or a writable download folder is never picked up (binary planting).
    /// </summary>
    public static string? FindFfmpeg()
    {
        var custom = AppSettings.Current.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(custom) && Path.IsPathFullyQualified(custom.Trim()) && File.Exists(custom.Trim())) return custom.Trim();

        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;

        if (InstallerIntegration.FfmpegPath is { } chosen && Path.IsPathFullyQualified(chosen) && File.Exists(chosen)) return chosen;   // set in the installer
        if (InstallerIntegration.WingetFfmpeg() is { } winget) return winget;

        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var raw in paths)
        {
            try
            {
                var dir = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                if (dir.Length == 0 || !Path.IsPathFullyQualified(dir)) continue;
                var full = Path.GetFullPath(dir).TrimEnd('\\');
                if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || full.StartsWith(downloads, StringComparison.OrdinalIgnoreCase)) continue;
                var candidate = Path.Combine(full, "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>
    /// Decodes with FFmpeg when it's installed, otherwise with Windows' own Media Foundation codecs (MP3, WAV, M4A/AAC,
    /// FLAC, WMA, MP4/MOV/WMV/AVI audio…). The Store copy can't install FFmpeg for the user, so without this a fresh
    /// install couldn't read even its own recordings.
    /// </summary>
    public static Task<float[]> DecodeAsync(string? ffmpeg, string input, CancellationToken ct) =>
        ffmpeg != null ? DecodeWithFfmpegAsync(ffmpeg, input, ct) : Task.Run(() => DecodeWithWindows(input, ct), ct);

    public const string FfmpegHint = "Install FFmpeg for more formats (winget install Gyan.FFmpeg) or set its location in File → Settings.";

    /// <summary>Windows' Media Foundation decoder: 16 kHz mono float, read in the same 8 MB blocks as the FFmpeg path.</summary>
    public static float[] DecodeWithWindows(string input, CancellationToken ct)
    {
        NAudio.Wave.MediaFoundationReader reader;
        try { reader = new NAudio.Wave.MediaFoundationReader(input); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Windows can't read this file ({ex.Message.Trim()}). " + FfmpegHint, ex);
        }
        using (reader)
        {
            var mono = new MonoMix(NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader));
            NAudio.Wave.ISampleProvider source = mono.WaveFormat.SampleRate == SampleRate
                ? mono : new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(mono, SampleRate);

            const int Block = 2 << 20; // floats (8 MB)
            var blocks = new List<float[]>();
            long total = 0;
            int fill = Block;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (fill == Block) { blocks.Add(new float[Block]); fill = 0; }
                int n = source.Read(blocks[^1], fill, Block - fill);
                if (n == 0) break;
                fill += n;
                total += n;
            }
            if (total == 0) throw new InvalidOperationException("The file has no audio track.");

            var samples = new float[total];
            long copied = 0;
            foreach (var b in blocks)
            {
                int take = (int)Math.Min(Block, total - copied);
                if (take <= 0) break;
                Array.Copy(b, 0, samples, copied, take);
                copied += take;
            }
            return samples;
        }
    }

    /// <summary>Averages every channel into one (any channel count).</summary>
    private sealed class MonoMix(NAudio.Wave.ISampleProvider source) : NAudio.Wave.ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _buffer = Array.Empty<float>();
        public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            if (_channels == 1) return source.Read(buffer, offset, count);
            int needed = count * _channels;
            if (_buffer.Length < needed) _buffer = new float[needed];
            int read = source.Read(_buffer, 0, needed);
            int frames = read / _channels;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++) sum += _buffer[f * _channels + c];
                buffer[offset + f] = sum / _channels;
            }
            return frames;
        }
    }

    private static async Task<float[]> DecodeWithFfmpegAsync(string ffmpeg, string input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", input,
                                    "-vn", "-ac", "1", "-ar", SampleRate.ToString(), "-f", "f32le", "pipe:1" })
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        // Read in fixed 8 MB blocks and assemble once at the end: a growing MemoryStream would briefly need about
        // three times the audio's size (≈1.4 GB for a 2-hour call) and stress the garbage collector.
        const int Block = 8 << 20;
        var blocks = new List<byte[]>();
        long total = 0;
        int fill = Block;

        using (ct.Register(() => { try { proc.Kill(true); } catch { } }))
        {
            var stream = proc.StandardOutput.BaseStream;
            while (true)
            {
                if (fill == Block) { blocks.Add(new byte[Block]); fill = 0; }
                int n = await stream.ReadAsync(blocks[^1].AsMemory(fill, Block - fill), ct);
                if (n == 0) break;
                fill += n;
                total += n;
            }
            await proc.WaitForExitAsync(ct);
        }

        if (proc.ExitCode != 0)
            throw new InvalidOperationException("FFmpeg could not read this file: " + (await stderr).Trim());
        if (total == 0)
            throw new InvalidOperationException("The file has no audio track.");

        var samples = new float[total / sizeof(float)];
        long copied = 0, bytes = samples.LongLength * sizeof(float);
        foreach (var b in blocks)
        {
            int take = (int)Math.Min(Block, bytes - copied);
            if (take <= 0) break;
            Buffer.BlockCopy(b, 0, samples, (int)copied, take);
            copied += take;
        }
        return samples;
    }
}
