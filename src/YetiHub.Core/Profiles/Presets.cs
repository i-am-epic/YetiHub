namespace YetiHub.Core.Profiles;

/// <summary>
/// Standard profiles, tuned for the Blue Yeti (cardioid, ~16 kHz condenser): it picks up a lot of room, gets boxy
/// around 250–400 Hz, boomy up close, and has a bright, sometimes sibilant top. Every chain therefore centres the
/// voice (Mono), cuts rumble, removes steady noise, trims box, controls "s", levels, compresses and limits at -1 dBFS.
/// They don't set mic gain (that depends on your voice and distance); Auto Level absorbs the difference.
/// </summary>
public static class Presets
{
    public static IReadOnlyList<Profile> All { get; } =
    [
        Make("std-natural", "Studio Natural", "Transparent and clean. Sounds like you, only better.",
            Fx("mono", ("amount", 100)),
            Fx("lowcut", ("frequency", 75)),
            Fx("denoise", ("amount", 12), ("strength", 40)),
            Fx("gate", ("threshold", -55), ("range", -15), ("release", 200)),
            Fx("eq", ("bass", 0), ("body", -2), ("presence", 1.5f), ("air", 1.5f)),
            Fx("deesser", ("threshold", -30), ("frequency", 6500), ("amount", 5)),
            Fx("leveler", ("target", -18), ("maxgain", 6), ("speed", 1500)),
            Fx("compressor", ("threshold", -22), ("ratio", 2.5f), ("attack", 10), ("release", 180), ("makeup", 4)),
            Fx("limiter", ("ceiling", -1))),

        Make("std-broadcast", "Broadcast Pro", "Big, polished radio voice with weight and presence.",
            Fx("mono", ("amount", 100)),
            Fx("lowcut", ("frequency", 85)),
            Fx("denoise", ("amount", 15), ("strength", 50)),
            Fx("gate", ("threshold", -50), ("range", -25), ("release", 180)),
            Fx("eq", ("bass", 3.5f), ("body", -3.5f), ("presence", 3), ("air", 2.5f)),
            Fx("deesser", ("threshold", -32), ("frequency", 6500), ("amount", 7)),
            Fx("leveler", ("target", -16), ("maxgain", 9), ("speed", 1000)),
            Fx("compressor", ("threshold", -24), ("ratio", 4), ("attack", 4), ("release", 140), ("makeup", 6)),
            Fx("warmth", ("drive", 8), ("mix", 30)),
            Fx("exciter", ("frequency", 4500), ("amount", 20)),
            Fx("limiter", ("ceiling", -1))),

        Make("std-cinematic", "Cinematic Deep", "Deep, rich trailer-style voice with a touch of space.",
            Fx("mono", ("amount", 100)),
            Fx("lowcut", ("frequency", 60)),
            Fx("denoise", ("amount", 15), ("strength", 45)),
            Fx("gate", ("threshold", -55), ("range", -20), ("release", 250)),
            Fx("eq", ("bass", 5), ("body", -2), ("presence", 2), ("air", 3.5f)),
            Fx("deesser", ("threshold", -30), ("frequency", 6500), ("amount", 7)),
            Fx("leveler", ("target", -18), ("maxgain", 8), ("speed", 1500)),
            Fx("compressor", ("threshold", -24), ("ratio", 3.5f), ("attack", 12), ("release", 250), ("makeup", 5)),
            Fx("warmth", ("drive", 10), ("mix", 40)),
            Fx("exciter", ("frequency", 6000), ("amount", 25)),
            Fx("reverb", ("size", 55), ("damping", 60), ("mix", 10)),
            Fx("limiter", ("ceiling", -1))),

        Make("std-podcast", "Podcast Clean", "Smooth, consistent and easy to listen to for hours.",
            Fx("mono", ("amount", 100)),
            Fx("lowcut", ("frequency", 80)),
            Fx("denoise", ("amount", 18), ("strength", 55)),
            Fx("gate", ("threshold", -52), ("range", -20), ("release", 220)),
            Fx("eq", ("bass", 1.5f), ("body", -2.5f), ("presence", 2), ("air", 1.5f)),
            Fx("deesser", ("threshold", -30), ("frequency", 6800), ("amount", 6)),
            Fx("leveler", ("target", -18), ("maxgain", 9), ("speed", 1200)),
            Fx("compressor", ("threshold", -22), ("ratio", 3), ("attack", 8), ("release", 200), ("makeup", 5)),
            Fx("warmth", ("drive", 4), ("mix", 20)),
            Fx("limiter", ("ceiling", -1))),

        Make("std-streamer", "Streamer Punch", "Loud, crisp and in-your-face. Cuts through game audio.",
            Fx("mono", ("amount", 100)),
            Fx("lowcut", ("frequency", 100)),
            Fx("denoise", ("amount", 22), ("strength", 65)),
            Fx("gate", ("threshold", -45), ("range", -40), ("release", 120)),
            Fx("eq", ("bass", 2.5f), ("body", -3), ("presence", 4), ("air", 3)),
            Fx("deesser", ("threshold", -30), ("frequency", 7000), ("amount", 8)),
            Fx("leveler", ("target", -16), ("maxgain", 12), ("speed", 800)),
            Fx("compressor", ("threshold", -26), ("ratio", 5), ("attack", 3), ("release", 100), ("makeup", 8)),
            Fx("warmth", ("drive", 6), ("mix", 25)),
            Fx("exciter", ("frequency", 5000), ("amount", 30)),
            Fx("limiter", ("ceiling", -1))),

        Make("std-gaming", "Gaming Comms", "Maximum clarity, keyboard and fan noise removed.",
            Fx("mono", ("amount", 100)),
            Fx("lowcut", ("frequency", 130)),
            Fx("denoise", ("amount", 28), ("strength", 80)),
            Fx("gate", ("threshold", -42), ("range", -60), ("release", 100)),
            Fx("eq", ("bass", 0), ("body", -3), ("presence", 4), ("air", 1)),
            Fx("deesser", ("threshold", -28), ("frequency", 7000), ("amount", 6)),
            Fx("leveler", ("target", -16), ("maxgain", 12), ("speed", 600)),
            Fx("compressor", ("threshold", -24), ("ratio", 4), ("attack", 3), ("release", 100), ("makeup", 7)),
            Fx("limiter", ("ceiling", -2))),

        Make("std-meeting", "Bright & Clear", "Crisp and intelligible for calls, Zoom and Teams.",
            Fx("mono", ("amount", 100)),
            Fx("lowcut", ("frequency", 110)),
            Fx("denoise", ("amount", 20), ("strength", 60)),
            Fx("gate", ("threshold", -48), ("range", -30), ("release", 150)),
            Fx("eq", ("bass", -1), ("body", -3), ("presence", 3.5f), ("air", 2)),
            Fx("deesser", ("threshold", -30), ("frequency", 7000), ("amount", 6)),
            Fx("leveler", ("target", -18), ("maxgain", 10), ("speed", 1000)),
            Fx("compressor", ("threshold", -22), ("ratio", 3), ("attack", 6), ("release", 150), ("makeup", 5)),
            Fx("limiter", ("ceiling", -1))),

        Make("std-intimate", "Warm & Intimate", "Soft, close and velvety for late-night talk and ASMR.",
            Fx("mono", ("amount", 100)),
            Fx("lowcut", ("frequency", 55)),
            Fx("denoise", ("amount", 20), ("strength", 55)),
            Fx("gate", ("threshold", -60), ("range", -15), ("release", 300)),
            Fx("eq", ("bass", 4), ("body", -1), ("presence", 0), ("air", 2)),
            Fx("deesser", ("threshold", -34), ("frequency", 6500), ("amount", 8)),
            Fx("leveler", ("target", -22), ("maxgain", 10), ("speed", 2000)),
            Fx("compressor", ("threshold", -26), ("ratio", 2.5f), ("attack", 15), ("release", 300), ("makeup", 5)),
            Fx("warmth", ("drive", 8), ("mix", 35)),
            Fx("reverb", ("size", 25), ("damping", 70), ("mix", 6)),
            Fx("limiter", ("ceiling", -2))),

        Make("std-arena", "Arena Announcer", "Stadium-sized hype voice. Just for fun.",
            Fx("mono", ("amount", 100)),
            Fx("lowcut", ("frequency", 90)),
            Fx("denoise", ("amount", 15), ("strength", 50)),
            Fx("gate", ("threshold", -48), ("range", -40), ("release", 150)),
            Fx("eq", ("bass", 4), ("body", -3), ("presence", 5), ("air", 2)),
            Fx("leveler", ("target", -16), ("maxgain", 10), ("speed", 800)),
            Fx("compressor", ("threshold", -26), ("ratio", 6), ("attack", 3), ("release", 120), ("makeup", 9)),
            Fx("warmth", ("drive", 12), ("mix", 40)),
            Fx("reverb", ("size", 90), ("damping", 30), ("mix", 28)),
            Fx("limiter", ("ceiling", -1))),

        new Profile
        {
            Id = "std-raw", Name = "Raw (No FX)", Tagline = "Untouched Yeti signal with the lowest possible latency.",
            IsBuiltIn = true, EffectsEnabled = false,
        },
    ];

    private static Profile Make(string id, string name, string tagline, params (string Id, EffectState State)[] effects) => new()
    {
        Id = id,
        Name = name,
        Tagline = tagline,
        IsBuiltIn = true,
        Effects = effects.ToDictionary(e => e.Id, e => e.State),
    };

    private static (string, EffectState) Fx(string id, params (string Param, float Value)[] values) =>
        (id, new EffectState { Enabled = true, Parameters = values.ToDictionary(v => v.Param, v => v.Value) });
}
