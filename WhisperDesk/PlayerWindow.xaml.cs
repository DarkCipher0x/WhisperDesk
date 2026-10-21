using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>
/// Player mode: listen to a saved or shared session with its transcript — click a line to jump there, the current
/// line is highlighted and followed. Opens library recordings and imported .wdsession files.
/// </summary>
public partial class PlayerWindow : Window, Services.Security.ICloseOnLock
{
    private readonly RecordingMeta _meta;
    private readonly TranscriptResult _result;
    private readonly bool _isImported;
    private readonly MediaPlayer _media = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly List<TranscriptLine> _lines;
    private bool _seeking, _playing, _hasAudio;
    private int _current = -1, _pendingJump = -1;

    /// <summary>Raised after an opened session was added to the library.</summary>
    public event Action<RecordingMeta>? Imported;

    public PlayerWindow(RecordingMeta meta, TranscriptResult result, bool isImported)
    {
        InitializeComponent();
        _meta = meta;
        _result = result;
        _isImported = isImported;
        _lines = result.Lines.Where(l => l.Text.Trim().Length > 0).ToList();

        Title = $"{meta.Title} — WhisperDesk player";
        TitleText.Text = meta.Title;
        MetaText.Text = string.Join("  ·  ", new[] { meta.Type, meta.Created.ToString("yyyy-MM-dd HH:mm"), meta.Duration, meta.Source, meta.TagText }
                                             .Where(s => !string.IsNullOrWhiteSpace(s)));
        SpeakerChips.ItemsSource = result.Speakers;
        if (!string.IsNullOrWhiteSpace(meta.Notes)) { NotesBox.Visibility = Visibility.Visible; NotesText.Text = meta.Notes; }
        Lines.ItemsSource = _lines;
        ImportButton.Visibility = isImported ? Visibility.Visible : Visibility.Collapsed;

        _hasAudio = meta.HasMedia;
        if (_hasAudio)
        {
            _media.Open(new Uri(meta.MediaPath));
            _media.MediaOpened += (_, _) =>
            {
                if (_media.NaturalDuration.HasTimeSpan) Seek.Maximum = _media.NaturalDuration.TimeSpan.TotalSeconds;
                if (_pendingJump >= 0) { GoTo(_pendingJump); _pendingJump = -1; }
            };
            _media.MediaEnded += (_, _) => SetPlaying(false);
            _media.MediaFailed += (_, e) => { NoAudioText.Text = "Could not play the audio: " + e.ErrorException.Message; NoAudioText.Visibility = Visibility.Visible; };
        }
        else
        {
            NoAudioText.Visibility = Visibility.Visible;
            PlayPause.IsEnabled = Seek.IsEnabled = PrevButton.IsEnabled = NextButton.IsEnabled = StopButton.IsEnabled = false;
        }

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop(); _media.Close(); _playing = false; MarkCurrent();
            if (_isImported) Services.Security.WorkDir.Release(meta.Dir);   // the unpacked shared session (plaintext) goes away with the window
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    // ----- Transport -----

    private void SetPlaying(bool play)
    {
        if (!_hasAudio) return;
        if (play) _media.Play(); else _media.Pause();
        _playing = play;
        PlayPause.Content = play ? "❚❚  Pause" : "▶  Play";
        MarkCurrent();
    }

    /// <summary>The current line's ▶ shows ■ while it plays.</summary>
    private void MarkCurrent()
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            bool on = _playing && i == _current;
            if (_lines[i].IsPlaying != on) _lines[i].IsPlaying = on;
        }
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => SetPlaying(!_playing);

    private void PlayFrom(TranscriptLine line)
    {
        if (!_hasAudio) return;
        GoTo(_lines.IndexOf(line));
        SetPlaying(true);
    }

    /// <summary>Moves the playhead to the start of line <paramref name="idx"/> and selects it.</summary>
    private void GoTo(int idx)
    {
        if (!_hasAudio || _lines.Count == 0) return;
        idx = Math.Clamp(idx, 0, _lines.Count - 1);
        _media.Position = TimeSpan.FromSeconds(Math.Max(0, _lines[idx].Start.TotalSeconds - 0.1));
        _current = idx;
        Lines.SelectedIndex = idx;
        Lines.ScrollIntoView(_lines[idx]);
        Seek.Value = _media.Position.TotalSeconds;
        MarkCurrent();
    }

    /// <summary>Selects a line and moves the playhead there (used by the voice library's "open in player").</summary>
    public void JumpTo(TranscriptLine line)
    {
        var idx = _lines.IndexOf(line);
        if (idx < 0) idx = _lines.FindIndex(l => l.Start == line.Start);
        if (idx < 0) return;
        if (_hasAudio) { _pendingJump = idx; GoTo(idx); }   // re-applied once the media has opened
        else { Lines.SelectedIndex = idx; Lines.ScrollIntoView(_lines[idx]); }
    }

    private void OnPlayLine(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TranscriptLine l) return;
        if (l.IsPlaying) SetPlaying(false);   // ■ on the playing line pauses
        else PlayFrom(l);
    }

    /// <summary>⏮: back to the start of this line, or to the previous line if we're already at its start.</summary>
    private void OnPrevLine(object sender, RoutedEventArgs e)
    {
        if (!_hasAudio || _lines.Count == 0) return;
        int idx = LineAt(_media.Position.TotalSeconds);
        if (idx < 0) idx = 0;
        else if (_media.Position.TotalSeconds - _lines[idx].Start.TotalSeconds < 1.5) idx--;
        GoTo(idx);
    }

    /// <summary>⏭: skip to the next line.</summary>
    private void OnNextLine(object sender, RoutedEventArgs e)
    {
        if (!_hasAudio || _lines.Count == 0) return;
        int idx = LineAt(_media.Position.TotalSeconds);
        if (idx >= _lines.Count - 1) return;
        GoTo(idx + 1);
    }

    /// <summary>■: stop and rewind to the start of the current line.</summary>
    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (!_hasAudio) return;
        SetPlaying(false);
        int idx = LineAt(_media.Position.TotalSeconds);
        if (idx >= 0) GoTo(idx); else _media.Position = TimeSpan.Zero;
    }

    /// <summary>Index of the last line that started at or before <paramref name="seconds"/> (-1 before the first line).</summary>
    private int LineAt(double seconds)
    {
        int lo = 0, hi = _lines.Count - 1, idx = -1;
        var t = seconds + 0.15;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_lines[mid].Start.TotalSeconds <= t) { idx = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return idx;
    }

    private void OnLineDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Lines.SelectedItem is TranscriptLine l) PlayFrom(l);
    }

    private void OnSeekStart(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void OnSeekEnd(object sender, MouseButtonEventArgs e)
    {
        _seeking = false;
        if (_hasAudio) _media.Position = TimeSpan.FromSeconds(Seek.Value);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Space) { SetPlaying(!_playing); e.Handled = true; }
        else if (e.Key == Key.Escape) { OnStop(this, e); e.Handled = true; }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control) { OnPrevLine(this, e); e.Handled = true; }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control) { OnNextLine(this, e); e.Handled = true; }
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    private void Tick()
    {
        if (!_hasAudio) return;
        var pos = _media.Position;
        var total = _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        TimeText.Text = $"{Fmt(pos)} / {Fmt(total)}";
        if (!_seeking) Seek.Value = pos.TotalSeconds;

        // Current line = last line that started before the playhead.
        int idx = LineAt(pos.TotalSeconds);
        SyncFeed(idx);
        ForensicsTick(pos);
        if (idx != _current && idx >= 0 && _playing)
        {
            _current = idx;
            Lines.SelectedIndex = idx;
            if (FollowBox.IsChecked == true) Lines.ScrollIntoView(_lines[idx]);
            MarkCurrent();
        }
    }

    // ----- Live feed (replays the lines as the audio plays) -----

    private readonly System.Collections.ObjectModel.ObservableCollection<TranscriptLine> _played = new();
    private CaptionFeedWindow? _feed;

    private void OnFeed(object sender, RoutedEventArgs e)
    {
        if (_feed != null) { _feed.Activate(); return; }
        _feed = new CaptionFeedWindow();
        _feed.Closed += (_, _) => _feed = null;
        _feed.SetLines(_hasAudio ? _played : new System.Collections.ObjectModel.ObservableCollection<TranscriptLine>(_lines), _meta.Title);
        _feed.Show();
        Closed += (_, _) => _feed?.Close();
    }

    /// <summary>Keeps <see cref="_played"/> = every line up to the playhead (rebuilt after seeking back).</summary>
    private void SyncFeed(int idx)
    {
        if (_feed == null || !_hasAudio) return;
        int want = idx + 1;
        if (want < _played.Count) { _played.Clear(); }
        for (int i = _played.Count; i < want && i < _lines.Count; i++) _played.Add(_lines[i]);
    }

    // ----- Share / import -----

    private void OnSummary(object sender, RoutedEventArgs e) =>
        new SummaryWindow(_result, _meta.Title) { Owner = this }.ShowDialog();

    private async void OnShare(object sender, RoutedEventArgs e)
    {
        ShareButton.IsEnabled = false;
        try { await ShareAsync(this, _meta, _result); }
        finally { ShareButton.IsEnabled = true; }
    }

    /// <summary>Share / export dialog: Windows share sheet or save as web player, session package, PDF, Word, text or summary.</summary>
    public static Task ShareAsync(Window owner, RecordingMeta meta, TranscriptResult result)
    {
        ShareDialog.Show(owner, meta, result);
        return Task.CompletedTask;
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveToLibraryWindow(_meta, hasExternalMedia: false, info: $"Imported session · {_meta.Source}") { Owner = this, Title = "Add to library — WhisperDesk" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var saved = SessionPackage.Import(dlg.Meta, _result, dlg.Folder);
            ImportButton.IsEnabled = false;
            ImportButton.Content = "✓ In your library";
            Imported?.Invoke(saved);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not import: " + ex.Message, "Add to library", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
