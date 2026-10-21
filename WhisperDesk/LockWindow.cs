using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WhisperDesk.Services;
using WhisperDesk.Services.Security;

namespace WhisperDesk;

/// <summary>
/// The lock screen, which is the vault's unlock: the passphrase (or Windows Hello, or the recovery key) re-derives the
/// encryption key. There is no separate login over unencrypted data. With 2FA on, a code from the authenticator app
/// is asked for after the passphrase.
/// </summary>
public sealed class LockWindow : Window
{
    private readonly PasswordBox _pass = new() { Padding = new Thickness(6, 5, 6, 5), FontSize = 14 };
    private readonly TextBox _code = new() { Padding = new Thickness(6, 5, 6, 5), FontSize = 18, MaxLength = 7, Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MinHeight = 18 };
    private readonly Button _unlock;
    private readonly Button _hello = new() { Content = "Use Windows Hello", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _recovery = new() { Content = "Use the recovery key…", Margin = new Thickness(0) };
    private readonly StackPanel _passPanel = new(), _codePanel = new() { Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;

    /// <summary>True when unlocked with the recovery key: the caller must make the user choose a new passphrase.</summary>
    public bool UsedRecovery { get; private set; }

    public LockWindow(bool startup)
    {
        Title = "WhisperDesk is locked";
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var title = new TextBlock { Text = "🔒  WhisperDesk is locked", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        root.Children.Add(title);
        var sub = Muted(startup
            ? "Your recordings, transcripts and voices are encrypted. Enter your passphrase to open them."
            : "WhisperDesk locked itself and forgot the encryption key. Enter your passphrase to continue." +
              (Vault.LockPending ? " A live recording is still being saved in the background." : ""));
        root.Children.Add(sub);

        _passPanel.Children.Add(new TextBlock { Text = "Passphrase", Margin = new Thickness(0, 14, 0, 4) });
        _passPanel.Children.Add(_pass);
        root.Children.Add(_passPanel);

        _codePanel.Children.Add(new TextBlock { Text = "Code from your authenticator app", Margin = new Thickness(0, 14, 0, 4) });
        _codePanel.Children.Add(_code);
        _codePanel.Children.Add(Muted("Two-factor sign-in protects the app; your data's encryption depends on the passphrase."));
        root.Children.Add(_codePanel);

        root.Children.Add(_message);

        _unlock = new Button { Content = "Unlock", IsDefault = true, Margin = new Thickness(0), MinWidth = 100, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_unlock, Dock.Right);
        buttons.Children.Add(_unlock);
        if (Vault.HelloMode == "alternative") buttons.Children.Add(_hello);
        buttons.Children.Add(_recovery);
        root.Children.Add(buttons);
        if (Vault.HelloMode == "required")
            root.Children.Add(Muted("This PC also asks for Windows Hello after the passphrase (the key is bound to this PC's TPM)."));
        var exit = new TextBlock { Margin = new Thickness(0, 12, 0, 0), FontSize = 11.5 };
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Close WhisperDesk"));
        link.Click += (_, _) => { DialogResult = false; };
        exit.Inlines.Add(link);
        if (startup)
        {
            exit.Inlines.Add(new System.Windows.Documents.Run("   ·   "));
            var over = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("I can't unlock: start over…"));
            over.Click += (_, _) => StartOver();
            exit.Inlines.Add(over);
        }
        root.Children.Add(exit);
        Content = root;

        _unlock.Click += async (_, _) => await UnlockAsync();
        _hello.Click += async (_, _) => await RunAsync(Vault.UnlockWithHelloAsync);
        _recovery.Click += (_, _) => Recover();
        _code.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        _timer.Tick += (_, _) => ShowDelay();
        Loaded += (_, _) => { _pass.Focus(); ShowDelay(); _timer.Start(); Activate(); };
        Closed += (_, _) => { _timer.Stop(); Vault.CancelPendingUnlock(); };
    }

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    private void ShowDelay()
    {
        var wait = Vault.RetryDelay;
        if (wait > TimeSpan.Zero) { _message.Text = $"Too many wrong attempts. Try again in {Vault.Format(wait)}."; _unlock.IsEnabled = false; }
        else if (!_busy) { _unlock.IsEnabled = true; if (_message.Text.StartsWith("Too many")) _message.Text = ""; }
    }

    private async Task UnlockAsync()
    {
        if (_codePanel.Visibility == Visibility.Visible) { Show(Vault.VerifyTotp(_code.Text)); return; }
        var pass = _pass.Password;
        await RunAsync(() => Vault.UnlockWithPassphraseAsync(pass));
    }

    private async Task RunAsync(Func<Task<UnlockResult>> attempt)
    {
        if (_busy) return;
        _busy = true;
        _unlock.IsEnabled = _hello.IsEnabled = _recovery.IsEnabled = false;
        _message.Text = "Checking…";
        Cursor = Cursors.Wait;
        try { Show(await attempt()); }
        catch (Exception ex) { _message.Text = ex.Message; }
        finally
        {
            Cursor = null;
            _busy = false;
            _hello.IsEnabled = _recovery.IsEnabled = true;
            ShowDelay();
        }
    }

    private void Show(UnlockResult r)
    {
        switch (r.Outcome)
        {
            case UnlockOutcome.Unlocked:
                _pass.Clear();
                DialogResult = true;
                return;
            case UnlockOutcome.NeedsTotp:
                _pass.Clear();
                _passPanel.Visibility = Visibility.Collapsed;
                _codePanel.Visibility = Visibility.Visible;
                _hello.Visibility = Visibility.Collapsed;
                _message.Text = "";
                _code.Focus();
                return;
            default:
                _message.Text = r.Message ?? "That didn't work.";
                if (_codePanel.Visibility == Visibility.Visible) { _code.Clear(); _code.Focus(); } else { _pass.SelectAll(); _pass.Focus(); }
                return;
        }
    }

    /// <summary>True when the user set the encrypted data aside to use WhisperDesk without it.</summary>
    public bool StartedOver { get; private set; }

    /// <summary>
    /// Last resort when the passphrase and recovery key are both lost. Nothing is decrypted or deleted: the encrypted
    /// library stays on disk, unreadable; WhisperDesk starts with encryption off and an empty library view.
    /// </summary>
    private void StartOver()
    {
        var typed = InputDialog.Ask(this, "Start over",
            "Your encrypted recordings stay on this PC, still encrypted and unreadable. WhisperDesk will start without them, with " +
            "encryption off. If you find your passphrase or recovery key later, rename the set-aside vault.json back to open them.\n\n" +
            "Type START OVER to continue:", "");
        if (!string.Equals(typed?.Trim(), "START OVER", StringComparison.Ordinal)) return;
        var aside = Vault.SetAside();
        App.LogEvent("Encryption header set aside at the lock screen.");
        MessageBox.Show(this, $"The encryption header was renamed to:\n{System.IO.Path.GetFileName(aside)}\n(in {System.IO.Path.GetDirectoryName(aside)}).\n\n" +
                              "Rename it back to vault.json to open the encrypted data again.", "Start over", MessageBoxButton.OK, MessageBoxImage.Information);
        StartedOver = true;
        DialogResult = true;
    }

    private void Recover()
    {
        var key = InputDialog.Ask(this, "Recovery key", "Enter your recovery key (8 groups of 4 characters):", "");
        if (string.IsNullOrWhiteSpace(key)) return;
        var r = Vault.UnlockWithRecovery(key);
        if (r.Outcome == UnlockOutcome.Unlocked) { UsedRecovery = true; DialogResult = true; }
        else _message.Text = r.Message ?? "That recovery key didn't work.";
    }
}
