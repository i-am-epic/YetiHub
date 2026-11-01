using System.Runtime.InteropServices;

namespace YetiHub.Core.LowLatency;

public delegate void SamplesHandler(Span<float> samples);

/// <summary>
/// Event-driven WASAPI capture on a Pro Audio (MMCSS) thread, delivering interleaved float samples.
/// Shared mode uses the device's smallest engine period and raw mode; exclusive mode opens the device at its
/// minimum hardware period (3 ms on the Yeti) but blocks other apps from using the mic directly.
/// </summary>
public sealed class LowLatencyCapture : IDisposable
{
    private readonly IAudioClient3 _client;
    private readonly IAudioCaptureClient _captureClient;
    private readonly IntPtr _event;
    private Thread? _thread;
    private volatile bool _running;
    private float[] _buffer = new float[4096];

    public LowLatencyCapture(string deviceId, bool exclusive)
    {
        (_client, Format, PeriodMs, IsExclusive) = exclusive
            ? OpenExclusive(deviceId) ?? OpenShared(deviceId)
            : OpenShared(deviceId);

        _event = CoreAudio.CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
        CoreAudio.Check(_client.SetEventHandle(_event));
        _captureClient = CoreAudio.GetService<IAudioCaptureClient>(_client);
    }

    /// <summary>Raised on the audio thread with the native channel count and rate of <see cref="Format"/>.</summary>
    public event SamplesHandler? DataAvailable;
    public event Action<Exception>? Failed;

    public StreamFormat Format { get; }
    public double PeriodMs { get; }
    public bool IsExclusive { get; }

    private static (IAudioClient3, StreamFormat, double, bool) OpenShared(string deviceId)
    {
        foreach (var raw in new[] { true, false })
        {
            var client = CoreAudio.Activate(deviceId);
            try
            {
                if (raw) CoreAudio.TrySetRaw(client);
                CoreAudio.Check(client.GetMixFormat(out var mix));
                try
                {
                    var format = StreamFormat.FromNative(mix);
                    // IAudioClient3 lets us ask for the engine's minimum period; fall back to classic init.
                    var hr = client.GetSharedModeEnginePeriod(mix, out _, out _, out var minFrames, out _);
                    if (hr >= 0 && client.InitializeSharedAudioStream(CoreAudio.StreamFlagsEventCallback, minFrames, mix, IntPtr.Zero) >= 0)
                        return (client, format, minFrames * 1000.0 / format.SampleRate, false);

                    CoreAudio.Release(client);
                    client = CoreAudio.Activate(deviceId);
                    if (raw) CoreAudio.TrySetRaw(client);
                    CoreAudio.Check(client.Initialize(CoreAudio.ShareModeShared, CoreAudio.StreamFlagsEventCallback, 0, 0, mix, IntPtr.Zero));
                    client.GetDevicePeriod(out var period, out _);
                    return (client, format, period / 10_000.0, false);
                }
                finally { Marshal.FreeCoTaskMem(mix); }
            }
            catch when (raw)
            {
                CoreAudio.Release(client); // retry without raw mode
            }
        }
        throw new InvalidOperationException("unreachable");
    }

    private static (IAudioClient3, StreamFormat, double, bool)? OpenExclusive(string deviceId)
    {
        var probe = CoreAudio.Activate(deviceId);
        StreamFormat mixFormat;
        long minPeriod;
        try
        {
            CoreAudio.Check(probe.GetMixFormat(out var mix));
            mixFormat = StreamFormat.FromNative(mix);
            Marshal.FreeCoTaskMem(mix);
            CoreAudio.Check(probe.GetDevicePeriod(out _, out minPeriod));
        }
        finally { CoreAudio.Release(probe); }

        foreach (var kind in new[] { SampleKind.Int16, SampleKind.Int24, SampleKind.Int32, SampleKind.Float32 })
        {
            var format = mixFormat with { Kind = kind };
            var native = format.ToNative();
            try
            {
                var period = minPeriod;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var client = CoreAudio.Activate(deviceId);
                    var hr = client.Initialize(CoreAudio.ShareModeExclusive, CoreAudio.StreamFlagsEventCallback, period, period, native, IntPtr.Zero);
                    if (hr >= 0)
                        return (client, format, period / 10_000.0, true);

                    if (hr == CoreAudio.ErrBufferSizeNotAligned && client.GetBufferSize(out var frames) >= 0)
                        period = (long)(10_000_000.0 * frames / format.SampleRate + 0.5);
                    CoreAudio.Release(client);
                    if (hr != CoreAudio.ErrBufferSizeNotAligned) break;
                }
            }
            finally { Marshal.FreeCoTaskMem(native); }
        }
        return null; // device busy or no exclusive format: caller falls back to shared
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "YetiHub capture", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        uint taskIndex = 0;
        var mmcss = CoreAudio.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        try
        {
            CoreAudio.Check(_client.Start());
            while (_running)
            {
                if (CoreAudio.WaitForSingleObject(_event, 200) != 0)
                    continue;
                while (_running && _captureClient.GetNextPacketSize(out var packet) >= 0 && packet > 0)
                {
                    CoreAudio.Check(_captureClient.GetBuffer(out var data, out var frames, out var flags, out _, out _));
                    var count = (int)frames * Format.Channels;
                    if (_buffer.Length < count) _buffer = new float[count * 2];
                    var span = _buffer.AsSpan(0, count);
                    if ((flags & CoreAudio.BufferFlagsSilent) != 0) span.Clear();
                    else Format.ToFloat(data, span);
                    _captureClient.ReleaseBuffer(frames);
                    DataAvailable?.Invoke(span);
                }
            }
        }
        catch (Exception ex)
        {
            _running = false;
            Failed?.Invoke(ex);
        }
        finally
        {
            _client.Stop();
            if (mmcss != IntPtr.Zero) CoreAudio.AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        CoreAudio.Release(_captureClient);
        CoreAudio.Release(_client);
        CoreAudio.CloseHandle(_event);
    }
}
