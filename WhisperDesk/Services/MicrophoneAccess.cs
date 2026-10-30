using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Media.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace WhisperDesk.Services;

/// <summary>
/// Windows' microphone permission. The Store (MSIX) copy has its own entry in Settings → Privacy &amp; security →
/// Microphone and must ask before its first recording: WASAPI on its own never shows the prompt, it just gets
/// E_ACCESSDENIED. The MSI / portable copy falls under "Let desktop apps access your microphone" instead.
/// </summary>
public static class MicrophoneAccess
{
    public const int EAccessDenied = unchecked((int)0x80070005);

    /// <summary>
    /// Makes sure WhisperDesk may record from a microphone, asking Windows' consent prompt when it hasn't been asked yet.
    /// Returns null when access is allowed (or can't be told in advance), otherwise the reason it isn't.
    /// </summary>
    public static async Task<string?> EnsureAsync()
    {
        if (!PackageInfo.IsPackaged) return null; // unpackaged: no per-app prompt; a denial shows up as E_ACCESSDENIED
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return null; // AppCapability is 1903+ (the package needs 2004)

        AppCapabilityAccessStatus status;
        try { status = AppCapability.Create("microphone").CheckAccess(); }
        catch (Exception ex) { App.Log("Microphone access", ex); return null; }

        switch (status)
        {
            case AppCapabilityAccessStatus.Allowed:
                return null;
            case AppCapabilityAccessStatus.UserPromptRequired:
                // Opening an audio-only MediaCapture is what shows Windows' "Let WhisperDesk access your microphone?"
                // prompt and adds WhisperDesk to the microphone list in Settings.
                try
                {
                    using var capture = new MediaCapture();
                    await capture.InitializeAsync(new MediaCaptureInitializationSettings
                    {
                        StreamingCaptureMode = StreamingCaptureMode.Audio,
                        MediaCategory = MediaCategory.Speech,
                    });
                    return null;
                }
                catch (Exception ex) when (IsAccessDenied(ex)) { return DeniedMessage(byUser: true); }
                catch (Exception ex) { App.Log("Microphone access", ex); return null; } // e.g. no microphone at all: let WASAPI report it
            case AppCapabilityAccessStatus.DeniedByUser:
                return DeniedMessage(byUser: true);
            case AppCapabilityAccessStatus.DeniedBySystem:
                return DeniedMessage(byUser: false);
            default:
                return null;
        }
    }

    public static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException || ex.HResult == EAccessDenied ||
        (ex is COMException c && c.ErrorCode == EAccessDenied) ||
        (ex.InnerException != null && IsAccessDenied(ex.InnerException));

    public static string DeniedMessage(bool byUser = true) =>
        (PackageInfo.IsPackaged
            ? byUser
                ? "Windows isn't letting WhisperDesk use the microphone.\n\nTurn on “Microphone access” and WhisperDesk in Settings → Privacy & security → Microphone."
                : "Microphone access is turned off for this PC (or by your organisation).\n\nTurn on “Microphone access” in Settings → Privacy & security → Microphone."
            : "Windows isn't letting WhisperDesk use the microphone.\n\nTurn on “Microphone access” and “Let desktop apps access your microphone” in Settings → Privacy & security → Microphone.")
        + "\n\nOpen those settings now?";

    public static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }); }
        catch (Exception ex) { App.Log("Microphone settings", ex); }
    }
}

/// <summary>Windows refused the microphone; the message says where to turn it on.</summary>
public sealed class MicrophoneDeniedException(string message) : UnauthorizedAccessException(message);
