using System.Runtime.InteropServices;

namespace WhisperDesk.Services;

/// <summary>
/// Whether this copy runs as an MSIX package (Microsoft Store) rather than from the MSI or the portable zip. The Store
/// copy has its own package identity: Windows groups its windows by that identity, file types are registered by the
/// package, and it is repaired or removed in Settings → Apps instead of by an installer.
/// </summary>
public static class PackageInfo
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch { return false; }
    }

    /// <summary>How the user repairs a damaged copy (shown in messages).</summary>
    public static string RepairHint => IsPackaged
        ? "Repair WhisperDesk in Windows Settings → Apps → Installed apps → WhisperDesk → Advanced options → Repair"
        : "Repair WhisperDesk (run its installer again and choose Repair)";
}
