using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using WhisperDesk.Services;

namespace WhisperDesk;

public partial class App : Application
{
    /// <summary>Windows AppUserModelID: groups taskbar windows and matches the Start menu shortcut the installer creates.</summary>
    public const string AppUserModelId = "darkcipher0x.WhisperDesk";

    /// <summary>Called if the process is about to die, so a running live session can be saved for recovery.</summary>
    public static Action? EmergencySave { get; set; }

    public static string LogDir => Path.Combine(WhisperDesk.Services.AppPaths.LocalData, "logs");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string appId);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Run by the uninstaller (user ticked data to remove): no window.
        if (e.Args.Length > 0 && e.Args[0] == "--cleanup")
        {
            try { InstallerIntegration.Cleanup(e.Args.Skip(1)); } catch (Exception ex) { Log("Cleanup", ex); }
            Shutdown(0);
            return;
        }

        // Crash guards: an unexpected error is logged and shown, but never takes down a recording or a long job.
        DispatcherUnhandledException += OnUiException;
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log("Background task", ev.Exception); ev.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            Log("Fatal", ev.ExceptionObject as Exception);
            try { EmergencySave?.Invoke(); } catch { }
        };

        // The Store package has its own identity, which Windows uses for the taskbar and Start; don't override it.
        if (!PackageInfo.IsPackaged) try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }
        try { RecordingLibrary.PinPreviousDefault(); } catch (Exception ex) { Log("Library location", ex); }
        ThemeManager.Apply(Enum.TryParse<ThemeMode>(AppSettings.Current.Theme, out var mode) ? mode : ThemeMode.System);
        ThemeManager.FollowSystemChanges();
        base.OnStartup(e);
        WindowsShare.CleanUp();
        // Housekeeping off the UI thread: old logs and plaintext working files left by a crash.
        _ = Task.Run(() =>
        {
            Services.Security.LogRedactor.PruneLogs(LogDir);
            try { Services.Security.WorkDir.WipeAll(); } catch { }
        });

        // Encryption on: the lock screen (the vault's unlock) comes before anything that shows or loads data.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        OfferLibraryVault();
        if (!Services.Security.AppLock.UnlockAtStartup()) { Shutdown(0); return; }
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Launch screen: shown first so it paints while the main window (models list, library, add-ons) is built.
        AboutWindow? splash = null;
        if (AppSettings.Current.ShowSplash)
        {
            splash = new AboutWindow(splash: true);
            splash.Show();
            splash.SetStatus("Loading library and settings…");
        }
        var started = DateTime.Now;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var main = new MainWindow();
            MainWindow = main;
            main.ContentRendered += async (_, _) =>
            {
                if (splash == null) return;
                var shown = DateTime.Now - started;
                if (shown < TimeSpan.FromSeconds(1.2)) await Task.Delay(TimeSpan.FromSeconds(1.2) - shown);
                splash.Close();
                main.Activate();
            };
            splash?.SetStatus("Ready.");
            main.Show();
            Services.Security.AppLock.Start();
        });
    }

    /// <summary>
    /// A library copied from another PC (or after settings were reset) can carry its encryption header. If this PC has none
    /// and the library holds encrypted recordings, offer to use it; the passphrase is still needed to open anything.
    /// </summary>
    private static void OfferLibraryVault()
    {
        try
        {
            if (Services.Security.Vault.IsEnabled) return;
            var header = Path.Combine(RecordingLibrary.Root, Services.Security.Vault.LibraryCopyName);
            var store = Path.Combine(RecordingLibrary.Root, RecordingLibrary.EncryptedDir);
            if (!File.Exists(header) || !Directory.Exists(store) || Services.Security.Vault.ReadHeader(header) == null) return;
            if (MessageBox.Show("Your recordings library is encrypted, but this PC has no record of its encryption.\n\n" +
                                "Use the library's encryption header? You will be asked for its passphrase (or recovery key).",
                    "Encrypted library", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Services.Security.Vault.Adopt(header);
        }
        catch (Exception ex) { Log("Library vault", ex); }
    }

    /// <summary>A one-line operational event in the log (no content, no paths).</summary>
    public static void LogEvent(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(Path.Combine(LogDir, $"errors-{DateTime.Now:yyyy-MM-dd}.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {Services.Security.LogRedactor.Redact(message)}\r\n");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Services.Security.WorkDir.WipeAll(); } catch { }   // decrypted working copies never outlive the app
        base.OnExit(e);
    }

    private static int _shown;

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = Log("UI", e.Exception);
        e.Handled = true;   // keep running: live sessions and jobs continue
        if (Interlocked.Increment(ref _shown) > 3) { Interlocked.Decrement(ref _shown); return; }   // don't stack up dialogs
        try
        {
            MessageBox.Show(MainWindow, $"Something went wrong, but WhisperDesk kept running.\n\n{e.Exception.Message}\n\nDetails were saved to:\n{path}",
                "WhisperDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
        finally { Interlocked.Decrement(ref _shown); }
    }

    /// <summary>Appends an error to today's log file. Never throws.</summary>
    public static string Log(string where, Exception? ex)
    {
        var path = Path.Combine(LogDir, $"errors-{DateTime.Now:yyyy-MM-dd}.log");
        try
        {
            Directory.CreateDirectory(LogDir);
            // Redacted: no folder or file names, URLs without paths, no token-like strings (see LogRedactor).
            File.AppendAllText(path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {Services.Security.LogRedactor.Redact(where)}: {Services.Security.LogRedactor.Redact(ex?.ToString())}\r\n\r\n");
        }
        catch { }
        return path;
    }
}
