using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>A person from the voice library who isn't in this recording yet (shown in the line speaker dropdown).</summary>
public sealed record VoicePick(string Name, string Sex)
{
    public string Tag => Sex == Sexes.Unspecified ? Name : $"{Name} ({Sex})";
}

/// <summary>
/// Fills a line's speaker dropdown with two groups: the speakers in this recording, then everyone else in the
/// voice library. Picking a library person adds them to the recording as a speaker and assigns the line.
/// Use on a ComboBox whose DataContext is a <see cref="TranscriptLine"/> inside a ListBox bound to the TranscriptResult.
/// </summary>
public static class SpeakerPicker
{
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(SpeakerPicker), new PropertyMetadata(false, OnEnableChanged));

    public static bool GetEnable(DependencyObject d) => (bool)d.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject d, bool v) => d.SetValue(EnableProperty, v);

    /// <summary>Bind to the line's Speaker so the box follows changes made elsewhere (assign menu, merge…).</summary>
    public static readonly DependencyProperty CurrentProperty = DependencyProperty.RegisterAttached(
        "Current", typeof(object), typeof(SpeakerPicker), new PropertyMetadata(null, OnCurrentChanged));

    public static object? GetCurrent(DependencyObject d) => d.GetValue(CurrentProperty);
    public static void SetCurrent(DependencyObject d, object? v) => d.SetValue(CurrentProperty, v);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox box || e.NewValue is not true) return;
        box.Loaded += (_, _) => Fill(box);
        box.DataContextChanged += (_, _) => { if (box.IsLoaded) Fill(box); };
        box.DropDownOpened += (_, _) => Fill(box, full: true);
        box.SelectionChanged += OnSelectionChanged;
    }

    private static void OnCurrentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox box || !box.IsLoaded || ReferenceEquals(box.SelectedItem, e.NewValue)) return;
        Fill(box);
    }

    private static TranscriptResult? ResultOf(ComboBox box)
    {
        DependencyObject? p = box;
        while (p != null && p is not ListBox) p = VisualTreeHelper.GetParent(p);
        return (p as ListBox)?.DataContext as TranscriptResult;
    }

    private static ComboBoxItem Header(string text) => new()
    {
        Content = text, IsEnabled = false, Focusable = false, FontSize = 11, FontWeight = FontWeights.SemiBold,
        Padding = new Thickness(6, 4, 6, 2),
    };

    /// <summary>
    /// The same grouping everywhere a speaker is picked (line dropdowns and the Assign menu):
    /// Voice library (everyone saved; people already in this recording appear as their speaker) ·
    /// Named in this recording (not in the library yet) · Unidentified voices · Named in other recordings.
    /// Items are <see cref="SpeakerInfo"/> (already in the recording) or <see cref="VoicePick"/> (added when picked).
    /// </summary>
    public static List<(string Header, List<object> Items)> Groups(TranscriptResult result)
    {
        static string Key(string n) => n.Trim();
        var cmp = StringComparer.OrdinalIgnoreCase;
        var byName = result.Speakers.Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => Key(s.Name), cmp).ToDictionary(g => g.Key, g => g.First(), cmp);
        var library = VoiceLibrary.Shared.Voices.OrderBy(v => v.Name, cmp)
            .Select(v => byName.TryGetValue(Key(v.Name), out var s) ? (object)s : new VoicePick(v.Name, v.Sex)).ToList();
        var known = new HashSet<string>(VoiceLibrary.Shared.Voices.Select(v => Key(v.Name)), cmp);
        var namedHere = result.Speakers.Where(s => !string.IsNullOrWhiteSpace(s.Name) && !known.Contains(Key(s.Name)))
            .OrderBy(s => s.Name, cmp).Cast<object>().ToList();
        var unidentified = result.Speakers.Where(s => string.IsNullOrWhiteSpace(s.Name)).OrderBy(s => s.Id).Cast<object>().ToList();
        var other = VoiceLibrary.Shared.RecordingNames
            .Where(r => !known.Contains(r.Name) && !byName.ContainsKey(r.Name))
            .OrderBy(r => r.Name, cmp).Select(r => (object)new VoicePick(r.Name, r.Sex)).ToList();

        var groups = new List<(string, List<object>)>();
        if (library.Count > 0) groups.Add(("VOICE LIBRARY", library));
        if (namedHere.Count > 0) groups.Add(("NAMED IN THIS RECORDING", namedHere));
        if (unidentified.Count > 0) groups.Add(("UNIDENTIFIED VOICES", unidentified));
        if (other.Count > 0) groups.Add(("NAMED IN OTHER RECORDINGS", other));
        return groups;
    }

    /// <param name="full">False: only the current speaker (cheap, for rows scrolling into view). True: every group
    /// (when the dropdown opens).</param>
    private static void Fill(ComboBox box, bool full = false)
    {
        if (box.DataContext is not TranscriptLine line) return;
        var items = new List<object>();
        if (full && ResultOf(box) is { } result)
            foreach (var (header, list) in Groups(result))
            {
                items.Add(Header(header));
                items.AddRange(list);
            }
        else if (line.Speaker != null) items.Add(line.Speaker);
        _filling = true;
        try
        {
            box.ItemsSource = items;
            box.SelectedItem = line.Speaker;
        }
        finally { _filling = false; }
    }

    private static bool _filling;

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || sender is not ComboBox box || box.DataContext is not TranscriptLine line) return;
        switch (box.SelectedItem)
        {
            case SpeakerInfo s:
                line.Speaker = s;
                break;
            case VoicePick pick when ResultOf(box) is { } result:
                var speaker = result.SpeakerFor(pick.Name);
                if (speaker.Sex == Sexes.Unspecified && pick.Sex != Sexes.Unspecified) speaker.Sex = pick.Sex;
                line.Speaker = speaker;
                box.Dispatcher.BeginInvoke(() => Fill(box));
                break;
        }
    }
}
