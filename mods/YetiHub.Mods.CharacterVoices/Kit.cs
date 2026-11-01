namespace YetiHub.Mods.CharacterVoices;

// A small, self-contained DSP kit for character voices. Everything is mono and per-sample: characters process the
// voice once and write it to every channel.

/// <summary>RBJ cookbook biquad, transposed direct form II.</summary>
internal sealed class Filter
{
    private float _b0 = 1, _b1, _b2, _a1, _a2, _z1, _z2;

    public static Filter LowPass(int sr, float hz, float q = 0.707f) => new Filter().Set(sr, hz, q, Kind.Low);
    public static Filter HighPass(int sr, float hz, float q = 0.707f) => new Filter().Set(sr, hz, q, Kind.High);
    public static Filter BandPass(int sr, float hz, float q) => new Filter().Set(sr, hz, q, Kind.Band);
    public static Filter Peak(int sr, float hz, float q, float db) => new Filter().Set(sr, hz, q, Kind.Peak, db);
    public static Filter LowShelf(int sr, float hz, float db) => new Filter().Set(sr, hz, 0.707f, Kind.LowShelf, db);

    public enum Kind { Low, High, Band, Peak, LowShelf }

    public Filter Set(int sr, float hz, float q, Kind kind, float db = 0)
    {
        var w = 2 * MathF.PI * Math.Clamp(hz, 10, sr * 0.45f) / sr;
        float cos = MathF.Cos(w), alpha = MathF.Sin(w) / (2 * q), a = MathF.Pow(10, db / 40);
        float b0, b1, b2, a0, a1, a2;
        switch (kind)
        {
            case Kind.Low: b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = b0; a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
            case Kind.High: b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = b0; a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
            case Kind.Band: b0 = alpha; b1 = 0; b2 = -alpha; a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
            case Kind.Peak: b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a; a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a; break;
            default:
                var k = 2 * MathF.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - (a - 1) * cos + k); b1 = 2 * a * ((a - 1) - (a + 1) * cos); b2 = a * ((a + 1) - (a - 1) * cos - k);
                a0 = (a + 1) + (a - 1) * cos + k; a1 = -2 * ((a - 1) + (a + 1) * cos); a2 = (a + 1) + (a - 1) * cos - k;
                break;
        }
        (_b0, _b1, _b2, _a1, _a2) = (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        return this;
    }

    public float Run(float x)
    {
        var y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }
}

/// <summary>Real-time pitch shifter: two cross-faded read heads sweeping a short delay line (~12 ms latency).</summary>
internal sealed class Shifter(int sampleRate)
{
    private readonly int _grain = sampleRate * 25 / 1000;
    private readonly float[] _buf = new float[sampleRate * 25 / 1000 * 4];
    private int _write;
    private double _phase;

    public float Run(float x, double ratio)
    {
        var len = _buf.Length;
        _buf[_write] = x;
        _phase += (1 - ratio) / _grain;
        _phase -= Math.Floor(_phase);
        var phaseB = (_phase + 0.5) % 1.0;
        var gainA = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * _phase));
        var y = Read(_write - (2 + _phase * _grain), len) * gainA + Read(_write - (2 + phaseB * _grain), len) * (1 - gainA);
        _write = (_write + 1) % len;
        return y;
    }

    public static double Ratio(double semitones) => Math.Pow(2, semitones / 12);

    private float Read(double pos, int len)
    {
        pos %= len;
        if (pos < 0) pos += len;
        var i = (int)pos;
        var f = (float)(pos - i);
        return _buf[i] + (_buf[(i + 1) % len] - _buf[i]) * f;
    }
}

/// <summary>Fractional delay line with feedback, for echoes, combs and choruses.</summary>
internal sealed class Delay(int maxSamples)
{
    private readonly float[] _buf = new float[maxSamples + 2];
    private int _write;

    public float Read(float delaySamples)
    {
        var pos = _write - Math.Clamp(delaySamples, 1, _buf.Length - 2);
        if (pos < 0) pos += _buf.Length;
        var i = (int)pos;
        var f = pos - i;
        return _buf[i] + (_buf[(i + 1) % _buf.Length] - _buf[i]) * f;
    }

    public void Write(float x)
    {
        _buf[_write] = x;
        _write = (_write + 1) % _buf.Length;
    }
}

/// <summary>Compact mono Freeverb-style reverb.</summary>
internal sealed class Reverb
{
    private readonly float[][] _combs, _aps;
    private readonly int[] _cPos, _aPos;
    private readonly float[] _store;
    private readonly float _feedback, _damp;

    public Reverb(int sampleRate, float size, float damping)
    {
        var s = sampleRate / 44100f;
        _combs = new[] { 1116, 1188, 1277, 1356, 1422, 1491 }.Select(t => new float[(int)(t * s)]).ToArray();
        _aps = new[] { 556, 441, 341 }.Select(t => new float[(int)(t * s)]).ToArray();
        _cPos = new int[_combs.Length];
        _aPos = new int[_aps.Length];
        _store = new float[_combs.Length];
        _feedback = 0.7f + size * 0.28f;
        _damp = damping * 0.4f;
    }

    public float Run(float x)
    {
        var input = x * 0.03f;
        var y = 0f;
        for (var i = 0; i < _combs.Length; i++)
        {
            var buf = _combs[i];
            var o = buf[_cPos[i]];
            _store[i] = o * (1 - _damp) + _store[i] * _damp;
            buf[_cPos[i]] = input + _store[i] * _feedback;
            if (++_cPos[i] >= buf.Length) _cPos[i] = 0;
            y += o;
        }
        for (var i = 0; i < _aps.Length; i++)
        {
            var buf = _aps[i];
            var b = buf[_aPos[i]];
            buf[_aPos[i]] = y + b * 0.5f;
            y = b - y;
            if (++_aPos[i] >= buf.Length) _aPos[i] = 0;
        }
        return y * 2.5f;
    }
}

/// <summary>Sine oscillator / LFO.</summary>
internal sealed class Osc(int sampleRate, double hz)
{
    private double _phase;
    public double Hz { get; set; } = hz;

    public float Run()
    {
        _phase += Hz / sampleRate;
        if (_phase >= 1) _phase -= 1;
        return (float)Math.Sin(2 * Math.PI * _phase);
    }
}

/// <summary>Detects when you start and stop talking (for radio squelch, space beeps, …).</summary>
internal sealed class SpeechDetector(int sampleRate)
{
    private float _env;
    private int _hold;
    private readonly float _atk = 1 - MathF.Exp(-1f / (0.005f * sampleRate));
    private readonly float _rel = 1 - MathF.Exp(-1f / (0.08f * sampleRate));
    private readonly int _holdSamples = sampleRate / 5;

    public bool Talking { get; private set; }

    /// <summary>Returns +1 when speech starts, -1 when it ends, else 0.</summary>
    public int Run(float x)
    {
        var a = MathF.Abs(x);
        _env += (a - _env) * (a > _env ? _atk : _rel);
        if (_env > 0.03f)
        {
            _hold = _holdSamples;
            if (!Talking) { Talking = true; return 1; }
        }
        else if (Talking && _env < 0.012f && --_hold <= 0)
        {
            Talking = false;
            return -1;
        }
        return 0;
    }
}

internal static class Shape
{
    /// <summary>Smooth saturation that never exceeds ±1.</summary>
    public static float Soft(float x) => MathF.Tanh(x);

    public static float Hard(float x, float ceiling) => Math.Clamp(x, -ceiling, ceiling);

    /// <summary>Reduces bit depth (e.g. 5 bits) for a lo-fi digital sound.</summary>
    public static float Crush(float x, int bits)
    {
        var steps = 1 << (bits - 1);
        return MathF.Round(x * steps) / steps;
    }
}
