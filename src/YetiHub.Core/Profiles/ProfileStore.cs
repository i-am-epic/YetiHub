using System.Text.Json;

namespace YetiHub.Core.Profiles;

/// <summary>
/// User profiles, one JSON file each, so they are easy to back up and share. Standard profiles live in
/// <see cref="Presets"/> and are never stored here.
/// </summary>
public sealed class ProfileStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // v0.1.0 seeded its presets as editable files under these ids; they are now built in.
    private static readonly HashSet<string> LegacySeedIds = ["natural", "broadcast", "podcast", "streamer", "gaming", "warm"];

    public string Directory { get; } = directory;

    public List<Profile> LoadUserProfiles()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var profiles = new List<Profile>();
        foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Profile>(File.ReadAllText(file)) is not { } p)
                    continue;
                if (LegacySeedIds.Contains(p.Id))
                {
                    File.Delete(file);
                    continue;
                }
                profiles.Add(p);
            }
            catch (JsonException) { /* skip a corrupt file rather than losing the rest */ }
        }
        return profiles;
    }

    public void Save(Profile profile)
    {
        if (profile.IsBuiltIn)
            throw new InvalidOperationException("Standard profiles are read-only; duplicate them to edit.");
        File.WriteAllText(PathOf(profile), JsonSerializer.Serialize(profile, Json));
    }

    public void Delete(Profile profile)
    {
        if (!profile.IsBuiltIn) File.Delete(PathOf(profile));
    }

    private string PathOf(Profile p) => Path.Combine(Directory, p.Id + ".json");
}
