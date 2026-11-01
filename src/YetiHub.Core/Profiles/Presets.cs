namespace YetiHub.Core.Profiles;

/// <summary>
/// Starting profiles written on first run. Users edit these like any other profile.
/// </summary>
public static class Presets
{
    public static IEnumerable<Profile> All()
    {
        yield return Make("natural", "Natural", 0.6f,
            Fx("lowcut", ("frequency", 70)));

        yield return Make("broadcast", "Broadcast", 0.55f,
            Fx("lowcut", ("frequency", 90)),
            Fx("gate", ("threshold", -52), ("range", -30), ("release", 180)),
            Fx("eq", ("bass", 3), ("body", -3), ("presence", 3), ("air", 2)),
            Fx("deesser", ("threshold", -32), ("amount", 6)),
            Fx("compressor", ("threshold", -24), ("ratio", 4), ("attack", 5), ("release", 150), ("makeup", 8)),
            Fx("limiter", ("ceiling", -1)));

        yield return Make("podcast", "Podcast", 0.55f,
            Fx("lowcut", ("frequency", 80)),
            Fx("gate", ("threshold", -55), ("range", -25), ("release", 220)),
            Fx("eq", ("bass", 1), ("body", -2), ("presence", 2), ("air", 1.5f)),
            Fx("deesser", ("threshold", -30), ("amount", 6)),
            Fx("compressor", ("threshold", -20), ("ratio", 3), ("attack", 10), ("release", 200), ("makeup", 5)),
            Fx("limiter", ("ceiling", -1)));

        yield return Make("streamer", "Streamer", 0.6f,
            Fx("lowcut", ("frequency", 100)),
            Fx("gate", ("threshold", -45), ("range", -50), ("release", 120)),
            Fx("eq", ("bass", 2), ("body", -3), ("presence", 4), ("air", 3)),
            Fx("deesser", ("threshold", -30), ("amount", 8)),
            Fx("compressor", ("threshold", -26), ("ratio", 5), ("attack", 3), ("release", 100), ("makeup", 9)),
            Fx("limiter", ("ceiling", -1)));

        yield return Make("gaming", "Gaming", 0.6f,
            Fx("lowcut", ("frequency", 120)),
            Fx("gate", ("threshold", -40), ("range", -60), ("release", 100)),
            Fx("eq", ("body", -2), ("presence", 3)),
            Fx("compressor", ("threshold", -22), ("ratio", 4), ("attack", 3), ("release", 100), ("makeup", 6)),
            Fx("limiter", ("ceiling", -2)));

        yield return Make("warm", "Warm & Clear", 0.55f,
            Fx("lowcut", ("frequency", 60)),
            Fx("gate", ("threshold", -58), ("range", -20), ("release", 250)),
            Fx("eq", ("bass", 4), ("body", -1), ("presence", 1.5f), ("air", 2.5f)),
            Fx("compressor", ("threshold", -18), ("ratio", 2.5f), ("attack", 15), ("release", 250), ("makeup", 4)),
            Fx("limiter", ("ceiling", -1)));
    }

    private static Profile Make(string id, string name, float gain, params (string Id, EffectState State)[] effects) => new()
    {
        Id = id,
        Name = name,
        Gain = gain,
        Effects = effects.ToDictionary(e => e.Id, e => e.State),
    };

    private static (string, EffectState) Fx(string id, params (string Param, float Value)[] values) =>
        (id, new EffectState { Enabled = true, Parameters = values.ToDictionary(v => v.Param, v => v.Value) });
}
