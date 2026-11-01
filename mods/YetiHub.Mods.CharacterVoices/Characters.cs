namespace YetiHub.Mods.CharacterVoices;

/// <summary>Registers every character with YetiHub. All start off; pick one in the Mods tab.</summary>
public sealed class CharacterVoicePack : IYetiMod
{
    public string Id => "yetihub.charactervoices";
    public string Name => "Character Voice Pack";
    public string Description => "17 character voices: chipmunk, giant, demon, dark lord, alien, ghost, astronaut, radio and more.";
    public Version Version => new(1, 0, 0);

    public static IReadOnlyList<CharacterVoice> CreateAll() =>
    [
        new Chipmunk(), new Giant(), new Demon(), new DarkLord(), new Alien(), new Ghost(),
        new Astronaut(), new WalkieTalkie(), new OldRadio(), new Telephone(), new Megaphone(),
        new Underwater(), new Canyon(), new RetroGame(), new Angel(), new Goblin(), new Cyborg(),
    ];

    public void OnLoad(IModContext context)
    {
        foreach (var voice in CreateAll())
            context.RegisterProcessor(voice);
        context.Log("17 character voices ready");
    }

    public void OnUnload() { }
}

/// <summary>
/// Base for a character: processes the (mono-summed) voice once per frame and writes it to every channel.
/// Characters rebuild their DSP whenever the sample rate changes.
/// </summary>
public abstract class CharacterVoice : IAudioProcessor, IProcessorInfo
{
    private int _sampleRate;

    public abstract string Name { get; }
    public abstract string Emoji { get; }
    public abstract string Description { get; }
    public string Group => IProcessorInfo.CharacterGroup;
    public bool Enabled { get; set; }

    public void Process(Span<float> samples, int sampleRate, int channels)
    {
        if (sampleRate != _sampleRate)
        {
            _sampleRate = sampleRate;
            Prepare(sampleRate);
        }
        for (var f = 0; f + channels <= samples.Length; f += channels)
        {
            var mono = 0f;
            for (var c = 0; c < channels; c++) mono += samples[f + c];
            // Keep characters from ever clipping, even with heavy drive.
            var y = Math.Clamp(Voice(mono / channels), -0.98f, 0.98f);
            for (var c = 0; c < channels; c++) samples[f + c] = y;
        }
    }

    protected abstract void Prepare(int sampleRate);
    protected abstract float Voice(float x);
}

// ---------------------------------------------------------------- pitch characters

internal sealed class Chipmunk : CharacterVoice
{
    private Shifter _up = null!;
    private Filter _hp = null!, _air = null!;
    public override string Name => "Chipmunk";
    public override string Emoji => "🐿️";
    public override string Description => "Squeaky and hyper: seven semitones up.";
    protected override void Prepare(int sr) { _up = new(sr); _hp = Filter.HighPass(sr, 180); _air = Filter.Peak(sr, 5000, 0.8f, 3); }
    protected override float Voice(float x) => _air.Run(_hp.Run(_up.Run(x, Shifter.Ratio(7))));
}

internal sealed class Giant : CharacterVoice
{
    private Shifter _down = null!;
    private Filter _boom = null!, _lp = null!;
    public override string Name => "Giant";
    public override string Emoji => "🗿";
    public override string Description => "A slow, enormous voice ten semitones down.";
    protected override void Prepare(int sr) { _down = new(sr); _boom = Filter.LowShelf(sr, 160, 6); _lp = Filter.LowPass(sr, 5500); }
    protected override float Voice(float x) => _lp.Run(_boom.Run(_down.Run(x, Shifter.Ratio(-10)))) * 0.9f;
}

internal sealed class Demon : CharacterVoice
{
    private Shifter _low = null!, _lower = null!;
    private Filter _rumble = null!, _hp = null!;
    private Reverb _hell = null!;
    public override string Name => "Demon";
    public override string Emoji => "😈";
    public override string Description => "Layered, distorted voice from below, with a dark echo.";
    protected override void Prepare(int sr)
    {
        _low = new(sr); _lower = new(sr);
        _rumble = Filter.LowShelf(sr, 120, 7); _hp = Filter.HighPass(sr, 45);
        _hell = new Reverb(sr, 0.6f, 0.7f);
    }
    protected override float Voice(float x)
    {
        var layered = _low.Run(x, Shifter.Ratio(-7)) * 0.7f + _lower.Run(x, Shifter.Ratio(-12)) * 0.5f;
        var grit = Shape.Soft(layered * 3.5f) * 0.5f;
        var y = _hp.Run(_rumble.Run(grit));
        return y + _hell.Run(y) * 0.25f;
    }
}

internal sealed class DarkLord : CharacterVoice
{
    private Shifter _down = null!;
    private Delay _helmet = null!;
    private Filter _chest = null!, _lp = null!;
    private int _helmetDelay;
    public override string Name => "Dark Lord";
    public override string Emoji => "🌑";
    public override string Description => "Deep, commanding voice echoing inside a metal helmet.";
    protected override void Prepare(int sr)
    {
        _down = new(sr); _helmetDelay = sr * 22 / 10000; // 2.2 ms resonance
        _helmet = new Delay(_helmetDelay + 4); _chest = Filter.LowShelf(sr, 200, 6); _lp = Filter.LowPass(sr, 6000);
    }
    protected override float Voice(float x)
    {
        var deep = _down.Run(x, Shifter.Ratio(-4));
        var y = deep + _helmet.Read(_helmetDelay) * 0.55f;
        _helmet.Write(y);
        return Shape.Soft(_lp.Run(_chest.Run(y)) * 1.6f) * 0.7f;
    }
}

internal sealed class Alien : CharacterVoice
{
    private Shifter _shift = null!;
    private Osc _wobble = null!, _ring = null!;
    private Filter _hp = null!;
    public override string Name => "Alien";
    public override string Emoji => "👽";
    public override string Description => "Wobbly, otherworldly voice with a shimmering ring.";
    protected override void Prepare(int sr) { _shift = new(sr); _wobble = new(sr, 6.5); _ring = new(sr, 1200); _hp = Filter.HighPass(sr, 200); }
    protected override float Voice(float x)
    {
        var cents = 45 * _wobble.Run();
        var y = _shift.Run(x, Shifter.Ratio(3 + cents / 100.0));
        return _hp.Run(y * 0.8f + y * _ring.Run() * 0.35f);
    }
}

internal sealed class Goblin : CharacterVoice
{
    private Shifter _up = null!;
    private Filter _hp = null!, _nasal = null!;
    public override string Name => "Goblin";
    public override string Emoji => "🧌";
    public override string Description => "Nasal, sneaky little creature voice.";
    protected override void Prepare(int sr) { _up = new(sr); _hp = Filter.HighPass(sr, 400); _nasal = Filter.Peak(sr, 1300, 1.4f, 10); }
    protected override float Voice(float x) => Shape.Soft(_nasal.Run(_hp.Run(_up.Run(x, Shifter.Ratio(4)))) * 1.4f) * 0.75f;
}

internal sealed class Angel : CharacterVoice
{
    private Shifter _octave = null!, _fifth = null!;
    private Reverb _heaven = null!;
    private Filter _hp = null!;
    public override string Name => "Angel Choir";
    public override string Emoji => "😇";
    public override string Description => "Your voice plus an octave and a fifth, floating in a cathedral.";
    protected override void Prepare(int sr) { _octave = new(sr); _fifth = new(sr); _heaven = new Reverb(sr, 0.92f, 0.2f); _hp = Filter.HighPass(sr, 150); }
    protected override float Voice(float x)
    {
        var choir = x * 0.6f + _octave.Run(x, 2.0) * 0.35f + _fifth.Run(x, Shifter.Ratio(7)) * 0.3f;
        var y = _hp.Run(choir);
        return y * 0.75f + _heaven.Run(y) * 0.45f;
    }
}

// ---------------------------------------------------------------- space & atmosphere

internal sealed class Ghost : CharacterVoice
{
    private Shifter _shift = null!;
    private Osc _tremolo = null!;
    private Filter _hp = null!;
    private Reverb _haunt = null!;
    public override string Name => "Ghost";
    public override string Emoji => "👻";
    public override string Description => "Thin, trembling voice fading into a haunted hall.";
    protected override void Prepare(int sr) { _shift = new(sr); _tremolo = new(sr, 4); _hp = Filter.HighPass(sr, 300); _haunt = new Reverb(sr, 0.9f, 0.5f); }
    protected override float Voice(float x)
    {
        var y = _hp.Run(_shift.Run(x, Shifter.Ratio(1.5))) * (0.65f + 0.35f * _tremolo.Run());
        return y * 0.55f + _haunt.Run(y) * 0.7f;
    }
}

internal sealed class Underwater : CharacterVoice
{
    private Filter _lp = null!;
    private Osc _wobble = null!, _pitchLfo = null!;
    private Shifter _shift = null!;
    private int _sr;
    public override string Name => "Underwater";
    public override string Emoji => "🌊";
    public override string Description => "Muffled and wobbly, like talking at the bottom of a pool.";
    protected override void Prepare(int sr) { _sr = sr; _lp = Filter.LowPass(sr, 500, 1.2f); _wobble = new(sr, 0.7); _pitchLfo = new(sr, 3.3); _shift = new(sr); }
    protected override float Voice(float x)
    {
        _lp.Set(_sr, 500 + 220 * _wobble.Run(), 1.2f, Filter.Kind.Low);
        return _lp.Run(_shift.Run(x, Shifter.Ratio(0.35 * _pitchLfo.Run()))) * 1.5f;
    }
}

internal sealed class Canyon : CharacterVoice
{
    private Delay _echo = null!;
    private Filter _tone = null!;
    private int _delay;
    public override string Name => "Canyon Echo";
    public override string Emoji => "🏔️";
    public override string Description => "Shout across a canyon and hear it come back... back... back.";
    protected override void Prepare(int sr) { _delay = sr * 30 / 100; _echo = new Delay(_delay + 4); _tone = Filter.LowPass(sr, 3500); }
    protected override float Voice(float x)
    {
        var back = _echo.Read(_delay);
        _echo.Write(x + _tone.Run(back) * 0.5f);
        return x * 0.8f + back * 0.6f;
    }
}

internal sealed class Astronaut : CharacterVoice
{
    private Filter _hp = null!, _lp = null!;
    private Delay _helmet = null!;
    private Osc _beep = null!;
    private SpeechDetector _speech = null!;
    private int _beepLeft, _beepLength, _helmetDelay;
    public override string Name => "Astronaut";
    public override string Emoji => "🧑‍🚀";
    public override string Description => "Helmet radio with the classic mission-control beeps when you start and stop talking.";
    protected override void Prepare(int sr)
    {
        _hp = Filter.HighPass(sr, 400); _lp = Filter.LowPass(sr, 3200);
        _helmetDelay = sr * 3 / 1000; _helmet = new Delay(_helmetDelay + 4);
        _beep = new Osc(sr, 2525); _speech = new SpeechDetector(sr); _beepLength = sr / 4;
    }
    protected override float Voice(float x)
    {
        // Mission-control style tones: 2525 Hz when the mic opens, 2475 Hz when it closes.
        switch (_speech.Run(x))
        {
            case 1: _beep.Hz = 2525; _beepLeft = _beepLength; break;
            case -1: _beep.Hz = 2475; _beepLeft = _beepLength; break;
        }
        var y = _hp.Run(_lp.Run(x));
        y += _helmet.Read(_helmetDelay) * 0.4f;
        _helmet.Write(y);
        y = Shape.Soft(y * 2f) * 0.55f;
        if (_beepLeft > 0) { _beepLeft--; y += _beep.Run() * 0.18f; }
        return y;
    }
}

// ---------------------------------------------------------------- devices

internal sealed class WalkieTalkie : CharacterVoice
{
    private Filter _hp = null!, _lp = null!, _squelchTone = null!;
    private SpeechDetector _speech = null!;
    private readonly Random _rng = new(5);
    private int _squelchLeft, _squelchLength;
    public override string Name => "Walkie-Talkie";
    public override string Emoji => "📟";
    public override string Description => "Crunchy handheld radio, with a static burst when you release. Over.";
    protected override void Prepare(int sr)
    {
        _hp = Filter.HighPass(sr, 500); _lp = Filter.LowPass(sr, 2800);
        _squelchTone = Filter.BandPass(sr, 2500, 0.8f);
        _speech = new SpeechDetector(sr); _squelchLength = sr * 18 / 100;
    }
    protected override float Voice(float x)
    {
        if (_speech.Run(x) == -1) _squelchLeft = _squelchLength;
        var y = Shape.Hard(_lp.Run(_hp.Run(x)) * 4f, 0.5f);
        if (_squelchLeft > 0)
        {
            _squelchLeft--;
            y += _squelchTone.Run((float)(_rng.NextDouble() * 2 - 1)) * 0.5f;
        }
        return y;
    }
}

internal sealed class OldRadio : CharacterVoice
{
    private Filter _hp = null!, _lp = null!, _body = null!;
    private Osc _hum = null!;
    private readonly Random _rng = new(9);
    public override string Name => "1940s Radio";
    public override string Emoji => "📻";
    public override string Description => "Warm, crackly AM broadcast from the golden age of radio.";
    protected override void Prepare(int sr) { _hp = Filter.HighPass(sr, 300); _lp = Filter.LowPass(sr, 3000); _body = Filter.Peak(sr, 1000, 0.9f, 4); _hum = new Osc(sr, 60); }
    protected override float Voice(float x)
    {
        var y = Shape.Soft(_body.Run(_lp.Run(_hp.Run(x))) * 2.5f) * 0.6f;
        var crackle = _rng.NextDouble() < 0.0004 ? (float)(_rng.NextDouble() - 0.5) * 0.4f : 0f;
        var hiss = (float)(_rng.NextDouble() - 0.5) * 0.006f;
        return y + crackle + hiss + _hum.Run() * 0.004f;
    }
}

internal sealed class Telephone : CharacterVoice
{
    private Filter _hp = null!, _lp = null!;
    private float _held;
    private int _counter, _divide;
    public override string Name => "Telephone";
    public override string Emoji => "☎️";
    public override string Description => "Narrow, 8 kHz landline sound.";
    protected override void Prepare(int sr) { _hp = Filter.HighPass(sr, 300); _lp = Filter.LowPass(sr, 3400); _divide = Math.Max(1, sr / 8000); }
    protected override float Voice(float x)
    {
        var y = _lp.Run(_hp.Run(x));
        if (_counter++ % _divide == 0) _held = Shape.Crush(Shape.Soft(y * 1.8f), 8);
        return _held * 0.8f;
    }
}

internal sealed class Megaphone : CharacterVoice
{
    private Filter _hp = null!, _lp = null!, _horn = null!;
    public override string Name => "Megaphone";
    public override string Emoji => "📣";
    public override string Description => "Loud, harsh bullhorn. Attention please!";
    protected override void Prepare(int sr) { _hp = Filter.HighPass(sr, 600); _lp = Filter.LowPass(sr, 4500); _horn = Filter.Peak(sr, 2000, 1.2f, 8); }
    protected override float Voice(float x) => Shape.Hard(_horn.Run(_lp.Run(_hp.Run(x))) * 5f, 0.45f);
}

internal sealed class RetroGame : CharacterVoice
{
    private Shifter _up = null!;
    private float _held;
    private int _counter, _divide;
    public override string Name => "8-Bit Hero";
    public override string Emoji => "👾";
    public override string Description => "Crunchy retro console voice: 6 kHz, 5-bit.";
    protected override void Prepare(int sr) { _up = new(sr); _divide = Math.Max(1, sr / 6000); }
    protected override float Voice(float x)
    {
        if (_counter++ % _divide == 0) _held = Shape.Crush(_up.Run(x, Shifter.Ratio(2)) * 1.5f, 5);
        else _up.Run(x, Shifter.Ratio(2)); // keep the shifter's clock running
        return _held * 0.7f;
    }
}

internal sealed class Cyborg : CharacterVoice
{
    private Shifter _down = null!;
    private Delay _metal = null!;
    private Osc _ring = null!;
    private int _metalDelay;
    public override string Name => "Cyborg";
    public override string Emoji => "🦾";
    public override string Description => "Half human, half machine: metallic resonance and a buzzing core.";
    protected override void Prepare(int sr) { _down = new(sr); _metalDelay = sr * 5 / 1000; _metal = new Delay(_metalDelay + 4); _ring = new Osc(sr, 90); }
    protected override float Voice(float x)
    {
        var y = _down.Run(x, Shifter.Ratio(-2));
        y += _metal.Read(_metalDelay) * 0.7f;
        _metal.Write(y);
        return (y * 0.6f + y * _ring.Run() * 0.3f) * 0.7f;
    }
}
