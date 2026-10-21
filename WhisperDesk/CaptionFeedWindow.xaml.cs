using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>
/// A read-only, chat-style live feed of the transcript (speaker names in their colours), for a second monitor, the side of
/// the screen or full screen. Follows the live session while one runs, otherwise the transcript in the main window.
/// </summary>
public partial class CaptionFeedWindow : Window
{
    public static readonly DependencyProperty FeedFontSizeProperty =
        DependencyProperty.Register(nameof(FeedFontSize), typeof(double), typeof(CaptionFeedWindow), new PropertyMetadata(18.0));
    public static readonly DependencyProperty FeedTimeSizeProperty =
        DependencyProperty.Register(nameof(FeedTimeSize), typeof(double), typeof(CaptionFeedWindow), new PropertyMetadata(12.0));

    public double FeedFontSize { get => (double)GetValue(FeedFontSizeProperty); set => SetValue(FeedFontSizeProperty, value); }
    public double FeedTimeSize { get => (double)GetValue(FeedTimeSizeProperty); set => SetValue(FeedTimeSizeProperty, value); }

    private System.Collections.ObjectModel.ObservableCollection<TranscriptLine>? _source;
    private bool _follow = true;
    private WindowState _beforeFull;
    private WindowStyle _styleBeforeFull;

    public CaptionFeedWindow()
    {
        InitializeComponent();
        var s = AppSettings.Current;
        FeedFontSize = Math.Clamp(s.FeedFontSize, 10, 72);
        FeedTimeSize = Math.Max(9, FeedFontSize * 0.62);
        TimesButton.IsChecked = s.FeedShowTimes;
        PinButton.IsChecked = s.FeedTopmost;
        Feed.ItemTemplate = (DataTemplate)FindResource(s.FeedShowTimes ? "LineWithTime" : "LineNoTime");
        if (s.FeedBounds is { Length: 4 } b && b[2] > 100 && b[3] > 100) { Left = b[0]; Top = b[1]; Width = b[2]; Height = b[3]; }
        else { Left = SystemParameters.WorkArea.Right - Width - 20; Top = SystemParameters.WorkArea.Top + 40; }
        Closing += (_, _) =>
        {
            s.FeedFontSize = FeedFontSize;
            s.FeedShowTimes = TimesButton.IsChecked == true;
            s.FeedTopmost = PinButton.IsChecked == true;
            if (WindowState == WindowState.Normal && WindowStyle != WindowStyle.None) s.FeedBounds = new[] { Left, Top, Width, Height };
            s.Save();
            SetSource(null, "");
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    /// <summary>Shows this transcript (or nothing). Called by the main window whenever the shown transcript changes.</summary>
    public void SetSource(TranscriptResult? result, string label) => SetLines(result?.Lines, label);

    /// <summary>Shows any live-updating list of lines (e.g. the lines a player has played so far).</summary>
    public void SetLines(System.Collections.ObjectModel.ObservableCollection<TranscriptLine>? lines, string label)
    {
        if (_source != null) _source.CollectionChanged -= OnLinesChanged;
        _source = lines;
        Feed.ItemsSource = lines;
        SourceText.Text = label;
        Title = string.IsNullOrEmpty(label) ? "Live feed — WhisperDesk" : $"{label} — live feed";
        if (lines != null) lines.CollectionChanged += OnLinesChanged;
        UpdateEmpty();
        _follow = true;
        ScrollToEnd();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmpty();
        if (_follow) Dispatcher.BeginInvoke(ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
        else JumpButton.Visibility = Visibility.Visible;
    }

    private void UpdateEmpty() => EmptyText.Visibility = _source is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;

    private void ScrollToEnd()
    {
        if (_source is not { Count: > 0 } s) return;
        Feed.ScrollIntoView(s[^1]);
        JumpButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>Stop following new lines while the user scrolls back; resume at the bottom.</summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0) return;   // caused by new content, not the user
        if (e.OriginalSource is ScrollViewer sv)
        {
            _follow = sv.VerticalOffset >= sv.ScrollableHeight - 4;
            if (_follow) JumpButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnJumpToEnd(object sender, RoutedEventArgs e) { _follow = true; ScrollToEnd(); }

    // ----- Text size -----
    private void Zoom(double delta)
    {
        FeedFontSize = Math.Clamp(FeedFontSize + delta, 10, 72);
        FeedTimeSize = Math.Max(9, FeedFontSize * 0.62);
    }
    private void OnSmaller(object sender, RoutedEventArgs e) => Zoom(-2);
    private void OnBigger(object sender, RoutedEventArgs e) => Zoom(2);
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        Zoom(e.Delta > 0 ? 2 : -2);
        e.Handled = true;
    }

    private void OnTimesChanged(object sender, RoutedEventArgs e) =>
        Feed.ItemTemplate = (DataTemplate)FindResource(TimesButton.IsChecked == true ? "LineWithTime" : "LineNoTime");

    private void OnPinChanged(object sender, RoutedEventArgs e) => Topmost = PinButton.IsChecked == true;

    // ----- Toolbar only on hover (a clean feed) -----
    private void OnMouseEnterWindow(object sender, MouseEventArgs e) => Toolbar.Visibility = Visibility.Visible;
    private void OnMouseLeaveWindow(object sender, MouseEventArgs e)
    {
        if (_source is { Count: > 0 }) Toolbar.Visibility = Visibility.Collapsed;
    }

    // ----- Snapping and full screen (on the monitor the window is on) -----
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>The work area (and full area) of the window's monitor, in WPF units.</summary>
    private (Rect Work, Rect Full) MonitorArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info))
            return (SystemParameters.WorkArea, new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));
        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        Rect R(RECT r) => new(m.Transform(new Point(r.Left, r.Top)), m.Transform(new Point(r.Right, r.Bottom)));
        return (R(info.rcWork), R(info.rcMonitor));
    }

    private void Snap(Func<Rect, Rect> place)
    {
        if (WindowStyle == WindowStyle.None) LeaveFullScreen();
        WindowState = WindowState.Normal;
        var r = place(MonitorArea().Work);
        Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
    }

    private void OnSnapLeft(object sender, RoutedEventArgs e) => Snap(w => new Rect(w.Left, w.Top, Math.Max(MinWidth, w.Width * 0.25), w.Height));
    private void OnSnapRight(object sender, RoutedEventArgs e) => Snap(w => new Rect(w.Right - Math.Max(MinWidth, w.Width * 0.25), w.Top, Math.Max(MinWidth, w.Width * 0.25), w.Height));
    private void OnSnapBottom(object sender, RoutedEventArgs e) => Snap(w => new Rect(w.Left, w.Bottom - Math.Max(MinHeight, w.Height * 0.22), w.Width, Math.Max(MinHeight, w.Height * 0.22)));

    private void OnFullScreen(object sender, RoutedEventArgs e)
    {
        if (WindowStyle == WindowStyle.None) { LeaveFullScreen(); return; }
        _beforeFull = WindowState;
        _styleBeforeFull = WindowStyle;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        var full = MonitorArea().Full;
        Left = full.Left; Top = full.Top; Width = full.Width; Height = full.Height;
    }

    private void LeaveFullScreen()
    {
        WindowStyle = _styleBeforeFull == WindowStyle.None ? WindowStyle.SingleBorderWindow : _styleBeforeFull;
        ResizeMode = ResizeMode.CanResize;
        var w = MonitorArea().Work;
        Width = Math.Min(460, w.Width); Height = Math.Min(720, w.Height);
        Left = w.Right - Width - 20; Top = w.Top + 40;
        WindowState = _beforeFull == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.F11: OnFullScreen(sender, e); break;
            case Key.Escape when WindowStyle == WindowStyle.None: LeaveFullScreen(); break;
            case Key.OemPlus or Key.Add when ctrl: Zoom(2); break;
            case Key.OemMinus or Key.Subtract when ctrl: Zoom(-2); break;
            case Key.Left when ctrl: OnSnapLeft(sender, e); break;
            case Key.Right when ctrl: OnSnapRight(sender, e); break;
            case Key.Down when ctrl: OnSnapBottom(sender, e); break;
            case Key.End: OnJumpToEnd(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }
}
