using System.Globalization;

namespace YetiHub.Core.Effects;

/// <summary>
/// One adjustable knob of an effect. The UI and profile files are both driven from this description.
/// </summary>
public sealed class EffectParameter
{
    private readonly Action _changed;
    private float _value;

    internal EffectParameter(string id, string name, float min, float max, float @default, string unit, Action changed)
    {
        Id = id;
        Name = name;
        Min = min;
        Max = max;
        Default = @default;
        Unit = unit;
        _value = @default;
        _changed = changed;
    }

    public string Id { get; }
    public string Name { get; }
    public float Min { get; }
    public float Max { get; }
    public float Default { get; }
    public string Unit { get; }

    public float Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, Min, Max);
            if (v == _value) return;
            _value = v;
            _changed();
        }
    }

    private static readonly string[] NoteNames = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];
    private static readonly string[] ScaleNames = ["Chromatic", "Major", "Minor", "Pentatonic"];

    public string Display => Unit switch
    {
        "Hz" when _value >= 1000 => $"{_value / 1000:0.0} kHz",
        "Hz" => $"{_value:0} Hz",
        "dB" => $"{_value:+0.0;-0.0;0.0} dB",
        "-dB" => _value == 0 ? "0.0 dB" : $"-{_value:0.0} dB", // amounts of reduction, stored positive
        "ms" => $"{_value:0} ms",
        ":1" => $"{_value:0.0}:1",
        "st" => $"{_value:+0.0;-0.0;0} st",
        "key" => NoteNames[(int)Math.Round(_value) % 12],
        "scale" => ScaleNames[(int)Math.Round(_value)],
        "x" => $"{_value:0}×",
        "Q" => $"Q {_value:0}",
        "%" => $"{_value:0} %",
        _ => _value.ToString("0.##", CultureInfo.CurrentCulture) + " " + Unit,
    };
}

/// <summary>
/// Base class for a real-time effect. Parameters are written by the UI thread and picked up by the audio thread
/// at the start of the next buffer.
/// </summary>
public abstract class Effect
{
    private readonly List<EffectParameter> _parameters = [];
    private volatile bool _dirty = true;

    protected Effect(string id, string name, string description)
    {
        Id = id;
        Name = name;
        Description = description;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public bool Enabled { get; set; }
    public IReadOnlyList<EffectParameter> Parameters => _parameters;

    /// <summary>Algorithmic delay this effect adds, in ms (0 for sample-by-sample effects).</summary>
    public virtual double LatencyMs => 0;

    /// <summary>Short UI category used for colour coding: "clean", "tone", "dynamics", "space", "fun".</summary>
    public virtual string Category => "tone";

    protected int SampleRate { get; private set; }
    protected int Channels { get; private set; }

    public EffectParameter this[string id] => _parameters.First(p => p.Id == id);

    protected EffectParameter Param(string id, string name, float min, float max, float @default, string unit)
    {
        var p = new EffectParameter(id, name, min, max, @default, unit, () => _dirty = true);
        _parameters.Add(p);
        return p;
    }

    public void ResetParameters()
    {
        foreach (var p in _parameters)
            p.Value = p.Default;
    }

    public void Process(Span<float> samples, int sampleRate, int channels)
    {
        if (sampleRate != SampleRate || channels != Channels)
        {
            SampleRate = sampleRate;
            Channels = channels;
            Reset();
            _dirty = true;
        }
        if (_dirty)
        {
            _dirty = false;
            Update();
        }
        ProcessCore(samples);
    }

    /// <summary>Clears filter/envelope state after a format change.</summary>
    protected virtual void Reset() { }

    /// <summary>Recomputes coefficients from parameter values.</summary>
    protected virtual void Update() { }

    protected abstract void ProcessCore(Span<float> samples);

    /// <summary>One-pole smoothing coefficient for a time constant in milliseconds.</summary>
    protected float Coefficient(float ms) => 1f - MathF.Exp(-1f / (Math.Max(ms, 0.01f) * 0.001f * SampleRate));

    protected static float DbToGain(float db) => MathF.Pow(10, db / 20);

    protected static float GainToDb(float gain) => 20 * MathF.Log10(Math.Max(gain, 1e-9f));
}
