namespace WhisperDesk.Services;

/// <summary>FFT, spectrogram columns, frame pitch/loudness and colour maps for the forensics view.</summary>
public static class Spectral
{
    public const int Rate = AudioDecoder.SampleRate;

    /// <summary>In-place radix-2 FFT (n must be a power of two).</summary>
    public static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }

    private static readonly Dictionary<int, double[]> Windows = new();
    private static double[] Hann(int n)
    {
        lock (Windows)
        {
            if (Windows.TryGetValue(n, out var w)) return w;
            w = new double[n];
            for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            return Windows[n] = w;
        }
    }

    /// <summary>Magnitude spectrum in dBFS (n/2 bins, 0..Rate/2) of the frame centred at <paramref name="centre"/>.</summary>
    public static double[] SpectrumDb(float[] x, int centre, int n = 1024)
    {
        var re = new double[n]; var im = new double[n]; var w = Hann(n);
        int start = centre - n / 2;
        for (int i = 0; i < n; i++)
        {
            int k = start + i;
            re[i] = k >= 0 && k < x.Length ? x[k] * w[i] : 0;
        }
        Fft(re, im);
        var db = new double[n / 2];
        double norm = 2.0 / (n * 0.5);
        for (int i = 0; i < n / 2; i++) db[i] = 20 * Math.Log10(Math.Sqrt(re[i] * re[i] + im[i] * im[i]) * norm + 1e-10);
        return db;
    }

    /// <summary>RMS level of a frame in dBFS.</summary>
    public static double LevelDb(float[] x, int start, int count)
    {
        double e = 0; int n = 0;
        for (int i = Math.Max(0, start); i < Math.Min(x.Length, start + count); i++, n++) e += x[i] * x[i];
        return n == 0 ? -120 : 10 * Math.Log10(e / n + 1e-12);
    }

    /// <summary>Pitch (Hz) of a 40 ms frame by normalised autocorrelation; NaN when unvoiced.</summary>
    public static double PitchHz(float[] x, int centre)
    {
        int frame = Rate / 25, minLag = Rate / 500, maxLag = Rate / 65, start = centre - frame / 2;
        if (start < 0 || start + frame + maxLag >= x.Length) return double.NaN;
        double energy = 0;
        for (int i = 0; i < frame; i++) energy += x[start + i] * x[start + i];
        if (Math.Sqrt(energy / frame) < 0.006) return double.NaN;
        double best = 0; int lagBest = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double r = 0, e2 = 0;
            for (int i = 0; i < frame; i += 2) { r += x[start + i] * x[start + i + lag]; e2 += x[start + i + lag] * x[start + i + lag]; }
            double v = r / Math.Sqrt(energy / 2 * e2 + 1e-12);
            if (v > best) { best = v; lagBest = lag; }
        }
        return best >= 0.6 && lagBest > 0 ? (double)Rate / lagBest : double.NaN;
    }

    /// <summary>Perceptual "inferno"-style colour map (dark → purple → orange → pale yellow), t in 0..1, as BGRA.</summary>
    public static uint Inferno(double t)
    {
        t = Math.Clamp(t, 0, 1);
        (double r, double g, double b)[] stops =
        {
            (0, 0, 4), (40, 11, 84), (101, 21, 110), (159, 42, 99), (212, 72, 66), (245, 125, 21), (250, 193, 39), (252, 255, 164),
        };
        double p = t * (stops.Length - 1);
        int i = Math.Min((int)p, stops.Length - 2);
        double f = p - i;
        var (r0, g0, b0) = stops[i]; var (r1, g1, b1) = stops[i + 1];
        byte R = (byte)(r0 + (r1 - r0) * f), G = (byte)(g0 + (g1 - g0) * f), B = (byte)(b0 + (b1 - b0) * f);
        return 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
    }
}
