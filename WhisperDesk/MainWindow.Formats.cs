using System.Windows;
using System.Windows.Controls;

namespace WhisperDesk;

/// <summary>The "Save as" output-format dropdown (multi-select, with a saved default).</summary>
public partial class MainWindow
{
    private (CheckBox Box, string Key, string Short)[] FormatBoxes => new[]
    {
        (TxtBox, "txt", "TXT"), (SrtBox, "srt", "SRT"), (VttBox, "vtt", "VTT"), (JsonBox, "json", "JSON"),
        (DocxBox, "docx", "Word"), (PdfBox, "pdf", "PDF"), (SummaryBox, "summary", "Summary"), (BurnBox, "burn", "Burn-in"),
    };

    private void OnFormatChanged(object sender, RoutedEventArgs e) => UpdateFormatsText();

    private void UpdateFormatsText()
    {
        if (FormatsText == null) return;
        var on = FormatBoxes.Where(f => f.Box.IsChecked == true).Select(f => f.Short).ToList();
        FormatsText.Text = on.Count == 0 ? "Nothing selected" : string.Join(", ", on);
        var defaults = _settings.DefaultFormats;
        DefaultFormatsText.Text = "Default: " + (defaults.Count == 0 ? "none"
            : string.Join(", ", FormatBoxes.Where(f => defaults.Contains(f.Key)).Select(f => f.Short)));
    }

    // Clicking the button while the popup is open would close and immediately reopen it.
    private void OnFormatsOpened(object? sender, EventArgs e) => FormatsButton.IsHitTestVisible = false;
    private void OnFormatsClosed(object? sender, EventArgs e) => FormatsButton.IsHitTestVisible = true;

    private void OnSaveDefaultFormats(object sender, RoutedEventArgs e)
    {
        _settings.DefaultFormats = FormatBoxes.Where(f => f.Box.IsChecked == true).Select(f => f.Key).ToList();
        _settings.Save();
        UpdateFormatsText();
        StatusText.Text = "Saved as your default output formats.";
    }

    private void OnUseDefaultFormats(object sender, RoutedEventArgs e)
    {
        foreach (var (box, key, _) in FormatBoxes) box.IsChecked = _settings.DefaultFormats.Contains(key);
        UpdateFormatsText();
    }
}
