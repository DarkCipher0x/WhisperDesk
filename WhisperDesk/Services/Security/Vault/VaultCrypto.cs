using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WhisperDesk.Services.Security;

/// <summary>
/// The only cryptographic building blocks WhisperDesk uses, all from Windows CNG through .NET: AES-256-GCM, HKDF-SHA256,
/// PBKDF2-HMAC-SHA256 and the system CSPRNG. Nothing here is a new algorithm or protocol; this class only fixes the
/// parameters in one place so they can be reviewed.
/// </summary>
public static class VaultCrypto
{
    public const int KeySize = 32, NonceSize = 12, TagSize = 16, SaltSize = 32;
    /// <summary>OWASP (2023) recommendation for PBKDF2-HMAC-SHA256.</summary>
    public const int Pbkdf2Iterations = 600_000;

    /// <summary>A key buffer pinned in memory (the GC never copies it), to be cleared with <see cref="Clear"/>.</summary>
    public static byte[] NewKeyBuffer(int size = KeySize) => GC.AllocateArray<byte>(size, pinned: true);

    public static byte[] RandomKey()
    {
        var k = NewKeyBuffer();
        RandomNumberGenerator.Fill(k);
        return k;
    }

    public static void Clear(byte[]? key)
    {
        if (key != null) CryptographicOperations.ZeroMemory(key);
    }

    /// <summary>HKDF-SHA256 (RFC 5869) into a pinned buffer.</summary>
    public static byte[] Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, string info, int length = KeySize)
    {
        var output = NewKeyBuffer(length);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt, Encoding.UTF8.GetBytes(info));
        return output;
    }

    /// <summary>PBKDF2-HMAC-SHA256 of the UTF-8 (NFC) passphrase into a pinned buffer.</summary>
    public static byte[] Pbkdf2(string passphrase, ReadOnlySpan<byte> salt, int iterations)
    {
        var bytes = Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC));
        try
        {
            var output = NewKeyBuffer();
            Rfc2898DeriveBytes.Pbkdf2(bytes, salt, output, iterations, HashAlgorithmName.SHA256);
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>AES-256-GCM seal with a fresh random nonce. Returns (nonce, ciphertext, tag).</summary>
    public static (byte[] Nonce, byte[] Cipher, byte[] Tag) Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag, aad);
        return (nonce, cipher, tag);
    }

    /// <summary>AES-256-GCM open into a pinned buffer. Throws <see cref="AuthenticationTagMismatchException"/> on any change.</summary>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> aad)
    {
        var plain = NewKeyBuffer(cipher.Length);
        using var aes = new AesGcm(key, TagSize);
        try { aes.Decrypt(nonce, cipher, tag, plain, aad); }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
        return plain;
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>RFC 4648 base32 without padding (authenticator apps and recovery keys).</summary>
    public static string Base32(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        var clean = new string(text.ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7').ToArray());
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            buffer = (buffer << 5) | (c <= 'Z' && c >= 'A' ? c - 'A' : c - '2' + 26);
            bits += 5;
            if (bits >= 8) { output.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return output.ToArray();
    }

    /// <summary>Constant-time comparison.</summary>
    public static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => CryptographicOperations.FixedTimeEquals(a, b);
}
