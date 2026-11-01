using System.Text.Json;

namespace YetiHub.Core.Profiles;

/// <summary>
/// One JSON file per profile in a folder, so profiles are easy to back up and share.
/// </summary>
public sealed class ProfileStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Directory { get; } = directory;

    /// <summary>Loads all profiles, seeding the built-in presets on first run.</summary>
    public List<Profile> LoadAll()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var profiles = new List<Profile>();
        foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Profile>(File.ReadAllText(file)) is { } p)
                    profiles.Add(p);
            }
            catch (JsonException) { /* skip a corrupt file rather than losing the rest */ }
        }

        if (profiles.Count == 0)
        {
            profiles.AddRange(Presets.All());
            foreach (var p in profiles)
                Save(p);
        }
        return profiles;
    }

    public void Save(Profile profile) =>
        File.WriteAllText(PathOf(profile), JsonSerializer.Serialize(profile, Json));

    public void Delete(Profile profile) => File.Delete(PathOf(profile));

    private string PathOf(Profile p) => Path.Combine(Directory, p.Id + ".json");
}
