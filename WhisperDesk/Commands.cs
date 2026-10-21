using System.Windows.Input;

namespace WhisperDesk;

/// <summary>Commands used by shared templates (so any window showing transcript lines can handle them).</summary>
public static class Commands
{
    /// <summary>Play the audio of one transcript line. Parameter: the <see cref="TranscriptLine"/>.</summary>
    public static readonly RoutedUICommand PlayLine = new("Play line", nameof(PlayLine), typeof(Commands));

    /// <summary>Correct a line's text (asks for a reason and records an audited edit). Parameter: the line.</summary>
    public static readonly RoutedUICommand EditLine = new("Edit line", nameof(EditLine), typeof(Commands));

    /// <summary>Open the chat-style live feed window.</summary>
    public static readonly RoutedUICommand OpenFeed = new("Live feed", nameof(OpenFeed), typeof(Commands));

    /// <summary>Lock WhisperDesk now (Ctrl+L).</summary>
    public static readonly RoutedUICommand Lock = new("Lock", nameof(Lock), typeof(Commands));
}
