using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using QRCoder;
using WhisperDesk.Services;
using WhisperDesk.Services.Security;

namespace WhisperDesk;

/// <summary>
/// The dialogs behind Settings → Security &amp; privacy: turning encryption on/off, the recovery key, passphrase changes,
/// two-factor sign-in, Windows Hello, and the network activity list. Anything that weakens protection asks for the
/// passphrase again first, so someone at an unlocked PC can't quietly switch it off.
/// </summary>
public static class SecurityDialogs
{
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));

    private static TextBlock Text(string s, double size = 13, bool bold = false, double bottom = 8) =>
        new() { Text = s, TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, bottom) };

    private static TextBlock Muted(string s)
    {
        var t = Text(s, 12);
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    private static PasswordBox Pass() => new() { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 2, 0, 8) };

    // ---------------- passphrase strength ----------------

    /// <summary>A rough, honest strength estimate (length and character variety; common patterns penalised). Not a guarantee.</summary>
    public static (int Bits, string Label) Strength(string p)
    {
        if (p.Length == 0) return (0, "");
        int pool = 0;
        if (p.Any(char.IsLower)) pool += 26;
        if (p.Any(char.IsUpper)) pool += 26;
        if (p.Any(char.IsDigit)) pool += 10;
        if (p.Any(c => !char.IsLetterOrDigit(c))) pool += 33;
        var distinct = p.Distinct().Count();
        double bits = Math.Min(p.Length, distinct * 2) * Math.Log2(Math.Max(pool, 2));
        var lower = p.ToLowerInvariant();
        foreach (var weak in new[] { "password", "123456", "qwerty", "letmein", "whisperdesk", "admin", "welcome" })
            if (lower.Contains(weak)) bits -= 25;
        var b = (int)Math.Max(0, bits);
        return (b, b < 50 ? "weak" : b < 70 ? "fair" : b < 90 ? "good" : "strong");
    }

    private static StackPanel NewPassphraseFields(int minLength, out PasswordBox first, out PasswordBox second, out TextBlock meter)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text($"New passphrase (at least {minLength} characters; a few unrelated words works well)", bottom: 2));
        first = Pass();
        panel.Children.Add(first);
        meter = Muted("");
        var m = meter;
        var f = first;
        first.PasswordChanged += (_, _) => { var (bits, label) = Strength(f.Password); m.Text = label.Length > 0 ? $"Strength: {label} (about {bits} bits)" : ""; };
        panel.Children.Add(meter);
        panel.Children.Add(Text("Repeat it", bottom: 2));
        second = Pass();
        panel.Children.Add(second);
        return panel;
    }

    private static string? CheckNew(PasswordBox a, PasswordBox b, int min)
    {
        if (a.Password.Length < min) return $"The passphrase must be at least {min} characters.";
        if (a.Password != b.Password) return "The two passphrases don't match.";
        if (Strength(a.Password).Bits < 50) return "That passphrase is easy to guess. Use a longer one (for example four or five unrelated words).";
        return null;
    }

    /// <summary>Asks for the current passphrase again. True when it is right.</summary>
    public static Task<bool> ReauthenticateAsync(Window owner, string why)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Text(why));
        panel.Children.Add(Text("Current passphrase", bottom: 2));
        var pass = Pass();
        panel.Children.Add(pass);
        var msg = Muted("");
        panel.Children.Add(msg);
        var win = SpeakerDialogs.MakeWindow(owner, "Confirm it's you", panel, out var ok, "Continue");
        bool result = false;
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = false;
            msg.Text = "Checking…";
            if (await Vault.VerifyPassphraseAsync(pass.Password)) { result = true; win.DialogResult = true; }
            else { msg.Text = Vault.RetryDelay > TimeSpan.Zero ? $"Too many attempts. Try again in {Vault.Format(Vault.RetryDelay)}." : "That passphrase is not right."; ok.IsEnabled = true; }
        };
        win.Loaded += (_, _) => pass.Focus();
        win.ShowDialog();   // modal: the check above finishes before it returns
        return Task.FromResult(result);
    }

    // ---------------- turning encryption on ----------------

    public static async Task<bool> EnableEncryptionAsync(Window owner)
    {
        if (owner is MainWindow { IsBusy: true } || Application.Current.MainWindow is MainWindow { IsBusy: true })
        {
            MessageBox.Show(owner, "Finish the current transcription or live session first.", "Encryption", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var policy = new VaultPolicy();
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Text("Encrypt your WhisperDesk data", 16, bold: true));
        panel.Children.Add(Text("Recordings, transcripts, edit history, summaries, voiceprints, forensic logs, live recordings and crash-recovery files are " +
                                "encrypted with AES-256-GCM. The key is made from your passphrase (PBKDF2-SHA256, 600,000 rounds) and is only in memory " +
                                "while WhisperDesk is unlocked. Recording titles and folder names are hidden too."));
        panel.Children.Add(Text("WhisperDesk will ask for the passphrase every time it starts, and lock itself after inactivity. Locking forgets the key.", 12.5));
        var notCovered = Muted("Not encrypted: files you export or share, transcription outputs written next to your audio files on the Files tab, backups without a password, " +
                               "and models. Encryption does not protect against malware running on this PC while WhisperDesk is unlocked.");
        panel.Children.Add(notCovered);
        if (PrivacyInfo.CloudSyncedBy(RecordingLibrary.Root) is { } cloud)
        {
            var c = Text($"Your library is in a {cloud} folder. Once encrypted, {cloud} will still upload the files, but only as encrypted data " +
                         "(their number, sizes and times stay visible to it).", 12);
            c.Foreground = Warn;
            panel.Children.Add(c);
        }
        panel.Children.Add(NewPassphraseFields(policy.MinPassphraseLength, out var p1, out var p2, out _));
        var understand = new CheckBox { Content = "I understand: if I lose both my passphrase and my recovery key, my data can't be recovered by anyone.", Margin = new Thickness(0, 4, 0, 10) };
        ((CheckBox)understand).SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        panel.Children.Add(understand);
        var msg = Text("", 12);
        msg.Foreground = Warn;
        panel.Children.Add(msg);
        var win = SpeakerDialogs.MakeWindow(owner, "Turn on encryption", panel, out var ok, "Encrypt");
        win.Width = 600;
        string? passphrase = null;
        ok.Click += (_, _) =>
        {
            if (CheckNew(p1, p2, policy.MinPassphraseLength) is { } problem) { msg.Text = problem; return; }
            if (understand.IsChecked != true) { msg.Text = "Tick the box to confirm you understand there is no other way back in."; return; }
            passphrase = p1.Password;
            win.DialogResult = true;
        };
        win.Loaded += (_, _) => p1.Focus();
        if (win.ShowDialog() != true || passphrase == null) return false;

        string recovery;
        try { recovery = Vault.Create(passphrase, policy); }
        catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Encryption", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
        ShowRecoveryKey(owner, recovery, first: true);
        await RunMigrationAsync(owner, encrypt: true);
        return true;
    }

    /// <summary>Shows the recovery key once; the user must type its last group to show they kept it.</summary>
    public static void ShowRecoveryKey(Window owner, string key, bool first)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Text(first ? "Your recovery key" : "Your new recovery key", 16, bold: true));
        panel.Children.Add(Text("It opens your encrypted data if you forget the passphrase or lose your phone or Windows Hello. " +
                                "It is shown only now. WhisperDesk does not keep a copy, and there is no other way back in."));
        var box = new TextBox { Text = key, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 17, Padding = new Thickness(8, 6, 8, 6),
                                HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(box);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var save = new Button { Content = "Save to a file…" };
        var print = new Button { Content = "Print…" };
        row.Children.Add(save); row.Children.Add(print);
        panel.Children.Add(row);
        panel.Children.Add(Muted("Keep it somewhere safe and separate from this PC: printed in a drawer, or in a password manager. Don't store it next to the library or in the same cloud folder."));
        panel.Children.Add(Text("Type the last four characters to confirm you kept it:", bottom: 2));
        var confirm = new TextBox { Padding = new Thickness(6, 4, 6, 4), Width = 120, HorizontalAlignment = HorizontalAlignment.Left, CharacterCasing = CharacterCasing.Upper, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(confirm);
        var win = SpeakerDialogs.MakeWindow(owner, "Recovery key", panel, out var ok, "I have kept it");
        win.Width = 560;
        save.Click += (_, _) =>
        {
            var dlg = new SaveFileDialog { FileName = "WhisperDesk recovery key.txt", Filter = "Text file|*.txt", Title = "Save the recovery key (not in the library folder)" };
            if (dlg.ShowDialog(win) != true) return;
            if (dlg.FileName.StartsWith(RecordingLibrary.Root, StringComparison.OrdinalIgnoreCase) || PrivacyInfo.CloudSyncedBy(dlg.FileName) != null)
                if (MessageBox.Show(win, "That folder is inside the library or a cloud-synced folder, where the key would sit next to (or travel with) the data it opens. Save there anyway?",
                        "Recovery key", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            File.WriteAllText(dlg.FileName, $"WhisperDesk recovery key (vault {Vault.Id})\r\nCreated {DateTime.Now:yyyy-MM-dd HH:mm}\r\n\r\n{key}\r\n");
        };
        print.Click += (_, _) =>
        {
            var pd = new PrintDialog();
            if (pd.ShowDialog() != true) return;
            var visual = new TextBlock { Text = $"WhisperDesk recovery key\n\n{key}\n\nVault {Vault.Id} · {DateTime.Now:yyyy-MM-dd}", FontSize = 22, Margin = new Thickness(60), FontFamily = new FontFamily("Consolas") };
            visual.Measure(new Size(pd.PrintableAreaWidth, pd.PrintableAreaHeight));
            visual.Arrange(new Rect(visual.DesiredSize));
            pd.PrintVisual(visual, "WhisperDesk recovery key");
        };
        ok.Click += (_, _) =>
        {
            if (!string.Equals(confirm.Text.Trim(), key[^4..], StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(win, "That doesn't match the last four characters.", "Recovery key"); return; }
            win.DialogResult = true;
        };
        win.Closing += (_, e) => { if (win.DialogResult != true) { e.Cancel = MessageBox.Show(win, "Close without confirming you kept the recovery key?", "Recovery key", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes; } };
        win.ShowDialog();
        box.Text = "";
    }

    /// <summary>Runs the conversion with a progress window (cannot be cancelled half-way; it can be repeated).</summary>
    public static async Task RunMigrationAsync(Window owner, bool encrypt)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        var status = Text(encrypt ? "Encrypting…" : "Decrypting…");
        panel.Children.Add(status);
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 4, 0, 10) });
        panel.Children.Add(Muted("Each file is written in its new form and checked before the old one is removed. Keep WhisperDesk open until this finishes."));
        var win = new Window
        {
            Title = encrypt ? "Encrypting your data" : "Decrypting your data", Owner = owner, Content = panel, Width = 460, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow,
        };
        win.SetResourceReference(Window.BackgroundProperty, "Bg");
        win.SetResourceReference(Window.ForegroundProperty, "Fg");
        bool done = false;
        win.Closing += (_, e) => e.Cancel = !done;
        win.Show();
        owner.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(s => status.Text = s);
            var report = encrypt ? await VaultMigration.EncryptAllAsync(progress, CancellationToken.None)
                                 : await VaultMigration.DecryptAllAsync(progress, CancellationToken.None);
            done = true;
            win.Close();
            MessageBox.Show(owner,
                (encrypt ? $"Encryption is on. {report.Recordings} recording(s) and {report.Files} other file(s) were encrypted."
                         : $"Encryption is off. {report.Recordings} recording(s) and {report.Files} other file(s) were decrypted.") +
                (report.Failed > 0 ? $"\n\n⚠ {report.Failed} item(s) could not be converted and were left as they were. Run it again later:\n" + string.Join("\n", report.Problems.Take(8)) : ""),
                "Encryption", MessageBoxButton.OK, report.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            done = true;
            win.Close();
            App.Log("Encryption conversion", ex);
            MessageBox.Show(owner, "The conversion stopped: " + ex.Message + "\n\nNothing was lost. Run it again from Settings → Security & privacy.", "Encryption", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { owner.IsEnabled = true; }
    }

    public static async Task<bool> DisableEncryptionAsync(Window owner)
    {
        if (!await ReauthenticateAsync(owner, "Turning encryption off writes all your recordings, transcripts and voiceprints back to disk as ordinary, readable files.")) return false;
        if (MessageBox.Show(owner, "Decrypt everything and turn encryption, the lock screen and two-factor sign-in off?", "Turn off encryption",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        await RunMigrationAsync(owner, encrypt: false);
        return !Vault.IsEnabled;
    }

    // ---------------- passphrase and recovery ----------------

    public static async Task ChangePassphraseAsync(Window owner)
    {
        if (!await ReauthenticateAsync(owner, "Change the passphrase that opens WhisperDesk.")) return;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(NewPassphraseFields(Vault.Policy.MinPassphraseLength, out var p1, out var p2, out _));
        var msg = Text("", 12); msg.Foreground = Warn; panel.Children.Add(msg);
        var win = SpeakerDialogs.MakeWindow(owner, "New passphrase", panel, out var ok, "Change");
        ok.Click += (_, _) =>
        {
            if (CheckNew(p1, p2, Vault.Policy.MinPassphraseLength) is { } problem) { msg.Text = problem; return; }
            try { Vault.ChangePassphrase(p1.Password); win.DialogResult = true; }
            catch (Exception ex) { msg.Text = ex.Message; }
        };
        win.Loaded += (_, _) => p1.Focus();
        if (win.ShowDialog() == true) MessageBox.Show(owner, "The passphrase was changed. Your recovery key still works.", "Passphrase");
    }

    /// <summary>After unlocking with the recovery key: a new passphrase is required before continuing.</summary>
    public static void ForceNewPassphrase(Window? owner)
    {
        bool hadHello = Vault.HelloMode != "none";
        while (true)
        {
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(Text("You opened WhisperDesk with the recovery key. Choose a new passphrase now.", bold: true));
            if (hadHello) panel.Children.Add(Muted("Windows Hello is removed for safety; set it up again afterwards in Settings → Security & privacy."));
            if (Vault.HasTotp) panel.Children.Add(Muted("Two-factor sign-in stays on. If you lost your phone, turn it off and set it up again in Settings → Security & privacy."));
            panel.Children.Add(NewPassphraseFields(Vault.Policy.MinPassphraseLength, out var p1, out var p2, out _));
            var msg = Text("", 12); msg.Foreground = Warn; panel.Children.Add(msg);
            var win = new Window { Title = "New passphrase", Content = panel, Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                                   WindowStartupLocation = WindowStartupLocation.CenterScreen };
            if (owner != null) win.Owner = owner;
            win.SetResourceReference(Window.BackgroundProperty, "Bg");
            win.SetResourceReference(Window.ForegroundProperty, "Fg");
            var ok = new Button { Content = "Set passphrase", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
            panel.Children.Add(ok);
            ok.Click += (_, _) =>
            {
                if (CheckNew(p1, p2, Vault.Policy.MinPassphraseLength) is { } problem) { msg.Text = problem; return; }
                try { Vault.ResetPassphraseAfterRecovery(p1.Password); win.DialogResult = true; }
                catch (Exception ex) { msg.Text = ex.Message; }
            };
            win.Loaded += (_, _) => p1.Focus();
            if (win.ShowDialog() == true) return;
        }
    }

    public static async Task NewRecoveryKeyAsync(Window owner)
    {
        if (!await ReauthenticateAsync(owner, "Make a new recovery key. The old one stops working.")) return;
        ShowRecoveryKey(owner, Vault.NewRecoveryKey(), first: false);
    }

    // ---------------- two-factor ----------------

    public static void SetupTotp(Window owner)
    {
        var (secret, uri) = Vault.NewTotpEnrollment(Environment.UserName.Length > 0 ? "this PC" : "WhisperDesk");
        try
        {
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(Text("Two-factor sign-in with an authenticator app", 15, bold: true));
            panel.Children.Add(Text("Scan the code with Microsoft Authenticator, Google Authenticator, Aegis, 1Password, Bitwarden or any TOTP app. " +
                                    "After this, WhisperDesk asks for a 6-digit code after your passphrase."));
            panel.Children.Add(Muted("This protects the app against someone who learnt your passphrase but doesn't have your phone. It doesn't add encryption: " +
                                     "a stolen copy of your encrypted files is protected by the passphrase (and Windows Hello, if required)."));
            var qr = new PngByteQRCode(new QRCodeGenerator().CreateQrCode(uri, QRCodeGenerator.ECCLevel.M)).GetGraphic(6);
            var img = new BitmapImage();
            img.BeginInit(); img.StreamSource = new MemoryStream(qr); img.CacheOption = BitmapCacheOption.OnLoad; img.EndInit();
            panel.Children.Add(new Image { Source = img, Width = 220, Height = 220, Margin = new Thickness(0, 6, 0, 6) });
            panel.Children.Add(Muted("Can't scan? Enter this key in the app (time-based, 6 digits, 30 seconds):"));
            panel.Children.Add(new TextBox { Text = string.Join(" ", Enumerable.Range(0, (VaultCrypto.Base32(secret).Length + 3) / 4).Select(i => VaultCrypto.Base32(secret).Skip(i * 4).Take(4)).Select(c => new string(c.ToArray()))),
                                             IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 8) });
            panel.Children.Add(Text("Enter the code the app shows now:", bottom: 2));
            var code = new TextBox { Width = 140, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 16, Padding = new Thickness(6, 4, 6, 4), MaxLength = 7, Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(code);
            var msg = Text("", 12); msg.Foreground = Warn; panel.Children.Add(msg);
            var win = SpeakerDialogs.MakeWindow(owner, "Two-factor sign-in", panel, out var ok, "Turn on");
            win.Width = 520;
            ok.Click += (_, _) =>
            {
                if (Vault.EnableTotp(secret, code.Text)) win.DialogResult = true;
                else msg.Text = "That code is not right. Check that the phone's clock is correct, then try the next code.";
            };
            win.Loaded += (_, _) => code.Focus();
            if (win.ShowDialog() == true)
                MessageBox.Show(owner, "Two-factor sign-in is on. Keep your recovery key: it also works if you lose your phone.", "Two-factor sign-in");
        }
        finally { VaultCrypto.Clear(secret); }
    }

    public static async Task DisableTotpAsync(Window owner)
    {
        if (!await ReauthenticateAsync(owner, "Turn off two-factor sign-in.")) return;
        Vault.DisableTotp();
    }

    // ---------------- Windows Hello ----------------

    public static async Task SetupHelloAsync(Window owner)
    {
        if (!await WindowsHello.IsAvailableAsync())
        {
            MessageBox.Show(owner, "Windows Hello isn't set up on this PC (Windows Settings → Accounts → Sign-in options).", "Windows Hello");
            return;
        }
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Text("Windows Hello (PIN, face or fingerprint, with a key kept in this PC's security chip)", 14, bold: true));
        var alt = new RadioButton { Content = "Unlock with Windows Hello instead of typing the passphrase (convenient)", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        var req = new RadioButton { Content = "Require the passphrase AND Windows Hello (stronger: a copied library can't be opened on another PC without the recovery key)", Margin = new Thickness(0, 4, 0, 4) };
        alt.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        panel.Children.Add(alt); panel.Children.Add(req);
        panel.Children.Add(Muted("With the first option, anyone who knows your Windows PIN can open WhisperDesk on this PC. With the second, if this PC or its security chip fails you need the recovery key."));
        panel.Children.Add(Text("Current passphrase", bottom: 2));
        var pass = Pass();
        panel.Children.Add(pass);
        var msg = Text("", 12); msg.Foreground = Warn; panel.Children.Add(msg);
        var win = SpeakerDialogs.MakeWindow(owner, "Windows Hello", panel, out var ok, "Set up");
        win.Width = 560;
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = false;
            msg.Text = "Checking the passphrase…";
            if (!await Vault.VerifyPassphraseAsync(pass.Password)) { msg.Text = "That passphrase is not right."; ok.IsEnabled = true; return; }
            msg.Text = "Follow the Windows Hello prompt (it may appear behind this window)…";
            try { await Vault.EnrollHelloAsync(req.IsChecked == true ? "required" : "alternative", pass.Password); win.DialogResult = true; }
            catch (Exception ex) { msg.Text = ex.Message; ok.IsEnabled = true; }
        };
        win.ShowDialog();
    }

    public static Task RemoveHelloAsync(Window owner)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Text("Remove Windows Hello. Enter the passphrase (it will open WhisperDesk on its own again)."));
        var pass = Pass();
        panel.Children.Add(pass);
        var msg = Text("", 12); msg.Foreground = Warn; panel.Children.Add(msg);
        var win = SpeakerDialogs.MakeWindow(owner, "Windows Hello", panel, out var ok, "Remove");
        ok.Click += async (_, _) =>
        {
            if (!await Vault.VerifyPassphraseAsync(pass.Password)) { msg.Text = "That passphrase is not right."; return; }
            try { await Vault.RemoveHelloAsync(pass.Password); win.DialogResult = true; }
            catch (Exception ex) { msg.Text = ex.Message; }
        };
        win.ShowDialog();
        return Task.CompletedTask;
    }

    // ---------------- network activity ----------------

    public static void ShowNetworkActivity(Window owner)
    {
        var list = new ListBox { Height = 360, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5 };
        void Fill() => list.ItemsSource = NetworkGate.Activity.AsEnumerable().Reverse().Select(e => $"{e.AtText}  {e.Feature,-28}  {e.Summary}").ToList();
        Fill();
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Text("Every connection WhisperDesk made, newest first: when, which feature, which server. Addresses are shown without their query; nothing you transcribed is listed here.", 12.5));
        panel.Children.Add(list);
        var clear = new Button { Content = "Clear the list", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        clear.Click += (_, _) => { NetworkGate.ClearLog(); Fill(); };
        panel.Children.Add(clear);
        panel.Children.Add(Muted("Not listed: connections made by third-party add-ons you allowed network access (they run in their own sandbox), Windows itself, and programs you open from WhisperDesk (your browser, FFmpeg runs offline)."));
        var win = SpeakerDialogs.MakeWindow(owner, "Network activity", panel, out var ok, "Close");
        win.Width = 860;
        ok.Click += (_, _) => win.DialogResult = true;
        Action<NetworkEvent> live = _ => win.Dispatcher.BeginInvoke(Fill);
        NetworkGate.Recorded += live;
        win.Closed += (_, _) => NetworkGate.Recorded -= live;
        win.ShowDialog();
    }
}
