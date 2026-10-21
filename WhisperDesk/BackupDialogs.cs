using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>File → Backup… and File → Restore from backup… (plain .zip or AES-256 encrypted .wdbackup).</summary>
public static class BackupDialogs
{
    private static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(22, 0, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    public static void ShowBackup(Window owner)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = "Makes one file with everything you tick. Keep it on another drive or in safe storage. With a password it is encrypted (AES-256), " +
                   "so it is safe to store in the cloud or on a USB stick.",
        });
        var boxes = new List<(CheckBox Box, BackupItem Item)>();
        foreach (var item in Backup.Items())
        {
            var cb = new CheckBox { Content = item.Label + "  (…)", IsChecked = item.Key != "models", Margin = new Thickness(0, 3, 0, 2) };
            boxes.Add((cb, item));
            panel.Children.Add(cb);
            panel.Children.Add(Hint(item.Path));
            _ = Task.Run(() => item.Size).ContinueWith(t => cb.Dispatcher.Invoke(() => cb.Content = $"{item.Label}  ({ModelManager.FormatSize(t.Result)})"));
        }

        var encrypt = new CheckBox { Content = "Encrypt with a password (recommended)", IsChecked = true, Margin = new Thickness(0, 10, 0, 6), FontWeight = FontWeights.SemiBold };
        var pw1 = new PasswordBox { Margin = new Thickness(0, 2, 0, 6), Padding = new Thickness(5, 3, 5, 3) };
        var pw2 = new PasswordBox { Margin = new Thickness(0, 2, 0, 4), Padding = new Thickness(5, 3, 5, 3) };
        var pwPanel = new StackPanel { Margin = new Thickness(22, 0, 0, 0) };
        pwPanel.Children.Add(new TextBlock { Text = "Password (at least 8 characters)" });
        pwPanel.Children.Add(pw1);
        pwPanel.Children.Add(new TextBlock { Text = "Repeat the password" });
        pwPanel.Children.Add(pw2);
        var warn = new TextBlock { Text = "⚠ There is no way to recover a forgotten password. Store it in a password manager.", TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
        warn.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        pwPanel.Children.Add(warn);
        encrypt.Checked += (_, _) => pwPanel.IsEnabled = true;
        encrypt.Unchecked += (_, _) => pwPanel.IsEnabled = false;
        panel.Children.Add(encrypt);
        panel.Children.Add(pwPanel);
        var status = new TextBlock { Margin = new Thickness(0, 12, 0, 10), TextWrapping = TextWrapping.Wrap };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(status);

        var win = SpeakerDialogs.MakeWindow(owner, "Back up WhisperDesk", panel, out var ok, "Back up…");
        win.Width = 560;
        CancellationTokenSource? cts = null;
        win.Closing += (_, e) => { if (cts != null) { cts.Cancel(); e.Cancel = true; } };
        ok.Click += async (_, _) =>
        {
            var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Item).ToList();
            if (chosen.Count == 0) { status.Text = "Tick at least one item."; return; }
            string? password = null;
            if (encrypt.IsChecked == true)
            {
                if (pw1.Password.Length < 8) { status.Text = "The password must be at least 8 characters."; return; }
                if (pw1.Password != pw2.Password) { status.Text = "The two passwords don't match."; return; }
                password = pw1.Password;
            }
            var ext = password != null ? Backup.EncryptedExtension : ".zip";
            var dlg = new SaveFileDialog
            {
                Title = "Save the backup", FileName = $"WhisperDesk backup {DateTime.Now:yyyy-MM-dd HH.mm}{ext}",
                Filter = password != null ? "Encrypted WhisperDesk backup (*.wdbackup)|*.wdbackup" : "Zip archive (*.zip)|*.zip",
            };
            if (dlg.ShowDialog(win) != true) return;
            if (chosen.Any(c => c.Key == "library") && Path.GetFullPath(dlg.FileName).StartsWith(Path.GetFullPath(RecordingLibrary.Root), StringComparison.OrdinalIgnoreCase))
            {
                status.Text = "Save the backup outside the library folder.";
                return;
            }
            cts = new CancellationTokenSource();
            ok.IsEnabled = false;
            try
            {
                var (files, bytes) = await Backup.CreateAsync(dlg.FileName, chosen, password, new Progress<string>(s => status.Text = s), cts.Token);
                status.Text = $"✓ Backed up {files:N0} files ({ModelManager.FormatSize(bytes)}) to {dlg.FileName}";
                cts = null;
                if (MessageBox.Show(win, $"Backup complete: {files:N0} files, {ModelManager.FormatSize(bytes)}" +
                                         (password != null ? ", encrypted with AES-256." : ".") + "\n\nShow it in Explorer?",
                        "Backup", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
                win.DialogResult = true;
            }
            catch (OperationCanceledException) { status.Text = "Cancelled."; }
            catch (Exception ex) { App.Log("Backup", ex); status.Text = "Backup failed: " + ex.Message; }
            finally { cts = null; ok.IsEnabled = true; }
        };
        win.ShowDialog();
    }

    public static async void ShowRestore(Window owner, Action afterRestore)
    {
        var open = new OpenFileDialog { Title = "Choose a WhisperDesk backup", Filter = "WhisperDesk backups (*.wdbackup;*.zip)|*.wdbackup;*.zip" };
        if (open.ShowDialog(owner) != true) return;
        string? password = null;
        if (Backup.IsEncrypted(open.FileName))
        {
            var pw = new PasswordBox { Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(5, 3, 5, 3) };
            var p = new StackPanel { Margin = new Thickness(18) };
            p.Children.Add(new TextBlock { Text = $"“{Path.GetFileName(open.FileName)}” is encrypted. Enter its password:", TextWrapping = TextWrapping.Wrap });
            p.Children.Add(pw);
            var pwin = SpeakerDialogs.MakeWindow(owner, "Encrypted backup", p, out var pok, "Unlock");
            pok.Click += (_, _) => pwin.DialogResult = true;
            pwin.Loaded += (_, _) => pw.Focus();
            if (pwin.ShowDialog() != true) return;
            password = pw.Password;
        }

        ZipArchive zip;
        string? temp;
        var status = new Progress<string>(s => ((MainWindow)owner).SetStatus(s));
        try { (zip, temp) = await Backup.OpenAsync(open.FileName, password, status, CancellationToken.None); }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var counts = zip.Entries.Where(e => e.FullName.Contains('/')).GroupBy(e => e.FullName[..e.FullName.IndexOf('/')])
                .ToDictionary(g => g.Key, g => (Files: g.Count(), Bytes: g.Sum(e => e.Length)));
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = "Choose what to restore from this backup:", Margin = new Thickness(0, 0, 0, 10) });
            var boxes = new List<(CheckBox, string)>();
            foreach (var item in Backup.Items().Where(i => counts.ContainsKey(i.Key)))
            {
                var (files, bytes) = counts[item.Key];
                var cb = new CheckBox { Content = $"{item.Label} — {files:N0} files, {ModelManager.FormatSize(bytes)}", IsChecked = item.Key is "library" or "voices", Margin = new Thickness(0, 3, 0, 3) };
                boxes.Add((cb, item.Key));
                panel.Children.Add(cb);
            }
            panel.Children.Add(Hint($"Recordings go into your current library ({RecordingLibrary.Root}). Voices are merged with your voice library. " +
                                    "Restoring settings replaces your current settings."));
            var overwrite = new CheckBox { Content = "Overwrite recordings and files that already exist (otherwise they are skipped)", Margin = new Thickness(0, 8, 0, 12) };
            panel.Children.Add(overwrite);
            var win = SpeakerDialogs.MakeWindow(owner, "Restore from backup", panel, out var ok, "Restore");
            win.Width = 560;
            ok.Click += (_, _) => win.DialogResult = true;
            if (win.ShowDialog() != true) return;

            var keys = boxes.Where(b => b.Item1.IsChecked == true).Select(b => b.Item2).ToList();
            var result = await Backup.RestoreAsync(zip, keys, overwrite.IsChecked == true, RecordingLibrary.Root, status, CancellationToken.None);
            afterRestore();
            MessageBox.Show(owner,
                $"Restored {result.Restored:N0} file(s), skipped {result.Skipped:N0} that already existed.\n" +
                $"Integrity: {result.Verified:N0} verified with SHA-256" + (result.Mismatched > 0 ? $", ⚠ {result.Mismatched} did NOT match" : "") + "." +
                (result.Notes.Count > 0 ? "\n\n" + string.Join("\n", result.Notes.Take(12)) : ""),
                "Restore", MessageBoxButton.OK, result.Mismatched > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            App.Log("Restore", ex);
            MessageBox.Show(owner, "Restore failed: " + ex.Message, "Restore", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            zip.Dispose();
            if (temp != null) Services.Security.WorkDir.Release(Path.GetDirectoryName(temp));   // the decrypted copy never stays on disk
        }
    }
}
