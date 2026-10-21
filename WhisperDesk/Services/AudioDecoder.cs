using System.Diagnostics;
using System.IO;

namespace WhisperDesk.Services;

/// <summary>Uses FFmpeg to turn any audio/video file into 16 kHz mono float samples for Whisper.</summary>
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

    public static async Task<float[]> DecodeAsync(string ffmpeg, string input, CancellationToken ct)
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
