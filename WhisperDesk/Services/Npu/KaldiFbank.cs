namespace WhisperDesk.Services.Npu;

/// <summary>
/// Kaldi-style log mel filterbank (as kaldi-native-fbank, which sherpa-onnx uses for speaker models): 25 ms Povey
/// window, 10 ms shift, DC removal, pre-emphasis 0.97, 512-point power spectrum, 80 mel bins from 20 Hz, natural log.
/// Matching sherpa-onnx exactly matters: voiceprints already in the voice library were made by it.
/// </summary>
public sealed class KaldiFbank
{
    public sealed record Options(int Bins = 80, float LowFreq = 20, float HighFreq = -400, bool SnipEdges = false,
                                 bool SubtractMean = false, float Scale = 32768f);

    private const int Rate = 16000, FrameLength = 400, Shift = 160, Padded = 512;
    private readonly Options _o;
    private readonly float[] _window = new float[FrameLength];
    private readonly (int First, float[] Weights)[] _banks;
    private readonly float[] _cos = new float[Padded / 2], _sin = new float[Padded / 2];
    private readonly int[] _rev = new int[Padded];

    public KaldiFbank(Options? options = null)
    {
        _o = options ?? new Options();
        for (int i = 0; i < FrameLength; i++)
            _window[i] = (float)Math.Pow(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameLength - 1)), 0.85);

        static double Mel(double f) => 1127.0 * Math.Log(1 + f / 700.0);
        double nyquist = Rate / 2.0, high = _o.HighFreq > 0 ? _o.HighFreq : nyquist + _o.HighFreq;
        double lo = Mel(_o.LowFreq), hi = Mel(high), delta = (hi - lo) / (_o.Bins + 1), width = (double)Rate / Padded;
        _banks = new (int, float[])[_o.Bins];
        for (int b = 0; b < _o.Bins; b++)
        {
            double left = lo + b * delta, center = left + delta, right = center + delta;
            int first = -1, last = -1;
            var w = new float[Padded / 2];
            for (int i = 0; i < Padded / 2; i++)
            {
                double m = Mel(width * i);
                if (m > left && m < right)
                {
                    w[i] = (float)(m <= center ? (m - left) / (center - left) : (right - m) / (right - center));
                    if (first < 0) first = i;
                    last = i;
                }
            }
            _banks[b] = first < 0 ? (0, Array.Empty<float>()) : (first, w[first..(last + 1)]);
        }

        for (int k = 0; k < Padded / 2; k++)
        {
            _cos[k] = (float)Math.Cos(-2 * Math.PI * k / Padded);
            _sin[k] = (float)Math.Sin(-2 * Math.PI * k / Padded);
        }
        int bits = (int)Math.Log2(Padded);
        for (int i = 0; i < Padded; i++)
        {
            int r = 0;
            for (int j = 0; j < bits; j++) r |= ((i >> j) & 1) << (bits - 1 - j);
            _rev[i] = r;
        }
    }

    public int Bins => _o.Bins;

    /// <summary>Features [frames, bins] row-major, and the frame count.</summary>
    public (float[] Features, int Frames) Compute(float[] samples)
    {
        int n = samples.Length;
        int frames = _o.SnipEdges ? (n < FrameLength ? 0 : 1 + (n - FrameLength) / Shift) : (n + Shift / 2) / Shift;
        var feats = new float[frames * _o.Bins];
        Parallel.For(0, frames, () => (new float[Padded], new float[Padded], new float[FrameLength]), (t, _, buf) =>
        {
            var (re, im, frame) = buf;
            long start = _o.SnipEdges ? (long)t * Shift : (long)t * Shift + Shift / 2 - FrameLength / 2;
            double mean = 0;
            for (int s = 0; s < FrameLength; s++)
            {
                long i = start + s;
                while (i < 0 || i >= n) i = i < 0 ? -i - 1 : 2L * n - 1 - i;
                frame[s] = samples[i] * _o.Scale;
                mean += frame[s];
            }
            mean /= FrameLength;
            for (int s = 0; s < FrameLength; s++) frame[s] -= (float)mean;
            for (int s = FrameLength - 1; s > 0; s--) frame[s] -= 0.97f * frame[s - 1];
            frame[0] -= 0.97f * frame[0];

            Array.Clear(re); Array.Clear(im);
            for (int s = 0; s < FrameLength; s++) re[_rev[s]] = frame[s] * _window[s];
            Fft(re, im);

            for (int b = 0; b < _o.Bins; b++)
            {
                var (first, w) = _banks[b];
                double e = 0;
                for (int k = 0; k < w.Length; k++)
                {
                    int i = first + k;
                    e += w[k] * (re[i] * (double)re[i] + im[i] * (double)im[i]);
                }
                feats[t * _o.Bins + b] = (float)Math.Log(Math.Max(e, 1.1920929e-07));   // FLT_EPSILON floor, as Kaldi
            }
            return buf;
        }, _ => { });

        if (_o.SubtractMean && frames > 0)
            for (int b = 0; b < _o.Bins; b++)
            {
                double m = 0;
                for (int t = 0; t < frames; t++) m += feats[t * _o.Bins + b];
                m /= frames;
                for (int t = 0; t < frames; t++) feats[t * _o.Bins + b] -= (float)m;
            }
        return (feats, frames);
    }

    /// <summary>In-place radix-2 FFT; input already in bit-reversed order.</summary>
    private void Fft(float[] re, float[] im)
    {
        for (int size = 2; size <= Padded; size <<= 1)
        {
            int half = size / 2, step = Padded / size;
            for (int i = 0; i < Padded; i += size)
                for (int j = 0; j < half; j++)
                {
                    float wr = _cos[j * step], wi = _sin[j * step];
                    int a = i + j, b = a + half;
                    float tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
        }
    }
}
