using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>Settings → Updates: check, download and verify, install (Services/Updater.cs). These choices are saved at once,
/// in updates.json, not with Save.</summary>
public partial class SettingsWindow
{
    private UpdateInfo? _updInfo;
    private string? _updVerifiedPath;
    private bool _updLoading;

    private void InitUpdates()
    {
        var kind = PackageInfo.IsPackaged ? "Microsoft Store" : Updater.InstalledByMsi ? "installed with the installer" : "portable copy";
        UpdInstalled.Text = $"Installed version {AboutWindow.Version} ({kind}).";
        if (!Updater.Enabled)
        {
            UpdDisabled.Text = "⏸ " + Updater.DisabledMessage + " Nothing here contacts the internet.";
            UpdDisabled.Visibility = Visibility.Visible;
            UpdPanel.Visibility = Visibility.Collapsed;
            return;
        }
        _updLoading = true;
        var s = Updater.Settings;
        UpdSourceBox.SelectedIndex = s.Source == "url" ? 1 : 0;
        UpdEndpointBox.Text = s.Endpoint;
        UpdEndpointRow.Visibility = s.Source == "url" ? Visibility.Visible : Visibility.Collapsed;
        UpdAutoBox.IsChecked = s.AutoCheck;
        UpdIntervalBox.SelectedIndex = s.Interval == "daily" ? 1 : 0;
        UpdAutoDownloadBox.IsChecked = s.AutoDownload;
        UpdAutoDownloadBox.IsEnabled = Updater.InstalledByMsi;
        if (!Updater.InstalledByMsi) UpdAutoDownloadBox.ToolTip = "The portable copy is updated by downloading the new zip from the release page.";
        _updLoading = false;
        UpdTrust.Text = $"Trust: an update is offered only when its update information carries both WhisperDesk release-key signatures pinned in this app " +
                        $"(ECDSA P-256 {Updater.ReleaseKeyX[..8]}…{Updater.ReleaseKeyY[^8..]} and post-quantum ML-DSA-65). GitHub or any other server only " +
                        "carries the files: nothing it changes can pass. The installer must match the signed size and SHA-256 and be WhisperDesk's own MSI " +
                        "at that version. Older versions are never offered. Microsoft Store copies are updated by the Store.";

        // Downloaded and verified in the background by the automatic check (you allowed it): ready to install now.
        if (Updater.Ready is { } ready && File.Exists(ready.Path) && ready.Info.Version > Updater.Installed)
        {
            ShowUpdate(ready.Info);
            _updVerifiedPath = ready.Path;
            UpdResult.Text = $"Version {ready.Info.Version} was downloaded and verified in the background.";
            SetVerify(true, "✓ Verified: ready to install.");
            UpdInstallButton.IsEnabled = Updater.InstalledByMsi;
        }
    }

    private void SaveUpdateSettings()
    {
        try { Updater.SaveSettings(); }
        catch (Exception ex) { App.Log("Update settings", ex); UpdResult.Text = "Couldn't save the update settings: " + ex.Message; }
    }

    private void OnUpdSource(object sender, SelectionChangedEventArgs e)
    {
        if (_updLoading || UpdSourceBox.SelectedItem is not ComboBoxItem { Tag: string v }) return;
        Updater.Settings.Source = v;
        SaveUpdateSettings();
        UpdEndpointRow.Visibility = v == "url" ? Visibility.Visible : Visibility.Collapsed;
        ClearUpdate();
    }

    private void OnUpdSaveEndpoint(object sender, RoutedEventArgs e)
    {
        var v = UpdEndpointBox.Text.Trim();
        if (Updater.ValidateEndpoint(v) is { } bad) { MessageBox.Show(this, bad, "Updates", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Updater.Settings.Endpoint = v;
        SaveUpdateSettings();
        UpdResult.Text = "Address saved.";
    }

    private async void OnUpdCheck(object sender, RoutedEventArgs e)
    {
        ClearUpdate();
        UpdCheckButton.IsEnabled = false;
        UpdResult.Text = "Checking…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var (info, msg) = await Updater.CheckAsync(cts.Token);
            Updater.Settings.LastCheck = DateTime.Now;
            SaveUpdateSettings();
            UpdResult.Text = msg;
            if (info != null) ShowUpdate(info);
        }
        catch (Exception ex) { App.Log("Update check", ex); UpdResult.Text = "The check failed: " + ex.Message; }
        finally { UpdCheckButton.IsEnabled = true; }
    }

    private void ShowUpdate(UpdateInfo i)
    {
        _updInfo = i;
        UpdNotes.Text = (i.Released.Length > 0 ? $"Released {i.Released}\n\n" : "") + (i.Notes.Length > 0 ? i.Notes : "(no release notes)");
        UpdPackage.Text = $"{i.FileName} · {i.Bytes / 1048576.0:0.0} MB\nSHA-256 {i.Sha256}\n✓ update information signed by both WhisperDesk release keys";
        // The portable copy can't be upgraded in place: it gets the release page, where the new zip is.
        UpdDownloadButton.Visibility = UpdInstallButton.Visibility = Updater.InstalledByMsi ? Visibility.Visible : Visibility.Collapsed;
        if (!Updater.InstalledByMsi) SetVerify(true, "This is the portable copy: download the new portable zip from the release page and unzip it over (or beside) this one. Your library and settings stay where they are.");
        UpdDetails.Visibility = Visibility.Visible;
    }

    private void ClearUpdate()
    {
        _updInfo = null; _updVerifiedPath = null;
        UpdDetails.Visibility = Visibility.Collapsed;
        UpdInstallButton.IsEnabled = false;
        UpdVerify.Text = "";
    }

    private void SetVerify(bool ok, string text)
    {
        UpdVerify.Text = text;
        if (ok) UpdVerify.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        else UpdVerify.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xA3, 0x3D));   // the warning amber used in Settings
    }

    private async void OnUpdDownload(object sender, RoutedEventArgs e)
    {
        if (_updInfo is not { } i) return;
        UpdDownloadButton.IsEnabled = UpdCheckButton.IsEnabled = false;
        UpdProgress.Visibility = Visibility.Visible; UpdProgress.Value = 0;
        UpdVerify.Text = "Downloading into a private temp folder…";
        try
        {
            var (path, v) = await Updater.DownloadAndVerifyAsync(i, new Progress<double>(p => UpdProgress.Value = p), CancellationToken.None);
            SetVerify(v.Ok, (v.Ok ? "✓ " : "✕ ") + v.Message);
            _updVerifiedPath = path;
            UpdInstallButton.IsEnabled = v.Ok && path != null;
            App.LogEvent($"Update: {i.Version} downloaded, {v.Verdict}");
        }
        catch (Exception ex) { App.Log("Update download", ex); SetVerify(false, "✕ The download failed: " + ex.Message); }
        finally { UpdDownloadButton.IsEnabled = UpdCheckButton.IsEnabled = true; UpdProgress.Visibility = Visibility.Collapsed; }
    }

    private void OnUpdInstall(object sender, RoutedEventArgs e)
    {
        if (_updInfo is not { } i || _updVerifiedPath == null) return;
        if (Owner is MainWindow { IsBusy: true })
        {
            MessageBox.Show(this, "A live session or a transcription is running. Finish or stop it first, then install the update.", "Install update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Checked again right before it runs (nothing may have changed since the download).
        var again = Updater.Verify(_updVerifiedPath, i);
        if (!again.Ok) { SetVerify(false, "✕ The downloaded installer no longer verifies: " + again.Message); UpdInstallButton.IsEnabled = false; return; }
        if (MessageBox.Show(this, $"WhisperDesk will close and Windows Installer will upgrade it from {AboutWindow.Version} to {i.Version} (Windows asks for permission). " +
                $"Your library, voices, models and settings stay where they are.\n\nWhen WhisperDesk next starts it checks that {i.Version} is really running.\n\nClose and install now?",
                "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { Updater.LaunchInstaller(_updVerifiedPath, i); }
        catch (Exception ex) { App.Log("Update install", ex); MessageBox.Show(this, "The update wasn't started: " + ex.Message, "Install update", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var main = Owner;
        DialogResult = false;
        main?.Close();
    }

    private void OnUpdReleasePage(object sender, RoutedEventArgs e)
    {
        var url = _updInfo?.ReleasePage ?? Updater.ReleasesPage;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log("Release page", ex); }
    }

    private void OnUpdAuto(object sender, RoutedEventArgs e)
    {
        Updater.Settings.AutoCheck = UpdAutoBox.IsChecked == true;
        SaveUpdateSettings();
    }

    private void OnUpdInterval(object sender, SelectionChangedEventArgs e)
    {
        if (_updLoading || UpdIntervalBox.SelectedItem is not ComboBoxItem { Tag: string v }) return;
        Updater.Settings.Interval = v;
        SaveUpdateSettings();
    }

    private void OnUpdAutoDownload(object sender, RoutedEventArgs e)
    {
        Updater.Settings.AutoDownload = UpdAutoDownloadBox.IsChecked == true;
        SaveUpdateSettings();
    }
}
