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
        // Signal order: clean up → pitch → shape tone → control dynamics → de-ess (after compression, whose makeup
        // gain would bring sibilance back) → colour → space → final level → loudness meter.
        Effects =
        [
            new MonoEffect(),
            new LowCutEffect(),
            new PopFilterEffect(),
            new HumRemoverEffect(),
            new NoiseReductionEffect(),
            new NoiseGateEffect(),
            new AutoTuneEffect(),
            new PitchShiftEffect(),
            new VoiceEqEffect(),
            new AutoLevelEffect(),
            new CompressorEffect(),
            new DeEsserEffect(),
            new WarmthEffect(),
            new ExciterEffect(),
            new ReverbEffect(),
            new OutputGainEffect(),
            new LimiterEffect(),
        ];
    }

    /// <summary>Loudness of the processed voice (measured even when Voice FX is off).</summary>
    public LoudnessMeter Loudness { get; } = new();

    public T Get<T>() where T : Effect => Effects.OfType<T>().First();

    public string Name => "Voice FX";
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<Effect> Effects { get; }

    public Effect this[string id] => Effects.First(e => e.Id == id);

    /// <summary>Delay added by enabled look-ahead/block effects (e.g. noise reduction).</summary>
    public double LatencyMs => Enabled ? Effects.Where(e => e.Enabled).Sum(e => e.LatencyMs) : 0;

    public void Process(Span<float> samples, int sampleRate, int channels)
    {
        foreach (var effect in Effects)
        {
            if (effect.Enabled)
                effect.Process(samples, sampleRate, channels);
        }
    }

    /// <summary>Called by the engine on the final signal (after mods) so the meter reflects what listeners hear.</summary>
    public void Measure(ReadOnlySpan<float> samples, int sampleRate, int channels) => Loudness.Process(samples, sampleRate, channels);

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
