using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlassDock.Services;

public enum DockEdge { Bottom, Top }
public enum DockStyle { ClearGlass, FrostedGlass, TintedGlass, Dark, Light, Midnight }
public enum DockSize { Small, Medium, Large }

public class PinnedApp
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Arguments { get; set; }
}

/// <summary>One slot on the dock: an app, a folder of apps, or a widget.</summary>
public class DockEntry
{
    public string Kind { get; set; } = "app";          // app | folder | widget | separator
    public PinnedApp? App { get; set; }
    public string? FolderName { get; set; }
    public List<PinnedApp>? FolderApps { get; set; }
    public string? WidgetId { get; set; }              // clock, weather, calendar, todo, system, media
}

public class AppSettings
{
    public DockEdge Edge { get; set; } = DockEdge.Bottom;
    public DockStyle Style { get; set; } = DockStyle.FrostedGlass;
    public DockSize Size { get; set; } = DockSize.Medium;
    public bool Magnify { get; set; } = true;
    public bool Use24HourClock { get; set; } = true;
    public bool HideWindowsTaskbar { get; set; } = false;
    public bool ReserveScreenSpace { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public string Language { get; set; } = "zh";

    // Weather (Open-Meteo, no key). Default: Dongying, Shandong.
    public string WeatherCity { get; set; } = "东营";
    public double WeatherLat { get; set; } = 37.43;
    public double WeatherLon { get; set; } = 118.67;

    public List<string> Todos { get; set; } = new();
    public List<DockEntry> Entries { get; set; } = new();

    [JsonIgnore] public static string DataDir { get; private set; } = "";
    [JsonIgnore] private static string FilePath => Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Portable first: data\ next to the exe; falls back to %AppData%\GlassDock if not writable.</summary>
    public static AppSettings Load()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            Directory.CreateDirectory(portable);
            var probe = Path.Combine(portable, ".w");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            DataDir = portable;
        }
        catch
        {
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassDock");
            Directory.CreateDirectory(DataDir);
        }

        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? Default();
        }
        catch { /* corrupt file: start fresh */ }
        return Default();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); } catch { }
    }

    private static AppSettings Default()
    {
        var s = new AppSettings();
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        void App(string name, string path) { if (File.Exists(path)) s.Entries.Add(new DockEntry { Kind = "app", App = new PinnedApp { Name = name, Path = path } }); }
        App("文件资源管理器", Path.Combine(win, "explorer.exe"));
        App("记事本", Path.Combine(win, "System32", "notepad.exe"));
        App("计算器", Path.Combine(win, "System32", "calc.exe"));
        s.Entries.Add(new DockEntry { Kind = "separator" });
        foreach (var w in new[] { "system", "weather", "media", "calendar", "clock" })
            s.Entries.Add(new DockEntry { Kind = "widget", WidgetId = w });
        return s;
    }
}
