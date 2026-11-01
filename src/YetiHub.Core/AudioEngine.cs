using YetiHub.Core.Effects;
using YetiHub.Core.LowLatency;
using YetiHub.Mods;

namespace YetiHub.Core;

public enum OutputRole
{
    /// <summary>Speakers/headphones so you can hear the processed voice.</summary>
    Monitor,

    /// <summary>A virtual cable input (VB-CABLE, Voicemeeter) that Discord/OBS record from.</summary>
    VirtualMic,
}

/// <summary>
/// Captures the Yeti, runs Voice FX and mod processors, and writes the result straight into up to two outputs,
/// all on one Pro Audio thread. See <see cref="LowLatencyRender"/> for how the output delay is kept minimal.
/// The Yeti's own headphone jack always carries the raw signal with zero latency; that is hardware.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly YetiDevice _device;
    private readonly List<IAudioProcessor> _processors = [];
    private readonly Lock _lock = new();
    private IAudioProcessor[] _processorSnapshot = [];
    private readonly Dictionary<OutputRole, LowLatencyRender> _outputs = [];
    private LowLatencyRender[] _outputSnapshot = [];

    private LowLatencyCapture? _capture;
    private float[] _work = [];
    private float _outputPeak;
    private double _dspLoad;

    /// <summary>Last ~85 ms of the raw mic and processed output (mono), for the live spectrum/spectrogram.</summary>
    public AnalyzerTap InputTap { get; } = new();
    public AnalyzerTap OutputTap { get; } = new();

    public AudioEngine(YetiDevice device, EffectChain effects)
    {
        _device = device;
        Effects = effects;
        // Read now, on the creating thread: the NAudio device object belongs to that thread's COM apartment,
        // and Start() may run on a worker thread.
        _captureId = device.Capture.ID;
    }

    private readonly string _captureId;

    public EffectChain Effects { get; }

    public event EventHandler<string>? Error;

    /// <summary>Peak after Voice FX and mods, 0..1.</summary>
    public float OutputPeak => Volatile.Read(ref _outputPeak);

    public bool IsExclusive => _capture?.IsExclusive == true;

    private long _zeroFrames;
    private bool _hardwareMuted;

    /// <summary>True while the Yeti's physical mute button is on (detected from exact digital silence).</summary>
    public bool HardwareMuted => Volatile.Read(ref _hardwareMuted);
    public int SampleRate => _capture?.Format.SampleRate ?? 48000;

    /// <summary>Glitch counters and load: everything should read 0 / low for clean audio.</summary>
    public AudioHealth Health
    {
        get
        {
            lock (_lock)
            {
                var monitor = _outputs.GetValueOrDefault(OutputRole.Monitor) ?? _outputs.Values.FirstOrDefault();
                return new AudioHealth(
                    _capture?.Glitches ?? 0,
                    _outputs.Values.Sum(o => o.Underruns),
                    monitor?.DriftPercent ?? 0,
                    monitor?.QueuedMs ?? 0,
                    Volatile.Read(ref _dspLoad) * 100);
            }
        }
    }
    public double CapturePeriodMs => _capture?.PeriodMs ?? 0;
    public string CaptureFormat => _capture?.Format.ToString() ?? "";

    public void AddProcessor(IAudioProcessor processor)
    {
        lock (_lock)
        {
            _processors.Add(processor);
            _processorSnapshot = _processors.ToArray();
        }
    }

    /// <param name="exclusive">Open the Yeti exclusively at its minimum period. Falls back to shared if busy.</param>
    public void Start(bool exclusive = false)
    {
        if (_capture != null)
            return;
        _capture = new LowLatencyCapture(_captureId, exclusive);
        _capture.DataAvailable += OnData;
        _capture.Failed += ex => Error?.Invoke(this, "Capture stopped: " + ex.Message);
        _capture.Start();
    }

    /// <summary>Routes processed audio to the device with <paramref name="deviceId"/>, or stops that route when null.</summary>
    public void SetOutput(OutputRole role, string? deviceId)
    {
        if (_capture is null)
            throw new InvalidOperationException("Start the engine first.");

        LowLatencyRender? old;
        lock (_lock)
        {
            _outputs.Remove(role, out old);
            if (deviceId != null)
                _outputs[role] = new LowLatencyRender(deviceId, _capture.Format);
            _outputSnapshot = _outputs.Values.ToArray();
        }
        // The audio thread may still be inside Write on the old output for one packet.
        if (old != null)
            Task.Delay(100).ContinueWith(_ => old.Dispose());
    }

    /// <summary>
    /// Measured mic-to-speaker delay for a route: capture packet + processing + audio queued in the output + Windows
    /// engine/driver latency. Null when the route is off.
    /// </summary>
    public double? LatencyMs(OutputRole role)
    {
        lock (_lock)
        {
            if (!_outputs.TryGetValue(role, out var o)) return null;
            return CapturePeriodMs + Effects.LatencyMs + o.QueuedMs + o.EngineLatencyMs;
        }
    }

    private void OnData(Span<float> samples)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var format = _capture!.Format;
        var peak = Dsp.Peak(samples);
        _device.ReportPeak(peak);

        // The Yeti's own mute button never notifies Windows (its HID buttons aren't wired), but a muted Yeti sends
        // perfect digital silence, which a real room never does. 300 ms of exact zeros = muted on the mic.
        _zeroFrames = peak == 0 ? _zeroFrames + samples.Length / Math.Max(1, _capture!.Format.Channels) : 0;
        Volatile.Write(ref _hardwareMuted, _zeroFrames > _capture!.Format.SampleRate * 3 / 10);
        InputTap.Write(samples, format.Channels);

        if (_work.Length < samples.Length)
            _work = new float[samples.Length * 2];
        var work = _work.AsSpan(0, samples.Length);
        samples.CopyTo(work);

        if (Effects.Enabled)
            Run(Effects, work, format);
        var anyMod = false;
        foreach (var processor in _processorSnapshot)
        {
            if (!processor.Enabled) continue;
            Run(processor, work, format);
            anyMod = true;
        }
        // Mods run after the Voice FX limiter, so guard the output: transparent below -3 dBFS, soft above.
        if (anyMod) Dsp.SoftLimit(work);

        Volatile.Write(ref _outputPeak, Dsp.Peak(work));
        Effects.Measure(work, format.SampleRate, format.Channels);
        OutputTap.Write(work, format.Channels);
        foreach (var output in _outputSnapshot)
        {
            try { output.Write(work); }
            catch (Exception ex) { Error?.Invoke(this, "Output error: " + ex.Message); }
        }

        // DSP load = processing time / packet duration, smoothed.
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        var packet = samples.Length / (double)format.Channels / format.SampleRate;
        Volatile.Write(ref _dspLoad, _dspLoad + (elapsed / packet - _dspLoad) * 0.05);
    }

    private void Run(IAudioProcessor processor, Span<float> work, StreamFormat format)
    {
        try
        {
            processor.Process(work, format.SampleRate, format.Channels);
        }
        catch (Exception ex)
        {
            processor.Enabled = false;
            Error?.Invoke(this, $"'{processor.Name}' crashed and was disabled: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _capture = null;
        lock (_lock)
        {
            foreach (var o in _outputs.Values) o.Dispose();
            _outputs.Clear();
            _outputSnapshot = [];
        }
    }
}

/// <param name="MicGlitches">Mic packets Windows flagged as lost.</param>
/// <param name="Underruns">Times an output ran dry (audible clicks).</param>
/// <param name="DriftPercent">Current clock-drift correction.</param>
/// <param name="QueuedMs">Audio waiting in the output device.</param>
/// <param name="DspLoadPercent">Share of real time spent on effects.</param>
public sealed record AudioHealth(int MicGlitches, int Underruns, double DriftPercent, double QueuedMs, double DspLoadPercent);

/// <summary>
/// Lock-free single-writer ring buffer of mono samples. The audio thread writes; the UI reads a snapshot.
/// A torn read only affects one animation frame, which is fine for visualisation.
/// </summary>
public sealed class AnalyzerTap
{
    public const int Size = 4096;
    private readonly float[] _ring = new float[Size];
    private int _write;

    public void Write(ReadOnlySpan<float> interleaved, int channels)
    {
        var w = _write;
        for (var i = 0; i + channels <= interleaved.Length; i += channels)
        {
            var m = 0f;
            for (var c = 0; c < channels; c++) m += interleaved[i + c];
            _ring[w] = m / channels;
            w = (w + 1) & (Size - 1);
        }
        Volatile.Write(ref _write, w);
    }

    /// <summary>Copies the newest <c>destination.Length</c> samples, oldest first.</summary>
    public void Read(Span<float> destination)
    {
        var end = Volatile.Read(ref _write);
        var start = (end - destination.Length) & (Size - 1);
        for (var i = 0; i < destination.Length; i++)
            destination[i] = _ring[(start + i) & (Size - 1)];
    }
}
