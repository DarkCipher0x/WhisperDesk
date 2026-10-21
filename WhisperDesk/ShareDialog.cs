using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>
/// Import/Export → Share: pick a format, then either send it with the Windows share sheet (Mail, Teams, Nearby
/// Sharing…) or save it as a file.
/// </summary>
public static class ShareDialog
{
    private sealed record Format(string Key, string Label, string Hint, string Ext, string Filter);

    private static readonly Format[] Formats =
    {
        new("html", "Web player (.html)", "Opens in any browser: audio + clickable transcript. Nothing to install.", ".html", "Web player (*.html)|*.html"),
        new("session", "Session package (.wdsession)", "For another WhisperDesk user: transcript, speakers and audio.", ".wdsession", "WhisperDesk session (*.wdsession)|*.wdsession"),
        new("pdf", "Transcript (PDF)", "Printable transcript with speakers and timestamps.", ".pdf", "PDF (*.pdf)|*.pdf"),
        new("docx", "Transcript (Word)", "Editable transcript.", ".docx", "Word document (*.docx)|*.docx"),
        new("txt", "Transcript (plain text)", "Lightweight text with timestamps.", ".txt", "Text (*.txt)|*.txt"),
        new("summary", "Summary report (PDF)", "Overview, what was said, participation (and the AI report if enabled).", ".summary.pdf", "PDF (*.pdf)|*.pdf"),
    };

    public static void Show(Window owner, RecordingMeta meta, TranscriptResult result)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Share “{meta.Title}”", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var radios = new List<RadioButton>();
        var last = AppSettings.Current.LastShareFormat;
        foreach (var f in Formats)
        {
            var rb = new RadioButton { GroupName = "fmt", Tag = f, Margin = new Thickness(0, 0, 0, 6), IsChecked = f.Key == last };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = f.Label });
            var hint = new TextBlock { Text = f.Hint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            sp.Children.Add(hint);
            rb.Content = sp;
            radios.Add(rb);
            panel.Children.Add(rb);
        }
        if (radios.All(r => r.IsChecked != true)) radios[0].IsChecked = true;
        if (meta.MediaFile == null)
            foreach (var r in radios.Where(r => ((Format)r.Tag).Key is "html" or "session"))
                ((StackPanel)r.Content).Children.Add(new TextBlock { Text = "(transcript only: this recording has no audio)", FontSize = 11, FontStyle = FontStyles.Italic });

        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 12),
            Text = "Privacy: voiceprints are never included. With “Share with Windows”, the file goes only to the app you pick in the share sheet.",
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(note);

        var win = SpeakerDialogs.MakeWindow(owner, "Share", panel, out var shareButton, WindowsShare.IsSupported ? "Share with Windows…" : "Save as…");
        var save = new Button { Content = "Save as…" };
        if (WindowsShare.IsSupported) ((StackPanel)panel.Children[^1]).Children.Insert(1, save);

        Format Selected() => (Format)radios.First(r => r.IsChecked == true).Tag;

        async Task Run(bool viaWindows)
        {
            var f = Selected();
            AppSettings.Current.LastShareFormat = f.Key;
            AppSettings.Current.Save();
            string path;
            if (viaWindows)
                path = Path.Combine(WindowsShare.TempFolder(), RecordingLibrary.Sanitize(meta.Title) + f.Ext);
            else
            {
                var dlg = new SaveFileDialog { Title = "Save as", FileName = RecordingLibrary.Sanitize(meta.Title) + f.Ext, Filter = f.Filter };
                if (dlg.ShowDialog(win) != true) return;
                path = dlg.FileName;
            }
            try
            {
                shareButton.IsEnabled = save.IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;
                await WriteAsync(f.Key, meta, result, path);
                Mouse.OverrideCursor = null;
                if (viaWindows)
                {
                    win.DialogResult = true;
                    await WindowsShare.ShareFilesAsync(owner, meta.Title, $"{f.Label} from WhisperDesk", new[] { path });
                }
                else
                {
                    win.DialogResult = true;
                    if (MessageBox.Show(owner, $"Saved {Path.GetFileName(path)} ({new FileInfo(path).Length / 1048576.0:0.0} MB).\n\nShow it in Explorer?",
                            "Share", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                        Process.Start("explorer.exe", $"/select,\"{path}\"");
                }
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show(owner, "Could not share: " + ex.Message, "Share", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                shareButton.IsEnabled = save.IsEnabled = true;
            }
        }

        shareButton.Click += async (_, _) => await Run(WindowsShare.IsSupported);
        save.Click += async (_, _) => await Run(false);
        win.ShowDialog();
    }

    private static async Task WriteAsync(string key, RecordingMeta meta, TranscriptResult result, string path)
    {
        switch (key)
        {
            case "html": await SessionPackage.ExportHtmlAsync(meta, result, path); break;
            case "session": await SessionPackage.ExportAsync(meta, path); break;
            case "pdf": await Task.Run(() => DocumentExporter.WritePdf(result, meta.Title, path)); break;
            case "docx": await Task.Run(() => DocumentExporter.WriteDocx(result, meta.Title, path)); break;
            case "txt": await File.WriteAllTextAsync(path, result.Render()); break;
            case "summary":
                var summary = await CallSummary.BuildAsync(result, meta.Title, meta.Created, null, CancellationToken.None);
                await Task.Run(() => DocumentExporter.WriteSummaryPdf(summary, path));
                break;
        }
    }
}
