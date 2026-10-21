using System.Windows;
using System.Windows.Input;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>
/// Asks for title, type, folder, tags and notes before a transcript is stored in the recordings library.
/// When given a transcript it also shows a review panel: play any line, fix its speaker, name the speakers.
/// </summary>
public partial class SaveToLibraryWindow : Window
{
    private const string RootLabel = "(Library root)";
    private readonly TranscriptResult? _review;
    private readonly LinePlayer? _player;

    public RecordingMeta Meta { get; }
    public string Folder => FolderBox.SelectedItem is string f && f != RootLabel ? f : "";
    public bool CopyMediaIntoLibrary => CopyMedia.IsChecked == true;

    /// <param name="hasExternalMedia">True for transcribed files (offer link vs copy). False for live sessions.</param>
    /// <param name="review">Transcript to review before saving (shows the review panel), or null.</param>
    public SaveToLibraryWindow(RecordingMeta meta, bool hasExternalMedia, string info, string discardLabel = "Cancel",
                               TranscriptResult? review = null, LinePlayer? player = null)
    {
        InitializeComponent();
        Meta = meta;
        _review = review;
        _player = player;
        TitleBox.Text = meta.Title;
        TypeBox.ItemsSource = RecordingLibrary.Types;
        TypeBox.SelectedItem = meta.Type;
        TagsBox.Text = string.Join(", ", meta.Tags);
        NotesBox.Text = meta.Notes;
        MediaPanel.Visibility = hasExternalMedia ? Visibility.Visible : Visibility.Collapsed;
        InfoText.Text = info;
        DiscardButton.Content = discardLabel;
        LoadFolders(AppSettings.Current.LastLibraryFolder);

        if (review != null)
        {
            SizeToContent = SizeToContent.Manual;
            Width = Math.Min(1280, SystemParameters.WorkArea.Width - 80);
            Height = Math.Min(760, SystemParameters.WorkArea.Height - 80);
            ReviewGap.Width = new GridLength(20);
            ReviewColumn.Width = new GridLength(1, GridUnitType.Star);
            ReviewPanel.Visibility = Visibility.Visible;
            ReviewList.DataContext = review;
            ReviewList.ItemsSource = review.Lines;
            SpeakersPanel.Visibility = review.HasSpeakers ? Visibility.Visible : Visibility.Collapsed;
            SpeakerEditor.ItemsSource = review.Speakers;
            ReviewStatus.Text = review.MediaPath != null
                ? $"{review.Lines.Count} lines · {review.Speakers.Count} speaker(s). Click ▶ to hear a line."
                : $"{review.Lines.Count} lines · {review.Speakers.Count} speaker(s). Audio wasn't kept, so lines can't be played.";
        }
        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
        Closed += (_, _) => _player?.Stop();
    }

    private void LoadFolders(string? select)
    {
        var folders = new List<string> { RootLabel };
        folders.AddRange(RecordingLibrary.Folders());
        FolderBox.ItemsSource = folders;
        FolderBox.SelectedItem = select != null && folders.Contains(select) ? select : RootLabel;
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    private async void OnPlayLine(object sender, ExecutedRoutedEventArgs e)
    {
        if (_review == null || _player == null || e.Parameter is not TranscriptLine line) return;
        try
        {
            ReviewList.SelectedItem = line;
            await _player.ToggleLineAsync(_review, line);
            ReviewStatus.Text = line.IsPlaying ? $"Playing {line.Time} — {line.Speaker?.Tag ?? "no speaker"} (press ■ to stop)" : "Stopped.";
        }
        catch (Exception ex) { ReviewStatus.Text = ex.Message; }
    }

    private void OnEditLine(object sender, ExecutedRoutedEventArgs e)
    {
        if (_review == null || e.Parameter is not TranscriptLine line) return;
        ReviewList.SelectedItem = line;
        var dlg = new EditLineWindow(_review, line, _player) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Edit is { } edit)
            ReviewStatus.Text = $"Line [{line.Time}] edited and logged ({edit.AtText} by {edit.User}).";
    }

    private void OnNewFolder(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, "New folder", "Folder name:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var created = RecordingLibrary.CreateFolder(Folder, name);
        LoadFolders(created);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Meta.Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "Untitled" : TitleBox.Text.Trim();
        Meta.Type = (string)TypeBox.SelectedItem;
        Meta.Tags = TagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Meta.Notes = NotesBox.Text;
        AppSettings.Current.LastLibraryFolder = Folder;
        AppSettings.Current.Save();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
