using System.IO;
using System.Security.Cryptography;
using System.Text;
using WhisperDesk.Services.Security;
using Xunit;

namespace WhisperDesk.Tests;

/// <summary>The WDE1 file format: round trips, and every kind of tampering must be detected.</summary>
public class WdeFormatTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(1_048_579)]
    public void RoundTrips(int size)
    {
        var data = RandomNumberGenerator.GetBytes(size);
        var enc = WdeFormat.Encrypt(data, Key, "lib/r/abc/metadata.json");
        Assert.True(WdeFormat.HasMagic(enc));
        Assert.Equal(data, WdeFormat.Decrypt(enc, Key, "lib/r/abc/metadata.json"));
    }

    [Fact]
    public void CiphertextDoesNotContainPlaintext()
    {
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Confidential settlement terms. ", 200)));
        var enc = WdeFormat.Encrypt(text, Key, "x");
        Assert.DoesNotContain("Confidential", Encoding.UTF8.GetString(enc));
    }

    [Fact]
    public void SameContentEncryptsDifferentlyEachTime()
    {
        var data = Encoding.UTF8.GetBytes("same");
        Assert.NotEqual(WdeFormat.Encrypt(data, Key, "x"), WdeFormat.Encrypt(data, Key, "x"));
    }

    [Fact]
    public void EveryFlippedByteIsDetected()
    {
        var data = RandomNumberGenerator.GetBytes(70_000);   // two chunks
        var enc = WdeFormat.Encrypt(data, Key, "ctx");
        // Header, first length field, inside chunk 1, its tag, chunk 2, last byte.
        foreach (var i in new[] { 0, 5, 10, 47, 48, 50, 1000, 48 + 4 + 65536 + 3, enc.Length - 20, enc.Length - 1 })
        {
            var bad = (byte[])enc.Clone();
            bad[i] ^= 0x01;
            Assert.ThrowsAny<CryptographicException>(() => WdeFormat.Decrypt(bad, Key, "ctx"));
        }
    }

    [Fact]
    public void TruncationIsDetected()
    {
        var enc = WdeFormat.Encrypt(RandomNumberGenerator.GetBytes(200_000), Key, "ctx");
        // Cut exactly after the first full chunk (a chunk boundary) and in the middle of a chunk.
        int boundary = WdeFormat.HeaderSize + 4 + 65536 + 16;
        Assert.ThrowsAny<CryptographicException>(() => WdeFormat.Decrypt(enc[..boundary], Key, "ctx"));
        Assert.ThrowsAny<CryptographicException>(() => WdeFormat.Decrypt(enc[..(boundary + 100)], Key, "ctx"));
    }

    [Fact]
    public void AppendedDataIsDetected()
    {
        var enc = WdeFormat.Encrypt(Encoding.UTF8.GetBytes("hello"), Key, "ctx");
        Assert.ThrowsAny<CryptographicException>(() => WdeFormat.Decrypt(enc.Concat(new byte[] { 1, 2, 3 }).ToArray(), Key, "ctx"));
    }

    [Fact]
    public void ChunkReorderingIsDetected()
    {
        var enc = WdeFormat.Encrypt(RandomNumberGenerator.GetBytes(65536 * 3), Key, "ctx");
        int c = 4 + 65536 + 16, h = WdeFormat.HeaderSize;
        var swapped = enc[..h].Concat(enc[(h + c)..(h + 2 * c)]).Concat(enc[h..(h + c)]).Concat(enc[(h + 2 * c)..]).ToArray();
        Assert.ThrowsAny<CryptographicException>(() => WdeFormat.Decrypt(swapped, Key, "ctx"));
    }

    [Fact]
    public void FileSwappedBetweenRecordingsIsDetected()
    {
        var enc = WdeFormat.Encrypt(Encoding.UTF8.GetBytes("{\"Title\":\"A\"}"), Key, "lib/r/aaa/metadata.json");
        Assert.ThrowsAny<CryptographicException>(() => WdeFormat.Decrypt(enc, Key, "lib/r/bbb/metadata.json"));
    }

    [Fact]
    public void WrongKeyFails()
    {
        var enc = WdeFormat.Encrypt(Encoding.UTF8.GetBytes("x"), Key, "ctx");
        Assert.ThrowsAny<CryptographicException>(() => WdeFormat.Decrypt(enc, RandomNumberGenerator.GetBytes(32), "ctx"));
    }

    [Fact]
    public void RecordingCutOffByACrashIsRecoverableOnlyWhenAskedFor()
    {
        using var ms = new MemoryStream();
        var w = new WdeWriteStream(ms, Key, "app/live/x.wav", leaveOpen: true);
        var part = RandomNumberGenerator.GetBytes(10_000);
        w.Write(part);
        w.Flush();                          // sealed as a non-final chunk, like the 10-second flushes while recording
        var crashed = ms.ToArray();         // power cut: no final chunk

        Assert.ThrowsAny<CryptographicException>(() => WdeFormat.Decrypt(crashed, Key, "app/live/x.wav"));
        using var r = new WdeReadStream(new MemoryStream(crashed), Key, "app/live/x.wav", allowIncomplete: true);
        using var outMs = new MemoryStream();
        r.CopyTo(outMs);
        Assert.Equal(part, outMs.ToArray());
        Assert.True(r.WasIncomplete);
    }
}

/// <summary>RFC 6238 / RFC 4648 test vectors and replay protection.</summary>
public class TotpTests
{
    private static readonly byte[] Rfc6238Secret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void MatchesRfc6238(long unixTime, string expected) =>
        Assert.Equal(expected, Totp.Code(Rfc6238Secret, unixTime / Totp.Period));

    [Fact]
    public void AcceptsAdjacentStepsAndRefusesReplay()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepAt(now);
        var code = Totp.Code(Rfc6238Secret, step);
        Assert.Equal(step, Totp.Verify(Rfc6238Secret, code, 0, now));
        Assert.Null(Totp.Verify(Rfc6238Secret, code, step, now));                                 // already used
        Assert.Equal(step - 1, Totp.Verify(Rfc6238Secret, Totp.Code(Rfc6238Secret, step - 1), 0, now));
        Assert.Null(Totp.Verify(Rfc6238Secret, Totp.Code(Rfc6238Secret, step - 3), 0, now));     // too old
        Assert.Null(Totp.Verify(Rfc6238Secret, "12345", 0, now));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32MatchesRfc4648(string plain, string encoded)
    {
        Assert.Equal(encoded, VaultCrypto.Base32(Encoding.ASCII.GetBytes(plain)));
        Assert.Equal(plain, Encoding.ASCII.GetString(VaultCrypto.FromBase32(encoded)));
    }
}
