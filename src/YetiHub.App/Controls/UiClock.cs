using System.Windows.Threading;

namespace YetiHub.App.Controls;

/// <summary>
/// One shared animation clock for meters and graphs. Runs at a fixed 30 fps (instead of every display refresh,
/// which is 144–165 times a second on gaming monitors) and stops completely while the window is minimized, so
/// YetiHub costs almost nothing in the background.
/// </summary>
public static class UiClock
{
    public const double Fps = 30;

    private static readonly DispatcherTimer Timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / Fps) };
    private static event Action<double>? TickHandlers;
    private static DateTime _last = DateTime.UtcNow;
    private static bool _paused;
    private static int _subscribers;

    static UiClock()
    {
        Timer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            var dt = Math.Clamp((now - _last).TotalSeconds, 0, 0.1);
            _last = now;
            TickHandlers?.Invoke(dt);
        };
    }

    /// <summary>Raised on the UI thread with the elapsed seconds since the previous tick.</summary>
    public static event Action<double> Tick
    {
        add { TickHandlers += value; _subscribers++; Update(); }
        remove { TickHandlers -= value; _subscribers = Math.Max(0, _subscribers - 1); Update(); }
    }

    /// <summary>True while the window is minimized: nothing animates.</summary>
    public static bool Paused
    {
        get => _paused;
        set { _paused = value; Update(); }
    }

    private static void Update()
    {
        var run = !_paused && _subscribers > 0;
        if (run == Timer.IsEnabled) return;
        _last = DateTime.UtcNow;
        Timer.IsEnabled = run;
    }
}
