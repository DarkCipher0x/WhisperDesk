using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperDesk.Services.Security;

public enum VaultState { Disabled, Locked, Unlocked }

/// <summary>Security policy, stored in vault.json and protected by a MAC made with the data key.</summary>
public sealed class VaultPolicy
{
    /// <summary>Lock after this many minutes without keyboard or mouse input in WhisperDesk; 0 = never.</summary>
    public int AutoLockMinutes { get; set; } = 10;
    public bool LockOnWindowsLock { get; set; } = true;
    public bool LockOnMinimize { get; set; }
    public bool ClearClipboardOnLock { get; set; } = true;
    public int MinPassphraseLength { get; set; } = 12;

    public VaultPolicy Copy() => (VaultPolicy)MemberwiseClone();
}

public sealed class SealedBlob
{
    public string Nonce { get; set; } = "";
    public string Cipher { get; set; } = "";
    public string Tag { get; set; } = "";

    public static SealedBlob Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plain, string aad)
    {
        var (n, c, t) = VaultCrypto.Seal(key, plain, VaultCrypto.Utf8(aad));
        return new SealedBlob { Nonce = Convert.ToBase64String(n), Cipher = Convert.ToBase64String(c), Tag = Convert.ToBase64String(t) };
    }

    public byte[] Open(ReadOnlySpan<byte> key, string aad) =>
        VaultCrypto.Open(key, Convert.FromBase64String(Nonce), Convert.FromBase64String(Cipher), Convert.FromBase64String(Tag), VaultCrypto.Utf8(aad));
}

/// <summary>One way to unwrap the data key.</summary>
public sealed class KeySlot
{
    /// <summary>"passphrase", "recovery", "hello" (Hello alone) or "passphrase+hello" (both needed).</summary>
    public string Type { get; set; } = "";
    public string Kdf { get; set; } = "";
    public int Iterations { get; set; }
    public string Salt { get; set; } = "";
    /// <summary>Random challenge Windows Hello signs (Hello slots only).</summary>
    public string? Challenge { get; set; }
    public SealedBlob Key { get; set; } = new();
}

public sealed class VaultFile
{
    public string Format { get; set; } = "whisperdesk-vault";
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public List<KeySlot> Slots { get; set; } = new();
    /// <summary>TOTP secret, encrypted with a key derived from the data key (null = 2FA off).</summary>
    public SealedBlob? Totp { get; set; }
    public long TotpLastStep { get; set; }
    public VaultPolicy Policy { get; set; } = new();
    public string? PolicyMac { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset? LastFailure { get; set; }
}

public enum UnlockOutcome { Unlocked, NeedsTotp, Wrong, Delayed, Error }

public sealed record UnlockResult(UnlockOutcome Outcome, string? Message = null);

public sealed class VaultLockedException() : InvalidOperationException("WhisperDesk is locked: unlock it to read or save protected data.");

/// <summary>
/// The encryption vault: one random 256-bit data key (DEK) that encrypts every protected file, wrapped by key slots
/// (passphrase, recovery key, Windows Hello). The DEK exists only in memory while unlocked, in a pinned buffer that is
/// zeroed on lock. See docs/SECURITY-ARCHITECTURE.md.
/// </summary>
public static class Vault
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly object Lock_ = new();
    private static VaultFile? _file;
    private static byte[]? _dek;
    private static byte[]? _pendingDek;     // passphrase was right, waiting for the TOTP code
    private static int _holds;
    private static bool _lockRequested;

    public static string LocalData => Path.Combine(WhisperDesk.Services.AppPaths.LocalData);
    public static string FilePath => Path.Combine(LocalData, "vault.json");
    public const string LibraryCopyName = "vault.json";

    /// <summary>Raised (on the calling thread) when the state changes.</summary>
    public static event Action<VaultState>? StateChanged;

    /// <summary>True when the policy MAC didn't match on the last unlock (vault.json was edited outside WhisperDesk).</summary>
    public static bool PolicyWasTampered { get; private set; }

    static Vault() => Reload();

    public static void Reload()
    {
        lock (Lock_)
        {
            _file = null;
            try
            {
                if (File.Exists(FilePath))
                {
                    var f = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(FilePath));
                    if (f is { Format: "whisperdesk-vault", Version: 1 } && f.Slots.Count > 0 && f.Id.Length > 0) _file = f;
                }
            }
            catch { }
        }
    }

    public static VaultState State
    {
        get { lock (Lock_) return _file == null ? VaultState.Disabled : _dek != null ? VaultState.Unlocked : VaultState.Locked; }
    }

    public static bool IsEnabled => State != VaultState.Disabled;
    public static bool IsUnlocked => State == VaultState.Unlocked;
    public static string? Id { get { lock (Lock_) return _file?.Id; } }
    public static VaultPolicy Policy { get { lock (Lock_) return _file?.Policy.Copy() ?? new VaultPolicy(); } }
    public static bool HasTotp { get { lock (Lock_) return _file?.Totp != null; } }
    public static bool HasRecovery { get { lock (Lock_) return _file?.Slots.Any(s => s.Type == "recovery") == true; } }
    /// <summary>"none", "alternative" (Hello or passphrase) or "required" (passphrase and Hello).</summary>
    public static string HelloMode
    {
        get
        {
            lock (Lock_)
            {
                if (_file == null) return "none";
                if (_file.Slots.Any(s => s.Type == "passphrase+hello")) return "required";
                return _file.Slots.Any(s => s.Type == "hello") ? "alternative" : "none";
            }
        }
    }

    // ---------------- brute-force delay ----------------

    /// <summary>How long until another attempt is allowed (after 5 failures: 30 s, doubling, at most 15 min).</summary>
    public static TimeSpan RetryDelay
    {
        get
        {
            lock (Lock_)
            {
                if (_file is not { Failures: >= 5, LastFailure: { } last }) return TimeSpan.Zero;
                var wait = TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, _file.Failures - 5), 900));
                var left = last + wait - DateTimeOffset.Now;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    public static int Failures { get { lock (Lock_) return _file?.Failures ?? 0; } }

    private static void RecordFailure()
    {
        lock (Lock_)
        {
            if (_file == null) return;
            _file.Failures++;
            _file.LastFailure = DateTimeOffset.Now;
            SaveLocked();
        }
    }

    // ---------------- set up ----------------

    /// <summary>
    /// Turns encryption on: makes a new data key, a passphrase slot and a recovery slot. Returns the recovery key, which is
    /// shown to the user once and never stored. Existing data is converted separately (<see cref="VaultMigration"/>).
    /// </summary>
    public static string Create(string passphrase, VaultPolicy policy)
    {
        if (IsEnabled) throw new InvalidOperationException("Encryption is already on.");
        if (passphrase.Length < policy.MinPassphraseLength) throw new ArgumentException($"The passphrase must be at least {policy.MinPassphraseLength} characters.");
        var dek = VaultCrypto.RandomKey();
        var file = new VaultFile
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            Created = DateTimeOffset.Now, Policy = policy.Copy(),
        };
        file.Slots.Add(PassphraseSlot(file.Id, dek, passphrase, null));
        var (slot, recovery) = RecoverySlot(file.Id, dek);
        file.Slots.Add(slot);
        lock (Lock_)
        {
            _file = file;
            _dek = dek;
            SignPolicyLocked();
            SaveLocked();
        }
        StateChanged?.Invoke(VaultState.Unlocked);
        return recovery;
    }

    private static string SlotAad(string id, string type) => $"WhisperDesk vault slot v1|{id}|{type}";

    private static KeySlot PassphraseSlot(string id, byte[] dek, string passphrase, byte[]? helloSecret)
    {
        var salt = RandomNumberGenerator.GetBytes(VaultCrypto.SaltSize);
        var pb = VaultCrypto.Pbkdf2(passphrase, salt, VaultCrypto.Pbkdf2Iterations);
        byte[] kek;
        string type;
        if (helloSecret == null) { kek = VaultCrypto.Hkdf(pb, salt, "WhisperDesk passphrase slot"); type = "passphrase"; }
        else
        {
            var both = VaultCrypto.NewKeyBuffer(pb.Length + helloSecret.Length);
            pb.CopyTo(both, 0); helloSecret.CopyTo(both, pb.Length);
            kek = VaultCrypto.Hkdf(both, salt, "WhisperDesk passphrase+hello slot");
            VaultCrypto.Clear(both);
            type = "passphrase+hello";
        }
        VaultCrypto.Clear(pb);
        try
        {
            return new KeySlot { Type = type, Kdf = "PBKDF2-HMAC-SHA256", Iterations = VaultCrypto.Pbkdf2Iterations, Salt = Convert.ToBase64String(salt),
                                 Key = SealedBlob.Seal(kek, dek, SlotAad(id, type)) };
        }
        finally { VaultCrypto.Clear(kek); }
    }

    /// <summary>A 160-bit recovery key shown as 8 groups of 4 base32 characters.</summary>
    private static (KeySlot Slot, string Display) RecoverySlot(string id, byte[] dek)
    {
        var secret = RandomNumberGenerator.GetBytes(20);
        var salt = RandomNumberGenerator.GetBytes(VaultCrypto.SaltSize);
        var kek = VaultCrypto.Hkdf(secret, salt, "WhisperDesk recovery slot");
        try
        {
            var text = VaultCrypto.Base32(secret);
            var display = string.Join("-", Enumerable.Range(0, text.Length / 4).Select(i => text.Substring(i * 4, 4)));
            return (new KeySlot { Type = "recovery", Kdf = "HKDF-SHA256", Salt = Convert.ToBase64String(salt), Key = SealedBlob.Seal(kek, dek, SlotAad(id, "recovery")) }, display);
        }
        finally { VaultCrypto.Clear(kek); CryptographicOperations.ZeroMemory(secret); }
    }

    // ---------------- unlock ----------------

    public static async Task<UnlockResult> UnlockWithPassphraseAsync(string passphrase)
    {
        if (RetryDelay > TimeSpan.Zero) return new UnlockResult(UnlockOutcome.Delayed, $"Too many attempts. Try again in {Format(RetryDelay)}.");
        VaultFile file;
        lock (Lock_) file = _file ?? throw new InvalidOperationException("Encryption is not set up.");
        var slot = file.Slots.FirstOrDefault(s => s.Type is "passphrase" or "passphrase+hello");
        if (slot == null) return new UnlockResult(UnlockOutcome.Error, "This vault has no passphrase slot.");
        if (slot.Iterations is < 100_000 or > 10_000_000) return new UnlockResult(UnlockOutcome.Error, "The encryption header (vault.json) is damaged.");
        var salt = Convert.FromBase64String(slot.Salt);
        var pb = await Task.Run(() => VaultCrypto.Pbkdf2(passphrase, salt, slot.Iterations));
        byte[]? kek = null, hello = null;
        try
        {
            if (slot.Type == "passphrase") kek = VaultCrypto.Hkdf(pb, salt, "WhisperDesk passphrase slot");
            else
            {
                try { hello = await WindowsHello.UnlockAsync(file.Id, Convert.FromBase64String(slot.Challenge!)); }
                catch (Exception ex) { return new UnlockResult(UnlockOutcome.Error, ex.Message + " This vault needs both the passphrase and Windows Hello (or the recovery key)."); }
                var both = VaultCrypto.NewKeyBuffer(pb.Length + hello.Length);
                pb.CopyTo(both, 0); hello.CopyTo(both, pb.Length);
                kek = VaultCrypto.Hkdf(both, salt, "WhisperDesk passphrase+hello slot");
                VaultCrypto.Clear(both);
            }
            return TryOpen(file, slot, kek, viaRecovery: false);
        }
        finally { VaultCrypto.Clear(pb); VaultCrypto.Clear(kek); VaultCrypto.Clear(hello); }
    }

    /// <summary>
    /// Re-checks the passphrase while unlocked (before turning protections off or changing them), without changing state.
    /// Wrong answers count towards the brute-force delay like any other attempt.
    /// </summary>
    public static async Task<bool> VerifyPassphraseAsync(string passphrase)
    {
        if (RetryDelay > TimeSpan.Zero) return false;
        VaultFile file;
        lock (Lock_) file = _file ?? throw new InvalidOperationException("Encryption is not set up.");
        var slot = file.Slots.FirstOrDefault(s => s.Type is "passphrase" or "passphrase+hello");
        if (slot == null || slot.Iterations is < 100_000 or > 10_000_000) return false;
        var salt = Convert.FromBase64String(slot.Salt);
        var pb = await Task.Run(() => VaultCrypto.Pbkdf2(passphrase, salt, slot.Iterations));
        byte[]? kek = null, hello = null;
        try
        {
            if (slot.Type == "passphrase") kek = VaultCrypto.Hkdf(pb, salt, "WhisperDesk passphrase slot");
            else
            {
                hello = await WindowsHello.UnlockAsync(file.Id, Convert.FromBase64String(slot.Challenge!));
                var both = VaultCrypto.NewKeyBuffer(pb.Length + hello.Length);
                pb.CopyTo(both, 0); hello.CopyTo(both, pb.Length);
                kek = VaultCrypto.Hkdf(both, salt, "WhisperDesk passphrase+hello slot");
                VaultCrypto.Clear(both);
            }
            VaultCrypto.Clear(slot.Key.Open(kek, SlotAad(file.Id, slot.Type)));
            return true;
        }
        catch (CryptographicException) { RecordFailure(); return false; }
        finally { VaultCrypto.Clear(pb); VaultCrypto.Clear(kek); VaultCrypto.Clear(hello); }
    }

    public static async Task<UnlockResult> UnlockWithHelloAsync()
    {
        VaultFile file;
        lock (Lock_) file = _file ?? throw new InvalidOperationException("Encryption is not set up.");
        var slot = file.Slots.FirstOrDefault(s => s.Type == "hello");
        if (slot == null) return new UnlockResult(UnlockOutcome.Error, "Windows Hello is not set up for WhisperDesk.");
        byte[]? secret = null, kek = null;
        try
        {
            secret = await WindowsHello.UnlockAsync(file.Id, Convert.FromBase64String(slot.Challenge!));
            kek = VaultCrypto.Hkdf(secret, Convert.FromBase64String(slot.Salt), "WhisperDesk hello slot");
            return TryOpen(file, slot, kek, viaRecovery: false);
        }
        catch (Exception ex) { return new UnlockResult(UnlockOutcome.Error, ex.Message); }
        finally { VaultCrypto.Clear(secret); VaultCrypto.Clear(kek); }
    }

    /// <summary>Opens the vault with the recovery key. Skips Hello and 2FA; the caller must make the user set a new passphrase.</summary>
    public static UnlockResult UnlockWithRecovery(string recoveryKey)
    {
        if (RetryDelay > TimeSpan.Zero) return new UnlockResult(UnlockOutcome.Delayed, $"Too many attempts. Try again in {Format(RetryDelay)}.");
        VaultFile file;
        lock (Lock_) file = _file ?? throw new InvalidOperationException("Encryption is not set up.");
        var slot = file.Slots.FirstOrDefault(s => s.Type == "recovery");
        if (slot == null) return new UnlockResult(UnlockOutcome.Error, "This vault has no recovery key.");
        var secret = VaultCrypto.FromBase32(recoveryKey);
        if (secret.Length != 20) { RecordFailure(); return new UnlockResult(UnlockOutcome.Wrong, "That isn't a valid recovery key (32 characters, letters A–Z and digits 2–7)."); }
        var kek = VaultCrypto.Hkdf(secret, Convert.FromBase64String(slot.Salt), "WhisperDesk recovery slot");
        try { return TryOpen(file, slot, kek, viaRecovery: true); }
        finally { VaultCrypto.Clear(kek); CryptographicOperations.ZeroMemory(secret); }
    }

    private static UnlockResult TryOpen(VaultFile file, KeySlot slot, byte[] kek, bool viaRecovery)
    {
        byte[] dek;
        try { dek = slot.Key.Open(kek, SlotAad(file.Id, slot.Type)); }
        catch (CryptographicException)
        {
            RecordFailure();
            var left = Math.Max(0, 5 - Failures);
            return new UnlockResult(UnlockOutcome.Wrong, viaRecovery ? "That recovery key is not right."
                : "That passphrase is not right." + (left is > 0 and <= 3 ? $" {left} more tries before a waiting time." : ""));
        }
        lock (Lock_)
        {
            if (file.Totp != null && !viaRecovery)
            {
                VaultCrypto.Clear(_pendingDek);
                _pendingDek = dek;
                return new UnlockResult(UnlockOutcome.NeedsTotp);
            }
            Complete(dek);
        }
        StateChanged?.Invoke(VaultState.Unlocked);
        return new UnlockResult(UnlockOutcome.Unlocked);
    }

    /// <summary>Second step when 2FA is on. Wrong codes count as failed attempts.</summary>
    public static UnlockResult VerifyTotp(string code)
    {
        if (RetryDelay > TimeSpan.Zero) return new UnlockResult(UnlockOutcome.Delayed, $"Too many attempts. Try again in {Format(RetryDelay)}.");
        lock (Lock_)
        {
            if (_pendingDek == null || _file?.Totp == null) return new UnlockResult(UnlockOutcome.Error, "Enter the passphrase first.");
            var key = SubKey(_pendingDek, "totp");
            byte[]? secret = null;
            try
            {
                secret = _file.Totp.Open(key, "WhisperDesk totp v1|" + _file.Id);
                var step = Totp.Verify(secret, code, _file.TotpLastStep, DateTimeOffset.UtcNow);
                if (step == null)
                {
                    _file.Failures++;
                    _file.LastFailure = DateTimeOffset.Now;
                    SaveLocked();
                    return new UnlockResult(UnlockOutcome.Wrong, "That code is not right (or was already used). Check the time on your phone and PC.");
                }
                _file.TotpLastStep = step.Value;
                var dek = _pendingDek;
                _pendingDek = null;
                Complete(dek);
            }
            finally { VaultCrypto.Clear(key); VaultCrypto.Clear(secret); }
        }
        StateChanged?.Invoke(VaultState.Unlocked);
        return new UnlockResult(UnlockOutcome.Unlocked);
    }

    /// <summary>Called with the lock held: installs the key, resets the failure counter and checks the policy MAC.</summary>
    private static void Complete(byte[] dek)
    {
        VaultCrypto.Clear(_dek);
        _dek = dek;
        _file!.Failures = 0;
        _file.LastFailure = null;
        _lockRequested = false;
        if (!PolicyMacMatches())
        {
            // vault.json was edited outside WhisperDesk: don't trust a weakened policy.
            PolicyWasTampered = true;
            _file.Policy = new VaultPolicy();
            SignPolicyLocked();
        }
        SaveLocked();
    }

    public static void CancelPendingUnlock()
    {
        lock (Lock_) { VaultCrypto.Clear(_pendingDek); _pendingDek = null; }
    }

    // ---------------- lock ----------------

    /// <summary>Forgets the data key. While something holds the key (a live recording), it is forgotten when that ends.</summary>
    public static void Lock()
    {
        bool changed;
        lock (Lock_)
        {
            CancelPendingUnlock();
            if (_dek == null) return;
            if (_holds > 0) { _lockRequested = true; changed = false; }
            else { VaultCrypto.Clear(_dek); _dek = null; changed = true; }
        }
        if (changed) StateChanged?.Invoke(VaultState.Locked);
    }

    /// <summary>Keeps the key in memory while a live recording runs, even if the app is locked meanwhile.</summary>
    public static IDisposable Hold()
    {
        lock (Lock_) _holds++;
        return new Holder();
    }

    private sealed class Holder : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            bool lockNow;
            lock (Lock_) { _holds--; lockNow = _holds == 0 && _lockRequested; }
            if (lockNow) Lock();
        }
    }

    /// <summary>True when the UI is locked but the key is kept for a running recording.</summary>
    public static bool LockPending { get { lock (Lock_) return _lockRequested; } }

    // ---------------- keys for other components ----------------

    /// <summary>The data key, for the file format. Throws when locked. Callers never keep it.</summary>
    internal static byte[] DataKey()
    {
        lock (Lock_) return _dek ?? throw new VaultLockedException();
    }

    /// <summary>A separate key for one purpose ("secrets", "settings"…), derived from the data key. Caller clears it.</summary>
    public static byte[] DeriveKey(string purpose)
    {
        lock (Lock_) return SubKey(_dek ?? throw new VaultLockedException(), purpose);
    }

    private static byte[] SubKey(byte[] dek, string purpose) => VaultCrypto.Hkdf(dek, Array.Empty<byte>(), "WhisperDesk subkey|" + purpose);

    // ---------------- changes while unlocked ----------------

    public static void ChangePassphrase(string passphrase)
    {
        lock (Lock_)
        {
            RequireUnlocked();
            if (passphrase.Length < _file!.Policy.MinPassphraseLength) throw new ArgumentException($"The passphrase must be at least {_file.Policy.MinPassphraseLength} characters.");
            if (_file.Slots.Any(s => s.Type == "passphrase+hello")) throw new InvalidOperationException("Turn off “passphrase and Windows Hello” first, or change it there.");
            _file.Slots.RemoveAll(s => s.Type == "passphrase");
            _file.Slots.Insert(0, PassphraseSlot(_file.Id, _dek!, passphrase, null));
            SignPolicyLocked();
            SaveLocked();
        }
    }

    /// <summary>
    /// After opening with the recovery key: replaces every passphrase and Windows Hello slot with one new passphrase slot,
    /// in one step. (Hello has to be set up again: the person recovering may not have this PC's Hello.)
    /// </summary>
    public static void ResetPassphraseAfterRecovery(string passphrase)
    {
        string id;
        lock (Lock_)
        {
            RequireUnlocked();
            if (passphrase.Length < _file!.Policy.MinPassphraseLength) throw new ArgumentException($"The passphrase must be at least {_file.Policy.MinPassphraseLength} characters.");
            id = _file.Id;
            _file.Slots.RemoveAll(s => s.Type is "passphrase" or "passphrase+hello" or "hello");
            _file.Slots.Insert(0, PassphraseSlot(_file.Id, _dek!, passphrase, null));
            SignPolicyLocked();
            SaveLocked();
        }
        _ = WindowsHello.RemoveAsync(id);
    }

    /// <summary>Replaces the recovery key (the old one stops working). Returns the new one to show once.</summary>
    public static string NewRecoveryKey()
    {
        lock (Lock_)
        {
            RequireUnlocked();
            _file!.Slots.RemoveAll(s => s.Type == "recovery");
            var (slot, display) = RecoverySlot(_file.Id, _dek!);
            _file.Slots.Add(slot);
            SignPolicyLocked();
            SaveLocked();
            return display;
        }
    }

    public static (byte[] Secret, string Uri) NewTotpEnrollment(string account)
    {
        var secret = Totp.NewSecret();
        return (secret, Totp.ProvisioningUri(secret, account));
    }

    /// <summary>Turns 2FA on after the user proved they can generate codes for <paramref name="secret"/>.</summary>
    public static bool EnableTotp(byte[] secret, string code)
    {
        lock (Lock_)
        {
            RequireUnlocked();
            var step = Totp.Verify(secret, code, 0, DateTimeOffset.UtcNow);
            if (step == null) return false;
            var key = SubKey(_dek!, "totp");
            try { _file!.Totp = SealedBlob.Seal(key, secret, "WhisperDesk totp v1|" + _file.Id); }
            finally { VaultCrypto.Clear(key); }
            _file.TotpLastStep = step.Value;
            SignPolicyLocked();
            SaveLocked();
            return true;
        }
    }

    public static void DisableTotp()
    {
        lock (Lock_)
        {
            RequireUnlocked();
            _file!.Totp = null;
            _file.TotpLastStep = 0;
            SignPolicyLocked();
            SaveLocked();
        }
    }

    /// <summary>
    /// Sets up Windows Hello. <paramref name="mode"/> "alternative": Hello alone also unlocks. "required": the passphrase
    /// slot is replaced by one that needs the passphrase and Hello together (the recovery key still works alone).
    /// </summary>
    public static async Task EnrollHelloAsync(string mode, string passphrase)
    {
        VaultFile file;
        lock (Lock_) { RequireUnlocked(); file = _file!; }
        var challenge = RandomNumberGenerator.GetBytes(32);
        var secret = await WindowsHello.EnrollAsync(file.Id, challenge);
        try
        {
            lock (Lock_)
            {
                RequireUnlocked();
                _file!.Slots.RemoveAll(s => s.Type is "hello");
                if (mode == "required")
                {
                    var check = _file.Slots.FirstOrDefault(s => s.Type == "passphrase");
                    if (check != null)
                    {
                        // Prove the passphrase before binding it to Hello.
                        var salt = Convert.FromBase64String(check.Salt);
                        var pb = VaultCrypto.Pbkdf2(passphrase, salt, check.Iterations);
                        var kek = VaultCrypto.Hkdf(pb, salt, "WhisperDesk passphrase slot");
                        try { VaultCrypto.Clear(check.Key.Open(kek, SlotAad(_file.Id, "passphrase"))); }
                        catch (CryptographicException) { throw new ArgumentException("That passphrase is not right."); }
                        finally { VaultCrypto.Clear(pb); VaultCrypto.Clear(kek); }
                    }
                    _file.Slots.RemoveAll(s => s.Type is "passphrase" or "passphrase+hello");
                    var slot = PassphraseSlot(_file.Id, _dek!, passphrase, secret);
                    slot.Challenge = Convert.ToBase64String(challenge);
                    _file.Slots.Insert(0, slot);
                }
                else
                {
                    var salt = RandomNumberGenerator.GetBytes(VaultCrypto.SaltSize);
                    var kek = VaultCrypto.Hkdf(secret, salt, "WhisperDesk hello slot");
                    try
                    {
                        _file.Slots.Add(new KeySlot { Type = "hello", Kdf = "WindowsHello-RSA-SHA256+HKDF", Salt = Convert.ToBase64String(salt),
                                                      Challenge = Convert.ToBase64String(challenge), Key = SealedBlob.Seal(kek, _dek!, SlotAad(_file.Id, "hello")) });
                    }
                    finally { VaultCrypto.Clear(kek); }
                }
                SignPolicyLocked();
                SaveLocked();
            }
        }
        finally { VaultCrypto.Clear(secret); }
    }

    /// <summary>Removes Windows Hello. In "required" mode the passphrase becomes enough again (needs the passphrase).</summary>
    public static async Task RemoveHelloAsync(string passphrase)
    {
        string id;
        lock (Lock_)
        {
            RequireUnlocked();
            id = _file!.Id;
            if (_file.Slots.Any(s => s.Type == "passphrase+hello"))
            {
                if (passphrase.Length < _file.Policy.MinPassphraseLength) throw new ArgumentException("Enter the passphrase to keep using it on its own.");
                _file.Slots.RemoveAll(s => s.Type == "passphrase+hello");
                _file.Slots.Insert(0, PassphraseSlot(_file.Id, _dek!, passphrase, null));
            }
            _file.Slots.RemoveAll(s => s.Type == "hello");
            SignPolicyLocked();
            SaveLocked();
        }
        await WindowsHello.RemoveAsync(id);
    }

    public static void SetPolicy(VaultPolicy policy)
    {
        lock (Lock_)
        {
            RequireUnlocked();
            _file!.Policy = policy.Copy();
            SignPolicyLocked();
            SaveLocked();
        }
    }

    /// <summary>Deletes the vault header (after <see cref="VaultMigration"/> decrypted everything) and forgets the key.</summary>
    internal static void Destroy()
    {
        string? id;
        lock (Lock_)
        {
            RequireUnlocked();
            id = _file?.Id;
            _file = null;
            VaultCrypto.Clear(_dek);
            _dek = null;
            try { File.Delete(FilePath); } catch { }
            try { File.Delete(Path.Combine(RecordingLibrary.Root, LibraryCopyName)); } catch { }
        }
        if (id != null) _ = WindowsHello.RemoveAsync(id);
        StateChanged?.Invoke(VaultState.Disabled);
    }

    private static void RequireUnlocked()
    {
        if (_file == null || _dek == null) throw new VaultLockedException();
    }

    // ---------------- persistence ----------------

    /// <summary>
    /// What the MAC covers: the lock policy and whether 2FA and Windows Hello are set up, so switching them off by editing
    /// vault.json (instead of in the app) is detected at the next unlock.
    /// </summary>
    private static string PolicyJson(VaultPolicy p) =>
        JsonSerializer.Serialize(p) + "|totp=" + (_file?.Totp != null) + "|hello=" + string.Join(",", _file?.Slots.Select(s => s.Type).OrderBy(x => x) ?? Enumerable.Empty<string>());

    private static bool PolicyMacMatches()
    {
        if (_file?.PolicyMac == null || _dek == null) return false;
        var key = SubKey(_dek, "policy");
        try { return VaultCrypto.Equal(HMACSHA256.HashData(key, VaultCrypto.Utf8(PolicyJson(_file.Policy))), Convert.FromBase64String(_file.PolicyMac)); }
        catch { return false; }
        finally { VaultCrypto.Clear(key); }
    }

    private static void SignPolicyLocked()
    {
        var key = SubKey(_dek!, "policy");
        try { _file!.PolicyMac = Convert.ToBase64String(HMACSHA256.HashData(key, VaultCrypto.Utf8(PolicyJson(_file.Policy)))); }
        finally { VaultCrypto.Clear(key); }
    }

    private static void SaveLocked()
    {
        if (_file == null) return;
        var json = JsonSerializer.Serialize(_file, Json);
        Directory.CreateDirectory(LocalData);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
        // A copy travels with the library, so a moved or copied library can still be opened (it holds only wrapped keys).
        try { File.WriteAllText(Path.Combine(RecordingLibrary.Root, LibraryCopyName), json); } catch { }
    }

    /// <summary>The vault header as JSON (for backups). Contains only salts, parameters and wrapped keys.</summary>
    public static string? ExportHeader()
    {
        lock (Lock_) return _file == null ? null : JsonSerializer.Serialize(_file, Json);
    }

    /// <summary>A vault header found next to a library (or in a backup) whose ID differs from ours, or null.</summary>
    public static VaultFile? ReadHeader(string path)
    {
        try
        {
            var f = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(path));
            return f is { Format: "whisperdesk-vault", Version: 1 } && f.Slots.Count > 0 ? f : null;
        }
        catch { return null; }
    }

    /// <summary>Adopts a vault header (a library copied from another PC, or restored from a backup) when none is set up here.</summary>
    public static void Adopt(string path)
    {
        if (IsEnabled) throw new InvalidOperationException("Encryption is already set up on this PC.");
        var f = ReadHeader(path) ?? throw new InvalidDataException("That is not a WhisperDesk vault file.");
        Directory.CreateDirectory(LocalData);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(f, Json));
        Reload();
        StateChanged?.Invoke(State);
    }

    /// <summary>
    /// "I can't unlock": the encryption header is renamed (not deleted) so WhisperDesk can start without it. The encrypted
    /// data stays on disk, still encrypted; renaming the header back (or restoring it) makes it openable again with the
    /// passphrase or recovery key. Returns the new name of the header.
    /// </summary>
    public static string SetAside()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var aside = FilePath + ".set-aside-" + stamp;
        lock (Lock_)
        {
            VaultCrypto.Clear(_dek); _dek = null;
            VaultCrypto.Clear(_pendingDek); _pendingDek = null;
            if (File.Exists(FilePath)) File.Move(FilePath, aside);
            var copy = Path.Combine(RecordingLibrary.Root, LibraryCopyName);
            try { if (File.Exists(copy)) File.Move(copy, copy + ".set-aside-" + stamp); } catch { }
            _file = null;
        }
        StateChanged?.Invoke(VaultState.Disabled);
        return aside;
    }

    /// <summary>Tests only: forgets keys and state so the next test starts clean (files are removed by the test).</summary>
    internal static void ResetForTests()
    {
        lock (Lock_)
        {
            VaultCrypto.Clear(_dek); _dek = null;
            VaultCrypto.Clear(_pendingDek); _pendingDek = null;
            _holds = 0; _lockRequested = false; PolicyWasTampered = false;
        }
        Reload();
    }

    public static string Format(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{Math.Ceiling(t.TotalSeconds)} s";
}
