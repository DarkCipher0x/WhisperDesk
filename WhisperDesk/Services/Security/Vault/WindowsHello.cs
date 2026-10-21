using System.Security.Cryptography;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace WhisperDesk.Services.Security;

/// <summary>
/// Windows Hello as a hardware-backed unlock factor. Windows keeps an RSA key for WhisperDesk in the TPM, usable only after
/// the user's PIN, face or fingerprint (with the TPM's own anti-hammering). WhisperDesk asks it to sign a random challenge
/// stored in the vault; RSA PKCS#1 v1.5 signatures are deterministic, so the signature is a secret that only this PC's TPM
/// can reproduce. Its SHA-256 goes into HKDF to make a key-encryption key. Enrolment signs twice and refuses if the two
/// signatures differ, so a non-deterministic implementation can never lock anyone out.
/// </summary>
public static class WindowsHello
{
    public static async Task<bool> IsAvailableAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch { return false; }
    }

    public static string CredentialName(string vaultId) => "WhisperDesk vault " + vaultId;

    /// <summary>Creates (or replaces) the Hello key and returns the secret for <paramref name="challenge"/>.</summary>
    public static async Task<byte[]> EnrollAsync(string vaultId, byte[] challenge)
    {
        var created = await KeyCredentialManager.RequestCreateAsync(CredentialName(vaultId), KeyCredentialCreationOption.ReplaceExisting);
        if (created.Status != KeyCredentialStatus.Success) throw new InvalidOperationException(Describe(created.Status));
        var first = await SignAsync(created.Credential, challenge);
        var second = await SignAsync(created.Credential, challenge);
        if (!VaultCrypto.Equal(first, second))
        {
            VaultCrypto.Clear(first); VaultCrypto.Clear(second);
            try { await KeyCredentialManager.DeleteAsync(CredentialName(vaultId)); } catch { }
            throw new InvalidOperationException("This PC's Windows Hello gives a different answer each time, so it can't be used to unlock WhisperDesk.");
        }
        VaultCrypto.Clear(second);
        return first;
    }

    /// <summary>Asks for Windows Hello and returns the secret for <paramref name="challenge"/>.</summary>
    public static async Task<byte[]> UnlockAsync(string vaultId, byte[] challenge)
    {
        var opened = await KeyCredentialManager.OpenAsync(CredentialName(vaultId));
        if (opened.Status != KeyCredentialStatus.Success) throw new InvalidOperationException(Describe(opened.Status));
        return await SignAsync(opened.Credential, challenge);
    }

    public static async Task RemoveAsync(string vaultId)
    {
        try { await KeyCredentialManager.DeleteAsync(CredentialName(vaultId)); } catch { }
    }

    private static async Task<byte[]> SignAsync(KeyCredential credential, byte[] challenge)
    {
        var result = await credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
        if (result.Status != KeyCredentialStatus.Success) throw new InvalidOperationException(Describe(result.Status));
        CryptographicBuffer.CopyToByteArray(result.Result, out byte[] signature);
        var secret = VaultCrypto.NewKeyBuffer();
        SHA256.HashData(signature, secret);
        CryptographicOperations.ZeroMemory(signature);
        return secret;
    }

    private static string Describe(KeyCredentialStatus s) => s switch
    {
        KeyCredentialStatus.UserCanceled => "Windows Hello was cancelled.",
        KeyCredentialStatus.NotFound => "The Windows Hello key for WhisperDesk wasn't found on this PC (it was removed, or this is another PC).",
        KeyCredentialStatus.UserPrefersPassword => "Windows Hello: the user chose to use the passphrase.",
        KeyCredentialStatus.SecurityDeviceLocked => "Windows Hello is locked after too many attempts. Try again later or use the passphrase.",
        _ => "Windows Hello is not available (" + s + ").",
    };
}
