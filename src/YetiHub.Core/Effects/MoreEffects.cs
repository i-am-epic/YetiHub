namespace YetiHub.Core.Effects;

/// <summary>The Yeti's stereo capsules can pull your voice off-centre; this folds it to the middle.</summary>
public sealed class MonoEffect : Effect
{
    private readonly EffectParameter _amount;

    public MonoEffect() : base("mono", "Mono Voice", "Centres your voice in both ears, like every broadcast mic.")
    {
        _amount = Param("amount", "Mono", 0, 100, 100, "%");
    }

    public override string Category => "clean";

    protected override void ProcessCore(Span<float> samples)
    {
        if (Channels < 2) return;
        var a = _amount.Value / 100f * 0.5f;
        for (var f = 0; f + 1 < samples.Length; f += Channels)
        {
            var l = samples[f];
            var r = samples[f + 1];
            samples[f] = l + (r - l) * a;
            samples[f + 1] = r + (l - r) * a;
        }
    }
}

/// <summary>
/// Noise cancelling: OM-LSA speech enhancement with MCRA noise tracking (Cohen &amp; Berdugo).
///
/// Per frequency bin and frame it estimates the probability that speech is present. Noise is learned only where
/// speech is unlikely (so the voice is never learned as noise), the gain is a log-spectral-amplitude estimator
/// driven by a decision-directed a-priori SNR (no "musical noise" warble), and the final gain blends between that
/// estimate (speech likely) and the full reduction floor (speech unlikely). This removes more steady noise than
/// spectral subtraction while cutting far less speech.
///
/// Standard mode: 256-point frames, 5.3 ms delay. High quality: 512-point frames (10.7 ms) for better low-frequency
/// (fan) resolution.
/// </summary>
public sealed class NoiseReductionEffect : Effect
{
    private readonly EffectParameter _amount, _strength, _keysParam, _quality;

    private int _n, _hop, _bins;
    private float[] _window = [];
    private float[][] _inFifo = [], _outFifo = [], _accum = [], _specRe = [], _specIm = [];
    private float[] _re = [], _im = [];
    private float[] _power = [], _smooth = [], _min = [], _minTemp = [], _pSpeech = [], _noise = [], _xiPrevGain2 = [], _gain = [], _lsa = [], _presence = [];
    private float _release, _speechAbsence, _talkThreshold, _speechFloor, _guardDepth;
    private int _rover, _frames, _minFrames, _minWindow;
    private float _floor, _snrBias, _keys, _clickGain;

    public NoiseReductionEffect() : base("denoise", "Noise Cancelling", "Learns your room's noise (fan, hiss, keyboard) and removes it while you talk.")
    {
        _amount = Param("amount", "Max reduction", 0, 40, 18, "-dB");
        _strength = Param("strength", "Strength", 0, 100, 50, "%");
        _keysParam = Param("keys", "Keyboard clicks", 0, 100, 50, "%");
        _quality = Param("quality", "High quality", 0, 1, 0, "toggle");
    }

    public override string Category => "clean";
    public override double LatencyMs => FrameSize * 1000.0 / (SampleRate > 0 ? SampleRate : 48000);

    private int FrameSize => _quality.Value >= 0.5f ? 512 : 256;

    protected override void Reset()
    {
        _n = FrameSize;
        _hop = _n / 2;
        _bins = _n / 2 + 1;
        // Square-root periodic Hann: analysis x synthesis sums to exactly 1 at 50 % overlap.
        _window = Enumerable.Range(0, _n).Select(i => MathF.Sqrt(0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / _n))).ToArray();
        _inFifo = Alloc(Channels, _n); _outFifo = Alloc(Channels, _n); _accum = Alloc(Channels, 2 * _n);
        _specRe = Alloc(Channels, _bins); _specIm = Alloc(Channels, _bins);
        _re = new float[_n]; _im = new float[_n];
        _power = new float[_bins]; _smooth = new float[_bins]; _min = new float[_bins]; _minTemp = new float[_bins];
        _pSpeech = new float[_bins]; _noise = new float[_bins]; _xiPrevGain2 = new float[_bins]; _gain = new float[_bins];
        _lsa = new float[_bins]; _presence = new float[_bins];
        Array.Fill(_gain, 1f);
        _rover = _n - _hop;
        _frames = 0;
        _minFrames = 0;
        // Minimum search over ~0.8 s.
        _minWindow = Math.Max(8, (int)(0.8 * SampleRate / _hop));

        static float[][] Alloc(int ch, int n) => Enumerable.Range(0, ch).Select(_ => new float[n]).ToArray();
    }

    protected override void Update()
    {
        if (_n != FrameSize) Reset(); // quality switched
        _floor = DbToGain(-_amount.Value);
        // Strength biases the a-priori SNR down: more strength = more aggressive noise removal.
        // Strength walks along a measured trade-off curve (offline speech bench): 0 = most protective of the voice
        // (~8.5 dB noise removed, ~1 % audible voice cut), 100 = most aggressive (~13 dB, ~2 %).
        var st = _strength.Value / 100f;
        _speechAbsence = Lerp(0.2f, 0.3f, st);
        _talkThreshold = Lerp(0.2f, 0.5f, st);
        _speechFloor = DbToGain(Lerp(-10f, -16f, st));
        _guardDepth = Lerp(1f, 0.7f, st); // higher strength lets the guard give a little more
        _snrBias = DbToGain(Lerp(-3f, -6f, st));
        _release = MathF.Exp(-(float)FrameSize / 2 / (Lerp(0.06f, 0.02f, st) * Math.Max(SampleRate, 8000)));
        _keys = _keysParam.Value / 100f;
        _clickGain = DbToGain(-30 * _keys);
    }

    protected override void ProcessCore(Span<float> samples)
    {
        var latency = _n - _hop;
        for (var f = 0; f + Channels <= samples.Length; f += Channels)
        {
            for (var c = 0; c < Channels; c++)
            {
                _inFifo[c][_rover] = samples[f + c];
                samples[f + c] = _outFifo[c][_rover - latency];
            }
            if (++_rover >= _n)
            {
                _rover = latency;
                ProcessFrame();
            }
        }
    }

    private void ProcessFrame()
    {
        Array.Clear(_power);
        for (var c = 0; c < Channels; c++)
        {
            for (var i = 0; i < _n; i++) { _re[i] = _inFifo[c][i] * _window[i]; _im[i] = 0; }
            Fft.Transform(_re, _im, inverse: false);
            for (var k = 0; k < _bins; k++)
            {
                _specRe[c][k] = _re[k];
                _specIm[c][k] = _im[k];
                _power[k] += (_re[k] * _re[k] + _im[k] * _im[k]) / Channels;
            }
        }

        _frames++;
        UpdateNoise();

        // OM-LSA gain, in two passes: per-bin estimate, then frame-level speech protection.
        const float alphaDd = 0.98f; // decision-directed smoothing
        var q = _speechAbsence;    // a-priori speech absence probability
        float voicePresence = 0;
        var voiceHi = Math.Min(_bins - 1, (int)(3400f * _n / SampleRate));
        for (var k = 0; k < _bins; k++)
        {
            var noise = MathF.Max(_noise[k], 1e-12f);
            var gamma = MathF.Min(_power[k] / noise, 1000f);                              // a-posteriori SNR
            var xi = alphaDd * _xiPrevGain2[k] + (1 - alphaDd) * MathF.Max(gamma - 1, 0); // a-priori SNR
            xi = MathF.Max(xi * _snrBias, 0.003f);
            var v = xi / (1 + xi) * gamma;
            var gLsa = MathF.Min(xi / (1 + xi) * MathF.Exp(0.5f * ExpIntegral(v)), 1f);

            // Probability that speech is present in this bin; MCRA's smoothed presence keeps word tails alive.
            var p = 1f / (1f + q / (1 - q) * (1 + xi) * MathF.Exp(-v));
            p = MathF.Max(p, _pSpeech[k]);

            _xiPrevGain2[k] = gLsa * gLsa * gamma;
            _lsa[k] = gLsa;
            _presence[k] = p;
            if (k >= 1 && k <= voiceHi) voicePresence += p;
        }
        voicePresence /= Math.Max(1, voiceHi);

        // While you're clearly talking, the voice masks the noise in the voice band: reduce there by at most 10 dB
        // so soft syllables and word endings are never carved out.
        var talking = voicePresence > _talkThreshold;
        var speechFloor = MathF.Max(_floor, _speechFloor);
        for (var k = 0; k < _bins; k++)
        {
            var floor = talking && k <= voiceHi ? speechFloor : _floor;
            var g = MathF.Pow(_lsa[k], _presence[k]) * MathF.Pow(floor, 1 - _presence[k]);
            // Voice guard: where this bin is clearly louder than the noise, never go below plain power subtraction,
            // so audible speech is never carved out. In noise-only bins (ratio ~1) this bound is ~0 and changes nothing.
            // Uses the time-smoothed power so random noise peaks don't trigger it; ~5 dB above noise = real voice.
            var ratio = _smooth[k] / MathF.Max(_noise[k], 1e-12f);
            if (ratio > 3f) g = MathF.Max(g, MathF.Sqrt((ratio - 1) / ratio) * _guardDepth);
            // "Max reduction" is a hard limit (and 0 dB means a true bypass).
            g = MathF.Max(g, _floor);
            // Hangover: gains rise instantly but fall over ~60 ms, so word tails fade naturally.
            _gain[k] = MathF.Max(g, _gain[k] * _release);
        }

        Classify();

        // Keyboard / mouse click suppression: a sudden broadband, treble-heavy burst while you aren't talking.
        if (_clickHold > 0)
        {
            _clickHold--;
            for (var k = 0; k < _bins; k++) _gain[k] *= _clickGain;
        }

        for (var c = 0; c < Channels; c++)
        {
            for (var k = 0; k < _bins; k++)
            {
                _re[k] = _specRe[c][k] * _gain[k];
                _im[k] = _specIm[c][k] * _gain[k];
                if (k > 0 && k < _n / 2) { _re[_n - k] = _re[k]; _im[_n - k] = -_im[k]; }
            }
            Fft.Transform(_re, _im, inverse: true);

            var accum = _accum[c];
            for (var i = 0; i < _n; i++) accum[i] += _re[i] * _window[i];
            Array.Copy(accum, _outFifo[c], _hop);
            Array.Copy(accum, _hop, accum, 0, _n);
            Array.Clear(accum, _n, _hop);
            Array.Copy(_inFifo[c], _hop, _inFifo[c], 0, _n - _hop);
        }
    }

    /// <summary>
    /// MCRA: smooth the power over time and frequency, track its minimum over ~0.8 s, decide where speech is present
    /// (power well above the minimum), and update the noise estimate only in proportion to speech absence.
    /// </summary>
    private void UpdateNoise()
    {
        const float alphaS = 0.8f, alphaP = 0.2f, alphaD = 0.95f, delta = 5f, bias = 1.6f;
        for (var k = 0; k < _bins; k++)
        {
            var sf = 0.25f * _power[Math.Max(k - 1, 0)] + 0.5f * _power[k] + 0.25f * _power[Math.Min(k + 1, _bins - 1)];
            if (_frames == 1)
            {
                _smooth[k] = _min[k] = _minTemp[k] = _noise[k] = sf + 1e-12f;
                continue;
            }
            _smooth[k] = alphaS * _smooth[k] + (1 - alphaS) * sf;
            _min[k] = MathF.Min(_min[k], _smooth[k]);
            _minTemp[k] = MathF.Min(_minTemp[k], _smooth[k]);

            var speech = _smooth[k] > delta * _min[k] ? 1f : 0f;
            _pSpeech[k] = alphaP * _pSpeech[k] + (1 - alphaP) * speech;
            var alpha = alphaD + (1 - alphaD) * _pSpeech[k];
            _noise[k] = alpha * _noise[k] + (1 - alpha) * _power[k];
        }

        // Sliding minimum: restart the search window periodically so the floor can rise when the room gets louder.
        if (++_minFrames >= _minWindow)
        {
            _minFrames = 0;
            for (var k = 0; k < _bins; k++)
            {
                _min[k] = MathF.Min(_minTemp[k], _smooth[k]);
                _minTemp[k] = _smooth[k];
            }
        }

        // Unbiased noise = never below the tracked minimum (scaled for the minimum's downward bias).
        for (var k = 0; k < _bins; k++)
            _noise[k] = MathF.Max(_noise[k], _min[k] * bias * (1 - _pSpeech[k]));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Exponential integral E1(x), Abramowitz &amp; Stegun approximations.</summary>
    private static float ExpIntegral(float x)
    {
        x = MathF.Max(x, 1e-6f);
        if (x < 1)
            return -0.57721566f - MathF.Log(x) + x * (0.99999193f + x * (-0.24991055f + x * (0.05519968f + x * (-0.00976004f + x * 0.00107857f))));
        var num = x * x + 2.334733f * x + 0.250621f;
        var den = x * x + 3.330657f * x + 1.681534f;
        return MathF.Exp(-x) / x * num / den;
    }

    private float _prevTotal;
    private int _clickHold;
    private int _clicksInWindow, _classifyFrames;

    /// <summary>Steady background noise level, dBFS (from the learned noise spectrum).</summary>
    public float NoiseLevelDb { get; private set; } = -120;

    /// <summary>Share of the background noise that is hiss (above 4 kHz), 0..1.</summary>
    public float HissShare { get; private set; }

    /// <summary>Share of the background noise that is low rumble/fan (below 400 Hz), 0..1.</summary>
    public float LowShare { get; private set; }

    /// <summary>Keyboard/mouse clicks suppressed per minute.</summary>
    public float ClicksPerMinute { get; private set; }

    /// <summary>True while the current frame looks like speech.</summary>
    public bool VoiceActive { get; private set; }

    private void Classify()
    {
        var binHz = (float)SampleRate / _n;
        int voiceHi = (int)(3400 / binHz), hissLo = (int)(4000 / binHz), lowHi = Math.Max(1, (int)(400 / binHz));
        float total = 0, voiceP = 0, high = 0, noiseTotal = 0, noiseHigh = 0, noiseLow = 0, presence = 0;
        for (var k = 1; k < _bins; k++)
        {
            total += _power[k];
            noiseTotal += _noise[k];
            if (k <= voiceHi) { voiceP += _pSpeech[k]; presence++; }
            if (k >= hissLo) { high += _power[k]; noiseHigh += _noise[k]; }
            if (k <= lowHi) noiseLow += _noise[k];
        }

        VoiceActive = presence > 0 && voiceP / presence > 0.25f;
        var jump = total > _prevTotal * 8f; // > 9 dB sudden rise
        if (_keys > 0 && jump && !VoiceActive && high > total * 0.35f)
        {
            _clickHold = Math.Max(2, 1024 / _n); // ~8 ms
            _clicksInWindow++;
        }
        _prevTotal = total;

        // Sum of |X|^2 for a sqrt-Hann window ~ N/2 * N/2 * mean-square; convert to dBFS.
        var norm = _n / 2f * _n / 2f;
        NoiseLevelDb = 10 * MathF.Log10(MathF.Max(noiseTotal / norm, 1e-12f));
        HissShare = noiseTotal > 0 ? noiseHigh / noiseTotal : 0;
        LowShare = noiseTotal > 0 ? noiseLow / noiseTotal : 0;

        if (++_classifyFrames >= SampleRate / _hop * 10) // 10 s window
        {
            ClicksPerMinute = _clicksInWindow * 6f;
            _clicksInWindow = 0;
            _classifyFrames = 0;
        }
    }
}

/// <summary>
/// Slow automatic gain: brings quiet and loud talkers to the same level, like a broadcast leveler. Speech is detected
/// relative to the tracked noise floor (not a fixed level), so it also works for soft voices far from the mic, and
/// noise between words is never pumped up.
/// </summary>
public sealed class AutoLevelEffect : Effect
{
    private readonly EffectParameter _target, _maxGain, _speed;
    private float _rms, _gainDb, _floorDb = -60, _rmsCoef, _gainCoef, _floorRise;

    public AutoLevelEffect() : base("leveler", "Auto Level", "Keeps your voice at a steady loudness whether you lean in or sit back.")
    {
        _target = Param("target", "Target", -30, -10, -18, "dB");
        _maxGain = Param("maxgain", "Max boost", 0, 30, 20, "dB");
        _speed = Param("speed", "Speed", 200, 3000, 1000, "ms");
    }

    public override string Category => "dynamics";

    /// <summary>Gain currently applied, dB. For the UI.</summary>
    public float GainDb => _gainDb;

    private bool _primed;

    protected override void Reset() { _rms = 0; _gainDb = 0; _floorDb = -60; _primed = false; }

    protected override void Update()
    {
        _rmsCoef = Coefficient(300);
        _gainCoef = Coefficient(_speed.Value);
        _floorRise = 3f / SampleRate;
    }

    protected override void ProcessCore(Span<float> samples)
    {
        var target = _target.Value;
        var max = _maxGain.Value;
        for (var f = 0; f + Channels <= samples.Length; f += Channels)
        {
            var sq = 0f;
            for (var c = 0; c < Channels; c++) sq += samples[f + c] * samples[f + c];
            _rms += (sq / Channels - _rms) * _rmsCoef;

            var levelDb = GainToDb(MathF.Sqrt(_rms));
            if (levelDb < _floorDb) _floorDb += (levelDb - _floorDb) * 0.0005f;
            else _floorDb = MathF.Min(_floorDb + _floorRise, -30f);

            // Speech = clearly above the room noise; otherwise hold the current gain.
            if (levelDb > _floorDb + 10f && levelDb > -70f)
            {
                var wanted = Math.Clamp(target - levelDb, -12, max);
                if (!_primed)
                {
                    // First words after start: jump most of the way so they aren't too quiet.
                    _gainDb = wanted * 0.8f;
                    _primed = true;
                }
                // Far off target (new speaker, moved away from the mic): catch up 4x faster.
                var coef = MathF.Abs(wanted - _gainDb) > 6f ? MathF.Min(1f, _gainCoef * 4f) : _gainCoef;
                _gainDb += (wanted - _gainDb) * coef;
            }

            var g = DbToGain(_gainDb);
            for (var c = 0; c < Channels; c++) samples[f + c] *= g;
        }
    }
}

/// <summary>Tube-style soft saturation: adds gentle harmonics for a fuller, "expensive preamp" sound.</summary>
public sealed class WarmthEffect : Effect
{
    private readonly EffectParameter _drive, _mix;
    private float _k, _norm, _bias, _biasOut, _wet;
    private readonly Biquad _dcBlock = new();

    public WarmthEffect() : base("warmth", "Warmth", "Analog tube saturation for a fuller, richer voice.")
    {
        _drive = Param("drive", "Drive", 0, 24, 6, "dB");
        _mix = Param("mix", "Mix", 0, 100, 35, "%");
    }

    public override string Category => "tone";

    protected override void Reset() { _dcBlock.EnsureChannels(Channels); _dcBlock.Reset(); }

    protected override void Update()
    {
        _k = DbToGain(_drive.Value);
        _bias = 0.12f;                      // asymmetry → even harmonics, like a triode
        _biasOut = MathF.Tanh(_k * _bias);
        _norm = 1f / MathF.Max(MathF.Tanh(_k * (1 + _bias)) - _biasOut, 1e-3f);
        _wet = _mix.Value / 100f;
        _dcBlock.SetHighPass(SampleRate, 15);
    }

    protected override void ProcessCore(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];
            var y = (MathF.Tanh(_k * (x + _bias)) - _biasOut) * _norm;
            y = _dcBlock.Process(y, i % Channels);
            samples[i] = x + (y - x) * _wet;
        }
    }
}

/// <summary>Harmonic exciter: regenerates top-end sparkle the Yeti's capsule rolls off.</summary>
public sealed class ExciterEffect : Effect
{
    private readonly EffectParameter _frequency, _amount;
    private readonly Biquad _hp = new();
    private float _amt;

    public ExciterEffect() : base("exciter", "Exciter", "Adds crisp, airy detail for that polished studio sheen.")
    {
        _frequency = Param("frequency", "Frequency", 2000, 10000, 4500, "Hz");
        _amount = Param("amount", "Amount", 0, 100, 25, "%");
    }

    public override string Category => "tone";

    protected override void Reset() { _hp.EnsureChannels(Channels); _hp.Reset(); }

    protected override void Update()
    {
        _hp.SetHighPass(SampleRate, _frequency.Value);
        _amt = _amount.Value / 100f * 0.6f;
    }

    protected override void ProcessCore(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var high = _hp.Process(samples[i], i % Channels);
            samples[i] += MathF.Tanh(high * 4f) * 0.25f * _amt;
        }
    }
}

/// <summary>Freeverb-style room: 8 damped combs and 4 allpasses per channel, decorrelated for width.</summary>
public sealed class ReverbEffect : Effect
{
    private static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllpassTunings = [556, 441, 341, 225];
    private const int StereoSpread = 23;

    private readonly EffectParameter _size, _damping, _mix;
    private float[][][] _combs = [], _allpasses = [];
    private int[][] _combPos = [], _apPos = [];
    private float[][] _combStore = [];
    private float _feedback, _damp, _wet;

    public ReverbEffect() : base("reverb", "Room Reverb", "Adds a sense of space, from a small booth to a big hall.")
    {
        _size = Param("size", "Size", 0, 100, 40, "%");
        _damping = Param("damping", "Damping", 0, 100, 50, "%");
        _mix = Param("mix", "Mix", 0, 60, 12, "%");
    }

    public override string Category => "space";

    protected override void Reset()
    {
        var scale = SampleRate / 44100f;
        _combs = new float[Channels][][];
        _allpasses = new float[Channels][][];
        _combPos = new int[Channels][];
        _apPos = new int[Channels][];
        _combStore = new float[Channels][];
        for (var c = 0; c < Channels; c++)
        {
            var spread = c % 2 == 1 ? StereoSpread : 0;
            _combs[c] = CombTunings.Select(t => new float[(int)((t + spread) * scale)]).ToArray();
            _allpasses[c] = AllpassTunings.Select(t => new float[(int)((t + spread) * scale)]).ToArray();
            _combPos[c] = new int[CombTunings.Length];
            _apPos[c] = new int[AllpassTunings.Length];
            _combStore[c] = new float[CombTunings.Length];
        }
    }

    protected override void Update()
    {
        _feedback = 0.7f + _size.Value / 100f * 0.28f;
        _damp = _damping.Value / 100f * 0.4f;
        _wet = _mix.Value / 100f;
    }

    protected override void ProcessCore(Span<float> samples)
    {
        for (var f = 0; f + Channels <= samples.Length; f += Channels)
        {
            var input = 0f;
            for (var c = 0; c < Channels; c++) input += samples[f + c];
            input *= 0.015f / Channels;

            for (var c = 0; c < Channels; c++)
            {
                var outSample = 0f;
                for (var i = 0; i < _combs[c].Length; i++)
                {
                    var buf = _combs[c][i];
                    ref var pos = ref _combPos[c][i];
                    var y = buf[pos];
                    _combStore[c][i] = y * (1 - _damp) + _combStore[c][i] * _damp;
                    buf[pos] = input + _combStore[c][i] * _feedback;
                    if (++pos >= buf.Length) pos = 0;
                    outSample += y;
                }
                for (var i = 0; i < _allpasses[c].Length; i++)
                {
                    var buf = _allpasses[c][i];
                    ref var pos = ref _apPos[c][i];
                    var b = buf[pos];
                    buf[pos] = outSample + b * 0.5f;
                    outSample = b - outSample;
                    if (++pos >= buf.Length) pos = 0;
                }
                samples[f + c] = samples[f + c] * (1 - _wet * 0.5f) + outSample * _wet * 3f;
            }
        }
    }
}

public static class Fft
{
    /// <summary>In-place iterative radix-2 complex FFT. Inverse is scaled by 1/n.</summary>
    public static void Transform(float[] re, float[] im, bool inverse)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = 2 * MathF.PI / len * (inverse ? 1 : -1);
            float wr = MathF.Cos(ang), wi = MathF.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var a = i + k;
                    var b = a + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    var ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
        if (inverse)
        {
            for (var i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }
    }
}
