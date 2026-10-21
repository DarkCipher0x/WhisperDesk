# Contributing to WhisperDesk

Thanks for helping. A few ground rules keep the project safe for the people who rely on it for private recordings.

## Before you open an issue

- **Security problems** go through the Security tab, not Issues (see [SECURITY.md](SECURITY.md)).
- **Never attach real recordings, transcripts, voiceprints, library files or error logs that name people.** Reproduce with
  test audio and made-up names (the guide uses Alex, Sam and Anna).
- Say which version (Help → About), whether it is the MSI, the portable zip or the Microsoft Store copy, and which
  engine you used (GPU, NPU or CPU).

## Pull requests

- Build with `WhisperDesk\build.ps1`: it restores in locked mode, refuses known-vulnerable packages and runs
  `WhisperDesk.Tests`. A pull request must pass all of it.
- Changing a package version? Run `dotnet restore --force-evaluate` deliberately and commit the reviewed
  `packages.lock.json` diff with it.
- Nothing may connect to the network without going through `NetworkGate` and being attributed to a feature.
- Keep the identities in [WhisperDesk/README.md](../WhisperDesk/README.md) (AppUserModelID, MSI UpgradeCode, assembly
  GUID) unchanged: changing them breaks upgrades.
- Bug fixes are point releases (1.8.1); new features are a minor version (1.9.0). Add a CHANGELOG entry.

By contributing you agree that your contribution is licensed under the MIT License.
