using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

// GlassDock watchdog: a tiny separate process. It never touches explorer.exe beyond ShowWindow on the taskbar.
// Restores the Windows taskbar when GlassDock exits, crashes, is killed, or its UI stops beating for ~15 s.

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? c, string? w);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr after, string? c, string? w);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
    [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public int msg; public int edge; public RECT rc; public IntPtr lParam; }
    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(int msg, ref APPBARDATA d);
    static int _origState; // original taskbar state + 1, 0 = untouched

    static void RestoreTaskbar()
    {
        if (_origState > 0)
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null), lParam = (IntPtr)(_origState - 1) };
            SHAppBarMessage(10, ref d);
        }
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) ShowWindow(main, 5);
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) ShowWindow(h, 5);
    }

    static int Main(string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out int pid)) return 1;
        Process? parent;
        try { parent = Process.GetProcessById(pid); } catch { RestoreTaskbar(); return 0; }

        using var mutex = new Mutex(true, "GlassDock.Watchdog", out bool first);
        if (!first) return 0;

        bool hungRestored = false;
        while (true)
        {
            if (parent.WaitForExit(3000)) { RestoreTaskbar(); return 0; }

            try
            {
                using var mmf = MemoryMappedFile.OpenExisting("Local\\GlassDock.Heartbeat");
                using var view = mmf.CreateViewAccessor(0, 16);
                long last = view.ReadInt64(0);
                bool hidden = view.ReadByte(8) == 1;
                bool clean = view.ReadByte(9) == 1;
                _origState = view.ReadByte(10);
                if (clean) { RestoreTaskbar(); return 0; }
                long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last;
                if (age > 15000 && hidden && !hungRestored) { RestoreTaskbar(); hungRestored = true; }
                if (age < 5000) hungRestored = false; // recovered; GlassDock re-hides on its own setting change
            }
            catch { RestoreTaskbar(); }
        }
    }
}
