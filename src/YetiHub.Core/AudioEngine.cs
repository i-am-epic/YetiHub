using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using YetiHub.Core.Effects;
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
/// Captures the Yeti, runs Voice FX and mod processors, and plays the result to up to two outputs.
/// Tuned for latency: 10 ms capture packets, 20 ms output buffers, and a drift controller that stops the
/// playback queue growing when the mic and output clocks disagree.
/// The Yeti's own headphone jack always carries the raw signal with zero latency; that is hardware.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int CaptureMs = 10;
    public const int OutputMs = 20;
    public const int QueueTargetMs = 10;

    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly YetiDevice _device;
    private readonly List<IAudioProcessor> _processors = [];
    private readonly Lock _processorsLock = new();
    private IAudioProcessor[] _processorSnapshot = [];
    private readonly Dictionary<OutputRole, OutputSink> _sinks = [];
    private OutputSink[] _sinkSnapshot = [];

    private WasapiCapture? _capture;
    private byte[] _scratch = [];
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

    /// <summary>Rough end-to-end delay of the processed signal, excluding the output device's own buffering.</summary>
    public static int EstimatedLatencyMs => CaptureMs + QueueTargetMs + OutputMs;

    public void AddProcessor(IAudioProcessor processor)
    {
        lock (_processorsLock)
        {
            _processors.Add(processor);
            _processorSnapshot = _processors.ToArray();
        }
    }

    public void Start()
    {
        if (_capture != null)
            return;

        _capture = new WasapiCapture(_device.Capture, useEventSync: true, audioBufferMillisecondsLength: CaptureMs);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, e) => { if (e.Exception != null) Error?.Invoke(this, e.Exception.Message); };
        _capture.StartRecording();
    }

    /// <summary>Routes processed audio to <paramref name="output"/>, or stops that route when null.</summary>
    public void SetOutput(OutputRole role, MMDevice? output)
    {
        Start();
        if (_sinks.Remove(role, out var old))
        {
            _sinkSnapshot = _sinks.Values.ToArray();
            old.Dispose();
        }
        if (output is null || _capture is null)
            return;

        _sinks[role] = new OutputSink(output, _capture.WaveFormat);
        _sinkSnapshot = _sinks.Values.ToArray();
    }

    public bool IsRouted(OutputRole role) => _sinks.ContainsKey(role);

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _capture!.WaveFormat;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);
        var sinks = _sinkSnapshot;

        if (!isFloat || format.BitsPerSample != 32)
        {
            // Shared-mode capture is float on every Windows build we know of; pass anything else through untouched.
            foreach (var sink in sinks) sink.Write(e.Buffer, e.BytesRecorded);
            return;
        }

        var raw = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
        _device.ReportPeak(Dsp.Peak(raw));

        if (_scratch.Length < e.BytesRecorded)
            _scratch = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, _scratch, 0, e.BytesRecorded);
        var work = MemoryMarshal.Cast<byte, float>(_scratch.AsSpan(0, e.BytesRecorded));

        if (Effects.Enabled)
            Run(Effects, work, format);
        foreach (var processor in _processorSnapshot)
        {
            if (processor.Enabled)
                Run(processor, work, format);
        }

        Volatile.Write(ref _outputPeak, Dsp.Peak(work));
        foreach (var sink in sinks)
            sink.Write(_scratch, e.BytesRecorded);
    }

    private void Run(IAudioProcessor processor, Span<float> work, WaveFormat format)
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
        foreach (var sink in _sinks.Values)
            sink.Dispose();
        _sinks.Clear();
        _sinkSnapshot = [];

        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.StopRecording();
            _capture.Dispose();
            _capture = null;
        }
    }

    private sealed class OutputSink : IDisposable
    {
        private readonly BufferedWaveProvider _queue;
        private readonly WasapiOut _out;
        private readonly int _targetBytes;
        private readonly int _maxBytes;
        private readonly byte[] _discard;

        public OutputSink(MMDevice device, WaveFormat format)
        {
            _queue = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromMilliseconds(250),
                DiscardOnBufferOverflow = true,
            };
            _targetBytes = Align(format.AverageBytesPerSecond * QueueTargetMs / 1000, format.BlockAlign);
            _maxBytes = Align(format.AverageBytesPerSecond * (QueueTargetMs + 2 * CaptureMs) / 1000, format.BlockAlign);
            _discard = new byte[format.AverageBytesPerSecond / 4];

            _out = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: OutputMs);
            _out.Init(_queue);
            _out.Play();
        }

        public void Write(byte[] data, int count)
        {
            _queue.AddSamples(data, 0, count);

            // Drift control: if playback falls behind capture, drop the oldest audio back down to the target
            // instead of letting delay pile up.
            var excess = _queue.BufferedBytes - _maxBytes;
            if (excess > 0)
            {
                var drop = Align(_queue.BufferedBytes - _targetBytes, _queue.WaveFormat.BlockAlign);
                _queue.Read(_discard, 0, Math.Min(drop, _discard.Length));
            }
        }

        private static int Align(int bytes, int blockAlign) => bytes / blockAlign * blockAlign;

        public void Dispose()
        {
            _out.Stop();
            _out.Dispose();
        }
    }
}
