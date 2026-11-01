namespace YetiHub.Core.Effects;

public sealed class LowCutEffect : Effect
{
    private readonly Biquad _filter = new();
    private readonly EffectParameter _frequency;

    public LowCutEffect() : base("lowcut", "Low Cut", "Removes rumble, desk bumps and plosive thumps below the cutoff.")
    {
        _frequency = Param("frequency", "Cutoff", 20, 300, 80, "Hz");
    }

    protected override void Reset() { _filter.EnsureChannels(Channels); _filter.Reset(); }
    protected override void Update() => _filter.SetHighPass(SampleRate, _frequency.Value);

    protected override void ProcessCore(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
            samples[i] = _filter.Process(samples[i], i % Channels);
    }
}

/// <summary>Downward expander: turns background noise down (not off) when you aren't talking.</summary>
public sealed class NoiseGateEffect : Effect
{
    private readonly EffectParameter _threshold, _range, _release;
    private float _gain = 1, _threshLin, _floor, _attackCoef, _releaseCoef;
    private int _holdSamples, _holdLeft;

    public NoiseGateEffect() : base("gate", "Noise Gate", "Quiets keyboard, fans and room noise between words.")
    {
        _threshold = Param("threshold", "Threshold", -80, -10, -50, "dB");
        _range = Param("range", "Reduction", -80, 0, -40, "dB");
        _release = Param("release", "Release", 20, 500, 150, "ms");
    }

    protected override void Update()
    {
        _threshLin = DbToGain(_threshold.Value);
        _floor = DbToGain(_range.Value);
        _attackCoef = Coefficient(1);
        _releaseCoef = Coefficient(_release.Value);
        _holdSamples = SampleRate / 20; // 50 ms hold stops the gate chattering on word endings
    }

    protected override void ProcessCore(Span<float> samples)
    {
        for (var f = 0; f + Channels <= samples.Length; f += Channels)
        {
            var level = 0f;
            for (var c = 0; c < Channels; c++)
                level = MathF.Max(level, MathF.Abs(samples[f + c]));

            float target;
            if (level >= _threshLin) { target = 1; _holdLeft = _holdSamples; }
            else if (_holdLeft > 0) { target = 1; _holdLeft--; }
            else target = _floor;

            _gain += (target - _gain) * (target > _gain ? _attackCoef : _releaseCoef);
            for (var c = 0; c < Channels; c++)
                samples[f + c] *= _gain;
        }
    }
}

public sealed class VoiceEqEffect : Effect
{
    private readonly Biquad _bass = new(), _body = new(), _presence = new(), _air = new();
    private readonly EffectParameter _bassDb, _bodyDb, _presenceDb, _airDb;

    public VoiceEqEffect() : base("eq", "Voice EQ", "Shapes the tone: warmth, mud, clarity and sparkle.")
    {
        _bassDb = Param("bass", "Bass · 100 Hz", -12, 12, 0, "dB");
        _bodyDb = Param("body", "Mud · 300 Hz", -12, 12, 0, "dB");
        _presenceDb = Param("presence", "Presence · 3.5 kHz", -12, 12, 0, "dB");
        _airDb = Param("air", "Air · 10 kHz", -12, 12, 0, "dB");
    }

    protected override void Reset()
    {
        foreach (var b in new[] { _bass, _body, _presence, _air }) { b.EnsureChannels(Channels); b.Reset(); }
    }

    protected override void Update()
    {
        _bass.SetLowShelf(SampleRate, 100, _bassDb.Value);
        _body.SetPeaking(SampleRate, 300, 1.0f, _bodyDb.Value);
        _presence.SetPeaking(SampleRate, 3500, 0.9f, _presenceDb.Value);
        _air.SetHighShelf(SampleRate, 10000, _airDb.Value);
    }

    protected override void ProcessCore(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var c = i % Channels;
            var x = _bass.Process(samples[i], c);
            x = _body.Process(x, c);
            x = _presence.Process(x, c);
            samples[i] = _air.Process(x, c);
        }
    }
}

/// <summary>Wideband de-esser: ducks the signal when the sibilance band gets loud.</summary>
public sealed class DeEsserEffect : Effect
{
    private readonly Biquad _detector = new();
    private readonly EffectParameter _threshold, _frequency, _amount;
    private float _env, _gain = 1, _thresh, _maxReduction, _envCoef, _relCoef;

    public DeEsserEffect() : base("deesser", "De-Esser", "Tames harsh \"s\" and \"t\" sounds.")
    {
        _threshold = Param("threshold", "Threshold", -50, 0, -30, "dB");
        _frequency = Param("frequency", "Frequency", 4000, 10000, 6500, "Hz");
        _amount = Param("amount", "Max reduction", 0, 20, 8, "dB");
    }

    protected override void Reset() { _detector.EnsureChannels(Channels); _detector.Reset(); _env = 0; _gain = 1; }

    protected override void Update()
    {
        _detector.SetBandPass(SampleRate, _frequency.Value, 1.5f);
        _thresh = DbToGain(_threshold.Value);
        _maxReduction = DbToGain(-_amount.Value);
        _envCoef = Coefficient(1);
        _relCoef = Coefficient(60);
    }

    protected override void ProcessCore(Span<float> samples)
    {
        for (var f = 0; f + Channels <= samples.Length; f += Channels)
        {
            var band = 0f;
            for (var c = 0; c < Channels; c++)
                band = MathF.Max(band, MathF.Abs(_detector.Process(samples[f + c], c)));

            _env += (band - _env) * (band > _env ? _envCoef : _relCoef);
            var target = _env > _thresh ? Math.Max(_thresh / _env, _maxReduction) : 1f;
            _gain += (target - _gain) * (target < _gain ? _envCoef : _relCoef);

            for (var c = 0; c < Channels; c++)
                samples[f + c] *= _gain;
        }
    }
}

public sealed class CompressorEffect : Effect
{
    private readonly EffectParameter _threshold, _ratio, _attack, _release, _makeup;
    private float _envDb = -120, _atkCoef, _relCoef, _makeupGain, _slope;

    public CompressorEffect() : base("compressor", "Compressor", "Evens out loud and quiet parts so your voice sits steady.")
    {
        _threshold = Param("threshold", "Threshold", -60, 0, -20, "dB");
        _ratio = Param("ratio", "Ratio", 1, 20, 3, ":1");
        _attack = Param("attack", "Attack", 1, 100, 5, "ms");
        _release = Param("release", "Release", 10, 1000, 120, "ms");
        _makeup = Param("makeup", "Makeup", 0, 24, 6, "dB");
    }

    /// <summary>Gain reduction of the last processed sample, in dB (0 or negative). For the UI.</summary>
    public float GainReductionDb { get; private set; }

    protected override void Reset() => _envDb = -120;

    protected override void Update()
    {
        _atkCoef = Coefficient(_attack.Value);
        _relCoef = Coefficient(_release.Value);
        _makeupGain = DbToGain(_makeup.Value);
        _slope = 1 - 1 / _ratio.Value;
    }

    protected override void ProcessCore(Span<float> samples)
    {
        var threshold = _threshold.Value;
        var reduction = 0f;
        for (var f = 0; f + Channels <= samples.Length; f += Channels)
        {
            var peak = 0f;
            for (var c = 0; c < Channels; c++)
                peak = MathF.Max(peak, MathF.Abs(samples[f + c]));

            var inDb = GainToDb(peak);
            _envDb += (inDb - _envDb) * (inDb > _envDb ? _atkCoef : _relCoef);
            reduction = _envDb > threshold ? -(_envDb - threshold) * _slope : 0f;

            var gain = DbToGain(reduction) * _makeupGain;
            for (var c = 0; c < Channels; c++)
                samples[f + c] *= gain;
        }
        GainReductionDb = reduction;
    }
}

public sealed class OutputGainEffect : Effect
{
    private readonly EffectParameter _gainDb;
    private float _current = 1, _target = 1;

    public OutputGainEffect() : base("output", "Output Gain", "Final level before the limiter.")
    {
        _gainDb = Param("gain", "Gain", -24, 24, 0, "dB");
    }

    protected override void Update() => _target = DbToGain(_gainDb.Value);

    protected override void ProcessCore(Span<float> samples)
    {
        // Ramp over the buffer so moving the knob never clicks.
        var step = (_target - _current) / Math.Max(samples.Length, 1);
        for (var i = 0; i < samples.Length; i++)
        {
            _current += step;
            samples[i] *= _current;
        }
        _current = _target;
    }
}

/// <summary>Peak limiter with instant attack: nothing leaves the chain above the ceiling.</summary>
public sealed class LimiterEffect : Effect
{
    private readonly EffectParameter _ceiling;
    private float _gain = 1, _ceil, _relCoef;

    public LimiterEffect() : base("limiter", "Limiter", "Safety net that stops clipping and loud spikes.")
    {
        _ceiling = Param("ceiling", "Ceiling", -12, 0, -1, "dB");
    }

    protected override void Update()
    {
        _ceil = DbToGain(_ceiling.Value);
        _relCoef = Coefficient(80);
    }

    protected override void ProcessCore(Span<float> samples)
    {
        for (var f = 0; f + Channels <= samples.Length; f += Channels)
        {
            var peak = 0f;
            for (var c = 0; c < Channels; c++)
                peak = MathF.Max(peak, MathF.Abs(samples[f + c]));

            var target = peak * _gain > _ceil ? _ceil / peak : 1f;
            _gain = target < _gain ? target : _gain + (target - _gain) * _relCoef;

            for (var c = 0; c < Channels; c++)
                samples[f + c] = Math.Clamp(samples[f + c] * _gain, -_ceil, _ceil);
        }
    }
}
