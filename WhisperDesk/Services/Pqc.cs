using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace WhisperDesk.Services;

/// <summary>
/// ML-DSA-65 (FIPS 204) post-quantum signatures, from BouncyCastle, so the same code runs on Windows 10 and 11 (.NET's own
/// ML-DSA needs a Windows build that has it). Used only beside a classical ECDSA P-256 signature: an update is trusted
/// when both verify, so a flaw in either one alone doesn't break it. The same scheme as ServerManagement and CaseKeeper.
/// </summary>
public static class Pqc
{
    public const string SigName = "ML-DSA-65";

    private static readonly SecureRandom Random = new();

    /// <summary>A new ML-DSA-65 key: its private seed (32 bytes) and public key (1,952 bytes). For the tests; the app
    /// itself only verifies (tools\sign-update.ps1 holds the real key).</summary>
    public static (byte[] Seed, byte[] Pub) SigGenerate()
    {
        var gen = new MLDsaKeyPairGenerator();
        gen.Init(new MLDsaKeyGenerationParameters(Random, MLDsaParameters.ml_dsa_65));
        var kp = gen.GenerateKeyPair();
        return (((MLDsaPrivateKeyParameters)kp.Private).GetSeed(), ((MLDsaPublicKeyParameters)kp.Public).GetEncoded());
    }

    /// <summary>An ML-DSA-65 signature (3,309 bytes, hedged: fresh randomness each time).</summary>
    public static byte[] Sign(byte[] seed, byte[] data)
    {
        var s = new MLDsaSigner(MLDsaParameters.ml_dsa_65, deterministic: false);
        s.Init(true, new ParametersWithRandom(MLDsaPrivateKeyParameters.FromSeed(MLDsaParameters.ml_dsa_65, seed), Random));
        s.BlockUpdate(data, 0, data.Length);
        return s.GenerateSignature();
    }

    public static bool Verify(byte[] pub, byte[] data, byte[] sig)
    {
        try
        {
            var s = new MLDsaSigner(MLDsaParameters.ml_dsa_65, deterministic: false);
            s.Init(false, MLDsaPublicKeyParameters.FromEncoding(MLDsaParameters.ml_dsa_65, pub));
            s.BlockUpdate(data, 0, data.Length);
            return s.VerifySignature(sig);
        }
        // Any malformed key or signature is simply not a valid signature
        catch (Exception) { return false; }
    }
}
