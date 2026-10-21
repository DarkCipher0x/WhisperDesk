using System.Windows;
using System.Windows.Controls;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>Small themed dialogs for the speakers panel (built in code like <see cref="InputDialog"/>).</summary>
public static class SpeakerDialogs
{
    public sealed record ReanalyseChoice(int SpeakerCount, bool CompareWithLibrary);

    /// <summary>Asks how to re-detect speakers. Warns when existing names/labels will be replaced.</summary>
    public static ReanalyseChoice? AskReanalyse(Window owner, TranscriptResult result, int defaultCount)
    {
        var named = result.Speakers.Where(s => !string.IsNullOrWhiteSpace(s.Name)).Select(s => s.Name.Trim()).Distinct().ToList();
        var count = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12) };
        count.Items.Add(new ComboBoxItem { Content = "Auto-detect", Tag = 0 });
        for (int i = 1; i <= 10; i++) count.Items.Add(new ComboBoxItem { Content = i.ToString(), Tag = i });
        count.SelectedIndex = Math.Clamp(defaultCount, 0, 10);

        var compare = new CheckBox
        {
            Content = "Compare with my voice library (auto-name voices it recognises)", IsChecked = true, Margin = new Thickness(0, 0, 0, 12),
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = "Runs speaker detection again on the recording's audio and re-tags every line. The text and edit history of each line are kept.",
        });
        panel.Children.Add(new TextBlock { Text = "Number of speakers" });
        panel.Children.Add(count);
        panel.Children.Add(compare);
        if (result.Speakers.Count > 0)
        {
            var warn = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14), FontWeight = FontWeights.SemiBold };
            warn.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            warn.Text = named.Count > 0
                ? $"⚠ This will overwrite the existing speaker labels, including the names you set ({string.Join(", ", named)}). " +
                  "Voices in your library are re-recognised if the comparison is ticked; other names must be set again."
                : $"⚠ This will overwrite the existing {result.Speakers.Count} speaker label(s) and every line's speaker.";
            panel.Children.Add(warn);
        }

        var win = MakeWindow(owner, "Re-analyse speakers", panel, out var ok, result.Speakers.Count > 0 ? "Overwrite & re-analyse" : "Analyse");
        ok.Click += (_, _) => win.DialogResult = true;
        if (win.ShowDialog() != true) return null;
        return new ReanalyseChoice((int)((ComboBoxItem)count.SelectedItem).Tag, compare.IsChecked == true);
    }

    /// <summary>One name + sex for several selected speakers. Returns null on cancel.</summary>
    public static (string Name, string Sex)? AskName(Window owner, IReadOnlyList<SpeakerInfo> speakers)
    {
        var first = speakers[0];
        var name = new ComboBox
        {
            IsEditable = true, Text = speakers.All(s => s.Name == first.Name) ? first.Name : "",
            ItemsSource = VoiceLibrary.Shared.Names, Margin = new Thickness(0, 4, 0, 12),
        };
        var sex = new ComboBox { ItemsSource = Sexes.All, Width = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 14) };
        sex.SelectedItem = speakers.All(s => s.Sex == first.Sex) ? first.Sex : Sexes.Unspecified;
        // Picking a known voice fills in its stored sex.
        name.SelectionChanged += (_, _) =>
        {
            if (name.SelectedItem is string n && VoiceLibrary.Shared.Find(n) is { } v && v.Sex != Sexes.Unspecified) sex.SelectedItem = v.Sex;
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = $"Name {speakers.Count} selected speaker(s): {string.Join(", ", speakers.Select(s => s.Label))}. " +
                   "Tip: if they are really the same person, use “Merge selected” instead.",
        });
        panel.Children.Add(new TextBlock { Text = "Name (type one, or pick from your voice library)" });
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Sex" });
        panel.Children.Add(sex);

        var win = MakeWindow(owner, "Name speakers", panel, out var ok, "Apply");
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => name.Focus();
        if (win.ShowDialog() != true) return null;
        return (name.Text.Trim(), sex.SelectedItem as string ?? Sexes.Unspecified);
    }

    /// <summary>A themed, centred dialog with Cancel + a primary button under <paramref name="panel"/>.</summary>
    internal static Window MakeWindow(Window owner, string title, StackPanel panel, out Button ok, string okText)
    {
        ok = new Button { Content = okText, IsDefault = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = title, Owner = owner, Content = panel, Width = 460, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            FontFamily = owner.FontFamily, FontSize = owner.FontSize,
        };
        win.SetResourceReference(Window.BackgroundProperty, "Bg");
        win.SetResourceReference(Window.ForegroundProperty, "Fg");
        win.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(win);
        return win;
    }
}
