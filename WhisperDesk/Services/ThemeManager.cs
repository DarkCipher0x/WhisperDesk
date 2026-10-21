using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace WhisperDesk.Services;

public enum ThemeMode { System, Dark, Light }

/// <summary>Swaps the app's colour brushes between dark and light and darkens the window title bar.</summary>
public static class ThemeManager
{
    /// <summary>darkcipher0x "terminal" palette (teal on black). Dark = neon terminal; Light = ink on paper. Accent-derived colours are computed.</summary>
    private static readonly Dictionary<string, (string Dark, string Light)> Palette = new()
    {
        ["Bg"]          = ("#050909", "#F1F6F6"),
        ["Surface"]     = ("#0B1314", "#E3EDED"),
        ["Input"]       = ("#020505", "#FAFDFD"),
        ["Fg"]          = ("#D2F2EF", "#08191A"),
        ["Muted"]       = ("#5A8986", "#4E6E6C"),
        ["Line"]        = ("#16302F", "#BCD2D1"),
        ["Hover"]       = ("#0F2122", "#D5E5E5"),
        ["Track"]       = ("#0F2122", "#CFDFDF"),
        ["ScrollThumb"] = ("#234746", "#A3BEBD"),
        ["HeaderBg"]    = ("#000000", "#061010"),
        ["HeaderFg"]    = ("#D2F2EF", "#E2F4F2"),
    };

    public const string DefaultAccent = "36D2CB";   // darkcipher0x brand teal (from the cipher watermark)

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static bool IsDark { get; private set; }

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        IsDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());

        var res = Application.Current.Resources;
        foreach (var (key, (dark, light)) in Palette)
            res[key] = Frozen((Color)ColorConverter.ConvertFromString(IsDark ? dark : light));

        // Brand accent (Settings â†’ Appearance). On light backgrounds a neon accent is darkened for contrast.
        var brand = ParseHex(AppSettings.Current.AccentColor) ?? ParseHex(DefaultAccent)!.Value;
        var bg = (Color)ColorConverter.ConvertFromString(IsDark ? Palette["Bg"].Dark : Palette["Bg"].Light);
        var accent = IsDark ? brand : Darken(brand, Luminance(brand) > 0.45 ? 0.42 : 0.1);
        res["Accent"] = Frozen(accent);
        res["AccentHover"] = Frozen(IsDark ? Lighten(accent, 0.25) : Darken(accent, 0.2));
        res["Brand"] = Frozen(brand);                                    // header text stays the pure brand colour
        res["OnAccent"] = Frozen(Luminance(accent) > 0.5 ? Colors.Black : Colors.White);
        res["Selection"] = Frozen(Mix(bg, accent, IsDark ? 0.18 : 0.20));

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
    }

    public static Color? ParseHex(string? hex)
    {
        hex = (hex ?? "").Trim().TrimStart('#');
        if (hex.Length != 6) return null;
        try { return (Color)ColorConverter.ConvertFromString("#" + hex); } catch { return null; }
    }

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    private static Color Darken(Color c, double t) => Mix(c, Colors.Black, t);
    private static Color Lighten(Color c, double t) => Mix(c, Colors.White, t);

    /// <summary>Re-applies "System" mode when the Windows theme changes.</summary>
    public static void FollowSystemChanges()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.System)
                Application.Current.Dispatcher.Invoke(() => Apply(ThemeMode.System));
        };
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}
