using System.IO;
using System.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperDesk.Services;

/// <summary>Anything that can show a ▶ / ■ state (transcript lines, speaker cards).</summary>
public interface IPlayable
{
    bool IsPlaying { get; set; }
}

/// <summary>
/// Plays the audio behind one transcript line (or a speaker's sample clip). Pressing the same ▶ again stops it.
/// A line plays to its natural end: until the next line starts (Whisper's end times are often a little early).
/// Audio is decoded once per transcript and cached (only the most recent transcript is kept in memory).
/// </summary>
public sealed class LinePlayer
{
    private const int Rate = AudioDecoder.SampleRate;
    private string? _cachedPath;
    private float[]? _cached;
    private WasapiOut? _out;
    private IPlayable? _current;

    /// <summary>Raised on the UI thread whenever playback starts or stops.</summary>
    public event Action? StateChanged;

    public IPlayable? Current => _current;

    /// <summary>▶ on a line: plays it, or stops it if it is the line already playing.</summary>
    public async Task ToggleLineAsync(TranscriptResult result, TranscriptLine line)
    {
        if (_current == line) { Stop(); return; }
        await PlayLineAsync(result, line);
    }

    public async Task PlayLineAsync(TranscriptResult result, TranscriptLine line)
    {
        // A live recording still being written is read directly (decrypting it as it grows); anything else via MediaPath.
        var logical = result.MediaLogical;
        if (logical == null || !Security.SecureFile.Exists(logical))
            throw new InvalidOperationException("No audio is available for this transcript (it was saved as transcript only, or the file was moved).");

        var samples = IsOwnLiveWav(logical) ? await Task.Run(() => LiveRecording.ReadPcm16Mono(logical)) : await LoadAsync(result.MediaPath!);

        // Play until the next line starts (or a little past this line's end if there's a long pause after it).
        var next = result.Lines.Where(l => l.Start > line.Start).OrderBy(l => l.Start).FirstOrDefault();
        double endSec = line.End.TotalSeconds + 0.6;
        if (next != null && next.Start.TotalSeconds > line.End.TotalSeconds && next.Start.TotalSeconds - line.End.TotalSeconds < 2.5)
            endSec = next.Start.TotalSeconds;
        else if (next != null && next.Start.TotalSeconds <= line.End.TotalSeconds)
            endSec = line.End.TotalSeconds + 0.15;

        int start = Math.Clamp((int)((line.Start.TotalSeconds - 0.15) * Rate), 0, samples.Length);
        int end = Math.Clamp((int)(endSec * Rate), start, samples.Length);
        if (end - start < Rate / 10) throw new InvalidOperationException("That part of the recording isn't available yet.");

        Play(samples.AsSpan(start, end - start).ToArray(), line);
    }

    /// <summary>▶ on a speaker card: plays their sample clip, or stops it.</summary>
    public void ToggleClip(float[] clip, IPlayable owner)
    {
        if (_current == owner) { Stop(); return; }
        if (clip.Length > 0) Play(clip, owner);
    }

    private void Play(float[] clip, IPlayable owner)
    {
        Stop();
        var ms = new MemoryStream();
        var bytes = new byte[clip.Length * 2];
        float peak = Math.Max(0.05f, clip.Max(Math.Abs));
        float gain = Math.Min(0.9f / peak, 8f);   // normalise quiet voices
        for (int i = 0; i < clip.Length; i++)
        {
            short v = (short)Math.Clamp(clip[i] * gain * short.MaxValue, short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)v; bytes[i * 2 + 1] = (byte)(v >> 8);
        }
        var source = new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(Rate, 16, 1));

        var output = new WasapiOut(AudioClientShareMode.Shared, 80);
        output.Init(source);
        output.PlaybackStopped += (_, _) => OnUi(() =>
        {
            if (_out != output) return;           // a newer clip already replaced this one
            Finish();
        });
        _out = output;
        SetCurrent(owner);
        output.Play();
    }

    public void Stop()
    {
        var o = _out;
        _out = null;
        if (o != null) { try { o.Stop(); o.Dispose(); } catch { } }
        SetCurrent(null);
    }

    private void Finish()
    {
        try { _out?.Dispose(); } catch { }
        _out = null;
        SetCurrent(null);
    }

    private void SetCurrent(IPlayable? item)
    {
        if (_current == item) return;
        if (_current != null) _current.IsPlaying = false;
        _current = item;
        if (_current != null) _current.IsPlaying = true;
        StateChanged?.Invoke();
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    /// <summary>Forget cached audio (e.g. a live recording that keeps growing).</summary>
    public void Invalidate(string? path = null)
    {
        if (path == null || path == _cachedPath) { _cached = null; _cachedPath = null; }
    }

    /// <summary>Decoded 16 kHz audio of a transcript's media (cached).</summary>
    public async Task<float[]> LoadAsync(string path)
    {
        bool growing = IsOwnLiveWav(path);
        if (!growing && _cachedPath == path && _cached != null) return _cached;

        float[] samples = growing
            ? await Task.Run(() => LiveRecording.ReadPcm16Mono(path))
            : await Task.Run(() => AudioDecoder.DecodeAsync(AudioDecoder.FindFfmpeg() ?? throw new InvalidOperationException("FFmpeg not found."),
                                                               path, CancellationToken.None));
        if (!growing) { _cached = samples; _cachedPath = path; }
        return samples;
    }

    private static bool IsOwnLiveWav(string path) =>
        path.Contains(Path.Combine("WhisperDesk", "live"), StringComparison.OrdinalIgnoreCase) && path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
}
