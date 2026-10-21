using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace WhisperDesk.Services;

/// <summary>
/// Burns (hard-codes) subtitles into a copy of a video with FFmpeg + libass.
/// Tries GPU encoders first (AMD AMF, NVIDIA NVENC, Intel QSV) and falls back to x264 on the CPU.
/// </summary>
public static class SubtitleBurner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".ts", ".mts",
    };

    private static string? _workingEncoder; // remembered after the first successful burn

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static string OutputPathFor(string source, string outputDir) =>
        Path.Combine(outputDir, Path.GetFileNameWithoutExtension(source) + ".subtitled.mp4");

    /// <param name="progress">0–100</param>
    /// <returns>The encoder that was used.</returns>
    public static async Task<string> BurnAsync(string ffmpeg, string video, string srtContent, string output,
        TimeSpan duration, IProgress<double> progress, CancellationToken ct)
    {
        var s = AppSettings.Current;
        var work = Security.WorkDir.NewFolder("burn");
        try
        {
            // The subtitles filter has awkward escaping for Windows paths; run FFmpeg inside the temp folder
            // and refer to the SRT by its plain file name instead.
            await File.WriteAllTextAsync(Path.Combine(work, "subs.srt"), srtContent, new UTF8Encoding(false), ct);

            var style = BuildStyle(s);
            var filter = $"subtitles=subs.srt:charenc=UTF-8:force_style='{style}'";

            var candidates = s.BurnUseGpu
                ? new[] { _workingEncoder, "h264_amf", "h264_nvenc", "h264_qsv", "libx264" }
                : new[] { "libx264" };
            Exception? last = null;
            foreach (var encoder in candidates.Where(e => e != null).Distinct())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await RunAsync(ffmpeg, work, video, filter, encoder!, s.BurnQuality, output, duration, progress, ct);
                    _workingEncoder = encoder;
                    return encoder!;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; try { File.Delete(output); } catch { } }
            }
            throw new InvalidOperationException("Could not burn subtitles: " + last?.Message);
        }
        finally
        {
            Security.WorkDir.Release(work);
        }
    }

    /// <summary>libass "force_style" from the user's burn-in settings.</summary>
    public static string BuildStyle(AppSettings s)
    {
        bool box = s.BurnStyle == "Box";
        // ASS colours are &HAABBGGRR, where alpha 00 = opaque and FF = transparent.
        var text = AssColor(s.BurnTextColor, 0);
        var outline = AssColor(s.BurnOutlineColor, box ? (int)Math.Round(255 * (1 - s.BurnBoxOpacity / 100.0)) : 0);
        var parts = new List<string>
        {
            $"FontName={s.BurnFont.Replace(",", " ").Replace("'", "")}",
            $"FontSize={s.BurnFontSize}",
            $"Bold={(s.BurnBold ? -1 : 0)}",
            $"Italic={(s.BurnItalic ? -1 : 0)}",
            $"PrimaryColour={text}",
            $"OutlineColour={outline}",
            $"BackColour={outline}",
            box ? "BorderStyle=3" : "BorderStyle=1",          // 3 = opaque box behind text
            $"Outline={(box ? "6" : s.BurnOutline.ToString(CultureInfo.InvariantCulture))}",
            "Shadow=0",
            $"Alignment={(s.BurnPosition == "Top" ? 8 : 2)}", // numpad layout: 2 = bottom centre, 8 = top centre
            $"MarginV={s.BurnMargin}",
        };
        return string.Join(",", parts);
    }

    private static string AssColor(string rrggbb, int alpha)
    {
        var hex = rrggbb.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) hex = "FFFFFF";
        return $"&H{alpha:X2}{hex[4..6]}{hex[2..4]}{hex[0..2]}".ToUpperInvariant();
    }

    private static async Task RunAsync(string ffmpeg, string workDir, string video, string filter, string encoder,
        int quality, string output, TimeSpan duration, IProgress<double> progress, CancellationToken ct)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", video, "-vf", filter };
        args.AddRange(encoder switch
        {
            "h264_amf" => new[] { "-c:v", "h264_amf", "-quality", "quality", "-rc", "cqp",
                                  "-qp_i", $"{quality}", "-qp_p", $"{quality}", "-qp_b", $"{quality}" },
            "h264_nvenc" => new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "constqp", "-qp", $"{quality}" },
            "h264_qsv" => new[] { "-c:v", "h264_qsv", "-global_quality", $"{quality}" },
            _ => new[] { "-c:v", "libx264", "-preset", "medium", "-crf", $"{quality}" },
        });
        args.AddRange(new[] { "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart",
                              "-progress", "pipe:1", "-nostats", output });

        var psi = new ProcessStartInfo(ffmpeg)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using (ct.Register(() => { try { proc.Kill(true); } catch { } }))
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
            {
                // "-progress" prints key=value lines; out_time_us is the encoded position.
                if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us) && duration.TotalSeconds > 0)
                    progress.Report(Math.Clamp(us / 1e6 / duration.TotalSeconds * 100, 0, 100));
            }
            await proc.WaitForExitAsync(ct);
        }
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{encoder}: {(await stderr).Trim()}");
    }
}
