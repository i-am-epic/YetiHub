namespace YetiHub.Core.Profiles;

/// <summary>
/// Standard profiles, tuned for the Blue Yeti (cardioid, side-address condenser): it picks up a lot of room, gets
/// boxy around 250–400 Hz, boomy and plosive up close, with a bright, sometimes sibilant top.
///
/// Chain targets follow common broadcast/podcast practice: high-pass 80–100 Hz, noise reduction before EQ,
/// presence around 3.5 kHz, 3:1–4:1 compression, de-essing at 5–8 kHz after compression, a -1 dBFS limiter and an
/// output level around -16 LUFS short-term (measured by the offline speech tests in tests/YetiHub.Core.Tests).
/// Standard profiles never set mic gain: Auto Level absorbs differences in voice and distance.
/// </summary>
public static class Presets
{
    public static IReadOnlyList<Profile> All { get; } =
    [
        Make("std-natural", "Studio Natural", "Transparent and clean. Sounds like you, only better.",
            Cleanup(lowCut: 75, denoise: 12, strength: 40, sensitivity: 8, gateRange: -15),
            Fx("eq", ("bass", 0), ("body", -2), ("presence", 1.5f), ("air", 1.5f)),
            Fx("leveler", ("target", -18), ("maxgain", 20), ("speed", 1500)),
            Fx("compressor", ("threshold", -22), ("ratio", 2.5f), ("attack", 10), ("release", 180), ("makeup", 3)),
            Fx("deesser", ("threshold", -28), ("frequency", 6500), ("amount", 5)),
            Out(3.5f)),

        Make("std-broadcast", "Broadcast Pro", "Big, polished radio voice with weight and presence.",
            Cleanup(lowCut: 85, denoise: 15, strength: 50, sensitivity: 9, gateRange: -25),
            Fx("eq", ("bass", 3.5f), ("body", -3.5f), ("presence", 3), ("air", 2.5f)),
            Fx("leveler", ("target", -18), ("maxgain", 20), ("speed", 1000)),
            Fx("compressor", ("threshold", -24), ("ratio", 4), ("attack", 4), ("release", 140), ("makeup", 5)),
            Fx("deesser", ("threshold", -30), ("frequency", 6500), ("amount", 7)),
            Fx("warmth", ("drive", 8), ("mix", 30)),
            Fx("exciter", ("frequency", 4500), ("amount", 20)),
            Out(0)),

        Make("std-cinematic", "Cinematic Deep", "Deep, rich trailer-style voice with a touch of space.",
            Cleanup(lowCut: 60, denoise: 18, strength: 55, sensitivity: 9, gateRange: -30),
            Fx("eq", ("bass", 5), ("body", -2), ("presence", 2), ("air", 3.5f)),
            Fx("leveler", ("target", -18), ("maxgain", 20), ("speed", 1500)),
            Fx("compressor", ("threshold", -24), ("ratio", 3.5f), ("attack", 12), ("release", 250), ("makeup", 4)),
            Fx("deesser", ("threshold", -30), ("frequency", 6500), ("amount", 7)),
            Fx("warmth", ("drive", 10), ("mix", 40)),
            Fx("exciter", ("frequency", 6000), ("amount", 25)),
            Fx("reverb", ("size", 55), ("damping", 60), ("mix", 9)),
            Out(-2)),

        Make("std-podcast", "Podcast Clean", "Smooth, consistent and easy to listen to for hours.",
            Cleanup(lowCut: 80, denoise: 18, strength: 55, sensitivity: 9, gateRange: -20),
            Fx("eq", ("bass", 1.5f), ("body", -2.5f), ("presence", 2), ("air", 1.5f)),
            Fx("leveler", ("target", -18), ("maxgain", 20), ("speed", 1200)),
            Fx("compressor", ("threshold", -22), ("ratio", 3), ("attack", 8), ("release", 200), ("makeup", 4)),
            Fx("deesser", ("threshold", -28), ("frequency", 6800), ("amount", 6)),
            Fx("warmth", ("drive", 4), ("mix", 20)),
            Out(1)),

        Make("std-streamer", "Streamer Punch", "Loud, crisp and in-your-face. Cuts through game audio.",
            Cleanup(lowCut: 100, denoise: 22, strength: 65, sensitivity: 8, gateRange: -35, keys: 70),
            Fx("eq", ("bass", 2.5f), ("body", -3), ("presence", 4), ("air", 3)),
            Fx("leveler", ("target", -17), ("maxgain", 20), ("speed", 800)),
            Fx("compressor", ("threshold", -26), ("ratio", 4.5f), ("attack", 3), ("release", 100), ("makeup", 5)),
            Fx("deesser", ("threshold", -28), ("frequency", 7000), ("amount", 8)),
            Fx("warmth", ("drive", 6), ("mix", 25)),
            Fx("exciter", ("frequency", 5000), ("amount", 30)),
            Out(1.5f)),

        Make("std-gaming", "Gaming Comms", "Maximum clarity, keyboard and fan noise removed.",
            Cleanup(lowCut: 120, denoise: 26, strength: 75, sensitivity: 7, gateRange: -40, keys: 100),
            Fx("eq", ("bass", 0), ("body", -3), ("presence", 4), ("air", 1)),
            Fx("leveler", ("target", -17), ("maxgain", 20), ("speed", 600)),
            Fx("compressor", ("threshold", -24), ("ratio", 4), ("attack", 3), ("release", 100), ("makeup", 4)),
            Fx("deesser", ("threshold", -26), ("frequency", 7000), ("amount", 6)),
            Out(2.5f)),

        Make("std-meeting", "Bright & Clear", "Crisp and intelligible for calls, Zoom and Teams.",
            Cleanup(lowCut: 110, denoise: 20, strength: 60, sensitivity: 8, gateRange: -30, keys: 80),
            Fx("eq", ("bass", -1), ("body", -3), ("presence", 3.5f), ("air", 2)),
            Fx("leveler", ("target", -18), ("maxgain", 20), ("speed", 1000)),
            Fx("compressor", ("threshold", -22), ("ratio", 3), ("attack", 6), ("release", 150), ("makeup", 4)),
            Fx("deesser", ("threshold", -28), ("frequency", 7000), ("amount", 6)),
            Out(3)),

        Make("std-intimate", "Warm & Intimate", "Soft, close and velvety for late-night talk and ASMR.",
            Cleanup(lowCut: 55, denoise: 20, strength: 55, sensitivity: 10, gateRange: -15),
            Fx("eq", ("bass", 4), ("body", -1), ("presence", 0), ("air", 2)),
            Fx("leveler", ("target", -20), ("maxgain", 20), ("speed", 2000)),
            Fx("compressor", ("threshold", -26), ("ratio", 2.5f), ("attack", 15), ("release", 300), ("makeup", 4)),
            Fx("deesser", ("threshold", -32), ("frequency", 6500), ("amount", 8)),
            Fx("warmth", ("drive", 8), ("mix", 35)),
            Fx("reverb", ("size", 25), ("damping", 70), ("mix", 6)),
            Out(0.5f)),

        Make("std-singer", "Singer", "Gentle pitch correction plus a vocal plate. For singing on stream.",
            Cleanup(lowCut: 80, denoise: 15, strength: 45, sensitivity: 10, gateRange: -20),
            Fx("autotune", ("key", 0), ("scale", 0), ("speed", 60), ("amount", 70)),
            Fx("eq", ("bass", 1), ("body", -2.5f), ("presence", 2.5f), ("air", 3)),
            Fx("leveler", ("target", -18), ("maxgain", 24), ("speed", 1500)),
            Fx("compressor", ("threshold", -22), ("ratio", 3), ("attack", 8), ("release", 150), ("makeup", 4)),
            Fx("deesser", ("threshold", -28), ("frequency", 6800), ("amount", 6)),
            Fx("reverb", ("size", 60), ("damping", 45), ("mix", 16)),
            Out(3)),

        Make("std-hardtune", "Hard Tune", "The robotic T-Pain effect: instant pitch snapping.",
            Cleanup(lowCut: 90, denoise: 18, strength: 55, sensitivity: 9, gateRange: -30),
            Fx("autotune", ("key", 0), ("scale", 0), ("speed", 0), ("amount", 100)),
            Fx("eq", ("bass", 1), ("body", -3), ("presence", 3), ("air", 3)),
            Fx("leveler", ("target", -18), ("maxgain", 20), ("speed", 1000)),
            Fx("compressor", ("threshold", -24), ("ratio", 4), ("attack", 3), ("release", 100), ("makeup", 4)),
            Fx("reverb", ("size", 40), ("damping", 50), ("mix", 10)),
            Out(2.5f)),

        Make("std-deep", "Deep Voice", "Three semitones lower and heavier. Instant villain.",
            Cleanup(lowCut: 70, denoise: 18, strength: 55, sensitivity: 9, gateRange: -30),
            Fx("pitch", ("semitones", -3), ("mix", 100)),
            Fx("eq", ("bass", 4), ("body", -2), ("presence", 2), ("air", 1)),
            Fx("leveler", ("target", -18), ("maxgain", 20), ("speed", 1000)),
            Fx("compressor", ("threshold", -24), ("ratio", 4), ("attack", 5), ("release", 150), ("makeup", 4)),
            Fx("warmth", ("drive", 10), ("mix", 35)),
            Out(-3.5f)),

        Make("std-arena", "Arena Announcer", "Stadium-sized hype voice. Just for fun.",
            Cleanup(lowCut: 90, denoise: 15, strength: 50, sensitivity: 9, gateRange: -40),
            Fx("eq", ("bass", 4), ("body", -3), ("presence", 5), ("air", 2)),
            Fx("leveler", ("target", -18), ("maxgain", 20), ("speed", 800)),
            Fx("compressor", ("threshold", -26), ("ratio", 6), ("attack", 3), ("release", 120), ("makeup", 5)),
            Fx("warmth", ("drive", 12), ("mix", 40)),
            Fx("reverb", ("size", 90), ("damping", 30), ("mix", 26)),
            Out(-4.5f)),

        new Profile
        {
            Id = "std-raw", Name = "Raw (No FX)", Tagline = "Untouched Yeti signal with the lowest possible latency.",
            IsBuiltIn = true, EffectsEnabled = false,
        },
    ];

    /// <summary>Shared clean-up stages every voice profile starts with.</summary>
    private static (string, EffectState)[] Cleanup(float lowCut, float denoise, float strength, float sensitivity, float gateRange, float keys = 50) =>
    [
        Fx("mono", ("amount", 100)),
        Fx("lowcut", ("frequency", lowCut)),
        Fx("popfilter", ("sensitivity", 60), ("amount", 18)),
        Fx("dehum", ("harmonics", 4), ("depth", 30)),
        Fx("denoise", ("amount", denoise), ("strength", strength), ("keys", keys)),
        Fx("gate", ("sensitivity", sensitivity), ("range", gateRange), ("hold", 150), ("release", 150)),
    ];

    /// <summary>Output trim (to land near -16 LUFS) followed by the -1 dBFS safety limiter.</summary>
    private static (string, EffectState)[] Out(float trimDb) =>
    [
        Fx("output", ("gain", trimDb)),
        Fx("limiter", ("ceiling", -1)),
    ];

    private static Profile Make(string id, string name, string tagline, params object[] parts) => new()
    {
        Id = id,
        Name = name,
        Tagline = tagline,
        IsBuiltIn = true,
        Effects = parts
            .SelectMany(p => p switch
            {
                ValueTuple<string, EffectState> one => [one],
                (string, EffectState)[] many => many,
                _ => throw new ArgumentException("bad preset part"),
            })
            .ToDictionary(e => e.Item1, e => e.Item2),
    };

    private static (string, EffectState) Fx(string id, params (string Param, float Value)[] values) =>
        (id, new EffectState { Enabled = true, Parameters = values.ToDictionary(v => v.Param, v => v.Value) });
}
