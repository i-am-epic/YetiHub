using System.IO;
using System.Windows;
using System.Windows.Threading;
using YetiHub.App.Services;

namespace YetiHub.App;

public partial class App : Application
{
    public static string CrashLogPath => Path.Combine(AppSettings.DataRoot, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        // Troubleshooting switch for broken GPU drivers / remote sessions: draw with the CPU instead.
        if (Environment.GetEnvironmentVariable("YETIHUB_SOFTWARE_RENDER") == "1" || AppSettings.Load().SoftwareRendering)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        new MainWindow().Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception, "UI");
        // Keep the app (and your audio) running; the error is in crash.log for bug reports.
        e.Handled = true;
    }

    public static void LogError(Exception ex, string where)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataRoot);
            File.AppendAllText(CrashLogPath, $"{DateTime.Now:u} [{where}] {ex}\n\n");
        }
        catch (IOException) { }
    }
}
