using System.IO;

namespace WhisperDesk.Services;

/// <summary>The one place WhisperDesk's data folder is decided: %LOCALAPPDATA%\WhisperDesk (never roams or syncs).</summary>
public static class AppPaths
{
    /// <summary>Tests only (internal): points every data path at a throw-away folder so tests never touch real data.</summary>
    internal static string? Override { get; set; }

#if DEBUG
    /// <summary>
    /// Debug builds only: WHISPERDESK_DEV_DATA=&lt;folder&gt; runs the app against a throw-away data folder (and ignores the
    /// installer's paths), for trying things without touching a real library. Release builds have no such switch.
    /// </summary>
    private static readonly string? DevData = Environment.GetEnvironmentVariable("WHISPERDESK_DEV_DATA") is { Length: > 3 } d && Path.IsPathFullyQualified(d) ? d : null;
    public static bool DevIsolated => Override != null || DevData != null;
    public static string LocalData => Override ?? DevData ?? Default;
#else
    public static bool DevIsolated => Override != null;
    public static string LocalData => Override ?? Default;
#endif

    private static string Default => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperDesk");
}
