using YetiHub.Core.Effects;
using YetiHub.Core.Profiles;

namespace YetiHub.App.ViewModels;

public sealed class EffectViewModel : Observable
{
    private readonly Effect _effect;
    private readonly Action _changed;

    public EffectViewModel(Effect effect, Action changed)
    {
        _effect = effect;
        _changed = changed;
        Parameters = effect.Parameters.Select(p => new ParameterViewModel(p, changed)).ToList();
    }

    public string Id => _effect.Id;
    public string Name => _effect.Name;
    public string Description => _effect.Description;

    /// <summary>"clean", "tone", "dynamics", "space": drives the card's colour.</summary>
    public string Category => _effect.Category;

    public string LatencyNote => _effect.LatencyMs > 0 ? $"+{_effect.LatencyMs:0.0} ms" : "";
    public IReadOnlyList<ParameterViewModel> Parameters { get; }

    public bool Enabled
    {
        get => _effect.Enabled;
        set
        {
            if (_effect.Enabled == value) return;
            _effect.Enabled = value;
            Raise();
            _changed();
        }
    }

    /// <summary>Re-reads the model after a profile was applied.</summary>
    public void Refresh()
    {
        Raise(nameof(Enabled));
        foreach (var p in Parameters) p.Refresh();
    }
}

public sealed class ParameterViewModel(EffectParameter parameter, Action changed) : Observable
{
    public string Name => parameter.Name;
    public double Minimum => parameter.Min;
    public double Maximum => parameter.Max;
    public double Default => parameter.Default;
    public bool Bipolar => parameter.Min < 0 && parameter.Max > 0;
    public string Display => parameter.Display;

    public double Value
    {
        get => parameter.Value;
        set
        {
            var before = parameter.Value;
            parameter.Value = (float)value;
            if (parameter.Value == before) return;
            Raise();
            Raise(nameof(Display));
            changed();
        }
    }

    public void Refresh()
    {
        Raise(nameof(Value));
        Raise(nameof(Display));
    }
}

public sealed class ProfileViewModel(Profile model, Action<ProfileViewModel> renamed) : Observable
{
    public Profile Model { get; } = model;
    public bool IsBuiltIn => Model.IsBuiltIn;
    public string Group => IsBuiltIn ? "Standard" : "My profiles";
    public string? Tagline => Model.Tagline;

    public string Name
    {
        get => Model.Name;
        set
        {
            var name = string.IsNullOrWhiteSpace(value) || IsBuiltIn ? Model.Name : value.Trim();
            if (name == Model.Name)
            {
                Raise(); // snap the text box back
                return;
            }
            Model.Name = name;
            Raise();
            Raise(nameof(Initial));
            renamed(this);
        }
    }

    public string Initial => Model.Name.Length > 0 ? Model.Name[..1].ToUpperInvariant() : "?";

    private bool _isActive;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    public override string ToString() => Name;
}

/// <param name="Unavailable">"unplugged" / "disabled", or null when the device can be used.</param>
public sealed record OutputOption(string? Id, string Name, bool IsVirtualCable, string? Unavailable = null)
{
    public static readonly OutputOption Off = new(null, "Off", false);
    public bool IsAvailable => Unavailable is null;
    public string Display => Unavailable is null ? Name : $"{Name}  ({Unavailable})";
    public override string ToString() => Display;
}
