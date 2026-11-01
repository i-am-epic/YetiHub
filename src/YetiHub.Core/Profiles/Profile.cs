using System.Text.Json;
using System.Text.Json.Serialization;

namespace YetiHub.Core.Profiles;

/// <summary>
/// A saved sound: optional mic gain/headphone level and the full Voice FX chain.
/// </summary>
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "New profile";

    /// <summary>Windows input level, 0..1; null leaves the current gain alone.</summary>
    public float? Gain { get; set; }

    /// <summary>Yeti headphone volume, 0..1; null leaves it alone.</summary>
    public float? HeadphoneVolume { get; set; }

    public bool EffectsEnabled { get; set; } = true;
    public Dictionary<string, EffectState> Effects { get; set; } = [];

    /// <summary>Standard profiles ship with the app, can't be edited, and are never written to disk.</summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; init; }

    /// <summary>One-line description shown for standard profiles.</summary>
    [JsonIgnore]
    public string? Tagline { get; init; }

    public Profile Clone(string name)
    {
        var copy = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(this))!;
        copy.Id = Guid.NewGuid().ToString("n");
        copy.Name = name;
        return copy;
    }
}

public sealed class EffectState
{
    public bool Enabled { get; set; }
    public Dictionary<string, float> Parameters { get; set; } = [];
}
