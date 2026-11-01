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

    public static float ToDecibels(float linear) => linear <= 0f ? float.NegativeInfinity : 20f * MathF.Log10(linear);

    public static float FromDecibels(float db) => MathF.Pow(10f, db / 20f);
}
