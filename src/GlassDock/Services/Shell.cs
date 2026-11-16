using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDock.Native;
using Microsoft.Win32;

namespace GlassDock.Services;

/// <summary>Hides / restores the native taskbar. Only ShowWindow on the taskbar windows: no injection into explorer.exe.</summary>
public static class TaskbarService
{
    private static IEnumerable<IntPtr> TaskbarWindows()
    {
        var main = Win32.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        IntPtr h = IntPtr.Zero;
        while ((h = Win32.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return h;
    }

    public static void Hide() { foreach (var h in TaskbarWindows()) Win32.ShowWindow(h, Win32.SW_HIDE); }
    public static void Restore() { foreach (var h in TaskbarWindows()) Win32.ShowWindow(h, Win32.SW_SHOW); }
}

/// <summary>Real-time blur behind the window, done by DWM (GPU), so the app redraws nothing when the background changes.</summary>
public static class GlassEffect
{
    public static void Apply(Window w, DockStyle style)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        // tint as AABBGGRR
        (Win32.AccentState state, uint tint) = style switch
        {
            DockStyle.ClearGlass   => (Win32.AccentState.ACCENT_ENABLE_BLURBEHIND, 0x10FFFFFFu),
            DockStyle.FrostedGlass => (Win32.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, 0x40F0F0F0u),
            DockStyle.TintedGlass  => (Win32.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, 0x55E07A3Au), // warm blue tint
            DockStyle.Dark         => (Win32.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, 0xCC1E1E1Eu),
            DockStyle.Light        => (Win32.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, 0xCCF5F5F5u),
            _                      => (Win32.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, 0xB0301A10u), // Midnight
        };

        var accent = new Win32.AccentPolicy { AccentState = state, AccentFlags = 2, GradientColor = tint };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new Win32.WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size };
            Win32.SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }

        // Win11: rounded corners on the blur region
        int round = Win32.DWMWCP_ROUND;
        try { Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)); } catch { }
    }

    public static bool IsDark(DockStyle s) => s is DockStyle.Dark or DockStyle.Midnight or DockStyle.TintedGlass;
}

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        if (on) key.SetValue("GlassDock", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("GlassDock", false);
    }
}

/// <summary>
/// Writes a tick from the UI thread every 2 s into shared memory. The watchdog process reads it:
/// if GlassDock exits, crashes, or the UI hangs ~15 s, the watchdog restores the Windows taskbar.
/// </summary>
public sealed class Heartbeat : IDisposable
{
    public const string MapName = "Local\\GlassDock.Heartbeat";
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private Process? _watchdog;

    public Heartbeat()
    {
        _mmf = MemoryMappedFile.CreateOrOpen(MapName, 16);
        _view = _mmf.CreateViewAccessor(0, 16);
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Beat(hidden: App.Current.SettingsData.HideWindowsTaskbar);
        _timer.Start();
        Beat(App.Current.SettingsData.HideWindowsTaskbar);
        StartWatchdog();
    }

    /// <summary>[0..8) unix ms, [8] taskbar-hidden flag, [9] clean-exit flag.</summary>
    public void Beat(bool hidden)
    {
        _view.Write(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _view.Write(8, (byte)(hidden ? 1 : 0));
        _view.Write(9, (byte)0);
    }

    private void StartWatchdog()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "GlassDock.Watchdog.exe");
        if (!File.Exists(exe)) return;
        try
        {
            _watchdog = Process.Start(new ProcessStartInfo(exe, Environment.ProcessId.ToString())
            { UseShellExecute = false, CreateNoWindow = true });
        }
        catch { }
    }

    public void Dispose()
    {
        _timer.Stop();
        _view.Write(9, (byte)1); // clean exit: watchdog just restores and leaves
        _view.Dispose();
        _mmf.Dispose();
    }
}

public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string path)
    {
        if (Cache.TryGetValue(path, out var img)) return img;
        img = null;
        try
        {
            var info = new Win32.SHFILEINFO();
            Win32.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);
            if (info.hIcon != IntPtr.Zero)
            {
                // Prefer the 256px jumbo icon through the associated-icon API when available
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                var src = ico != null
                    ? Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions())
                    : Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                img = src;
                Win32.DestroyIcon(info.hIcon);
            }
        }
        catch { }
        Cache[path] = img;
        return img;
    }

    public static void Launch(PinnedApp app)
    {
        try { Process.Start(new ProcessStartInfo(app.Path, app.Arguments ?? "") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "GlassDock"); }
    }
}
