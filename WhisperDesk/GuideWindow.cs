using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>
/// Built-in viewer for the user &amp; admin guide (PDF) and the privacy &amp; security page, using the Edge WebView2
/// runtime that ships with Windows 10/11. "Open externally" hands the PDF to the default PDF app.
/// Navigation is locked to local content: links to the web open in the normal browser instead.
/// </summary>
public sealed class GuideWindow : Window
{
    public const string PrivacyPage = "privacy";
    public static string GuidePath => Path.Combine(AppContext.BaseDirectory, "WhisperDesk User Guide.pdf");

    private static GuideWindow? _open;
    private readonly WebView2 _web = new();
    private readonly string? _page;

    public static void Open(Window owner, string? page = null)
    {
        if (_open != null)
        {
            _open.Activate();
            if (page != _open._page) { _open.Close(); } else return;
        }
        if (page == null && !File.Exists(GuidePath))
        {
            MessageBox.Show(owner, "The user guide wasn't found next to WhisperDesk.exe.", "User guide");
            return;
        }
        _open = new GuideWindow(page) { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    private GuideWindow(string? page)
    {
        _page = page;
        Title = page == PrivacyPage ? "Privacy & security — WhisperDesk" : "User & admin guide — WhisperDesk";
        Width = 1000; Height = 820; MinWidth = 520; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = Application.Current.MainWindow?.Icon;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var bar = new DockPanel { Margin = new Thickness(10, 8, 10, 8) };
        var external = new Button { Content = page == PrivacyPage ? "Open the full guide" : "Open in PDF app", Margin = new Thickness(0) };
        external.Click += (_, _) =>
        {
            if (page == PrivacyPage) { Close(); Open(Owner); }
            else OpenExternally();
        };
        DockPanel.SetDock(external, Dock.Right);
        bar.Children.Add(external);
        var hint = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
            Text = page == PrivacyPage ? "How WhisperDesk handles your data." : "Ctrl+F to search · Ctrl+P to print · zoom with Ctrl + mouse wheel",
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        bar.Children.Add(hint);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_web);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            // Keep the viewer local: InPrivate, no scripts on the privacy page, links to the web open in the user's browser.
            await Services.Security.SafeWebView.InitAsync(_web, scripts: _page != PrivacyPage, allowed: _page == PrivacyPage ? null : new Uri(GuidePath));
            var core = _web.CoreWebView2;

            if (_page == PrivacyPage) core.NavigateToString(PrivacyInfo.Html(ThemeManager.IsDark));
            else _web.Source = new Uri(GuidePath);
        }
        catch (Exception ex)
        {
            // No WebView2 runtime (very old Windows 10): fall back to the default viewer.
            App.Log("Guide viewer", ex);
            if (_page != PrivacyPage) OpenExternally();
            else MessageBox.Show(this, PrivacyInfo.PlainText, Title);
            Close();
        }
    }

    private static void OpenExternally()
    {
        if (File.Exists(GuidePath)) Process.Start(new ProcessStartInfo(GuidePath) { UseShellExecute = true });
    }
}
