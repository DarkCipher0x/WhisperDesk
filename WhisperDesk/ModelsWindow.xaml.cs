using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>
/// Tools → Models → Manage models: every model in the signed model list (<see cref="ModelCatalog"/>) with its state,
/// download / update / remove, "Check for new models" and "Update all". Downloads keep running if the window closes.
/// </summary>
public partial class ModelsWindow : Window
{
    private readonly ObservableCollection<Row> _rows = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private static readonly Brush Amber = Frozen(Color.FromRgb(0xE8, 0xA3, 0x3D));

    public ModelsWindow()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        _timer.Tick += (_, _) => RefreshProgress();
        ModelCatalog.Changed += OnCatalogChanged;
        Loaded += async (_, _) => { Build(); _timer.Start(); await CheckFilesAsync(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ModelCatalog.Changed -= OnCatalogChanged;
        _timer.Stop();
        _cts.Cancel();   // only the file checks; downloads carry on in the background
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnCatalogChanged() => Dispatcher.BeginInvoke(() => { if (IsLoaded) { Build(); _ = CheckFilesAsync(); } });

    private bool MainBusy => Owner is MainWindow { IsBusy: true };

    // ---------------------------------------------- rows ----------------------------------------------

    private void Build()
    {
        var cat = ModelCatalog.Current;
        CatalogInfo.Text = $"Model list version {cat.Sequence}" + (cat.Published.Length > 0 ? $" ({cat.Published})" : "") +
                           (cat.Source == "built in" ? $", built into WhisperDesk {AboutWindow.Version}" : $", {cat.Source} from WhisperDesk's releases, signed") +
                           $" · {cat.Models.Count(m => m.IsWhisper)} speech models · folder {ModelManager.ModelsDir}";
        _rows.Clear();
        _rows.Add(Row.Head("SPEECH RECOGNITION (WHISPER)"));
        foreach (var m in cat.Models.Where(m => m.IsWhisper)) _rows.Add(new Row(m));
        _rows.Add(Row.Head("SPEAKERS AND VOICE DETECTION (LIVE SESSIONS, SPEAKER LABELS)"));
        foreach (var m in cat.Models.Where(m => !m.IsWhisper)) _rows.Add(new Row(m));
        foreach (var r in _rows.Where(r => r.Model != null)) Show(r, ModelCatalog.QuickState(r.Model!));
        UpdateButtons();
    }

    /// <summary>Files not checked yet are hashed once, one at a time, in the background.</summary>
    private async Task CheckFilesAsync()
    {
        foreach (var r in _rows.Where(r => r.Model != null && r.State == ModelState.Checking && !ModelCatalog.IsDownloading(r.Model)).ToList())
        {
            try
            {
                r.Status = "Checking the file…";
                Show(r, await ModelCatalog.CheckFileAsync(r.Model!, _cts.Token));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { r.Status = "Couldn't check: " + ex.Message; }
        }
        UpdateButtons();
    }

    private void Show(Row r, ModelState state)
    {
        r.State = state;
        r.StatusBrush = (Brush)FindResource("Fg");
        r.ActionEnabled = true;
        r.RemoveVisibility = Visibility.Collapsed;
        switch (state)
        {
            case ModelState.NotDownloaded:
                r.Status = "Not downloaded"; r.ActionText = "Download"; r.ActionVisibility = Visibility.Visible;
                r.StatusBrush = (Brush)FindResource("Muted");
                break;
            case ModelState.Checking:
                r.Status = ModelCatalog.IsDownloading(r.Model!) ? "Downloading…" : "Not checked yet";
                r.ActionText = "Download"; r.ActionEnabled = false; r.ActionVisibility = Visibility.Visible;
                break;
            case ModelState.UpToDate:
                r.Status = "✓ Downloaded, up to date"; r.ActionVisibility = Visibility.Collapsed; r.RemoveVisibility = Visibility.Visible;
                r.StatusBrush = (Brush)FindResource("Accent");
                break;
            case ModelState.UpdateAvailable:
                r.Status = "Update available"; r.ActionText = "Update"; r.ActionVisibility = Visibility.Visible;
                r.RemoveVisibility = Visibility.Visible; r.StatusBrush = Amber;
                break;
            case ModelState.Damaged:
                r.Status = "✕ Doesn't match its SHA-256 (damaged or replaced): not used"; r.ActionText = "Download again";
                r.ActionVisibility = Visibility.Visible; r.RemoveVisibility = Visibility.Visible; r.StatusBrush = Amber;
                break;
        }
    }

    private void RefreshProgress()
    {
        foreach (var r in _rows.Where(r => r.Model != null))
        {
            if (ModelCatalog.DownloadedBytes(r.Model!) is { } done)
            {
                r.Status = $"Downloading {done * 100 / Math.Max(1, r.Model!.Bytes)}% ({ModelManager.FormatSize(done)} of {r.Model.Size})";
                r.ActionText = "Downloading…"; r.ActionEnabled = false; r.ActionVisibility = Visibility.Visible;
                r.RemoveVisibility = Visibility.Collapsed;
                r.WasDownloading = true;
            }
            else if (r.WasDownloading)
            {
                r.WasDownloading = false;
                Show(r, ModelCatalog.QuickState(r.Model!));
                UpdateButtons();
            }
        }
    }

    private void UpdateButtons()
    {
        var updates = _rows.Count(r => r.State == ModelState.UpdateAvailable);
        UpdateAllButton.IsEnabled = updates > 0;
        UpdateAllButton.Content = updates > 0 ? $"Update all ({updates})" : "Update all";
        CleanButton.IsEnabled = ModelCatalog.Unused().Count > 0;
    }

    // ---------------------------------------------- actions ----------------------------------------------

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Row { Model: { } m } r) return;
        await DownloadAsync(r, m);
    }

    private async Task DownloadAsync(Row r, CatalogModel m)
    {
        // A new model is safe to fetch at any time; replacing one could swap a file a running job is about to read.
        if (r.State != ModelState.NotDownloaded && MainBusy)
        {
            StatusLine.Text = "A transcription or live session is running: update models when it has finished.";
            return;
        }
        r.ActionEnabled = false;
        r.Status = "Starting…";
        try
        {
            await ModelCatalog.DownloadAsync(m, null, CancellationToken.None);
            StatusLine.Text = $"{m.Name}: downloaded and verified (SHA-256 {m.Sha256[..12]}…).";
            App.LogEvent($"Model {m.Id} downloaded ({m.Bytes} bytes)");
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"{m.Name}: {ex.Message}";
            if (ex is not Services.Security.NetworkBlockedException) App.Log("Model download", ex);
        }
        r.WasDownloading = false;
        Show(r, await ModelCatalog.CheckFileAsync(m, CancellationToken.None));
        UpdateButtons();
    }

    private async void OnUpdateAll(object sender, RoutedEventArgs e)
    {
        if (MainBusy) { StatusLine.Text = "A transcription or live session is running: update models when it has finished."; return; }
        var todo = _rows.Where(r => r.State == ModelState.UpdateAvailable && r.Model != null).ToList();
        if (todo.Count == 0) return;
        var total = todo.Sum(r => r.Model!.Bytes);
        if (MessageBox.Show(this, $"Download {todo.Count} model update(s), {ModelManager.FormatSize(total)} in total?\n\n" +
                string.Join("\n", todo.Select(r => $"• {r.Model!.Name} ({r.Model.Size})")) +
                "\n\nThe current files stay in use until each new one has downloaded and passed its SHA-256 check.",
                "Update models", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        UpdateAllButton.IsEnabled = false;
        foreach (var r in todo) await DownloadAsync(r, r.Model!);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Row { Model: { } m } r) return;
        if (MainBusy) { StatusLine.Text = "A transcription or live session is running: remove models when it has finished."; return; }
        if (MessageBox.Show(this, $"Delete {m.Name} ({m.Size}) from the models folder?\n\nIt downloads again the next time it is needed.",
                "Remove model", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { ModelCatalog.Remove(m); StatusLine.Text = $"{m.Name} removed."; }
        catch (Exception ex) { StatusLine.Text = $"{m.Name} couldn't be removed: {ex.Message}"; }
        Show(r, ModelCatalog.QuickState(m));
        UpdateButtons();
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        StatusLine.Text = "Checking for a newer model list…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var result = await ModelCatalog.CheckAsync(cts.Token);
            StatusLine.Text = result.Message;
            App.LogEvent("Model list check: " + result.Message);
        }
        catch (Services.Security.NetworkBlockedException ex) { StatusLine.Text = "The check was blocked: " + ex.Message; }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            StatusLine.Text = "Couldn't reach GitHub: " + (ex is TaskCanceledException ? "it timed out." : ex.Message);
        }
        catch (Exception ex) { App.Log("Model list check", ex); StatusLine.Text = "The check failed: " + ex.Message; }
        finally { CheckButton.IsEnabled = true; }
    }

    private void OnClean(object sender, RoutedEventArgs e)
    {
        var files = ModelCatalog.Unused();
        if (files.Count == 0) { UpdateButtons(); return; }
        if (MainBusy) { StatusLine.Text = "A transcription or live session is running: remove files when it has finished."; return; }
        if (MessageBox.Show(this, "These model files aren't used by any model in the list any more:\n\n" +
                string.Join("\n", files.Select(f => $"• {Path.GetRelativePath(ModelManager.ModelsDir, f.FullName)} ({ModelManager.FormatSize(f.Length)})")) +
                $"\n\nDelete them ({ModelManager.FormatSize(files.Sum(f => f.Length))})? Imported custom models are not touched.",
                "Remove old model files", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int done = 0;
        foreach (var f in files) { try { f.Delete(); done++; } catch { } }
        StatusLine.Text = $"Removed {done} of {files.Count} old file(s).";
        UpdateButtons();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ModelManager.ModelsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ModelManager.ModelsDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { StatusLine.Text = ex.Message; }
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>One line in the list: a group heading or a model.</summary>
    public sealed class Row : Observable
    {
        public Row(CatalogModel? model) { Model = model; }
        public static Row Head(string text) => new(null) { Heading = text };

        public CatalogModel? Model { get; }
        public string Heading { get; private init; } = "";
        public Visibility HeadingVisibility => Model == null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RowVisibility => Model == null ? Visibility.Collapsed : Visibility.Visible;
        public string Name => Model?.Name ?? "";
        public string Notes => Model?.Notes ?? "";
        public string Size => Model?.Size ?? "";
        public ModelState State { get; set; }
        public bool WasDownloading { get; set; }

        private string _status = "", _actionText = "Download";
        private bool _actionEnabled = true;
        private Visibility _actionVisibility = Visibility.Collapsed, _removeVisibility = Visibility.Collapsed;
        private Brush? _statusBrush;
        public string Status { get => _status; set => Set(ref _status, value); }
        public Brush? StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
        public string ActionText { get => _actionText; set => Set(ref _actionText, value); }
        public bool ActionEnabled { get => _actionEnabled; set => Set(ref _actionEnabled, value); }
        public Visibility ActionVisibility { get => _actionVisibility; set => Set(ref _actionVisibility, value); }
        public Visibility RemoveVisibility { get => _removeVisibility; set => Set(ref _removeVisibility, value); }
    }
}
