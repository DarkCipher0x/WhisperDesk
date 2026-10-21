# Publishing WhisperDesk in the Microsoft Store

WhisperDesk goes to the Store as an **MSIX package**. You upload it unsigned. After certification, Microsoft signs it with its own certificate, so you don't need to buy a code-signing certificate for the Store. Store users get no "unknown publisher" warning and receive updates through the Store.

The MSI and portable zip on the website are separate and are not signed by the Store. See [Signing outside the Store](#signing-outside-the-store).

## What the build makes

`.\build.ps1` builds the MSI, the zip and `dist\store\WhisperDesk-<version>-x64.msix`. All three come from the same publish folder and contain the same `WhisperDesk.exe`.

| File | Purpose |
|---|---|
| `Packaging\AppxManifest.xml` | The package manifest: identity, tiles, `.wdsession` file type, capabilities |
| `Packaging\store-identity.json` | Your Partner Center identity (fill in once, see step 2) |
| `Packaging\Images\` | Tile and icon images, made from `Assets\logo.png` by `Packaging\make-images.ps1` |
| `Packaging\StoreTools.csproj` + `packages.lock.json` | Gets Microsoft's `makeappx`/`makepri` (`Microsoft.Windows.SDK.BuildTools`), locked and audited like the app's packages. No Windows SDK install is needed |

The version is taken from `<Version>` in `WhisperDesk.csproj` as `a.b.c.0`, which the Store requires. Every Store submission needs a higher version than the last one.

Until `store-identity.json` is filled in, the build names the package `...-localtest.msix` and gives it a placeholder identity. That package works for local testing but can't be uploaded.

## One-time setup

1. **Developer account.** Sign up at Partner Center (<https://partner.microsoft.com/dashboard>) as an individual or a company. A company account needs a verified business. The publisher name shown in the Store comes from this account.
2. **Reserve the name.** Go to Apps and games → New product → MSIX or PWA app and reserve **WhisperDesk**. Then open Product management → **Product identity** and copy the three values into `Packaging\store-identity.json`:
   ```json
   {
     "IdentityName": "<Package/Identity/Name>",
     "Publisher": "CN=<the GUID shown as Package/Identity/Publisher>",
     "PublisherDisplayName": "<Package/Properties/PublisherDisplayName>"
   }
   ```
   These values aren't secret, but the package must match them exactly or the upload is rejected.
3. **Privacy policy URL.** The Store requires one because the app uses the microphone and the network. Publish the text from Help → Privacy & security (it is the same statement) on the website and use that URL.

## Each release

1. Run `.\build.ps1`. The console should say `MSIX: dist\store\WhisperDesk-<version>-x64.msix (ready to upload to Partner Center)`.
2. Test it locally (next section).
3. In Partner Center, create a new submission:
   - **Packages:** upload the `.msix`. Partner Center checks the identity, version and manifest.
   - **Properties:** category *Productivity*. Add the privacy policy URL. System requirements: 8 GB memory recommended; a Vulkan GPU or Intel NPU is optional.
   - **Age ratings:** complete the questionnaire. There is no user-generated content shared with others, and no in-app purchases.
   - **Store listing:** description, at least one screenshot (1366×768 or larger), and features. Use only synthetic screenshots from the approved guide images, never a real recording or name.
   - **Submission options → restricted capabilities:** Partner Center asks why two capabilities are needed. Suggested answers:
     - **runFullTrust:** "WhisperDesk is a WPF desktop application that runs whisper.cpp, ONNX Runtime and Vulkan native code on the user's PC for offline transcription. It also starts third-party add-ons in a Windows AppContainer sandbox that it creates itself."
     - **unvirtualizedResources:** "Users keep legal and evidential recordings in WhisperDesk's library (%LOCALAPPDATA%\WhisperDesk). Write virtualization would put this data in package storage that Windows deletes on uninstall, so reinstalling or repairing the app would destroy recordings. The data folder is shared with the MSI edition, and the add-on sandbox and encrypted working folder rely on real paths."
   - **Notes for certification:** "No account or sign-in. The app works offline. Models download on first use after the user confirms (Hugging Face/GitHub, pinned and SHA-256 verified). FFmpeg is a separate install (winget install Gyan.FFmpeg); without it, the app explains how to install it."
4. Submit. Certification usually takes 1–3 business days. Once it passes, the Store signs and publishes the package.

## Testing the package locally

Uploading isn't necessary for testing. These steps change your Windows setup, so run them yourself when you're ready:

1. Turn on Developer Mode in Windows Settings → System → For developers.
2. Make sure the MSI copy isn't running, then register the unpacked build. This needs no signing:
   ```powershell
   Add-AppxPackage -Register .\obj\msix\root\AppxManifest.xml
   ```
3. Start **WhisperDesk** from Start. Check that:
   - the tile and taskbar icons look right
   - Help → Privacy & security describes the Store install
   - Settings shows the same library as before (data is shared with the MSI)
   - a live session can use the microphone, and Windows Settings → Privacy → Microphone lists WhisperDesk
   - double-clicking a `.wdsession` opens it
   - a sandboxed third-party add-on starts (Tools → Add-ons)
   - encryption and lock still work
4. Remove it with `Get-AppxPackage *WhisperDesk* | Remove-AppxPackage`. Your library and settings stay.

To catch problems before Partner Center does, you can also run the **Windows App Certification Kit**. It comes with the full Windows SDK and checks an installed package.

### Not yet verified

The package has been built and its contents checked, but it hasn't been installed on a test PC. These areas need checking in the steps above before the first submission:
- **Add-on sandbox:** WhisperDesk starts add-ons in an AppContainer. From inside a package, Windows may give that child process the package identity.
- **Microphone permission prompt:** packaged apps get a per-app microphone switch in Windows privacy settings.
- **Windows Hello sign-in**

## Differences from the MSI

| | MSI / zip | Store (MSIX) |
|---|---|---|
| Signed by | You (optional, `WD_SIGN_THUMBPRINT`) | Microsoft |
| Updates | User installs the new MSI | Microsoft Store |
| Data folders | `%LOCALAPPDATA%\WhisperDesk`, or the folders chosen in Settings | Same folders. Write virtualization is off, so the data is kept on uninstall |
| Install options (FFmpeg/Ollama via winget, default library/models folder) | Installer dialog | None. Defaults apply, and Settings can change them |
| Uninstall "remove my data" ticks | Yes | No. Data is kept, and Help → Privacy & security lists the folders to delete |
| Repair | Run the MSI again → Repair | Settings → Apps → WhisperDesk → Advanced options → Repair |
| Program folder | `C:\Program Files\DarkCipher Systems\WhisperDesk` | `C:\Program Files\WindowsApps\...` (read-only) |

Install only one edition per PC. They share the same data, so either sees the other's library.

The app detects the Store copy (`Services\PackageInfo.cs`) only to word repair hints and the privacy page, and to leave taskbar grouping to the package. Everything else behaves the same in both editions.

## Signing outside the Store

The MSI and zip on the website are signed by `build.ps1` when `WD_SIGN_THUMBPRINT` holds the thumbprint of a code-signing certificate in your certificate store and `signtool.exe` is on PATH. You can use the copy in `%USERPROFILE%\.nuget\packages\microsoft.windows.sdk.buildtools\<version>\bin\<sdk>\x64`. Options:
- **Azure Artifact Signing** (formerly Trusted Signing): a low-cost monthly service. It requires identity validation and signs with a short-lived certificate.
- **OV code-signing certificate** from a certificate authority, normally on a hardware token or in a cloud HSM.

Unsigned website downloads still work, but SmartScreen shows "Windows protected your PC" until the file builds up reputation.

There is another way into the Store: submit the **MSI itself** as an "EXE or MSI app". That needs a signed MSI hosted at a versioned HTTPS URL, and updates stay with you. The MSIX route above avoids buying a certificate and gives Store updates, so it is the recommended one.
