using YetiHub.Core.Effects;
using YetiHub.Core.Profiles;
using YetiHub.Mods.RobotVoice;

namespace YetiHub.Core.Tests;

public class KnownDevicesTests
{
    [Fact]
    public void Parses_yeti_hardware_id_with_revision()
    {
        Assert.True(KnownDevices.TryParseHardwareId(@"USB\VID_B58C&PID_9E84&REV_0100", out var vid, out var pid, out var rev));
        Assert.Equal(0xB58C, vid);
        Assert.Equal(0x9E84, pid);
        Assert.Equal("1.00", rev);
        Assert.Equal("Blue Yeti", KnownDevices.ModelName(pid));
    }

    [Fact]
    public void Parses_interface_id_without_revision()
    {
        Assert.True(KnownDevices.TryParseHardwareId(@"USB\VID_B58C&PID_9E84&MI_00", out _, out _, out var rev));
        Assert.Null(rev);
    }

    [Fact]
    public void Rejects_non_usb_ids() =>
        Assert.False(KnownDevices.TryParseHardwareId(@"SWD\MMDEVAPI\{0.0.1.00000000}", out _, out _, out _));

    [Fact]
    public void Detects_virtual_cables()
    {
        Assert.True(DeviceScanner.IsVirtualCable("CABLE Input (VB-Audio Virtual Cable)"));
        Assert.False(DeviceScanner.IsVirtualCable("Speakers (Yeti Stereo Microphone)"));
    }
}

public class DspTests
{
    [Fact]
    public void Peak_is_max_absolute_sample() => Assert.Equal(0.8f, Dsp.Peak([0.1f, -0.8f, 0.5f]));

    [Fact]
    public void Decibel_round_trip() => Assert.Equal(0.5f, Dsp.FromDecibels(Dsp.ToDecibels(0.5f)), 4);
}

public class EffectTests
{
    private const int Rate = 48000;

    private static float[] Sine(float freq, float amp, int frames = Rate / 2) =>
        Enumerable.Range(0, frames * 2).Select(i => amp * MathF.Sin(2 * MathF.PI * freq * (i / 2) / Rate)).ToArray();

    private static float Peak(float[] s, int skip) => s.Skip(skip).Max(MathF.Abs);

    [Fact]
    public void Low_cut_removes_rumble_and_keeps_voice()
    {
        var fx = new LowCutEffect { Enabled = true };
        var rumble = Sine(20, 0.5f);
        fx.Process(rumble, Rate, 2);
        Assert.True(Peak(rumble, 9600) < 0.05f);

        var voice = Sine(1000, 0.5f);
        fx.Process(voice, Rate, 2);
        Assert.True(Peak(voice, 9600) > 0.48f);
    }

    [Fact]
    public void Gate_turns_steady_noise_down_by_its_range()
    {
        var gate = new NoiseGateEffect();
        gate["range"].Value = -40;
        gate["release"].Value = 20;
        gate["hold"].Value = 20;
        var rng = new Random(3);
        var hiss = Enumerable.Range(0, Rate * 4).Select(_ => (float)(rng.NextDouble() - 0.5) * 0.002f).ToArray();
        var before = hiss.Skip(hiss.Length - 4800).Select(MathF.Abs).Average();
        gate.Process(hiss, Rate, 2);
        var after = hiss.Skip(hiss.Length - 4800).Select(MathF.Abs).Average();
        Assert.InRange(20 * MathF.Log10(after / before), -42f, -36f);
        Assert.False(gate.IsOpen);
    }

    [Fact]
    public void Compressor_reduces_loud_signal()
    {
        var comp = new CompressorEffect();
        comp["threshold"].Value = -20;
        comp["ratio"].Value = 4;
        comp["makeup"].Value = 0;
        var loud = Sine(440, 1f);
        comp.Process(loud, Rate, 2);
        // 0 dB in, -20 threshold, 4:1 → about -15 dB out (0.18).
        Assert.InRange(Peak(loud, loud.Length / 2), 0.12f, 0.25f);
        Assert.True(comp.GainReductionDb < -10);
    }

    [Fact]
    public void Limiter_never_exceeds_ceiling()
    {
        var lim = new LimiterEffect();
        lim["ceiling"].Value = -6;
        var hot = Sine(440, 2f);
        lim.Process(hot, Rate, 2);
        Assert.True(Peak(hot, 0) <= Dsp.FromDecibels(-6) + 1e-5f);
    }

    [Fact]
    public void Eq_boost_raises_presence_band()
    {
        var eq = new VoiceEqEffect();
        eq["presence"].Value = 6;
        var s = Sine(3500, 0.25f);
        eq.Process(s, Rate, 2);
        Assert.InRange(Peak(s, 9600), 0.45f, 0.55f); // +6 dB ≈ ×2
    }

    [Fact]
    public void Parameters_are_clamped()
    {
        var fx = new LowCutEffect();
        fx["frequency"].Value = 5000;
        Assert.Equal(300, fx["frequency"].Value);
    }
}

public class ProfileTests
{
    [Fact]
    public void Chain_state_round_trips_through_a_profile()
    {
        var chain = new EffectChain();
        chain["compressor"].Enabled = true;
        chain["compressor"]["ratio"].Value = 6;

        var profile = new Profile { Effects = chain.CaptureState() };
        var other = new EffectChain();
        other.ApplyState(profile.Clone("copy").Effects);

        Assert.True(other["compressor"].Enabled);
        Assert.Equal(6, other["compressor"]["ratio"].Value);
        Assert.False(other["gate"].Enabled);
    }

    [Fact]
    public void Missing_effects_reset_to_default_and_off()
    {
        var chain = new EffectChain();
        chain["eq"].Enabled = true;
        chain["eq"]["bass"].Value = 9;
        chain.ApplyState(new Dictionary<string, EffectState>());
        Assert.False(chain["eq"].Enabled);
        Assert.Equal(0, chain["eq"]["bass"].Value);
    }

    [Fact]
    public void Presets_only_reference_real_effects_and_parameters()
    {
        var chain = new EffectChain();
        foreach (var preset in Presets.All)
        foreach (var (id, state) in preset.Effects)
        foreach (var param in state.Parameters.Keys)
            Assert.Contains(chain[id].Parameters, p => p.Id == param);
    }

    [Fact]
    public void Store_keeps_user_profiles_and_drops_legacy_seeds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yetihub-test-" + Guid.NewGuid().ToString("n"));
        try
        {
            var store = new ProfileStore(dir);
            Assert.Empty(store.LoadUserProfiles());

            store.Save(new Profile { Id = "broadcast", Name = "old v0.1.0 seed" });
            var mine = Presets.All[0].Clone("Mine");
            store.Save(mine);

            var loaded = new ProfileStore(dir).LoadUserProfiles();
            Assert.Single(loaded);
            Assert.Equal("Mine", loaded[0].Name);
            Assert.False(loaded[0].IsBuiltIn);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Standard_profiles_are_read_only_but_duplicates_are_not()
    {
        var store = new ProfileStore(Path.GetTempPath());
        Assert.All(Presets.All, p => Assert.True(p.IsBuiltIn));
        Assert.Throws<InvalidOperationException>(() => store.Save(Presets.All[0]));
        Assert.False(Presets.All[0].Clone("copy").IsBuiltIn);
    }

    [Fact]
    public void Standard_profile_ids_are_unique() =>
        Assert.Equal(Presets.All.Count, Presets.All.Select(p => p.Id).Distinct().Count());
}

public class MoreEffectTests
{
    private const int Rate = 48000;

    [Fact]
    public void Noise_reduction_with_no_reduction_reconstructs_the_input()
    {
        var nr = new NoiseReductionEffect();
        nr["amount"].Value = 0; // floor = 1: every bin passes untouched
        var input = Enumerable.Range(0, 9600).Select(i => 0.3f * MathF.Sin(i * 0.05f + (i % 2) * 0.3f)).ToArray();
        var output = (float[])input.Clone();
        nr.Process(output, Rate, 2);

        const int delay = 256 * 2; // one 256-sample window, stereo interleaved
        for (var i = 2000; i < input.Length; i++)
            Assert.Equal(input[i - delay], output[i], 1e-4f);
    }

    [Fact]
    public void Noise_reduction_suppresses_steady_hiss()
    {
        var nr = new NoiseReductionEffect();
        nr["amount"].Value = 30;
        nr["strength"].Value = 100;
        var rng = new Random(1);
        var hiss = Enumerable.Range(0, Rate * 2).Select(_ => (float)(rng.NextDouble() - 0.5) * 0.02f).ToArray();
        var before = Rms(hiss.AsSpan(hiss.Length / 2));
        nr.Process(hiss, Rate, 2);
        Assert.True(Rms(hiss.AsSpan(hiss.Length / 2)) < before * 0.3f);
    }

    [Fact]
    public void Noise_reduction_keeps_a_tone_above_the_noise()
    {
        var nr = new NoiseReductionEffect();
        nr["amount"].Value = 30;
        nr["strength"].Value = 100;
        var rng = new Random(2);
        // 1 s of room hiss (what the denoiser learns), then a voice-like tone for 0.5 s on top of it.
        var mix = Enumerable.Range(0, Rate * 3)
            .Select(i => (i >= Rate * 2 ? 0.2f * MathF.Sin(2 * MathF.PI * 1000 * (i / 2) / Rate) : 0f) + (float)(rng.NextDouble() - 0.5) * 0.01f)
            .ToArray();
        nr.Process(mix, Rate, 2);
        var db = 20 * MathF.Log10(Rms(mix.AsSpan(mix.Length - Rate / 2)) / (0.2f / MathF.Sqrt(2)));
        Assert.InRange(db, -1.5f, 0.5f);
    }

    [Fact]
    public void Auto_level_raises_quiet_speech_toward_target()
    {
        var lvl = new AutoLevelEffect();
        lvl["target"].Value = -18;
        lvl["maxgain"].Value = 12;
        lvl["speed"].Value = 200;
        var quiet = Enumerable.Range(0, Rate * 4).Select(i => 0.02f * MathF.Sin(i * 0.06f)).ToArray(); // ≈ -37 dB RMS
        lvl.Process(quiet, Rate, 2);
        var db = 20 * MathF.Log10(Rms(quiet.AsSpan(quiet.Length - 4800)));
        Assert.InRange(db, -29, -23); // boosted by ~max gain (12 dB)
    }

    [Fact]
    public void Mono_centres_a_one_sided_voice()
    {
        var mono = new MonoEffect();
        float[] s = [1f, 0f, 1f, 0f];
        mono.Process(s, Rate, 2);
        Assert.Equal(s[0], s[1], 5);
    }

    [Fact]
    public void Every_effect_survives_silence_and_full_scale()
    {
        foreach (var fx in new EffectChain().Effects)
        {
            fx.Enabled = true;
            var s = Enumerable.Range(0, 9600).Select(i => i % 2400 < 1200 ? 0f : MathF.Sin(i * 0.1f)).ToArray();
            fx.Process(s, Rate, 2);
            Assert.All(s, v => Assert.True(float.IsFinite(v) && MathF.Abs(v) < 8, $"{fx.Name} produced {v}"));
        }
    }

    private static float Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (var v in s) sum += v * v;
        return (float)Math.Sqrt(sum / s.Length);
    }
}

public class RobotVoiceTests
{
    [Fact]
    public void Off_by_default_and_modulates_when_on()
    {
        var robot = new RobotVoiceProcessor();
        Assert.False(robot.Enabled);

        var s = Enumerable.Repeat(0.5f, 4800).ToArray();
        robot.Process(s, 48000, 2);
        Assert.True(s.Min() < 0); // a constant signal times a sine carrier swings negative
    }
}
