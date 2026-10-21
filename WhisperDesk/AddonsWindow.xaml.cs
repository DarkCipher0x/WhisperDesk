using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WhisperDesk.Services;
using WhisperDesk.Services.Addons;
using WhisperDesk.Services.Security;

namespace WhisperDesk;

public sealed class OptionValue
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string Help { get; init; } = "";
    public bool Secret { get; init; }
    public string Value { get; set; } = "";
}

/// <summary>Enable/disable add-ons, review what they may do, edit their settings and watch their log.</summary>
public partial class AddonsWindow : Window
{
    private readonly AddonManager _manager;
    private List<OptionValue> _options = new();

    public AddonsWindow(AddonManager manager)
    {
        InitializeComponent();
        _manager = manager;
        AddonList.ItemsSource = manager.Addons;
        LogList.ItemsSource = manager.Log;
        manager.Log.CollectionChanged += (_, _) => { if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]); };
        foreach (var a in manager.Addons) a.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (a == Selected) ShowDetails(a); });
        if (manager.Addons.Count > 0) AddonList.SelectedIndex = 0;
    }

    private AddonEntry? Selected => AddonList.SelectedItem as AddonEntry;
    private bool IsCaptionServer => Selected?.Id == "whisperdesk.caption-server";

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is { } entry) ShowDetails(entry);
    }

    private void ShowDetails(AddonEntry entry)
    {
        AddonName.Text = entry.Name;
        AddonDescription.Text = entry.Description;
        AddonRunsIn.Text = entry.Kind == AddonKind.BuiltIn
            ? "Built into WhisperDesk (first-party code, runs inside the app)."
            : $"Third-party ({entry.Origin}). Runs in a separate sandboxed process (Windows AppContainer): it can't read your files, " +
              "library or settings, and only gets what you approve below.";
        var approval = entry.Approval;
        var lines = new List<string>();
        foreach (var cap in entry.Requested)
        {
            bool granted = approval?.Capabilities.Contains(cap) == true;
            lines.Add($"{(granted ? "✓" : approval == null ? "•" : "✗")} {AddonCapabilities.Describe(cap)}" +
                      (cap == AddonCapabilities.Files && granted && approval!.FilesFolder != null ? $" ({approval.FilesFolder})" : ""));
        }
        if (entry.NetworkHosts.Count > 0) lines.Add("Says it connects to: " + string.Join(", ", entry.NetworkHosts));
        AddonPermissions.Text = (approval == null ? "Requests (not approved yet):\n" : "") + (lines.Count > 0 ? string.Join("\n", lines) : "No permissions.");
        AddonStatus.Text = entry.Status.Length > 0 ? "Status: " + entry.Status : "";
        RevokeButton.IsEnabled = approval != null;

        AppSettings.Current.AddonOptions.TryGetValue(entry.Id, out var saved);
        _options = entry.Options.Select(o => new OptionValue
        {
            Key = o.Key, Label = o.Label.ToUpperInvariant() + (o.Secret ? (SecretStore.Has(AddonManager.SecretScope(entry.Id), o.Key) ? "  (SAVED SECURELY)" : "  (NOT SET)") : ""),
            Help = o.Secret ? (o.Help + " Leave empty to keep the saved value; type a single - to remove it.").Trim() : o.Help,
            Secret = o.Secret,
            Value = o.Secret ? "" : saved != null && saved.TryGetValue(o.Key, out var v) ? v : o.DefaultValue,
        }).ToList();
        OptionsList.ItemsSource = _options;
        SaveOptionsButton.IsEnabled = _options.Count > 0;
        OpenPageButton.Visibility = CopyUrlButton.Visibility = IsCaptionServer ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if ((sender as CheckBox)?.Tag is not AddonEntry entry) return;
        bool on = (sender as CheckBox)!.IsChecked == true;
        try
        {
            if (on && !AddonManager.IsApproved(entry, out _) && !AskApproval(entry)) { (sender as CheckBox)!.IsChecked = false; return; }
            _manager.SetEnabled(entry, on);
        }
        catch (Exception ex)
        {
            (sender as CheckBox)!.IsChecked = false;
            MessageBox.Show(this, ex.Message, "Add-ons", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        AddonList.Items.Refresh();
        ShowDetails(entry);
    }

    private void OnReview(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        if (AskApproval(entry)) { _manager.Restart(entry); ShowDetails(entry); }
    }

    private void OnRevoke(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        if (MessageBox.Show(this, $"Switch off {entry.Name} and remove everything it was allowed (including secrets saved for it)?",
                "Remove permissions", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _manager.Revoke(entry);
        AddonList.Items.Refresh();
        ShowDetails(entry);
    }

    /// <summary>
    /// The approval dialog: each requested permission in plain language, ticked by default but each can be refused; the
    /// DLL's fingerprint for third-party add-ons; a clear warning when the combination can send data off this PC.
    /// </summary>
    private bool AskApproval(AddonEntry entry)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = $"{entry.Name} by {entry.Author}", FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        var where = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10), FontSize = 12,
            Text = entry.Kind == AddonKind.BuiltIn
                ? "Built into WhisperDesk."
                : $"Third-party code from {entry.Origin}. It runs in a sandbox and gets only what you tick." +
                  (entry.DllPath != null ? $"\nSHA-256: {AddonManager.Sha256(entry.DllPath)}" : "") +
                  (entry.HasManifest ? "" : "\nIt has no addon.json, so its needs are unknown: it is offered live transcript lines only."),
        };
        where.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(where);
        panel.Children.Add(new TextBlock { Text = "It asks to:", Margin = new Thickness(0, 0, 0, 4) });
        var boxes = new List<(CheckBox Box, string Cap)>();
        string? folder = entry.Approval?.FilesFolder;
        foreach (var cap in entry.Requested.Where(c => AddonCapabilities.All.Contains(c)))
        {
            var cb = new CheckBox { Content = AddonCapabilities.Describe(cap), IsChecked = entry.Approval == null || entry.Approval.Capabilities.Contains(cap), Margin = new Thickness(6, 3, 0, 3) };
            boxes.Add((cb, cap));
            panel.Children.Add(cb);
        }
        foreach (var unknown in entry.Requested.Where(c => !AddonCapabilities.All.Contains(c)))
            panel.Children.Add(new TextBlock { Text = $"✗ {unknown}: not a WhisperDesk permission, ignored", Margin = new Thickness(6, 3, 0, 3) });
        if (entry.NetworkHosts.Count > 0)
            panel.Children.Add(new TextBlock { Text = "Says it connects to: " + string.Join(", ", entry.NetworkHosts) + " (Windows can't limit it to these).",
                                               TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(6, 4, 0, 0) });
        var warn = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 10),
                                   Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xA3, 0x3D)) };
        void UpdateWarn()
        {
            var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Cap).ToList();
            warn.Text = AddonCapabilities.CanExfiltrate(chosen)
                ? "⚠ With these permissions it can send your transcripts off this PC. Only allow this for an add-on you trust."
                : "";
        }
        foreach (var (b, _) in boxes) { b.Checked += (_, _) => UpdateWarn(); b.Unchecked += (_, _) => UpdateWarn(); }
        UpdateWarn();
        panel.Children.Add(warn);

        var win = SpeakerDialogs.MakeWindow(this, "Add-on permissions", panel, out var ok, "Allow");
        win.Width = 560;
        ok.Click += (_, _) =>
        {
            var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Cap).ToList();
            if (chosen.Contains(AddonCapabilities.Files) && entry.Kind == AddonKind.External)
            {
                var dlg = new OpenFolderDialog { Title = $"Choose the folder {entry.Name} may write to" };
                if (dlg.ShowDialog(win) != true) return;
                if (AddonManager.FolderRefusal(dlg.FolderName) is { } why)
                {
                    MessageBox.Show(win, $"Choose another folder: {why}. Make a new, separate folder for this add-on's files.", "Add-on permissions");
                    return;
                }
                folder = dlg.FolderName;
            }
            win.DialogResult = true;
        };
        if (win.ShowDialog() != true) return false;
        _manager.Approve(entry, boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Cap), folder);
        return true;
    }

    private void OnSaveOptions(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        var plain = new Dictionary<string, string>();
        foreach (var o in _options)
        {
            var value = o.Value.Trim();
            if (!o.Secret) { plain[o.Key] = value; continue; }
            if (value.Length == 0) continue;                              // keep what is saved
            if (!SecretStore.CanStore) { MessageBox.Show(this, "Unlock WhisperDesk first.", "Add-ons"); return; }
            SecretStore.Set(AddonManager.SecretScope(entry.Id), o.Key, value == "-" ? null : value);
        }
        AppSettings.Current.AddonOptions[entry.Id] = plain;
        AppSettings.Current.Save();
        _manager.Restart(entry);
        _manager.WriteLog($"Saved settings for {entry.Name}{(entry.Enabled ? " (restarted)" : "")}.");
        ShowDetails(entry);
    }

    private string BaseUrl => $"http://localhost:{Services.Addons.BuiltIn.CaptionServerAddon.ParsePort(_options.FirstOrDefault(o => o.Key == "port")?.Value)}";

    private void OnOpenPage(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(BaseUrl + "/") { UseShellExecute = true });

    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        if (!Services.SafeClipboard.SetText(BaseUrl + "/overlay", this)) return;
        _manager.WriteLog($"Copied {BaseUrl}/overlay — in OBS add a Browser Source with this URL (width 1920, height 300).");
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AddonManager.AddonsFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AddonManager.AddonsFolder}\"") { UseShellExecute = true });
    }

    private void OnOpenGuide(object sender, RoutedEventArgs e)
    {
        var guide = Path.Combine(AppContext.BaseDirectory, "sdk", "README.md");
        if (File.Exists(guide)) Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true });
        else MessageBox.Show(this, "The developer guide is installed with WhisperDesk in the “sdk” folder next to WhisperDesk.exe.", "Add-ons");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
