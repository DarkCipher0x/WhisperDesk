using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WhisperDesk.Services.Security;

/// <summary>
/// Secrets (webhook URLs with tokens, add-on API keys, OAuth tokens) in Windows Credential Manager: protected by Windows
/// (DPAPI, tied to the Windows account), stored on this PC only (never roaming). When encryption is on, the value is also
/// AES-256-GCM encrypted with a key derived from the vault, so it can't be read while WhisperDesk is locked.
/// Secrets never go into settings.json, logs, error messages or backups.
/// </summary>
public static class SecretStore
{
    private const string Prefix = "WhisperDesk/";
    private const string VaultMarker = "wdv1:";

    private static string Target(string scope, string key) => $"{Prefix}{scope}/{key}";

    /// <summary>Secrets can be read and written now (encryption off, or on and unlocked).</summary>
    public static bool CanStore => !Vault.IsEnabled || Vault.IsUnlocked;

    /// <summary>Stores (or with null/empty, deletes) a secret.</summary>
    public static void Set(string scope, string key, string? value)
    {
        var target = Target(scope, key);
        if (string.IsNullOrEmpty(value)) { CredDelete(target, CredType.Generic, 0); return; }
        var stored = Protect(value);
        var blob = Encoding.UTF8.GetBytes(stored);
        var cred = new Credential
        {
            Type = CredType.Generic, TargetName = target, CredentialBlobSize = (uint)blob.Length,
            Persist = CredPersist.LocalMachine, UserName = "WhisperDesk",
        };
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            cred.CredentialBlob = handle.AddrOfPinnedObject();
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the secret in Windows Credential Manager.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            handle.Free();
        }
    }

    /// <summary>The secret, or null when there is none or WhisperDesk is locked.</summary>
    public static string? Get(string scope, string key)
    {
        if (!CredRead(Target(scope, key), CredType.Generic, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlobSize == 0) return "";
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            var text = Encoding.UTF8.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            return Unprotect(text);
        }
        finally { CredFree(ptr); }
    }

    public static bool Has(string scope, string key)
    {
        if (!CredRead(Target(scope, key), CredType.Generic, 0, out var ptr)) return false;
        CredFree(ptr);
        return true;
    }

    public static void DeleteScope(string scope)
    {
        foreach (var t in List(Prefix + scope + "/*")) CredDelete(t, CredType.Generic, 0);
    }

    /// <summary>Every WhisperDesk secret readable now (scope, key, value) — used when encryption is turned off.</summary>
    public static List<(string Scope, string Key, string Value)> ReadAll()
    {
        var all = new List<(string, string, string)>();
        foreach (var target in List(Prefix + "*"))
        {
            var rest = target[Prefix.Length..];
            var slash = rest.LastIndexOf('/');
            if (slash <= 0) continue;
            var (scope, key) = (rest[..slash], rest[(slash + 1)..]);
            if (Get(scope, key) is { } v) all.Add((scope, key, v));
        }
        return all;
    }

    /// <summary>Removes every WhisperDesk secret (uninstall with "settings" ticked).</summary>
    public static void DeleteAll()
    {
        foreach (var t in List(Prefix + "*")) CredDelete(t, CredType.Generic, 0);
    }

    /// <summary>Re-stores every WhisperDesk secret in the current form (after encryption is turned on or off).</summary>
    public static int Rewrap()
    {
        int n = 0;
        foreach (var target in List(Prefix + "*"))
        {
            var rest = target[Prefix.Length..];
            var slash = rest.LastIndexOf('/');
            if (slash <= 0) continue;
            var (scope, key) = (rest[..slash], rest[(slash + 1)..]);
            // Read with the old form: a vault-wrapped value needs the vault unlocked.
            var value = Get(scope, key);
            if (value == null) continue;
            Set(scope, key, value);
            n++;
        }
        return n;
    }

    private static string Protect(string value)
    {
        if (!Vault.IsEnabled) return value;
        var key = Vault.DeriveKey("secrets");
        try
        {
            var plain = Encoding.UTF8.GetBytes(value);
            var blob = SealedBlob.Seal(key, plain, "WhisperDesk secret v1");
            CryptographicOperations.ZeroMemory(plain);
            return VaultMarker + blob.Nonce + "." + blob.Cipher + "." + blob.Tag;
        }
        finally { VaultCrypto.Clear(key); }
    }

    private static string? Unprotect(string stored)
    {
        if (!stored.StartsWith(VaultMarker, StringComparison.Ordinal)) return stored;
        if (!Vault.IsUnlocked) return null;
        var parts = stored[VaultMarker.Length..].Split('.');
        if (parts.Length != 3) return null;
        var key = Vault.DeriveKey("secrets");
        try
        {
            var plain = new SealedBlob { Nonce = parts[0], Cipher = parts[1], Tag = parts[2] }.Open(key, "WhisperDesk secret v1");
            var s = Encoding.UTF8.GetString(plain);
            VaultCrypto.Clear(plain);
            return s;
        }
        catch (CryptographicException) { return null; }
        finally { VaultCrypto.Clear(key); }
    }

    private static List<string> List(string filter)
    {
        var list = new List<string>();
        if (!CredEnumerate(filter, 0, out var count, out var ptr)) return list;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.ReadIntPtr(ptr, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<Credential>(item);
                if (cred.TargetName != null) list.Add(cred.TargetName);
            }
        }
        finally { CredFree(ptr); }
        return list;
    }

    // ---------------- Win32 ----------------

    private enum CredType : uint { Generic = 1 }
    private enum CredPersist : uint { LocalMachine = 2 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public CredType Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredPersist Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, CredType type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, CredType type, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredEnumerateW")]
    private static extern bool CredEnumerate(string? filter, uint flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
