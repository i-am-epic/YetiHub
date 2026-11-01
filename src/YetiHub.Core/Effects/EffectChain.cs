using YetiHub.Core.Profiles;
using YetiHub.Mods;

namespace YetiHub.Core.Effects;

/// <summary>
/// The built-in voice chain, in signal order. Disabled effects cost nothing.
/// </summary>
public sealed class EffectChain : IAudioProcessor
{
    public EffectChain()
    {
        Effects =
        [
            new LowCutEffect(),
            new NoiseGateEffect(),
            new VoiceEqEffect(),
            new DeEsserEffect(),
            new CompressorEffect(),
            new OutputGainEffect(),
            new LimiterEffect(),
        ];
    }

    public string Name => "Voice FX";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<Effect> Effects { get; }

    public Effect this[string id] => Effects.First(e => e.Id == id);

    public void Process(Span<float> samples, int sampleRate, int channels)
    {
        foreach (var effect in Effects)
        {
            if (effect.Enabled)
                effect.Process(samples, sampleRate, channels);
        }
    }

    public Dictionary<string, EffectState> CaptureState() =>
        Effects.ToDictionary(e => e.Id, e => new EffectState
        {
            Enabled = e.Enabled,
            Parameters = e.Parameters.ToDictionary(p => p.Id, p => p.Value),
        });

    /// <summary>Effects missing from <paramref name="state"/> are reset to defaults and turned off.</summary>
    public void ApplyState(IReadOnlyDictionary<string, EffectState> state)
    {
        foreach (var effect in Effects)
        {
            effect.ResetParameters();
            if (!state.TryGetValue(effect.Id, out var s))
            {
                effect.Enabled = false;
                continue;
            }
            effect.Enabled = s.Enabled;
            foreach (var p in effect.Parameters)
            {
                if (s.Parameters.TryGetValue(p.Id, out var v))
                    p.Value = v;
            }
        }
    }
}
