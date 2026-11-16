using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using GlassDock.Native;

namespace GlassDock.Services;

/// <summary>CPU / memory / battery / network, all read locally with plain Win32 calls.</summary>
public sealed class SystemStats
{
    private ulong _lastIdle, _lastTotal;
    private long _lastBytes;
    private DateTime _lastNetAt = DateTime.MinValue;

    public double CpuPercent { get; private set; }
    public double MemPercent { get; private set; }
    public int? BatteryPercent { get; private set; }
    public bool Charging { get; private set; }
    public double NetKBps { get; private set; }

    public void Sample()
    {
        if (Win32.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong total = kernel.Value + user.Value; // kernel includes idle
            ulong dIdle = idle.Value - _lastIdle, dTotal = total - _lastTotal;
            if (_lastTotal != 0 && dTotal > 0) CpuPercent = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            _lastIdle = idle.Value; _lastTotal = total;
        }

        var mem = new Win32.MEMORYSTATUSEX();
        if (Win32.GlobalMemoryStatusEx(mem)) MemPercent = mem.dwMemoryLoad;

        var p = System.Windows.Forms.SystemInformation.PowerStatus;
        BatteryPercent = p.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
            ? null : (int)Math.Round(p.BatteryLifePercent * 100);
        Charging = p.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

        long bytes = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var s = ni.GetIPStatistics();
            bytes += s.BytesReceived + s.BytesSent;
        }
        var now = DateTime.UtcNow;
        if (_lastNetAt != DateTime.MinValue)
        {
            double secs = (now - _lastNetAt).TotalSeconds;
            if (secs > 0) NetKBps = Math.Max(0, (bytes - _lastBytes) / 1024.0 / secs);
        }
        _lastBytes = bytes; _lastNetAt = now;
    }
}

/// <summary>Open-Meteo: free, no key, no account. Called directly from this PC.</summary>
public static class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public record Now(double TempC, int Code, double High, double Low);

    public static async Task<Now?> GetAsync(double lat, double lon)
    {
        try
        {
            var url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            return new Now(cur.GetProperty("temperature_2m").GetDouble(), cur.GetProperty("weather_code").GetInt32(),
                daily.GetProperty("temperature_2m_max")[0].GetDouble(), daily.GetProperty("temperature_2m_min")[0].GetDouble());
        }
        catch { return null; }
    }

    public static (string icon, string text) Describe(int code) => code switch
    {
        0 => ("☀", "晴"),
        1 or 2 => ("⛅", "多云"),
        3 => ("☁", "阴"),
        45 or 48 => ("🌫", "雾"),
        >= 51 and <= 57 => ("🌦", "毛毛雨"),
        >= 61 and <= 67 => ("🌧", "雨"),
        >= 71 and <= 77 => ("❄", "雪"),
        >= 80 and <= 82 => ("🌧", "阵雨"),
        >= 85 and <= 86 => ("🌨", "阵雪"),
        >= 95 => ("⛈", "雷暴"),
        _ => ("•", "—")
    };

    /// <summary>City name to coordinates (Open-Meteo geocoding, supports Chinese names).</summary>
    public static async Task<(string name, double lat, double lon)?> GeocodeAsync(string city)
    {
        try
        {
            var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=zh";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var r) || r.GetArrayLength() == 0) return null;
            var first = r[0];
            return (first.GetProperty("name").GetString() ?? city, first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
        }
        catch { return null; }
    }
}
