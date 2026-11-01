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

    public AudioEngine(YetiDevice device, EffectChain effects)
    {
        _device = device;
        Effects = effects;
    }

    public EffectChain Effects { get; }

    public event EventHandler<string>? Error;

    /// <summary>Peak after Voice FX and mods, 0..1.</summary>
    public float OutputPeak => Volatile.Read(ref _outputPeak);

    public bool IsExclusive => _capture?.IsExclusive == true;
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
        _capture = new LowLatencyCapture(_device.Capture.ID, exclusive);
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
        var format = _capture!.Format;
        _device.ReportPeak(Dsp.Peak(samples));

        if (_work.Length < samples.Length)
            _work = new float[samples.Length * 2];
        var work = _work.AsSpan(0, samples.Length);
        samples.CopyTo(work);

        if (Effects.Enabled)
            Run(Effects, work, format);
        foreach (var processor in _processorSnapshot)
        {
            if (processor.Enabled)
                Run(processor, work, format);
        }

        Volatile.Write(ref _outputPeak, Dsp.Peak(work));
        foreach (var output in _outputSnapshot)
        {
            try { output.Write(work); }
            catch (Exception ex) { Error?.Invoke(this, "Output error: " + ex.Message); }
        }
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
