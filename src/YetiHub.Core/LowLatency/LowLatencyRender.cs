using System.Runtime.InteropServices;

namespace YetiHub.Core.LowLatency;

/// <summary>
/// Shared-mode WASAPI output written directly from the capture thread: no queue, no extra thread.
/// Windows converts our format (rate, channels) to the device's (AUTOCONVERTPCM).
///
/// Latency control: before each write we look at how much audio is still queued in the device buffer.
/// We keep that cushion between <see cref="MinCushionMs"/> and <see cref="MaxCushionMs"/> by inserting a little
/// silence (startup / underrun) or dropping incoming frames (clock drift), so delay can never accumulate.
/// </summary>
public sealed class LowLatencyRender : IDisposable
{
    public const double MinCushionMs = 2;
    public const double MaxCushionMs = 8;

    private readonly IAudioClient3 _client;
    private readonly IAudioRenderClient _renderClient;
    private readonly int _channels;
    private readonly uint _bufferFrames;
    private readonly uint _minCushion, _maxCushion;
    private bool _started;
    private double _avgPaddingFrames;

    public LowLatencyRender(string deviceId, StreamFormat sourceFormat)
    {
        Format = sourceFormat with { Kind = SampleKind.Float32 };
        _channels = Format.Channels;
        _client = CoreAudio.Activate(deviceId);

        var native = Format.ToNative();
        try
        {
            const long bufferDuration = 40 * 10_000; // capacity only; the cushion logic keeps actual fill small
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
        _minCushion = (uint)(Format.SampleRate * MinCushionMs / 1000);
        _maxCushion = (uint)(Format.SampleRate * MaxCushionMs / 1000);
    }

    public StreamFormat Format { get; }

    /// <summary>Windows' own reported stream latency for the device (mixing engine + driver).</summary>
    public double EngineLatencyMs { get; }

    /// <summary>Smoothed amount of audio waiting in the device buffer, in ms.</summary>
    public double QueuedMs => Volatile.Read(ref _avgPaddingFrames) * 1000 / Format.SampleRate;

    public int DroppedFrames { get; private set; }
    public int InsertedFrames { get; private set; }

    /// <summary>Called from the capture thread with interleaved float samples in <see cref="Format"/>.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (_client.GetCurrentPadding(out var padding) < 0)
            return;
        var frames = (uint)(samples.Length / _channels);

        _avgPaddingFrames += (padding - _avgPaddingFrames) * 0.05;

        if (padding < _minCushion)
        {
            var silence = _minCushion - padding;
            WriteFrames(ReadOnlySpan<float>.Empty, silence);
            InsertedFrames += (int)silence;
            padding += silence;
        }
        else if (padding > _maxCushion)
        {
            // Output clock is slower than the mic clock: skip the start of this packet to pull delay back down.
            var drop = Math.Min(frames, padding - _minCushion);
            samples = samples[(int)(drop * _channels)..];
            frames -= drop;
            DroppedFrames += (int)drop;
        }

        frames = Math.Min(frames, _bufferFrames - padding);
        if (frames > 0)
            WriteFrames(samples[..(int)(frames * _channels)], frames);

        if (!_started)
        {
            _started = true;
            _client.Start();
        }
    }

    private void WriteFrames(ReadOnlySpan<float> samples, uint frames)
    {
        if (_renderClient.GetBuffer(frames, out var data) < 0)
            return;
        if (samples.IsEmpty)
        {
            _renderClient.ReleaseBuffer(frames, CoreAudio.BufferFlagsSilent);
            return;
        }
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
