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
/// Spectral noise reduction: learns the steady background noise (fans, hum, hiss) per frequency and subtracts it.
/// 256-point STFT, 50 % overlap; overlap-add needs one full window, so it adds 256 samples (5.3 ms at 48 kHz).
/// </summary>
public sealed class NoiseReductionEffect : Effect
{
    private const int N = 256, Hop = N / 2, Bins = N / 2 + 1;

    private readonly EffectParameter _amount, _strength;
    private static readonly float[] Window = MakeWindow();

    private float[][] _inFifo = [], _outFifo = [], _accum = [];
    private readonly float[] _re = new float[N], _im = new float[N];
    private float[][] _specRe = [], _specIm = [];
    private readonly float[] _power = new float[Bins], _psd = new float[Bins], _noise = new float[Bins], _gain = new float[Bins], _smoothed = new float[Bins];
    private int _rover = N - Hop;
    private int _frames;
    private float _floor, _oversub;

    public NoiseReductionEffect() : base("denoise", "Noise Reduction", "Learns your room's background noise (fan, hum, hiss) and removes it.")
    {
        _amount = Param("amount", "Max reduction", 0, 40, 18, "-dB");
        _strength = Param("strength", "Strength", 0, 100, 50, "%");
    }

    public override string Category => "clean";
    public override double LatencyMs => N * 1000.0 / (SampleRate > 0 ? SampleRate : 48000);

    protected override void Reset()
    {
        _inFifo = Alloc(N); _outFifo = Alloc(N); _accum = Alloc(2 * N);
        _specRe = Alloc(Bins); _specIm = Alloc(Bins);
        Array.Fill(_noise, 0f);
        Array.Fill(_smoothed, 1f);
        _rover = N - Hop;
        _frames = 0;

        float[][] Alloc(int n) => Enumerable.Range(0, Channels).Select(_ => new float[n]).ToArray();
    }

    protected override void Update()
    {
        _floor = DbToGain(-_amount.Value);
        _oversub = 1f + _strength.Value / 100f * 2f;
    }

    protected override void ProcessCore(Span<float> samples)
    {
        const int latency = N - Hop;
        for (var f = 0; f + Channels <= samples.Length; f += Channels)
        {
            for (var c = 0; c < Channels; c++)
            {
                _inFifo[c][_rover] = samples[f + c];
                samples[f + c] = _outFifo[c][_rover - latency];
            }
            if (++_rover >= N)
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
            for (var i = 0; i < N; i++) { _re[i] = _inFifo[c][i] * Window[i]; _im[i] = 0; }
            Fft.Transform(_re, _im, inverse: false);
            for (var k = 0; k < Bins; k++)
            {
                _specRe[c][k] = _re[k];
                _specIm[c][k] = _im[k];
                _power[k] += _re[k] * _re[k] + _im[k] * _im[k];
            }
        }

        // Minimum-statistics noise tracking on time-smoothed power: follow quieter frames instantly, creep up
        // ~4 dB/s so a fan switching on is learned within seconds. The minimum of a fluctuating power sits below
        // its mean, hence the bias factor.
        const float bias = 1.5f;
        _frames++;
        for (var k = 0; k < Bins; k++)
        {
            var p = _power[k] + 1e-12f;
            _psd[k] = _frames == 1 ? p : _psd[k] + (p - _psd[k]) * 0.3f;
            if (_frames == 1 || _psd[k] < _noise[k]) _noise[k] = _psd[k];
            else _noise[k] *= 1.0025f;

            var g = 1f - _oversub * bias * _noise[k] / _psd[k];
            _gain[k] = MathF.Max(_floor, MathF.Min(1f, g));
        }

        // Smooth across frequency and time to avoid "musical noise" artefacts.
        for (var k = 0; k < Bins; k++)
        {
            var g = (_gain[Math.Max(k - 1, 0)] + 2 * _gain[k] + _gain[Math.Min(k + 1, Bins - 1)]) * 0.25f;
            _smoothed[k] += (g - _smoothed[k]) * (g > _smoothed[k] ? 0.7f : 0.3f);
        }

        for (var c = 0; c < Channels; c++)
        {
            for (var k = 0; k < Bins; k++)
            {
                _re[k] = _specRe[c][k] * _smoothed[k];
                _im[k] = _specIm[c][k] * _smoothed[k];
                if (k > 0 && k < N / 2) { _re[N - k] = _re[k]; _im[N - k] = -_im[k]; }
            }
            Fft.Transform(_re, _im, inverse: true);

            var accum = _accum[c];
            for (var i = 0; i < N; i++) accum[i] += _re[i] * Window[i];
            Array.Copy(accum, _outFifo[c], Hop);
            Array.Copy(accum, Hop, accum, 0, N);
            Array.Clear(accum, N, Hop);
            Array.Copy(_inFifo[c], Hop, _inFifo[c], 0, N - Hop);
        }
    }

    /// <summary>Square-root periodic Hann: analysis × synthesis sums to exactly 1 at 50 % overlap.</summary>
    private static float[] MakeWindow() =>
        Enumerable.Range(0, N).Select(i => MathF.Sqrt(0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / N))).ToArray();
}

/// <summary>Slow automatic gain: brings quiet and loud talkers to the same level, like a broadcast leveler.</summary>
public sealed class AutoLevelEffect : Effect
{
    private readonly EffectParameter _target, _maxGain, _speed;
    private float _rms, _gainDb, _rmsCoef, _gainCoef;
    private const float SpeechThreshold = 0.003f; // ~-50 dBFS: below this we hold, so noise isn't pumped up

    public AutoLevelEffect() : base("leveler", "Auto Level", "Keeps your voice at a steady loudness whether you lean in or sit back.")
    {
        _target = Param("target", "Target", -30, -10, -18, "dB");
        _maxGain = Param("maxgain", "Max boost", 0, 24, 9, "dB");
        _speed = Param("speed", "Speed", 200, 3000, 1000, "ms");
    }

    public override string Category => "dynamics";

    protected override void Reset() { _rms = 0; _gainDb = 0; }

    protected override void Update()
    {
        _rmsCoef = Coefficient(300);
        _gainCoef = Coefficient(_speed.Value);
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

            var level = MathF.Sqrt(_rms);
            if (level > SpeechThreshold)
            {
                var wanted = Math.Clamp(target - GainToDb(level), -12, max);
                _gainDb += (wanted - _gainDb) * _gainCoef;
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

internal static class Fft
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
