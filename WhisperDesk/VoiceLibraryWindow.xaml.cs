using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>One line spoken by a voice, found in a library recording.</summary>
public sealed class VoiceLine
{
    public required RecordingMeta Meta { get; init; }
    public required TranscriptResult Result { get; init; }
    public required TranscriptLine Line { get; init; }
    /// <summary>"named" when the speaker carries the voice's name, or the similarity for an unnamed look-alike.</summary>
    public required string How { get; init; }
    public string Title => Meta.Title;
    public string Date => Meta.Created.ToString("yyyy-MM-dd");
}

public partial class VoiceLibraryWindow : Window
{
    private readonly VoiceLibrary _library;
    private readonly SpeakerAnalyzer _analyzer;
    private readonly LinePlayer _player = new();
    private int _findVersion;
    private bool _loadingDetails;

    /// <summary>Raised after a voice was renamed (old name, new name, sex) so open transcripts can follow.</summary>
    public event Action<string, string, string>? VoiceRenamed;

    public VoiceLibraryWindow(VoiceLibrary library, SpeakerAnalyzer analyzer, string? select = null)
    {
        InitializeComponent();
        _library = library;
        _analyzer = analyzer;
        Refresh(select);
        Closed += (_, _) => _player.Stop();
    }

    private KnownVoice? Selected => VoicesList.SelectedItem as KnownVoice;

    private void Refresh(string? select = null)
    {
        var list = _library.Voices.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        VoicesList.ItemsSource = list;
        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (select != null) VoicesList.SelectedItem = list.FirstOrDefault(v => v.Name.Equals(select, StringComparison.OrdinalIgnoreCase));
        else if (list.Count > 0 && VoicesList.SelectedItem == null) VoicesList.SelectedIndex = 0;
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ForgetButton.IsEnabled = Selected != null;
        DetailsPanel.IsEnabled = Selected != null;
        _loadingDetails = true;
        EditName.Text = Selected?.Name ?? "";
        EditSex.SelectedItem = Selected?.Sex;
        _loadingDetails = false;
        SaveDetailsButton.IsEnabled = false;
        if (Selected is { } v)
        {
            NewName.Text = v.Name;      // makes "add another sample for this person" one click away
            NewSex.SelectedItem = v.Sex;
        }
        FindLines();
    }

    // ----- Rename -----

    private void OnDetailsChanged(object sender, EventArgs e)
    {
        if (_loadingDetails || Selected is not { } v) return;
        var name = EditName.Text.Trim();
        SaveDetailsButton.IsEnabled = name.Length > 0 && (name != v.Name || EditSex.SelectedItem as string != v.Sex);
    }

    private void OnRenameMenu(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } v) return;
        var name = InputDialog.Ask(this, "Rename voice", $"New name for “{v.Name}”:", v.Name);
        if (string.IsNullOrWhiteSpace(name) || name == v.Name) return;
        EditName.Text = name;
        OnSaveDetails(sender, e);
    }

    private async void OnSaveDetails(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } v) return;
        var oldName = v.Name;
        var newName = EditName.Text.Trim();
        var sex = EditSex.SelectedItem as string ?? v.Sex;
        if (newName.Length == 0) { StatusText.Text = "The name can't be empty."; return; }

        var exists = _library.Voices.Any(x => x != v && x.Name.Equals(newName, StringComparison.OrdinalIgnoreCase));
        if (exists && MessageBox.Show(this, $"“{newName}” is already in your voice library.\n\nMerge “{oldName}” into “{newName}”? " +
                                            "Their voice samples are combined.", "Rename voice",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SaveDetailsButton.IsEnabled = false;
        try
        {
            _library.Rename(v, newName, sex);
            _library.Save();

            var updated = 0;
            if (RenameInRecordings.IsChecked == true)
            {
                StatusText.Text = "Updating saved recordings…";
                updated = await Task.Run(() => RenameInLibrary(oldName, newName, sex));
            }

            VoiceRenamed?.Invoke(oldName, newName, sex);
            Refresh(newName);
            StatusText.Text = $"✓ Saved “{newName}”" + (exists ? " (merged)" : "") +
                              (updated > 0 ? $" · updated {updated} recording(s)" : "") + ".";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save: " + ex.Message;
        }
    }

    /// <summary>Renames the speaker in every saved recording that uses the old name. Returns how many changed.</summary>
    private static int RenameInLibrary(string oldName, string newName, string sex)
    {
        var count = 0;
        foreach (var meta in RecordingLibrary.All())
        {
            if (!meta.Speakers.Any(s => s.Name.Trim().Equals(oldName, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var result = RecordingLibrary.Load(meta);
                foreach (var s in result.Speakers.Where(s => s.Name.Trim().Equals(oldName, StringComparison.OrdinalIgnoreCase)))
                {
                    s.Name = newName;
                    s.Sex = sex;
                }
                RecordingLibrary.WriteTranscript(meta, result);
                count++;
            }
            catch { /* skip recordings that can't be read */ }
        }
        return count;
    }

    // ----- Lines spoken by this voice -----

    private void OnFindLines(object sender, RoutedEventArgs e) => FindLines();

    private async void FindLines()
    {
        _player.Stop();
        var version = ++_findVersion;
        LinesList.ItemsSource = null;
        OpenPlayerButton.IsEnabled = false;
        if (Selected is not { } voice) { LinesStatus.Text = ""; LinesHeader.Text = "LINES SPOKEN BY THIS VOICE"; return; }

        LinesHeader.Text = $"LINES SPOKEN BY {voice.Name.ToUpperInvariant()}";
        LinesStatus.Text = "Searching your library…";
        bool includeMatches = IncludeMatches.IsChecked == true;
        var threshold = AppSettings.Current.VoiceMatchThreshold;

        List<VoiceLine> found;
        try
        {
            found = await Task.Run(() =>
            {
                var list = new List<VoiceLine>();
                foreach (var meta in RecordingLibrary.All())
                {
                    var how = new Dictionary<int, string>();
                    foreach (var s in meta.Speakers)
                    {
                        if (s.Name.Trim().Equals(voice.Name, StringComparison.OrdinalIgnoreCase)) how[s.Id] = "named";
                        else if (includeMatches && string.IsNullOrWhiteSpace(s.Name) && s.Voiceprint is { Length: > 0 } vp)
                        {
                            var score = VoiceLibrary.Score(voice, vp);
                            if (score >= threshold) how[s.Id] = $"~{score * 100:0}%";
                        }
                    }
                    if (how.Count == 0) continue;
                    try
                    {
                        var result = RecordingLibrary.Load(meta);
                        foreach (var line in result.Lines.Where(l => l.Speaker != null && how.ContainsKey(l.Speaker.Id) && l.Text.Length > 0))
                            list.Add(new VoiceLine { Meta = meta, Result = result, Line = line, How = how[line.Speaker!.Id] });
                    }
                    catch { /* unreadable recording */ }
                }
                return list.OrderByDescending(l => l.Meta.Created).ThenBy(l => l.Line.Start).ToList();
            });
        }
        catch (Exception ex)
        {
            if (version == _findVersion) LinesStatus.Text = "Could not search the library: " + ex.Message;
            return;
        }
        if (version != _findVersion) return;   // a newer search started

        LinesList.ItemsSource = found;
        var recordings = found.Select(f => f.Meta.Id).Distinct().Count();
        var talk = TimeSpan.FromSeconds(found.Sum(f => (f.Line.End - f.Line.Start).TotalSeconds));
        LinesStatus.Text = found.Count == 0
            ? "No lines yet. Name this person in a recording (or tick “Include unnamed speakers…”)."
            : $"{found.Count} line(s) in {recordings} recording(s) · {talk:hh\\:mm\\:ss} of speech · ▶ plays a line, double-click opens the player";
    }

    private void OnLineSelected(object sender, SelectionChangedEventArgs e) =>
        OpenPlayerButton.IsEnabled = LinesList.SelectedItem is VoiceLine;

    private async void OnPlayLine(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VoiceLine vl) return;
        LinesList.SelectedItem = vl;
        try { await _player.ToggleLineAsync(vl.Result, vl.Line); }
        catch (Exception ex) { LinesStatus.Text = ex.Message; }
    }

    private void OnOpenPlayer(object sender, RoutedEventArgs e)
    {
        if (LinesList.SelectedItem is not VoiceLine vl) return;
        _player.Stop();
        try
        {
            var player = new PlayerWindow(vl.Meta, vl.Result, isImported: false) { Owner = this };
            player.Show();
            player.JumpTo(vl.Line);
        }
        catch (Exception ex) { LinesStatus.Text = "Could not open the player: " + ex.Message; }
    }

    // ----- Add / forget -----

    private async void OnAddFromFile(object sender, RoutedEventArgs e)
    {
        var name = NewName.Text.Trim();
        if (name.Length == 0)
        {
            AddStatus.Text = "Type the person's name first.";
            NewName.Focus();
            return;
        }
        var ffmpeg = AudioDecoder.FindFfmpeg();
        if (ffmpeg == null) { AddStatus.Text = "FFmpeg not found (see File → Settings)."; return; }

        var exts = string.Join(";", AudioDecoder.SupportedExtensions.Select(x => "*" + x));
        var dlg = new OpenFileDialog { Filter = $"Audio & video|{exts}", Title = $"Recording of {name} talking" };
        if (dlg.ShowDialog(this) != true) return;

        AddFromFileButton.IsEnabled = false;
        try
        {
            await SpeakerModels.EnsureAsync(new Progress<string>(s => AddStatus.Text = s), CancellationToken.None);
            AddStatus.Text = "Reading voice…";
            var samples = await Task.Run(() => AudioDecoder.DecodeAsync(ffmpeg, dlg.FileName, CancellationToken.None));
            var first = samples.AsSpan(0, Math.Min(samples.Length, AudioDecoder.SampleRate * 60)).ToArray();
            var embedding = await Task.Run(() => _analyzer.Embed(first));
            if (embedding.Length == 0) { AddStatus.Text = "That recording is too short. Use at least a few seconds of speech."; return; }

            var sex = (string)NewSex.SelectedItem;
            if (sex == Sexes.Unspecified)
                sex = PitchEstimator.SuggestSex(PitchEstimator.MedianPitch(first, AudioDecoder.SampleRate));

            _library.Remember(name, sex, embedding);
            _library.Save();
            Refresh(name);
            AddStatus.Text = $"✓ Saved {name}'s voice from {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            AddStatus.Text = "Could not add voice: " + ex.Message;
        }
        finally
        {
            AddFromFileButton.IsEnabled = true;
        }
    }

    private void OnForget(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } voice) return;
        if (MessageBox.Show(this, $"Forget the voice of “{voice.Name}”?\n\nSaved recordings keep the name; it just won't be recognised automatically any more.",
                "Voice library", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _library.Forget(voice.Name);
        _library.Save();
        VoicesList.SelectedItem = null;
        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
