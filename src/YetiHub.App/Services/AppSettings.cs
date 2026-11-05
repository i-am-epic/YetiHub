using System.IO;
using System.Text.Json;

namespace YetiHub.App.Services;

/// <summary>Machine-level settings. Sound settings live in profiles.</summary>
public sealed class AppSettings
{
    public static readonly string DataRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YetiHub");

    private static string FilePath => Path.Combine(DataRoot, "settings.json");

    public bool CheckForUpdatesOnStart { get; set; } = true;
    public string? ActiveProfileId { get; set; }
    public string? MonitorDeviceId { get; set; }
    public string? VirtualMicDeviceId { get; set; }

    /// <summary>Open the Yeti in exclusive mode (3 ms packets). Other apps then need the virtual mic.</summary>
    public bool UltraLowLatency { get; set; }

    /// <summary>Draw the UI with the CPU (fixes blank/white windows on broken GPU drivers or remote desktop).</summary>
    public bool SoftwareRendering { get; set; }

    /// <summary>Switch to a game's profile while it runs, then back.</summary>
    public bool AutoGameProfiles { get; set; } = true;

    /// <summary>Profile id per game id; games not listed use their recommended standard profile.</summary>
    public Dictionary<string, string> GameProfiles { get; set; } = [];

    /// <summary>Monitor output imitates game voice chat.</summary>
    public bool VoiceChatPreview { get; set; }

    /// <summary>Mod processor on/off by name; mods not listed keep their own default.</summary>
    public Dictionary<string, bool> ProcessorStates { get; set; } = [];

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
