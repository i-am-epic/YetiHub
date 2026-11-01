namespace YetiHub.Mods.CharacterVoices;

/// <summary>
/// Original "tactical agent" archetypes for team shooters: comms-ready voices with their own sound design.
/// (Original characters; not based on any game's cast or voice actors.)
/// </summary>
public sealed class TacticalAgentsPack : IYetiMod
{
    public string Id => "yetihub.tacticalagents";
    public string Name => "Tactical Agents Pack";
    public string Description => "10 original agent voices for team shooters: shadow operative, toxin specialist, recon drone and more.";
    public Version Version => new(1, 0, 0);

    public static IReadOnlyList<CharacterVoice> CreateAll() =>
    [
        new ShadowOperative(), new ToxinSpecialist(), new CombatAndroid(), new FieldMedic(), new ReconDrone(),
        new Demolitions(), new CommandHq(), new CryoAgent(), new StormDuelist(), new SentinelAi(),
    ];

    public void OnLoad(IModContext context)
    {
        foreach (var agent in CreateAll())
            context.RegisterProcessor(agent);
        context.Log("10 tactical agents ready");
    }

    public void OnUnload() { }
}

/// <summary>A voice layered with a darker copy of itself and a faint whisper, fading into shadow.</summary>
internal sealed class ShadowOperative : CharacterVoice
{
    private Shifter _dark = null!;
    private Filter _lp = null!, _whisperBand = null!;
    private Reverb _shadow = null!;
    private SpeechDetector _speech = null!;
    private readonly Random _rng = new(21);
    private float _env;
    public override string Name => "Shadow Operative";
    public override string Emoji => "🥷";
    public override string Description => "Agent: a doubled, darker voice with a whisper underneath, swallowed by shadow.";
    protected override void Prepare(int sr)
    {
        _dark = new(sr); _lp = Filter.LowPass(sr, 3200); _whisperBand = Filter.BandPass(sr, 3000, 0.7f);
        _shadow = new Reverb(sr, 0.75f, 0.75f); _speech = new SpeechDetector(sr);
    }
    protected override float Voice(float x)
    {
        _speech.Run(x);
        _env += (MathF.Abs(x) - _env) * 0.01f;
        var whisper = _whisperBand.Run((float)(_rng.NextDouble() * 2 - 1)) * _env * 0.9f;
        var y = _lp.Run(x * 0.6f + _dark.Run(x, Shifter.Ratio(-5)) * 0.6f) + whisper;
        return y * 0.8f + _shadow.Run(y) * 0.35f;
    }
}

/// <summary>Talking through a sealed respirator: boxy chamber resonance and filtered breath.</summary>
internal sealed class ToxinSpecialist : CharacterVoice
{
    private Filter _hp = null!, _lp = null!, _chamber = null!, _breath = null!;
    private Delay _mask = null!;
    private readonly Random _rng = new(33);
    private int _maskDelay;
    private float _env;
    public override string Name => "Toxin Specialist";
    public override string Emoji => "☣️";
    public override string Description => "Agent: muffled voice inside a sealed gas mask, with a hiss of filtered breath.";
    protected override void Prepare(int sr)
    {
        _hp = Filter.HighPass(sr, 300); _lp = Filter.LowPass(sr, 2600); _chamber = Filter.Peak(sr, 900, 2.5f, 9);
        _breath = Filter.BandPass(sr, 1800, 0.6f); _maskDelay = sr * 12 / 10000; _mask = new Delay(_maskDelay + 4);
    }
    protected override float Voice(float x)
    {
        _env += (MathF.Abs(x) - _env) * 0.004f;
        var y = _chamber.Run(_lp.Run(_hp.Run(x)));
        y += _mask.Read(_maskDelay) * 0.5f;
        _mask.Write(y);
        var breath = _breath.Run((float)(_rng.NextDouble() * 2 - 1)) * (0.004f + _env * 0.25f);
        return Shape.Soft(y * 1.8f) * 0.6f + breath;
    }
}

/// <summary>A battle-hardened machine: metallic resonance, servo buzz and a slightly digital edge.</summary>
internal sealed class CombatAndroid : CharacterVoice
{
    private Delay _metal = null!;
    private Osc _servo = null!;
    private Filter _hp = null!;
    private int _metalDelay;
    public override string Name => "Combat Android";
    public override string Emoji => "⚙️";
    public override string Description => "Agent: metal chassis resonance with a humming servo core and a digital edge.";
    protected override void Prepare(int sr) { _metalDelay = sr * 4 / 1000; _metal = new Delay(_metalDelay + 4); _servo = new Osc(sr, 140); _hp = Filter.HighPass(sr, 150); }
    protected override float Voice(float x)
    {
        var y = x + _metal.Read(_metalDelay) * 0.65f;
        _metal.Write(y);
        y = y * 0.6f + y * _servo.Run() * 0.4f;
        return Shape.Crush(_hp.Run(y) * 0.8f, 10);
    }
}

/// <summary>Calm, reassuring healer with a soft halo of light around the voice.</summary>
internal sealed class FieldMedic : CharacterVoice
{
    private Shifter _halo = null!;
    private Filter _warm = null!, _hp = null!;
    private Reverb _space = null!;
    public override string Name => "Field Medic";
    public override string Emoji => "🩺";
    public override string Description => "Agent: warm and reassuring, with a soft glowing halo around every word.";
    protected override void Prepare(int sr) { _halo = new(sr); _warm = Filter.LowShelf(sr, 250, 3); _hp = Filter.HighPass(sr, 90); _space = new Reverb(sr, 0.6f, 0.5f); }
    protected override float Voice(float x)
    {
        var y = _hp.Run(_warm.Run(x)) + _halo.Run(x, 2.0) * 0.15f;
        return y * 0.85f + _space.Run(y) * 0.25f;
    }
}

/// <summary>A small flying scout: tiny speaker, rotor buzz and a radio ring.</summary>
internal sealed class ReconDrone : CharacterVoice
{
    private Shifter _up = null!;
    private Filter _hp = null!, _lp = null!;
    private Osc _rotor = null!, _ring = null!;
    public override string Name => "Recon Drone";
    public override string Emoji => "🛰️";
    public override string Description => "Agent: a tiny flying scout with buzzing rotors and a thin radio speaker.";
    protected override void Prepare(int sr) { _up = new(sr); _hp = Filter.HighPass(sr, 700); _lp = Filter.LowPass(sr, 5000); _rotor = new Osc(sr, 32); _ring = new Osc(sr, 2000); }
    protected override float Voice(float x)
    {
        var y = _lp.Run(_hp.Run(_up.Run(x, Shifter.Ratio(5))));
        y *= 0.75f + 0.25f * _rotor.Run();
        return Shape.Soft((y + y * _ring.Run() * 0.2f) * 2.2f) * 0.55f;
    }
}

/// <summary>Loud, gritty explosives expert who has been near too many blasts.</summary>
internal sealed class Demolitions : CharacterVoice
{
    private Shifter _down = null!;
    private Filter _hp = null!, _bite = null!;
    public override string Name => "Demolitions";
    public override string Emoji => "💥";
    public override string Description => "Agent: rough, shouty and overdriven, like an explosives expert mid-fight.";
    protected override void Prepare(int sr) { _down = new(sr); _hp = Filter.HighPass(sr, 150); _bite = Filter.Peak(sr, 2500, 1, 6); }
    protected override float Voice(float x) => Shape.Soft(_bite.Run(_hp.Run(_down.Run(x, Shifter.Ratio(-2)))) * 7f) * 0.5f;
}

/// <summary>Clean headquarters comms: tight radio band, a click when you key up and a squelch when you release.</summary>
internal sealed class CommandHq : CharacterVoice
{
    private Filter _hp = null!, _lp = null!, _squelch = null!;
    private SpeechDetector _speech = null!;
    private readonly Random _rng = new(44);
    private int _clickLeft, _squelchLeft, _squelchLength;
    public override string Name => "Command HQ";
    public override string Emoji => "🎖️";
    public override string Description => "Agent: crisp headquarters radio with a key-up click and a squelch tail.";
    protected override void Prepare(int sr)
    {
        _hp = Filter.HighPass(sr, 400); _lp = Filter.LowPass(sr, 3500); _squelch = Filter.BandPass(sr, 3000, 1);
        _speech = new SpeechDetector(sr); _squelchLength = sr / 10;
    }
    protected override float Voice(float x)
    {
        switch (_speech.Run(x))
        {
            case 1: _clickLeft = 60; break;
            case -1: _squelchLeft = _squelchLength; break;
        }
        var y = Shape.Soft(_lp.Run(_hp.Run(x)) * 3f) * 0.5f;
        if (_clickLeft > 0) { _clickLeft--; y += (_clickLeft % 20 < 10 ? 0.25f : -0.25f) * _clickLeft / 60f; }
        if (_squelchLeft > 0) { _squelchLeft--; y += _squelch.Run((float)(_rng.NextDouble() * 2 - 1)) * 0.35f * _squelchLeft / _squelchLength; }
        return y;
    }
}

/// <summary>Icy, crystalline voice with a shimmering frost halo.</summary>
internal sealed class CryoAgent : CharacterVoice
{
    private Shifter _up = null!, _shimmer = null!;
    private Filter _hp = null!, _ice = null!;
    private Reverb _frost = null!;
    public override string Name => "Cryo Agent";
    public override string Emoji => "❄️";
    public override string Description => "Agent: crystalline and cold, with a shimmering octave of frost.";
    protected override void Prepare(int sr) { _up = new(sr); _shimmer = new(sr); _hp = Filter.HighPass(sr, 250); _ice = Filter.Peak(sr, 7000, 0.8f, 6); _frost = new Reverb(sr, 0.85f, 0.15f); }
    protected override float Voice(float x)
    {
        var y = _ice.Run(_hp.Run(_up.Run(x, Shifter.Ratio(2))));
        var sparkle = _shimmer.Run(y, 2.0) * 0.3f;
        return y * 0.75f + _frost.Run(y * 0.5f + sparkle) * 0.4f;
    }
}

/// <summary>Hyped-up duelist: doubled, electrified and bright.</summary>
internal sealed class StormDuelist : CharacterVoice
{
    private Shifter _left = null!, _right = null!;
    private Filter _hp = null!, _spark = null!;
    private Osc _crackle = null!;
    public override string Name => "Storm Duelist";
    public override string Emoji => "⚡";
    public override string Description => "Agent: energetic and electric, a doubled voice crackling with static.";
    protected override void Prepare(int sr) { _left = new(sr); _right = new(sr); _hp = Filter.HighPass(sr, 120); _spark = Filter.Peak(sr, 4500, 1, 5); _crackle = new Osc(sr, 55); }
    protected override float Voice(float x)
    {
        var doubled = x * 0.6f + _left.Run(x, Shifter.Ratio(0.15)) * 0.35f + _right.Run(x, Shifter.Ratio(-0.15)) * 0.35f;
        var y = _spark.Run(_hp.Run(doubled));
        return Shape.Soft(y * (1.6f + 0.25f * _crackle.Run())) * 0.65f;
    }
}

/// <summary>Synthetic tactical AI: smooth, slightly quantised and resonant.</summary>
internal sealed class SentinelAi : CharacterVoice
{
    private Shifter _a = null!, _b = null!;
    private Filter _formant = null!, _hp = null!;
    private Osc _hum = null!;
    public override string Name => "Sentinel AI";
    public override string Emoji => "🧠";
    public override string Description => "Agent: a calm synthetic intelligence, smooth and resonant with a digital sheen.";
    protected override void Prepare(int sr) { _a = new(sr); _b = new(sr); _formant = Filter.Peak(sr, 2800, 2, 7); _hp = Filter.HighPass(sr, 180); _hum = new Osc(sr, 80); }
    protected override float Voice(float x)
    {
        var synth = _a.Run(x, Shifter.Ratio(1)) * 0.5f + _b.Run(x, Shifter.Ratio(1.1)) * 0.5f;
        var y = _formant.Run(_hp.Run(synth));
        return Shape.Crush(y * 0.8f + y * _hum.Run() * 0.1f, 11);
    }
}
