using System.IO;
using System.Windows;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>Start-up banner: the library is in a cloud-synced folder, or recordings were found in the old Documents library.</summary>
public partial class MainWindow
{
    private enum StorageIssue { None, CloudLibrary, StrayLibrary }
    private StorageIssue _storageIssue;
    private string _storageKey = "";

    /// <summary>A transcription or live session is running (the library must not be moved now).</summary>
    public bool IsBusy => _cts != null || _live != null;

    /// <summary>Shows the banner for the most important storage issue that hasn't been dismissed (call after the library changes).</summary>
    public void UpdateStorageBanner()
    {
        _storageIssue = StorageIssue.None;
        var root = RecordingLibrary.Root;
        var dismissed = AppSettings.Current.DismissedStorageWarnings;
        if (PrivacyInfo.LibraryCloudWarning(root) is { } warning && !dismissed.Contains("cloud|" + root))
        {
            _storageIssue = StorageIssue.CloudLibrary;
            _storageKey = "cloud|" + root;
            StorageText.Text = "⚠ " + warning;
            StorageAction.Content = "Move library to a local folder";
            StorageAction.ToolTip = $"Copies every recording to {LibraryStorage.LocalDefault}, checks each copy, then removes the originals";
        }
        else if (RecordingLibrary.StrayLibrary() is { } stray)
        {
            _storageIssue = StorageIssue.StrayLibrary;
            _storageKey = "stray|" + stray.Dir;
            var cloud = LibraryStorage.CloudSyncedBy(stray.Dir);
            StorageText.Text = $"⚠ Found {stray.Count} recording(s) in {stray.Dir}, the folder older versions used" +
                               (cloud != null ? $". It is synced by {cloud}, so they may come from another PC and are still being uploaded." : ".") +
                               " They are not in your library, which is now kept locally in " + root + ".";
            StorageAction.Content = "Move them into my library";
            StorageAction.ToolTip = $"Copies them into {root}, checks each copy, then removes them from {stray.Dir}";
        }
        StorageBanner.Visibility = _storageIssue == StorageIssue.None ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnStorageDismiss(object sender, RoutedEventArgs e)
    {
        if (_storageKey.Length > 0 && !AppSettings.Current.DismissedStorageWarnings.Contains(_storageKey))
        {
            AppSettings.Current.DismissedStorageWarnings.Add(_storageKey);
            AppSettings.Current.Save();
        }
        UpdateStorageBanner();
    }

    private async void OnStorageAction(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            MessageBox.Show(this, "Finish the current transcription or live session first.", "Move library", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var root = RecordingLibrary.Root;
        var (source, target) = _storageIssue == StorageIssue.CloudLibrary
            ? (root, LibraryStorage.LocalDefault)
            : (RecordingLibrary.StrayLibrary()?.Dir ?? "", root);
        if (source.Length == 0) { UpdateStorageBanner(); return; }

        var count = LibraryStorage.CountRecordings(source);
        if (MessageBox.Show(this, $"Move {count} recording(s) from\n{source}\nto\n{target}?\n\n" +
                                  "Every file is copied, checked against the original (SHA-256), and only then removed from the old folder. " +
                                  "Files that exist only in the cloud are downloaded first, so this can take a while.",
                "Move library", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        StorageAction.IsEnabled = StorageDismiss.IsEnabled = false;
        MainTabs.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(s => StatusText.Text = s);
            var r = _storageIssue == StorageIssue.CloudLibrary
                ? await RecordingLibrary.MoveLibraryAsync(target, progress, CancellationToken.None)
                : await LibraryStorage.MoveAsync(source, target, progress, CancellationToken.None);
            var notes = (r.Conflicts > 0 ? $"\n\n{r.Conflicts} file(s) already existed in the new folder with different content and were left in the old folder." : "") +
                        (r.LeftBehind > 0 ? $"\n\n{r.LeftBehind} old file(s) were in use and could not be removed. Delete them from {source} later." : "");
            StatusText.Text = $"Library moved to {target}.";
            MessageBox.Show(this, $"Moved {r.Copied + r.Skipped} file(s) ({r.Bytes / 1048576.0:N0} MB) to\n{target}.{notes}" +
                                  (LibraryStorage.CloudSyncedBy(source) is { } c ? $"\n\n{c} may keep deleted files in its recycle bin online for a while; empty it there if needed." : ""),
                "Move library", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "The library was not moved.";
            MessageBox.Show(this, "Could not move the library: " + ex.Message + "\n\nNothing was deleted and the library setting was not changed.",
                "Move library", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            MainTabs.IsEnabled = true;
            StorageAction.IsEnabled = StorageDismiss.IsEnabled = true;
            _library.Refresh();
            VoiceLibrary.Shared.RefreshRecordingNames();
            UpdateStorageBanner();
        }
    }
}
