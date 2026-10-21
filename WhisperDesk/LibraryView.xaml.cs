using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>One folder in the library tree (or the "All recordings" root).</summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public int Count { get; set; }
    public bool IsAll { get; init; }
    public ObservableCollection<FolderNode> Children { get; } = new();
    public string Icon => IsAll ? "🗂" : IsExpanded && Children.Count > 0 ? "📂" : "📁";
    public string CountText => Count > 0 ? $"({Count})" : "";

    private bool _expanded;
    public bool IsExpanded { get => _expanded; set { if (Set(ref _expanded, value)) OnChanged(nameof(Icon)); } }
    private bool _selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    public IEnumerable<FolderNode> SelfAndDescendants() => new[] { this }.Concat(Children.SelectMany(c => c.SelfAndDescendants()));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name!);
        return true;
    }
}

/// <summary>Recordings library: folder tree, search, type filter, open / play / edit / move / delete / re-transcribe / report.</summary>
public partial class LibraryView : UserControl
{
    private const string AllTypes = "All types";
    private List<RecordingMeta> _all = new();
    private readonly Dictionary<string, string> _textCache = new();
    private FolderNode? _root;

    /// <summary>Raised when the user wants a recording's transcript opened in the editor below.</summary>
    public event Action<RecordingMeta>? OpenRequested;
    /// <summary>Raised when the user asks to re-detect a recording's speakers (the main window owns the analyser).</summary>
    public event Action<RecordingMeta>? ReanalyseRequested;
    /// <summary>Raised to transcribe recordings again with the current model (the main window owns the engine).</summary>
    public event Action<IReadOnlyList<RecordingMeta>>? RetranscribeRequested;

    public LibraryView()
    {
        InitializeComponent();
        TypeFilter.ItemsSource = new[] { AllTypes }.Concat(RecordingLibrary.Types).ToList();
        TypeFilter.SelectedIndex = 0;
    }

    private FolderNode? SelectedNode => FolderTree.SelectedItem as FolderNode;
    private string SelectedFolder => SelectedNode is { IsAll: false } f ? f.Path : "";
    private bool AllSelected => SelectedNode is not { IsAll: false };
    private RecordingMeta? Selected => RecordingList.SelectedItem as RecordingMeta;
    private List<RecordingMeta> SelectedMany => RecordingList.SelectedItems.Cast<RecordingMeta>().ToList();
    public RecordingMeta? SelectedRecording => Selected;
    public IReadOnlyList<RecordingMeta> SelectedRecordings => SelectedMany;
    private Window Owner => Window.GetWindow(this);

    public void Refresh(string? selectFolder = null, string? selectId = null)
    {
        var keep = selectFolder ?? (AllSelected ? null : SelectedFolder);
        var keepIds = selectId != null ? new HashSet<string> { selectId } : SelectedMany.Select(r => r.Id).ToHashSet();
        var expanded = _root?.SelfAndDescendants().Where(n => n.IsExpanded && !n.IsAll).Select(n => n.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
                       ?? AppSettings.Current.ExpandedFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scroll = RecordingList.Items.Count > 0 ? FindScroll(RecordingList)?.VerticalOffset : null;
        _all = RecordingLibrary.All();
        _textCache.Clear();
        VoiceLibrary.Shared.RefreshRecordingNames();   // names offered in the speaker dropdowns

        // Build the folder tree.
        _root = new FolderNode { Path = "", Name = "All recordings", Count = _all.Count, IsAll = true, IsExpanded = true };
        var byPath = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in RecordingLibrary.Folders())
        {
            var node = new FolderNode
            {
                Path = f, Name = System.IO.Path.GetFileName(f),
                Count = _all.Count(r => r.Folder.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                                        r.Folder.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)),
            };
            node.IsExpanded = expanded.Contains(f);
            byPath[f] = node;
            var parent = System.IO.Path.GetDirectoryName(f);
            (string.IsNullOrEmpty(parent) || !byPath.TryGetValue(parent, out var p) ? _root : p).Children.Add(node);
        }
        FolderTree.ItemsSource = new[] { _root };
        var target = keep != null && byPath.TryGetValue(keep, out var k) ? k : _root;
        for (var dir = System.IO.Path.GetDirectoryName(target.Path); !string.IsNullOrEmpty(dir); dir = System.IO.Path.GetDirectoryName(dir))
            if (byPath.TryGetValue(dir, out var anc)) anc.IsExpanded = true;   // reveal the selected folder
        target.IsSelected = true;

        ApplyFilter();
        if (RecordingList.ItemsSource is List<RecordingMeta> shown)
        {
            var reselect = shown.Where(r => keepIds.Contains(r.Id)).ToList();
            foreach (var r in reselect) RecordingList.SelectedItems.Add(r);
            if (reselect.Count > 0)
            {
                if (scroll is { } y && FindScroll(RecordingList) is { } sv) { RecordingList.UpdateLayout(); sv.ScrollToVerticalOffset(y); }
                RecordingList.ScrollIntoView(reselect[0]);
            }
        }
    }

    private static ScrollViewer? FindScroll(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    private void ApplyFilter()
    {
        IEnumerable<RecordingMeta> items = _all;
        if (!AllSelected)
        {
            var f = SelectedFolder;
            items = items.Where(r => r.Folder.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                                     r.Folder.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));
        }
        if (TypeFilter.SelectedItem is string t && t != AllTypes) items = items.Where(r => r.Type == t);

        var q = SearchBox.Text.Trim();
        if (q.Length > 0) items = items.Where(r => Matches(r, q));

        var list = items.ToList();
        RecordingList.ItemsSource = list;
        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool Matches(RecordingMeta r, string q)
    {
        bool Has(string? s) => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
        if (Has(r.Title) || Has(r.Type) || Has(r.Notes) || Has(r.Source) || Has(r.SpeakerNames) || r.Tags.Any(Has)) return true;

        // Full-text search in the transcript (cached).
        if (!_textCache.TryGetValue(r.Id, out var text))
        {
            try { text = RecordingLibrary.ReadTranscriptText(r); } catch { text = ""; }
            _textCache[r.Id] = text;
        }
        return Has(text);
    }

    private void OnFolderChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => ApplyFilter();
    private void OnFilterChanged(object sender, EventArgs e) { if (IsLoaded) ApplyFilter(); }
    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>Remembers which folders are expanded (saved with the settings).</summary>
    public void SaveTreeState()
    {
        if (_root == null) return;
        AppSettings.Current.ExpandedFolders = _root.SelfAndDescendants().Where(n => n.IsExpanded && !n.IsAll).Select(n => n.Path).ToList();
    }

    // ----- Folders -----

    private void OnExpandAll(object sender, RoutedEventArgs e) { foreach (var n in _root?.SelfAndDescendants() ?? Enumerable.Empty<FolderNode>()) n.IsExpanded = true; }
    private void OnCollapseAll(object sender, RoutedEventArgs e) { foreach (var n in _root?.SelfAndDescendants() ?? Enumerable.Empty<FolderNode>()) n.IsExpanded = n.IsAll; }

    private void OnNewFolder(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(Owner, "New folder", AllSelected ? "Folder name:" : $"New folder inside “{SelectedFolder}”:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        Refresh(RecordingLibrary.CreateFolder(AllSelected ? "" : SelectedFolder, name));
    }

    private void OnRenameFolder(object sender, RoutedEventArgs e)
    {
        if (AllSelected) return;
        var old = SelectedFolder;
        var name = InputDialog.Ask(Owner, "Rename folder", "New name:", System.IO.Path.GetFileName(old));
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Refresh(RecordingLibrary.RenameFolder(old, name));
        }
        catch (Exception ex) { MessageBox.Show(Owner, ex.Message, "Rename folder"); }
    }

    private void OnDeleteFolder(object sender, RoutedEventArgs e)
    {
        if (AllSelected) return;
        var folder = SelectedFolder;
        var count = InFolder(folder).Count;
        var msg = count == 0 ? $"Delete the empty folder “{folder}”?"
                             : $"Delete “{folder}” and the {count} recording(s) inside it? This cannot be undone.";
        if (MessageBox.Show(Owner, msg, "Delete folder", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RecordingLibrary.DeleteFolder(folder);
        Refresh("");
    }

    private List<RecordingMeta> InFolder(string folder) => folder.Length == 0 ? _all
        : _all.Where(r => r.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                          r.Folder.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)).ToList();

    private void OnRetranscribeFolder(object sender, RoutedEventArgs e)
    {
        var list = InFolder(SelectedFolder);
        if (list.Count > 0) RetranscribeRequested?.Invoke(list);
    }

    private void OnRevealFolder(object sender, RoutedEventArgs e)
    {
        // With encryption on, folders only exist inside the encrypted index: never create a plaintext folder named after one.
        var dir = Services.Security.Vault.IsEnabled ? RecordingLibrary.Root : System.IO.Path.Combine(RecordingLibrary.Root, SelectedFolder);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // ----- Drag recordings onto a folder to move them -----

    private Point _dragStart;

    private void OnListMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || SelectedMany.Count == 0) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (e.OriginalSource is DependencyObject src && FindParent<ListViewItem>(src) == null) return;
        DragDrop.DoDragDrop(RecordingList, new DataObject(typeof(List<RecordingMeta>), SelectedMany), DragDropEffects.Move);
    }

    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(List<RecordingMeta>)) && NodeAt(e) != null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFolderDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(List<RecordingMeta>)) is not List<RecordingMeta> list || NodeAt(e) is not { } node) return;
        MoveAll(list, node.IsAll ? "" : node.Path);
    }

    private FolderNode? NodeAt(DragEventArgs e) =>
        e.OriginalSource is DependencyObject d && FindParent<TreeViewItem>(d)?.DataContext is FolderNode n ? n : null;

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    private void MoveAll(IReadOnlyList<RecordingMeta> list, string folder)
    {
        var errors = new List<string>();
        foreach (var r in list)
        {
            try { RecordingLibrary.Move(r, folder); }
            catch (Exception ex) { errors.Add($"{r.Title}: {ex.Message}"); }
        }
        Refresh(selectId: list.Count == 1 ? list[0].Id : null);
        if (errors.Count > 0) MessageBox.Show(Owner, string.Join("\n", errors), "Move recordings");
    }

    // ----- Recordings -----

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { OnDelete(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter) { OnOpen(sender, e); e.Handled = true; }
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => RecordingList.SelectAll();

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (Selected is { } r) OpenRequested?.Invoke(r);
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        try
        {
            new PlayerWindow(r, RecordingLibrary.Load(r), isImported: false) { Owner = Owner }.Show();
        }
        catch (Exception ex) { MessageBox.Show(Owner, "Could not open the player: " + ex.Message, "Player"); }
    }

    private void OnForensics(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        try { new PlayerWindow(r, RecordingLibrary.Load(r), isImported: false) { Owner = Owner }.ShowForensics().Show(); }
        catch (Exception ex) { MessageBox.Show(Owner, "Could not open: " + ex.Message, "Forensics"); }
    }

    private async void OnShare(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        try { await PlayerWindow.ShareAsync(Owner, r, RecordingLibrary.Load(r)); }
        catch (Exception ex) { MessageBox.Show(Owner, "Could not share: " + ex.Message, "Share"); }
    }

    private void OnBulkReport(object sender, RoutedEventArgs e)
    {
        var list = SelectedMany;
        if (list.Count == 0) list = (RecordingList.ItemsSource as List<RecordingMeta>) ?? new();
        if (list.Count == 0) { MessageBox.Show(Owner, "Select one or more recordings first (Ctrl+click or Shift+click).", "Report"); return; }
        new BulkReportWindow(list) { Owner = Owner }.Show();
    }

    private void OnRetranscribe(object sender, RoutedEventArgs e)
    {
        var list = SelectedMany;
        if (list.Count > 0) RetranscribeRequested?.Invoke(list);
    }

    private void OnReanalyse(object sender, RoutedEventArgs e)
    {
        if (Selected is { } r) ReanalyseRequested?.Invoke(r);
    }

    private void OnSummary(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        try { new SummaryWindow(RecordingLibrary.Load(r), r.Title) { Owner = Owner }.ShowDialog(); }
        catch (Exception ex) { MessageBox.Show(Owner, "Could not summarise: " + ex.Message, "Summary"); }
    }

    /// <summary>Enables what makes sense for the selection and fills "Move to folder" each time the menu opens.</summary>
    private void OnRecordingMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var many = SelectedMany;
        bool one = many.Count == 1, any = many.Count > 0;
        foreach (var mi in new[] { MenuOpen, MenuPlay, MenuForensics, MenuSummary, MenuShare, MenuReanalyse, MenuEdit, MenuReveal }) mi.IsEnabled = one;
        MenuReport.IsEnabled = true;
        MenuReport.Header = many.Count > 1 ? $"Report on {many.Count} selected recordings…" : "Report on recordings…";
        MenuRetranscribe.IsEnabled = any;
        MenuRetranscribe.Header = many.Count > 1 ? $"Re-transcribe {many.Count} recordings…" : "Re-transcribe…";
        MenuDelete.IsEnabled = any;
        MenuDelete.Header = many.Count > 1 ? $"Delete {many.Count} recordings…" : "Delete…";
        MoveMenu.IsEnabled = any;
        MoveMenu.Items.Clear();
        if (!any) return;
        foreach (var f in new[] { "" }.Concat(RecordingLibrary.Folders()))
        {
            var mi = new MenuItem
            {
                Header = f.Length == 0 ? "(root)" : f.Replace("\\", " / "),
                IsEnabled = !many.All(r => f.Equals(r.Folder, StringComparison.OrdinalIgnoreCase)),
            };
            var target = f;
            mi.Click += (_, _) => MoveAll(many, target);
            MoveMenu.Items.Add(mi);
        }
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        var dir = Selected?.Dir ?? System.IO.Path.Combine(RecordingLibrary.Root, SelectedFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        AppSettings.Current.LastLibraryFolder = r.Folder;
        var dlg = new SaveToLibraryWindow(r, hasExternalMedia: false, info: $"Source: {r.Source}") { Owner = Owner, Title = "Recording details — WhisperDesk" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            RecordingLibrary.SaveMeta(r);
            if (!dlg.Folder.Equals(r.Folder, StringComparison.OrdinalIgnoreCase)) RecordingLibrary.Move(r, dlg.Folder);
            Refresh(selectId: r.Id);
        }
        catch (Exception ex) { MessageBox.Show(Owner, ex.Message, "Edit details"); }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var list = SelectedMany;
        if (list.Count == 0) return;
        var withAudio = list.Count(r => r.MediaFile != null);
        var msg = list.Count == 1
            ? $"Delete “{list[0].Title}”{(withAudio > 0 ? " and its audio" : "")} from the library? This cannot be undone."
            : $"Delete these {list.Count} recordings{(withAudio > 0 ? $" ({withAudio} with audio)" : "")} from the library? This cannot be undone.";
        if (MessageBox.Show(Owner, msg, "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var r in list)
        {
            try { RecordingLibrary.Delete(r); }
            catch (Exception ex) { MessageBox.Show(Owner, $"{r.Title}: {ex.Message}", "Delete"); }
        }
        Refresh();
    }
}
