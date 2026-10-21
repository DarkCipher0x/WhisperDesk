using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>Advanced settings. Edits a copy and only applies it on Save.</summary>
public partial class SettingsWindow : Window
{
    private AppSettings _draft;

    public SettingsWindow()
    {
        InitializeComponent();
        BurnFontBox.ItemsSource = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n).ToList();
        BurnPositionBox.ItemsSource = new[] { "Bottom", "Top" };
        _draft = AppSettings.Current.Clone();
        Bind(_draft);
        FfmpegFound.Text = AudioDecoder.FindFfmpeg() is { } f ? $"Currently using: {f}" : "FFmpeg not found. Install it with: winget install Gyan.FFmpeg";
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _draft = AppSettings.Current.Clone();
        var (models, library) = (_draft.ModelsPath, _draft.LibraryPath);   // where models and recordings physically are — never reset
        _draft.CopyAdvancedFrom(new AppSettings());
        _draft.ModelsPath = models;
        _draft.LibraryPath = library;
        Bind(_draft);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Commit any text box that still has focus, then sanity-clamp the numbers.
        (FocusManager_GetFocused() as System.Windows.Controls.TextBox)?.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        var d = _draft;
        d.Threads = Math.Clamp(d.Threads, 0, Environment.ProcessorCount);
        d.GpuDevice = Math.Max(0, d.GpuDevice);
        d.ClusterThreshold = Math.Clamp(d.ClusterThreshold, 0.05f, 0.95f);
        d.VoiceMatchThreshold = Math.Clamp(d.VoiceMatchThreshold, 0.1f, 0.99f);
        d.MinSpeechSeconds = Math.Clamp(d.MinSpeechSeconds, 0f, 5f);
        d.MinSilenceSeconds = Math.Clamp(d.MinSilenceSeconds, 0f, 5f);
        d.SpeakerMergeSimilarity = Math.Clamp(d.SpeakerMergeSimilarity, 0.3f, 0.95f);
        d.MaxAutoSpeakers = Math.Clamp(d.MaxAutoSpeakers, 1, 50);
        if (d.PerformanceMode is not ("Light" or "Balanced" or "Maximum")) d.PerformanceMode = "Balanced";
        d.MaleBelowHz = Math.Clamp(d.MaleBelowHz, 60, 400);
        d.FemaleAboveHz = Math.Clamp(d.FemaleAboveHz, d.MaleBelowHz, 400);
        d.LiveSilenceMs = Math.Clamp(d.LiveSilenceMs, 150, 5000);
        d.LiveMaxUtteranceSeconds = Math.Clamp(d.LiveMaxUtteranceSeconds, 3, 60);
        d.FfmpegPath = d.FfmpegPath.Trim().Trim('"');
        d.ModelsPath = d.ModelsPath.Trim().Trim('"');
        d.LiveSameSpeakerThreshold = Math.Clamp(d.LiveSameSpeakerThreshold, 0.1f, 0.95f);
        d.BurnFontSize = Math.Clamp(d.BurnFontSize, 8, 72);
        d.BurnBoxOpacity = Math.Clamp(d.BurnBoxOpacity, 0, 100);
        d.BurnMargin = Math.Clamp(d.BurnMargin, 0, 200);
        d.BurnOutline = Math.Clamp(d.BurnOutline, 0, 8);
        d.BurnQuality = Math.Clamp(d.BurnQuality, 10, 40);

        if (ThemeManager.ParseHex(d.AccentColor) == null) d.AccentColor = ThemeManager.DefaultAccent;
        d.AccentColor = d.AccentColor.Trim().TrimStart('#').ToUpperInvariant();

        AppSettings.Current.CopyAdvancedFrom(d);
        AppSettings.Current.Save();
        if (Services.Security.Vault.IsUnlocked)
            try { Services.Security.Vault.SetPolicy(DraftPolicy()); } catch (Exception ex) { App.Log("Lock settings", ex); }
        ThemeManager.Apply(ThemeManager.Mode);   // repaint with the new accent
        DialogResult = true;
    }

    private bool _binding;

    private void Bind(AppSettings d)
    {
        _binding = true;
        d.Hardware = d.EffectiveHardware;   // settings from before 1.6 have none yet
        _ = ShowDetectedHardwareAsync();
        DataContext = null;
        DataContext = d;
        StyleBox.IsChecked = d.BurnStyle == "Box";
        StyleOutline.IsChecked = d.BurnStyle != "Box";
        _binding = false;
        UpdatePreview();
        UpdateModelsInfo();
        UpdateLibraryInfo();
        OnAccentChanged(this, EventArgs.Empty);
        var cloud = PrivacyInfo.CloudWarnings();
        CloudWarning.Text = "⚠ " + string.Join("\n⚠ ", cloud);
        CloudWarning.Visibility = cloud.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSecurity();
    }

    /// <summary>What the start-up NPU probe found ("Intel NPU found, used for transcription").</summary>
    private async Task ShowDetectedHardwareAsync()
    {
        SpeakerHardwareStatus.Text = "Voiceprints now: " + Services.Npu.NpuSpeakerEmbedder.Status;
        var probe = await Services.Npu.NpuDetector.ProbeAsync();
        HardwareDetected.Text = probe.Available
            ? $"✓ {probe.Summary} (with Auto or NPU; Base, Small and Large-v3 Turbo have an NPU version, other models use the GPU)."
            : $"{probe.Summary}. Auto uses the GPU (Vulkan), or the CPU when there is no Vulkan GPU.";
        if (TranscriptionEngine.SessionNpuProblem is { } problem)
            HardwareDetected.Text += $"\n⚠ This session: {problem}. Transcription is on the GPU/CPU until WhisperDesk is restarted.";
        SpeakerHardwareStatus.Text = "Voiceprints now: " + Services.Npu.NpuSpeakerEmbedder.Status;
    }

    private void OnPrivacyPage(object sender, RoutedEventArgs e) => GuideWindow.Open(Owner ?? this, GuideWindow.PrivacyPage);

    // ---------------- Security & privacy ----------------

    /// <summary>Fills the Security &amp; privacy page from the current state (encryption, lock, 2FA, Hello, network, plaintext).</summary>
    private void UpdateSecurity()
    {
        if (EncryptionStatus == null) return;
        var vault = Services.Security.Vault.State;
        bool on = vault != Services.Security.VaultState.Disabled;
        int plainLeft = on ? RecordingLibrary.PlaintextCount() : 0;
        EncryptionStatus.Text = on ? "✓ Encryption is on (AES-256-GCM, key from your passphrase)" : "Encryption is off: recordings and transcripts are ordinary files.";
        EncryptionDetail.Text = on
            ? "Library, transcripts, voiceprints, forensic logs, live recordings and recovery files are encrypted; titles and folder names are hidden. " +
              (Services.Security.Vault.HasRecovery ? "A recovery key exists. " : "⚠ No recovery key: make one. ") +
              (plainLeft > 0 ? $"⚠ {plainLeft} recording(s) are still unencrypted: click “Encrypt remaining recordings”." : "")
            : "Turn it on to protect your data if this PC, its disk or a copy of the library is stolen. You will set a passphrase and get a recovery key.";
        EncryptButton.Content = !on ? "Turn on encryption…" : plainLeft > 0 ? "Encrypt remaining recordings" : "Turn off encryption…";
        ChangePassButton.IsEnabled = RecoveryButton.IsEnabled = LockNowButton.IsEnabled = on;
        LockPanel.IsEnabled = on;
        LockOffHint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        var p = Services.Security.Vault.Policy;
        AutoLockBox.SelectedItem = AutoLockBox.Items.Cast<System.Windows.Controls.ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == p.AutoLockMinutes.ToString())
                                   ?? AutoLockBox.Items[3];
        LockWindowsBox.IsChecked = p.LockOnWindowsLock;
        LockMinimizeBox.IsChecked = p.LockOnMinimize;
        ClearClipboardBox.IsChecked = p.ClearClipboardOnLock;
        TotpStatus.Text = Services.Security.Vault.HasTotp ? "✓ Two-factor sign-in (authenticator app) is on." : "Two-factor sign-in is off.";
        TotpButton.Content = Services.Security.Vault.HasTotp ? "Turn off two-factor…" : "Set up two-factor sign-in…";
        var hello = Services.Security.Vault.HelloMode;
        HelloButton.Content = hello == "none" ? "Set up Windows Hello…" : "Remove Windows Hello…";
        HelloStatus.Text = hello switch
        {
            "alternative" => "Windows Hello can open WhisperDesk instead of the passphrase.",
            "required" => "The passphrase and Windows Hello are both needed (the key is bound to this PC's security chip).",
            _ => "Windows Hello: not used.",
        };

        var net = new List<string>();
        net.Add(AiReport.IsRemote && _draft.AllowRemoteAi ? "⚠ Summaries are written by an AI server on another computer (the transcript is sent there)."
               : _draft.AiSummaryEnabled ? "Summaries: local AI on this PC (Ollama at localhost)." : "Summaries: offline (no AI server).");
        var addons = AppSettings.Current.AddonsEnabled.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        var networked = AppSettings.Current.AddonApprovals.Where(kv => addons.Contains(kv.Key) && kv.Value.Capabilities.Contains(Services.Addons.AddonCapabilities.Network)).Select(kv => kv.Key).ToList();
        net.Add(networked.Count > 0 ? $"⚠ Add-ons with network access switched on: {string.Join(", ", networked)}." : "No add-on with network access is switched on.");
        NetworkFeatures.Text = string.Join("\n", net);

        var outside = new List<string>();
        var output = _draft.OutputFolder;
        outside.Add($"• Transcripts the Files tab writes ({(_draft.Txt ? "txt " : "")}{(_draft.Srt ? "srt " : "")}{(_draft.Vtt ? "vtt " : "")}{(_draft.Json ? "json " : "")}) go to {(string.IsNullOrEmpty(output) ? "the folder of each audio file" : output)}, as ordinary files.");
        outside.Add("• Anything you export, share or save with “Save as”, and backups made without a password.");
        if (AppSettings.Current.AddonsEnabled.TryGetValue(Services.Addons.BuiltIn.LiveTextFileAddon.AddonId, out var lt) && lt)
            outside.Add($"• The live captions text file: {Services.Addons.BuiltIn.LiveTextFileAddon.CurrentPath()}.");
        if (on) outside.Add("• While unlocked: what is on screen and in WhisperDesk's memory, and decrypted working copies of audio being played (deleted when it locks or closes).");
        else outside.Add("• Everything in the library and the voice library, by anyone who can read your files (encryption is off).");
        PlainOutside.Text = string.Join("\n", outside);
    }

    /// <summary>The lock settings chosen on the page (applied on Save).</summary>
    private Services.Security.VaultPolicy DraftPolicy()
    {
        var p = Services.Security.Vault.Policy;
        p.AutoLockMinutes = int.TryParse((AutoLockBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string, out var m) ? m : 10;
        p.LockOnWindowsLock = LockWindowsBox.IsChecked == true;
        p.LockOnMinimize = LockMinimizeBox.IsChecked == true;
        p.ClearClipboardOnLock = ClearClipboardBox.IsChecked == true;
        return p;
    }

    private async void OnEncrypt(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Services.Security.Vault.IsEnabled) await SecurityDialogs.EnableEncryptionAsync(this);
            else if (RecordingLibrary.PlaintextCount() > 0) await SecurityDialogs.RunMigrationAsync(this, encrypt: true);
            else await SecurityDialogs.DisableEncryptionAsync(this);
            if (Owner is MainWindow mw) mw.OnSecurityChanged();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Encryption", MessageBoxButton.OK, MessageBoxImage.Error); }
        UpdateSecurity();
    }

    private async void OnChangePassphrase(object sender, RoutedEventArgs e) { await SecurityDialogs.ChangePassphraseAsync(this); UpdateSecurity(); }
    private async void OnNewRecovery(object sender, RoutedEventArgs e) { await SecurityDialogs.NewRecoveryKeyAsync(this); UpdateSecurity(); }

    private void OnLockNow(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Dispatcher.BeginInvoke(() => Services.Security.AppLock.Lock("locked by you"));
    }

    private async void OnTotp(object sender, RoutedEventArgs e)
    {
        if (Services.Security.Vault.HasTotp) await SecurityDialogs.DisableTotpAsync(this);
        else SecurityDialogs.SetupTotp(this);
        UpdateSecurity();
    }

    private async void OnHello(object sender, RoutedEventArgs e)
    {
        if (Services.Security.Vault.HelloMode == "none") await SecurityDialogs.SetupHelloAsync(this);
        else await SecurityDialogs.RemoveHelloAsync(this);
        UpdateSecurity();
    }

    private void OnNetworkActivity(object sender, RoutedEventArgs e) => SecurityDialogs.ShowNetworkActivity(this);

    private void OnAddonPermissions(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow mw) mw.OpenAddons(this);
        UpdateSecurity();
    }

    /// <summary>Opens Settings on a given section (e.g. "Summary &amp; AI").</summary>
    public SettingsWindow ShowSection(string header)
    {
        foreach (System.Windows.Controls.TabItem t in Tabs.Items)
            if (string.Equals(t.Header as string, header, StringComparison.OrdinalIgnoreCase)) { Tabs.SelectedItem = t; break; }
        return this;
    }

    private void OnBurnStyleRadio(object sender, RoutedEventArgs e)
    {
        if (_binding || _draft == null) return;
        _draft.BurnStyle = StyleBox.IsChecked == true ? "Box" : "Outline";
        UpdatePreview();
    }

    private void OnBurnStyleChanged(object sender, EventArgs e) { if (!_binding) UpdatePreview(); }

    private void OnAccentChanged(object sender, EventArgs e)
    {
        if (AccentSwatch == null || _draft == null) return;
        AccentSwatch.Background = ThemeManager.ParseHex(_draft.AccentColor) is { } c
            ? new System.Windows.Media.SolidColorBrush(c) : System.Windows.Media.Brushes.Transparent;
    }

    private void OnResetAccent(object sender, RoutedEventArgs e)
    {
        _draft.AccentColor = ThemeManager.DefaultAccent;
        Bind(_draft);
    }

    private static System.Windows.Media.Color? ParseHex(string hex)
    {
        hex = (hex ?? "").Trim().TrimStart('#');
        if (hex.Length != 6) return null;
        try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#" + hex); }
        catch { return null; }
    }

    /// <summary>Approximates the burned-in look (font, colours, outline or box) on a sample background.</summary>
    private void UpdatePreview()
    {
        if (_draft == null || BurnPreviewText == null) return;
        var d = _draft;
        var text = ParseHex(d.BurnTextColor) ?? System.Windows.Media.Colors.White;
        var edge = ParseHex(d.BurnOutlineColor) ?? System.Windows.Media.Colors.Black;
        TextSwatch.Background = new System.Windows.Media.SolidColorBrush(text);
        OutlineSwatch.Background = new System.Windows.Media.SolidColorBrush(edge);

        BurnPreviewText.FontFamily = new System.Windows.Media.FontFamily(d.BurnFont);
        BurnPreviewText.FontSize = Math.Clamp(d.BurnFontSize, 8, 72) * 0.85;
        BurnPreviewText.FontWeight = d.BurnBold ? FontWeights.Bold : FontWeights.Normal;
        BurnPreviewText.FontStyle = d.BurnItalic ? FontStyles.Italic : FontStyles.Normal;
        BurnPreviewText.Foreground = new System.Windows.Media.SolidColorBrush(text);

        if (d.BurnStyle == "Box")
        {
            var a = (byte)Math.Round(255 * Math.Clamp(d.BurnBoxOpacity, 0, 100) / 100.0);
            BurnPreviewBox.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, edge.R, edge.G, edge.B));
            BurnPreviewText.Effect = null;
        }
        else
        {
            BurnPreviewBox.Background = System.Windows.Media.Brushes.Transparent;
            BurnPreviewText.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = edge, ShadowDepth = 0, BlurRadius = Math.Max(2, d.BurnOutline * 3), Opacity = 1,
            };
        }
        BurnPreviewBox.VerticalAlignment = d.BurnPosition == "Top" ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        BurnPreviewBox.Margin = d.BurnPosition == "Top" ? new Thickness(0, 10, 0, 0) : new Thickness(0, 0, 0, 10);
    }

    private IInputElement? FocusManager_GetFocused() => System.Windows.Input.FocusManager.GetFocusedElement(this);

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnBrowseFfmpeg(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|Programs|*.exe", Title = "Locate ffmpeg.exe" };
        if (dlg.ShowDialog(this) == true)
        {
            _draft.FfmpegPath = dlg.FileName;
            Bind(_draft);
        }
    }

    private void OnBrowseLibrary(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the recordings library folder" };
        if (dlg.ShowDialog(this) == true)
        {
            _draft.LibraryPath = dlg.FolderName;
            Bind(_draft);
        }
    }

    /// <summary>Points at a different folder without moving anything (models are downloaded there when needed).</summary>
    private void OnBrowseModels(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the models folder (nothing is moved — use “Move models to…” to bring your models along)" };
        if (dlg.ShowDialog(this) == true)
        {
            _draft.ModelsPath = dlg.FolderName;
            Bind(_draft);
        }
    }

    private void UpdateModelsInfo()
    {
        if (ModelsInfo == null) return;
        long bytes = 0;
        var dir = ModelManager.ModelsDir;
        try { if (Directory.Exists(dir)) bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { }
        ModelsInfo.Text = $"Currently using: {dir}  ({bytes / 1048576.0:N0} MB). Changing the path by hand downloads models again " +
                          "when needed; “Move models to…” moves the ones you already have.";
    }

    /// <summary>The library folder the draft settings point at (what Save would switch to).</summary>
    private string DraftLibrary => !string.IsNullOrWhiteSpace(_draft.LibraryPath)
        ? Environment.ExpandEnvironmentVariables(_draft.LibraryPath.Trim().Trim('"'))
        : InstallerIntegration.DefaultLibraryPath ?? LibraryStorage.LocalDefault;

    private void UpdateLibraryInfo()
    {
        if (LibraryInfo == null || _draft == null) return;
        var current = RecordingLibrary.Root;
        var draft = DraftLibrary;
        LibraryInfo.Text = $"Currently using: {current}  ({LibraryStorage.CountRecordings(current)} recordings). " +
                           (LibraryStorage.SamePath(current, draft)
                               ? "Changing the path by hand doesn't move recordings; “Move library to…” does."
                               : $"After Save: {draft}. Recordings are not moved by changing the path; use “Move library to…”.");
        // Warn about the folder that will be used: the current one, or the one just typed/browsed to.
        var warning = PrivacyInfo.LibraryCloudWarning(draft);
        LibraryWarning.Text = warning != null ? "⚠ " + warning : "";
        LibraryWarningBox.Visibility = warning != null ? Visibility.Visible : Visibility.Collapsed;
        MoveLibraryLocalButton.Visibility = warning != null && LibraryStorage.SamePath(current, draft) ? Visibility.Visible : Visibility.Collapsed;
        MoveLibraryLocalButton.ToolTip = $"Copies every recording to {LibraryStorage.LocalDefault}, checks each copy, then removes the originals";
    }

    private void OnLibraryPathEdited(object sender, RoutedEventArgs e) => UpdateLibraryInfo();

    private void OnMoveLibraryLocal(object sender, RoutedEventArgs e) => MoveLibrary(LibraryStorage.LocalDefault);

    private void OnMoveLibrary(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Move the recordings library to…" };
        if (dlg.ShowDialog(this) != true) return;
        if (LibraryStorage.CloudSyncedBy(dlg.FolderName) is { } cloud &&
            MessageBox.Show(this, $"{dlg.FolderName} is inside a {cloud} folder, so {cloud} would upload your recordings. Move there anyway?",
                "Move library", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        MoveLibrary(dlg.FolderName);
    }

    /// <summary>Moves the library (copy, verify, delete) and switches to it immediately.</summary>
    private async void MoveLibrary(string target)
    {
        if (Owner is MainWindow { IsBusy: true })
        {
            MessageBox.Show(this, "Finish the current transcription or live session first.", "Move library", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var source = RecordingLibrary.Root;
        if (MessageBox.Show(this, $"Move {LibraryStorage.CountRecordings(source)} recording(s) from\n{source}\nto\n{target}?\n\n" +
                                  "Every file is copied, checked against the original (SHA-256), and only then removed from the old folder.",
                "Move library", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        IsEnabled = false;
        try
        {
            var r = await RecordingLibrary.MoveLibraryAsync(target, new Progress<string>(s => LibraryInfo.Text = s), CancellationToken.None);
            _draft.LibraryPath = AppSettings.Current.LibraryPath;   // already saved: the files are there now
            Bind(_draft);
            MessageBox.Show(this, $"Moved {r.Copied + r.Skipped} file(s) ({r.Bytes / 1048576.0:N0} MB) to\n{target}." +
                                  (r.Conflicts > 0 ? $"\n\n{r.Conflicts} file(s) already existed there with different content and were left in {source}." : "") +
                                  (r.LeftBehind > 0 ? $"\n\n{r.LeftBehind} old file(s) were in use and could not be removed. Delete them from {source} later." : ""),
                "Move library", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not move the library: " + ex.Message + "\n\nNothing was deleted and the library setting was not changed.",
                "Move library", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateLibraryInfo();
        }
        finally { IsEnabled = true; }
    }

    /// <summary>Moves the downloaded models to a new folder and switches to it immediately.</summary>
    private async void OnMoveModels(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Move models to…" };
        if (dlg.ShowDialog(this) != true) return;
        var target = dlg.FolderName;
        if (MessageBox.Show(this, $"Move all models from\n{ModelManager.ModelsDir}\nto\n{target}?\n\n" +
                                  "Don't start a transcription until it finishes.", "Move models",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        MoveModelsButton.IsEnabled = false;
        IsEnabled = false;
        try
        {
            var (moved, bytes, left) = await ModelManager.MigrateAsync(target, new Progress<string>(s => ModelsInfo.Text = s), CancellationToken.None);
            _draft.ModelsPath = AppSettings.Current.ModelsPath;   // already saved: the files are there now
            Bind(_draft);
            MessageBox.Show(this, $"Moved {moved} file(s) ({bytes / 1048576.0:N0} MB) to\n{target}." +
                                  (left > 0 ? $"\n\n{left} old file(s) were in use and could not be deleted. You can delete them after restarting WhisperDesk." : ""),
                "Move models", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not move the models: " + ex.Message + "\n\nNothing was switched; your models are still in the old folder.",
                "Move models", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateModelsInfo();
        }
        finally
        {
            IsEnabled = true;
            MoveModelsButton.IsEnabled = true;
        }
    }

    private void OnResetPrompt(object sender, RoutedEventArgs e)
    {
        _draft.AiPrompt = AiReport.DefaultPrompt;
        Bind(_draft);
    }

    /// <summary>Checks Ollama with the address/model typed here (temporarily applied, then restored).</summary>
    private async void OnTestAi(object sender, RoutedEventArgs e)
    {
        (FocusManager_GetFocused() as System.Windows.Controls.TextBox)?.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        var cur = AppSettings.Current;
        var (url, model, remote) = (cur.AiUrl, cur.AiModel, cur.AllowRemoteAi);
        cur.AiUrl = _draft.AiUrl; cur.AiModel = _draft.AiModel; cur.AllowRemoteAi = _draft.AllowRemoteAi;
        AiTestButton.IsEnabled = false;
        AiTestResult.Text = $"Asking {_draft.AiModel}… (the first run loads the model, which can take a minute)";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var reply = await AiReport.TestAsync(cts.Token);
            AiTestResult.Text = $"✓ Connected — {_draft.AiModel} replied “{(reply.Length > 40 ? reply[..40] + "…" : reply)}”.";
        }
        catch (Exception ex) { AiTestResult.Text = "✗ " + ex.Message; }
        finally
        {
            cur.AiUrl = url; cur.AiModel = model; cur.AllowRemoteAi = remote;
            AiTestButton.IsEnabled = true;
        }
    }

    private static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void OnOpenModels(object sender, RoutedEventArgs e) => OpenFolder(ModelManager.ModelsDir);

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(WhisperDesk.Services.AppPaths.LocalData));
}
