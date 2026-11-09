<#
    Signs WhisperDesk's update information with the two release keys, the same scheme as DarkCipher ServerManagement:

      update.json.sig      base64 ECDSA P-256 / SHA-256 signature (IEEE P1363, r||s) over the exact bytes of update.json
      update.json.pq.sig   base64 ML-DSA-65 (FIPS 204, post-quantum) signature over the same bytes

    The app pins both public keys (WhisperDesk\Services\Updater.cs: ReleaseKeyX / ReleaseKeyY and ReleasePqKey) and
    offers an update only when BOTH signatures verify, so GitHub only carries the files and a hijacked account or
    server can't make WhisperDesk install anything.

    The private keys are two small files protected by Windows (DPAPI) for this Windows user, in
    %LOCALAPPDATA%\DarkCipher\keys. They are never in the repository, never in a build and never a plain file.
    NEVER replace them: every installed copy trusts only these keys. Escrow them once, with a password, and keep the
    escrow file somewhere safe and offline (a USB stick in a drawer, not cloud storage):

      .\tools\sign-update.ps1 -Escrow <file>          write a password-encrypted copy of both keys
      .\tools\sign-update.ps1 -RestoreEscrow <file>   put them back on a new PC or Windows account

    Usage:
      .\tools\sign-update.ps1 -NewKeys                create both keys (refuses if either exists) and print the pins
      .\tools\sign-update.ps1 -ShowPublicKey          print the public keys to pin in Updater.cs
      .\tools\sign-update.ps1 -Path <update.json>     write <update.json>.sig and .pq.sig and verify them
      .\tools\sign-update.ps1 -Verify <update.json>   verify only, against the keys pinned in Updater.cs (no private key)

    Runs in Windows PowerShell 5.1 (as build.ps1 and release.ps1 do) and PowerShell 7. The ML-DSA maths is BouncyCastle 2.7.0,
    the package the app itself pins, loaded from the NuGet cache (any build restores it).
#>
param([switch]$NewKeys, [switch]$ShowPublicKey, [string]$Path, [string]$Verify, [string]$PinnedFrom, [string]$Escrow, [string]$RestoreEscrow)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Core
Add-Type -AssemblyName System.Security

$KeyDir = Join-Path $env:LOCALAPPDATA 'DarkCipher\keys'
$EcKeyFile = Join-Path $KeyDir 'DarkCipherWhisperDesk-UpdateSigning-ECDSAP256.dpapi'
$PqKeyFile = Join-Path $KeyDir 'DarkCipherWhisperDesk-UpdateSigning-MLDSA65.dpapi'
$EcEntropy = [Text.Encoding]::ASCII.GetBytes('DarkCipherWhisperDesk-UpdateSigning-ECDSAP256')
$PqEntropy = [Text.Encoding]::ASCII.GetBytes('DarkCipherWhisperDesk-UpdateSigning-MLDSA65')
$UpdaterCs = if ($PinnedFrom) { $PinnedFrom } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'Services\Updater.cs' }
$Scope = [System.Security.Cryptography.DataProtectionScope]::CurrentUser

# A shell started by a packaged (MSIX) app runs in that package's context: files it writes under %LOCALAPPDATA% are
# silently redirected to %LOCALAPPDATA%\Packages\<app>\LocalCache\Local\ and vanish when that app is reset or removed.
# Release keys must never land there, so creating or restoring them refuses in package context; every mode warns.
if (-not ('WdSignPkg' -as [type])) {
    Add-Type -Namespace '' -Name WdSignPkg -MemberDefinition '[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder name);'
}
function PackageContext {
    try { $n = 0; return ([WdSignPkg]::GetCurrentPackageFullName([ref]$n, $null) -ne 15700) } catch { return $false }   # 15700 = APPMODEL_ERROR_NO_PACKAGE
}
if (PackageContext) {
    if ($NewKeys -or $RestoreEscrow) { throw "This PowerShell runs inside a packaged app's context (for example a terminal started by the Claude desktop app), so the keys would be written to that app's private, virtualised copy of $KeyDir and lost when it is reset or removed. Run this from a normal PowerShell window." }
    Write-Warning "This PowerShell runs inside a packaged app's context: $KeyDir may be that app's virtualised copy, not the real folder. Use a normal PowerShell window for anything that matters."
} else {
    $virt = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'LocalCache\Local\DarkCipher\keys\DarkCipherWhisperDesk-UpdateSigning-ECDSAP256.dpapi' } | Where-Object { Test-Path $_ }
    if ($virt -and -not (Test-Path $EcKeyFile)) { Write-Warning "WhisperDesk release keys exist only in a packaged app's virtualised storage ($(@($virt)[0] | Split-Path)), not in $KeyDir. Copy them (byte for byte) into $KeyDir and escrow them." }
}

function Wipe([byte[]]$b) { if ($null -ne $b) { [Array]::Clear($b, 0, $b.Length) } }
function ToHex([byte[]]$b) { return (($b | ForEach-Object { $_.ToString('X2') }) -join '') }
function FromHex([string]$h) { $b = New-Object byte[] ($h.Length / 2); for ($i = 0; $i -lt $b.Length; $i++) { $b[$i] = [Convert]::ToByte($h.Substring(2 * $i, 2), 16) }; return ,$b }
function Utf8File([string]$file, [string]$text) { [IO.File]::WriteAllText($file, $text, (New-Object Text.UTF8Encoding $false)) }

function LoadBc {
    if ('Org.BouncyCastle.Crypto.Parameters.MLDsaParameters' -as [type]) { return }
    $dll = Join-Path $env:USERPROFILE '.nuget\packages\bouncycastle.cryptography\2.7.0\lib\net461\BouncyCastle.Cryptography.dll'
    if (-not (Test-Path $dll)) { throw "BouncyCastle 2.7.0 isn't in the NuGet cache: build WhisperDesk (or run dotnet restore) first." }
    Add-Type -Path $dll
}
function PqParams { return [Org.BouncyCastle.Crypto.Parameters.MLDsaParameters]::ml_dsa_65 }

# ---- the ECDSA key: a BCRYPT_ECCPRIVATE_BLOB (magic, cbKey, X, Y, d) -------------------------------------------------
function EcPublic([byte[]]$blob) {
    $len = [BitConverter]::ToInt32($blob, 4)
    return @((ToHex $blob[8..(8 + $len - 1)]), (ToHex $blob[(8 + $len)..(8 + 2 * $len - 1)]))
}
function EcBlob {
    if (-not (Test-Path $EcKeyFile)) { return $null }
    $b = [System.Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($EcKeyFile), $EcEntropy, $Scope)
    return ,$b
}
function EcVerifyWith([string]$file, [string]$x, [string]$y) {
    $sigFile = "$file.sig"
    if (-not (Test-Path $sigFile)) { return "there is no $sigFile" }
    $sig = [Convert]::FromBase64String(([IO.File]::ReadAllText($sigFile)).Trim())
    $blob = New-Object byte[] (8 + 64)
    [BitConverter]::GetBytes([int]0x31534345).CopyTo($blob, 0)   # BCRYPT_ECDSA_PUBLIC_P256_MAGIC "ECS1"
    [BitConverter]::GetBytes([int]32).CopyTo($blob, 4)
    (FromHex $x).CopyTo($blob, 8); (FromHex $y).CopyTo($blob, 40)
    $pub = [System.Security.Cryptography.CngKey]::Import($blob, [System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    $ec = New-Object System.Security.Cryptography.ECDsaCng $pub
    $ec.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
    try { if ($ec.VerifyData([IO.File]::ReadAllBytes($file), $sig)) { return $null } } finally { $ec.Dispose(); $pub.Dispose() }
    return "the signature on $file does not verify against the pinned ECDSA release key"
}

# ---- the ML-DSA-65 key: its 32-byte seed ------------------------------------------------------------------------------
function PqSeed {
    if (-not (Test-Path $PqKeyFile)) { return $null }
    $b = [System.Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($PqKeyFile), $PqEntropy, $Scope)
    return ,$b
}
function PqPublicB64([byte[]]$seed) {
    return [Convert]::ToBase64String([Org.BouncyCastle.Crypto.Parameters.MLDsaPrivateKeyParameters]::FromSeed((PqParams), $seed).GetPublicKey().GetEncoded())
}
function PqVerifyWith([string]$file, [string]$pubB64) {
    $sigFile = "$file.pq.sig"
    if (-not (Test-Path $sigFile)) { return "there is no $sigFile" }
    $sig = [Convert]::FromBase64String(([IO.File]::ReadAllText($sigFile)).Trim())
    $data = [IO.File]::ReadAllBytes($file)
    $pub = [Org.BouncyCastle.Crypto.Parameters.MLDsaPublicKeyParameters]::FromEncoding((PqParams), [Convert]::FromBase64String($pubB64))
    $s = New-Object Org.BouncyCastle.Crypto.Signers.MLDsaSigner((PqParams), $false)
    $s.Init($false, $pub)
    $s.BlockUpdate($data, 0, $data.Length)
    if ($s.VerifySignature($sig)) { return $null }
    return "the post-quantum signature on $file does not verify against the pinned ML-DSA release key"
}

# ---- what Updater.cs pins -----------------------------------------------------------------------------------------
function Pinned {
    $src = [IO.File]::ReadAllText($UpdaterCs)
    $mx = [regex]::Match($src, 'ReleaseKeyX\s*=\s*"([0-9A-Fa-f]{64})"'); $my = [regex]::Match($src, 'ReleaseKeyY\s*=\s*"([0-9A-Fa-f]{64})"')
    $mq = [regex]::Match($src, 'ReleasePqKey\s*=\s*"([A-Za-z0-9+/=]{2000,})"')
    if (-not ($mx.Success -and $my.Success -and $mq.Success)) { throw "$UpdaterCs doesn't pin both release keys (ReleaseKeyX / ReleaseKeyY and ReleasePqKey)." }
    return @($mx.Groups[1].Value, $my.Groups[1].Value, $mq.Groups[1].Value)
}
function PrintPins([byte[]]$ecBlob, [byte[]]$seed) {
    $xy = EcPublic $ecBlob
    Write-Host "Pin in Services\Updater.cs:"
    Write-Host "  ReleaseKeyX = `"$($xy[0])`""
    Write-Host "  ReleaseKeyY = `"$($xy[1])`""
    Write-Host "  ReleasePqKey = `"$(PqPublicB64 $seed)`""
}
function SaveKeys([byte[]]$ecBlob, [byte[]]$seed) {
    New-Item -ItemType Directory -Force $KeyDir | Out-Null
    [IO.File]::WriteAllBytes($EcKeyFile, [System.Security.Cryptography.ProtectedData]::Protect($ecBlob, $EcEntropy, $Scope))
    [IO.File]::WriteAllBytes($PqKeyFile, [System.Security.Cryptography.ProtectedData]::Protect($seed, $PqEntropy, $Scope))
}

# ---- escrow: both keys under one password (PBKDF2-SHA256 -> AES-256-CBC, then HMAC-SHA256 over everything) ----------
$EscrowFormat = 'whisperdesk-update-keys-escrow-v1'
$EscrowIterations = 600000
function EscrowKeys([string]$password, [byte[]]$salt) {
    $kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes($password, $salt, $EscrowIterations, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try { $k = $kdf.GetBytes(64) } finally { $kdf.Dispose() }
    $enc = New-Object byte[] 32; $mac = New-Object byte[] 32
    [Array]::Copy($k, 0, $enc, 0, 32); [Array]::Copy($k, 32, $mac, 0, 32); Wipe $k
    return @($enc, $mac)
}
function ReadPassword([string]$prompt) {
    $s = Read-Host -AsSecureString $prompt
    $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }
}
function MacOf([byte[]]$macKey, [byte[]]$salt, [byte[]]$iv, [byte[]]$ct) {
    $h = New-Object System.Security.Cryptography.HMACSHA256 (, $macKey)
    try {
        $hdr = [Text.Encoding]::ASCII.GetBytes("$EscrowFormat|$EscrowIterations|")
        $all = New-Object byte[] ($hdr.Length + $salt.Length + $iv.Length + $ct.Length)
        $hdr.CopyTo($all, 0); $salt.CopyTo($all, $hdr.Length); $iv.CopyTo($all, $hdr.Length + $salt.Length); $ct.CopyTo($all, $hdr.Length + $salt.Length + $iv.Length)
        return ,$h.ComputeHash($all)
    } finally { $h.Dispose() }
}

if ($NewKeys) {
    LoadBc
    if ((Test-Path $EcKeyFile) -or (Test-Path $PqKeyFile)) { throw "A WhisperDesk release key already exists in $KeyDir; release keys are never replaced (every installed copy trusts only them)." }
    $p = New-Object System.Security.Cryptography.CngKeyCreationParameters
    $p.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport   # ephemeral, exported once into the DPAPI file
    $p.KeyUsage = [System.Security.Cryptography.CngKeyUsages]::Signing
    # [NullString]::Value, not $null: Windows PowerShell passes $null to a string parameter as "", which would make (or
    # find) a key NAMED "" that stays in the key store, instead of an ephemeral one.
    $key = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, [NullString]::Value, $p)
    $ecBlob = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob); $key.Dispose()
    $gen = New-Object Org.BouncyCastle.Crypto.Generators.MLDsaKeyPairGenerator
    $gen.Init((New-Object Org.BouncyCastle.Crypto.Parameters.MLDsaKeyGenerationParameters((New-Object Org.BouncyCastle.Security.SecureRandom), (PqParams))))
    $seed = $gen.GenerateKeyPair().Private.GetSeed()
    try {
        SaveKeys $ecBlob $seed
        Write-Host "Created the WhisperDesk release keys (ECDSA P-256 and ML-DSA-65), protected by Windows for this user, in $KeyDir"
        PrintPins $ecBlob $seed
        Write-Host "`nNow escrow them once:  .\tools\sign-update.ps1 -Escrow <file on removable media>"
    } finally { Wipe $ecBlob; Wipe $seed }
    return
}

if ($ShowPublicKey) {
    LoadBc
    $ecBlob = EcBlob; $seed = PqSeed
    if ($null -eq $ecBlob -or $null -eq $seed) { throw "This Windows account has no WhisperDesk release keys (-NewKeys creates them once; -RestoreEscrow puts an escrowed copy back)." }
    try { PrintPins $ecBlob $seed } finally { Wipe $ecBlob; Wipe $seed }
    return
}

if ($Escrow) {
    LoadBc
    if (Test-Path $Escrow) { throw "$Escrow already exists; choose a new file." }
    $ecBlob = EcBlob; $seed = PqSeed
    if ($null -eq $ecBlob -or $null -eq $seed) { throw "This Windows account has no WhisperDesk release keys to escrow." }
    $pw = ReadPassword 'Escrow password (at least 16 characters; keep it apart from the file)'
    if ($pw.Length -lt 16) { throw 'The escrow password must be at least 16 characters.' }
    if ((ReadPassword 'The same password again') -ne $pw) { throw "The two passwords don't match." }
    $plain = New-Object byte[] ($ecBlob.Length + $seed.Length)
    $ecBlob.CopyTo($plain, 0); $seed.CopyTo($plain, $ecBlob.Length)
    $salt = New-Object byte[] 32; $iv = New-Object byte[] 16
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($salt); $rng.GetBytes($iv); $rng.Dispose()
    $keys = EscrowKeys $pw $salt; $pw = $null
    try {
        $aes = [System.Security.Cryptography.Aes]::Create(); $aes.Key = $keys[0]; $aes.IV = $iv
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC; $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $ct = $aes.CreateEncryptor().TransformFinalBlock($plain, 0, $plain.Length); $aes.Dispose()
        $mac = MacOf $keys[1] $salt $iv $ct
        $xy = EcPublic $ecBlob
        $doc = [ordered]@{
            format = $EscrowFormat; kdf = 'PBKDF2-HMAC-SHA256'; iterations = $EscrowIterations; cipher = 'AES-256-CBC + HMAC-SHA256'
            salt = [Convert]::ToBase64String($salt); iv = [Convert]::ToBase64String($iv); ciphertext = [Convert]::ToBase64String($ct); mac = [Convert]::ToBase64String($mac)
            ecdsaPublic = "$($xy[0])$($xy[1])"
        }
        Utf8File $Escrow (($doc | ConvertTo-Json) + "`n")
        Write-Host "Escrowed both WhisperDesk release keys to $Escrow (password-encrypted). Keep it offline, apart from the password."
    } finally { Wipe $plain; Wipe $ecBlob; Wipe $seed; Wipe $keys[0]; Wipe $keys[1] }
    return
}

if ($RestoreEscrow) {
    LoadBc
    if ((Test-Path $EcKeyFile) -or (Test-Path $PqKeyFile)) { throw "This Windows account already has WhisperDesk release keys in $KeyDir; nothing was changed." }
    $doc = Get-Content -Raw $RestoreEscrow | ConvertFrom-Json
    if ($doc.format -ne $EscrowFormat -or [int]$doc.iterations -ne $EscrowIterations) { throw "$RestoreEscrow isn't a WhisperDesk key escrow file." }
    $salt = [Convert]::FromBase64String($doc.salt); $iv = [Convert]::FromBase64String($doc.iv); $ct = [Convert]::FromBase64String($doc.ciphertext)
    $keys = EscrowKeys (ReadPassword 'Escrow password') $salt
    try {
        $mac = MacOf $keys[1] $salt $iv $ct
        if ([Convert]::ToBase64String($mac) -ne $doc.mac) { throw 'Wrong password, or the escrow file has been changed; nothing was restored.' }
        $aes = [System.Security.Cryptography.Aes]::Create(); $aes.Key = $keys[0]; $aes.IV = $iv
        $plain = $aes.CreateDecryptor().TransformFinalBlock($ct, 0, $ct.Length); $aes.Dispose()
        $ecBlob = New-Object byte[] ($plain.Length - 32); $seed = New-Object byte[] 32
        [Array]::Copy($plain, 0, $ecBlob, 0, $ecBlob.Length); [Array]::Copy($plain, $ecBlob.Length, $seed, 0, 32)
        $pins = Pinned; $xy = EcPublic $ecBlob
        if ($xy[0] -ne $pins[0] -or $xy[1] -ne $pins[1] -or (PqPublicB64 $seed) -ne $pins[2]) { throw "The escrowed keys aren't the ones pinned in $UpdaterCs; nothing was restored." }
        SaveKeys $ecBlob $seed
        Write-Host "Restored both WhisperDesk release keys for this Windows user, in $KeyDir. They match the keys pinned in Updater.cs."
    } finally { Wipe $plain; Wipe $ecBlob; Wipe $seed; Wipe $keys[0]; Wipe $keys[1] }
    return
}

if ($Verify) {
    LoadBc
    $pins = Pinned
    $err = EcVerifyWith $Verify $pins[0] $pins[1]
    if ($err) { throw $err }
    $err = PqVerifyWith $Verify $pins[2]
    if ($err) { throw $err }
    Write-Host "Verified: $Verify.sig and $Verify.pq.sig match the pinned WhisperDesk release keys (ECDSA P-256 and ML-DSA-65)."
    return
}

if ($Path) {
    LoadBc
    $pins = Pinned
    $ecBlob = EcBlob; $seed = PqSeed
    if ($null -eq $ecBlob -or $null -eq $seed) { throw "This Windows account has no WhisperDesk release keys, so the update information can't be signed (-NewKeys once, or -RestoreEscrow)." }
    try {
        $xy = EcPublic $ecBlob
        if ($xy[0] -ne $pins[0] -or $xy[1] -ne $pins[1]) { throw "This account's ECDSA release key isn't the one pinned in Updater.cs; refusing to sign." }
        if ((PqPublicB64 $seed) -ne $pins[2]) { throw "This account's post-quantum release key isn't the one pinned in Updater.cs; refusing to sign." }
        $data = [IO.File]::ReadAllBytes($Path)

        $key = [System.Security.Cryptography.CngKey]::Import($ecBlob, [System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
        $ec = New-Object System.Security.Cryptography.ECDsaCng $key
        $ec.HashAlgorithm = [System.Security.Cryptography.CngAlgorithm]::Sha256
        try { $sig = $ec.SignData($data) } finally { $ec.Dispose(); $key.Dispose() }
        Utf8File "$Path.sig" ([Convert]::ToBase64String($sig) + "`n")

        $s = New-Object Org.BouncyCastle.Crypto.Signers.MLDsaSigner((PqParams), $false)
        $priv = [Org.BouncyCastle.Crypto.Parameters.MLDsaPrivateKeyParameters]::FromSeed((PqParams), $seed)
        $s.Init($true, (New-Object Org.BouncyCastle.Crypto.Parameters.ParametersWithRandom($priv, (New-Object Org.BouncyCastle.Security.SecureRandom))))
        $s.BlockUpdate($data, 0, $data.Length)
        Utf8File "$Path.pq.sig" ([Convert]::ToBase64String($s.GenerateSignature()) + "`n")
    } finally { Wipe $ecBlob; Wipe $seed }
    $err = EcVerifyWith $Path $pins[0] $pins[1]
    if ($err) { throw $err }
    $err = PqVerifyWith $Path $pins[2]
    if ($err) { throw $err }
    Write-Host "Signed: $Path.sig and $Path.pq.sig (verified against the pinned WhisperDesk release keys, ECDSA P-256 and ML-DSA-65)."
    return
}

Write-Host 'Nothing to do: -NewKeys, -ShowPublicKey, -Path <update.json>, -Verify <update.json>, -Escrow <file> or -RestoreEscrow <file>.'
