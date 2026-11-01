using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace YetiHub.App.Services;

/// <summary>
/// Self-update from GitHub Releases. Only works in an installed copy (Setup.exe); a dev build reports "not installed".
/// </summary>
public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/i-am-epic/YetiHub";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "dev";

    public Task<UpdateInfo?> CheckAsync() => _manager.CheckForUpdatesAsync();

    public Task DownloadAsync(UpdateInfo update, Action<int> progress) => _manager.DownloadUpdatesAsync(update, progress);

    public void ApplyAndRestart(UpdateInfo update) => _manager.ApplyUpdatesAndRestart(update);
}
