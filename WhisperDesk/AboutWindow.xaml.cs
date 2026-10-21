using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>About box and launch screen (same design; the splash shows a status line instead of details).</summary>
public partial class AboutWindow : Window
{
    /// <summary>
    /// The app version everywhere it is shown or recorded (title bar, About, reports, backups, session files, add-ons).
    /// Comes from &lt;Version&gt; in WhisperDesk.csproj via the informational version; nothing else hard-codes it.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0];
        return asm.GetName().Version?.ToString(3) ?? "";
    }

    public AboutWindow(bool splash = false)
    {
        InitializeComponent();
        VersionText.Text = "v" + Version;
        if (splash)
        {
            SplashPanel.Visibility = Visibility.Visible;
            AboutPanel.Visibility = Visibility.Collapsed;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Topmost = false;
            return;
        }
        var npu = Services.Npu.NpuDetector.Result;
        EngineText.Text = $"Last transcription ran on: {TranscriptionEngine.LastBackend ?? "nothing yet"}, on this PC · " +
                          $"whisper.cpp ({TranscriptionEngine.LoadedRuntime ?? "loads on first use"}) · " +
                          $"NPU: {(npu == null ? "checking…" : npu.Summary)}" +
                          (TranscriptionEngine.SessionNpuProblem is { } p ? $" (this session: {p})" : "") + " · " +
                          $"speaker voiceprints: {Services.Npu.NpuSpeakerEmbedder.Status} · diarization: sherpa-onnx on the CPU · " +
                          $"{(AppSettings.Current.AiSummaryEnabled ? $"AI report: {AppSettings.Current.AiModel} via {AppSettings.Current.AiUrl}" : "AI report: off")}";
        LibraryText.Text = RecordingLibrary.Root;
        LibraryText.ToolTip = RecordingLibrary.Root;
        ModelsText.Text = ModelManager.ModelsDir;
        ModelsText.ToolTip = ModelManager.ModelsDir;
        DataText.Text = DataDir;
        DataText.ToolTip = DataDir;
        var cloud = PrivacyInfo.CloudWarnings();
        if (cloud.Count > 0)
        {
            CloudWarning.Text = "⚠ " + string.Join("\n⚠ ", cloud);
            CloudWarning.Visibility = Visibility.Visible;
        }
    }

    public static string DataDir => Path.Combine(WhisperDesk.Services.AppPaths.LocalData);

    public void SetStatus(string text) => SplashStatus.Text = text;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && AboutPanel.Visibility == Visibility.Visible) Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnGuide(object sender, RoutedEventArgs e) => GuideWindow.Open(Owner ?? this);

    private void OnPrivacy(object sender, RoutedEventArgs e) => GuideWindow.Open(Owner ?? this, GuideWindow.PrivacyPage);

    private void OnLicence(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else MessageBox.Show(this, "WhisperDesk is open-source software under the MIT License. © 2026 darkcipher0x.", "Licence");
    }

    private void OnDataFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataDir}\"") { UseShellExecute = true });
    }
}
