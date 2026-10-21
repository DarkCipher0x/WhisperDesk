# Changelog

## 1.8.0

**Microsoft Store edition**
- `build.ps1` now also makes an MSIX package for the Microsoft Store (`dist\store\`), from the same program files as the MSI and zip. The Store signs it with Microsoft's certificate, so Store users get no "unknown publisher" warning and receive updates through the Store. How to submit it: `docs/MICROSOFT-STORE.md`.
- The Store copy keeps its data in the same place as the MSI (`%LOCALAPPDATA%\WhisperDesk`, or the folders chosen in Settings), not in a package-private folder: uninstalling or reinstalling it never deletes recordings, and the add-on sandbox and encrypted working folder work the same way.
- In the Store copy, messages about repairing WhisperDesk point to Settings → Apps, the privacy page describes the Store install, and the taskbar uses the package's own identity. The MSI and zip behave as before.
- The MSIX tools come from Microsoft's `Microsoft.Windows.SDK.BuildTools` package, locked and audited like the app's other packages; no Windows SDK install is needed.

## 1.7.1

**Fixed**
- WhisperDesk 1.7.0 closed immediately after upgrading from 1.5.x. The upgrade left out `onnxruntime.dll` (the old copy had a higher file version, so Windows Installer skipped the new one and then removed the old one), and Windows' own older copy was loaded instead, which crashed the app. The installer now always replaces WhisperDesk's own files, and WhisperDesk only ever loads its own `onnxruntime.dll`: if it is missing, the app still starts normally with no message; speaker recognition and voice detection are simply unavailable (the status bar says why when you use them) until it is repaired.
- PCs without an NPU no longer load ONNX Runtime or the OpenVINO plugin at start-up: WhisperDesk first asks Windows whether an NPU is present.

## 1.7.0

Security and privacy hardening. This release also includes the NPU work listed under 1.6.0, which was not released on its own. Design, threat model and limits: `docs/SECURITY-ARCHITECTURE.md`.

**Encryption (optional)**
- Settings → Security & privacy → Turn on encryption. Library recordings, transcripts and their text/SRT copies, edit history, summaries, titles and folder names, voiceprints, forensic logs, live recordings (while recording) and crash-recovery files are encrypted with AES-256-GCM. The key comes from a passphrase (PBKDF2-SHA256, 600,000 rounds) and is only in memory while unlocked. Windows' own cryptography is used; no custom algorithms.
- A one-time recovery key is shown at set-up and must be confirmed. There is no other way back in.
- Existing libraries are converted in place: each file is encrypted, checked by decrypting it, and only then is the plaintext removed. Turning encryption off reverses it the same way.
- Decrypted working copies (audio being played, shares, opened sessions, subtitle burning, restores) live in one private folder that is wiped on lock, exit and start, instead of %TEMP%.

**Lock screen, 2FA, Windows Hello**
- With encryption on: passphrase at start, auto-lock after inactivity (default 10 min), on Windows lock/sleep, optionally on minimise, and Ctrl+L. Locking hides all windows, forgets the key, wipes working copies and clears text WhisperDesk copied.
- Increasing delays after 5 wrong attempts (30 s, doubling to 15 min), kept across restarts. Lock settings are protected by a MAC; a file edited outside the app resets them to safe defaults.
- Optional TOTP two-factor sign-in (any authenticator app, QR code at set-up). It protects the lock screen, not the encryption, and the UI says so.
- Optional Windows Hello, either instead of the passphrase or required in addition to it (binds the key to this PC's TPM).

**Network**
- Nothing connects at start-up. Every request goes through one gate that checks policy, attributes it to a feature and logs it (Settings → Security & privacy → Network activity).
- Model downloads ask first by default (Ask / Allow / Never). Whisper GGML and speaker/VAD models are now pinned to fixed revisions and checked against SHA-256 (previously unchecked); files downloaded by older versions are verified once.
- Summaries only go to an AI server on another computer when allowed; plain http:// is refused for internet addresses (webhook too).
- ONNX Runtime telemetry events are switched off; the embedded browser views run InPrivate with Edge background networking off.
- The installer no longer downloads FFmpeg unless the box is ticked.

**Add-ons**
- Capabilities (`addon.json`): live transcript, library read, network, files (one approved folder), credentials, background. The user approves each one; events are only delivered for granted capabilities.
- Third-party add-ons run in a Windows AppContainer sandbox process (no file, library, settings or network access unless granted; no child processes; 2 GB memory cap). DLLs are no longer loaded into WhisperDesk, and are never loaded just to list them. Approval is bound to the DLL's SHA-256.
- Secret options (webhook URL, API keys) are stored in Windows Credential Manager, additionally encrypted with the vault when it is on. The existing webhook URL moves there automatically.
- Add-on SDK 1.1: `AddonOption.IsSecret`, `IAddonHostPermissions`.

**Fixed (security)**
- The live-captions server sent `Access-Control-Allow-Origin: *`, so any web page open in the browser could read live transcripts. It now refuses other origins and non-local host names (DNS rebinding) and limits listeners.
- Restoring a backup could re-enable add-ons or point summaries at a remote server; restored settings now keep this PC's approvals and security choices.
- Library move, backup and uninstall clean-up followed directory junctions (could copy or delete files outside the library); links are now skipped.
- Opened .wdsession files were left in %TEMP%; only known files are extracted, with size limits, and they are wiped when the player closes.
- Error logs contained file paths and names (titles, user name); logs are now redacted and kept 14 days.
- FFmpeg was looked up on PATH before trusted locations; PATH now comes last and skips Temp/Downloads.
- Exported HTML players escape every `<` in embedded data; reserved Windows names (CON, NUL…) in titles are handled; the captions port can no longer inject text into a URL; the guide viewer only opens http(s) links.
- Copied text is kept out of Windows clipboard history and cloud clipboard.
- `System.Security.Cryptography.Pkcs` pinned to 8.0.2 (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7 via PDFsharp).

**Build**
- `packages.lock.json` with locked restore; NuGet audit fails the build on high/critical advisories; `build.ps1` refuses vulnerable packages, runs the security tests, writes SHA256SUMS, and signs when `WD_SIGN_THUMBPRINT` is set.
- New test project `..\WhisperDesk.Tests` (file-format tampering, RFC 6238 vectors, vault, migration, network policy, captions server, untrusted input, log redaction, and a real AppContainer escape probe).

## 1.6.0 (not released separately; included in 1.7.0)

**Changed in 1.6.0: transcription on the NPU**
- On PCs with an Intel NPU (Core Ultra, for example Copilot+ PCs), Whisper now runs on the NPU automatically. Nothing to set up: the main window's device box is now **Hardware: Auto / NPU / GPU (Vulkan) / CPU only**, and Auto picks the NPU when one is found, then the GPU, then the CPU.
- Works for Base, Small and Large-v3 Turbo. Other models, and PCs without an NPU, use whisper.cpp on the GPU or CPU as before.
- At start-up WhisperDesk checks for an NPU in the background and runs a tiny test calculation on it, so a driver that is present but broken is not used. Settings → Transcription shows the result, for example "Intel NPU found, used for transcription", and has the same Hardware choice.
- The NPU version of a model (ONNX format) downloads from Hugging Face the first time it is needed. It comes from a pinned release and every file is checked against its SHA-256 before use. Nothing extra ships in the installer.
- The model is prepared for the NPU once and the result is kept, so later starts are quick. It is rebuilt by itself after a driver or runtime update.
- If anything goes wrong on the NPU, WhisperDesk logs why and carries on with whisper.cpp from where it stopped. Lines already transcribed are kept.
- File transcription and live sessions both use it. Transcripts have the same format as before: lines with timestamps, language and confidence values.
- About shows where the last transcription ran, and the privacy page explains that the NPU is part of this PC: audio is never sent anywhere to be transcribed.
- Backups leave out the device-specific prepared NPU model. It is rebuilt on the PC it is restored to.

**Speaker voiceprints on the NPU**
- Voiceprints (speaker recognition, the per-line speaker check and live speaker identification) also run on the NPU. They are used only after a start-up check shows they match the CPU's result, so voices already saved in the voice library keep being recognised. Clips over 30 seconds, and any NPU problem, go to the CPU, which gives the same result.
- Settings → Speakers → Voiceprint hardware: Auto (NPU when available) or CPU only, with a line saying where they run now.
- Finding who spoke when (diarization) and voice detection stay on the CPU. The speaker library they come from (sherpa-onnx) can only reach the NPU through a version of ONNX Runtime that would need a separate OpenVINO installation.

**Faster, clearer first run on the NPU**
- The selected model is prepared for the NPU in the background while WhisperDesk is idle, so Start doesn't have to wait. A job started meanwhile waits for that preparation instead of starting a second one.
- The status bar shows each step with a running timer: downloading (with real sizes), compiling for the NPU (first time only), loading the prepared model, preparing the decoder, warming up. Cancel works during a long compile; the compile finishes in the background and is kept.
- When the GPU/CPU fallback model isn't downloaded yet, it is fetched in the background after the NPU files, so the NPU can start first. Two requests for the same download share one download.
- The prepared encoder from 1.6 previews is reused, not compiled again.

**Never a silent fallback**
- When the NPU was wanted but isn't used, the engine badge says so (`· npu failed` or `· npu not used`), the status bar explains why, and Settings and About show this session's NPU problem.

**Version**
- The version is set in one place (`<Version>` in WhisperDesk.csproj). The title bar, About, reports, backups, session files, add-ons, the installer, the build script and the user guide all take it from there.

## 1.5.2

**Recordings stay on this PC by default**
- New libraries go to `%LOCALAPPDATA%\WhisperDesk\Library`, which is never synced. Before 1.5.2 the default was `Documents\WhisperDesk Library`, and Windows often puts Documents inside OneDrive, which uploaded recordings, transcripts and session data.
- Existing libraries are not moved: a profile that already had recordings keeps using the same folder.
- A start-up banner and Settings → Storage & tools warn when the library is inside OneDrive, Dropbox, Google Drive, iCloud or Box. **Move library to a local folder** copies every file, checks it against the original (SHA-256), and only then removes the original.
- **Move library to…** in Settings moves the library to any folder the same safe way.
- Recordings found in the old Documents library (for example synced down from another PC) are reported and can be moved into the library.
- The live captions text file and the sample Word export add-on no longer default to a cloud-synced Documents folder, and log a warning if their path is synced.
- The installer's library prompt shows the new default and warns about cloud-synced folders.

**Fixed**
- Copy buttons (OBS overlay URL, transcript, selected lines, summary) no longer show an error when another program is briefly holding the clipboard (`CLIPBRD_E_CANT_OPEN`).

## 1.5.1

**Fixed**
- Privacy clean-up: removed a screenshot of a personal test recording from the user guide, replaced demo names with fictional ones, and removed local build paths from the shipped files.
