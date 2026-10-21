using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace WhisperDesk.Services;

/// <summary>
/// Copies text without crashing when another program (clipboard history, OBS, a password manager, a remote-desktop
/// session…) briefly holds the clipboard open, which makes Clipboard.SetText throw CLIPBRD_E_CANT_OPEN.
/// Copied text is marked so Windows keeps it out of clipboard history (Win+V) and doesn't upload it to the cloud
/// clipboard (sync across devices), and WhisperDesk clears it when it locks.
/// </summary>
public static class SafeClipboard
{
    private static byte[]? _lastHash;

    private static DataObject Make(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        var zero = BitConverter.GetBytes(0);
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(zero));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(zero));
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(zero));
        return data;
    }

    /// <summary>True when the text reached the clipboard.</summary>
    public static bool TrySetText(string text)
    {
        _lastHash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(Make(text), true);
                return true;
            }
            catch (COMException) { Thread.Sleep(50); }
            catch (ExternalException) { Thread.Sleep(50); }
        }
        // Last resort: skip the flush (the step that usually fails). The text stays pasteable while WhisperDesk runs.
        try
        {
            Clipboard.SetDataObject(Make(text), false);
            return true;
        }
        catch (ExternalException) { return false; }
    }

    /// <summary>Copies the text, or tells the user another program is holding the clipboard.</summary>
    public static bool SetText(string text, Window? owner = null)
    {
        if (TrySetText(text)) return true;
        const string msg = "Couldn't copy: another program is using the clipboard right now. Wait a moment and try again.";
        if (owner != null) MessageBox.Show(owner, msg, "Copy", MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(msg, "Copy", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    /// <summary>On lock: empties the clipboard if it still holds what WhisperDesk copied (never anything else).</summary>
    public static void ClearIfOurs()
    {
        if (_lastHash == null) return;
        try
        {
            if (Clipboard.ContainsText() && SHA256.HashData(Encoding.UTF8.GetBytes(Clipboard.GetText())).AsSpan().SequenceEqual(_lastHash))
                Clipboard.Clear();
        }
        catch { }
        _lastHash = null;
    }
}
