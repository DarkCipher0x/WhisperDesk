using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;
using Whisper.net;
using WhisperDesk.Services;

namespace WhisperDesk;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name!);
        return true;
    }
}

/// <summary>One recorded correction of a line's text (audit trail).</summary>
public sealed record LineEdit(DateTimeOffset At, string User, string OldText, string NewText, string Reason)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string AtText => At.ToString("yyyy-MM-dd HH:mm:ss zzz");
}

/// <summary>One timestamped line. Its speaker can be changed, and its text corrected with a recorded reason.</summary>
public sealed class TranscriptLine : Observable, IPlayable
{
    private bool _isPlaying;
    /// <summary>True while this line's audio is playing (▶ shows ■).</summary>
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }

    public TranscriptLine(TimeSpan start, TimeSpan end, string text, string? language = null, float probability = 0, SpeakerInfo? speaker = null)
    {
        Start = start; End = end; _text = text.Trim(); OriginalText = _text; Language = language; Probability = probability; _speaker = speaker;
    }

    public TimeSpan Start { get; }
    public TimeSpan End { get; }
    public string? Language { get; }
    public float Probability { get; }
    public string Time => Start.ToString(@"hh\:mm\:ss");

    private string _text;
    /// <summary>Current text. Change it only through <see cref="ApplyEdit"/> so the change is audited.</summary>
    public string Text { get => _text; private set => Set(ref _text, value); }

    /// <summary>Text exactly as transcribed, before any edits.</summary>
    public string OriginalText { get; private set; }

    private readonly List<LineEdit> _edits = new();
    public IReadOnlyList<LineEdit> Edits => _edits;
    public bool IsEdited => _edits.Count > 0;

    /// <summary>Tooltip / summary of the edit history, newest first.</summary>
    public string EditSummary => IsEdited
        ? "Edited " + _edits.Count + "×\n" + string.Join("\n", _edits.AsEnumerable().Reverse().Select(e =>
              $"{e.AtText} by {e.User}\nReason: {e.Reason}\nWas: “{e.OldText}”"))
          + $"\n\nOriginal transcription: “{OriginalText}”"
        : "";

    /// <summary>Corrects the text and records who, when and why.</summary>
    public LineEdit ApplyEdit(string newText, string reason, string? user = null)
    {
        var edit = new LineEdit(DateTimeOffset.Now, user ?? Environment.UserName, Text, newText.Trim(), reason.Trim());
        _edits.Add(edit);
        Text = edit.NewText;
        OnPropertyChanged(nameof(IsEdited));
        OnPropertyChanged(nameof(EditSummary));
        OnPropertyChanged(nameof(Edits));
        return edit;
    }

    /// <summary>Restores a saved edit history (library / JSON).</summary>
    public void RestoreHistory(string originalText, IEnumerable<LineEdit> edits)
    {
        OriginalText = originalText;
        _edits.Clear();
        _edits.AddRange(edits);
        OnPropertyChanged(nameof(IsEdited));
        OnPropertyChanged(nameof(EditSummary));
    }

    private SpeakerInfo? _speaker;
    public SpeakerInfo? Speaker { get => _speaker; set => Set(ref _speaker, value); }
}

/// <summary>A voice in one file or live session. Name and Sex are edited by the user.</summary>
public sealed class SpeakerInfo : Observable, IPlayable
{
    private bool _isPlaying;
    /// <summary>True while this speaker's sample clip is playing.</summary>
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }

    private static readonly string[] Palette = { "#36D2CB", "#E8A33D", "#E0607E", "#4E9BE8", "#9B7CF0", "#7DD957", "#F07CC4", "#D9734A" };

    /// <summary>A voice found automatically.</summary>
    public SpeakerInfo(DetectedSpeaker detected) : this(detected.Id)
    {
        Clip = detected.Clip;
        Embedding = detected.Embedding;
        TalkTime = TimeSpan.FromSeconds(detected.TalkSeconds);
        SuggestedSex = PitchEstimator.SuggestSex(detected.MedianPitchHz);
        _sex = SuggestedSex;
        PitchHz = detected.MedianPitchHz;
        PitchText = detected.MedianPitchHz is double hz ? $"~{hz:0} Hz" : "pitch n/a";
    }

    public double? PitchHz { get; }

    /// <summary>A voice added by hand.</summary>
    public SpeakerInfo(int id)
    {
        Id = id;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Palette[id % Palette.Length]));
        brush.Freeze();
        Color = brush;
    }

    public int Id { get; }
    public float[] Clip { get; set; } = Array.Empty<float>();
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public TimeSpan TalkTime { get; set; }
    public string SuggestedSex { get; } = Sexes.Unspecified;
    public string PitchText { get; } = "";
    public Brush Color { get; }
    public bool IsManual => PitchText.Length == 0;
    public bool HasClip => Clip.Length > 0;
    public string DefaultLabel => $"Speaker {Id + 1}";

    public string Details => IsManual
        ? $"{DefaultLabel} · added by you"
        : $"{DefaultLabel} · {TalkTime:mm\\:ss} talk · {PitchText} · suggests {SuggestedSex.ToLowerInvariant()}";

    private string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(Tag));
            // Picking someone from the voice library fills in their stored sex.
            if (VoiceLibrary.Shared.Find(value) is { } known && known.Sex != Sexes.Unspecified) Sex = known.Sex;
            OnPropertyChanged(nameof(IsKnownVoice));
        }
    }

    /// <summary>True when the name matches a voice in the voice library.</summary>
    public bool IsKnownVoice => VoiceLibrary.Shared.Find(_name) != null;

    private string? _matchHint;
    /// <summary>Closest voice-library match when it was below the recognition threshold, e.g. "closest: Anna 42%".</summary>
    public string? MatchHint { get => _matchHint; set => Set(ref _matchHint, value); }

    private string _sex = Sexes.Unspecified;
    public string Sex { get => _sex; set { if (Set(ref _sex, value)) OnPropertyChanged(nameof(Tag)); } }

    private bool _remember = true;
    public bool Remember { get => _remember; set => Set(ref _remember, value); }

    private string? _recognized;
    /// <summary>Set when the voice library matched this speaker.</summary>
    public string? Recognized { get => _recognized; set => Set(ref _recognized, value); }

    public string Label => string.IsNullOrWhiteSpace(Name) ? DefaultLabel : Name.Trim();
    public string Tag => Sex == Sexes.Unspecified || !AppSettings.Current.IncludeSexInTags ? Label : $"{Label} ({Sex})";

    public void Refresh()
    {
        OnPropertyChanged(nameof(Details)); OnPropertyChanged(nameof(HasClip)); OnPropertyChanged(nameof(Tag));
    }
}

/// <summary>Everything produced for one source file (or one live session).</summary>
public sealed class TranscriptResult
{
    public required string SourceFile { get; init; }
    public ObservableCollection<TranscriptLine> Lines { get; } = new();
    public ObservableCollection<SpeakerInfo> Speakers { get; } = new();
    public string? OutputDir { get; set; }
    private string? _media;

    /// <summary>
    /// Audio/video this transcript came from, if still available (used to learn voices of added speakers). Set with the
    /// stored name; reading it gives a path tools can open (an encrypted recording is decrypted to a private working copy
    /// only when something actually needs the audio).
    /// </summary>
    public string? MediaPath
    {
        get => _media != null && Services.Security.SecureFile.IsEncrypted(_media) ? Services.Security.SecureFile.Materialize(_media) : _media;
        set => _media = value;
    }

    /// <summary>The media as stored (may be an encrypted file's plaintext name), without decrypting anything.</summary>
    public string? MediaLogical => _media;
    /// <summary>Set when this transcript lives in the recordings library.</summary>
    public RecordingMeta? Library { get; set; }
    public bool HasSpeakers => Speakers.Count > 0;

    public SpeakerInfo AddSpeaker()
    {
        var s = new SpeakerInfo(Speakers.Count == 0 ? 0 : Speakers.Max(x => x.Id) + 1);
        Speakers.Add(s);
        return s;
    }

    /// <summary>The speaker with this person's name, added to the recording if they aren't in it yet.</summary>
    public SpeakerInfo SpeakerFor(string name)
    {
        var existing = Speakers.FirstOrDefault(s => string.Equals(s.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        var s = AddSpeaker();
        s.Name = name.Trim();   // also fills in the stored sex
        return s;
    }

    public void RemoveSpeaker(SpeakerInfo speaker)
    {
        foreach (var line in Lines.Where(l => l.Speaker == speaker)) line.Speaker = null;
        Speakers.Remove(speaker);
    }

    /// <summary>One line per timestamp: "[00:01:12] Anna (Female): text".</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        foreach (var line in Lines.Where(l => l.Text.Length > 0))
        {
            sb.Append('[').Append(line.Time).Append("] ");
            if (line.Speaker is { } s) sb.Append(s.Tag).Append(": ");
            sb.Append(line.Text);
            if (line.IsEdited) sb.Append(" [edited]");
            sb.Append('\n');
        }
        if (Lines.Any(l => l.IsEdited)) sb.Append('\n').Append(RenderEditLog());
        return sb.ToString();
    }

    /// <summary>Audit trail of every text correction, oldest first.</summary>
    public string RenderEditLog()
    {
        var sb = new StringBuilder("EDIT LOG\n");
        foreach (var (line, e) in Lines.SelectMany(l => l.Edits.Select(e => (l, e))).OrderBy(x => x.e.At))
        {
            sb.Append($"- {e.AtText} by {e.User} · line [{line.Time}]")
              .Append(line.Speaker != null ? $" ({line.Speaker.Label})" : "").Append('\n')
              .Append($"    Reason: {e.Reason}\n")
              .Append($"    Before: {e.OldText}\n")
              .Append($"    After:  {e.NewText}\n");
        }
        return sb.ToString();
    }

    public bool HasEdits => Lines.Any(l => l.IsEdited);

    public static TranscriptLine FromSegment(SegmentData s) => new(s.Start, s.End, s.Text, s.Language, s.Probability);

    /// <summary>Gives each line the speaker whose turns overlap it most (or the nearest turn).</summary>
    public void AssignSpeakers(IReadOnlyList<SpeakerTurn> turns)
    {
        if (turns.Count == 0) return;
        var ids = SpeakerAnalyzer.AssignLines(Lines.Select(l => (l.Start.TotalSeconds, l.End.TotalSeconds)).ToList(), turns);
        ApplySpeakerIds(ids);
    }

    /// <summary>Sets each line's speaker from an id per line (-1 = nobody).</summary>
    public void ApplySpeakerIds(IReadOnlyList<int> ids)
    {
        var byId = Speakers.ToDictionary(s => s.Id);
        for (int i = 0; i < ids.Count && i < Lines.Count; i++)
            Lines[i].Speaker = byId.GetValueOrDefault(ids[i]);
    }
}
