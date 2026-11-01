namespace YetiHub.Mods.RobotVoice;

/// <summary>
/// Sample mod: a ring modulator that makes you sound like a robot. Off by default; switch it on in the Mods tab.
/// </summary>
public sealed class RobotVoiceMod : IYetiMod
{
    public string Id => "yetihub.robotvoice";
    public string Name => "Robot Voice";
    public string Description => "Ring-modulator voice changer. A fun example of a real-time audio mod.";
    public Version Version => new(1, 0, 0);

    public void OnLoad(IModContext context) => context.RegisterProcessor(new RobotVoiceProcessor());

    public void OnUnload() { }
}

public sealed class RobotVoiceProcessor : IAudioProcessor
{
    private double _phase;

    public string Name => "Robot Voice";
    public bool Enabled { get; set; }

    /// <summary>Carrier frequency; 30–80 Hz sounds robotic, higher sounds metallic.</summary>
    public double CarrierHz { get; set; } = 50;

    /// <summary>0 = dry, 1 = fully modulated.</summary>
    public float Mix { get; set; } = 0.85f;

    public void Process(Span<float> samples, int sampleRate, int channels)
    {
        var step = 2 * Math.PI * CarrierHz / sampleRate;
        for (var frame = 0; frame + channels <= samples.Length; frame += channels)
        {
            var carrier = (float)Math.Sin(_phase);
            _phase += step;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;

            for (var c = 0; c < channels; c++)
            {
                var dry = samples[frame + c];
                samples[frame + c] = dry * (1 - Mix) + dry * carrier * Mix;
            }
        }
    }
}
