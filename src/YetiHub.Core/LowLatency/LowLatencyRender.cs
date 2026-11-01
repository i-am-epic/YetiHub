using System.Runtime.InteropServices;

namespace YetiHub.Core.LowLatency;

/// <summary>
/// Shared-mode WASAPI output written directly from the capture thread: no queue, no extra thread.
/// Windows converts our format (rate, channels) to the device's (AUTOCONVERTPCM).
///
/// The mic and the output run on different hardware clocks, so the amount of audio waiting in the device slowly
/// drifts. Instead of dropping or inserting chunks (audible cuts), an adaptive resampler stretches the stream by at
/// most ±0.5 % (inaudible) to hold the queue at a small target. Only a real device underrun inserts silence, and
/// then the target grows by 1 ms so it doesn't happen again (self-tuning per device).
/// </summary>
public sealed class LowLatencyRender : IDisposable
{
    /// <summary>Safety margin kept in the device at its emptiest moment.</summary>
    public const double StartTargetMs = 1.5;
    public const double MaxTargetMs = 12;

    private readonly IAudioClient3 _client;
    private readonly IAudioRenderClient _renderClient;
    private readonly int _channels;
    private readonly uint _bufferFrames;
    private readonly float[] _last;
    private float[] _out = new float[4096];
    private bool _started;
    private double _avgPadding, _targetFrames, _position;
    private uint _windowMin = uint.MaxValue, _lastMin;
    private int _windowCount;

    public LowLatencyRender(string deviceId, StreamFormat sourceFormat)
    {
        Format = sourceFormat with { Kind = SampleKind.Float32 };
        _channels = Format.Channels;
        _last = new float[_channels];
        _client = CoreAudio.Activate(deviceId);

        var native = Format.ToNative();
        try
        {
            const long bufferDuration = 40 * 10_000; // capacity only; the controller keeps actual fill small
            CoreAudio.Check(_client.Initialize(CoreAudio.ShareModeShared,
                CoreAudio.StreamFlagsAutoConvertPcm | CoreAudio.StreamFlagsSrcDefaultQuality,
                bufferDuration, 0, native, IntPtr.Zero));
        }
        finally { Marshal.FreeCoTaskMem(native); }

        CoreAudio.Check(_client.GetBufferSize(out _bufferFrames));
        // Some drivers report 0 here; the shared mixer still holds at least one engine period, so count that.
        _client.GetStreamLatency(out var latency);
        _client.GetDevicePeriod(out var enginePeriod, out _);
        EngineLatencyMs = Math.Max(latency, enginePeriod) / 10_000.0;
        _renderClient = CoreAudio.GetService<IAudioRenderClient>(_client);
        _targetFrames = Format.SampleRate * StartTargetMs / 1000;
        _avgPadding = _targetFrames;
        _lastMin = (uint)_targetFrames;
    }

    public StreamFormat Format { get; }

    /// <summary>Windows' own reported stream latency for the device (mixing engine + driver).</summary>
    public double EngineLatencyMs { get; }

    /// <summary>Smoothed amount of audio waiting in the device buffer, in ms.</summary>
    public double QueuedMs => Volatile.Read(ref _avgPadding) * 1000 / Format.SampleRate;

    /// <summary>Times the device ran dry (each one is an audible glitch). Should stay at 0.</summary>
    public int Underruns { get; private set; }

    /// <summary>Current drift correction in percent (positive = stretching).</summary>
    public double DriftPercent { get; private set; }

    /// <summary>Called from the capture thread with interleaved float samples in <see cref="Format"/>.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (_client.GetCurrentPadding(out var padding) < 0)
            return;
        var inFrames = samples.Length / _channels;
        if (inFrames == 0) return;

        if (!_started || padding == 0)
        {
            if (_started)
            {
                // The device starved: that was a glitch. Allow a slightly bigger cushion from now on.
                Underruns++;
                _targetFrames = Math.Min(_targetFrames + Format.SampleRate / 1000.0, Format.SampleRate * MaxTargetMs / 1000);
            }
            WriteSilence((uint)_targetFrames);
            padding += (uint)_targetFrames;
            _avgPadding = padding;
        }
        _avgPadding += (padding - _avgPadding) * 0.02;

        // Control the *minimum* queue seen over ~0.5 s (the instant just before Windows pulls the next engine
        // period), not the average: part of the average is the engine's own period, which we can't remove.
        _windowMin = Math.Min(_windowMin, padding);
        if (++_windowCount >= 50)
        {
            _lastMin = _windowMin;
            _windowMin = uint.MaxValue;
            _windowCount = 0;
        }

        // Proportional drift control: ratio = output frames per input frame.
        var error = _targetFrames - _lastMin;
        var ratio = 1 + Math.Clamp(error * 3e-5, -0.005, 0.005);
        DriftPercent = (ratio - 1) * 100;

        // Way over (e.g. after the system stalled): resampling alone would take too long, so skip ahead once.
        if (_lastMin > _targetFrames + Format.SampleRate * 0.03)
        {
            _lastMin = (uint)_targetFrames;
            if (!_started) Start();
            return;
        }

        var produced = Resample(samples, inFrames, 1 / ratio);
        var frames = (int)Math.Min(produced, _bufferFrames - padding);
        if (frames > 0) WriteFrames(_out.AsSpan(0, frames * _channels), (uint)frames);
        if (!_started) Start();
    }

    /// <summary>Linear-interpolation resampler with one frame of history; writes into _out, returns frames.</summary>
    private int Resample(ReadOnlySpan<float> input, int inFrames, double step)
    {
        var maxOut = (int)(inFrames / step) + 2;
        if (_out.Length < maxOut * _channels) _out = new float[maxOut * _channels * 2];

        var produced = 0;
        // Position is relative to input frame 0; position -1 refers to the last frame of the previous packet.
        while (_position < inFrames - 1)
        {
            var i = (int)Math.Floor(_position);
            var frac = (float)(_position - i);
            for (var c = 0; c < _channels; c++)
            {
                var a = i < 0 ? _last[c] : input[i * _channels + c];
                var b = input[(i + 1) * _channels + c];
                _out[produced * _channels + c] = a + (b - a) * frac;
            }
            produced++;
            _position += step;
        }
        _position -= inFrames;
        for (var c = 0; c < _channels; c++) _last[c] = input[(inFrames - 1) * _channels + c];
        return produced;
    }

    private void Start()
    {
        _started = true;
        _client.Start();
    }

    private void WriteSilence(uint frames)
    {
        if (frames == 0 || _renderClient.GetBuffer(frames, out _) < 0) return;
        _renderClient.ReleaseBuffer(frames, CoreAudio.BufferFlagsSilent);
    }

    private void WriteFrames(ReadOnlySpan<float> samples, uint frames)
    {
        if (_renderClient.GetBuffer(frames, out var data) < 0)
            return;
        unsafe
        {
            samples.CopyTo(new Span<float>((void*)data, samples.Length));
        }
        _renderClient.ReleaseBuffer(frames, 0);
    }

    public void Dispose()
    {
        _client.Stop();
        CoreAudio.Release(_renderClient);
        CoreAudio.Release(_client);
    }
}
