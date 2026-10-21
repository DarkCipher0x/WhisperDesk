using System.Runtime.InteropServices;

namespace WhisperDesk;

/// <summary>
/// Entry point. Normally starts the WPF app. With "--addon-host" it is the sandbox process for one third-party add-on:
/// no window, no access to the library, running inside a Windows AppContainer (see Services/Addons/Sandbox).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        KeepOutOfErrorReporting();
        Services.OnnxRuntimeGuard.Init();   // before anything can load a wrong onnxruntime.dll
        if (args.Length > 0 && args[0] == Services.Addons.Sandbox.SandboxProtocol.HostArgument)
            return Services.Addons.Sandbox.SandboxHost.Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// A crash report can contain whatever was in memory: transcripts, voiceprints, the encryption key. WhisperDesk asks
    /// Windows Error Reporting not to collect or send reports for it (per user, no administrator rights), and never to
    /// include the process heap. WhisperDesk itself has no crash reporting; errors go only to its local, redacted log.
    /// </summary>
    private static void KeepOutOfErrorReporting()
    {
        try { WerSetFlags(WerFaultReportingFlagNoHeap | WerFaultReportingNoUi); } catch { }
        try
        {
            if (Environment.ProcessPath is { } exe) WerAddExcludedApplication(System.IO.Path.GetFileName(exe), false);
        }
        catch { }
    }

    private const uint WerFaultReportingFlagNoHeap = 0x1, WerFaultReportingNoUi = 0x20;

    [DllImport("kernel32.dll")] private static extern int WerSetFlags(uint flags);
    [DllImport("wer.dll", CharSet = CharSet.Unicode)] private static extern int WerAddExcludedApplication(string exeName, bool allUsers);
}
