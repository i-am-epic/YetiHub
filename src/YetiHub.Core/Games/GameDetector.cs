using System.Diagnostics;

namespace YetiHub.Core.Games;

public sealed record KnownGame(string Id, string Name, string Emoji, string[] ProcessNames, string? RecommendedProfileId);

/// <summary>
/// Notices when a supported game is running, by process name only. It never opens, reads or injects into the game
/// process, so it is safe alongside anti-cheat software such as Riot Vanguard.
/// </summary>
public sealed class GameDetector : IDisposable
{
    public static IReadOnlyList<KnownGame> Games { get; } =
    [
        new("valorant", "VALORANT", "🎯", ["VALORANT-Win64-Shipping"], "std-valorant"),
        new("cs2", "Counter-Strike 2", "💣", ["cs2"], "std-valorant"),
        new("apex", "Apex Legends", "🛡️", ["r5apex", "r5apex_dx12"], "std-gaming"),
        new("fortnite", "Fortnite", "🏗️", ["FortniteClient-Win64-Shipping"], "std-gaming"),
        new("overwatch", "Overwatch 2", "🦾", ["Overwatch"], "std-gaming"),
        new("r6", "Rainbow Six Siege", "🧱", ["RainbowSix", "RainbowSix_BE"], "std-valorant"),
        new("lol", "League of Legends", "🗡️", ["League of Legends"], "std-gaming"),
        new("cod", "Call of Duty", "🎖️", ["cod", "ModernWarfare", "BlackOps6"], "std-gaming"),
    ];

    private readonly Timer _timer;
    private KnownGame? _current;

    public GameDetector(TimeSpan? interval = null)
    {
        var every = interval ?? TimeSpan.FromSeconds(3);
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, every);
    }

    /// <summary>The supported game currently running, or null.</summary>
    public KnownGame? Current => Volatile.Read(ref _current);

    /// <summary>Raised on a thread-pool thread when a game starts or stops (argument: the game now running, or null).</summary>
    public event EventHandler<KnownGame?>? Changed;

    private void Poll()
    {
        KnownGame? running = null;
        foreach (var game in Games)
        {
            foreach (var name in game.ProcessNames)
            {
                var procs = Process.GetProcessesByName(name);
                var found = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (found) { running = game; break; }
            }
            if (running != null) break;
        }

        if (running?.Id == _current?.Id) return;
        Volatile.Write(ref _current, running);
        Changed?.Invoke(this, running);
    }

    public void Dispose() => _timer.Dispose();
}
