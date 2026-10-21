using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperDesk.Services;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsLoopback)
{
    public string Display => IsLoopback ? $"🔊  {Name}  (what's playing)" : $"🎤  {Name}";
    public override string ToString() => Display;
}

/// <summary>
/// Captures a microphone / line-in / virtual cable (e.g. Voicemeeter Out B1) or a playback device's loopback
/// through WASAPI and delivers 16 kHz mono float samples.
/// </summary>
public sealed class LiveAudioSource : IDisposable
{
    private IWaveIn? _capture;
    private Resampler? _resampler;

    public event Action<float[]>? SamplesAvailable;
    /// <summary>Input level 0–1 (peak of each captured block), for a level meter.</summary>
    public event Action<float>? LevelChanged;
    public event Action<Exception?>? Stopped;
    /// <summary>Raised (once per start) if processing captured audio failed; capture keeps running.</summary>
    public event Action<Exception>? Error;
    private int _errors;

    public static List<AudioDeviceInfo> ListDevices()
    {
        var list = new List<AudioDeviceInfo>();
        using var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, false));
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, true));
        return list;
    }

    public void Start(AudioDeviceInfo device)
    {
        Stop();
        _errors = 0;
        using var en = new MMDeviceEnumerator();
        var mm = en.GetDevice(device.Id);
        _capture = device.IsLoopback ? new WasapiLoopbackCapture(mm) : new WasapiCapture(mm);
        _resampler = new Resampler(_capture.WaveFormat, AudioDecoder.SampleRate);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        // Runs on the audio driver's thread: an exception here would end the process, so nothing may escape.
        try
        {
            if (_resampler == null || e.BytesRecorded == 0) return;
            var mono = _resampler.Process(e.Buffer, e.BytesRecorded);
            if (mono.Length == 0) return;

            float peak = 0;
            foreach (var s in mono) peak = Math.Max(peak, Math.Abs(s));
            LevelChanged?.Invoke(peak);
            SamplesAvailable?.Invoke(mono);
        }
        catch (Exception ex)
        {
            if (_errors++ == 0) { WhisperDesk.App.Log("Audio capture", ex); Error?.Invoke(ex); }
        }
    }

    public void Stop()
    {
        if (_capture == null) return;
        try { _capture.StopRecording(); } catch { }
        _capture.DataAvailable -= OnData;
        _capture.Dispose();
        _capture = null;
    }

    public void Dispose() => Stop();

    /// <summary>Converts any PCM16/PCM24/PCM32/IEEE-float multi-channel stream to mono at the target rate.</summary>
    private sealed class Resampler
    {
        private readonly WaveFormat _fmt;
        private readonly double _step;
        private double _pos;          // fractional read position into the mono input stream
        private float _prev;          // last input sample (for interpolation across blocks)

        public Resampler(WaveFormat format, int targetRate)
        {
            _fmt = format;
            _step = (double)format.SampleRate / targetRate;
        }

        public float[] Process(byte[] buffer, int bytes)
        {
            int channels = _fmt.Channels;
            int bytesPerSample = _fmt.BitsPerSample / 8;
            int frames = bytes / (bytesPerSample * channels);
            if (frames == 0) return Array.Empty<float>();

            // 1. Downmix to mono
            var mono = new float[frames];
            bool isFloat = _fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (_fmt is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int o = (f * channels + c) * bytesPerSample;
                    sum += bytesPerSample switch
                    {
                        4 when isFloat => BitConverter.ToSingle(buffer, o),
                        4 => BitConverter.ToInt32(buffer, o) / 2147483648f,
                        3 => ((buffer[o] << 8 | buffer[o + 1] << 16 | buffer[o + 2] << 24) >> 8) / 8388608f,
                        2 => BitConverter.ToInt16(buffer, o) / 32768f,
                        _ => 0,
                    };
                }
                mono[f] = sum / channels;
            }

            // 2. Resample: average over the step window (simple low-pass) with linear interpolation.
            var output = new List<float>((int)(frames / _step) + 2);
            while (_pos < frames)
            {
                int i = (int)_pos;
                double frac = _pos - i;
                float a = i - 1 >= 0 ? mono[i - 1] : _prev;
                float b = mono[i];
                float sample = (float)(a + (b - a) * frac);
                if (_step >= 2) // average neighbours to reduce aliasing when downsampling a lot
                {
                    int span = (int)_step, count = 1;
                    for (int k = 1; k < span && i - k >= 0; k++, count++) sample += mono[i - k];
                    sample /= count;
                }
                output.Add(sample);
                _pos += _step;
            }
            _pos -= frames;
            _prev = mono[frames - 1];
            return output.ToArray();
        }
    }
}
