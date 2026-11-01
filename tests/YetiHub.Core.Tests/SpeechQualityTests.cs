using YetiHub.Core.Effects;
using YetiHub.Core.Profiles;

namespace YetiHub.Core.Tests;

/// <summary>
/// Regression tests for the "words get cut off" problem: every standard profile must pass speech through without
/// chopping syllables, for both normal and soft (far from the mic) talkers, and land near the -16 LUFS target.
/// </summary>
public class SpeechQualityTests
{
    private const int Rate = 48000;
    private const int Frame = 480; // 10 ms

    public static TheoryData<string, float> Cases()
    {
        var data = new TheoryData<string, float>();
        foreach (var p in Presets.All.Where(p => p.EffectsEnabled))
        {
            data.Add(p.Id, -12f); // normal talker
            data.Add(p.Id, -24f); // soft talker / leaning back
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Profile_never_chops_speech(string profileId, float speechPeakDb)
    {
        var (clean, noisy) = SyntheticSpeech(speechPeakDb);
        var chain = new EffectChain();
        chain.ApplyState(Presets.All.First(p => p.Id == profileId).Effects);

        var output = (float[])noisy.Clone();
        for (var o = 0; o < output.Length; o += 960)
            chain.Process(output.AsSpan(o, Math.Min(960, output.Length - o)), Rate, 2);
        var delay = (int)Math.Round(chain.LatencyMs * Rate / 1000);

        // Per-frame gain on frames that contain speech, relative to the median: a "chop" is a 10 dB dip.
        var frames = clean.Length / Frame;
        var gains = new List<(int Frame, float Db)>();
        for (var f = 0; f < frames; f++)
        {
            if (Rms(clean, f * Frame, Frame, 1) < Dsp.FromDecibels(speechPeakDb - 30)) continue;
            var inR = Rms(noisy, f * Frame * 2, Frame * 2, 1);
            var outR = Rms(output, (f * Frame + delay) * 2, Frame * 2, 1);
            gains.Add((f, 20 * MathF.Log10((outR + 1e-9f) / (inR + 1e-9f))));
        }
        var median = gains.Select(g => g.Db).OrderBy(g => g).ElementAt(gains.Count / 2);
        var chopped = gains.Count(g => g.Db < median - 10);

        Assert.True(chopped <= gains.Count / 100, $"{profileId} at {speechPeakDb} dBFS chopped {chopped} of {gains.Count} speech frames");

        var meter = new LoudnessMeter();
        meter.Process(output.AsSpan(output.Length / 3), Rate, 2);
        Assert.InRange(meter.ShortTermLufs, -21, -11);
    }

    /// <summary>
    /// Speech-like test signal: voiced syllables (120 Hz harmonics shaped by two formants) of varying length and
    /// loudness, soft decaying tails, short gaps — over pink-ish room noise, 50 Hz hum and keyboard clicks.
    /// </summary>
    private static (float[] Clean, float[] Noisy) SyntheticSpeech(float peakDb)
    {
        var rng = new Random(11);
        var seconds = 8;
        var clean = new float[Rate * seconds];
        var pos = Rate / 2;
        while (pos < clean.Length - Rate / 2)
        {
            var len = rng.Next(Rate / 12, Rate / 3);         // 80–330 ms syllables
            var loud = 0.4f + (float)rng.NextDouble() * 0.6f; // stressed vs unstressed
            var f0 = 105 + rng.Next(40);
            for (var i = 0; i < len && pos + i < clean.Length; i++)
            {
                var t = (double)i / Rate;
                var env = (float)(Math.Min(1, i / (Rate * 0.01)) * Math.Exp(-2.5 * i / (double)len)); // soft tail
                double v = 0;
                for (var h = 1; h <= 25; h++)
                {
                    var hz = f0 * h;
                    var formant = Math.Exp(-Math.Pow((hz - 700) / 300.0, 2)) + 0.6 * Math.Exp(-Math.Pow((hz - 2200) / 500.0, 2)) + 0.05;
                    v += formant * Math.Sin(2 * Math.PI * hz * t + h);
                }
                clean[pos + i] = (float)(v * env * loud);
            }
            pos += len + rng.Next(Rate / 40, Rate / 6); // 25–160 ms gaps
        }

        var peak = clean.Max(MathF.Abs);
        var scale = Dsp.FromDecibels(peakDb) / peak;
        var noisy = new float[clean.Length * 2];
        float b0 = 0, b1 = 0;
        for (var i = 0; i < clean.Length; i++)
        {
            clean[i] *= scale;
            var white = (float)(rng.NextDouble() * 2 - 1);
            b0 = 0.997f * b0 + white * 0.03f; b1 = 0.95f * b1 + white * 0.1f;
            var noise = (b0 + b1 + white * 0.05f) * 0.004f + 0.0015f * MathF.Sin(2 * MathF.PI * 50 * i / Rate);
            var click = i % 12000 < 30 ? (float)(rng.NextDouble() - 0.5) * 0.05f : 0f;
            noisy[i * 2] = noisy[i * 2 + 1] = clean[i] + noise + click;
        }
        return (clean, noisy);
    }

    private static float Rms(float[] s, int start, int count, int stride)
    {
        double sum = 0;
        var n = 0;
        for (var i = start; i < Math.Min(start + count, s.Length); i += stride) { sum += s[i] * s[i]; n++; }
        return n == 0 ? 0 : (float)Math.Sqrt(sum / n);
    }
}
