using System.IO;
using NAudio.Wave;
using WhisperDesk.Services;
using Xunit;

namespace WhisperDesk.Tests;

/// <summary>Windows' own decoders (Media Foundation), used when FFmpeg isn't installed, as on a fresh Store install.</summary>
public class AudioDecoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wd-decoder-" + Guid.NewGuid().ToString("N"));

    public AudioDecoderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Two seconds of a 440 Hz tone at half scale, stereo 44.1 kHz 16-bit.</summary>
    private string WriteStereoWav()
    {
        var path = Path.Combine(_dir, "tone.wav");
        using var w = new WaveFileWriter(path, new WaveFormat(44100, 16, 2));
        for (int i = 0; i < 44100 * 2; i++)
        {
            var s = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 44100.0));
            w.WriteSample(s); w.WriteSample(s);
        }
        return path;
    }

    private static void AssertTone(float[] samples)
    {
        Assert.InRange(samples.Length, AudioDecoder.SampleRate * 2 - 1600, AudioDecoder.SampleRate * 2 + 1600);
        var peak = samples.Skip(AudioDecoder.SampleRate / 4).Take(AudioDecoder.SampleRate).Max(Math.Abs);
        Assert.InRange(peak, 0.4f, 0.6f);
    }

    [Fact]
    public async Task Wav_without_ffmpeg_decodes_to_16k_mono()
    {
        AssertTone(await AudioDecoder.DecodeAsync(null, WriteStereoWav(), CancellationToken.None));
    }

    [Fact]
    public async Task Aac_m4a_without_ffmpeg_round_trips()
    {
        var wav = WriteStereoWav();
        var m4a = Path.Combine(_dir, "tone.m4a");
        using (var reader = new WaveFileReader(wav)) MediaFoundationEncoder.EncodeToAac(reader, m4a, 96000);
        AssertTone(await AudioDecoder.DecodeAsync(null, m4a, CancellationToken.None));
    }

    [Fact]
    public async Task Unreadable_file_says_how_to_get_more_formats()
    {
        var bogus = Path.Combine(_dir, "not-audio.mp3");
        File.WriteAllText(bogus, "this is not audio");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AudioDecoder.DecodeAsync(null, bogus, CancellationToken.None));
        Assert.Contains("FFmpeg", ex.Message);
    }
}
