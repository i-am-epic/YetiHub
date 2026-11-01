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
    public void Gate_reduces_quiet_signal_by_its_range()
    {
        var gate = new NoiseGateEffect();
        gate["threshold"].Value = -40;
        gate["range"].Value = -40;
        gate["release"].Value = 20;
        var hiss = Enumerable.Repeat(0.001f, Rate).ToArray();
        gate.Process(hiss, Rate, 2);
        Assert.InRange(hiss[^1], 0.000009f, 0.000011f); // -40 dB of 0.001
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
        foreach (var preset in Presets.All())
        foreach (var (id, state) in preset.Effects)
        foreach (var param in state.Parameters.Keys)
            Assert.Contains(chain[id].Parameters, p => p.Id == param);
    }

    [Fact]
    public void Store_seeds_presets_and_persists_edits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yetihub-test-" + Guid.NewGuid().ToString("n"));
        try
        {
            var store = new ProfileStore(dir);
            var all = store.LoadAll();
            Assert.Equal(Presets.All().Count(), all.Count);

            all[0].Name = "Renamed";
            store.Save(all[0]);
            Assert.Contains(new ProfileStore(dir).LoadAll(), p => p.Name == "Renamed");
        }
        finally { Directory.Delete(dir, true); }
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
