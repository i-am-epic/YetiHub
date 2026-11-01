using YetiHub.Mods;
using YetiHub.Mods.CharacterVoices;
using YetiHub.Mods.RobotVoice;

namespace YetiHub.Core.Tests;

public class CharacterVoiceTests
{
    private const int Rate = 48000;

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var v in AllVoices()) data.Add(v.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Character_transforms_voice_safely(string name)
    {
        var voice = AllVoices().First(v => v.Name == name);
        var input = Speech(Rate * 2);
        var output = (float[])input.Clone();
        for (var o = 0; o < output.Length; o += 960)
            voice.Process(output.AsSpan(o, Math.Min(960, output.Length - o)), Rate, 2);

        Assert.All(output, v => Assert.True(float.IsFinite(v) && MathF.Abs(v) <= 0.98f, $"{name}: {v}"));

        // Still audible: within 18 dB of the input level.
        var inDb = Db(Rms(input));
        var outDb = Db(Rms(output));
        Assert.InRange(outDb, inDb - 18, inDb + 12);

        // Actually a different voice: the output is not just a scaled copy of the input.
        var scale = Rms(output) / Rms(input);
        var diff = output.Zip(input, (o, i) => o - i * scale).ToArray();
        Assert.True(Rms(diff) > Rms(output) * 0.2f, $"{name} barely changes the voice");
    }

    [Fact]
    public void Every_character_has_a_unique_name_emoji_and_description()
    {
        var all = AllVoices().Cast<IProcessorInfo>().Append(new RobotVoiceProcessor()).ToList();
        Assert.All(all, c => Assert.Equal(IProcessorInfo.CharacterGroup, c.Group));
        Assert.All(all, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
        Assert.All(all, c => Assert.False(string.IsNullOrWhiteSpace(c.Emoji)));
        var names = all.Cast<IAudioProcessor>().Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.True(names.Count >= 28);
    }

    [Fact]
    public void Characters_start_switched_off() =>
        Assert.All(AllVoices(), v => Assert.False(v.Enabled));

    private static IEnumerable<CharacterVoice> AllVoices() => CharacterVoicePack.CreateAll().Concat(TacticalAgentsPack.CreateAll());

    [Fact]
    public void Soft_limiter_is_transparent_below_knee_and_never_clips()
    {
        float[] s = [0.5f, -0.6f, 0.9f, -3f, 50f, float.NaN];
        Dsp.SoftLimit(s);
        Assert.Equal(0.5f, s[0]);
        Assert.Equal(-0.6f, s[1]);
        Assert.InRange(s[2], 0.7f, 0.98f);
        Assert.InRange(s[3], -0.98f, -0.9f);
        Assert.InRange(s[4], 0.95f, 0.98f);
        Assert.Equal(0f, s[5]);
    }

    /// <summary>Voiced syllables (harmonics + formant) with gaps, stereo, peaking around -12 dBFS.</summary>
    private static float[] Speech(int frames)
    {
        var s = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var syllable = (i / (Rate / 5)) % 3 != 2; // 200 ms on, on, off
            if (!syllable) continue;
            var t = (double)i / Rate;
            double v = 0;
            for (var h = 1; h <= 20; h++)
                v += Math.Exp(-Math.Pow((120 * h - 800) / 400.0, 2)) * Math.Sin(2 * Math.PI * 120 * h * t);
            s[i * 2] = s[i * 2 + 1] = (float)(v * 0.12);
        }
        return s;
    }

    private static float Rms(float[] s) => MathF.Sqrt(s.Sum(v => v * v) / s.Length);
    private static float Db(float x) => 20 * MathF.Log10(x + 1e-9f);
}
