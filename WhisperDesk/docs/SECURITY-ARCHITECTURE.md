# WhisperDesk security & privacy architecture

Status: part of WhisperDesk 1.7.0 (which also first ships the 1.6.0 NPU work).
See §5 for what was built, how it was tested, and the second review. This document is the reference for what WhisperDesk
protects, how, and where its protection ends. It must stay honest: no "100% secure", "unhackable" or "zero risk"
claims here, in the UI, the guide or marketing.

**Guiding rule: nothing leaves the device unless the user deliberately enabled or requested the feature that needs it.**

---

## 1. Audit of the existing application (1.6.0)

### 1.1 What the application is

A .NET 8 WPF desktop app (single-file, self-contained, x64). Data lives in ordinary files:

| Data | Location | Format |
|---|---|---|
| Recordings library | `%LOCALAPPDATA%\WhisperDesk\Library` (or user/installer choice) | One folder per recording: `metadata.json`, `transcript.json`, `transcript.txt`, `transcript.srt`, media, `forensics-log.jsonl`, `previous\` |
| Voice library (biometric) | `%LOCALAPPDATA%\WhisperDesk\voices.json` | JSON voiceprints + names |
| Settings | `%LOCALAPPDATA%\WhisperDesk\settings.json` | JSON (paths, prompts, add-on options incl. webhook URL) |
| Live recording / crash recovery | `%LOCALAPPDATA%\WhisperDesk\live\` | WAV + recovery folders |
| Forensic logs for non-library media | `%LOCALAPPDATA%\WhisperDesk\forensics\` | JSONL hash chain |
| Error logs | `%LOCALAPPDATA%\WhisperDesk\logs\` | Text, full exception text |
| WebView2 profile | `%LOCALAPPDATA%\WhisperDesk\webview\` | Chromium profile |
| Temporary files | `%TEMP%\WhisperDesk\share`, `%TEMP%\wdsession-open-*`, `wdrestore-*`, burn work dirs | Plaintext copies |
| Models | `%LOCALAPPDATA%\WhisperDesk\models` | GGML / ONNX |
| Third-party add-ons | `%LOCALAPPDATA%\WhisperDesk\addons\*.dll` | .NET assemblies |

### 1.2 Network use found

| Component | Destination | Trigger | Integrity | Finding |
|---|---|---|---|---|
| Whisper GGML models | huggingface.co (`ggerganov/whisper.cpp`, branch `main`) | First transcription with a model | **None** | Unpinned branch, no hash check |
| Speaker / VAD models | huggingface.co, github.com | First speaker analysis / live session | **None** | Unpinned, no hash check |
| NPU ONNX models | huggingface.co (pinned revisions) | First NPU use | SHA-256 pinned | Good |
| Ollama summaries | `AiUrl` (default localhost) | Opt-in setting | n/a | A non-local URL silently sends full transcripts; plain HTTP allowed |
| Webhook add-on | user URL | Opt-in add-on | n/a | Plain HTTP to remote hosts allowed; URL (often containing a token) stored in settings.json |
| Caption server add-on | listens on localhost | Opt-in add-on | n/a | **`Access-Control-Allow-Origin: *`: any web page open in the user's browser can read the live transcript** |
| WebView2 (guide, privacy page, analysis preview) | Edge runtime | Window open | n/a | Runtime may do background networking; `NewWindowRequested` shell-executes any URI scheme |
| Installer | winget | **FFmpeg install ticked by default** | winget | Opt-out network use at install |
| ONNX Runtime | ETW telemetry provider | Process start | n/a | Official ORT builds emit telemetry events to Windows diagnostics unless disabled |

No telemetry, analytics, crash upload, update check or remote logging exists in WhisperDesk's own code.

### 1.3 Weaknesses found

1. **No encryption at rest.** Transcripts, audio, voiceprints and edit history are plaintext files.
2. **Third-party add-ons run in-process with full trust**, and *every* DLL in the add-ons folder is loaded and
   instantiated at start-up, even when disabled. Restoring a backup can drop DLLs there.
3. **Caption server CORS wildcard** (above) leaks live transcripts to any website.
4. **Unverified model downloads** (GGML, speaker models): a compromised mirror or TLS-intercepting proxy could
   supply a malicious file that is then parsed by native code.
5. **Directory junctions are followed** by library move, backup and uninstall clean-up enumeration; a junction
   inside the library could make "Move library" or the uninstaller delete files outside the library.
6. **Plaintext temporary files** are left behind: opened `.wdsession` files are extracted to `%TEMP%` and never
   deleted; share files live for a day.
7. **Sensitive logging**: error logs contain full exception text including file paths (user name, recording titles)
   and FFmpeg stderr.
8. **Secrets in plaintext config**: webhook URLs/tokens, add-on options.
9. **Restore trusts the backup**: restored `settings.json` can re-enable add-ons or point the AI URL at a remote host.
10. **FFmpeg found on PATH before installer/winget locations**: a writable PATH directory could plant `ffmpeg.exe`.
11. **Vulnerable transitive dependency**: `System.Security.Cryptography.Pkcs 8.0.1` (GHSA-846f-f764-76mx, high;
    GHSA-2fmv-rv2c-59x7, moderate) via PDFsharp.
12. **No dependency lock file, no vulnerability gate in the build, unsigned binaries, no published checksums.**
13. Smaller items: unvalidated port option used to build a browser URL (`7890@evil.example`), reserved Windows file
    names not handled in titles, JSON embedded in exported HTML only escapes `</`, no limits when opening
    `.wdsession` zips.

What is already good: backups can be AES-256-GCM encrypted with PBKDF2 (600k); NPU models pinned by SHA-256; zip-slip
checks on restore and session open; System.Text.Json only (no polymorphic deserialization); FFmpeg arguments use
`ArgumentList` (no shell); library default is non-synced; cloud-sync detection for the library already exists.

---

## 2. Threat model

Assets, most sensitive first: recordings and transcripts (possibly legal evidence), voiceprints (biometric),
speaker names and edit history, metadata (titles, folder names, dates), credentials for integrations, the
integrity of evidence (hash chains, edit logs).

| # | Threat | Realistic? | What WhisperDesk does | What it cannot do |
|---|---|---|---|---|
| T1 | **Stolen or lost device (powered off)** | Yes | With encryption on: library, voice library, recovery files and logs-of-content are AES-256-GCM ciphertext; keys derived from the passphrase (PBKDF2-SHA256 600k) and optionally bound to the TPM via Windows Hello. | A weak passphrase can be guessed offline. BitLocker is still recommended for the OS, swap and hibernation file. |
| T2 | **Stolen library folder / database copy** (USB copy, sync, backup leak) | Yes | Files are individually encrypted and authenticated; names of recordings and folders are hidden (opaque IDs). | File count, sizes and modification times remain visible. |
| T3 | **Compromised backup** | Yes | Backups of an encrypted library contain only ciphertext; password-encrypted backups add a second layer. Restore never trusts code or security settings from a backup. | A plain (unencrypted) backup of a non-encrypted library is plaintext by the user's choice. |
| T4 | **Malicious local process running as the same Windows user while WhisperDesk is unlocked** | Yes | Out of scope for full protection. Reduced exposure: keys only in memory while unlocked, cleared on lock; auto-lock; clipboard cleared on lock. | A process with the user's rights can read memory, log keystrokes, inject into the process or replace the executable. No user-mode app can defend against this. |
| T5 | Malicious local process while WhisperDesk is **locked or closed** | Yes | Encrypted data is unreadable without the passphrase/recovery key. | It can tamper with files (detected by authentication tags), delete them (not preventable), or wait for the next unlock (T4). |
| T6 | **Malicious files** (audio/video, `.wdsession`, backups, imported models) | Yes | Treated as untrusted: size limits, zip-slip checks, no code from data, FFmpeg run without a shell, models pinned by hash. | FFmpeg/whisper.cpp/ONNX Runtime parse untrusted input in native code; a memory-safety bug in them is outside WhisperDesk's control. Keep them updated. |
| T7 | **Malicious or compromised add-on** | Yes | Third-party add-ons run in a separate **AppContainer** process (Windows sandbox) with only the capabilities the user approved: no network, no file access outside its own data folder, no library access unless granted. Approval is bound to the DLL's SHA-256. | A granted capability can be abused within its scope (e.g. an add-on with network + transcript access can send transcripts). Built-in add-ons run in-process (first-party code). |
| T8 | **Compromised integration / remote service** | Yes | Remote services are opt-in, listed with what they receive, shown in network activity; HTTPS required for non-local hosts. | Data sent to a service the user enabled is under that service's control. |
| T9 | **Network attacker** (Wi-Fi, proxy) | Yes | HTTPS for all downloads with pinned SHA-256; no network at start-up; no plaintext HTTP to remote hosts. | Traffic analysis (that a model was downloaded) is visible. |
| T10 | **Credential theft** | Yes | Integration secrets in Windows Credential Manager (DPAPI), additionally wrapped with the vault key when encryption is on; never in settings, logs or exports. | Malware running as the user while unlocked (T4). |
| T11 | **Supply-chain compromise** (NuGet package, model host, build machine) | Yes | Lock file, vulnerability audit in the build, pinned model hashes, SHA-256 checksums for releases, optional Authenticode signing. | A malicious package version already pinned, or a compromised build machine, is not detectable by these controls. |
| T12 | **Library accidentally in a synced folder** | Yes (common: OneDrive Documents backup) | Detects OneDrive, Dropbox, Google Drive, iCloud Drive, Box (environment + folder names + sync-root registry); warns, never blocks; acknowledgment per path. | Detection is heuristic; unknown sync tools or network shares that sync elsewhere may not be detected. |
| T13 | **Accidental disclosure** (shoulder surfing, screenshots, exports, clipboard, logs) | Yes | Lock screen, auto-lock, lock on Windows lock/sleep, clipboard cleared on lock, redacted logs, explicit wording when exporting plaintext. | Exports are plaintext by design once the user saves them. |
| T14 | Forgotten passphrase / lost recovery key | Yes | Recovery key created at setup and confirmed by the user. | **If both are lost the data is unrecoverable.** There is deliberately no back door, email reset or vendor recovery. |
| T15 | Physical attacker with the device running and unlocked | Yes | Auto-lock shortens the window. | Cold-boot/DMA attacks on a running machine are out of scope. |
| T16 | Brute force on the lock screen | Yes | Increasing delays after failures, persisted across restarts. | Offline guessing against a copied vault file is bounded only by PBKDF2 cost and passphrase strength. |

---

## 3. Security architecture

### 3.1 Layering: core vs. online

```
Core (always local)                      Optional, opt-in (each needs an explicit user action)
───────────────────                      ───────────────────────────────────────────────
Transcription (CPU/GPU/NPU)               Model downloads (Ask / Allow / Block)
Speaker analysis, voice library           Remote AI server (non-local Ollama URL)
Library, player, forensics, exports       Built-in add-ons: webhook (network), caption server (localhost)
Vault (encryption, lock, 2FA)             Third-party add-ons (sandboxed, per-capability)
                    │
                    └── every outbound HTTP request goes through NetworkGate:
                        policy check → user consent → activity log (feature, host, bytes, time)
```

Nothing in the core path needs the network once models are present. No network request happens at start-up.

### 3.2 Encryption ("vault")

* **Algorithms** (all from Windows CNG via .NET, FIPS-validated implementations): AES-256-GCM, HKDF-SHA256,
  PBKDF2-HMAC-SHA256 (600,000 iterations, OWASP 2023), CSPRNG `RandomNumberGenerator`, HMAC-SHA1 for TOTP (RFC 6238).
  No custom primitives or protocols.
* **Key hierarchy**: one random 256-bit data key (DEK). It is never stored in the clear; it is wrapped
  (AES-256-GCM) by key slots in `vault.json`:
  * *passphrase* slot: KEK = PBKDF2(passphrase, 32-byte salt, 600k);
  * *recovery* slot: KEK = HKDF(160-bit random recovery key) — shown once, user confirms it;
  * *Windows Hello* slot (optional): KEK = HKDF(SHA-256 of a TPM-backed RSA signature over a random challenge).
    Mode "Hello instead of passphrase" adds a slot; mode "passphrase **and** Hello" replaces the passphrase slot with
    KEK = HKDF(PBKDF2(passphrase) ‖ Hello secret), so a copied vault cannot be opened without this PC's TPM.
    Enrolment signs the challenge twice and refuses if signatures differ.
* **File format `WDE1`** (streaming AEAD, the STREAM/Tink "AES-GCM-HKDF streaming" construction): header
  (magic, version, 32-byte random salt) → per-file key = HKDF(DEK, salt, "WhisperDesk file v1"); 64 KiB chunks,
  nonce = chunk counter, AAD = header ‖ logical path ‖ chunk index ‖ final flag. Detects modification, truncation,
  reordering, and swapping a file between recordings. A live recording is written as an append stream; after a crash
  it can be recovered with an explicit "incomplete" flag.
* **What is encrypted** when the vault is on: every file in the library (metadata, transcripts, text/SRT copies,
  media, forensic logs, previous versions), the voice library, live recordings and crash-recovery files, forensic logs
  outside the library, sensitive settings (vocabulary prompt, folder names, last folders), integration secrets.
* **Hidden names**: recordings are stored as `r\<random id>\*.wde`; the folder tree is kept in an encrypted index.
* **Working copies**: media that FFmpeg or the player must read is decrypted to `%LOCALAPPDATA%\WhisperDesk\work`
  (EFS-encrypted where Windows supports it), deleted on lock, exit and next start. Temporary files of every kind
  (shares, opened sessions, subtitle burning, restores) use this folder instead of `%TEMP%`.
* **Not encrypted**: models (public), add-on binaries, non-sensitive settings needed before unlock (theme, library
  location, hardware), the vault header (salts, iteration counts, wrapped keys — safe to disclose), error logs (which
  are redacted and never contain content), and **files the user exports** (outputs of the Files tab, Save as, shares,
  backups without a password). The dashboard lists these.
* **Memory**: keys live in pinned arrays and are zeroed on lock (`CryptographicOperations.ZeroMemory`). .NET strings
  (e.g. the WPF password box, decrypted transcript text in the UI) cannot be reliably wiped; this is documented.
* **Migration**: enabling encryption converts the library in place: encrypt → decrypt-and-compare → delete the
  plaintext. Interrupted conversions resume; plaintext recordings still present are listed in the dashboard.
  Deletion of plaintext on SSDs is best-effort (wear levelling); BitLocker is recommended for this reason.

### 3.3 Authentication: app lock and 2FA

* The **lock screen is the vault's unlock**: there is no separate "login" over unencrypted data. Enabling the
  passphrase always enables encryption. Locking clears the keys from memory (except while a live recording is
  running, which needs the key to keep writing; the key is cleared when it stops).
* Locks: on start, after inactivity (configurable, default 10 min), on Windows lock / sleep, manually (Ctrl+L).
* **Brute force**: after 5 failures, delays double from 30 s to 15 min, persisted in `vault.json`. This only slows
  guessing in the app; offline guessing is bounded by PBKDF2 cost and passphrase length (minimum configurable,
  default 12).
* **TOTP** (RFC 6238, any authenticator app, QR code shown at enrolment, ±1 step, replay protected). The TOTP secret is
  stored encrypted under the DEK. **It is an authentication factor, not an encryption factor**: it protects the app's
  lock screen against someone who learnt the passphrase but does not have the phone. It does not make the files harder
  to decrypt offline, and someone with the passphrase *and* write access to WhisperDesk's files could remove it from
  vault.json; that edit is detected at the next unlock (the policy MAC covers whether 2FA and Hello are set up) and the
  user is warned. The UI says what TOTP does and doesn't do.
* **Windows Hello** is the hardware-backed option (TPM key, PIN/biometric with TPM anti-hammering). Used as a second
  factor it *is* bound into the encryption key. FIDO2/WebAuthn security keys (hmac-secret/PRF) are a possible future
  addition; not implemented because Windows' WebAuthn API support for the extension varies by build.
* **Recovery**: the recovery key opens the vault without passphrase, Hello or TOTP, and forces a new passphrase.
  There is no other recovery path. No vendor, email or cloud recovery exists, by design.

### 3.4 Add-ons: capabilities and sandbox

* Add-ons declare capabilities in `addon.json`: `transcript.live`, `library.read`, `library.write` (reserved),
  `network`, `files` (a folder the user picks), `credentials` (its own secrets), `background`.
* Enabling shows the request in plain language; approval is stored with the DLL's SHA-256 and revoked if the DLL
  changes. Nothing is loaded until approved.
* Third-party DLLs run in `WhisperDesk.exe --addon-host` inside a Windows **AppContainer** with a job object
  (one process, kill on close). Network only via the `internetClient` capability when `network` was granted; file
  access only to the add-on's data folder and an approved output folder (ACLs for the container SID). Events
  reach the add-on over a pipe only for granted capabilities.
* Built-in add-ons declare capabilities the same way and are shown identically; they run in-process.
* Restored backups never carry approvals, and add-on DLLs restored from backups stay disabled.

### 3.5 Credentials

`SecretStore` → Windows Credential Manager (generic credentials, persisted per machine, not roaming). When the vault
is on, values are also AES-GCM-wrapped with a DEK-derived key, so they are unreadable while locked. Add-on options
marked secret are stored there, never in `settings.json`; logs, error messages and backups never contain them.

### 3.6 Network transparency

`NetworkGate` wraps every `HttpClient`. Each request is attributed to a feature (`Model download`, `Local AI`,
`Remote AI`, `Webhook add-on`, add-on name) and logged (time, feature, host, bytes; no URL query, no body). Settings →
Security & privacy shows per-feature switches and the activity list. Model downloads default to **Ask**: the dialog
says what is downloaded, from where, its size, and that only a file request (IP address, TLS) is visible to the host.
There are no automatic update or model checks; updates are downloaded manually from the website.

### 3.7 Logging

`App.Log` redacts: user profile and library paths become `%USERPROFILE%`/`<library>`, recording folder names are
removed, URL queries and anything that looks like a token are masked, exception messages from FFmpeg are truncated.
Logs older than 14 days are deleted. Transcript text, passphrases, keys, TOTP secrets and tokens are never passed to
the logger.

### 3.8 Build and distribution

`packages.lock.json` with locked restore, NuGet audit (`all` packages, fail on high/critical in Release),
`dotnet list package --vulnerable` gate in `build.ps1`, `SHA256SUMS.txt` beside the release files, optional
Authenticode signing of the exe and MSI when a certificate is configured. Model files are pinned by revision and
SHA-256.

---

## 4. What WhisperDesk does not protect against (shown in the dashboard and guide)

* Malware running under your Windows account while WhisperDesk is unlocked.
* Someone who knows your passphrase and has your recovery key.
* Weak passphrases against offline guessing of a stolen vault file.
* Files you export or share, and services you connect.
* Your sync client uploading encrypted files (contents stay encrypted, but sizes and timing are visible, and the
  provider holds a copy).
* Vulnerabilities in Windows, drivers, FFmpeg, whisper.cpp, ONNX Runtime or OpenVINO.

---

## 5. Implementation and verification (this release)

### 5.1 Where things are

| Area | Code |
|---|---|
| Crypto building blocks, file format, vault, TOTP, Windows Hello | `Services/Security/Vault/*.cs` |
| Protected file I/O, working folder, migration | `SecureFile.cs`, `WorkDir.cs`, `Services/Security/VaultMigration.cs` |
| Lock screen, auto-lock, dialogs | `LockWindow.cs`, `Services/Security/AppLock.cs`, `SecurityDialogs.cs`, Settings → Security & privacy |
| Network gate and activity log | `Services/Security/NetworkGate.cs` |
| Credentials | `Services/Security/SecretStore.cs` (Credential Manager) |
| Add-on capabilities, sandbox | `Services/Addons/AddonCapabilities.cs`, `AddonManager.cs`, `Services/Addons/Sandbox/*` |
| Log redaction, embedded browser lockdown, clipboard | `LogRedactor.cs`, `SafeWebView.cs`, `Services/SafeClipboard.cs` |
| Encrypted library layout and live recording | `Services/RecordingLibrary.cs`, `Services/LiveRecording.cs` |
| Supply chain | `packages.lock.json`, NuGet audit in `WhisperDesk.csproj`, `build.ps1` |
| Tests | `..\WhisperDesk.Tests` (97 tests; isolated data folder, never the real library) |

### 5.2 Automated tests

* **File format**: round trips at chunk boundaries; every single-byte flip, truncation at and inside a chunk boundary,
  appended data, chunk reordering, a file swapped between recordings, wrong key: all rejected. A crash-cut recording is
  readable only with the explicit "incomplete" flag.
* **TOTP**: the RFC 6238 SHA-1 test vectors; ±1 step; replay refused; RFC 4648 base32 vectors.
* **Vault**: wrong/right passphrase, failure counter and delay surviving a restart, recovery key (and a replaced one
  stops working), TOTP as a required second step, a weakened policy or removed 2FA in vault.json detected, absurd KDF
  parameters refused, the key held during a recording, "start over" keeps the header.
* **Storage**: nothing written in plaintext with encryption on; a locked vault refuses to write rather than writing
  plaintext; a failed write leaves the old file; settings' vocabulary hint not in settings.json; settings edited outside
  the app (remote AI, pre-enabled add-on) reset at unlock.
* **Migration**: a library with folders, audio and voices is encrypted with no title, folder name, transcript text,
  audio or voice name left readable on disk, used while encrypted (play, search, move, rename), refused while locked,
  and decrypted back intact.
* **Network**: loopback/private address classification, http to the internet refused, model downloads need consent or
  are blocked, remote AI needs the switch, a blocked request is logged without its path or query.
* **Hardening**: captions server origin/host checks (including DNS rebinding and `null` origin), port parsing, title
  sanitising (CON, `..`, separators), manifest entry escapes, malicious `.wdsession` (path traversal, extra files, planted
  media path), script-safe HTML export, a directory junction in the library during a move, log redaction of paths, URLs
  and tokens, add-on folder grants refused for WhisperDesk's own folders, unique add-on data folders.
* **Sandbox (real AppContainer)**: the probe add-on can't read a file it wasn't given, can't write outside its data
  folder, can't start a program, has no network, doesn't receive a secret it wasn't granted, doesn't see WhisperDesk's
  environment, receives events through the pipe, and a DLL that doesn't match its approval is not loaded.

### 5.3 Manual checks done

* The debug build against a throw-away data folder: starts normally; with a vault, the lock screen comes first, a wrong
  passphrase is refused, the right one opens the app; File → Lock hides the window and shows the lock screen; unlocking
  restores it.
* The Release build contains no local paths, user name or the debug data-folder switch.

### 5.4 Not verified here

* Windows Hello enrolment and unlock (needs a PC with Hello set up; the code refuses to enrol if signatures aren't
  deterministic, so failure can't lock anyone out).
* The two-factor set-up dialog end-to-end with a phone, the encryption wizard and migration through the UI on a real
  library (the same code paths are covered by tests). The 1.7.0 packages were built with `build.ps1` (locked restore,
  vulnerability gate, tests) and privacy-scanned, but not installed on this PC.
* The NPU paths with encryption on (media reaches the engines as decrypted working copies, the same as GPU/CPU).

### 5.5 Second review: ways around the protections, and what was changed

| Finding | Change |
|---|---|
| While locked, add-ons still received live lines and the captions server still served the transcript on localhost | Nothing is given to add-ons while locked; `/events` and `/api/lines` answer 423 |
| A player window kept a decrypted copy open, so the lock couldn't wipe it | Players close on lock (`ICloseOnLock`) |
| The library list and search cache stayed in memory after lock | Rebuilt after the key is gone |
| Webhook URLs (often with a token in the path) could appear in the network log | Only model downloads log a path; others log the host |
| The add-on DLL could be swapped between the hash check and loading | The sandbox hashes the exact bytes it loads (`LoadFromStream`) |
| The sandbox inherited WhisperDesk's environment | A minimal environment block (system variables and the profile variables Windows needs) |
| An add-on granted a folder containing WhisperDesk's data could rewrite settings or other add-ons | Such folders (and drive roots, the program folder, the whole profile) are refused, also when starting |
| Two add-on ids could share one data folder | Data folder names get a fingerprint when the id needed cleaning |
| An add-on could send an unbounded line and exhaust memory; or use all CPU | Bounded line reader; 25 % CPU cap and 2 GB memory cap in the job object |
| Editing settings.json could pre-approve an add-on or point summaries at a remote server | With encryption on, those settings carry a vault-keyed MAC; a missing or wrong MAC resets them at unlock and warns |
| Removing 2FA or Windows Hello by editing vault.json went unnoticed | The policy MAC covers them; the user is warned at unlock |
| A doctored KDF iteration count could hang the app | Accepted range 100,000 to 10,000,000 |
| Windows Error Reporting could collect crash data (memory) for WhisperDesk | Excluded per user (`WerAddExcludedApplication`), heap never included; undone by the uninstaller's settings clean-up |
| Someone who lost both passphrase and recovery key couldn't use the app at all | "Start over" renames the header aside (data stays encrypted and can be reopened later) after typing START OVER |

### 5.6 Remaining risks (accepted, documented to users)

* Same-user malware while unlocked (memory, screen, keystrokes, process injection) — out of scope for any user-mode app.
* .NET strings (PasswordBox, transcript text in the UI) can't be wiped; transcripts already open stay in memory while
  locked (windows are hidden).
* Plaintext deletion on SSDs is best effort (wear levelling); BitLocker recommended.
* Built-in model files are fully verified once, then by size and timestamp; a same-user attacker could replace one
  keeping both.
* The encrypted library reveals file count, sizes and timing to anyone (including a sync service) who can see it.
* The "previous" versions kept when re-transcribing a plaintext library each contain a metadata.json and are listed as
  recordings (existing behaviour, not changed here).