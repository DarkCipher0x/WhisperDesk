using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WhisperDesk.Addons;
using WhisperDesk.Services;
using WhisperDesk.Services.Addons;
using Whisper.net;

namespace WhisperDesk;

public partial class MainWindow : Window
{
    private static readonly Dictionary<string, string> Languages = new()
    {
        ["auto"] = "Auto-detect", ["en"] = "English", ["da"] = "Danish", ["no"] = "Norwegian", ["sv"] = "Swedish",
        ["fi"] = "Finnish", ["de"] = "German", ["nl"] = "Dutch", ["fr"] = "French", ["es"] = "Spanish",
        ["it"] = "Italian", ["pt"] = "Portuguese", ["pl"] = "Polish", ["ru"] = "Russian", ["uk"] = "Ukrainian",
        ["tr"] = "Turkish", ["ar"] = "Arabic", ["hi"] = "Hindi", ["ja"] = "Japanese", ["ko"] = "Korean", ["zh"] = "Chinese",
    };

    private readonly ObservableCollection<QueueItem> _queue = new();
    private readonly TranscriptionEngine _engine = new();
    private readonly AppSettings _settings = AppSettings.Current;
    /// <summary>AppSettings.Hardware values, in the order of the HARDWARE box.</summary>
    private static readonly string[] HardwareKeys = { "Auto", "NPU", "GPU", "CPU" };
    private string SelectedHardware => HardwareKeys[Math.Clamp(DeviceBox.SelectedIndex, 0, HardwareKeys.Length - 1)];
    private readonly SpeakerAnalyzer _speakerAnalyzer = new();
    private readonly VoiceLibrary _voices = VoiceLibrary.Shared;
    private CancellationTokenSource? _cts;
    private string? _outputFolder;
    private TranscriptResult? _shown;
    private QueueItem? _processing;
    private readonly LinePlayer _linePlayer = new();

    // Live
    private readonly LiveAudioSource _audio = new();
    private LiveSession? _live;
    private TranscriptResult? _liveResult;
    private AudioDeviceInfo? _liveDevice;
    private readonly System.Windows.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool LiveRunning => _live != null;

    // Library
    private readonly LibraryView _library = new();

    // Add-ons
    private readonly AddonManager _addons = new(AboutWindow.Version);
    private string _liveSessionId = "";

    public MainWindow()
    {
        InitializeComponent();

        VersionText.Text = "v" + AboutWindow.Version;
        QueueList.ItemsSource = _queue;
        _queue.CollectionChanged += (_, _) => DropHint.Visibility = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RefreshModels(_settings.Model);
        DeviceBox.SelectedIndex = Math.Max(0, Array.IndexOf(HardwareKeys, _settings.EffectiveHardware));
        _ = Services.Npu.NpuDetector.ProbeAsync();   // background: is there a working NPU? (Settings → Transcription shows it)
        // Once the window is up and idle: get the selected model ready on the NPU, so the first Start doesn't wait.
        Loaded += async (_, _) => { await Task.Delay(1500); PrepareNpuInBackground(); };
        LanguageBox.ItemsSource = Languages;
        LanguageBox.SelectedValue = Languages.ContainsKey(_settings.Language) ? _settings.Language : "auto";
        TranslateBox.IsChecked = _settings.Translate;
        TxtBox.IsChecked = _settings.Txt;
        SrtBox.IsChecked = _settings.Srt;
        VttBox.IsChecked = _settings.Vtt;
        JsonBox.IsChecked = _settings.Json;
        BurnBox.IsChecked = _settings.BurnSubtitles;
        DocxBox.IsChecked = _settings.Docx;
        PdfBox.IsChecked = _settings.Pdf;
        SummaryBox.IsChecked = _settings.Summary;
        UpdateFormatsText();
        SpeakersBox.IsChecked = _settings.IdentifySpeakers;
        SpeakerCountBox.SelectedItem = SpeakerCountBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == _settings.SpeakerCount.ToString()) ?? SpeakerCountBox.Items[0];
        SetOutputFolder(_settings.OutputFolder);
        ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == ThemeManager.Mode.ToString());

        _addons.LoadAll();
        // Lock: stop playback and drop decoded audio; unlock: read the (encrypted) library again.
        LockMenuItem.IsEnabled = Services.Security.Vault.IsEnabled;
        Services.Security.AppLock.Locking += () =>
        {
            _linePlayer.Stop();
            _linePlayer.Invalidate();
            // After the key is gone: the library list and its search cache are rebuilt without the encrypted recordings.
            Dispatcher.BeginInvoke(() => _library.Refresh(), DispatcherPriority.Background);
        };
        Services.Security.AppLock.Unlocked += () => { _library.Refresh(); VoiceLibrary.Shared.RefreshRecordingNames(); };
        StateChanged += (_, _) => Services.Security.AppLock.OnWindowStateChanged(this);
        LibraryHost.Content = _library;
        _library.OpenRequested += OpenFromLibrary;
        _library.RetranscribeRequested += OnLibraryRetranscribe;
        InitSchedule();
        VoiceLibrary.Shared.RefreshRecordingNames();
        try { UpdateStorageBanner(); } catch (Exception ex) { App.Log("Storage check", ex); }
        _autosave.Tick += (_, _) => AutosaveLive();
        App.EmergencySave = () =>
        {
            _live?.FlushRecording();
            try { Dispatcher.Invoke(AutosaveLive, TimeSpan.FromSeconds(3)); } catch { }
        };
        Loaded += (_, _) => Dispatcher.BeginInvoke(RecoverLiveSessions, DispatcherPriority.ApplicationIdle);
        _library.ReanalyseRequested += async meta =>
        {
            if (_cts != null) { StatusText.Text = "Wait for the current job to finish first."; return; }
            OpenFromLibrary(meta);
            if (_shown?.Library?.Id == meta.Id) await ReanalyseAsync(_shown);
        };
        LoadDevices();
        _audio.LevelChanged += level => Dispatcher.BeginInvoke(() => LiveLevel.Value = Math.Min(1, level * 1.5));
        _clock.Tick += (_, _) =>
        {
            if (_live == null) return;
            LiveClock.Text = _live.Elapsed.ToString(@"hh\:mm\:ss");
            var pending = _live.PendingUtterances;
            StatusText.Text = pending > 0 ? $"Live · transcribing… ({pending} waiting)" : "Live · listening…";
            if (_engine.NpuFallbackReason != null && RuntimeText.Text != _engine.Badge)
                RuntimeText.Text = _engine.Badge;   // the NPU failed mid-session: the badge says so ("· npu failed")
            if (_engine.NpuFallbackReason != null && RuntimeText.Text.EndsWith("npu failed")) StatusText.Text += "  ⚠ NPU failed, now on the GPU/CPU";
        };

        // Files passed on the command line (e.g. "Open with → WhisperDesk", or a double-clicked .wdsession)
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        AddPaths(args.Where(a => !IsSession(a)));
        if (_queue.Count == 0) StatusText.Text = "Ready. Drop audio or video files here.";
        var sessions = args.Where(IsSession).ToList();
        if (sessions.Count > 0) Loaded += (_, _) => sessions.ForEach(OpenSession);
    }

    private static bool IsSession(string path) => path.EndsWith(SessionPackage.Extension, StringComparison.OrdinalIgnoreCase);

    private void OnOpenSession(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "WhisperDesk session (*.wdsession)|*.wdsession", Title = "Open a shared session" };
        if (dlg.ShowDialog(this) == true) OpenSession(dlg.FileName);
    }

    /// <summary>Opens a shared .wdsession in the player (read-only until “Add to my library”).</summary>
    private void OpenSession(string path)
    {
        try
        {
            var (meta, result) = SessionPackage.Open(path);
            var player = new PlayerWindow(meta, result, isImported: true) { Owner = this };
            player.Imported += saved =>
            {
                StatusText.Text = $"Added “{saved.Title}” to your library.";
                if (MainTabs.SelectedItem == LibraryTab) _library.Refresh();
            };
            player.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open {Path.GetFileName(path)}: {ex.Message}", "Open session", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private ModelInfo SelectedModel => (ModelInfo)ModelBox.SelectedItem;
    private int SelectedSpeakerCount => int.Parse((string)((ComboBoxItem)SpeakerCountBox.SelectedItem).Tag);

    // ---------- Queue management ----------

    private void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (IsSession(path) && File.Exists(path)) { OpenSession(path); continue; }
            if (Directory.Exists(path))
            {
                AddPaths(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Where(AudioDecoder.IsSupported).OrderBy(p => p));
            }
            else if (File.Exists(path) && AudioDecoder.IsSupported(path)
                     && !_queue.Any(q => string.Equals(q.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                _queue.Add(new QueueItem(path));
            }
        }
        StatusText.Text = $"{_queue.Count} file(s) in queue.";
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _cts == null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_cts == null && e.Data.GetData(DataFormats.FileDrop) is string[] files) AddPaths(files);
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var exts = string.Join(";", AudioDecoder.SupportedExtensions.Select(x => "*" + x));
        var dlg = new OpenFileDialog { Multiselect = true, Filter = $"Audio & video|{exts}|All files|*.*" };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var dlg = new OpenFolderDialog { Title = "Add all audio/video files in a folder" };
        if (dlg.ShowDialog(this) == true) AddPaths(new[] { dlg.FolderName });
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        foreach (var item in QueueList.SelectedItems.Cast<QueueItem>().Where(i => i.State != ItemState.Working).ToList())
            RemoveFromQueue(item);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        foreach (var item in _queue.Where(i => i.State != ItemState.Working).ToList()) RemoveFromQueue(item);
    }

    /// <summary>Removes a file from the list and clears the transcript pane if it was showing that file.</summary>
    private void RemoveFromQueue(QueueItem item)
    {
        if (item.Result != null && _shown == item.Result) { _linePlayer.Stop(); ShowResult(null); }
        _queue.Remove(item);
    }

    /// <summary>Right-click on the Files list: enable what makes sense for the clicked item(s).</summary>
    private void OnQueueMenuOpening(object sender, ContextMenuEventArgs e)
    {
        bool idle = _cts == null && !LiveRunning;
        var item = QueueList.SelectedItem as QueueItem;
        bool done = item is { State: ItemState.Done, Result: not null };
        QueueMenuTranscribe.IsEnabled = idle && _queue.Count > 0;
        QueueMenuSave.IsEnabled = idle && done && item!.Result!.Library == null;
        QueueMenuSummary.IsEnabled = done;
        QueueMenuReanalyse.IsEnabled = idle && done;
        QueueMenuAddFiles.IsEnabled = QueueMenuAddFolder.IsEnabled = _cts == null;
        QueueMenuRemove.IsEnabled = _cts == null && QueueList.SelectedItems.Count > 0;
        QueueMenuClear.IsEnabled = _cts == null && _queue.Count > 0;
        QueueMenuReveal.IsEnabled = item != null;
        var selected = QueueList.SelectedItems.Cast<QueueItem>().ToList();
        QueueMenuTranscribeSelected.IsEnabled = idle && selected.Any(i => i.State != ItemState.Done);
        QueueMenuRetranscribe.IsEnabled = idle && selected.Count > 0;
        QueueMenuSchedule.IsEnabled = idle && _queue.Any(i => i.State != ItemState.Done);
        QueueMenuShow.IsEnabled = done;
        QueueMenuClearDone.IsEnabled = _queue.Any(i => i.State == ItemState.Done);
    }

    private void OnRevealSource(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueItem { FilePath: var path } && File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    // ---------- Menu ----------

    private void OnCmdOpen(object sender, ExecutedRoutedEventArgs e) => OnAddFiles(sender, e);
    private void OnCmdSettings(object sender, ExecutedRoutedEventArgs e) => OnSettings(sender, e);

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var library = RecordingLibrary.Root;
        _settings.Hardware = SelectedHardware;   // Settings shows the same choice as the HARDWARE box
        if (new SettingsWindow { Owner = this }.ShowDialog() == true)
        {
            DeviceBox.SelectedIndex = Math.Max(0, Array.IndexOf(HardwareKeys, _settings.EffectiveHardware));
            PrepareNpuInBackground();
            // Output-related settings change how tags render: refresh what is on screen.
            if (_shown != null) foreach (var s in _shown.Speakers) s.Refresh();
            StatusText.Text = "Settings saved.";
        }
        // The library may have been moved or re-pointed (a move applies at once, even on Cancel).
        if (!LibraryStorage.SamePath(library, RecordingLibrary.Root)) _library.Refresh();
        UpdateStorageBanner();
    }

    private void OnMenuTheme(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag)
            ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == tag);
    }

    private void OnOpenGuide(object sender, RoutedEventArgs e) => GuideWindow.Open(this);

    private void OnOpenGuideExternal(object sender, RoutedEventArgs e)
    {
        if (File.Exists(GuideWindow.GuidePath)) Process.Start(new ProcessStartInfo(GuideWindow.GuidePath) { UseShellExecute = true });
        else MessageBox.Show(this, "The user guide wasn't found next to WhisperDesk.exe.", "WhisperDesk");
    }

    private void OnPrivacy(object sender, RoutedEventArgs e) => GuideWindow.Open(this, GuideWindow.PrivacyPage);

    private void OnOpenModels(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ModelManager.ModelsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ModelManager.ModelsDir}\"") { UseShellExecute = true });
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnMenuTab(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = (sender as FrameworkElement)?.Tag switch
        {
            "Live" => LiveTab,
            "Library" => LibraryTab,
            _ => FilesTab,
        };
    }

    /// <summary>File → Export → Library recording: the Share dialog for the recording selected in the Library.</summary>
    private void OnExportLibraryRecording(object sender, RoutedEventArgs e)
    {
        var meta = _library.SelectedRecording ?? _shown?.Library;
        if (meta == null)
        {
            MainTabs.SelectedItem = LibraryTab;
            StatusText.Text = "Select a recording in the Library first, then use File → Export again (or right-click it → Share…).";
            return;
        }
        try { ShareDialog.Show(this, meta, _shown?.Library?.Id == meta.Id ? _shown : RecordingLibrary.Load(meta)); }
        catch (Exception ex) { MessageBox.Show(this, "Could not export: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ---------- Main-window settings ----------

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModelStatus();
        if (IsLoaded) PrepareNpuInBackground();
    }

    private void OnHardwareChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) PrepareNpuInBackground();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse<ThemeMode>(tag, out var mode)) return;
        if (mode == ThemeManager.Mode) return;
        ThemeManager.Apply(mode);
        _settings.Theme = mode.ToString();
        _settings.Save();
    }

    private void UpdateModelStatus()
    {
        if (ModelBox.SelectedItem is not ModelInfo m) return;
        bool npuFiles = Services.Npu.WhisperOnnxModels.IsDownloaded(m);
        ModelStatus.Text = m.IsCustom ? (File.Exists(m.CustomPath) ? "★ Custom model · " + m.CustomPath : "⚠ Custom model file is missing")
                         : ModelManager.IsDownloading(m) ? "Downloading…"
                         : ModelManager.IsDownloaded(m) ? "✓ Downloaded" + (npuFiles ? " · NPU version too" : "")
                         : npuFiles ? "✓ NPU version downloaded" : $"Will download {m.Size} on first use";
        ModelStatus.ToolTip = m.CustomPath;
    }

    /// <summary>Fills the model list (built-in + imported) and selects the model with this key.</summary>
    private void RefreshModels(string? selectKey)
    {
        var all = ModelManager.All();
        ModelBox.ItemsSource = all;
        ModelBox.SelectedItem = all.FirstOrDefault(m => m.Key == selectKey) ?? all.First(m => m.Name == "Large-v3 Turbo");
        UpdateModelStatus();
    }

    private async void OnImportModel(object sender, RoutedEventArgs e)
    {
        if (_cts != null || LiveRunning) { StatusText.Text = "Wait for the current job to finish first."; return; }
        var dlg = new OpenFileDialog
        {
            Title = "Import a Whisper model (whisper.cpp GGML format)",
            Filter = "Whisper models (*.bin;*.gguf)|*.bin;*.gguf|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        var answer = MessageBox.Show(this,
            $"Copy “{Path.GetFileName(dlg.FileName)}” into the WhisperDesk models folder?\n\n" +
            "Yes: copy it (safe if the original is moved or deleted).\nNo: use it where it is (saves disk space).",
            "Import model", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;
        try
        {
            var model = await ModelManager.ImportAsync(dlg.FileName, answer == MessageBoxResult.Yes,
                new Progress<string>(s => StatusText.Text = s), CancellationToken.None);
            RefreshModels(model.Key);
            StatusText.Text = $"Imported model “{model.Name}” ({model.Size}). It's selected in the Model list.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Import model", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRemoveModel(object sender, RoutedEventArgs e)
    {
        if (SelectedModel is not { IsCustom: true } m)
        {
            MessageBox.Show(this, "Select an imported (★) model in the Model list first. Built-in models can be deleted from the models folder.",
                "Remove model", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var copied = m.CustomPath!.StartsWith(ModelManager.CustomDir, StringComparison.OrdinalIgnoreCase);
        if (MessageBox.Show(this, copied ? $"Delete the imported model file?\n\n{m.CustomPath}" : $"Stop using this model? The file itself is kept:\n\n{m.CustomPath}",
                "Remove model", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            ModelManager.RemoveCustom(m);
            RefreshModels(null);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Remove model", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void SetOutputFolder(string? folder)
    {
        _outputFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        OutputText.Text = _outputFolder ?? "Same folder as each source file";
    }

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where transcripts are saved" };
        if (dlg.ShowDialog(this) == true) SetOutputFolder(dlg.FolderName);
    }

    private void OnResetOutput(object sender, RoutedEventArgs e) => SetOutputFolder(null);

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        var selected = QueueList.SelectedItem as QueueItem ?? _queue.LastOrDefault(q => q.LastOutputDir != null);
        var dir = _outputFolder ?? selected?.LastOutputDir ?? (selected != null ? Path.GetDirectoryName(selected.FilePath) : null);
        if (dir != null && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    /// <summary>
    /// Loads the transcription engine for the HARDWARE choice: the NPU engine when Auto/NPU finds a working NPU and the
    /// model has an ONNX version (downloaded here on first use), else whisper.cpp on the GPU or CPU. The whisper.cpp
    /// model is the fallback; when it isn't downloaded yet it is fetched in the background after the NPU files, so the
    /// NPU can start first. Never fails because of the NPU, and never switches away from it without saying so.
    /// </summary>
    private async Task LoadEngineAsync(ModelInfo model, CancellationToken ct)
    {
        var hardware = SelectedHardware;
        string? onnxDir = null, notice = null;
        bool npu = Services.Npu.WhisperOnnxModels.HasOnnx(model) && _engine.NpuFallbackReason == null
                   && await TranscriptionEngine.NpuWantedAsync(hardware);
        if (npu)
        {
            try
            {
                onnxDir = await Services.Npu.WhisperOnnxModels.EnsureAsync(model, new Progress<(long Done, long Total)>(p =>
                    StatusText.Text = $"Downloading {model.Name} for the NPU (checked with SHA-256)… " +
                                      $"{p.Done / 1048576.0:N0} / {p.Total / 1048576.0:N0} MB ({100.0 * p.Done / Math.Max(1, p.Total):0}%)"), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                App.Log("NPU model download (using the GPU/CPU this time)", ex);
                notice = $"Couldn't get the NPU version of {model.Name} ({ex.Message}); using the GPU/CPU this time.";
            }
        }

        string? ggmlPath = ModelManager.IsDownloaded(model) ? ModelManager.PathFor(model) : null;
        // Built-in model files are checked against their official SHA-256 once (older versions didn't check downloads).
        if (ggmlPath != null) await ModelManager.VerifyAsync(model, new Progress<string>(s => StatusText.Text = s), ct);
        Task<string>? fallback = null;
        if (ggmlPath == null && onnxDir != null)
        {
            fallback = ModelManager.EnsureAsync(model, new Progress<long>(b =>
                ModelStatus.Text = $"NPU ready · downloading the GPU/CPU fallback in the background… {b / 1048576.0:N0} MB"), CancellationToken.None);
            _ = fallback.ContinueWith(t =>
            {
                if (t.IsFaulted) App.Log("Fallback model download", t.Exception);
                Dispatcher.InvokeAsync(UpdateModelStatus);
            }, TaskScheduler.Default);
        }
        else if (ggmlPath == null)
        {
            ggmlPath = await ModelManager.EnsureAsync(model,
                new Progress<long>(b => StatusText.Text = $"Downloading {model.Name} model… {b / 1048576.0:0} MB"), ct);
        }
        UpdateModelStatus();

        var engineOptions = new EngineOptions(hardware != "CPU", _settings.GpuDevice, _settings.FlashAttention, hardware, onnxDir);
        await WithStagesAsync(async stage =>
        {
            if (_engine.IsPreparing) ((IProgress<string>)stage).Report("Waiting for the background NPU preparation to finish…");
            else if (onnxDir == null) ((IProgress<string>)stage).Report($"Loading {model.Name} model…" + (hardware == "NPU" && !Services.Npu.WhisperOnnxModels.HasOnnx(model)
                ? " (no NPU version of this model; Base, Small and Large-v3 Turbo have one)" : ""));
            // The UI can be cancelled during a long NPU compile; the compile itself carries on and is kept for next time.
            await Task.Run(() => _engine.Load(ggmlPath, engineOptions, stage, fallback), CancellationToken.None).WaitAsync(ct);
        });
        RuntimeText.Text = _engine.Badge;
        ShowNpuNotice(npu, notice);
    }

    /// <summary>Tells the user when the NPU was wanted but isn't being used (no silent fallback).</summary>
    private void ShowNpuNotice(bool npuWanted, string? notice = null)
    {
        if (notice != null) StatusText.Text = notice;
        else if (npuWanted && _engine.NpuFallbackReason is { } reason)
            StatusText.Text = $"⚠ {reason}. Using the GPU/CPU instead (details in the error log; restart WhisperDesk to try the NPU again).";
    }

    /// <summary>
    /// Runs a preparation step with an indeterminate progress bar and its current stage in the status bar, plus the
    /// elapsed time once it takes more than a few seconds, so a long NPU compile never looks frozen.
    /// </summary>
    private async Task WithStagesAsync(Func<Progress<string>, Task> work)
    {
        string current = StatusText.Text;
        var clock = Stopwatch.StartNew();
        void Show() => StatusText.Text = clock.Elapsed.TotalSeconds >= 3 ? $"{current}   {clock.Elapsed:m\\:ss}" : current;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Show();
        var stage = new Progress<string>(s => { current = s; Show(); });
        var visibility = OverallProgress.Visibility;
        OverallProgress.IsIndeterminate = true;
        OverallProgress.Visibility = Visibility.Visible;
        timer.Start();
        try { await work(stage); }
        finally
        {
            timer.Stop();
            OverallProgress.IsIndeterminate = false;
            OverallProgress.Visibility = visibility;
        }
    }

    /// <summary>
    /// When the selected model will run on the NPU and its files are here, load (and if needed compile) it in the
    /// background while the app is idle, so Start doesn't have to wait. Only one preparation runs at a time; a job that
    /// starts meanwhile simply waits for it and reuses the result.
    /// </summary>
    private async void PrepareNpuInBackground()
    {
        try
        {
            if (IsBusy || _engine.IsPreparing || SelectedModel is not { } model) return;
            var hardware = SelectedHardware;
            if (!Services.Npu.WhisperOnnxModels.IsDownloaded(model) || _engine.NpuFallbackReason != null) return;
            if (!await TranscriptionEngine.NpuWantedAsync(hardware) || IsBusy || model != SelectedModel || hardware != SelectedHardware) return;

            var dir = Services.Npu.WhisperOnnxModels.DirFor(model);
            bool compiled = Services.Npu.OnnxWhisperEngine.IsPrepared(dir, TranscriptionEngine.NpuEncoderDevice);
            var ggml = ModelManager.IsDownloaded(model) ? ModelManager.PathFor(model) : null;
            var options = new EngineOptions(hardware != "CPU", _settings.GpuDevice, _settings.FlashAttention, hardware, dir);
            var stage = new Progress<string>(s => { if (!IsBusy) StatusText.Text = "Getting the NPU ready in the background: " + s; });
            await Task.Run(() => _engine.Load(ggml, options, stage));
            if (IsBusy) return;
            RuntimeText.Text = _engine.Badge;
            StatusText.Text = _engine.UsesNpu
                ? $"{model.Name} is ready on the NPU" + (compiled ? "." : " (prepared once; next time it loads quickly).")
                : $"⚠ {_engine.NpuFallbackReason}. The GPU/CPU will be used (details in the error log).";
        }
        catch (Exception ex)
        {
            App.Log("Background NPU preparation", ex);
        }
    }

    private void SaveSettings()
    {
        _settings.Model = SelectedModel.Key;
        _settings.Hardware = SelectedHardware;
        _settings.UseGpu = SelectedHardware != "CPU";
        _settings.Language = (string)LanguageBox.SelectedValue;
        _settings.Translate = TranslateBox.IsChecked == true;
        _settings.Txt = TxtBox.IsChecked == true;
        _settings.Srt = SrtBox.IsChecked == true;
        _settings.Vtt = VttBox.IsChecked == true;
        _settings.Json = JsonBox.IsChecked == true;
        _settings.BurnSubtitles = BurnBox.IsChecked == true;
        _settings.Docx = DocxBox.IsChecked == true;
        _settings.Pdf = PdfBox.IsChecked == true;
        _settings.Summary = SummaryBox.IsChecked == true;
        _settings.OutputFolder = _outputFolder;
        _settings.Theme = ThemeManager.Mode.ToString();
        _settings.IdentifySpeakers = SpeakersBox.IsChecked == true;
        _settings.SpeakerCount = SelectedSpeakerCount;
        _settings.Save();
    }

    // ---------- Transcription ----------

    private IDisposable? _jobHold;

    private void SetBusy(bool busy)
    {
        // A job that is running when WhisperDesk locks (e.g. auto-lock during a long transcription) keeps the key until it
        // finishes, so it can still save its results; the screen is locked meanwhile.
        if (busy && _jobHold == null && Services.Security.Vault.IsUnlocked) _jobHold = Services.Security.Vault.Hold();
        else if (!busy) { _jobHold?.Dispose(); _jobHold = null; }
        OverallProgress.Visibility = busy && !LiveRunning ? Visibility.Visible : Visibility.Collapsed;
        KeepAwake(busy || LiveRunning);
        ScheduleButton.IsEnabled = !busy;
        StartButton.IsEnabled = !busy && !LiveRunning;
        LiveStartButton.IsEnabled = !busy && !LiveRunning;
        CancelButton.IsEnabled = busy;
        foreach (var c in new Control[] { ModelBox, DeviceBox, LanguageBox, TranslateBox, FormatsButton, TxtBox, SrtBox, VttBox, JsonBox, DocxBox, PdfBox, SummaryBox, BurnBox,
                                          SpeakersBox, AddFilesButton, AddFolderButton, RemoveButton, ClearButton,
                                          MenuAddFiles, MenuAddFolder })
            c.IsEnabled = !busy;
        UpdateSpeakerButtons();
    }

    private void UpdateSpeakerButtons()
    {
        bool idle = _cts == null;
        SaveSpeakersButton.IsEnabled = idle && _shown != null && _shown.Lines.Count > 0;
        AddSpeakerButton.IsEnabled = idle && _shown != null && _shown.Lines.Count > 0;
        ReanalyseButton.IsEnabled = idle && _shown != null && _shown.Lines.Count > 0 && _shown != _liveResult;
        var selected = SpeakersList.SelectedItems.Count;
        NameSelectedButton.IsEnabled = idle && selected >= 1;
        MergeButton.IsEnabled = idle && selected >= 2;
    }

    private void OnSpeakerSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSpeakerButtons();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "Cancelling…";
    }

    private async void OnStart(object sender, RoutedEventArgs e) => await StartJobsAsync(null);

    /// <summary>Right-click → Transcribe selected: only the selected files that haven't been done yet.</summary>
    private async void OnTranscribeSelected(object sender, RoutedEventArgs e) =>
        await StartJobsAsync(QueueList.SelectedItems.Cast<QueueItem>().Where(i => i.State != ItemState.Done).ToList());

    /// <summary>Right-click → Re-transcribe: runs the selected files again (e.g. with another model or settings).</summary>
    private async void OnRetranscribeSelected(object sender, RoutedEventArgs e)
    {
        var items = QueueList.SelectedItems.Cast<QueueItem>().Where(i => i.State != ItemState.Working).ToList();
        if (items.Count == 0) return;
        foreach (var i in items) { i.State = ItemState.Pending; i.Status = "Waiting"; i.Progress = 0; }
        await StartJobsAsync(items);
    }

    private void OnClearFinished(object sender, RoutedEventArgs e)
    {
        foreach (var item in _queue.Where(i => i.State == ItemState.Done).ToList()) RemoveFromQueue(item);
    }

    private void OnShowSelectedTranscript(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueItem { Result: { } r }) ShowResult(r);
    }

    /// <summary>Library → Re-transcribe: queues each recording's audio; the new transcript replaces the old one in place.</summary>
    private async void OnLibraryRetranscribe(IReadOnlyList<RecordingMeta> recordings)
    {
        if (_cts != null || LiveRunning) { StatusText.Text = "Wait for the current job to finish first."; return; }
        var usable = recordings.Where(r => r.HasMedia).ToList();
        var skipped = recordings.Count - usable.Count;
        if (usable.Count == 0)
        {
            MessageBox.Show(this, "These recordings have no audio kept with them, so they can't be transcribed again.", "Re-transcribe",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var edited = usable.Count(r => { try { return RecordingLibrary.ReadTranscriptJson(r).Contains("\"Edits\": ["); } catch { return false; } });
        if (MessageBox.Show(this,
                $"Transcribe {usable.Count} recording(s) again with the current model ({SelectedModel.Name}) and settings?\n\n" +
                "The new transcript replaces the current one. The old transcript is kept in each recording's “previous” folder." +
                (edited > 0 ? $"\n\n⚠ {edited} of them have manual text corrections; those are in the previous version only." : "") +
                (skipped > 0 ? $"\n\n{skipped} recording(s) without audio will be skipped." : ""),
                "Re-transcribe", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var items = new List<QueueItem>();
        foreach (var r in usable)
        {
            var item = _queue.FirstOrDefault(q => q.TargetLibrary?.Id == r.Id && q.State != ItemState.Working)
                       ?? new QueueItem(r.MediaPath) { TargetLibrary = r };
            item.State = ItemState.Pending; item.Status = "Waiting (re-transcribe library recording)"; item.Progress = 0;
            if (!_queue.Contains(item)) _queue.Add(item);
            items.Add(item);
        }
        MainTabs.SelectedItem = FilesTab;
        await StartJobsAsync(items);
    }

    private async Task StartJobsAsync(IReadOnlyList<QueueItem>? only)
    {
        if (_cts != null || LiveRunning) return;
        bool txt = TxtBox.IsChecked == true, srt = SrtBox.IsChecked == true,
             vtt = VttBox.IsChecked == true, json = JsonBox.IsChecked == true, burn = BurnBox.IsChecked == true;
        bool libraryOnly = only != null && only.All(i => i.TargetLibrary != null);
        if (!libraryOnly && !(txt || srt || vtt || json || burn || DocxBox.IsChecked == true || PdfBox.IsChecked == true || SummaryBox.IsChecked == true))
        {
            MessageBox.Show(this, "Pick at least one output (TXT, SRT, VTT, JSON, Word, PDF or Burn into video).", "WhisperDesk");
            return;
        }

        foreach (var item in (only ?? _queue).Where(i => i.State is ItemState.Failed or ItemState.Cancelled))
        {
            item.State = ItemState.Pending;
            item.Status = "Waiting";
            item.Progress = 0;
        }
        var work = (only ?? _queue).Where(i => i.State == ItemState.Pending && (i.NotBefore == null || i.NotBefore <= DateTime.Now)).ToList();
        if (work.Count == 0)
        {
            StatusText.Text = "Nothing to do. Add some files first.";
            return;
        }

        var ffmpeg = AudioDecoder.FindFfmpeg();
        if (ffmpeg == null)
        {
            MessageBox.Show(this, "FFmpeg was not found.\n\nInstall it with:  winget install Gyan.FFmpeg\nor set its location in File → Settings.",
                "WhisperDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveSettings();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        var ct = _cts.Token;
        var model = SelectedModel;
        var options = new TranscribeOptions((string)LanguageBox.SelectedValue, TranslateBox.IsChecked == true,
            _settings.WhisperThreads,
            _settings.InitialPrompt, _settings.NoContext);
        OverallProgress.Value = 0;
        bool npuBefore = false;

        try
        {
            // 1. Whisper model (downloads what is missing, then loads it on the chosen hardware)
            await LoadEngineAsync(model, ct);
            npuBefore = _engine.UsesNpu;

            // 2. Speaker models (small, one-time download)
            var identify = SpeakersBox.IsChecked == true && OnnxRuntimeGuard.Available;   // without ONNX Runtime: transcript without speakers
            if (SpeakersBox.IsChecked == true && !OnnxRuntimeGuard.Available)
                StatusText.Text = "Speakers can't be identified: " + OnnxRuntimeGuard.Problem;   // said only when the feature is used, no pop-up
            var speakerCount = SelectedSpeakerCount;
            if (identify)
                await SpeakerModels.EnsureAsync(new Progress<string>(s => StatusText.Text = s), ct);

            // 3. Files
            for (int i = 0; i < work.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = work[i];
                int index = i;
                await ProcessItemAsync(item, ffmpeg, options, identify, speakerCount, txt, srt, vtt, json, burn,
                    p => OverallProgress.Value = (index + p / 100.0) / work.Count * 100, ct);
            }

            var failed = work.Count(w => w.State == ItemState.Failed);
            StatusText.Text = failed == 0 ? $"Finished {work.Count} file(s)." : $"Finished with {failed} error(s).";
            OverallProgress.Value = 100;
            if (npuBefore && !_engine.UsesNpu)
            {
                // The NPU failed mid-job and whisper.cpp finished it: say so rather than switching silently.
                StatusText.Text += $"  ⚠ The NPU stopped working ({_engine.NpuFallbackReason}); the rest ran on the GPU/CPU.";
                RuntimeText.Text = _engine.Badge;
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var item in work.Where(w => w.State is ItemState.Pending or ItemState.Working))
            {
                item.State = ItemState.Cancelled;
                item.Status = "Cancelled";
            }
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
            MessageBox.Show(this, ex.Message, "WhisperDesk", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private async Task ProcessItemAsync(QueueItem item, string ffmpeg, TranscribeOptions options,
        bool identify, int speakerCount, bool txt, bool srt, bool vtt, bool json, bool burn, Action<double> overall, CancellationToken ct)
    {
        item.State = ItemState.Working;
        item.Progress = 0;
        _processing = item;
        QueueList.ScrollIntoView(item);
        var result = new TranscriptResult { SourceFile = item.FilePath, MediaPath = item.FilePath };
        item.Result = result;
        ShowResult(result, live: true);

        try
        {
            item.Status = "Extracting audio…";
            StatusText.Text = $"Extracting audio from {item.FileName}…";
            var samples = await Task.Run(() => AudioDecoder.DecodeAsync(ffmpeg, item.FilePath, ct), ct);
            var duration = TimeSpan.FromSeconds(samples.Length / (double)AudioDecoder.SampleRate);

            var sw = Stopwatch.StartNew();
            var settings = AppSettings.Current;

            // Only transcribe where someone is talking: silence, hold music and noise are what make Whisper
            // invent "Thank you." and get stuck repeating a line.
            List<SpeechRegion>? regions = null;
            if (settings.SkipSilence && OnnxRuntimeGuard.Available)
            {
                item.Status = "Finding speech…";
                await SpeakerModels.EnsureAsync(new Progress<string>(s => StatusText.Text = s), ct);
                regions = await Task.Run(() => SpeechDetector.Detect(samples, ct), ct);
            }

            item.Status = identify ? $"Transcribing + finding speakers ({duration:hh\\:mm\\:ss})…" : $"Transcribing ({duration:hh\\:mm\\:ss})…";
            StatusText.Text = $"Transcribing {item.FileName}…";

            // The preview is fed in batches at background priority: a burst of segments must never block clicks,
            // scrolling or repainting (the cause of the "Not responding" white window on long files).
            var pending = new System.Collections.Concurrent.ConcurrentQueue<SegmentData>();
            int lastProgress = 0;
            var pump = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
            pump.Tick += (_, _) =>
            {
                item.Progress = lastProgress * 0.9;
                overall(lastProgress * 0.9);
                if (pending.IsEmpty) return;
                TranscriptLine? newest = null;
                for (int n = 0; n < 300 && pending.TryDequeue(out var s); n++)
                    if (!settings.FilterHallucinations || !HallucinationFilter.IsSuspect(s.Text))
                        result.Lines.Add(newest = TranscriptResult.FromSegment(s));
                if (newest != null && _shown == result) TranscriptList.ScrollIntoView(newest);
            };
            if (_shown == result) result.Lines.CollectionChanged -= OnLinesChanged;   // the pump scrolls once per batch
            pump.Start();

            // Whisper runs on the GPU while speaker analysis runs on the CPU at the same time (except in Light mode).
            Task<DiarizationResult?> diarize = Task.FromResult<DiarizationResult?>(null);
            List<SegmentData> segments;
            try
            {
                var transcribe = Task.Run(() => _engine.TranscribeAsync(samples, options,
                    p => lastProgress = p, pending.Enqueue, ct, regions), ct);
                if (identify && settings.ParallelSpeakerAnalysis)
                    diarize = Task.Run<DiarizationResult?>(() => _speakerAnalyzer.Analyze(samples, speakerCount, ct), ct);
                segments = await transcribe;
            }
            finally { pump.Stop(); }
            if (identify && !settings.ParallelSpeakerAnalysis)
            {
                item.Status = "Finding speakers…";
                diarize = Task.Run<DiarizationResult?>(() => _speakerAnalyzer.Analyze(samples, speakerCount, ct), ct);
            }
            if (settings.FilterHallucinations)
                segments = await Task.Run(() => HallucinationFilter.Clean(segments, samples), ct);
            item.Progress = 90;
            if (identify) item.Status = "Matching speakers…";
            var diarization = await diarize;
            sw.Stop();

            // Rebuild from the final segment list (the preview is only a preview). Detach the list from the
            // screen while thousands of lines are swapped in, so it lays out once instead of per line.
            bool onScreen = _shown == result;
            if (onScreen) TranscriptList.ItemsSource = null;
            result.Lines.Clear();
            foreach (var s in segments) result.Lines.Add(TranscriptResult.FromSegment(s));
            if (diarization != null && diarization.Speakers.Count > 0)
            {
                foreach (var d in diarization.Speakers) result.Speakers.Add(new SpeakerInfo(d));
                var spans = result.Lines.Select(l => (l.Start.TotalSeconds, l.End.TotalSeconds)).ToList();
                var ids = await Task.Run(() => SpeakerAnalyzer.AssignLines(spans, diarization.Turns), ct);
                result.ApplySpeakerIds(ids);
                if (onScreen && _shown == result) TranscriptList.ItemsSource = result.Lines;

                item.Status = "Checking each line's speaker…";
                var lineInfo = result.Lines.Select((l, i) => (l.Start.TotalSeconds, l.End.TotalSeconds, ids[i])).ToList();
                var refined = await Task.Run(() => _speakerAnalyzer.Refine(samples, lineInfo, diarization.Speakers, ct), ct);
                result.ApplySpeakerIds(refined);

                ApplyVoiceLibrary(result, diarization);
            }
            if (onScreen && _shown == result && TranscriptList.ItemsSource == null) TranscriptList.ItemsSource = result.Lines;

            string outDir;
            if (item.TargetLibrary is { } target)
            {
                // Re-transcribing a library recording: keep the old version, then replace it in place.
                outDir = target.Dir;
                var previous = Path.Combine(target.Dir, "previous", DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss"));
                Directory.CreateDirectory(previous);
                foreach (var f in new[] { "transcript.json", "transcript.txt", "transcript.srt", "metadata.json" })
                    if (Services.Security.SecureFile.Exists(Path.Combine(target.Dir, f))) Services.Security.SecureFile.Copy(Path.Combine(target.Dir, f), Path.Combine(previous, f));
                result.Library = target;
                result.OutputDir = target.Dir;
                target.Model = SelectedModel.Name;
                target.DurationSeconds = duration.TotalSeconds;
                RecordingLibrary.WriteTranscript(target, result);
                if (MainTabs.SelectedItem == LibraryTab) _library.Refresh(selectId: target.Id);
            }
            else
            {
                outDir = _outputFolder ?? Path.GetDirectoryName(item.FilePath)!;
                await TranscriptWriter.WriteAsync(result, outDir, txt, srt, vtt, json);
            }
            item.LastOutputDir = outDir;
            PublishFileTranscript(result, item.FilePath);

            var burned = "";
            if (burn && item.TargetLibrary == null && SubtitleBurner.IsVideo(item.FilePath))
            {
                item.Status = "Burning subtitles into video…";
                var encoder = await SubtitleBurner.BurnAsync(ffmpeg, item.FilePath, TranscriptWriter.ToSrt(result),
                    SubtitleBurner.OutputPathFor(item.FilePath, outDir), duration,
                    new Progress<double>(p => { item.Progress = p; item.Status = $"Burning subtitles into video… {p:0}%"; }), ct);
                burned = $" · video ✓ ({encoder.Replace("h264_", "").ToUpperInvariant()})";
            }

            var speed = sw.Elapsed.TotalSeconds > 0 ? duration.TotalSeconds / sw.Elapsed.TotalSeconds : 0;
            var lang = segments.FirstOrDefault()?.Language;
            var who = result.HasSpeakers ? $" · {result.Speakers.Count} speaker{(result.Speakers.Count == 1 ? "" : "s")}" : "";
            item.Progress = 100;
            overall(100);
            item.State = ItemState.Done;
            item.Status = $"Done in {sw.Elapsed:mm\\:ss} · {speed:0.#}× realtime{(lang != null ? " · " + lang : "")}{who}{burned}";

            _processing = null;
            QueueList.SelectedItem = item;
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            item.State = ItemState.Failed;
            item.Status = "Error: " + ex.Message;
            item.Result = null;
        }
        finally
        {
            _processing = null;
        }
    }

    /// <summary>Fills in names and sexes for voices already in the voice library.</summary>
    private void ApplyVoiceLibrary(TranscriptResult result, DiarizationResult diarization) =>
        SpeakerReanalysis.ApplyVoiceLibrary(result, diarization, _voices);

    /// <summary>
    /// Adds the voiceprints of named speakers (with "Remember this voice" ticked) to the voice library.
    /// Used by every save path: Save changes, Save to library (review dialog) and live sessions.
    /// </summary>
    private int EnrollVoices(TranscriptResult result)
    {
        var count = 0;
        foreach (var s in result.Speakers.Where(s => s.Remember && !string.IsNullOrWhiteSpace(s.Name) && s.Embedding.Length > 0))
        {
            _voices.Remember(s.Name, s.Sex, s.Embedding);
            count++;
        }
        if (count > 0) _voices.Save();
        return count;
    }

    // ---------- Transcript view ----------

    private void OnQueueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueItem { Result: { } r } item && item != _processing) ShowResult(r);
    }

    // Speakers can be added from a line's dropdown (picking someone from the voice library).
    private void OnShownSpeakersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        SpeakersHint.Visibility = _shown?.HasSpeakers == true ? Visibility.Collapsed : Visibility.Visible;
        UpdateSpeakerButtons();
    }

    private void ShowResult(TranscriptResult? result, bool live = false)
    {
        if (_shown != null)
        {
            _shown.Lines.CollectionChanged -= OnLinesChanged;
            _shown.Speakers.CollectionChanged -= OnShownSpeakersChanged;
        }
        _shown = result;
        if (result != null) result.Speakers.CollectionChanged += OnShownSpeakersChanged;
        UpdateFeed();

        TranscriptList.DataContext = result;
        TranscriptList.ItemsSource = result?.Lines;
        SpeakersList.ItemsSource = result?.Speakers;
        SpeakersHint.Visibility = result?.HasSpeakers == true ? Visibility.Collapsed : Visibility.Visible;
        UpdateSpeakerButtons();
        if (result == null) { TranscriptTitle.Text = "TRANSCRIPT"; return; }

        var name = Path.GetFileName(result.SourceFile).ToUpperInvariant();
        TranscriptTitle.Text = live ? $"LIVE · {name}" : $"TRANSCRIPT · {name}";
        if (live) result.Lines.CollectionChanged += OnLinesChanged;
    }

    /// <summary>Keeps the newest line in view while a file is being transcribed.</summary>
    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems?[0] is { } line) TranscriptList.ScrollIntoView(line);
    }

    private void OnCopyTranscript(object sender, RoutedEventArgs e)
    {
        if (_shown != null && _shown.Lines.Count > 0) SafeClipboard.SetText(_shown.Render(), this);
    }

    private void OnExportTranscript(object sender, RoutedEventArgs e)
    {
        if (_shown == null || _shown.Lines.Count == 0)
        {
            StatusText.Text = "Nothing to export yet. Transcribe a file, run a live session, or open a recording from the Library.";
            return;
        }
        var title = _shown.Library?.Title ?? Path.GetFileNameWithoutExtension(_shown.SourceFile);
        var dlg = new SaveFileDialog
        {
            Title = "Export transcript",
            FileName = RecordingLibrary.Sanitize(title),
            Filter = "Word document (*.docx)|*.docx|PDF document (*.pdf)|*.pdf|Text log (*.txt)|*.txt|" +
                     "Subtitles (*.srt)|*.srt|WebVTT subtitles (*.vtt)|*.vtt|JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            TranscriptWriter.ExportTo(_shown, dlg.FileName, title);
            StatusText.Text = $"Exported {Path.GetFileName(dlg.FileName)}.";
            if (MessageBox.Show(this, $"Exported {Path.GetFileName(dlg.FileName)}.\n\nOpen it now?", "Export", MessageBoxButton.YesNo,
                    MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Export failed: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSummary(object sender, RoutedEventArgs e)
    {
        if (_shown == null || _shown.Lines.Count == 0)
        {
            StatusText.Text = "Nothing to summarise yet. Transcribe a file, run a live session, or open a recording from the Library.";
            return;
        }
        var title = _shown.Library?.Title ?? Path.GetFileNameWithoutExtension(_shown.SourceFile);
        new SummaryWindow(_shown, title) { Owner = this }.ShowDialog();
    }

    private void OnSelectAllLines(object sender, RoutedEventArgs e) => TranscriptList.SelectAll();

    /// <summary>Empties the transcript pane. Nothing is deleted: a running job keeps writing to its own result.</summary>
    private void OnClearTranscriptView(object sender, RoutedEventArgs e)
    {
        if (_shown == null) return;
        if (_shown == _liveResult && _live != null)
        {
            StatusText.Text = "The live session is still running. Stop it first, then clear the pane.";
            return;
        }
        _linePlayer.Stop();
        QueueList.SelectedItem = null;
        ShowResult(null);
        StatusText.Text = "Pane cleared. Pick a file above or open a recording from the Library to show it again.";
    }

    private void OnCopySelectedLines(object sender, RoutedEventArgs e)
    {
        if (_shown == null) return;
        var selected = TranscriptList.SelectedItems.Cast<TranscriptLine>().OrderBy(l => l.Start).ToList();
        if (selected.Count == 0) return;
        var copy = new TranscriptResult { SourceFile = _shown.SourceFile };
        foreach (var l in selected) copy.Lines.Add(l);
        SafeClipboard.SetText(copy.Render(), this);
    }

    /// <summary>Builds "Assign selected lines to → (speakers…) / New speaker / Nobody" each time the menu opens.</summary>
    private void OnTranscriptMenuOpening(object sender, ContextMenuEventArgs e)
    {
        AssignMenu.Items.Clear();
        var hasSelection = _shown != null && TranscriptList.SelectedItems.Count > 0;
        AssignMenu.IsEnabled = hasSelection && _cts == null;
        if (_shown == null) return;

        // Same grouping as the line dropdowns: voice library · named here · unidentified · other recordings.
        foreach (var (header, items) in SpeakerPicker.Groups(_shown))
        {
            AssignMenu.Items.Add(new MenuItem { Header = header, IsEnabled = false, FontSize = 11, FontWeight = FontWeights.SemiBold });
            foreach (var item in items)
            {
                var mi = new MenuItem { Header = item is SpeakerInfo s ? s.Tag : ((VoicePick)item).Tag };
                mi.Click += (_, _) =>
                {
                    var speaker = item as SpeakerInfo;
                    if (item is VoicePick pick)
                    {
                        speaker = _shown.SpeakerFor(pick.Name);
                        if (speaker.Sex == Sexes.Unspecified && pick.Sex != Sexes.Unspecified) speaker.Sex = pick.Sex;
                    }
                    AssignSelected(speaker);
                };
                AssignMenu.Items.Add(mi);
            }
        }
        if (AssignMenu.Items.Count > 0) AssignMenu.Items.Add(new Separator());
        var add = new MenuItem { Header = "+ New speaker" };
        add.Click += (_, _) => AssignSelected(AddSpeaker());
        AssignMenu.Items.Add(add);
        var none = new MenuItem { Header = "Nobody (clear)" };
        none.Click += (_, _) => AssignSelected(null);
        AssignMenu.Items.Add(none);
    }

    private void AssignSelected(SpeakerInfo? speaker)
    {
        foreach (var line in TranscriptList.SelectedItems.Cast<TranscriptLine>()) line.Speaker = speaker;
    }

    // ---------- Speakers panel ----------

    private SpeakerInfo AddSpeaker()
    {
        var s = _shown!.AddSpeaker();
        SpeakersHint.Visibility = Visibility.Collapsed;
        SpeakersList.UpdateLayout();
        return s;
    }

    private void OnAddSpeaker(object sender, RoutedEventArgs e)
    {
        if (_shown == null) return;
        var s = AddSpeaker();
        StatusText.Text = $"Added {s.DefaultLabel}. Pick them on their lines (or select lines, right-click → Assign).";
    }

    private void OnRemoveSpeaker(object sender, RoutedEventArgs e)
    {
        if (_shown == null || (sender as FrameworkElement)?.Tag is not SpeakerInfo s) return;
        var count = _shown.Lines.Count(l => l.Speaker == s);
        if (count > 0 && MessageBox.Show(this, $"Remove {s.Label}? Their {count} line(s) will have no speaker.", "WhisperDesk",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _shown.RemoveSpeaker(s);
        SpeakersHint.Visibility = _shown.HasSpeakers ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnSaveSpeakers(object sender, RoutedEventArgs e)
    {
        if (_shown == null) return;
        var result = _shown;
        SaveSpeakersButton.IsEnabled = false;
        try
        {
            // Speakers added by hand (or reassigned) get a voiceprint and preview clip from the lines they now own.
            var needVoice = result.Speakers.Where(s => s.Embedding.Length == 0 && result.Lines.Any(l => l.Speaker == s)).ToList();
            var skippedVoices = 0;
            if (needVoice.Count > 0)
            {
                if (result.MediaPath != null && File.Exists(result.MediaPath))
                {
                    StatusText.Text = "Learning voices of added speakers…";
                    await LearnVoicesAsync(result, needVoice);
                }
                else skippedVoices = needVoice.Count; // transcript-only recording: no audio to learn from
            }

            string savedTo;
            if (result.Library is { } meta)
            {
                RecordingLibrary.WriteTranscript(meta, result);
                savedTo = $"library recording “{meta.Title}”";
            }
            else if (result == _liveResult)
            {
                savedTo = "this live session (it's saved to the library when you stop)";
            }
            else
            {
                var outDir = result.OutputDir ?? _outputFolder ?? Path.GetDirectoryName(result.SourceFile)!;
                await TranscriptWriter.WriteAsync(result, outDir,
                    TxtBox.IsChecked == true, SrtBox.IsChecked == true, VttBox.IsChecked == true, JsonBox.IsChecked == true);
                savedTo = Path.GetFileName(result.SourceFile);

                if (BurnBox.IsChecked == true && SubtitleBurner.IsVideo(result.SourceFile) && AudioDecoder.FindFfmpeg() is { } ffmpeg)
                {
                    var duration = result.Lines.Count > 0 ? result.Lines[^1].End : TimeSpan.Zero;
                    await SubtitleBurner.BurnAsync(ffmpeg, result.SourceFile, TranscriptWriter.ToSrt(result),
                        SubtitleBurner.OutputPathFor(result.SourceFile, outDir), duration,
                        new Progress<double>(p => StatusText.Text = $"Re-burning subtitles with the new names… {p:0}%"), CancellationToken.None);
                }
            }

            var remembered = EnrollVoices(result);
            VoiceLibrary.Shared.RefreshRecordingNames();

            _addons.PublishSaved(AddonManager.ToSavedEvent(result.Library?.Id ?? result.SourceFile,
                result.Library?.Title ?? Path.GetFileNameWithoutExtension(result.SourceFile),
                result.Library?.Source ?? result.SourceFile, result.Library?.Folder, result));

            StatusText.Text = $"Updated {savedTo}" +
                              (remembered > 0 ? $" · {remembered} voice(s) saved to your voice library" : "") +
                              (skippedVoices > 0 ? $" · {skippedVoices} added speaker(s) not learned (no audio kept)" : "") + ".";
            if (MainTabs.SelectedItem == LibraryTab) _library.Refresh(selectId: result.Library?.Id);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save: " + ex.Message;
        }
        finally
        {
            UpdateSpeakerButtons();
        }
    }

    private async Task LearnVoicesAsync(TranscriptResult result, List<SpeakerInfo> speakers)
    {
        var ffmpeg = AudioDecoder.FindFfmpeg() ?? throw new InvalidOperationException("FFmpeg not found.");
        await SpeakerModels.EnsureAsync(new Progress<string>(s => StatusText.Text = s), CancellationToken.None);
        var samples = await Task.Run(() => AudioDecoder.DecodeAsync(ffmpeg, result.MediaPath!, CancellationToken.None));

        foreach (var s in speakers)
        {
            var turns = result.Lines.Where(l => l.Speaker == s)
                .Select(l => new SpeakerTurn(l.Start.TotalSeconds, l.End.TotalSeconds, s.Id))
                .OrderByDescending(t => t.End - t.Start).ToList();
            var audio = SpeakerAnalyzer.Gather(samples, turns, maxSeconds: 30);
            s.Embedding = await Task.Run(() => _speakerAnalyzer.Embed(audio));
            s.Clip = SpeakerAnalyzer.MakeClip(samples, turns[0], maxSeconds: 6);
            s.TalkTime = TimeSpan.FromSeconds(turns.Sum(t => t.End - t.Start));
            s.Refresh();
        }
    }

    private void OnPlayClip(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SpeakerInfo { Clip.Length: > 0 } s) return;
        try { _linePlayer.ToggleClip(s.Clip, s); }
        catch (Exception ex) { StatusText.Text = "Could not play: " + ex.Message; }
    }

    private List<SpeakerInfo> SelectedSpeakers() => SpeakersList.SelectedItems.Cast<SpeakerInfo>().ToList();

    /// <summary>Gives several selected speaker cards the same name and sex.</summary>
    private void OnNameSelectedSpeakers(object sender, RoutedEventArgs e)
    {
        var selected = SelectedSpeakers();
        if (_shown == null || selected.Count == 0)
        {
            StatusText.Text = "Select one or more speaker cards first (Ctrl/Shift+click).";
            return;
        }
        if (SpeakerDialogs.AskName(this, selected) is not { } choice) return;
        foreach (var s in selected)
        {
            s.Name = choice.Name;
            s.Sex = choice.Sex;
        }
        StatusText.Text = $"Named {selected.Count} speaker(s) “{(choice.Name.Length > 0 ? choice.Name : "(cleared)")}”. " +
                          "Click “Save changes & update files” to write it to disk.";
    }

    /// <summary>Combines the selected speakers into the first one: lines are reassigned and voiceprints averaged.</summary>
    private void OnMergeSpeakers(object sender, RoutedEventArgs e)
    {
        var selected = SelectedSpeakers();
        if (_shown == null || selected.Count < 2)
        {
            StatusText.Text = "Select two or more speaker cards to merge (Ctrl/Shift+click).";
            return;
        }
        // Keep the named one (or the one who talked most) as the survivor.
        var keep = selected.OrderByDescending(s => !string.IsNullOrWhiteSpace(s.Name)).ThenByDescending(s => s.TalkTime).First();
        var others = selected.Where(s => s != keep).ToList();
        if (MessageBox.Show(this, $"Merge {string.Join(", ", others.Select(s => s.Label))} into {keep.Label}?\n\nTheir lines will all be tagged {keep.Label}.",
                "Merge speakers", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var prints = selected.Where(s => s.Embedding.Length > 0).Select(s => s.Embedding).ToList();
        if (prints.Count > 1) keep.Embedding = VoiceLibrary.Normalize(VoiceLibrary.Average(prints));
        if (!keep.HasClip) keep.Clip = others.FirstOrDefault(s => s.HasClip)?.Clip ?? keep.Clip;
        keep.TalkTime = selected.Aggregate(TimeSpan.Zero, (t, s) => t + s.TalkTime);
        foreach (var line in _shown.Lines.Where(l => l.Speaker != null && others.Contains(l.Speaker))) line.Speaker = keep;
        foreach (var s in others) _shown.Speakers.Remove(s);
        keep.Refresh();
        SpeakersList.SelectedItem = keep;
        StatusText.Text = $"Merged {others.Count + 1} speakers into {keep.Label}. Click “Save changes & update files” to write it to disk.";
    }

    /// <summary>Re-detects speakers from the audio (warns before overwriting labels), optionally matching the voice library.</summary>
    private async void OnReanalyseSpeakers(object sender, RoutedEventArgs e)
    {
        if (_shown == null || _shown.Lines.Count == 0 || _cts != null) return;
        if (_shown == _liveResult)
        {
            StatusText.Text = "Stop the live session first, then re-analyse it.";
            return;
        }
        await ReanalyseAsync(_shown);
    }

    /// <summary>Shared by the Speakers panel and the Library's right-click menu.</summary>
    internal async Task<bool> ReanalyseAsync(TranscriptResult result)
    {
        if (result.MediaLogical == null || !Services.Security.SecureFile.Exists(result.MediaLogical))
        {
            MessageBox.Show(this, "This transcript has no audio (it was saved as transcript only, or the source file was moved), " +
                                  "so its speakers can't be re-analysed.", "Re-analyse speakers", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (SpeakerDialogs.AskReanalyse(this, result, SelectedSpeakerCount) is not { } choice) return false;

        _linePlayer.Stop();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var n = await SpeakerReanalysis.RunAsync(result, _speakerAnalyzer, _voices, choice.SpeakerCount, choice.CompareWithLibrary,
                new Progress<string>(s => StatusText.Text = s), _cts.Token);
            if (_shown == result)
            {
                ShowResult(result);
                if (result.Library is { } lib) TranscriptTitle.Text = $"LIBRARY · {lib.Title.ToUpperInvariant()}";
            }
            var known = result.Speakers.Count(s => s.Recognized != null);
            var summary = $"Re-analysed: {n} speaker(s) found" + (known > 0 ? $", {known} recognised from your voice library" : "");
            if (result.Library is { } meta)
            {
                // The user already confirmed the overwrite, so a library recording is updated straight away.
                RecordingLibrary.WriteTranscript(meta, result);
                if (MainTabs.SelectedItem == LibraryTab) _library.Refresh(selectId: meta.Id);
                StatusText.Text = summary + $" and saved to “{meta.Title}”. Name anyone new, then click “Save changes & update files”.";
            }
            else StatusText.Text = summary + ". Check the names, then click “Save changes & update files”.";
            return true;
        }
        catch (OperationCanceledException) { StatusText.Text = "Re-analysis cancelled."; return false; }
        catch (Exception ex) { StatusText.Text = "Re-analysis failed: " + ex.Message; return false; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void OnVoiceLibrary(object sender, RoutedEventArgs e)
    {
        // Opening it from a selected speaker card that has a known voice jumps straight to that voice.
        var select = SpeakersList.SelectedItems.Cast<SpeakerInfo>().FirstOrDefault(s => s.IsKnownVoice)?.Name;
        var win = new VoiceLibraryWindow(_voices, _speakerAnalyzer, select) { Owner = this };
        win.VoiceRenamed += (oldName, newName, sex) =>
        {
            // Keep the open transcript in step (library recordings were already rewritten by the window).
            foreach (var s in _shown?.Speakers.Where(s => s.Name.Trim().Equals(oldName, StringComparison.OrdinalIgnoreCase)).ToList() ?? new())
            {
                s.Name = newName;
                s.Sex = sex;
            }
        };
        win.ShowDialog();
        if (MainTabs.SelectedItem == LibraryTab) _library.Refresh();
    }

    // ---------- Tabs ----------

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs) return; // ignore SelectionChanged bubbling up from lists inside the tabs

        if (MainTabs.SelectedItem == LibraryTab)
            _library.Refresh();
        else if (MainTabs.SelectedItem == LiveTab && _liveResult != null && _shown != _liveResult)
            ShowResult(_liveResult, live: LiveRunning);
        else if (MainTabs.SelectedItem == FilesTab && QueueList.SelectedItem is QueueItem { Result: { } r } && _shown != r)
            ShowResult(r);
    }

    // ---------- Live ----------

    private void LoadDevices()
    {
        try
        {
            var devices = LiveAudioSource.ListDevices();
            LiveDeviceBox.ItemsSource = devices;
            LiveDeviceBox.SelectedItem = devices.FirstOrDefault(d => d.Id == _settings.LiveDeviceId)
                                         ?? devices.FirstOrDefault(d => !d.IsLoopback) ?? devices.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LiveInfo.Text = "Could not list audio devices: " + ex.Message;
        }
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e) => LoadDevices();

    private async void OnLiveStart(object sender, RoutedEventArgs e)
    {
        if (_cts != null || LiveRunning) return;
        if (LiveDeviceBox.SelectedItem is not AudioDeviceInfo device)
        {
            MessageBox.Show(this, "Pick an audio input first.", "WhisperDesk");
            return;
        }

        SaveSettings();
        _settings.LiveDeviceId = device.Id;
        _cts = new CancellationTokenSource(); // blocks file transcription while we prepare
        SetBusy(true);
        LiveStartButton.IsEnabled = false;
        try
        {
            var model = SelectedModel;
            await SpeakerModels.EnsureAsync(new Progress<string>(s => StatusText.Text = s), _cts.Token);
            await LoadEngineAsync(model, _cts.Token);

            var options = new TranscribeOptions((string)LanguageBox.SelectedValue, TranslateBox.IsChecked == true,
                _settings.WhisperThreads,
                _settings.InitialPrompt, NoContext: true);

            var started = DateTime.Now;
            string? recording = null;
            if (LiveKeepAudioBox.IsChecked == true)
            {
                recording = Path.Combine(LiveDir, $"live-{started:yyyyMMdd-HHmmss}.wav");
            }

            _liveResult = new TranscriptResult { SourceFile = $"Live session {started:yyyy-MM-dd HH.mm}.wav", MediaPath = recording };
            _liveSessionId = Guid.NewGuid().ToString("N");
            var sessionId = _liveSessionId;
            _liveResult.Lines.CollectionChanged += (_, ev) =>
            {
                if (ev.Action == NotifyCollectionChangedAction.Add && ev.NewItems != null)
                    foreach (TranscriptLine l in ev.NewItems) _addons.PublishLine(AddonManager.ToEvent(sessionId, true, l));
            };
            _addons.PublishSessionStarted(new SessionEvent(sessionId, true, $"Live · {device.Name}", started));
            // With encryption on, the key stays in memory until this session is saved, even if WhisperDesk locks meanwhile.
            _liveHold = Services.Security.Vault.IsEnabled ? Services.Security.Vault.Hold() : null;
            _live = new LiveSession(_engine, _speakerAnalyzer, _voices, options, SpeakersBox.IsChecked == true,
                                    Dispatcher, _liveResult, recording);
            _live.Status += msg => StatusText.Text = msg;
            _liveDevice = device;
            _recoveryDir = Path.Combine(LiveDir, $"recovery-{started:yyyyMMdd-HHmmss}");
            _autosave.Start();
            _audio.SamplesAvailable += _live.Feed;
            _audio.Stopped += OnAudioStopped;
            _audio.Start(device);

            ShowResult(_liveResult, live: true);
            LiveStopButton.IsEnabled = true;
            LiveDeviceBox.IsEnabled = false;
            LiveKeepAudioBox.IsEnabled = false;
            LiveTab.Header = "●  Live (recording)";
            _clock.Start();
            KeepAwake(true);
            StatusText.Text = $"Live · listening on {device.Name}…";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not start live: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Live transcription", MessageBoxButton.OK, MessageBoxImage.Error);
            await CleanupLiveAsync();
            DeleteRecovery();
            ReleaseLiveHold();
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void OnAudioStopped(Exception? ex)
    {
        if (ex != null) Dispatcher.BeginInvoke(() =>
            StatusText.Text = "⚠ Audio input stopped: " + ex.Message + " — everything so far is kept. Press Stop to save it.");
    }

    // ---------- Crash safety for live sessions ----------

    private static string LiveDir => Path.Combine(WhisperDesk.Services.AppPaths.LocalData, "live");
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(30) };
    private string? _recoveryDir;

    /// <summary>Every 30 s a running live session is written to a recovery folder, so a crash or power cut loses at most that much.</summary>
    private void AutosaveLive()
    {
        if (_live == null || _liveResult == null || _recoveryDir == null || _liveResult.Lines.Count == 0) return;
        try
        {
            Directory.CreateDirectory(_recoveryDir);
            var meta = new RecordingMeta
            {
                Title = $"Live session {_live.StartedAt:yyyy-MM-dd HH:mm}", Type = "Live", Created = _live.StartedAt,
                DurationSeconds = _live.Elapsed.TotalSeconds, Source = $"Live · {_liveDevice?.Name}", Model = SelectedModel.Name,
                ExternalMedia = _live.RecordingPath, Dir = _recoveryDir,
            };
            _live.FlushRecording();
            RecordingLibrary.WriteTranscript(meta, _liveResult);
        }
        catch (Exception ex) { App.Log("Autosave", ex); }
    }

    private void DeleteRecovery()
    {
        _autosave.Stop();
        if (_recoveryDir != null) try { Services.Security.WorkDir.WipeTree(_recoveryDir); } catch { }
        _recoveryDir = null;
    }

    private IDisposable? _liveHold;

    private void ReleaseLiveHold()
    {
        _liveHold?.Dispose();
        _liveHold = null;
    }

    /// <summary>On startup: live sessions that never got stopped (crash / power cut) are saved to the library's “Recovered” folder.</summary>
    private void RecoverLiveSessions()
    {
        if (!Directory.Exists(LiveDir)) return;
        var recovered = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(LiveDir, "recovery-*"))
        {
            try
            {
                var meta = RecordingLibrary.ReadMeta(dir);
                var result = RecordingLibrary.Load(meta);
                var wav = meta.ExternalMedia;
                // Only WhisperDesk's own live folder is trusted as the source of a recovered recording.
                if (wav != null && !Path.GetFullPath(wav).StartsWith(Path.GetFullPath(LiveDir) + "\\", StringComparison.OrdinalIgnoreCase)) wav = null;
                if (wav != null && Services.Security.SecureFile.Exists(wav)) LiveRecording.Finish(wav);   // cut off by the crash: give it a proper end
                meta.ExternalMedia = null;
                meta.MediaFile = null;
                meta.Id = Guid.NewGuid().ToString("N");
                meta.Title += " (recovered)";
                meta.Notes = "Recovered automatically after WhisperDesk closed unexpectedly. The last ~30 seconds may be missing from the transcript.";
                RecordingLibrary.Save(result, meta, "Recovered", mediaToMove: wav != null && Services.Security.SecureFile.Exists(wav) ? wav : null, mediaToCopy: null, mediaToLink: null);
                Services.Security.WorkDir.WipeTree(dir);
                recovered.Add($"{meta.Title} — {result.Lines.Count} lines");
            }
            catch (Exception ex) { App.Log("Recovery", ex); }
        }
        if (recovered.Count == 0) return;
        MessageBox.Show(this, "WhisperDesk closed unexpectedly during a live session last time. It was saved to your library, " +
                              "in the “Recovered” folder:\n\n" + string.Join("\n", recovered), "Session recovered",
            MessageBoxButton.OK, MessageBoxImage.Information);
        _library.Refresh();
    }

    private async void OnLiveStop(object sender, RoutedEventArgs e)
    {
        if (_live == null || _liveResult == null) return;
        LiveStopButton.IsEnabled = false;
        StatusText.Text = "Finishing the last lines…";

        var session = _live;
        var result = _liveResult;
        var device = _liveDevice;
        AutosaveLive();               // final snapshot: protects the session while the save dialog is open
        _autosave.Stop();
        await CleanupLiveAsync();
        try { await FinishLiveAsync(session, result, device); }
        finally { DeleteRecovery(); ReleaseLiveHold(); }
    }

    private async Task FinishLiveAsync(LiveSession session, TranscriptResult result, AudioDeviceInfo? device)
    {
        await Task.CompletedTask;
        _addons.PublishSessionStopped(new SessionEvent(_liveSessionId, true, $"Live · {device?.Name}", DateTime.Now));

        if (result.Lines.Count == 0)
        {
            StatusText.Text = "Live session ended. Nothing was said, so nothing was saved.";
            if (session.RecordingPath != null) try { Services.Security.SecureFile.Delete(session.RecordingPath); } catch { }
            return;
        }

        var meta = new RecordingMeta
        {
            Title = $"Live session {session.StartedAt:yyyy-MM-dd HH:mm}",
            Type = "Live",
            Created = session.StartedAt,
            DurationSeconds = session.Elapsed.TotalSeconds,
            Source = $"Live · {device?.Name}",
            Model = SelectedModel.Name,
        };
        var info = session.RecordingPath != null
            ? $"Saves the transcript and the audio recording ({session.Elapsed:hh\\:mm\\:ss})."
            : "Saves the transcript only (audio recording was off).";
        var dlg = new SaveToLibraryWindow(meta, hasExternalMedia: false, info, discardLabel: "Don't save",
                                          review: result, player: _linePlayer) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                RecordingLibrary.Save(result, dlg.Meta, dlg.Folder, mediaToMove: session.RecordingPath, mediaToCopy: null, mediaToLink: null);
                _addons.PublishSaved(AddonManager.ToSavedEvent(_liveSessionId, dlg.Meta.Title, dlg.Meta.Source, dlg.Folder, result));
                var learned = EnrollVoices(result);
                StatusText.Text = $"Saved “{dlg.Meta.Title}” to the library." +
                                  (learned > 0 ? $" {learned} voice(s) added to your voice library." : "");
                ShowResult(result);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not save to library: " + ex.Message;
            }
        }
        else
        {
            if (session.RecordingPath != null) try { Services.Security.SecureFile.Delete(session.RecordingPath); } catch { }
            StatusText.Text = "Live session discarded.";
        }
    }

    private async Task CleanupLiveAsync()
    {
        _clock.Stop();
        KeepAwake(_cts != null);
        _audio.Stop();
        _audio.Stopped -= OnAudioStopped;
        if (_live != null)
        {
            _audio.SamplesAvailable -= _live.Feed;
            try { await _live.StopAsync(); } catch { }
            _live.Dispose();
            _live = null;
        }
        LiveLevel.Value = 0;
        LiveStopButton.IsEnabled = false;
        LiveDeviceBox.IsEnabled = true;
        LiveKeepAudioBox.IsEnabled = true;
        LiveTab.Header = "●  Live";
        SetBusy(_cts != null);
        if (_shown == _liveResult && _shown != null) ShowResult(_shown); // drop the "LIVE" title
    }

    // ---------- Library ----------

    private void OnSaveToLibrary(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not QueueItem { Result: { } result, State: ItemState.Done } item)
        {
            MessageBox.Show(this, "Select a finished file in the list first.", "Save to library");
            return;
        }
        if (result.Library != null)
        {
            MessageBox.Show(this, $"This transcript is already in the library as “{result.Library.Title}”.", "Save to library");
            return;
        }

        var meta = new RecordingMeta
        {
            Title = Path.GetFileNameWithoutExtension(item.FilePath),
            Type = SubtitleBurner.IsVideo(item.FilePath) ? "Video" : "Other",
            Created = File.GetLastWriteTime(item.FilePath),
            DurationSeconds = result.Lines.Count > 0 ? result.Lines[^1].End.TotalSeconds : 0,
            Source = $"File · {item.FilePath}",
            Model = SelectedModel.Name,
        };
        var dlg = new SaveToLibraryWindow(meta, hasExternalMedia: true, info: $"From {item.FileName}",
                                          review: result, player: _linePlayer) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try
        {
            RecordingLibrary.Save(result, dlg.Meta, dlg.Folder, mediaToMove: null,
                mediaToCopy: dlg.CopyMediaIntoLibrary ? item.FilePath : null,
                mediaToLink: dlg.CopyMediaIntoLibrary ? null : item.FilePath);
            var learned = EnrollVoices(result);
            StatusText.Text = $"Saved “{dlg.Meta.Title}” to the library." +
                              (learned > 0 ? $" {learned} voice(s) added to your voice library." : "");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save to library: " + ex.Message;
        }
    }

    private void OpenFromLibrary(RecordingMeta meta)
    {
        try
        {
            var result = RecordingLibrary.Load(meta);
            ShowResult(result);
            TranscriptTitle.Text = $"LIBRARY · {meta.Title.ToUpperInvariant()}";
            StatusText.Text = $"Opened “{meta.Title}”. Rename speakers or fix lines, then click “Save changes & update files”.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open this recording: " + ex.Message, "Library");
        }
    }

    /// <summary>▶ on a transcript line: play just that part of the audio.</summary>
    private async void OnPlayLine(object sender, ExecutedRoutedEventArgs e)
    {
        if (_shown == null || e.Parameter is not TranscriptLine line) return;
        try
        {
            TranscriptList.SelectedItem = line;
            await _linePlayer.ToggleLineAsync(_shown, line);
            StatusText.Text = line.IsPlaying ? $"Playing {line.Time} — {line.Speaker?.Tag ?? "no speaker"} (press ■ to stop)" : "Stopped.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    /// <summary>✎ on a transcript line: correct the text with a required, logged reason.</summary>
    private void OnEditLine(object sender, ExecutedRoutedEventArgs e)
    {
        if (_shown == null || e.Parameter is not TranscriptLine line) return;
        TranscriptList.SelectedItem = line;
        var dlg = new EditLineWindow(_shown, line, _linePlayer) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Edit is not { } edit) return;

        _addons.PublishLineEdited(new LineEditedEvent(_shown.Library?.Id ?? _shown.SourceFile, line.Start, line.Speaker?.Label,
            edit.OldText, edit.NewText, edit.Reason, edit.User, edit.At));
        StatusText.Text = $"Line [{line.Time}] edited and logged ({edit.AtText}). " +
                          (_shown == _liveResult ? "It is saved with the session." : "Click “Save changes & update files” to write it to disk.");
    }

    // ---------- Add-ons ----------

    private void OnAddons(object sender, RoutedEventArgs e) => OpenAddons(this);

    /// <summary>The Add-ons window (also opened from Settings → Security &amp; privacy → Add-on permissions).</summary>
    public void OpenAddons(Window owner) => new AddonsWindow(_addons) { Owner = owner }.ShowDialog();

    /// <summary>After encryption was switched on or off: the library is now stored differently.</summary>
    public void OnSecurityChanged()
    {
        _library.Refresh();
        UpdateStorageBanner();
        LockMenuItem.IsEnabled = Services.Security.Vault.IsEnabled;
    }

    private void OnLock(object sender, RoutedEventArgs e) => Services.Security.AppLock.Lock("locked by you");

    private void OnSecuritySettings(object sender, RoutedEventArgs e)
    {
        var library = RecordingLibrary.Root;
        new SettingsWindow { Owner = this }.ShowSection("Security & privacy").ShowDialog();
        if (!LibraryStorage.SamePath(library, RecordingLibrary.Root)) _library.Refresh();
        UpdateStorageBanner();
    }

    /// <summary>Sends a finished file transcription to add-ons (as a session with its lines, then "saved").</summary>
    private void PublishFileTranscript(TranscriptResult result, string file)
    {
        var id = Guid.NewGuid().ToString("N");
        _addons.PublishSessionStarted(new SessionEvent(id, false, $"File · {file}", DateTime.Now));
        foreach (var l in result.Lines) _addons.PublishLine(AddonManager.ToEvent(id, false, l));
        _addons.PublishSaved(AddonManager.ToSavedEvent(id, Path.GetFileNameWithoutExtension(file), $"File · {file}", null, result));
        _addons.PublishSessionStopped(new SessionEvent(id, false, $"File · {file}", DateTime.Now));
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (LiveRunning && MessageBox.Show(this, "A live session is running. Stop it and quit without saving?", "WhisperDesk",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        if (!LiveRunning && _cts != null && MessageBox.Show(this, "Files are being transcribed. Cancel the job and quit?", "WhisperDesk",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        if (_scheduleTimer.IsEnabled && MessageBox.Show(this, $"A transcription is scheduled for {_scheduledAt:ddd HH:mm}. It only runs while WhisperDesk is open. Quit anyway?",
                "WhisperDesk", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        App.EmergencySave = null;
        DeleteRecovery();   // the user chose to quit without saving
        _audio.Dispose();
        _live?.Dispose();
        _cts?.Cancel();
        _linePlayer.Stop();
        _library.SaveTreeState();
        _feed?.Close();
        SaveSettings();
        _engine.Dispose();
        _speakerAnalyzer.Dispose();
        _addons.Dispose();
    }
}

