using System.IO;
using System.Windows;

namespace WhisperDesk;

/// <summary>The pop-out chat-style live feed (View → Live feed, Ctrl+Shift+F).</summary>
public partial class MainWindow
{
    private CaptionFeedWindow? _feed;

    public void SetStatus(string text) => StatusText.Text = text;

    private void OnVoiceAnalysis(object sender, RoutedEventArgs e) =>
        new VoiceAnalysisWindow(_speakerAnalyzer, _shown) { Owner = this }.Show();

    private void OnBulkReportMenu(object sender, RoutedEventArgs e)
    {
        var list = _library.SelectedRecordings.Count > 1 ? _library.SelectedRecordings : WhisperDesk.Services.RecordingLibrary.All();
        if (list.Count == 0) { StatusText.Text = "The library is empty: save some recordings first."; return; }
        new BulkReportWindow(list) { Owner = this }.Show();
    }

    private void OnOpenCaptions(object sender, RoutedEventArgs e)
    {
        var entry = _addons.Addons.FirstOrDefault(a => a.Id == "whisperdesk.caption-server");
        if (entry is not { Enabled: true })
        {
            MessageBox.Show(this, "Switch on “Live captions for OBS & browsers” in Tools → Add-ons first.", "Captions", MessageBoxButton.OK, MessageBoxImage.Information);
            OnAddons(sender, e);
            return;
        }
        var port = WhisperDesk.Services.Addons.BuiltIn.CaptionServerAddon.ParsePort(
            WhisperDesk.Services.AppSettings.Current.AddonOptions.TryGetValue(entry.Id, out var o) && o.TryGetValue("port", out var p) ? p : null);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://localhost:{port}/") { UseShellExecute = true });
    }

    private void OnBackup(object sender, RoutedEventArgs e) => BackupDialogs.ShowBackup(this);

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_cts != null || LiveRunning) { StatusText.Text = "Wait for the current job to finish first."; return; }
        BackupDialogs.ShowRestore(this, () =>
        {
            _library.Refresh();
            WhisperDesk.Services.VoiceLibrary.Shared.RefreshRecordingNames();
            StatusText.Text = "Restore finished.";
        });
    }

    private void OnOpenFeed(object sender, RoutedEventArgs e)
    {
        if (_feed != null) { _feed.Activate(); return; }
        _feed = new CaptionFeedWindow();
        _feed.Closed += (_, _) => _feed = null;
        UpdateFeed();
        _feed.Show();
    }

    /// <summary>Points the feed at the live session while one runs, otherwise at the transcript in the pane.</summary>
    private void UpdateFeed()
    {
        if (_feed == null) return;
        if (_liveResult != null && (LiveRunning || _shown == _liveResult)) _feed.SetSource(_liveResult, "● LIVE");
        else if (_shown != null) _feed.SetSource(_shown, _shown.Library?.Title ?? Path.GetFileNameWithoutExtension(_shown.SourceFile));
        else _feed.SetSource(null, "");
    }
}
