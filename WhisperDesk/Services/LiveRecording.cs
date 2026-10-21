using System.Buffers.Binary;
using System.IO;
using NAudio.Wave;
using WhisperDesk.Services.Security;

namespace WhisperDesk.Services;

/// <summary>Where a live session's audio goes while it is recorded.</summary>
public interface IRecordingSink : IDisposable
{
    /// <summary>Bytes of audio data written so far.</summary>
    long Length { get; }
    void WriteSamples(float[] samples);
    /// <summary>Makes what was written so far survive a crash or power cut.</summary>
    void Flush();
}

/// <summary>
/// Live recordings (16 kHz, 16-bit mono WAV). With encryption off: an ordinary WAV file. With encryption on: the WAV is
/// written straight into an encrypted append-only stream (never to disk in plaintext); every flush seals a chunk, so after
/// a crash everything up to the last flush can still be decrypted and recovered.
/// </summary>
public static class LiveRecording
{
    public const int Rate = AudioDecoder.SampleRate;
    private const int HeaderSize = 44;

    public static IRecordingSink Create(string path) =>
        Vault.IsEnabled ? new EncryptedWavSink(SecureFile.CreateAppendOnly(path)) : new PlainWavSink(new WaveFileWriter(path, new WaveFormat(Rate, 16, 1)));

    /// <summary>
    /// After recording (or after a crash): an encrypted recording is rewritten once with the correct WAV sizes and a proper
    /// end, so players and FFmpeg read it normally. Plain recordings are already complete. Returns false if there is nothing.
    /// </summary>
    public static bool Finish(string path)
    {
        if (!SecureFile.IsEncrypted(path)) return File.Exists(path);
        long total = 0;
        using (var s = SecureFile.OpenRead(path, allowIncomplete: true))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) total += n;
        }
        if (total < HeaderSize) return false;
        // The encrypted stream can't be patched in place: write a copy with the right sizes, which replaces it on commit.
        var dst = SecureFile.Create(path);
        try
        {
            using (var src = SecureFile.OpenRead(path, allowIncomplete: true))
            {
                src.ReadExactly(new byte[HeaderSize]);
                dst.Write(WavHeader(total - HeaderSize));
                src.CopyTo(dst, 1 << 20);
            }
            dst.Commit();
        }
        finally { dst.Dispose(); }
        return true;
    }
    internal static byte[] WavHeader(long dataBytes)
    {
        uint data = (uint)Math.Min(dataBytes, uint.MaxValue - 36);
        var h = new byte[HeaderSize];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), data + 36);
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1);          // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), 1);          // mono
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), Rate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), Rate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), data);
        return h;
    }

    /// <summary>Reads WhisperDesk's own live WAV (also while it is still growing) as samples.</summary>
    public static float[] ReadPcm16Mono(string path)
    {
        using var s = SecureFile.OpenRead(path, allowIncomplete: true);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.GetBuffer();
        int count = (int)Math.Max(0, (ms.Length - HeaderSize) / 2);
        var samples = new float[count];
        for (int i = 0; i < count; i++) samples[i] = BitConverter.ToInt16(bytes, HeaderSize + i * 2) / 32768f;
        Array.Clear(bytes);
        return samples;
    }

    private sealed class PlainWavSink(WaveFileWriter w) : IRecordingSink
    {
        public long Length => w.Length;
        public void WriteSamples(float[] samples) => w.WriteSamples(samples, 0, samples.Length);
        public void Flush() => w.Flush();
        public void Dispose() => w.Dispose();
    }

    private sealed class EncryptedWavSink : IRecordingSink
    {
        private readonly WdeWriteStream _w;
        private readonly byte[] _buf = new byte[32768];

        public EncryptedWavSink(WdeWriteStream w)
        {
            _w = w;
            _w.Write(WavHeader(uint.MaxValue));   // sizes unknown while recording; fixed by Finish()
        }

        public long Length => _w.Length - HeaderSize;

        public void WriteSamples(float[] samples)
        {
            int i = 0;
            while (i < samples.Length)
            {
                int n = Math.Min(samples.Length - i, _buf.Length / 2);
                for (int k = 0; k < n; k++)
                {
                    var v = (short)Math.Clamp((int)(samples[i + k] * 32767f), short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(_buf.AsSpan(k * 2), v);
                }
                _w.Write(_buf, 0, n * 2);
                i += n;
            }
            Array.Clear(_buf);
        }

        public void Flush() => _w.Flush();
        public void Dispose() => _w.Dispose();   // writes the final chunk
    }
}
