namespace YetiHub.Core;

public static class Dsp
{
    public static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            var a = Math.Abs(s);
            if (a > peak)
                peak = a;
        }
        return Math.Min(peak, 1f);
    }

    /// <summary>
    /// Leaves samples below 0.7 (-3 dBFS) untouched and smoothly compresses anything above toward 0.98, so mod
    /// output can never hard-clip. Also scrubs NaN/infinity from a misbehaving mod.
    /// </summary>
    public static void SoftLimit(Span<float> samples)
    {
        const float knee = 0.7f, headroom = 0.28f;
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];
            if (!float.IsFinite(x)) { samples[i] = 0; continue; }
            var a = MathF.Abs(x);
            if (a <= knee) continue;
            samples[i] = MathF.CopySign(knee + headroom * MathF.Tanh((a - knee) / headroom), x);
        }
    }

    public static float ToDecibels(float linear) => linear <= 0f ? float.NegativeInfinity : 20f * MathF.Log10(linear);

    public static float FromDecibels(float db) => MathF.Pow(10f, db / 20f);
}
