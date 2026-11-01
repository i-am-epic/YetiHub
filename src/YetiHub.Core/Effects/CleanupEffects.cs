namespace YetiHub.Core.Effects;

/// <summary>
/// Software pop filter: watches for plosive bursts ("p", "b") — a sudden low-frequency thump much louder than the
/// voice band — and ducks only the lows for those few milliseconds. Side-address condensers like the Yeti are
/// very prone to these.
/// </summary>
public sealed class PopFilterEffect : Effect
{
    private readonly EffectParameter _sensitivity, _amount;
    private readonly Biquad _lowSplit = new(), _detLow = new(), _detMid = new();
    private float _lowEnv, _midEnv, _duck = 1, _atk, _rel, _ratio, _maxCut;

    public PopFilterEffect() : base("popfilter", "Pop Filter", "Catches plosive thumps on P and B sounds before they hit your listeners.")
    {
        _sensitivity = Param("sensitivity", "Sensitivity", 0, 100, 60, "%");
        _amount = Param("amount", "Max reduction", 0, 30, 18, "-dB");
    }

    public override string Category => "clean";

    /// <summary>Count of plosives caught since start. For the UI.</summary>
    public int PopsCaught { get; private set; }

    protected override void Reset()
    {
        foreach (var b in new[] { _lowSplit, _detLow, _detMid }) { b.EnsureChannels(Channels); b.Reset(); }
        _lowEnv = _midEnv = 0;
        _duck = 1;
    }

    protected override void Update()
    {
        _lowSplit.SetLowPass(SampleRate, 160);
        _detLow.SetLowPass(SampleRate, 120);
        _detMid.SetBandPass(SampleRate, 1500, 0.7f);
        _atk = Coefficient(0.5f);
        _rel = Coefficient(60);
        _ratio = 6f - _sensitivity.Value / 100f * 4f; // lows must exceed mids by 6x (insensitive) .. 2x (sensitive)
        _maxCut = DbToGain(-_amount.Value);
    }

    protected override void ProcessCore(Span<float> samples)
    {
        var wasDucking = _duck < 0.7f;
        for (var f = 0; f + Channels <= samples.Length; f += Channels)
        {
            var mono = 0f;
            for (var c = 0; c < Channels; c++) mono += samples[f + c];
            mono /= Channels;
            var lo = MathF.Abs(_detLow.Process(mono, 0));
            var mid = MathF.Abs(_detMid.Process(mono, Channels > 1 ? 1 : 0));
            _lowEnv += (lo - _lowEnv) * (lo > _lowEnv ? _atk : _rel);
            _midEnv += (mid - _midEnv) * (mid > _midEnv ? _atk : _rel);

            var target = _lowEnv > 0.02f && _lowEnv > _midEnv * _ratio
                ? MathF.Max(_maxCut, _midEnv * _ratio / _lowEnv)
                : 1f;
            _duck += (target - _duck) * (target < _duck ? _atk : _rel);

            for (var c = 0; c < Channels; c++)
            {
                var x = samples[f + c];
                var low = _lowSplit.Process(x, c);
                samples[f + c] = x - low * (1 - _duck);
            }
        }
        if (!wasDucking && _duck < 0.7f) PopsCaught++;
    }
}

/// <summary>
/// Mains hum remover. Listens for 50 Hz (Europe/Asia) vs 60 Hz (Americas) hum and its harmonics, then removes them
/// with very narrow notches, leaving the voice untouched.
/// </summary>
public sealed class HumRemoverEffect : Effect
{
    private readonly EffectParameter _harmonics, _depth;
    private Biquad[] _notches = [];
    private double _p50, _q50, _p60, _q60, _detectPhase;
    private int _detectCount;
    private int _mainsHz = 50;

    public HumRemoverEffect() : base("dehum", "Hum Remover", "Auto-detects 50/60 Hz electrical hum and removes it surgically.")
    {
        _harmonics = Param("harmonics", "Harmonics", 1, 8, 4, "x");
        _depth = Param("depth", "Narrowness", 5, 60, 30, "Q");
    }

    public override string Category => "clean";

    /// <summary>Detected mains frequency (50 or 60) and how strong the hum is relative to the signal.</summary>
    public int MainsHz => _mainsHz;
    public float HumLevelDb { get; private set; } = -120;

    protected override void Reset()
    {
        _p50 = _q50 = _p60 = _q60 = 0;
        _detectCount = 0;
        BuildNotches();
    }

    protected override void Update() => BuildNotches();

    private void BuildNotches()
    {
        var n = (int)Math.Round(_harmonics.Value);
        _notches = Enumerable.Range(1, n).Select(h =>
        {
            var b = new Biquad();
            b.EnsureChannels(Math.Max(Channels, 1));
            b.SetNotch(Math.Max(SampleRate, 8000), _mainsHz * h, _depth.Value);
            return b;
        }).ToArray();
    }

    protected override void ProcessCore(Span<float> samples)
    {
        // Detection: correlate with 50 and 60 Hz references over 1 s windows (Goertzel-style energy).
        for (var f = 0; f + Channels <= samples.Length; f += Channels)
        {
            // In-phase + quadrature correlation so the hum's phase doesn't matter.
            var x = samples[f];
            var t = _detectPhase / SampleRate;
            _p50 += x * Math.Sin(2 * Math.PI * 50 * t);
            _q50 += x * Math.Cos(2 * Math.PI * 50 * t);
            _p60 += x * Math.Sin(2 * Math.PI * 60 * t);
            _q60 += x * Math.Cos(2 * Math.PI * 60 * t);
            _detectPhase++;
            if (++_detectCount >= SampleRate)
            {
                // Amplitude of each component = 2/N * |correlation|.
                var e50 = 2 * Math.Sqrt(_p50 * _p50 + _q50 * _q50) / SampleRate;
                var e60 = 2 * Math.Sqrt(_p60 * _p60 + _q60 * _q60) / SampleRate;
                HumLevelDb = 20 * MathF.Log10((float)Math.Max(Math.Max(e50, e60), 1e-9));
                var detected = e60 > e50 * 2 ? 60 : e50 > e60 * 2 ? 50 : _mainsHz;
                if (detected != _mainsHz) { _mainsHz = detected; BuildNotches(); }
                _p50 = _q50 = _p60 = _q60 = 0;
                _detectCount = 0;
                _detectPhase = 0;
            }
        }

        foreach (var notch in _notches)
            for (var i = 0; i < samples.Length; i++)
                samples[i] = notch.Process(samples[i], i % Channels);
    }
}
