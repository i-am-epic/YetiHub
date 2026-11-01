namespace YetiHub.Core.Effects;

/// <summary>
/// Second-order IIR filter (RBJ Audio EQ Cookbook), transposed direct form II, one state pair per channel.
/// </summary>
public sealed class Biquad
{
    private float _b0 = 1, _b1, _b2, _a1, _a2;
    private float[] _z1 = new float[2], _z2 = new float[2];

    public void EnsureChannels(int channels)
    {
        if (_z1.Length >= channels) return;
        _z1 = new float[channels];
        _z2 = new float[channels];
    }

    public void Reset()
    {
        Array.Clear(_z1);
        Array.Clear(_z2);
    }

    public float Process(float x, int channel)
    {
        var y = _b0 * x + _z1[channel];
        _z1[channel] = _b1 * x - _a1 * y + _z2[channel];
        _z2[channel] = _b2 * x - _a2 * y;
        return y;
    }

    public void SetHighPass(int sampleRate, float frequency, float q = 0.7071f)
    {
        var (cos, alpha) = Prewarp(sampleRate, frequency, q);
        Set((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public void SetBandPass(int sampleRate, float frequency, float q)
    {
        var (cos, alpha) = Prewarp(sampleRate, frequency, q);
        Set(alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public void SetPeaking(int sampleRate, float frequency, float q, float gainDb)
    {
        var a = MathF.Pow(10, gainDb / 40);
        var (cos, alpha) = Prewarp(sampleRate, frequency, q);
        Set(1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a);
    }

    public void SetLowShelf(int sampleRate, float frequency, float gainDb)
    {
        var a = MathF.Pow(10, gainDb / 40);
        var (cos, alpha) = Prewarp(sampleRate, frequency, 0.7071f);
        var k = 2 * MathF.Sqrt(a) * alpha;
        Set(a * ((a + 1) - (a - 1) * cos + k), 2 * a * ((a - 1) - (a + 1) * cos), a * ((a + 1) - (a - 1) * cos - k),
            (a + 1) + (a - 1) * cos + k, -2 * ((a - 1) + (a + 1) * cos), (a + 1) + (a - 1) * cos - k);
    }

    public void SetHighShelf(int sampleRate, float frequency, float gainDb)
    {
        var a = MathF.Pow(10, gainDb / 40);
        var (cos, alpha) = Prewarp(sampleRate, frequency, 0.7071f);
        var k = 2 * MathF.Sqrt(a) * alpha;
        Set(a * ((a + 1) + (a - 1) * cos + k), -2 * a * ((a - 1) + (a + 1) * cos), a * ((a + 1) + (a - 1) * cos - k),
            (a + 1) - (a - 1) * cos + k, 2 * ((a - 1) - (a + 1) * cos), (a + 1) - (a - 1) * cos - k);
    }

    private static (float Cos, float Alpha) Prewarp(int sampleRate, float frequency, float q)
    {
        var w = 2 * MathF.PI * Math.Clamp(frequency, 10, sampleRate * 0.45f) / sampleRate;
        return (MathF.Cos(w), MathF.Sin(w) / (2 * q));
    }

    private void Set(float b0, float b1, float b2, float a0, float a1, float a2)
    {
        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }
}
