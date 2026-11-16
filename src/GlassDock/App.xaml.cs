using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GlassDock.Services;
using GlassDock.Views;

namespace GlassDock;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;
    public AppSettings SettingsData { get; private set; } = new();
    private Mutex? _single;
    private Heartbeat? _heartbeat;
    private DockWindow? _dock;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, "GlassDock.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        // Any crash path restores the native taskbar first.
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { TaskbarService.Restore(); CrashLog(a.ExceptionObject); };
        DispatcherUnhandledException += (_, args) => { TaskbarService.Restore(); CrashLog(args.Exception); args.Handled = false; };
        TaskScheduler.UnobservedTaskException += (_, a) => { CrashLog(a.Exception); a.SetObserved(); };

        base.OnStartup(e);
        SettingsData = AppSettings.Load();
        _heartbeat = new Heartbeat();

        _dock = new DockWindow();
        _dock.Show();
        ApplyTaskbar();
        if (e.Args.Contains("--selftest")) SelfTest.Run(_dock);
    }

    private static void CrashLog(object ex)
    {
        try
        {
            var dir = string.IsNullOrEmpty(AppSettings.DataDir) ? AppContext.BaseDirectory : AppSettings.DataDir;
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    public void ApplyTaskbar()
    {
        if (SettingsData.HideWindowsTaskbar) TaskbarService.Hide(); else TaskbarService.Restore();
        _heartbeat?.Beat(SettingsData.HideWindowsTaskbar);
    }

    public void RebuildDock() => _dock?.Rebuild();

    public void Quit()
    {
        SettingsData.Save();
        _dock?.Close();
        TaskbarService.Restore();
        _heartbeat?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TaskbarService.Restore();
        base.OnExit(e);
    }
}
