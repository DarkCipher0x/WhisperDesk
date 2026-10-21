using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using WhisperDesk.Services;

namespace WhisperDesk;

public sealed class SpeakerCheck : INotifyPropertyChanged
{
    public required SpeakerInfo Speaker { get; init; }
    public string Name => Speaker.Label;
    public string Info { get; init; } = "";
    private bool _selected = true;
    public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Tools → Voice &amp; audio analysis: voice profiles, voice comparison and audio integrity, as an exportable report.</summary>
public partial class VoiceAnalysisWindow : Window
{
    private sealed record Source(string Label, RecordingMeta? Meta, TranscriptResult? Result)
    {
        public override string ToString() => Label;
    }
    private sealed record VoiceRef(string Label, SpeakerInfo? Speaker, KnownVoice? Known)
    {
        public override string ToString() => Label;
    }

    private readonly SpeakerAnalyzer _analyzer;
    private TranscriptResult? _result;
    private RecordingMeta? _meta;
    private float[]? _samples;
    private readonly List<VoiceProfile> _profiles = new();
    private readonly List<VoiceComparison> _comparisons = new();
    private IntegrityReport? _integrity;
    private bool _webReady;

    public VoiceAnalysisWindow(SpeakerAnalyzer analyzer, TranscriptResult? current)
    {
        InitializeComponent();
        _analyzer = analyzer;
        DisclaimerText.Text = "ⓘ " + VoiceForensics.Disclaimer;
        var sources = new List<Source>();
        if (current is { Lines.Count: > 0 })
            sources.Add(new Source($"Transcript in the pane: {current.Library?.Title ?? Path.GetFileNameWithoutExtension(current.SourceFile)}", current.Library, current));
        foreach (var m in RecordingLibrary.All())
            if (current?.Library?.Id != m.Id) sources.Add(new Source($"{m.Title}  ·  {m.Created:yyyy-MM-dd}", m, null));
        RecordingBox.ItemsSource = sources;
        Loaded += async (_, _) =>
        {
            try
            {
                // The report preview is static HTML: no scripts, InPrivate, nothing navigates away.
                await Services.Security.SafeWebView.InitAsync(Preview, scripts: false, allowed: null);
                _webReady = true;
            }
            catch (Exception ex) { App.Log("Analysis preview", ex); }
            if (sources.Count > 0) RecordingBox.SelectedIndex = 0;
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy, string? text = null)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProfileButton.IsEnabled = CompareButton.IsEnabled = CompareAllButton.IsEnabled = IntegrityButton.IsEnabled = RecordingBox.IsEnabled = !busy;
        if (text != null) StatusText.Text = text;
    }

    private void OnRecordingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordingBox.SelectedItem is not Source s) return;
        try
        {
            _result = s.Result ?? RecordingLibrary.Load(s.Meta!);
            _meta = s.Meta ?? _result.Library;
            _samples = null;
            _profiles.Clear(); _comparisons.Clear(); _integrity = null;
            var media = _result.MediaPath;
            RecordingInfo.Text = media != null && File.Exists(media)
                ? $"{_result.Speakers.Count} voice(s) · {_result.Lines.Count} lines · audio: {Path.GetFileName(media)}"
                : "⚠ No audio is kept for this recording, so voices and audio can't be analysed.";
            SpeakerChecks.ItemsSource = _result.Speakers.Select(sp => new SpeakerCheck
            {
                Speaker = sp, Info = $"  · {TimeSpan.FromSeconds(_result.Lines.Where(l => l.Speaker == sp).Sum(l => (l.End - l.Start).TotalSeconds)):mm\\:ss}",
            }).ToList();
            var voices = _result.Speakers.Select(sp => new VoiceRef("This recording: " + sp.Label, sp, null))
                .Concat(VoiceLibrary.Shared.Voices.Select(v => new VoiceRef("Voice library: " + v.Name, null, v))).ToList();
            CompareA.ItemsSource = voices;
            CompareB.ItemsSource = voices;
            CompareA.SelectedIndex = voices.Count > 0 ? 0 : -1;
            CompareB.SelectedIndex = voices.Count > 1 ? 1 : -1;
            Render();
        }
        catch (Exception ex) { StatusText.Text = "Could not open the recording: " + ex.Message; }
    }

    private async Task<float[]?> SamplesAsync()
    {
        if (_samples != null) return _samples;
        var media = _result?.MediaPath;
        if (media == null || !File.Exists(media)) { StatusText.Text = "This recording has no audio to analyse."; return null; }
        var ffmpeg = AudioDecoder.FindFfmpeg();
        if (ffmpeg == null) { StatusText.Text = "FFmpeg was not found (Settings → Storage & tools)."; return null; }
        StatusText.Text = "Reading audio…";
        await SpeakerModels.EnsureAsync(new Progress<string>(s => StatusText.Text = s), CancellationToken.None);
        _samples = await Task.Run(() => AudioDecoder.DecodeAsync(ffmpeg, media, CancellationToken.None));
        return _samples;
    }

    private async void OnProfile(object sender, RoutedEventArgs e)
    {
        var chosen = (SpeakerChecks.ItemsSource as IEnumerable<SpeakerCheck>)?.Where(c => c.Selected).Select(c => c.Speaker).ToList();
        if (_result == null || chosen is not { Count: > 0 }) { StatusText.Text = "Tick at least one voice."; return; }
        SetBusy(true);
        try
        {
            var samples = await SamplesAsync();
            if (samples == null) return;
            _profiles.Clear();
            foreach (var sp in chosen)
            {
                StatusText.Text = $"Measuring {sp.Label}…";
                var lines = _result.Lines.Where(l => l.Speaker == sp).ToList();
                _profiles.Add(await Task.Run(() => VoiceForensics.Profile(sp.Label, samples, lines, CancellationToken.None)));
            }
            StatusText.Text = $"✓ Profiled {_profiles.Count} voice(s).";
            Render();
        }
        catch (Exception ex) { App.Log("Voice profile", ex); StatusText.Text = "Analysis failed: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private async Task<List<float[]>> SamplesFor(VoiceRef v, float[] samples)
    {
        if (v.Known != null) return v.Known.Embeddings.ToList();
        var turns = _result!.Lines.Where(l => l.Speaker == v.Speaker).Select(l => (l.Start.TotalSeconds, l.End.TotalSeconds)).ToList();
        return await Task.Run(() => VoiceForensics.Samples(_analyzer, samples, turns));
    }

    private async void OnCompare(object sender, RoutedEventArgs e)
    {
        if (CompareA.SelectedItem is not VoiceRef a || CompareB.SelectedItem is not VoiceRef b || a == b) { StatusText.Text = "Pick two different voices."; return; }
        await CompareAsync(new[] { (a, b) });
    }

    private async void OnCompareAll(object sender, RoutedEventArgs e)
    {
        if (CompareA.ItemsSource is not List<VoiceRef> voices) return;
        var mine = voices.Where(v => v.Speaker != null).ToList();
        var pairs = new List<(VoiceRef, VoiceRef)>();
        for (int i = 0; i < mine.Count; i++)
            for (int j = i + 1; j < mine.Count; j++) pairs.Add((mine[i], mine[j]));
        foreach (var m in mine) foreach (var k in voices.Where(v => v.Known != null)) pairs.Add((m, k));
        if (pairs.Count == 0) { StatusText.Text = "Nothing to compare."; return; }
        await CompareAsync(pairs);
    }

    private async Task CompareAsync(IReadOnlyList<(VoiceRef A, VoiceRef B)> pairs)
    {
        SetBusy(true);
        try
        {
            float[]? samples = pairs.Any(p => p.A.Speaker != null || p.B.Speaker != null) ? await SamplesAsync() : Array.Empty<float>();
            if (samples == null) return;
            var cache = new Dictionary<VoiceRef, List<float[]>>();
            foreach (var (a, b) in pairs)
            {
                StatusText.Text = $"Comparing {a.Label} with {b.Label}…";
                if (!cache.TryGetValue(a, out var sa)) cache[a] = sa = await SamplesFor(a, samples);
                if (!cache.TryGetValue(b, out var sb)) cache[b] = sb = await SamplesFor(b, samples);
                _comparisons.RemoveAll(c => c.A == a.Label && c.B == b.Label);
                _comparisons.Add(VoiceForensics.Compare(a.Label, sa, b.Label, sb));
            }
            StatusText.Text = $"✓ {pairs.Count} comparison(s).";
            Render();
        }
        catch (Exception ex) { App.Log("Voice compare", ex); StatusText.Text = "Comparison failed: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private async void OnIntegrity(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var samples = await SamplesAsync();
            if (samples == null) return;
            StatusText.Text = "Checking the audio…";
            _integrity = await VoiceForensics.CheckIntegrityAsync(_result!.MediaPath!, samples, CancellationToken.None);
            StatusText.Text = "✓ " + _integrity.Summary;
            Render();
        }
        catch (Exception ex) { App.Log("Integrity", ex); StatusText.Text = "Check failed: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private ReportDocument Report() =>
        VoiceForensics.BuildReport($"Voice & audio analysis · {_meta?.Title ?? (_result != null ? Path.GetFileNameWithoutExtension(_result.SourceFile) : "")}",
                                   _meta, _profiles, _comparisons, _integrity);

    private void Render()
    {
        bool any = _profiles.Count > 0 || _comparisons.Count > 0 || _integrity != null;
        EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ExportButton.IsEnabled = any;
        if (_webReady) Preview.NavigateToString(any ? Report().ToHtml() : "<html><body style='background:transparent'></body></html>");
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export analysis report", FileName = RecordingLibrary.Sanitize($"Voice analysis - {_meta?.Title ?? "recording"}"),
            Filter = "PDF (*.pdf)|*.pdf|Word (*.docx)|*.docx|Web page (*.html)|*.html|Text (*.txt)|*.txt|JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Report().Save(dlg.FileName);
            File.WriteAllText(dlg.FileName + ".sha256.txt", $"{BulkReport.Sha256(dlg.FileName)}  {Path.GetFileName(dlg.FileName)}\n");
            if (MessageBox.Show(this, "Report saved (with a .sha256.txt fingerprint next to it).\n\nShow it in Explorer?", "Export",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not export: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
