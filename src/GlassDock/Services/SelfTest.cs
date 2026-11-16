using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using GlassDock.Views;

namespace GlassDock.Services;

/// <summary>
/// `GlassDock.exe --selftest` drives the real app on a real desktop (used by CI on windows-latest):
/// cycles styles / sizes / edges, opens every window and widget popup, hides the taskbar, takes screenshots,
/// writes data\selftest.log, then crashes on purpose so CI can check the watchdog brings the taskbar back.
/// </summary>
public static class SelfTest
{
    private static string Dir => Path.Combine(AppSettings.DataDir, "selftest");
    private static readonly List<string> Fails = new();

    private static void Log(string s)
    {
        Directory.CreateDirectory(Dir);
        File.AppendAllText(Path.Combine(Dir, "selftest.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}{Environment.NewLine}");
    }

    private static void Check(bool ok, string what) { Log((ok ? "PASS " : "FAIL ") + what); if (!ok) Fails.Add(what); }

    private static Task Wait(int ms) => Task.Delay(ms);

    private static void Shot(string name)
    {
        try
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            using var bmp = new System.Drawing.Bitmap(b.Width, b.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, System.Drawing.Point.Empty, b.Size);
            bmp.Save(Path.Combine(Dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception e) { Log("screenshot failed: " + e.Message); }
    }

    public static async void Run(DockWindow dock)
    {
        var app = App.Current;
        var s = app.SettingsData;
        try
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            Log($"start, OS {Environment.OSVersion}, entries {s.Entries.Count}");
            await Wait(4000);
            Check(dock.IsVisible, "dock window visible");
            Check(dock.ActualWidth > 0 && dock.ActualHeight > 0, $"dock has size {dock.ActualWidth}x{dock.ActualHeight}");
            Check(dock.Owner != null, "icon overlay is owned by (so drawn above) the glass backdrop");
            Check(s.Entries.Any(e => e.Kind == "widget"), "default widgets present");
            Shot("01-default");

            var wa0 = SystemParameters.WorkArea;
            s.HideWindowsTaskbar = true; app.ApplyTaskbar();
            await Wait(3500);
            Check(!TaskbarService.IsVisible(), "taskbar hidden");
            Check(SystemParameters.WorkArea.Height > wa0.Height, "work area reclaimed from taskbar");
            Log($"work area before {wa0}, after {SystemParameters.WorkArea}");
            Shot("02-taskbar-hidden");

            foreach (DockStyle st in Enum.GetValues<DockStyle>())
            {
                s.Style = st; app.RebuildDock(); await Wait(900);
                Check(dock.IsVisible, $"style {st}");
                Shot($"03-style-{st}");
            }
            s.Style = DockStyle.FrostedGlass;
            foreach (DockSize sz in Enum.GetValues<DockSize>())
            {
                s.Size = sz; app.RebuildDock(); await Wait(900);
                Check(dock.IsVisible, $"size {sz}");
                Shot($"04-size-{sz}");
            }
            s.Size = DockSize.Medium;
            s.Edge = DockEdge.Top; app.RebuildDock(); await Wait(1200);
            Check(dock.Top < 50, $"edge top (Top={dock.Top})"); Shot("05-edge-top");
            s.Edge = DockEdge.Bottom; app.RebuildDock(); await Wait(1200);
            Check(dock.Top > 200, $"edge bottom (Top={dock.Top})");

            var firstApp = s.Entries.FirstOrDefault(e => e.Kind == "app")?.App;
            s.Entries.Insert(0, new DockEntry { Kind = "folder", FolderName = "测试文件夹", FolderApps = firstApp != null ? new() { firstApp, firstApp } : new() });

            // every widget, twice, to catch duplicate timers / leaks
            foreach (var w in Widgets.WidgetRegistry.All)
            {
                s.Entries.Add(new DockEntry { Kind = "widget", WidgetId = w.Id });
                app.RebuildDock(); await Wait(600);
            }
            await Wait(3000);
            Check(dock.IsVisible, "all widgets added");
            Shot("06-all-widgets");
            Check(dock.BarWidth <= SystemParameters.PrimaryScreenWidth, $"dock fits on screen ({dock.BarWidth:0} <= {SystemParameters.PrimaryScreenWidth})");

            dock.SelfTestMagnify(); await Wait(500); Shot("07-magnify");
            dock.SelfTestOpenPopups(); await Wait(800); Shot("08-popups");

            var settings = new SettingsWindow(); settings.Show(); await Wait(1200); Shot("09-settings"); settings.Close();
            var lib = new WidgetLibraryWindow(); lib.Show(); await Wait(1200); Shot("10-library"); lib.Close();

            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            Log($"memory working set {proc.WorkingSet64 / 1048576} MB, threads {proc.Threads.Count}");
            var cpu0 = proc.TotalProcessorTime; await Wait(10000); proc.Refresh();
            double cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / 10000 / Environment.ProcessorCount * 100;
            Log($"idle CPU over 10 s: {cpu:0.00}%");
            Check(cpu < 5, "idle CPU under 5%");

            Log(Fails.Count == 0 ? "RESULT PASS" : "RESULT FAIL: " + string.Join("; ", Fails));
        }
        catch (Exception e)
        {
            Log("RESULT FAIL exception: " + e);
        }
        Log("crashing on purpose to test the watchdog");
        await Wait(300);
        Environment.FailFast("GlassDock self-test deliberate crash");
    }
}
