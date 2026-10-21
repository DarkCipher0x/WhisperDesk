using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>One recording in the report list.</summary>
public sealed class ReportPick : INotifyPropertyChanged
{
    public required RecordingMeta Meta { get; init; }
    private bool _included = true;
    public bool Included { get => _included; set { _included = value; PropertyChanged?.Invoke(this, new(nameof(Included))); } }
    public string Info => $"{Meta.Created:yyyy-MM-dd HH:mm} · {Meta.Duration} · {Meta.SpeakerNames}";
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Reports and summaries across several library recordings (combined or separate, with audit trail and optional local AI).</summary>
public partial class BulkReportWindow : Window
{
    private readonly List<ReportPick> _picks;
    private CancellationTokenSource? _cts;

    public BulkReportWindow(IReadOnlyList<RecordingMeta> recordings)
    {
        InitializeComponent();
        _picks = recordings.OrderBy(r => r.Created).Select(r => new ReportPick { Meta = r }).ToList();
        RecordingsList.ItemsSource = _picks;
        TitleBox.Text = recordings.Count == 1 ? recordings[0].Title : $"Report on {recordings.Count} recordings";
        PreparedByBox.Text = Environment.UserName;
        PromptBox.Text = BulkReport.DefaultAiPrompt;
        SpeakersBox.ItemsSource = recordings.SelectMany(r => r.Speakers).Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        var ai = AppSettings.Current.AiSummaryEnabled;
        AiState.Text = ai
            ? $"Uses {AiReport.ModelName} via Ollama at {AiReport.ServerUrl} ({AiReport.ServerLocation(AiReport.ServerUrl)})."
            : "Local AI is off. Turn it on in Settings → Summary report (needs Ollama). Without it, summaries are written offline.";
        AiOverallBox.IsEnabled = AiPerRecordingBox.IsEnabled = PromptBox.IsEnabled = ai;
        AiOverallBox.IsChecked = ai && recordings.Count > 1;
        UpdateCount();
        Closed += (_, _) => _cts?.Cancel();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);
    private void OnIncludedChanged(object sender, RoutedEventArgs e) => UpdateCount();
    private void OnSelectAll(object sender, RoutedEventArgs e) { foreach (var p in _picks) p.Included = true; UpdateCount(); }
    private void OnSelectNone(object sender, RoutedEventArgs e) { foreach (var p in _picks) p.Included = false; UpdateCount(); }
    private void OnResetPrompt(object sender, RoutedEventArgs e) => PromptBox.Text = BulkReport.DefaultAiPrompt;
    private void OnClose(object sender, RoutedEventArgs e) { if (_cts != null) _cts.Cancel(); else Close(); }

    private void UpdateCount()
    {
        var n = _picks.Count(p => p.Included);
        CountText.Text = $"{n} of {_picks.Count} selected · {TimeSpan.FromSeconds(_picks.Where(p => p.Included).Sum(p => p.Meta.DurationSeconds)):h\\:mm\\:ss}";
    }

    private async void OnGenerate(object sender, RoutedEventArgs e)
    {
        var chosen = _picks.Where(p => p.Included).Select(p => p.Meta).ToList();
        if (chosen.Count == 0) { StatusText.Text = "Tick at least one recording."; return; }
        var formats = new List<string>();
        if (PdfBox.IsChecked == true) formats.Add(".pdf");
        if (DocxBox.IsChecked == true) formats.Add(".docx");
        if (HtmlBox.IsChecked == true) formats.Add(".html");
        if (TxtBox.IsChecked == true) formats.Add(".txt");
        if (JsonBox.IsChecked == true) formats.Add(".json");
        if (formats.Count == 0) { StatusText.Text = "Pick at least one format."; return; }

        var dlg = new OpenFolderDialog { Title = "Choose where to save the report" };
        if (dlg.ShowDialog(this) != true) return;
        var options = new BulkReportOptions
        {
            Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "Recordings report" : TitleBox.Text.Trim(),
            PreparedBy = PreparedByBox.Text.Trim(), Reference = ReferenceBox.Text.Trim(), Purpose = PurposeBox.Text.Trim(),
            Combined = CombinedRadio.IsChecked == true, Zip = ZipBox.IsChecked == true,
            Overview = OverviewBox.IsChecked == true, Summaries = SummariesBox.IsChecked == true, Participation = ParticipationBox.IsChecked == true,
            Transcripts = TranscriptsBox.IsChecked == true, EditLog = EditLogBox.IsChecked == true, Integrity = IntegrityBox.IsChecked == true,
            Keywords = KeywordsBox.Text, OnlySpeakers = SpeakersBox.Text,
            AiOverall = AiOverallBox.IsChecked == true && AiOverallBox.IsEnabled, AiPerRecording = AiPerRecordingBox.IsChecked == true && AiPerRecordingBox.IsEnabled,
            AiPrompt = PromptBox.Text, Formats = formats,
        };
        var folder = Path.Combine(dlg.FolderName, RecordingLibrary.Sanitize(options.Title) + $" {DateTime.Now:yyyy-MM-dd HH.mm}");

        _cts = new CancellationTokenSource();
        GenerateButton.IsEnabled = false;
        CloseButton.Content = "Cancel";
        Busy.Visibility = Visibility.Visible;
        try
        {
            var files = await BulkReport.GenerateAsync(chosen, options, folder, new Progress<string>(s => StatusText.Text = s), _cts.Token);
            StatusText.Text = $"✓ Wrote {files.Count} file(s) to {folder}";
            if (MessageBox.Show(this, $"Report written ({files.Count} files):\n{folder}\n\nOpen the folder?", "Report",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (OperationCanceledException) { StatusText.Text = "Cancelled."; }
        catch (Exception ex)
        {
            App.Log("Bulk report", ex);
            StatusText.Text = "Could not write the report: " + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            GenerateButton.IsEnabled = true;
            CloseButton.Content = "Close";
            Busy.Visibility = Visibility.Collapsed;
        }
    }
}
