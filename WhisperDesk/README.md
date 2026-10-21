# WhisperDesk

by **darkcipher0x** · v1.8.0 · open source ([MIT License](LICENSE))

Local, GPU-accelerated transcription for Windows: files, live audio (mic / Voicemeeter / PC loopback), speaker
identification with a voice library, a recordings library, and add-ons. Built on whisper.cpp (Vulkan: AMD, NVIDIA,
Intel GPUs) and sherpa-onnx speaker models. Nothing leaves the PC unless you switch on or ask for something that needs the
network (model downloads ask first; every connection is listed in Settings → Security & privacy).

**Full documentation:** [docs/WhisperDesk-User-Admin-Guide.pdf](docs/WhisperDesk-User-Admin-Guide.pdf)
(also installed as *Help → User & admin guide*).

## Features
- Transcribe audio/video files (drag & drop, folders, queue); Whisper models Tiny → Large-v3 (Turbo recommended)
- Speaker identification: every line tagged `[hh:mm:ss] Name (Sex): text`; auto speaker count; per-line correction,
  add/remove speakers, ▶ play any line
- Voice library: remember named voices, add voices from a recording, automatic recognition
- Live transcription with live speaker tagging; optional audio recording; review-before-save
- Recordings library: folders, types, tags, notes, metadata, full-text search
- Audited line edits: ✎ any line, required reason, dated/timezoned/user-stamped edit log in every export
- Player mode + sharing: self-contained web player (.html) or .wdsession package (no voiceprints)
- Offline call summary (key points, action items, decisions, questions) in TXT/Word/PDF/JSON
- Pick speaker names from the voice library; live re-checks and closest-match hints
- Outputs: TXT, SRT, VTT, JSON, Word (.docx), PDF, burned-in subtitles (GPU encoder, styleable)
- Add-ons: OBS caption overlay (`http://localhost:7890/overlay`), live text file, webhook, SDK in `sdk/`
- darkcipher0x terminal theme (dark / light / system), brand accent colour, File → Settings for advanced options
- Optional encryption (AES-256-GCM, passphrase + recovery key), lock screen with auto-lock, TOTP two-factor and Windows Hello;
  sandboxed add-ons with per-permission approval; see [docs/SECURITY-ARCHITECTURE.md](docs/SECURITY-ARCHITECTURE.md)

## Requirements
Windows 10/11 x64 · FFmpeg (`winget install Gyan.FFmpeg`) · Vulkan GPU recommended

## Build
```powershell
.\build.ps1
```
Produces `dist\WhisperDesk-<ver>-x64.msi`, the portable zip, the guide PDF, SHA256SUMS and the Microsoft Store package
(`dist\store\`, see [docs/MICROSOFT-STORE.md](docs/MICROSOFT-STORE.md)). It stops on an unlocked or
known-vulnerable dependency and runs the security tests in `..\WhisperDesk.Tests` (`dotnet test ..\WhisperDesk.Tests`). Bump `<Version>` in
`WhisperDesk.csproj` for a new release (the MSI upgrades older installs).

| Tool | Purpose |
|---|---|
| `tools\make-logo.ps1` | Regenerates `Assets\app.ico`, `logo.png`, `logo-tile.png` |
| `docs\make_guide.py` | Regenerates the user/admin guide PDF (needs `pip install reportlab`) |
| `..\WhisperDesk.Addons` | Add-on SDK contract (copied to `sdk\` for developers) |

## Identity (never change)
| What | Value |
|---|---|
| AppUserModelID | `darkcipher0x.WhisperDesk` |
| MSI UpgradeCode | `3fc82978-8263-42a1-9d62-8b858d3c0a9b` |
| Assembly GUID | `d06d3e32-6317-4cc3-a20f-2645b1c78f11` |

## License

WhisperDesk is open-source software under the [MIT License](LICENSE). © 2026 darkcipher0x.
Third-party components keep their own licences (listed in LICENSE). FFmpeg and Ollama are separate, optional programs.
