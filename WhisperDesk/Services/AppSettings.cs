using System.IO;
using System.Text.Json;

namespace WhisperDesk.Services;

/// <summary>Remembers the user's choices between runs (%LOCALAPPDATA%\WhisperDesk\settings.json).</summary>
public sealed class AppSettings
{
    // ----- Main window -----
    public string Model { get; set; } = "LargeV3Turbo";
    /// <summary>Pre-1.6 device choice; kept so older settings files still read. See <see cref="Hardware"/>.</summary>
    public bool UseGpu { get; set; } = true;
    /// <summary>
    /// Where transcription runs: "Auto" (NPU when one is found, else the Vulkan GPU, else CPU), "NPU", "GPU" or "CPU".
    /// Null in settings files written before 1.6: derived from <see cref="UseGpu"/> by <see cref="EffectiveHardware"/>.
    /// </summary>
    public string? Hardware { get; set; }
    /// <summary>
    /// Which OpenVINO device runs the Whisper decoder when the NPU engine is used: "CPU" (default, the known-good layout:
    /// encoder on the NPU, decoder on the CPU), "NPU" or "GPU". Not in the UI; for testing on NPU hardware.
    /// </summary>
    public string NpuDecoderDevice { get; set; } = "CPU";
    /// <summary>Where speaker voiceprints are computed: "Auto" (the NPU once it passed its check, else CPU) or "CPU".</summary>
    public string SpeakerHardware { get; set; } = "Auto";
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveHardware => Hardware is "Auto" or "NPU" or "GPU" or "CPU" ? Hardware : UseGpu ? "Auto" : "CPU";
    public string Language { get; set; } = "auto";
    public bool Translate { get; set; }
    public bool Txt { get; set; } = true;
    public bool Srt { get; set; } = true;
    public bool Vtt { get; set; }
    public bool Json { get; set; }
    public bool Docx { get; set; }
    public bool Pdf { get; set; }
    /// <summary>Also write a call summary (txt, docx, pdf, json).</summary>
    public bool Summary { get; set; }
    /// <summary>"Use my default" output formats (txt, srt, vtt, json, docx, pdf, summary, burn).</summary>
    public List<string> DefaultFormats { get; set; } = new() { "txt", "srt" };
    public string? OutputFolder { get; set; }
    public string Theme { get; set; } = "System";
    /// <summary>Show the launch screen while the app starts.</summary>
    public bool ShowSplash { get; set; } = true;
    /// <summary>Brand accent colour (RRGGBB). Default: darkcipher0x brand teal.</summary>
    public string AccentColor { get; set; } = "36D2CB";
    public bool IdentifySpeakers { get; set; } = true;
    public int SpeakerCount { get; set; }

    // ----- Advanced: transcription -----
    /// <summary>CPU threads for Whisper; 0 = automatic.</summary>
    public int Threads { get; set; }
    /// <summary>Vocabulary hint: names, jargon, spelling the model should prefer.</summary>
    public string InitialPrompt { get; set; } = "";
    public int GpuDevice { get; set; }
    public bool FlashAttention { get; set; }
    /// <summary>Don't feed previous text back as context (helps against repetition loops).</summary>
    public bool NoContext { get; set; }
    /// <summary>Only transcribe the parts where someone is speaking (stops "Thank you." hallucinations in silence/music).</summary>
    public bool SkipSilence { get; set; } = true;
    /// <summary>Drop typical Whisper hallucinations ("Thank you.", "Thanks for watching") and repeated-line loops.</summary>
    public bool FilterHallucinations { get; set; } = true;
    /// <summary>"Light", "Balanced" or "Maximum": how much CPU transcription and speaker analysis may use.</summary>
    public string PerformanceMode { get; set; } = "Balanced";

    // ----- Advanced: speakers -----
    /// <summary>Auto speaker-count sensitivity. Lower finds more speakers, higher merges similar voices.</summary>
    public float ClusterThreshold { get; set; } = 0.5f;
    /// <summary>How similar a voice must be to a saved voice to be recognised (0–1).</summary>
    public float VoiceMatchThreshold { get; set; } = 0.5f;
    public float MinSpeechSeconds { get; set; } = 0.3f;
    public float MinSilenceSeconds { get; set; } = 0.5f;
    /// <summary>Two detected speakers whose voiceprints are at least this similar are merged into one (0–1).</summary>
    public float SpeakerMergeSimilarity { get; set; } = 0.6f;
    /// <summary>Upper limit on automatically detected speakers; the most similar voices are merged until it fits.</summary>
    public int MaxAutoSpeakers { get; set; } = 10;
    public int MaleBelowHz { get; set; } = 158;
    public int FemaleAboveHz { get; set; } = 168;

    // ----- Advanced: output -----
    public bool IncludeSexInTags { get; set; } = true;
    public bool SpeakerInSubtitles { get; set; } = true;
    /// <summary>Timestamp TXT lines even when speakers are off.</summary>
    public bool AlwaysTimestampTxt { get; set; }

    // ----- Burned-in subtitles -----
    public bool BurnSubtitles { get; set; }
    public int BurnFontSize { get; set; } = 22;
    public double BurnOutline { get; set; } = 2;
    /// <summary>Lower = better quality, bigger file (QP/CRF). 18–24 is typical.</summary>
    public int BurnQuality { get; set; } = 20;
    public bool BurnUseGpu { get; set; } = true;
    public string BurnFont { get; set; } = "Segoe UI";
    public bool BurnBold { get; set; } = true;
    public bool BurnItalic { get; set; }
    /// <summary>RRGGBB hex.</summary>
    public string BurnTextColor { get; set; } = "FFFFFF";
    public string BurnOutlineColor { get; set; } = "000000";
    /// <summary>"Outline" = outlined text; "Box" = text on a semi-transparent box.</summary>
    public string BurnStyle { get; set; } = "Outline";
    public int BurnBoxOpacity { get; set; } = 60;
    /// <summary>"Bottom" or "Top".</summary>
    public string BurnPosition { get; set; } = "Bottom";
    public int BurnMargin { get; set; } = 28;

    // ----- Advanced: live -----
    public int LiveSilenceMs { get; set; } = 600;
    public int LiveMaxUtteranceSeconds { get; set; } = 15;
    /// <summary>Live: how similar a voice must be to someone already heard to count as the same person.</summary>
    public float LiveSameSpeakerThreshold { get; set; } = 0.45f;
    public string? LiveDeviceId { get; set; }

    // ----- Advanced: tools -----
    public string FfmpegPath { get; set; } = "";
    /// <summary>Recordings library location; empty = the installer's choice, else %LOCALAPPDATA%\WhisperDesk\Library.</summary>
    public string LibraryPath { get; set; } = "";
    /// <summary>
    /// Set once the pre-1.5.2 default (Documents\WhisperDesk Library) has been checked for this profile, so an existing
    /// library is pinned where it is and a folder that later appears through cloud sync never changes the library silently.
    /// </summary>
    public bool LibraryDefaultChecked { get; set; }
    /// <summary>Storage warnings the user dismissed ("cloud|&lt;path&gt;", "stray|&lt;path&gt;"); a new path warns again.</summary>
    public List<string> DismissedStorageWarnings { get; set; } = new();
    /// <summary>Where Whisper and speaker models are stored (empty = %LOCALAPPDATA%\WhisperDesk\models).</summary>
    public string ModelsPath { get; set; } = "";
    /// <summary>Imported Whisper models used where they are (copied ones live in models\custom).</summary>
    public List<string> CustomModelPaths { get; set; } = new();

    // Summary report written by a local AI model (Ollama). Off by default; the offline report is always available.
    public bool AiSummaryEnabled { get; set; }
    public string AiUrl { get; set; } = AiReport.DefaultUrl;
    public string AiModel { get; set; } = AiReport.DefaultModel;
    /// <summary>Prompt with {title} {date} {duration} {speakers} {transcript} placeholders (empty = built-in default).</summary>
    public string AiPrompt { get; set; } = AiReport.DefaultPrompt;
    public string? LastLibraryFolder { get; set; }
    public string LastShareFormat { get; set; } = "html";
    // ----- Live feed window -----
    public double FeedFontSize { get; set; } = 18;
    public bool FeedShowTimes { get; set; }
    public bool FeedTopmost { get; set; }
    public double[]? FeedBounds { get; set; }

    /// <summary>Library folders left expanded in the folder tree.</summary>
    public List<string> ExpandedFolders { get; set; } = new();

    // ----- Add-ons -----
    public Dictionary<string, bool> AddonsEnabled { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> AddonOptions { get; set; } = new();
    /// <summary>What the user approved for each add-on: capabilities, DLL fingerprint, folder. Never restored from backups.</summary>
    public Dictionary<string, Addons.AddonApproval> AddonApprovals { get; set; } = new();

    // ----- Network (Settings → Security & privacy) -----
    /// <summary>"Ask" (default: a dialog says what, from where, how big), "Allow" or "Block".</summary>
    public string ModelDownloadPolicy { get; set; } = "Ask";
    /// <summary>Summaries may be written by an Ollama server on another computer (the transcript is sent there). Off by default.</summary>
    public bool AllowRemoteAi { get; set; }

    // ----- Protected settings -----
    /// <summary>
    /// When encryption is on: the settings that can reveal content (vocabulary hint, folder names, recent folders), encrypted
    /// with the vault. Their plaintext properties are blank in this file while it is on.
    /// </summary>
    public string? Protected { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string FilePath => Path.Combine(AppPaths.LocalData, "settings.json");

    /// <summary>The app-wide settings instance.</summary>
    public static AppSettings Current { get; private set; } = Load();

    /// <summary>True when settings.json already existed at start-up (a profile that ran WhisperDesk before).</summary>
    public static bool ExistingProfile { get; private set; }

    private static AppSettings Load()
    {
        try
        {
            ExistingProfile = File.Exists(FilePath);
            if (ExistingProfile)
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var toWrite = this;
            if (Security.Vault.IsEnabled)
            {
                // Content-revealing settings are written only in encrypted form.
                if (Security.Vault.IsUnlocked) { Protected = SealProtected(); SecurityMac = ComputeSecurityMac(); }
                toWrite = Clone();
                toWrite.InitialPrompt = "";
                toWrite.ExpandedFolders = new();
                toWrite.LastLibraryFolder = null;
            }
            else { Protected = null; SecurityMac = null; }
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(toWrite, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }

    private sealed record ProtectedPart(string InitialPrompt, List<string> ExpandedFolders, string? LastLibraryFolder);

    private string SealProtected()
    {
        var key = Security.Vault.DeriveKey("settings");
        try
        {
            var plain = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ProtectedPart(InitialPrompt, ExpandedFolders, LastLibraryFolder)));
            var blob = Security.SealedBlob.Seal(key, plain, "WhisperDesk settings v1");
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
            return JsonSerializer.Serialize(blob);
        }
        finally { Security.VaultCrypto.Clear(key); }
    }

    /// <summary>After unlocking: reads the encrypted settings back. Before encryption existed, the plain values stay.</summary>
    public void UnsealProtected()
    {
        if (string.IsNullOrEmpty(Protected) || !Security.Vault.IsUnlocked) return;
        var key = Security.Vault.DeriveKey("settings");
        try
        {
            var plain = JsonSerializer.Deserialize<Security.SealedBlob>(Protected)!.Open(key, "WhisperDesk settings v1");
            var p = JsonSerializer.Deserialize<ProtectedPart>(plain);
            Security.VaultCrypto.Clear(plain);
            if (p == null) return;
            InitialPrompt = p.InitialPrompt ?? "";
            ExpandedFolders = p.ExpandedFolders ?? new();
            LastLibraryFolder = p.LastLibraryFolder;
        }
        catch { }
        finally { Security.VaultCrypto.Clear(key); }
    }

    /// <summary>
    /// With encryption on: a MAC (key derived from the vault) over the settings that decide where data may go: add-on
    /// approvals and on/off states, the AI server and whether it may be another computer. Editing them in settings.json
    /// outside WhisperDesk is detected at the next unlock and they are reset to safe values.
    /// </summary>
    public string? SecurityMac { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool SecuritySettingsWereReset { get; private set; }

    private string SecurityState() => JsonSerializer.Serialize(new
    {
        Approvals = AddonApprovals.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new { k.Key, k.Value.Sha256, Caps = k.Value.Capabilities.OrderBy(c => c, StringComparer.Ordinal), k.Value.FilesFolder }),
        Enabled = AddonsEnabled.Where(k => k.Value).Select(k => k.Key).OrderBy(k => k, StringComparer.Ordinal),
        AllowRemoteAi, AiUrl, ModelDownloadPolicy,
    });

    private string ComputeSecurityMac()
    {
        var key = Security.Vault.DeriveKey("settings-mac");
        try { return Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(SecurityState()))); }
        finally { Security.VaultCrypto.Clear(key); }
    }

    /// <summary>After unlocking: checks the MAC; on a mismatch, add-ons are switched off and unapproved and remote AI is turned off.</summary>
    public void VerifySecurity()
    {
        if (!Security.Vault.IsUnlocked) return;
        bool valid;
        try { valid = SecurityMac != null && Security.VaultCrypto.Equal(Convert.FromBase64String(SecurityMac), Convert.FromBase64String(ComputeSecurityMac())); }
        catch (FormatException) { valid = false; }
        if (valid) return;
        // Missing or wrong: settings.json was edited outside WhisperDesk (encryption-on saves always sign it). Anything that
        // could send data elsewhere goes back to off; the user is told and can switch things on again in the app.
        bool risky = AddonApprovals.Count > 0 || AddonsEnabled.Any(k => k.Value) || AllowRemoteAi || ModelDownloadPolicy == "Allow"
                     || (Uri.TryCreate(AiUrl, UriKind.Absolute, out var ai) && !Security.NetworkGate.IsLoopback(ai.Host));
        if (risky)
        {
            SecuritySettingsWereReset = true;
            AddonApprovals.Clear();
            foreach (var k in AddonsEnabled.Keys.ToList()) AddonsEnabled[k] = false;
            AllowRemoteAi = false;
            if (Uri.TryCreate(AiUrl, UriKind.Absolute, out var u) && !Security.NetworkGate.IsLoopback(u.Host)) AiUrl = AiReport.DefaultUrl;
            if (ModelDownloadPolicy == "Allow") ModelDownloadPolicy = "Ask";
        }
        Save();   // signs the (possibly reset) state
    }

    /// <summary>On lock: forget the content-revealing settings in memory (they come back on unlock).</summary>
    public void ForgetProtected()
    {
        if (!Security.Vault.IsEnabled) return;
        InitialPrompt = "";
        ExpandedFolders = new();
        LastLibraryFolder = null;
    }

    /// <summary>CPU threads for Whisper, from the Threads setting or the performance mode.</summary>
    public int WhisperThreads
    {
        get
        {
            if (Threads > 0) return Threads;
            var cores = Environment.ProcessorCount;
            return PerformanceMode switch
            {
                "Maximum" => Math.Max(4, cores / 2),
                "Light" => Math.Max(2, cores / 4),
                _ => Math.Max(2, cores / 2 - 2),
            };
        }
    }

    /// <summary>CPU threads for speaker analysis (leaves room for the UI and other work).</summary>
    public int SpeakerThreads => PerformanceMode switch
    {
        "Maximum" => Math.Max(2, Environment.ProcessorCount / 2),
        "Light" => 2,
        _ => Math.Max(2, Environment.ProcessorCount / 4),
    };

    /// <summary>Light mode runs speaker analysis after transcription instead of at the same time.</summary>
    public bool ParallelSpeakerAnalysis => PerformanceMode != "Light";

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    /// <summary>Copies the advanced section from another instance (used by the Settings window).</summary>
    public void CopyAdvancedFrom(AppSettings o)
    {
        Threads = o.Threads; InitialPrompt = o.InitialPrompt; GpuDevice = o.GpuDevice; FlashAttention = o.FlashAttention;
        NoContext = o.NoContext; SkipSilence = o.SkipSilence; FilterHallucinations = o.FilterHallucinations;
        PerformanceMode = o.PerformanceMode; SpeakerMergeSimilarity = o.SpeakerMergeSimilarity; MaxAutoSpeakers = o.MaxAutoSpeakers;
        ClusterThreshold = o.ClusterThreshold; VoiceMatchThreshold = o.VoiceMatchThreshold;
        MinSpeechSeconds = o.MinSpeechSeconds; MinSilenceSeconds = o.MinSilenceSeconds; MaleBelowHz = o.MaleBelowHz;
        FemaleAboveHz = o.FemaleAboveHz; IncludeSexInTags = o.IncludeSexInTags; SpeakerInSubtitles = o.SpeakerInSubtitles;
        AlwaysTimestampTxt = o.AlwaysTimestampTxt; LiveSilenceMs = o.LiveSilenceMs;
        BurnFontSize = o.BurnFontSize; BurnOutline = o.BurnOutline; BurnQuality = o.BurnQuality; BurnUseGpu = o.BurnUseGpu;
        BurnFont = o.BurnFont; BurnBold = o.BurnBold; BurnItalic = o.BurnItalic; BurnTextColor = o.BurnTextColor;
        BurnOutlineColor = o.BurnOutlineColor; BurnStyle = o.BurnStyle; BurnBoxOpacity = o.BurnBoxOpacity;
        BurnPosition = o.BurnPosition; BurnMargin = o.BurnMargin;
        LiveMaxUtteranceSeconds = o.LiveMaxUtteranceSeconds; FfmpegPath = o.FfmpegPath; NpuDecoderDevice = o.NpuDecoderDevice;
        if (o.Hardware is "Auto" or "NPU" or "GPU" or "CPU") { Hardware = o.Hardware; UseGpu = o.Hardware != "CPU"; }
        SpeakerHardware = o.SpeakerHardware is "CPU" ? "CPU" : "Auto";
        LiveSameSpeakerThreshold = o.LiveSameSpeakerThreshold; LibraryPath = o.LibraryPath; ModelsPath = o.ModelsPath;
        AiSummaryEnabled = o.AiSummaryEnabled; AiUrl = o.AiUrl; AiModel = o.AiModel; AiPrompt = o.AiPrompt; AccentColor = o.AccentColor;
        AllowRemoteAi = o.AllowRemoteAi;
        ModelDownloadPolicy = o.ModelDownloadPolicy is "Ask" or "Allow" or "Block" ? o.ModelDownloadPolicy : "Ask";
    }
}
