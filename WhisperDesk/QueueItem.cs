using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace WhisperDesk;

public enum ItemState { Pending, Working, Done, Failed, Cancelled }

public sealed class QueueItem : INotifyPropertyChanged
{
    public QueueItem(string path) => FilePath = path;

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);

    private ItemState _state;
    public ItemState State { get => _state; set => Set(ref _state, value); }

    private string _status = "Waiting";
    public string Status { get => _status; set => Set(ref _status, value); }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    public string? LastOutputDir { get; set; }

    public TranscriptResult? Result { get; set; }

    /// <summary>Set when re-transcribing a library recording: the new transcript replaces that recording's.</summary>
    public Services.RecordingMeta? TargetLibrary { get; set; }

    /// <summary>Set by the scheduler: don't start before this time.</summary>
    public DateTime? NotBefore { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
