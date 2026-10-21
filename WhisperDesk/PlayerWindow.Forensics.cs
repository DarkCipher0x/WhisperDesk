using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>
/// The player's Forensics view: AudioScope (waveform, spectrogram, pitch, loudness, speakers), selection measurements,
/// speaker identification, notes, whole-file integrity checks, and a hash-chained audit log that goes into the report.
/// </summary>
public partial class PlayerWindow
{
    private float[]? _fSamples;
    private ForensicAudit? _audit;
    private SpeakerAnalyzer? _fAnalyzer;
    private IntegrityReport? _fIntegrity;
    private readonly Dictionary<SpeakerInfo, List<float[]>> _voiceCache = new();
    private readonly List<(string Title, List<(string, string)> Rows)> _fResults = new();
    private bool _forensicsReady, _forensicsLoading;

    /// <summary>Opens straight into the Forensics view (Library → Forensics…).</summary>
    public PlayerWindow ShowForensics()
    {
        Loaded += (_, _) => OnForensicsView(this, new RoutedEventArgs());
        return this;
    }

    private void OnTranscriptView(object sender, RoutedEventArgs e)
    {
        TranscriptViewButton.IsChecked = true;
        ForensicsViewButton.IsChecked = false;
        ForensicsPanel.Visibility = Visibility.Collapsed;
        Lines.Visibility = Visibility.Visible;
    }

    private async void OnForensicsView(object sender, RoutedEventArgs e)
    {
        TranscriptViewButton.IsChecked = false;
        ForensicsViewButton.IsChecked = true;
        Lines.Visibility = Visibility.Collapsed;
        ForensicsPanel.Visibility = Visibility.Visible;
        if (_forensicsReady || _forensicsLoading) return;
        if (!_hasAudio) { SelText.Text = "This recording has no audio, so there is nothing to analyse."; SetForensicsEnabled(false); return; }
        _forensicsLoading = true;
        SetForensicsEnabled(false);
        try
        {
            var ffmpeg = AudioDecoder.FindFfmpeg() ?? throw new InvalidOperationException("FFmpeg was not found (Settings → Storage & tools).");
            SelText.Text = "Reading audio and fingerprinting the file…";
            _fSamples = await Task.Run(() => AudioDecoder.DecodeAsync(ffmpeg, _meta.MediaPath, CancellationToken.None));
            _audit = new ForensicAudit(_isImported ? null : _meta, _meta.MediaPath);
            await _audit.HashMediaAsync();
            Scope.SetAudio(_fSamples);
            Scope.Segments = _lines.Where(l => l.Speaker != null)
                .Select(l => new ScopeSegment(l.Start.TotalSeconds, l.End.TotalSeconds, l.Speaker!.Color, l.Speaker.Label)).ToList();
            Scope.SeekRequested += t => { _media.Position = TimeSpan.FromSeconds(t); Scope.Playhead = t; };
            Scope.SelectionChanged += UpdateSelectionText;
            _audit.Add("Opened", $"Forensics view opened on “{_meta.Title}”", details: new()
            {
                ["file"] = Path.GetFileName(_meta.MediaPath), ["sha256"] = _audit.MediaSha256,
                ["decoded"] = $"{_fSamples.Length / (double)Spectral.Rate:0.000} s at {Spectral.Rate} Hz mono",
            });
            RefreshMarkers();
            RefreshAudit();
            SelText.Text = "Drag across the waveform or spectrogram to select.";
            _forensicsReady = true;
            SetForensicsEnabled(true);
        }
        catch (Exception ex)
        {
            App.Log("Forensics view", ex);
            SelText.Text = "Could not open the audio: " + ex.Message;
        }
        finally { _forensicsLoading = false; }
        Closed += (_, _) => _fAnalyzer?.Dispose();
    }

    private void SetForensicsEnabled(bool on)
    {
        foreach (var b in new Control[] { MeasureButton, IdentifyButton, NoteButton, IntegrityButton, ReportButton, PlaySelButton }) b.IsEnabled = on;
    }

    /// <summary>Called from the player's timer: keeps the playhead and loop in step with playback.</summary>
    private void ForensicsTick(TimeSpan pos)
    {
        if (!_forensicsReady || ForensicsPanel.Visibility != Visibility.Visible) return;
        Scope.IsPlaying = _playing;
        if (Math.Abs(Scope.Playhead - pos.TotalSeconds) > 0.01) Scope.Playhead = pos.TotalSeconds;
        if (_playSelEnd is { } end && pos.TotalSeconds >= end)
        {
            if (LoopBox.IsChecked == true && Scope.SelStart is { } s) _media.Position = TimeSpan.FromSeconds(s);
            else { SetPlaying(false); _playSelEnd = null; }
        }
    }

    private double? _playSelEnd;

    private void OnPlaySelection(object sender, RoutedEventArgs e)
    {
        if (Scope.SelStart is not { } a || Scope.SelEnd is not { } b) { SelText.Text = "Select a stretch first."; return; }
        _media.Position = TimeSpan.FromSeconds(a);
        _playSelEnd = b;
        SetPlaying(true);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => Scope.ZoomBy(0.5);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Scope.ZoomBy(2);
    private void OnZoomFit(object sender, RoutedEventArgs e) => Scope.ZoomToFit();
    private void OnZoomSelection(object sender, RoutedEventArgs e) => Scope.ZoomToSelection();
    private void OnClearSelection(object sender, RoutedEventArgs e) => Scope.ClearSelection();
    private void OnScopeFollow(object sender, RoutedEventArgs e) => Scope.Follow = ScopeFollowBox.IsChecked == true;

    private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Scope == null) return;
        if (RangeBox.SelectedItem is ComboBoxItem { Tag: string r }) Scope.DynamicRangeDb = double.Parse(r);
        if (FreqBox.SelectedItem is ComboBoxItem { Tag: string f }) Scope.MaxFreq = double.Parse(f);
        Scope.SetView(Scope.ViewStart, Scope.ViewEnd);
    }

    private static string Fmt(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss\.fff");

    private void UpdateSelectionText()
    {
        if (Scope.SelStart is not { } a || Scope.SelEnd is not { } b) { SelText.Text = "Drag across the waveform or spectrogram to select."; return; }
        var who = _lines.Where(l => l.End.TotalSeconds > a && l.Start.TotalSeconds < b && l.Speaker != null)
            .Select(l => l.Speaker!.Label).Distinct().ToList();
        SelText.Text = $"{Fmt(a)} → {Fmt(b)}\nlength {b - a:0.000} s" + (who.Count > 0 ? $"\ntranscript speaker(s): {string.Join(", ", who)}" : "");
    }

    private bool NeedSelection(out double a, out double b, double min = 0.3)
    {
        a = Scope.SelStart ?? 0; b = Scope.SelEnd ?? 0;
        if (Scope.SelStart == null || b - a < min) { SelText.Text = $"Select at least {min:0.#} s first (drag across the display)."; return false; }
        return true;
    }

    private void AddResult(string title, List<(string, string)> rows)
    {
        _fResults.Add((title, rows));
        var sb = new StringBuilder(ResultsBox.Text);
        if (sb.Length > 0) sb.AppendLine().AppendLine();
        sb.AppendLine("■ " + title);
        foreach (var (k, v) in rows) sb.AppendLine($"  {k}: {v}");
        ResultsBox.Text = sb.ToString();
        ResultsBox.ScrollToEnd();
    }

    // ----- Measure -----

    private async void OnMeasure(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection(out var a, out var b) || _fSamples == null) return;
        MeasureButton.IsEnabled = false;
        try
        {
            var samples = _fSamples;
            var line = new TranscriptLine(TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b), "");
            var p = await Task.Run(() => VoiceForensics.Profile("selection", samples, new[] { line }, CancellationToken.None));
            int s0 = (int)(a * Spectral.Rate), s1 = Math.Min(samples.Length, (int)(b * Spectral.Rate));
            float peak = 0; long clipped = 0;
            for (int i = s0; i < s1; i++) { var v = Math.Abs(samples[i]); if (v > peak) peak = v; if (v >= 0.999f) clipped++; }
            var rows = new List<(string, string)>
            {
                ("Range", $"{Fmt(a)} – {Fmt(b)} ({b - a:0.000} s)"),
                ("Level (RMS / peak)", $"{Spectral.LevelDb(samples, s0, s1 - s0):0.0} dBFS / {20 * Math.Log10(peak + 1e-9):0.0} dBFS"),
                ("Clipped samples", clipped.ToString("N0")),
                ("Voiced", $"{p.VoicedFraction:P0}"),
                ("Pitch F0 median (5–95 %)", p.F0Median > 0 ? $"{p.F0Median:0} Hz ({p.F0Min:0}–{p.F0Max:0} Hz)" : "no voiced speech"),
                ("Pitch variability", $"{p.F0StdSemitones:0.0} semitones"),
                ("Jitter / shimmer", $"{p.JitterPercent:0.00} % / {p.ShimmerPercent:0.0} %"),
                ("HNR", $"{p.HnrDb:0.0} dB"),
                ("Formants F1/F2/F3", $"{p.F1:0} / {p.F2:0} / {p.F3:0} Hz"),
                ("Spectral centroid", $"{p.SpectralCentroidHz:0} Hz"),
            };
            AddResult($"Measurement {Fmt(a)}–{Fmt(b)}", rows);
            _audit!.Add("Measured", $"F0 {p.F0Median:0} Hz, HNR {p.HnrDb:0.0} dB, jitter {p.JitterPercent:0.00} %, level {rows[1].Item2}", a, b,
                rows.ToDictionary(r => r.Item1, r => r.Item2));
            RefreshAudit();
        }
        catch (Exception ex) { App.Log("Measure", ex); SelText.Text = "Measurement failed: " + ex.Message; }
        finally { MeasureButton.IsEnabled = true; }
    }

    // ----- Identify -----

    private async void OnIdentify(object sender, RoutedEventArgs e)
    {
        if (!NeedSelection(out var a, out var b, 1.0) || _fSamples == null) return;
        IdentifyButton.IsEnabled = false;
        var old = SelText.Text;
        try
        {
            SelText.Text = "Comparing voices…";
            await SpeakerModels.EnsureAsync(new Progress<string>(s => SelText.Text = s), CancellationToken.None);
            _fAnalyzer ??= new SpeakerAnalyzer();
            var samples = _fSamples;
            // Several 1.5–4 s chunks of the selection give a within-voice baseline as well as the comparison.
            var chunks = new List<(double, double)>();
            for (double t = a; t < b - 0.8; t += 3) chunks.Add((t, Math.Min(b, t + 3)));
            var sel = await Task.Run(() => chunks.Count > 1
                ? VoiceForensics.Samples(_fAnalyzer, samples, chunks)
                : VoiceForensics.Samples(_fAnalyzer, samples, new[] { (a, b) }));
            if (sel.Count == 0) { SelText.Text = "The selection has too little clear speech to compare."; return; }

            var candidates = new List<(string Name, List<float[]> Prints)>();
            foreach (var sp in _result.Speakers)
            {
                if (!_voiceCache.TryGetValue(sp, out var prints))
                {
                    var turns = _lines.Where(l => l.Speaker == sp && !(l.End.TotalSeconds > a && l.Start.TotalSeconds < b))
                        .Select(l => (l.Start.TotalSeconds, l.End.TotalSeconds)).ToList();   // exclude the selection itself
                    prints = await Task.Run(() => VoiceForensics.Samples(_fAnalyzer, samples, turns));
                    _voiceCache[sp] = prints;
                }
                if (prints.Count > 0) candidates.Add(("This recording: " + sp.Label, prints));
            }
            foreach (var v in VoiceLibrary.Shared.Voices.Where(v => v.Embeddings.Count > 0))
                candidates.Add(("Voice library: " + v.Name, v.Embeddings.ToList()));

            var results = candidates.Select(c => VoiceForensics.Compare("Selection", sel, c.Name, c.Prints)).OrderByDescending(r => r.Score).ToList();
            var rows = results.Take(8).Select(r => (r.B, $"{r.Score:0.00} ({r.Min:0.00}–{r.Max:0.00}) · {r.Verdict}")).ToList();
            rows.Insert(0, ("Selection", $"{Fmt(a)} – {Fmt(b)}, {sel.Count} voice sample(s)"));
            AddResult($"Speaker comparison {Fmt(a)}–{Fmt(b)}", rows);
            var best = results.FirstOrDefault();
            _audit!.Add("Identified", best == null ? "no candidates" : $"closest: {best.B} {best.Score:0.00} ({best.Verdict})", a, b,
                rows.ToDictionary(r => r.Item1, r => r.Item2));
            RefreshAudit();
            SelText.Text = old;
        }
        catch (Exception ex) { App.Log("Identify", ex); SelText.Text = "Comparison failed: " + ex.Message; }
        finally { IdentifyButton.IsEnabled = true; }
    }

    // ----- Notes, integrity, audit -----

    private void OnAddNote(object sender, RoutedEventArgs e)
    {
        if (_audit == null) return;
        double a = Scope.SelStart ?? Scope.Playhead, b = Scope.SelEnd ?? Scope.Playhead;
        var note = InputDialog.Ask(this, "Add note", $"Observation at {Fmt(a)}{(b > a ? " – " + Fmt(b) : "")} (signed into the audit log; it cannot be edited later):", "");
        if (string.IsNullOrWhiteSpace(note)) return;
        _audit.Add("Note", note.Trim(), a, b > a ? b : null);
        RefreshAudit();
        RefreshMarkers();
    }

    private async void OnScopeIntegrity(object sender, RoutedEventArgs e)
    {
        if (_fSamples == null || _audit == null) return;
        IntegrityButton.IsEnabled = false;
        try
        {
            _fIntegrity = await VoiceForensics.CheckIntegrityAsync(_meta.MediaPath, _fSamples, CancellationToken.None);
            var rows = _fIntegrity.File.Concat(_fIntegrity.Signal).ToList();
            rows.AddRange(_fIntegrity.Events.Select(ev => ("@ " + Fmt(ev.Time), ev.Finding)));
            AddResult("Integrity check (whole file)", rows);
            _audit.Add("Integrity check", _fIntegrity.Summary, details: _fIntegrity.Signal.ToDictionary(r => r.Label, r => r.Value));
            RefreshAudit();
            RefreshMarkers();
        }
        catch (Exception ex) { App.Log("Integrity", ex); SelText.Text = "Check failed: " + ex.Message; }
        finally { IntegrityButton.IsEnabled = true; }
    }

    private void OnVerifyAudit(object sender, RoutedEventArgs e)
    {
        if (_audit == null) return;
        var (ok, msg) = _audit.Verify();
        AuditStatus.Text = (ok ? "✓ " : "⚠ ") + msg;
        AuditStatus.Foreground = ok ? (Brush)FindResource("Muted") : Brushes.OrangeRed;
    }

    private void OnAuditDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AuditList.SelectedItem is not AuditEntry { From: { } f } entry) return;
        if (entry.To is { } t && t > f) { Scope.Select(f, t); Scope.ZoomToSelection(); }
        else { Scope.SetView(f - 5, f + 5); _media.Position = TimeSpan.FromSeconds(f); }
    }

    private void RefreshAudit()
    {
        if (_audit == null) return;
        AuditList.ItemsSource = null;
        AuditList.ItemsSource = _audit.Entries.AsEnumerable().Reverse().ToList();
        var (ok, msg) = _audit.Verify();
        AuditStatus.Text = (ok ? "✓ " : "⚠ ") + msg + $"\nLog: {_audit.LogPath}";
    }

    private void RefreshMarkers()
    {
        var markers = new List<ScopeMarker>();
        var noteBrush = new SolidColorBrush(Color.FromRgb(0x36, 0xD2, 0xCB));
        var warnBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
        if (_audit != null)
            markers.AddRange(_audit.Entries.Where(x => x.Action == "Note" && x.From != null).Select(x => new ScopeMarker(x.From!.Value, x.Summary, noteBrush)));
        if (_fIntegrity != null)
            markers.AddRange(_fIntegrity.Events.Where(ev => !ev.Finding.StartsWith("Note")).Select(ev => new ScopeMarker(ev.Time, ev.Finding, warnBrush)));
        Scope.Markers = markers;
        Scope.InvalidateVisual();
    }

    // ----- Report -----

    private void OnForensicReport(object sender, RoutedEventArgs e)
    {
        if (_audit == null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Export forensic report", FileName = RecordingLibrary.Sanitize($"Forensic report - {_meta.Title}"),
            Filter = "PDF (*.pdf)|*.pdf|Web page (*.html)|*.html|Word (*.docx)|*.docx|Text (*.txt)|*.txt|JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var basePath = Path.Combine(Path.GetDirectoryName(dlg.FileName)!, Path.GetFileNameWithoutExtension(dlg.FileName));
            // Pictures: the current view, plus the selection zoomed in if there is one.
            var pictures = new List<(string Path, string Caption)>();
            void Snap(string suffix, string caption)
            {
                Scope.UpdateLayout();
                var png = basePath + suffix + ".png";
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(Scope.Snapshot()));
                using (var fs = File.Create(png)) enc.Save(fs);
                pictures.Add((png, caption));
            }
            Snap(" - view", $"View {Fmt(Scope.ViewStart)} – {Fmt(Scope.ViewEnd)}: speakers, waveform, spectrogram with pitch, loudness.");

            _audit.Add("Exported", "Forensic report: " + Path.GetFileName(dlg.FileName), details: new() { ["path"] = dlg.FileName });
            var (ok, chain) = _audit.Verify();

            var doc = new ReportDocument
            {
                Title = $"Forensic audio examination · {_meta.Title}",
                Subtitle = $"Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} by {Environment.UserDomainName}\\{Environment.UserName} on {Environment.MachineName} · WhisperDesk {AboutWindow.Version}",
            };
            doc.P(VoiceForensics.Disclaimer, muted: true);
            doc.H1("Evidence item").KV(new (string, string)[]
            {
                ("Title", _meta.Title), ("Recorded", _meta.Created.ToString("yyyy-MM-dd HH:mm")), ("Length", _meta.Duration),
                ("Media file", _meta.MediaPath), ("Media SHA-256", _audit.MediaSha256), ("Source", _meta.Source), ("Library ID", _meta.Id),
                ("Transcribed with", _meta.Model ?? "unknown"), ("Speakers", _meta.SpeakerNames),
            });
            doc.H1("Audit trail integrity").P((ok ? "✓ " : "⚠ ") + chain, bold: true)
               .P("Each audit entry includes the SHA-256 of the previous entry; any change, deletion or reordering breaks the chain. " +
                  "The media SHA-256 recorded with each entry shows the same audio was examined throughout.", muted: true);
            doc.H1("Views");
            foreach (var (path, caption) in pictures) doc.Img(path, caption);
            if (_fResults.Count > 0)
            {
                doc.H1("Findings");
                foreach (var (title, rows) in _fResults) { doc.H2(title); doc.KV(rows); }
            }
            doc.H1("Audit log");
            doc.T(new[] { "#", "When", "Who", "Action", "Range", "Summary", "Hash" },
                _audit.Entries.Select(x => new[] { x.Seq.ToString(), x.AtText, x.User, x.Action, x.RangeText, x.Summary, x.Hash[..16] + "…" }),
                new[] { 0.4, 1.8, 1.4, 1.1, 1.6, 3.2, 1.4 });
            doc.Save(dlg.FileName);
            var sha = BulkReport.Sha256(dlg.FileName);
            File.WriteAllText(dlg.FileName + ".sha256.txt", $"{sha}  {Path.GetFileName(dlg.FileName)}\n" +
                string.Concat(pictures.Select(p => $"{BulkReport.Sha256(p.Path)}  {Path.GetFileName(p.Path)}\n")));
            _audit.Add("Report fingerprint", $"SHA-256 {sha}", details: new() { ["file"] = Path.GetFileName(dlg.FileName), ["sha256"] = sha });
            RefreshAudit();
            if (MessageBox.Show(this, "Forensic report saved, with the view image(s) and a .sha256.txt fingerprint file.\n\nShow it in Explorer?", "Export",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
        }
        catch (Exception ex)
        {
            App.Log("Forensic report", ex);
            MessageBox.Show(this, "Could not export: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
