namespace YetiHub.Core.Effects;

public sealed class LowCutEffect : Effect
{
    private readonly Biquad _filter = new();
    private readonly EffectParameter _frequency;

    public LowCutEffect() : base("lowcut", "Low Cut", "Removes rumble, desk bumps and plosive thumps below the cutoff.")
    {
        _frequency = Param("frequency", "Cutoff", 20, 300, 80, "Hz");
    }

    public override string Category => "clean";

    protected override void Reset() { _filter.EnsureChannels(Channels); _filter.Reset(); }
    protected override void Update() => _filter.SetHighPass(SampleRate, _frequency.Value);

    protected override void ProcessCore(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
            samples[i] = _filter.Process(samples[i], i % Channels);
    }
}

/// <summary>
/// Adaptive downward expander. Instead of a fixed threshold (which chops quiet talkers) it tracks the room's noise
/// floor and opens a set margin above it, with hysteresis (closes 5 dB lower than it opens) and a hold so word
/// endings and short pauses are never cut. When closed it turns noise down, not off.
/// </summary>
public sealed class NoiseGateEffect : Effect
{
    private const float HysteresisDb = 5f;

    private readonly EffectParameter _sensitivity, _range, _hold, _release;
    private readonly Biquad _detectorHp = new();
    private float _env, _floorDb = -70, _gain = 1, _rangeGain, _attackCoef, _releaseCoef, _envAtk, _envRel, _floorRise;
    private int _holdSamples, _holdLeft;
    private bool _open;

    public NoiseGateEffect() : base("gate", "Noise Gate", "Learns your room and quiets it between words, without chopping them.")
    {
        _sensitivity = Param("sensitivity", "Opens above noise", 3, 24, 9, "dB");
        _range = Param("range", "Reduction", -80, 0, -30, "dB");
        _hold = Param("hold", "Hold", 20, 500, 150, "ms");
        _release = Param("release", "Release", 20, 500, 150, "ms");
    }

    public override string Category => "clean";

    /// <summary>Current estimate of the room's noise level, dBFS. For the UI.</summary>
    public float NoiseFloorDb => _floorDb;
    public bool IsOpen => _open;

    protected override void Reset()
    {
        _detectorHp.EnsureChannels(1);
        _detectorHp.Reset();
        _env = 0;
        _floorDb = -70;
        _open = false;
    }

    protected override void Update()
    {
        _detectorHp.SetHighPass(SampleRate, 120); // hum and rumble mustn't hold the gate open
        _rangeGain = DbToGain(_range.Value);
        _attackCoef = Coefficient(0.5f);
        _releaseCoef = Coefficient(_release.Value);
        _envAtk = Coefficient(2);
        _envRel = Coefficient(40);
        _floorRise = 3f / SampleRate; // noise floor creeps up 3 dB/s, drops instantly
        _holdSamples = (int)(_hold.Value * 0.001f * SampleRate);
    }

    protected override void ProcessCore(Span<float> samples)
    {
        for (var f = 0; f + Channels <= samples.Length; f += Channels)
        {
            var mono = 0f;
            for (var c = 0; c < Channels; c++) mono += samples[f + c];
            var x = MathF.Abs(_detectorHp.Process(mono / Channels, 0));
            _env += (x - _env) * (x > _env ? _envAtk : _envRel);

            var envDb = GainToDb(_env);
            if (envDb < _floorDb) _floorDb += (envDb - _floorDb) * 0.001f;
            else _floorDb = MathF.Min(_floorDb + _floorRise, -25f);

            var openAt = MathF.Max(_floorDb + _sensitivity.Value, -75f);
            if (envDb > openAt) { _open = true; _holdLeft = _holdSamples; }
            else if (_open && envDb < openAt - HysteresisDb && --_holdLeft <= 0) _open = false;

            var target = _open ? 1f : _rangeGain;
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

    public override string Category => "tone";

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
        _amount = Param("amount", "Max reduction", 0, 20, 8, "-dB");
    }

    public override string Category => "tone";

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

    public override string Category => "dynamics";

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

    public override string Category => "dynamics";

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

    public override string Category => "dynamics";

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
