using System.Numerics;

namespace WhisperDesk.Services.Npu;

/// <summary>
/// Whisper's log-mel spectrogram for one 30-second window: 400-point Hann STFT, hop 160, Slaney mel filterbank
/// (as librosa / OpenAI's reference), log10, clamp to max − 8, then (x + 4) / 4. Output is [mels, 3000] row-major.
/// </summary>
public sealed class LogMel
{
    public const int SampleRate = 16000, NFft = 400, Hop = 160, Frames = 3000, WindowSamples = Frames * Hop;
    private const int Bins = NFft / 2 + 1;

    private readonly int _mels;
    private readonly float[] _filters;   // [mels, Bins]
    private readonly float[] _cos, _sin; // [Bins, NFft], Hann window folded in

    public LogMel(int mels)
    {
        _mels = mels;
        _filters = SlaneyFilters(mels);
        _cos = new float[Bins * NFft];
        _sin = new float[Bins * NFft];
        for (int n = 0; n < NFft; n++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / NFft);   // periodic Hann
            for (int k = 0; k < Bins; k++)
            {
                double a = 2 * Math.PI * k * n / NFft;
                _cos[k * NFft + n] = (float)(w * Math.Cos(a));
                _sin[k * NFft + n] = (float)(w * Math.Sin(a));
            }
        }
    }

    public int Mels => _mels;

    /// <summary>Features of <paramref name="count"/> samples from <paramref name="offset"/>; zero-padded to 30 s.</summary>
    public float[] Compute(float[] samples, int offset, int count)
    {
        count = Math.Clamp(count, 0, Math.Min(WindowSamples, samples.Length - offset));
        // center=True: reflect-pad by NFft/2 on both sides of the (zero-padded) 30 s window.
        var padded = new float[WindowSamples + NFft];
        for (int i = 0; i < count; i++) padded[NFft / 2 + i] = samples[offset + i];
        for (int i = 1; i <= NFft / 2; i++)
        {
            padded[NFft / 2 - i] = padded[NFft / 2 + i];
            padded[NFft / 2 + WindowSamples - 1 + i] = padded[NFft / 2 + WindowSamples - 1 - i];
        }

        var mel = new float[_mels * Frames];
        Parallel.For(0, Frames, () => new float[Bins], (t, _, power) =>
        {
            var frame = new ReadOnlySpan<float>(padded, t * Hop, NFft);
            for (int k = 0; k < Bins; k++)
            {
                float re = Dot(frame, new ReadOnlySpan<float>(_cos, k * NFft, NFft));
                float im = Dot(frame, new ReadOnlySpan<float>(_sin, k * NFft, NFft));
                power[k] = re * re + im * im;
            }
            for (int m = 0; m < _mels; m++)
            {
                float v = Dot(power, new ReadOnlySpan<float>(_filters, m * Bins, Bins));
                mel[m * Frames + t] = MathF.Log10(MathF.Max(v, 1e-10f));
            }
            return power;
        }, _ => { });

        float max = float.MinValue;
        foreach (var v in mel) if (v > max) max = v;
        for (int i = 0; i < mel.Length; i++) mel[i] = (MathF.Max(mel[i], max - 8f) + 4f) / 4f;
        return mel;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = a.Length, i = 0;
        float sum = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
                acc += new Vector<float>(a.Slice(i)) * new Vector<float>(b.Slice(i));
            sum = Vector.Dot(acc, Vector<float>.One);
        }
        for (; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    /// <summary>librosa.filters.mel(sr=16000, n_fft=400, n_mels, htk=False, norm="slaney").</summary>
    private static float[] SlaneyFilters(int mels)
    {
        static double HzToMel(double f) => f < 1000 ? f / (200.0 / 3) : 15 + Math.Log(f / 1000) / (Math.Log(6.4) / 27);
        static double MelToHz(double m) => m < 15 ? m * (200.0 / 3) : 1000 * Math.Exp((m - 15) * (Math.Log(6.4) / 27));

        double lo = HzToMel(0), hi = HzToMel(SampleRate / 2.0);
        var points = new double[mels + 2];
        for (int i = 0; i < points.Length; i++) points[i] = MelToHz(lo + (hi - lo) * i / (mels + 1));

        var w = new float[mels * Bins];
        for (int m = 0; m < mels; m++)
        {
            double enorm = 2.0 / (points[m + 2] - points[m]);
            for (int k = 0; k < Bins; k++)
            {
                double f = k * (double)SampleRate / NFft;
                double lower = (f - points[m]) / (points[m + 1] - points[m]);
                double upper = (points[m + 2] - f) / (points[m + 2] - points[m + 1]);
                w[m * Bins + k] = (float)(Math.Max(0, Math.Min(lower, upper)) * enorm);
            }
        }
        return w;
    }
}
