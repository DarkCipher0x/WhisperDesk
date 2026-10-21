using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace WhisperDesk.Services;

public sealed class KnownVoice
{
    public string Name { get; set; } = "";
    public string Sex { get; set; } = Sexes.Unspecified;
    public List<float[]> Embeddings { get; set; } = new();
    public DateTime Updated { get; set; } = DateTime.Now;

    /// <summary>Average of all samples (normalised) — more stable than any single sample.</summary>
    public float[] Centroid() => VoiceLibrary.Average(Embeddings);
}

/// <summary>
/// Locally stored voiceprints (%LOCALAPPDATA%\WhisperDesk\voices.json). Naming a speaker and saving with
/// "Remember this voice" adds a sample, so the same person is recognised automatically later.
/// </summary>
public sealed class VoiceLibrary
{
    private const int MaxSamplesPerVoice = 20;
    public const float DefaultMatchThreshold = 0.5f;

    private static string FilePath => Path.Combine(AppPaths.LocalData, "voices.json");

    /// <summary>The app-wide library (shared by all windows and the live session).</summary>
    public static VoiceLibrary Shared { get; } = Load();

    public List<KnownVoice> Voices { get; private set; } = new();

    /// <summary>Sorted names for the speaker-name dropdowns; kept in sync with <see cref="Voices"/>.</summary>
    public ObservableCollection<string> Names { get; } = new();

    private static VoiceLibrary Load()
    {
        var lib = new VoiceLibrary();
        try
        {
            if (!Security.SecureFile.Exists(FilePath)) lib._loaded = true;
            else if (Security.SecureFile.CanRead(FilePath))
            {
                lib.Voices = JsonSerializer.Deserialize<List<KnownVoice>>(Security.SecureFile.ReadAllText(FilePath)) ?? new();
                lib._loaded = true;
            }
        }
        catch { }
        lib.RefreshNames();
        return lib;
    }

    /// <summary>Saves the voice library (encrypted when encryption is on: voiceprints are biometric data).</summary>
    public void Save()
    {
        // Never replace a stored voice library that wasn't read (locked, or unreadable) with an empty one.
        if (!_loaded && Security.SecureFile.Exists(FilePath)) throw new Security.VaultLockedException();
        Security.SecureFile.WriteAllText(FilePath, JsonSerializer.Serialize(Voices));
        _loaded = true;
        RefreshNames();
    }

    /// <summary>The voice library file (plaintext name; stored as voices.json.wde when encrypted).</summary>
    public static string StoragePath => FilePath;

    /// <summary>After unlocking: reads the (encrypted) voice library. On lock: forgets it.</summary>
    public void Reload()
    {
        _loaded = false;
        try
        {
            if (!Security.SecureFile.Exists(FilePath)) { Voices = new(); _loaded = true; }
            else if (Security.SecureFile.CanRead(FilePath))
            {
                Voices = JsonSerializer.Deserialize<List<KnownVoice>>(Security.SecureFile.ReadAllText(FilePath)) ?? new();
                _loaded = true;
            }
            else Voices = new();
        }
        catch { Voices = new(); }
        RefreshNames();
    }

    private bool _loaded;

    public void Forget()
    {
        _loaded = false;
        Voices = new();
        RecordingNames = new();
        RefreshNames();
    }

    /// <summary>People named in saved recordings who have no voiceprint here yet (name, sex).</summary>
    public List<(string Name, string Sex)> RecordingNames { get; private set; } = new();

    /// <summary>Re-reads the names used in library recordings (in the background) and updates <see cref="Names"/>.</summary>
    public void RefreshRecordingNames() => Task.Run(() =>
    {
        try
        {
            var found = RecordingLibrary.All().SelectMany(r => r.Speakers)
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Select(s => s.Sex).FirstOrDefault(x => x != Sexes.Unspecified) ?? Sexes.Unspecified))
                .ToList();
            RecordingNames = found;
            RefreshNames();
        }
        catch { }
    });

    private void RefreshNames()
    {
        void Update()
        {
            var voiceNames = Voices.Select(v => v.Name.Trim()).ToList();
            var all = voiceNames.Concat(RecordingNames.Select(r => r.Name)
                    .Where(n => !voiceNames.Contains(n, StringComparer.OrdinalIgnoreCase)))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (all.SequenceEqual(Names)) return;
            Names.Clear();
            foreach (var n in all) Names.Add(n);
        }
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Update(); else d.Invoke(Update);
    }

    public KnownVoice? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null
            : Voices.FirstOrDefault(v => string.Equals(v.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public void Remember(string name, string sex, float[] embedding)
    {
        if (string.IsNullOrWhiteSpace(name) || embedding.Length == 0) return;
        var voice = Find(name);
        if (voice == null)
        {
            voice = new KnownVoice { Name = name.Trim() };
            Voices.Add(voice);
        }
        voice.Sex = sex;
        voice.Updated = DateTime.Now;
        voice.Embeddings.Add(Normalize(embedding));
        if (voice.Embeddings.Count > MaxSamplesPerVoice) voice.Embeddings.RemoveAt(0);
    }

    /// <summary>Merges voices from another voices.json (a backup): new people are added, known people get the extra samples.</summary>
    public int MergeFromJson(string json)
    {
        var other = JsonSerializer.Deserialize<List<KnownVoice>>(json) ?? new();
        int changed = 0;
        foreach (var v in other.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
        {
            var mine = Find(v.Name);
            if (mine == null) { Voices.Add(v); changed++; continue; }
            var fresh = v.Embeddings.Where(e => !mine.Embeddings.Any(m => m.SequenceEqual(e))).ToList();
            if (fresh.Count == 0) continue;
            mine.Embeddings.AddRange(fresh);
            while (mine.Embeddings.Count > MaxSamplesPerVoice) mine.Embeddings.RemoveAt(0);
            if (mine.Sex == Sexes.Unspecified) mine.Sex = v.Sex;
            changed++;
        }
        if (changed > 0) Save();
        return changed;
    }

    /// <summary>
    /// Renames a voice (and sets its sex). If another voice already has the new name, the two are merged
    /// (samples combined). Call <see cref="Save"/> afterwards.
    /// </summary>
    public bool Rename(KnownVoice voice, string newName, string sex)
    {
        newName = newName.Trim();
        if (newName.Length == 0) throw new ArgumentException("The name can't be empty.");
        voice.Sex = sex;
        voice.Updated = DateTime.Now;
        var other = Voices.FirstOrDefault(v => v != voice && string.Equals(v.Name, newName, StringComparison.OrdinalIgnoreCase));
        if (other == null) { voice.Name = newName; return false; }

        other.Embeddings.AddRange(voice.Embeddings);
        while (other.Embeddings.Count > MaxSamplesPerVoice) other.Embeddings.RemoveAt(0);
        other.Sex = sex;
        other.Updated = DateTime.Now;
        Voices.Remove(voice);
        return true;
    }

    public void Forget(string name) =>
        Voices.RemoveAll(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How similar a voiceprint is to a known voice: the better of (best single sample, averaged voice).
    /// The average helps when a person was enrolled from several short or noisy samples.
    /// </summary>
    public static float Score(KnownVoice v, float[] embedding)
    {
        var samples = v.Embeddings.Where(e => e.Length == embedding.Length).ToList();
        if (samples.Count == 0) return 0;
        var best = samples.Max(e => Cosine(e, embedding));
        return samples.Count > 1 ? Math.Max(best, Cosine(Average(samples), embedding)) : best;
    }

    /// <summary>Closest known voice regardless of threshold (used for "closest: …" hints).</summary>
    public (KnownVoice Voice, float Score)? BestMatch(float[] embedding)
    {
        if (embedding.Length == 0 || Voices.Count == 0) return null;
        var best = Voices.Select(v => (Voice: v, Score: Score(v, embedding))).MaxBy(x => x.Score);
        return best.Score > 0 ? best : null;
    }

    /// <summary>Matches detected speakers to known voices (each voice used at most once, best scores first).</summary>
    public Dictionary<int, (KnownVoice Voice, float Score)> Match(IEnumerable<DetectedSpeaker> speakers, float? threshold = null)
    {
        var minScore = threshold ?? AppSettings.Current.VoiceMatchThreshold;
        var candidates = new List<(int Speaker, KnownVoice Voice, float Score)>();
        foreach (var s in speakers.Where(s => s.Embedding.Length > 0))
            foreach (var v in Voices)
            {
                var score = Score(v, s.Embedding);
                if (score >= minScore) candidates.Add((s.Id, v, score));
            }

        var result = new Dictionary<int, (KnownVoice, float)>();
        var usedVoices = new HashSet<KnownVoice>();
        foreach (var c in candidates.OrderByDescending(c => c.Score))
        {
            if (result.ContainsKey(c.Speaker) || usedVoices.Contains(c.Voice)) continue;
            result[c.Speaker] = (c.Voice, c.Score);
            usedVoices.Add(c.Voice);
        }
        return result;
    }

    public static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return (float)(dot / (Math.Sqrt(na * nb) + 1e-9));
    }

    public static float[] Normalize(float[] v)
    {
        double n = Math.Sqrt(v.Sum(x => (double)x * x));
        return n < 1e-9 ? v : v.Select(x => (float)(x / n)).ToArray();
    }

    /// <summary>Mean of normalised vectors, normalised again.</summary>
    public static float[] Average(IReadOnlyCollection<float[]> vectors)
    {
        if (vectors.Count == 0) return Array.Empty<float>();
        var dim = vectors.First().Length;
        var sum = new double[dim];
        foreach (var v in vectors.Where(v => v.Length == dim))
        {
            var nv = Normalize(v);
            for (int i = 0; i < dim; i++) sum[i] += nv[i];
        }
        return Normalize(sum.Select(x => (float)x).ToArray());
    }
}
