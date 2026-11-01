namespace YetiHub.Core.Effects;

/// <summary>
/// Real-time pitch shifter: two read heads sweep a short delay line at the shifted rate, cross-faded with a
/// raised-cosine so the jumps are inaudible. Cheap and low latency (about half the grain, ~12 ms).
/// </summary>
internal sealed class GranularShifter
{
    private float[][] _buffers = [];
    private int _write;
    private double _phase; // 0..1 position of head A within the grain
    private int _grain;

    public void Prepare(int sampleRate, int channels)
    {
        _grain = sampleRate * 25 / 1000;
        _buffers = Enumerable.Range(0, channels).Select(_ => new float[_grain * 4]).ToArray();
        _write = 0;
        _phase = 0;
    }

    /// <summary>Processes one interleaved frame in place. ratio 2 = up an octave.</summary>
    public void ProcessFrame(Span<float> frame, double ratio)
    {
        var len = _buffers[0].Length;
        for (var c = 0; c < frame.Length; c++) _buffers[c][_write] = frame[c];

        // Delay shrinks (ratio > 1) or grows (ratio < 1) by (ratio - 1) samples per sample, wrapping each grain.
        _phase += (1 - ratio) / _grain;
        _phase -= Math.Floor(_phase);

        // Not shifting: two blended taps would comb-filter the voice. Drift (inaudibly, ~0.2 % pitch) to a phase
        // where one tap has all the gain.
        if (Math.Abs(ratio - 1) < 0.002)
        {
            var target = _phase < 0.25 ? 0 : _phase < 0.75 ? 0.5 : 1;
            _phase += Math.Clamp(target - _phase, -2e-6, 2e-6);
            _phase -= Math.Floor(_phase);
        }
        var phaseB = (_phase + 0.5) % 1.0;

        var gainA = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * _phase));
        var gainB = 1 - gainA;
        var delayA = 2 + _phase * _grain;
        var delayB = 2 + phaseB * _grain;

        for (var c = 0; c < frame.Length; c++)
            frame[c] = Read(_buffers[c], _write - delayA, len) * gainA + Read(_buffers[c], _write - delayB, len) * gainB;

        _write = (_write + 1) % len;
    }

    private static float Read(float[] buf, double pos, int len)
    {
        pos %= len;
        if (pos < 0) pos += len;
        var i = (int)pos;
        var frac = (float)(pos - i);
        return buf[i] + (buf[(i + 1) % len] - buf[i]) * frac;
    }
}

public sealed class PitchShiftEffect : Effect
{
    private readonly EffectParameter _semitones, _mix;
    private readonly GranularShifter _shifter = new();
    private float[] _frame = [], _dry = [];
    private double _ratio = 1;

    public PitchShiftEffect() : base("pitch", "Pitch Shift", "Make your voice deeper or higher, from monster to chipmunk.")
    {
        _semitones = Param("semitones", "Pitch", -12, 12, -3, "st");
        _mix = Param("mix", "Mix", 0, 100, 100, "%");
    }

    public override string Category => "fun";
    public override double LatencyMs => 12.5;

    protected override void Reset()
    {
        _shifter.Prepare(SampleRate, Channels);
        _frame = new float[Channels];
        _dry = new float[Channels];
    }

    protected override void Update() => _ratio = Math.Pow(2, _semitones.Value / 12.0);

    protected override void ProcessCore(Span<float> samples)
    {
        var wet = _mix.Value / 100f;
        for (var f = 0; f + Channels <= samples.Length; f += Channels)
        {
            var frame = samples.Slice(f, Channels);
            frame.CopyTo(_dry);
            _shifter.ProcessFrame(frame, _ratio);
            for (var c = 0; c < Channels; c++) frame[c] = _dry[c] + (frame[c] - _dry[c]) * wet;
        }
    }
}

/// <summary>
/// Auto-Tune style pitch correction: YIN pitch detection on a 12 kHz copy of the voice, snap to the nearest note of
/// the chosen key/scale, then shift. Retune speed 0 = robotic "hard tune", higher = natural correction.
/// </summary>
public sealed class AutoTuneEffect : Effect
{
    private static readonly int[][] Scales =
    [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], // chromatic
        [0, 2, 4, 5, 7, 9, 11],                 // major
        [0, 2, 3, 5, 7, 8, 10],                 // natural minor
        [0, 2, 4, 7, 9],                        // major pentatonic
    ];

    private const int Decimation = 4;
    private const int YinWindow = 384;   // 32 ms at 12 kHz
    private const int YinMaxLag = 200;   // down to 60 Hz
    private const int YinMinLag = 12;    // up to 1 kHz

    private readonly EffectParameter _key, _scale, _speed, _amount;
    private readonly GranularShifter _shifter = new();
    private readonly Biquad _antiAlias = new();
    private readonly float[] _decimated = new float[YinWindow + YinMaxLag];
    private readonly float[] _diff = new float[YinMaxLag + 1];
    private int _decimPos, _decimCounter, _sinceAnalysis;
    private float _decimAcc;
    private double _ratio = 1, _targetRatio = 1, _speedCoef;
    private float[] _dry = [];

    public AutoTuneEffect() : base("autotune", "AutoTune", "Snaps your pitch to a musical key. Hard tune for the robotic effect.")
    {
        _key = Param("key", "Key", 0, 11, 0, "key");
        _scale = Param("scale", "Scale", 0, 3, 0, "scale");
        _speed = Param("speed", "Retune speed", 0, 200, 10, "ms");
        _amount = Param("amount", "Amount", 0, 100, 100, "%");
    }

    public override string Category => "fun";
    public override double LatencyMs => 12.5;

    /// <summary>Last detected pitch in Hz (0 when unvoiced). For the UI.</summary>
    public double DetectedHz { get; private set; }

    protected override void Reset()
    {
        _shifter.Prepare(SampleRate, Channels);
        _antiAlias.EnsureChannels(1);
        _antiAlias.Reset();
        _dry = new float[Channels];
        _ratio = _targetRatio = 1;
    }

    protected override void Update()
    {
        // Low-pass before decimating to 12 kHz; 2nd order at 4 kHz is plenty for pitch detection.
        _antiAlias.SetLowPass(SampleRate, 4000);
        _speedCoef = _speed.Value <= 0 ? 1 : 1 - Math.Exp(-1.0 / (_speed.Value * 0.001 * SampleRate));
    }

    protected override void ProcessCore(Span<float> samples)
    {
        var amount = _amount.Value / 100f;
        for (var f = 0; f + Channels <= samples.Length; f += Channels)
        {
            var frame = samples.Slice(f, Channels);
            var mono = 0f;
            for (var c = 0; c < Channels; c++) mono += frame[c];
            _decimAcc += _antiAlias.Process(mono / Channels, 0);
            if (++_decimCounter == Decimation)
            {
                Push(_decimAcc / Decimation);
                _decimAcc = 0;
                _decimCounter = 0;
                if (++_sinceAnalysis >= 64) // re-detect every ~5 ms
                {
                    _sinceAnalysis = 0;
                    Analyze();
                }
            }

            // Amount scales the correction itself (not a wet/dry mix, which would comb-filter).
            var wanted = 1 + (_targetRatio - 1) * amount;
            _ratio += (wanted - _ratio) * _speedCoef;
            _shifter.ProcessFrame(frame, _ratio);
        }
    }

    private void Push(float v)
    {
        _decimated[_decimPos] = v;
        _decimPos = (_decimPos + 1) % _decimated.Length;
    }

    private void Analyze()
    {
        // Unroll the ring buffer view: x[i] = sample i of the last (window + maxLag) samples.
        var n = _decimated.Length;
        float X(int i) => _decimated[(_decimPos + i) % n];

        // Silence / unvoiced: leave pitch alone.
        float energy = 0;
        for (var i = 0; i < YinWindow; i++) { var v = X(i); energy += v * v; }
        if (energy / YinWindow < 1e-6f) { DetectedHz = 0; _targetRatio = 1; return; }

        // YIN: difference function + cumulative mean normalisation, first dip under threshold.
        _diff[0] = 1;
        float running = 0;
        var tau = -1;
        for (var lag = 1; lag <= YinMaxLag; lag++)
        {
            float d = 0;
            for (var i = 0; i < YinWindow; i++)
            {
                var delta = X(i) - X(i + lag);
                d += delta * delta;
            }
            running += d;
            _diff[lag] = running > 0 ? d * lag / running : 1;
            if (tau < 0 && lag > YinMinLag && _diff[lag] < 0.15f)
                tau = lag;
            if (tau > 0 && lag > tau + 1 && _diff[lag] > _diff[lag - 1])
                break;
        }
        if (tau < 0) { DetectedHz = 0; _targetRatio = 1; return; }
        while (tau + 1 <= YinMaxLag && _diff[tau + 1] < _diff[tau]) tau++;

        // Parabolic interpolation around the minimum for sub-sample accuracy.
        double betterTau = tau;
        if (tau > 1 && tau < YinMaxLag)
        {
            double s0 = _diff[tau - 1], s1 = _diff[tau], s2 = _diff[tau + 1];
            var denom = 2 * s1 - s2 - s0;
            if (Math.Abs(denom) > 1e-9) betterTau = tau + (s2 - s0) / (2 * denom);
        }

        var hz = SampleRate / (double)Decimation / betterTau;
        DetectedHz = hz;
        var target = Snap(hz);
        _targetRatio = Math.Clamp(target / hz, 0.7, 1.4);
    }

    private double Snap(double hz)
    {
        var midi = 69 + 12 * Math.Log2(hz / 440);
        var scale = Scales[(int)Math.Round(_scale.Value)];
        var key = (int)Math.Round(_key.Value);
        double best = midi, bestDist = double.MaxValue;
        var octave = Math.Floor(midi / 12) * 12;
        foreach (var o in new[] { octave - 12, octave, octave + 12 })
        foreach (var step in scale)
        {
            var note = o + (step + key) % 12;
            var dist = Math.Abs(note - midi);
            if (dist < bestDist) { bestDist = dist; best = note; }
        }
        return 440 * Math.Pow(2, (best - 69) / 12);
    }
}
