using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>Shows the offline call summary and saves it in every format.</summary>
public partial class SummaryWindow : Window
{
    private readonly TranscriptResult _result;
    private readonly string _title;
    private CallSummary _summary;
    private CancellationTokenSource? _cts;

    public SummaryWindow(TranscriptResult result, string title)
    {
        InitializeComponent();
        _result = result;
        _title = title;
        _summary = CallSummary.Build(result, title);   // offline report straight away
        TitleText.Text = "Summary · " + title;
        OverviewText.Text = $"{_summary.Date} · {_summary.OverviewText}";
        SummaryBox.Text = _summary.ToText();
        RegenerateButton.Visibility = AppSettings.Current.AiSummaryEnabled ? Visibility.Visible : Visibility.Collapsed;
        ShowProvenance();
        Loaded += async (_, _) => { if (AppSettings.Current.AiSummaryEnabled) await GenerateAiAsync(); };
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task GenerateAiAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        RegenerateButton.IsEnabled = SaveAllButton.IsEnabled = false;
        try
        {
            var s = await CallSummary.BuildAsync(_result, _title, null, new Progress<string>(m => AiStatus.Text = m), ct);
            if (ct.IsCancellationRequested) return;
            _summary = s;
            SummaryBox.Text = s.ToText();
            AiStatus.Text = s.ReportNote ?? $"✓ Report written by {s.ReportBy}.";
            ShowProvenance();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AiStatus.Text = "AI report failed: " + ex.Message; }
        finally
        {
            if (!ct.IsCancellationRequested) RegenerateButton.IsEnabled = SaveAllButton.IsEnabled = true;
        }
    }

    private async void OnRegenerate(object sender, RoutedEventArgs e) => await GenerateAiAsync();

    /// <summary>Footer: exactly what produced the report, where it ran and for which account (also at the end of every export).</summary>
    private void ShowProvenance()
    {
        string Get(string label) => _summary.Provenance.FirstOrDefault(p => p.Label == label).Value ?? "";
        FooterText.Text = $"Report: {Get("Report written by")}" +
                          (Get("AI processed on") is { Length: > 0 } where ? $" · processed on {where}" : " · processed on this PC") +
                          $" · {Get("Computer")} · {Get("Windows account")} · {Get("Software")}." +
                          (AppSettings.Current.AiSummaryEnabled ? "" : " For an AI-written report: Settings → Summary report.");
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    private void OnCopy(object sender, RoutedEventArgs e) => SafeClipboard.SetText(SummaryBox.Text, this);

    private async void OnSaveAll(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to save the summary (TXT, Word, PDF, JSON)" };
        if (_result.OutputDir != null && Directory.Exists(_result.OutputDir)) dlg.InitialDirectory = _result.OutputDir;
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var basePath = Path.Combine(dlg.FolderName, RecordingLibrary.Sanitize(_summary.Title) + ".summary");
            var files = await TranscriptWriter.WriteSummaryAllAsync(_result, _summary.Title, basePath, _summary);
            if (MessageBox.Show(this, "Saved:\n" + string.Join("\n", files.Select(Path.GetFileName)) + "\n\nOpen the folder?", "Summary",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start("explorer.exe", $"/select,\"{files[2]}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the summary: " + ex.Message, "Summary", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
