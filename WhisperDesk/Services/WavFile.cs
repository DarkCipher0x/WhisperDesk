using System.IO;

namespace WhisperDesk.Services;

/// <summary>Writes mono float samples as a 16-bit PCM WAV (used for speaker preview clips).</summary>
public static class WavFile
{
    public static void Write(string path, float[] samples, int sampleRate)
    {
        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);

        float peak = Math.Max(0.05f, samples.Max(Math.Abs));
        float gain = Math.Min(0.9f / peak, 8f); // normalise quiet voices for preview
        foreach (var s in samples) w.Write((short)Math.Clamp(s * gain * short.MaxValue, short.MinValue, short.MaxValue));
    }
}
