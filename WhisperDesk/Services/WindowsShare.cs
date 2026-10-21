using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace WhisperDesk.Services;

/// <summary>
/// Opens the Windows share sheet (Mail, Outlook, Teams, Nearby Sharing, Phone Link…) for files from a desktop window.
/// Only what the user picks in the sheet leaves the PC, through the app they choose.
/// </summary>
public static class WindowsShare
{
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    private static Guid _dtmIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    /// <summary>Shows the share sheet for <paramref name="paths"/> next to <paramref name="owner"/>.</summary>
    public static async Task ShareFilesAsync(Window owner, string title, string description, IReadOnlyList<string> paths)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Windows Share needs Windows 10 (1809) or newer.");
        var hwnd = new WindowInteropHelper(owner).Handle;
        var items = new List<IStorageItem>();
        foreach (var p in paths) items.Add(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(p)));

        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var ptr = interop.GetForWindow(hwnd, ref _dtmIid);
        var manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(ptr);

        void OnRequested(DataTransferManager sender, DataRequestedEventArgs e)
        {
            sender.DataRequested -= OnRequested;
            var data = e.Request.Data;
            data.Properties.Title = title;
            data.Properties.Description = description;
            data.SetStorageItems(items, readOnly: true);
        }
        manager.DataRequested += OnRequested;
        interop.ShowShareUIForWindow(hwnd);
    }

    /// <summary>A fresh private folder for files that are only made to be shared (wiped on lock, exit and next start).</summary>
    public static string TempFolder()
    {
        return Security.WorkDir.NewFolder("share");
    }

    /// <summary>Deletes share folders older than a day.</summary>
    public static void CleanUp()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "WhisperDesk", "share");
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.EnumerateDirectories(root))
                if (Directory.GetCreationTime(d) < DateTime.Now.AddDays(-1)) Directory.Delete(d, true);
        }
        catch { }
    }
}
