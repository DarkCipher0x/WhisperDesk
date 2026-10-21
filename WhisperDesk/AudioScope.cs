using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>A coloured span on the speaker lane.</summary>
public sealed record ScopeSegment(double Start, double End, Brush Brush, string Label);
/// <summary>A marker on the ruler (integrity findings, notes).</summary>
public sealed record ScopeMarker(double Time, string Label, Brush Brush);

/// <summary>
/// Forensic audio display: time ruler with markers, speaker lane, waveform, spectrogram (0–8 kHz) with a pitch track,
/// loudness lane and a whole-recording overview. Wheel = zoom at the cursor, Shift+wheel = pan, drag = select,
/// click = seek, drag the overview = pan. Drawn directly (no per-sample visuals) so hours of audio stay fast.
/// </summary>
public sealed class AudioScope : FrameworkElement
{
    private const int Rate = AudioDecoder.SampleRate, PeakBlock = 64;
    private const double Ruler = 20, SpeakerLane = 16, LoudLane = 46, Overview = 30, Gap = 4, AxisW = 46;

    private float[] _samples = Array.Empty<float>();
    private float[] _peakMin = Array.Empty<float>(), _peakMax = Array.Empty<float>();
    private WriteableBitmap? _spec;
    private double[] _pitch = Array.Empty<double>(), _loud = Array.Empty<double>();
    private (double Start, double End, int Width, int Height) _specKey;
    private CancellationTokenSource? _specCts;

    public double Duration { get; private set; }
    public double ViewStart { get; private set; }
    public double ViewEnd { get; private set; }
    public double Playhead { get => _playhead; set { _playhead = value; FollowPlayhead(); InvalidateVisual(); } }
    private double _playhead;
    public double? SelStart { get; private set; }
    public double? SelEnd { get; private set; }
    public bool Follow { get; set; } = true;
    /// <summary>Only follow the playhead while audio is actually playing (so the view stays put while you examine it).</summary>
    public bool IsPlaying { get; set; }
    public double MaxFreq { get; set; } = 8000;
    public double DynamicRangeDb { get; set; } = 80;
    public IReadOnlyList<ScopeSegment> Segments { get; set; } = Array.Empty<ScopeSegment>();
    public IReadOnlyList<ScopeMarker> Markers { get; set; } = Array.Empty<ScopeMarker>();

    public event Action<double>? SeekRequested;
    public event Action? SelectionChanged;
    public event Action? ViewChanged;

    public AudioScope()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.IBeam;
    }

    public void SetAudio(float[] samples)
    {
        _samples = samples;
        Duration = samples.Length / (double)Rate;
        int blocks = (samples.Length + PeakBlock - 1) / PeakBlock;
        _peakMin = new float[blocks]; _peakMax = new float[blocks];
        Parallel.For(0, blocks, b =>
        {
            float mn = 0, mx = 0;
            int s = b * PeakBlock, e = Math.Min(samples.Length, s + PeakBlock);
            for (int i = s; i < e; i++) { var v = samples[i]; if (v < mn) mn = v; if (v > mx) mx = v; }
            _peakMin[b] = mn; _peakMax[b] = mx;
        });
        SetView(0, Math.Min(Duration, 60));
    }

    public void SetView(double start, double end)
    {
        double len = Math.Clamp(end - start, 0.05, Math.Max(0.05, Duration));
        start = Math.Clamp(start, 0, Math.Max(0, Duration - len));
        ViewStart = start; ViewEnd = start + len;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public void ZoomBy(double factor, double? around = null)
    {
        double c = around ?? (ViewStart + ViewEnd) / 2, len = (ViewEnd - ViewStart) * factor;
        double f = (c - ViewStart) / Math.Max(1e-9, ViewEnd - ViewStart);
        SetView(c - len * f, c - len * f + len);
    }

    public void ZoomToFit() => SetView(0, Duration);
    public void ZoomToSelection() { if (SelStart is { } a && SelEnd is { } b && b - a > 0.05) SetView(a - (b - a) * 0.1, b + (b - a) * 0.1); }
    public void ClearSelection() { SelStart = SelEnd = null; InvalidateVisual(); SelectionChanged?.Invoke(); }
    public void Select(double a, double b) { SelStart = Math.Min(a, b); SelEnd = Math.Max(a, b); InvalidateVisual(); SelectionChanged?.Invoke(); }

    private void FollowPlayhead()
    {
        if (!Follow || !IsPlaying || Duration <= 0 || _dragging) return;
        double len = ViewEnd - ViewStart;
        if (_playhead > ViewEnd - len * 0.02 || _playhead < ViewStart) SetView(_playhead - len * 0.1, _playhead - len * 0.1 + len);
    }

    // ---------------- layout ----------------

    private Rect Plot => new(AxisW, 0, Math.Max(10, ActualWidth - AxisW), ActualHeight);
    private (Rect Ruler, Rect Speakers, Rect Wave, Rect Spec, Rect Loud, Rect Over) Lanes()
    {
        var p = Plot;
        double free = Math.Max(60, p.Height - Ruler - SpeakerLane - LoudLane - Overview - Gap * 5);
        double wave = free * 0.38, spec = free - wave;
        double y = 0;
        Rect R(double h) { var r = new Rect(p.X, y, p.Width, h); y += h + Gap; return r; }
        return (R(Ruler), R(SpeakerLane), R(wave), R(spec), R(LoudLane), R(Overview));
    }

    private double X(double t) { var p = Plot; return p.X + (t - ViewStart) / (ViewEnd - ViewStart) * p.Width; }
    private double T(double x) { var p = Plot; return ViewStart + (x - p.X) / p.Width * (ViewEnd - ViewStart); }

    // ---------------- rendering ----------------

    private static Brush B(string key, Brush fallback) => Application.Current.TryFindResource(key) as Brush ?? fallback;
    private static readonly Typeface Mono = new("Cascadia Mono");

    private FormattedText Text(string s, double size, Brush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        var bg = B("Input", Brushes.Black); var fg = B("Fg", Brushes.White); var muted = B("Muted", Brushes.Gray);
        var line = B("Line", Brushes.DimGray); var accent = B("Accent", Brushes.Teal);
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Duration <= 0 || ActualWidth < 80) { dc.DrawText(Text("Loading audio…", 12, muted), new Point(AxisW + 8, 8)); return; }
        var (ruler, spk, wave, spec, loud, over) = Lanes();
        var linePen = new Pen(line, 1); linePen.Freeze();

        // Ruler with ticks
        double len = ViewEnd - ViewStart;
        double step = new[] { 0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 }.First(s => len / s <= Math.Max(4, ruler.Width / 90));
        for (double t = Math.Ceiling(ViewStart / step) * step; t <= ViewEnd; t += step)
        {
            double x = X(t);
            dc.DrawLine(linePen, new Point(x, ruler.Bottom - 6), new Point(x, ruler.Bottom));
            var ts = TimeSpan.FromSeconds(t);
            dc.DrawText(Text(step < 1 ? ts.ToString(@"m\:ss\.ff") : ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss"), 9.5, muted), new Point(x + 2, ruler.Y + 1));
        }
        foreach (var m in Markers.Where(m => m.Time >= ViewStart && m.Time <= ViewEnd))
        {
            double x = X(m.Time);
            var tri = new StreamGeometry();
            using (var g = tri.Open()) { g.BeginFigure(new Point(x - 5, ruler.Y), true, true); g.LineTo(new Point(x + 5, ruler.Y), true, false); g.LineTo(new Point(x, ruler.Y + 8), true, false); }
            dc.DrawGeometry(m.Brush, null, tri);
        }

        // Speaker lane
        dc.DrawRectangle(B("Surface", Brushes.Black), null, spk);
        foreach (var s in Segments.Where(s => s.End >= ViewStart && s.Start <= ViewEnd))
            dc.DrawRectangle(s.Brush, null, new Rect(Math.Max(spk.X, X(s.Start)), spk.Y + 2, Math.Max(1, Math.Min(spk.Right, X(s.End)) - Math.Max(spk.X, X(s.Start))), spk.Height - 4));
        dc.DrawText(Text("SPK", 8.5, muted), new Point(4, spk.Y + 2));

        // Waveform (min/max per pixel)
        dc.DrawRectangle(B("Surface", Brushes.Black), null, wave);
        dc.DrawLine(linePen, new Point(wave.X, wave.Y + wave.Height / 2), new Point(wave.Right, wave.Y + wave.Height / 2));
        var wavePen = new Pen(accent, 1); wavePen.Freeze();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            double mid = wave.Y + wave.Height / 2, half = wave.Height / 2 - 2;
            for (int px = 0; px < (int)wave.Width; px++)
            {
                double t0 = T(wave.X + px), t1 = T(wave.X + px + 1);
                var (mn, mx) = Range(t0, t1);
                g.BeginFigure(new Point(wave.X + px + 0.5, mid - mx * half), false, false);
                g.LineTo(new Point(wave.X + px + 0.5, mid - Math.Min(mn, mx - 0.004) * half), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, wavePen, geo);
        dc.DrawText(Text("WAVE", 8.5, muted), new Point(4, wave.Y + 2));
        dc.DrawText(Text("+1", 8.5, muted), new Point(AxisW - 18, wave.Y));
        dc.DrawText(Text("−1", 8.5, muted), new Point(AxisW - 18, wave.Bottom - 12));

        // Spectrogram + pitch
        EnsureSpectrogram((int)spec.Width, (int)spec.Height);
        if (_spec != null) dc.DrawImage(_spec, spec);
        else dc.DrawRectangle(Brushes.Black, null, spec);
        dc.DrawText(Text("SPEC", 8.5, muted), new Point(4, spec.Y + 2));
        for (double f = 1000; f < MaxFreq; f += MaxFreq > 4000 ? 2000 : 1000)
        {
            double y = spec.Bottom - f / MaxFreq * spec.Height;
            dc.DrawText(Text($"{f / 1000:0}k", 8.5, muted), new Point(AxisW - 20, y - 6));
        }
        if (_pitch.Length > 1)
        {
            var pitchPen = new Pen(new SolidColorBrush(Color.FromRgb(0x36, 0xD2, 0xCB)), 1.6); pitchPen.Freeze();
            double maxP = 500;
            var pg = new StreamGeometry();
            using (var g = pg.Open())
            {
                bool open = false;
                double prev = double.NaN;
                for (int i = 0; i < _pitch.Length; i++)
                {
                    var hz = _pitch[i];
                    // Break the line at gaps and at implausible jumps (octave errors), instead of drawing spikes.
                    if (double.IsNaN(hz)) { open = false; prev = double.NaN; continue; }
                    if (!double.IsNaN(prev) && Math.Abs(Math.Log2(hz / prev)) > 0.45) open = false;
                    prev = hz;
                    var pt = new Point(spec.X + (i + 0.5) * spec.Width / _pitch.Length, spec.Bottom - hz / maxP * spec.Height * 0.9);
                    if (!open) { g.BeginFigure(pt, false, false); open = true; } else g.LineTo(pt, true, false);
                }
            }
            pg.Freeze();
            dc.DrawGeometry(null, pitchPen, pg);
            dc.DrawText(Text("— pitch (0–500 Hz)", 8.5, new SolidColorBrush(Color.FromRgb(0x36, 0xD2, 0xCB))), new Point(spec.Right - 110, spec.Y + 2));
        }

        // Loudness lane (dBFS, −70..0)
        dc.DrawRectangle(B("Surface", Brushes.Black), null, loud);
        if (_loud.Length > 1)
        {
            var lp = new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D)), 1.2); lp.Freeze();
            var lg = new StreamGeometry();
            using (var g = lg.Open())
                for (int i = 0; i < _loud.Length; i++)
                {
                    var pt = new Point(loud.X + (i + 0.5) * loud.Width / _loud.Length, loud.Bottom - Math.Clamp((_loud[i] + 70) / 70, 0, 1) * (loud.Height - 2));
                    if (i == 0) g.BeginFigure(pt, false, false); else g.LineTo(pt, true, false);
                }
            lg.Freeze();
            dc.DrawGeometry(null, lp, lg);
        }
        dc.DrawText(Text("dBFS", 8.5, muted), new Point(4, loud.Y + 2));
        dc.DrawText(Text("0", 8.5, muted), new Point(AxisW - 12, loud.Y));
        dc.DrawText(Text("−70", 8.5, muted), new Point(AxisW - 24, loud.Bottom - 12));

        // Selection + playhead across the main lanes
        var top = ruler.Bottom; var bottom = loud.Bottom;
        if (SelStart is { } a && SelEnd is { } b && b > ViewStart && a < ViewEnd)
        {
            var sel = new SolidColorBrush(Color.FromArgb(55, 0x36, 0xD2, 0xCB));
            var r = new Rect(Math.Max(wave.X, X(a)), top, Math.Max(1, Math.Min(wave.Right, X(b)) - Math.Max(wave.X, X(a))), bottom - top);
            dc.DrawRectangle(sel, new Pen(accent, 1), r);
        }
        if (_playhead >= ViewStart && _playhead <= ViewEnd)
        {
            var pp = new Pen(Brushes.White, 1.2); pp.Freeze();
            dc.DrawLine(pp, new Point(X(_playhead), top), new Point(X(_playhead), bottom));
        }

        // Overview: whole recording with the visible window
        dc.DrawRectangle(B("Surface", Brushes.Black), linePen, over);
        var og = new StreamGeometry();
        using (var g = og.Open())
        {
            double mid = over.Y + over.Height / 2, half = over.Height / 2 - 2;
            for (int px = 0; px < (int)over.Width; px += 2)
            {
                var (mn, mx) = Range(px / over.Width * Duration, (px + 2) / over.Width * Duration);
                g.BeginFigure(new Point(over.X + px, mid - mx * half), false, false);
                g.LineTo(new Point(over.X + px, mid - mn * half), true, false);
            }
        }
        og.Freeze();
        dc.DrawGeometry(null, new Pen(muted, 1), og);
        var win = new Rect(over.X + ViewStart / Duration * over.Width, over.Y, Math.Max(3, (ViewEnd - ViewStart) / Duration * over.Width), over.Height);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0x36, 0xD2, 0xCB)), new Pen(accent, 1), win);
        dc.DrawLine(new Pen(Brushes.White, 1), new Point(over.X + _playhead / Duration * over.Width, over.Y), new Point(over.X + _playhead / Duration * over.Width, over.Bottom));
        dc.DrawText(Text("ALL", 8.5, muted), new Point(4, over.Y + 8));
    }

    /// <summary>Min/max sample value between two times (from the peak blocks, or raw samples when zoomed in).</summary>
    private (double Min, double Max) Range(double t0, double t1)
    {
        long s0 = (long)(Math.Max(0, t0) * Rate), s1 = (long)(Math.Max(0, t1) * Rate);
        if (s0 >= _samples.Length) return (0, 0);
        s1 = Math.Min(_samples.Length, Math.Max(s1, s0 + 1));
        float mn = 0, mx = 0;
        if (s1 - s0 < PeakBlock * 2)
            for (long i = s0; i < s1; i++) { var v = _samples[i]; if (v < mn) mn = v; if (v > mx) mx = v; }
        else
            for (long b = s0 / PeakBlock; b <= Math.Min(_peakMin.Length - 1, (s1 - 1) / PeakBlock); b++) { mn = Math.Min(mn, _peakMin[b]); mx = Math.Max(mx, _peakMax[b]); }
        return (mn, mx);
    }

    /// <summary>Computes the spectrogram, pitch and loudness for the visible range in the background.</summary>
    private void EnsureSpectrogram(int w, int h)
    {
        if (w < 4 || h < 4) return;
        var key = (ViewStart, ViewEnd, w, h);
        if (key == _specKey) return;
        _specKey = key;
        _specCts?.Cancel();
        var cts = _specCts = new CancellationTokenSource();
        var samples = _samples; double start = ViewStart, end = ViewEnd, maxF = MaxFreq, range = DynamicRangeDb;
        int cols = Math.Min(w, 1400);
        Task.Run(() =>
        {
            var pixels = new uint[cols * h];
            var pitch = new double[Math.Min(cols, 400)];
            var loud = new double[Math.Min(cols, 600)];
            int n = (end - start) < 3 ? 512 : 1024;
            for (int c = 0; c < cols; c++)
            {
                if (cts.IsCancellationRequested) return;
                int centre = (int)((start + (c + 0.5) / cols * (end - start)) * Rate);
                var db = Spectral.SpectrumDb(samples, centre, n);
                double binHz = (double)Rate / n;
                for (int y = 0; y < h; y++)
                {
                    double f = (h - 1 - y) / (double)(h - 1) * maxF;
                    int bin = Math.Min(db.Length - 1, (int)(f / binHz));
                    pixels[y * cols + c] = Spectral.Inferno((db[bin] + range) / range);
                }
            }
            for (int i = 0; i < pitch.Length; i++)
                pitch[i] = Spectral.PitchHz(samples, (int)((start + (i + 0.5) / pitch.Length * (end - start)) * Rate));
            int win = Math.Max(Rate / 50, (int)((end - start) * Rate / loud.Length));
            for (int i = 0; i < loud.Length; i++)
                loud[i] = Spectral.LevelDb(samples, (int)((start + (i + 0.5) / loud.Length * (end - start)) * Rate) - win / 2, win);
            if (cts.IsCancellationRequested) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (cts.IsCancellationRequested) return;
                var bmp = new WriteableBitmap(cols, h, 96, 96, PixelFormats.Bgra32, null);
                bmp.WritePixels(new Int32Rect(0, 0, cols, h), pixels, cols * 4, 0);
                bmp.Freeze();
                _spec = bmp; _pitch = pitch; _loud = loud;
                InvalidateVisual();
            }, System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    /// <summary>The current spectrogram/waveform view as a PNG (for reports).</summary>
    public BitmapSource Snapshot()
    {
        var rtb = new RenderTargetBitmap((int)Math.Max(1, ActualWidth * 1.5), (int)Math.Max(1, ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        rtb.Render(this);
        rtb.Freeze();
        return rtb;
    }

    // ---------------- interaction ----------------

    private bool _dragging, _panning;
    private double _dragFrom;
    private Point _downAt;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Duration <= 0) return;
        double at = T(e.GetPosition(this).X);
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            double shift = (ViewEnd - ViewStart) * (e.Delta > 0 ? -0.15 : 0.15);
            SetView(ViewStart + shift, ViewEnd + shift);
        }
        else ZoomBy(e.Delta > 0 ? 0.7 : 1 / 0.7, at);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Duration <= 0) return;
        Focus();
        var p = e.GetPosition(this);
        _downAt = p;
        var over = Lanes().Over;
        if (over.Contains(p)) { _panning = true; PanTo(p.X); }
        else if (p.X >= AxisW) { _dragging = true; _dragFrom = T(p.X); }
        CaptureMouse();
        e.Handled = true;
    }

    private void PanTo(double x)
    {
        var over = Lanes().Over;
        double c = (x - over.X) / over.Width * Duration, len = ViewEnd - ViewStart;
        SetView(c - len / 2, c + len / 2);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_panning) { PanTo(p.X); return; }
        if (_dragging && Math.Abs(p.X - _downAt.X) > 3)
        {
            SelStart = Math.Clamp(Math.Min(_dragFrom, T(p.X)), 0, Duration);
            SelEnd = Math.Clamp(Math.Max(_dragFrom, T(p.X)), 0, Duration);
            InvalidateVisual();
        }
        ToolTip = Duration > 0 && p.X >= AxisW ? HoverText(p) : null;
    }

    private string HoverText(Point p)
    {
        double t = Math.Clamp(T(p.X), 0, Duration);
        var spec = Lanes().Spec;
        var s = TimeSpan.FromSeconds(t).ToString(@"hh\:mm\:ss\.fff");
        if (spec.Contains(p)) s += $"  ·  {(spec.Bottom - p.Y) / spec.Height * MaxFreq:0} Hz";
        var seg = Segments.FirstOrDefault(x => t >= x.Start && t <= x.End);
        if (seg != null) s += "  ·  " + seg.Label;
        return s;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        var p = e.GetPosition(this);
        if (_dragging)
        {
            if (Math.Abs(p.X - _downAt.X) <= 3) SeekRequested?.Invoke(Math.Clamp(T(p.X), 0, Duration));
            else SelectionChanged?.Invoke();
        }
        _dragging = _panning = false;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
