using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WhisperDesk.Services.Security;

/// <summary>
/// WDE1, WhisperDesk's encrypted file format: a streaming AEAD in the style of STREAM / Tink's "AES-GCM-HKDF streaming".
/// <code>
/// header (48 bytes): "WDE1" | version 1 | flags 0 | 2 reserved | 32-byte random salt | 8 reserved
/// chunk*:            4-byte length (LE; top bit = final chunk) | ciphertext | 16-byte GCM tag
/// </code>
/// Per-file key = HKDF-SHA256(data key, salt, "WhisperDesk WDE1 file key"). Chunk nonce = big-endian chunk counter.
/// AAD = SHA-256(header ‖ logical path) ‖ counter ‖ length field. So a file can't be changed, truncated, reordered,
/// or swapped for another file (the logical path is bound in), without decryption failing.
/// A stream without its final chunk (a recording cut off by a crash) is only readable with <c>allowIncomplete</c>.
/// </summary>
public static class WdeFormat
{
    public const string Extension = ".wde";
    public const int HeaderSize = 48;
    public const int DefaultChunk = 64 * 1024;
    public const int MaxChunk = 1 << 20;
    private const uint FinalBit = 0x8000_0000u;
    private static readonly byte[] Magic = "WDE1"u8.ToArray();

    public static bool HasMagic(ReadOnlySpan<byte> head) => head.Length >= 4 && head[..4].SequenceEqual(Magic);

    public static bool IsWdeFile(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[4];
            return f.Read(head) == 4 && HasMagic(head);
        }
        catch { return false; }
    }

    internal static byte[] FileKey(byte[] dataKey, ReadOnlySpan<byte> salt) => VaultCrypto.Hkdf(dataKey, salt, "WhisperDesk WDE1 file key");

    internal static byte[] AadPrefix(ReadOnlySpan<byte> header, string context)
    {
        var ctx = Encoding.UTF8.GetBytes(context.Replace('\\', '/').ToLowerInvariant());
        var all = new byte[header.Length + ctx.Length];
        header.CopyTo(all);
        ctx.CopyTo(all.AsSpan(header.Length));
        return SHA256.HashData(all);
    }

    internal static void Nonce(Span<byte> nonce, ulong counter)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
    }

    internal static void Aad(Span<byte> aad, ReadOnlySpan<byte> prefix, ulong counter, uint lengthField)
    {
        prefix.CopyTo(aad);
        BinaryPrimitives.WriteUInt64BigEndian(aad.Slice(32, 8), counter);
        BinaryPrimitives.WriteUInt32LittleEndian(aad.Slice(40, 4), lengthField);
    }

    /// <summary>Encrypts a whole buffer.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plain, byte[] dataKey, string context)
    {
        using var ms = new MemoryStream(plain.Length + 128);
        using (var w = new WdeWriteStream(ms, dataKey, context, leaveOpen: true)) w.Write(plain);
        return ms.ToArray();
    }

    /// <summary>Decrypts a whole buffer (throws <see cref="CryptographicException"/> on any change).</summary>
    public static byte[] Decrypt(byte[] data, byte[] dataKey, string context)
    {
        using var r = new WdeReadStream(new MemoryStream(data, writable: false), dataKey, context, allowIncomplete: false);
        using var ms = new MemoryStream(data.Length);
        r.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>Write side of <see cref="WdeFormat"/>. <see cref="Flush"/> seals what is buffered as a (short) chunk.</summary>
public sealed class WdeWriteStream : Stream
{
    private readonly Stream _out;
    private readonly bool _leaveOpen;
    private readonly AesGcm _aes;
    private readonly byte[] _prefix;
    private readonly byte[] _buf;
    private readonly byte[] _cipher;
    private int _fill;
    private ulong _counter;
    private long _plainLength;
    private bool _finished;

    public WdeWriteStream(Stream output, byte[] dataKey, string context, bool leaveOpen = false, int chunkSize = WdeFormat.DefaultChunk)
    {
        if (chunkSize is <= 0 or > WdeFormat.MaxChunk) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        _out = output;
        _leaveOpen = leaveOpen;
        var header = new byte[WdeFormat.HeaderSize];
        "WDE1"u8.CopyTo(header);
        header[4] = 1;
        RandomNumberGenerator.Fill(header.AsSpan(8, VaultCrypto.SaltSize));
        var key = WdeFormat.FileKey(dataKey, header.AsSpan(8, VaultCrypto.SaltSize));
        try { _aes = new AesGcm(key, VaultCrypto.TagSize); }
        finally { VaultCrypto.Clear(key); }
        _prefix = WdeFormat.AadPrefix(header, context);
        _buf = GC.AllocateArray<byte>(chunkSize, pinned: true);
        _cipher = new byte[chunkSize];
        _out.Write(header);
    }

    /// <summary>Plaintext bytes written so far.</summary>
    public override long Length => _plainLength;

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (_finished) throw new ObjectDisposedException(nameof(WdeWriteStream));
        while (data.Length > 0)
        {
            int take = Math.Min(data.Length, _buf.Length - _fill);
            data[..take].CopyTo(_buf.AsSpan(_fill));
            _fill += take;
            _plainLength += take;
            data = data[take..];
            if (_fill == _buf.Length) Emit(final: false);
        }
    }

    private void Emit(bool final)
    {
        uint len = (uint)_fill | (final ? 0x8000_0000u : 0);
        Span<byte> nonce = stackalloc byte[VaultCrypto.NonceSize];
        Span<byte> aad = stackalloc byte[44];
        Span<byte> tag = stackalloc byte[VaultCrypto.TagSize];
        Span<byte> lenBytes = stackalloc byte[4];
        WdeFormat.Nonce(nonce, _counter);
        WdeFormat.Aad(aad, _prefix, _counter, len);
        _aes.Encrypt(nonce, _buf.AsSpan(0, _fill), _cipher.AsSpan(0, _fill), tag, aad);
        BinaryPrimitives.WriteUInt32LittleEndian(lenBytes, len);
        _out.Write(lenBytes);
        _out.Write(_cipher, 0, _fill);
        _out.Write(tag);
        CryptographicOperations.ZeroMemory(_buf.AsSpan(0, _fill));
        _fill = 0;
        _counter++;
    }

    /// <summary>Seals the buffered data as a chunk and flushes it to disk (crash safety for long recordings).</summary>
    public override void Flush()
    {
        if (_finished) return;
        if (_fill > 0) Emit(final: false);
        _out.Flush();
        if (_out is FileStream fs) fs.Flush(flushToDisk: true);
    }

    /// <summary>Writes the final chunk. Without it, readers treat the file as cut short.</summary>
    public void Complete()
    {
        if (_finished) return;
        Emit(final: true);
        _finished = true;
        _out.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { Complete(); }
            finally
            {
                _aes.Dispose();
                CryptographicOperations.ZeroMemory(_buf);
                if (!_leaveOpen) _out.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_finished;
    public override long Position { get => _plainLength; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Read side of <see cref="WdeFormat"/>: sequential, authenticates every chunk before returning its bytes.</summary>
public sealed class WdeReadStream : Stream
{
    private readonly Stream _in;
    private readonly bool _allowIncomplete;
    private readonly AesGcm _aes;
    private readonly byte[] _prefix;
    private byte[] _plain = Array.Empty<byte>();
    private int _pos, _len;
    private ulong _counter;
    private bool _done;

    /// <summary>True when the stream ended without its final chunk (only possible with allowIncomplete).</summary>
    public bool WasIncomplete { get; private set; }

    public WdeReadStream(Stream input, byte[] dataKey, string context, bool allowIncomplete)
    {
        _in = input;
        _allowIncomplete = allowIncomplete;
        var header = new byte[WdeFormat.HeaderSize];
        if (ReadFully(header) != header.Length || !WdeFormat.HasMagic(header) || header[4] != 1)
            throw new CryptographicException("Not a WhisperDesk encrypted file, or an unsupported version.");
        var key = WdeFormat.FileKey(dataKey, header.AsSpan(8, VaultCrypto.SaltSize));
        try { _aes = new AesGcm(key, VaultCrypto.TagSize); }
        finally { VaultCrypto.Clear(key); }
        _prefix = WdeFormat.AadPrefix(header, context);
    }

    private int ReadFully(Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = _in.Read(buffer[total..]);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private bool NextChunk()
    {
        if (_done) return false;
        Span<byte> lenBytes = stackalloc byte[4];
        int got = ReadFully(lenBytes);
        if (got == 0 || got < 4)
        {
            if (_allowIncomplete) { WasIncomplete = true; _done = true; return false; }
            throw new CryptographicException("The encrypted file is incomplete (it was cut short).");
        }
        uint field = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);
        bool final = (field & 0x8000_0000u) != 0;
        int size = (int)(field & 0x7FFF_FFFFu);
        if (size > WdeFormat.MaxChunk) throw new CryptographicException("The encrypted file is damaged.");
        var cipher = new byte[size];
        Span<byte> tag = stackalloc byte[VaultCrypto.TagSize];
        if (ReadFully(cipher) != size || ReadFully(tag) != VaultCrypto.TagSize)
        {
            if (_allowIncomplete) { WasIncomplete = true; _done = true; return false; }   // a chunk being written when power was lost
            throw new CryptographicException("The encrypted file is incomplete (it was cut short).");
        }
        if (_plain.Length < size) { CryptographicOperations.ZeroMemory(_plain); _plain = GC.AllocateArray<byte>(Math.Max(size, 1), pinned: true); }
        Span<byte> nonce = stackalloc byte[VaultCrypto.NonceSize];
        Span<byte> aad = stackalloc byte[44];
        WdeFormat.Nonce(nonce, _counter);
        WdeFormat.Aad(aad, _prefix, _counter, field);
        _aes.Decrypt(nonce, cipher, tag, _plain.AsSpan(0, size), aad);   // throws AuthenticationTagMismatchException
        _counter++;
        _pos = 0;
        _len = size;
        if (final)
        {
            _done = true;
            // Anything after the final chunk is an attack or damage.
            Span<byte> one = stackalloc byte[1];
            if (_in.Read(one) > 0) throw new CryptographicException("The encrypted file has data after its end.");
        }
        return true;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (_pos >= _len)
            if (!NextChunk()) return 0;
        int n = Math.Min(buffer.Length, _len - _pos);
        _plain.AsSpan(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_plain);
            _in.Dispose();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
