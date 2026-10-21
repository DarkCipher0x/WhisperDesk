using System.Windows;
using System.Windows.Threading;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>
/// Corrects one transcript line. The new text and a reason are required; on confirm the edit is recorded with date,
/// time, time zone and Windows user, and the line is tagged "edited".
/// </summary>
public partial class EditLineWindow : Window
{
    private const int MinReasonLength = 5;
    private readonly TranscriptLine _line;
    private readonly TranscriptResult _result;
    private readonly LinePlayer? _player;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>The recorded edit, set when the user confirms.</summary>
    public LineEdit? Edit { get; private set; }

    public EditLineWindow(TranscriptResult result, TranscriptLine line, LinePlayer? player)
    {
        InitializeComponent();
        _line = line;
        _result = result;
        _player = player;

        LineHeader.Text = $"[{line.Time}]  {line.Speaker?.Tag ?? "no speaker"}";
        OriginalText.Text = line.OriginalText;
        NewTextBox.Text = line.Text;
        PlayButton.IsEnabled = player != null && result.MediaPath != null;
        if (line.IsEdited)
        {
            HistoryPanel.Visibility = Visibility.Visible;
            HistoryList.ItemsSource = line.Edits.Reverse().ToList();
        }

        _clock.Tick += (_, _) => UpdateAudit();
        _clock.Start();
        UpdateAudit();
        Loaded += (_, _) => { NewTextBox.Focus(); NewTextBox.SelectAll(); };
        if (_player != null) _player.StateChanged += OnPlayerStateChanged;
        Closed += (_, _) =>
        {
            _clock.Stop();
            if (_player != null) { _player.StateChanged -= OnPlayerStateChanged; _player.Stop(); }
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    private void UpdateAudit() =>
        AuditText.Text = $"Will be recorded as: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} · by {Environment.UserName}";

    private bool TextChangedFromCurrent => NewTextBox.Text.Trim().Length > 0 && NewTextBox.Text.Trim() != _line.Text;
    private bool ReasonValid => ReasonBox.Text.Trim().Length >= MinReasonLength;

    private void OnChanged(object sender, EventArgs e)
    {
        ConfirmButton.IsEnabled = TextChangedFromCurrent && ReasonValid;
        ReasonHint.Text = !TextChangedFromCurrent
            ? "Change the text above, then explain why."
            : ReasonValid ? "Ready — the edit will be logged with the timestamp below."
                          : $"A reason of at least {MinReasonLength} characters is required.";
    }

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_player == null) return;
        try { await _player.ToggleLineAsync(_result, _line); }
        catch (Exception ex) { ReasonHint.Text = ex.Message; }
    }

    private void OnPlayerStateChanged() => PlayButton.Content = _line.IsPlaying ? "■ Stop" : "▶ Play line";

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!TextChangedFromCurrent || !ReasonValid) return;
        Edit = _line.ApplyEdit(NewTextBox.Text, ReasonBox.Text);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
