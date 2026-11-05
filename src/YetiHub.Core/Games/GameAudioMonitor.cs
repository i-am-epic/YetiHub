using NAudio.CoreAudioApi;
using NAudio.Wave;
using YetiHub.Core.Effects;

namespace YetiHub.Core.Games;

/// <summary>
/// Listens to what your PC is playing (WASAPI loopback of the default output, read-only) and measures what matters for
/// competitive games: loudness, clipping, and whether low frequencies mask the footstep band (2–4 kHz).
/// It only listens: game audio is never changed or recorded to disk.
/// </summary>
public sealed class GameAudioMonitor : IAnalyzerSource, IDisposable
{
    /// <summary>Analysis bands, chosen for FPS audio: rumble/explosions, body, mids, footsteps, detail.</summary>
    public static readonly (string Name, float Low, float High)[] Bands =
    [
        ("Bass", 20, 150),
        ("Low mids", 150, 500),
        ("Mids", 500, 2000),
        ("Footsteps", 2000, 4500),
        ("Highs", 4500, 16000),
    ];

    private readonly WasapiLoopbackCapture _capture;
    private readonly LoudnessMeter _loudness = new();
    private readonly Biquad[][] _bandFilters;
    private readonly double[] _bandEnergy;
    private readonly double[] _bandSnapshot;
    private readonly Queue<double[]> _history = new();
    private float[] _mono = [];
    private long _frames, _clipped, _windowFrames;
    private float _peak;
    private readonly Lock _lock = new();

    public GameAudioMonitor()
    {
        _capture = new WasapiLoopbackCapture();
        SampleRate = _capture.WaveFormat.SampleRate;
        Channels = _capture.WaveFormat.Channels;
        DeviceName = SafeName();
        _bandFilters = Bands.Select(b => new[] { HighPass(b.Low), LowPass(b.High) }).ToArray();
        _bandEnergy = new double[Bands.Length];
        _bandSnapshot = new double[Bands.Length];
        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public string DeviceName { get; }

    public AnalyzerTap InputTap { get; } = new();
    public AnalyzerTap OutputTap => InputTap;

    /// <summary>A snapshot of the last ~3 seconds.</summary>
    public GameAudioStats Stats
    {
        get
        {
            lock (_lock)
            {
                var levels = _bandSnapshot.Select(e => e <= 0 ? -100 : 10 * Math.Log10(e)).ToArray();
                return new GameAudioStats(_loudness.ShortTermLufs, 20 * Math.Log10(Math.Max(_peak, 1e-9)),
                    _frames == 0 ? 0 : 100.0 * _clipped / _frames, levels);
            }
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        // Shared-mode loopback is 32-bit float on all current Windows builds.
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
        if (samples.IsEmpty) return;
        _loudness.Process(samples, SampleRate, Channels);
        InputTap.Write(samples, Channels);

        var frames = samples.Length / Channels;
        if (_mono.Length < frames) _mono = new float[frames * 2];
        float peak = 0;
        long clipped = 0;
        for (var f = 0; f < frames; f++)
        {
            float m = 0;
            for (var c = 0; c < Channels; c++)
            {
                var v = samples[f * Channels + c];
                var a = MathF.Abs(v);
                if (a > peak) peak = a;
                if (a >= 0.999f) clipped++;
                m += v;
            }
            _mono[f] = m / Channels;
        }

        lock (_lock)
        {
            for (var b = 0; b < Bands.Length; b++)
            {
                var (hp, lp) = (_bandFilters[b][0], _bandFilters[b][1]);
                double sum = 0;
                for (var f = 0; f < frames; f++)
                {
                    var y = lp.Process(hp.Process(_mono[f], 0), 0);
                    sum += y * y;
                }
                _bandEnergy[b] += sum;
            }
            _frames += frames;
            _clipped += clipped;
            _windowFrames += frames;
            _peak = MathF.Max(_peak * 0.97f, peak);

            // Every ~3 s, store the window's mean power per band. Silent windows (menus, loading) are skipped, and the
            // published levels average the last 5 windows with sound (~15 s), so advice stays stable through a round.
            if (_windowFrames >= SampleRate * 3)
            {
                var window = new double[Bands.Length];
                for (var b = 0; b < Bands.Length; b++)
                {
                    window[b] = _bandEnergy[b] / _windowFrames;
                    _bandEnergy[b] = 0;
                }
                if (window.Sum() > 1e-6) // about -60 dB of total band power
                {
                    _history.Enqueue(window);
                    while (_history.Count > 5) _history.Dequeue();
                    for (var b = 0; b < Bands.Length; b++)
                        _bandSnapshot[b] = _history.Average(w => w[b]);
                }
                _windowFrames = 0;
                _frames = _clipped = 0;
            }
        }
    }

    private Biquad HighPass(float hz) { var b = new Biquad(); b.EnsureChannels(1); b.SetHighPass(SampleRate, hz); return b; }
    private Biquad LowPass(float hz) { var b = new Biquad(); b.EnsureChannels(1); b.SetLowPass(SampleRate, hz); return b; }

    private static string SafeName()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d.FriendlyName;
        }
        catch { return "default output"; }
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        try { _capture.StopRecording(); } catch { /* device gone */ }
        _capture.Dispose();
    }
}

/// <param name="Lufs">Short-term loudness of the game mix.</param>
/// <param name="PeakDb">Recent peak level, dBFS.</param>
/// <param name="ClippingPercent">Share of samples at full scale in the last window.</param>
/// <param name="BandDb">Mean power per <see cref="GameAudioMonitor.Bands"/> entry, dB.</param>
public sealed record GameAudioStats(double Lufs, double PeakDb, double ClippingPercent, double[] BandDb)
{
    public bool IsPlaying => Lufs > -50;

    /// <summary>Plain-language findings for competitive play, most important first.</summary>
    public IReadOnlyList<GameAudioInsight> Insights()
    {
        var list = new List<GameAudioInsight>();
        if (!IsPlaying)
        {
            list.Add(new("info", "Waiting for game audio", "Play a match or a deathmatch. Analysis starts when sound is playing."));
            return list;
        }
        if (ClippingPercent > 0.05)
            list.Add(new("bad", "Game audio is clipping",
                $"{ClippingPercent:0.0}% of samples hit full scale. Lower the in-game master volume to about 80% and turn the Windows volume up instead, so loud fights stop distorting footsteps."));
        if (Lufs > -12)
            list.Add(new("warn", "Very loud mix",
                $"The game mix is about {Lufs:0} LUFS. Long sessions this loud cause listening fatigue; lower the Windows volume a few steps."));

        var bass = BandDb[0];
        var steps = BandDb[3];
        var gap = bass - steps;
        if (gap > 18)
            list.Add(new("warn", "Bass is masking footsteps",
                $"Low frequencies are {gap:0} dB louder than the footstep band (2–4.5 kHz). Turn game music off, disable headset bass boost / \"loudness\" effects, and keep Valorant on Stereo with HRTF on."));
        else if (gap < 0)
            list.Add(new("good", "Footsteps stand out",
                $"The footstep band is {-gap:0} dB louder than the bass, so steps cut through clearly."));
        else if (gap < 8)
            list.Add(new("good", "Footsteps are clear",
                $"The footstep band is only {gap:0} dB below the bass, so steps cut through well."));
        else
            list.Add(new("good", "Balanced mix", $"Bass is {gap:0} dB above the footstep band: a normal, healthy balance."));

        if (Lufs < -30)
            list.Add(new("info", "Quiet mix",
                $"The game is playing at about {Lufs:0} LUFS (menus and quiet rounds look like this). If you have to max out your headset to hear steps, raise Valorant's sound-effects volume rather than the Windows volume."));
        if (BandDb[4] < steps - 20)
            list.Add(new("info", "Dull highs",
                "Very little energy above 4.5 kHz. If your headset has an EQ, a small (+2 dB) lift around 5–6 kHz helps you place sounds."));
        return list;
    }
}

/// <param name="Level">"good", "warn", "bad" or "info".</param>
public sealed record GameAudioInsight(string Level, string Title, string Detail);
