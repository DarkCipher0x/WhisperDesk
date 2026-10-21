using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WhisperDesk.Services.Security;

/// <summary>A window that is closed (not just hidden) when WhisperDesk locks, because it holds decrypted data open.</summary>
public interface ICloseOnLock { }

/// <summary>
/// Locks WhisperDesk: after inactivity (policy), when Windows locks or sleeps, when minimised (optional) or on Ctrl+L.
/// Locking hides every window, clears what WhisperDesk put on the clipboard, wipes decrypted working files, forgets the
/// content-revealing settings and the voice library in memory, and forgets the encryption key (kept only while a live
/// recording still needs it). Unlocking is the vault's unlock (<see cref="LockWindow"/>).
/// </summary>
public static class AppLock
{
    private static DateTime _lastInput = DateTime.UtcNow;
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private static bool _locked, _started;

    /// <summary>Raised on the UI thread just before windows are hidden (stop playback, close menus).</summary>
    public static event Action? Locking;
    /// <summary>Raised on the UI thread after a successful unlock (reload library views).</summary>
    public static event Action? Unlocked;

    public static bool IsLocked => _locked;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        InputManager.Current.PreProcessInput += (_, e) =>
        {
            if (e.StagingItem.Input is KeyboardEventArgs or MouseButtonEventArgs or MouseWheelEventArgs) _lastInput = DateTime.UtcNow;
        };
        Timer.Tick += (_, _) =>
        {
            var p = Vault.Policy;
            if (!_locked && Vault.IsEnabled && p.AutoLockMinutes > 0 && DateTime.UtcNow - _lastInput > TimeSpan.FromMinutes(p.AutoLockMinutes))
                Lock("inactivity");
        };
        Timer.Start();
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect
                && Vault.IsEnabled && Vault.Policy.LockOnWindowsLock)
                Application.Current?.Dispatcher.BeginInvoke(() => Lock("Windows locked"));
        };
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Suspend && Vault.IsEnabled)
                Application.Current?.Dispatcher.Invoke(() => Lock("sleep"));
        };
    }

    /// <summary>Call from a window's StateChanged to honour "lock when minimised".</summary>
    public static void OnWindowStateChanged(Window w)
    {
        if (w.WindowState == WindowState.Minimized && Vault.IsEnabled && Vault.Policy.LockOnMinimize) Lock("minimised");
    }

    private static readonly List<Window> Hidden = new();

    public static void Lock(string reason)
    {
        if (_locked || !Vault.IsEnabled) return;
        var app = Application.Current;
        if (app == null) return;
        _locked = true;
        try { Locking?.Invoke(); } catch { }
        // Close modal dialogs (they can't be hidden safely) and windows that hold decrypted files open (players), so the
        // working copies can be wiped.
        foreach (var w in app.Windows.OfType<Window>().Where(w => w.IsVisible && w is not LockWindow && (IsModal(w) || w is ICloseOnLock)).ToList())
            try { w.Close(); } catch { }
        Hidden.Clear();
        foreach (var w in app.Windows.OfType<Window>().Where(w => w.IsVisible && w is not LockWindow).ToList())
        {
            Hidden.Add(w);
            w.Hide();
        }
        if (Vault.Policy.ClearClipboardOnLock) SafeClipboard.ClearIfOurs();
        bool keyHeld = Vault.LockPending || HoldsKey();
        AppSettings.Current.ForgetProtected();
        if (!keyHeld) VoiceLibrary.Shared.Forget();
        Vault.Lock();
        try { WorkDir.WipeAll(); } catch { }
        App.LogEvent($"Locked ({reason}).");
        app.Dispatcher.BeginInvoke(ShowLockScreen, DispatcherPriority.Background);
    }

    private static bool HoldsKey() => Vault.LockPending;

    private static bool IsModal(Window w) =>
        (bool)(typeof(Window).GetField("_showingAsDialog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(w) ?? false);

    private static void ShowLockScreen()
    {
        while (true)
        {
            var lockWin = new LockWindow(startup: false);
            var ok = lockWin.ShowDialog() == true;
            if (!ok)
            {
                // "Close WhisperDesk" from the lock screen: exit without showing anything.
                Application.Current.Shutdown();
                return;
            }
            AfterUnlock(lockWin.UsedRecovery, owner: null);
            break;
        }
        _locked = false;
        _lastInput = DateTime.UtcNow;
        foreach (var w in Hidden) try { w.Show(); } catch { }
        Hidden.Clear();
        try { Unlocked?.Invoke(); } catch { }
        Application.Current.MainWindow?.Activate();
    }

    /// <summary>Everything that has to be read again once the key is back. Also used at start-up.</summary>
    public static void AfterUnlock(bool usedRecovery, Window? owner)
    {
        AppSettings.Current.UnsealProtected();
        AppSettings.Current.VerifySecurity();
        VoiceLibrary.Shared.Reload();
        NetworkGate.Reload();
        if (AppSettings.Current.SecuritySettingsWereReset)
        {
            const string reset = "Some of WhisperDesk's settings were changed outside the app (add-on permissions, the AI server or downloads). " +
                                 "For safety, add-ons were switched off and must be approved again, and summaries only use this PC.";
            if (owner != null) MessageBox.Show(owner, reset, "Settings reset", MessageBoxButton.OK, MessageBoxImage.Warning);
            else MessageBox.Show(reset, "Settings reset", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (Vault.PolicyWasTampered)
        {
            const string msg = "WhisperDesk's security settings file (vault.json) was changed outside the app. The lock settings were reset to safe defaults, " +
                               "and two-factor sign-in or Windows Hello may have been removed. Check them in Settings → Security & privacy, and if you didn't do this, " +
                               "someone with access to this PC's files may know your passphrase: change it.";
            if (owner != null) MessageBox.Show(owner, msg, "Security settings reset", MessageBoxButton.OK, MessageBoxImage.Warning);
            else MessageBox.Show(msg, "Security settings reset", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (usedRecovery) SecurityDialogs.ForceNewPassphrase(owner);
    }

    /// <summary>
    /// Start-up: if encryption is on, the lock screen comes before any window. Returns false if the user closed it.
    /// </summary>
    public static bool UnlockAtStartup()
    {
        if (!Vault.IsEnabled) return true;
        var lockWin = new LockWindow(startup: true);
        if (lockWin.ShowDialog() != true) return false;
        if (!lockWin.StartedOver) AfterUnlock(lockWin.UsedRecovery, owner: null);
        return true;
    }
}
