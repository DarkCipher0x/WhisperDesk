# Security policy

## Reporting a vulnerability

Please report security problems **privately** through GitHub: the repository's **Security** tab → **Report a
vulnerability**. Don't open a public issue for anything that could put users or their recordings at risk.

Include the version (Help → About, or the MSI file name), what you did, what happened and what you expected. Never attach
real recordings, transcripts, voiceprints or library files: reproduce with test audio and made-up names.

## Supported versions

Only the latest release receives fixes. Each release on the Releases page carries SHA-256 checksums of its files.

## What is in scope

- The application (`WhisperDesk/`), the add-on contract (`WhisperDesk.Addons/`) and the add-on sandbox
  (`WhisperDesk/Services/Addons/Sandbox/`).
- The installer (`WhisperDesk/Installer/`), the Microsoft Store package (`WhisperDesk/Packaging/`) and `build.ps1`.
- How the app stores data (the encrypted library, the lock screen, two-factor and Windows Hello, backups) and what it
  sends on the network (model downloads, the optional AI server, add-ons, the local caption server).

How WhisperDesk is built to resist attack, and its known limits, are described in
[docs/SECURITY-ARCHITECTURE.md](../WhisperDesk/docs/SECURITY-ARCHITECTURE.md).
