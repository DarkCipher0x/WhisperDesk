using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WhisperDesk.Services.Security;

/// <summary>
/// Time-based one-time passwords, RFC 6238 (HMAC-SHA1, 6 digits, 30 s): the format every authenticator app supports
/// (Microsoft/Google Authenticator, Aegis, 1Password, Bitwarden…). Codes from one step before or after are accepted
/// for clock drift; a code is never accepted twice.
/// </summary>
public static class Totp
{
    public const int Digits = 6, Period = 30;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTimeOffset t) => t.ToUnixTimeSeconds() / Period;

    /// <summary>The code for one time step (RFC 4226 dynamic truncation).</summary>
    public static string Code(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>The step the code belongs to (now ±1), or null. Steps at or before <paramref name="lastUsed"/> are refused.</summary>
    public static long? Verify(ReadOnlySpan<byte> secret, string code, long lastUsed, DateTimeOffset now)
    {
        code = new string(code.Where(char.IsDigit).ToArray());
        if (code.Length != Digits) return null;
        long current = StepAt(now);
        long? match = null;
        for (long s = current - 1; s <= current + 1; s++)
        {
            // Compare every candidate (constant work), keep the match.
            bool ok = CryptographicOperations.FixedTimeEquals(VaultCrypto.Utf8(Code(secret, s)), VaultCrypto.Utf8(code));
            if (ok && s > lastUsed) match = s;
        }
        return match;
    }

    public static string ProvisioningUri(ReadOnlySpan<byte> secret, string account) =>
        $"otpauth://totp/WhisperDesk:{Uri.EscapeDataString(account)}?secret={VaultCrypto.Base32(secret)}&issuer=WhisperDesk&algorithm=SHA1&digits={Digits}&period={Period}";
}
