using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using GlassDock.Services;

namespace GlassDock.Widgets;

public record WidgetInfo(string Id, string Name, string Description);

public static class WidgetRegistry
{
    public static readonly WidgetInfo[] All =
    {
        new("clock", "时钟", "时间与日期，12/24 小时制"),
        new("calendar", "日历", "今天的日期，点开看整月"),
        new("weather", "天气", "Open-Meteo 实时天气，免密钥"),
        new("system", "系统状态", "CPU、内存、电池、网速"),
        new("media", "音乐控制", "当前播放与上一首/暂停/下一首"),
        new("todo", "待办", "本机保存的待办清单"),
    };

    public static WidgetBase? Create(string id, double unit) => id switch
    {
        "clock" => new ClockWidget(unit),
        "calendar" => new CalendarWidget(unit),
        "weather" => new WeatherWidget(unit),
        "system" => new SystemWidget(unit),
        "media" => new MediaWidget(unit),
        "todo" => new TodoWidget(unit),
        _ => null
    };
}

/// <summary>A widget is a rounded tile on the dock. Timers stop while the dock is hidden to keep idle CPU near zero.</summary>
public abstract class WidgetBase : Border
{
    protected readonly double Unit;
    protected Brush Fore => Theme.Foreground;
    protected Brush Dim => Theme.Dim;
    private readonly List<DispatcherTimer> _timers = new();

    protected WidgetBase(double unit)
    {
        Unit = unit;
        Height = unit;
        MinWidth = unit;
        CornerRadius = new CornerRadius(unit * 0.28);
        Background = Theme.TileBackground;
        BorderBrush = Theme.TileBorder;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(unit * 0.18, 0, unit * 0.18, 0);
        Margin = new Thickness(unit * 0.08, 0, unit * 0.08, 0);
        VerticalAlignment = VerticalAlignment.Bottom;
        Loaded += (_, _) => { foreach (var t in _timers) t.Start(); Refresh(); };
        Unloaded += (_, _) => { foreach (var t in _timers) t.Stop(); };
        IsVisibleChanged += (_, e) => { foreach (var t in _timers) { if ((bool)e.NewValue) t.Start(); else t.Stop(); } };
    }

    protected void Every(TimeSpan interval, Action tick)
    {
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        t.Tick += (_, _) => tick();
        _timers.Add(t);
    }

    public virtual void Refresh() { }

    protected TextBlock Text(string s, double scale, bool bold = false, Brush? brush = null) => new()
    {
        Text = s,
        FontSize = Unit * scale,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = brush ?? Fore,
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    protected Popup MakePopup(UIElement content)
    {
        var p = new Popup
        {
            PlacementTarget = this,
            Placement = App.Current.SettingsData.Edge == DockEdge.Top ? PlacementMode.Bottom : PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            VerticalOffset = App.Current.SettingsData.Edge == DockEdge.Top ? 8 : -8,
            Child = new Border
            {
                Background = Theme.PopupBackground,
                BorderBrush = Theme.TileBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14),
                Child = content,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, Opacity = 0.35, ShadowDepth = 4 }
            }
        };
        return p;
    }
}

/// <summary>Colors for the current dock style.</summary>
public static class Theme
{
    public static Brush Foreground = Brushes.White;
    public static Brush Dim = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
    public static Brush TileBackground = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
    public static Brush TileBorder = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
    public static Brush PopupBackground = new SolidColorBrush(Color.FromArgb(235, 32, 32, 36));
    public static Brush Accent = new SolidColorBrush(Color.FromRgb(90, 170, 255));

    public static void Use(DockStyle style)
    {
        bool light = style is DockStyle.Light or DockStyle.FrostedGlass or DockStyle.ClearGlass;
        Foreground = light ? new SolidColorBrush(Color.FromRgb(20, 20, 24)) : Brushes.White;
        Dim = light ? new SolidColorBrush(Color.FromArgb(150, 20, 20, 24)) : new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
        TileBackground = light ? new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(36, 255, 255, 255));
        TileBorder = light ? new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
        PopupBackground = light ? new SolidColorBrush(Color.FromArgb(245, 248, 248, 250)) : new SolidColorBrush(Color.FromArgb(240, 30, 30, 34));
    }
}

public sealed class ClockWidget : WidgetBase
{
    private readonly TextBlock _time, _date;
    public ClockWidget(double unit) : base(unit)
    {
        _time = Text("", 0.36, true);
        _date = Text("", 0.2, false, Dim);
        Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _time, _date } };
        Every(TimeSpan.FromSeconds(1), Refresh);
    }
    public override void Refresh()
    {
        var now = DateTime.Now;
        string t = App.Current.SettingsData.Use24HourClock ? now.ToString("HH:mm") : now.ToString("h:mm tt");
        if (_time.Text != t) _time.Text = t;
        _date.Text = now.ToString("M月d日 ddd");
    }
}

public sealed class CalendarWidget : WidgetBase
{
    private readonly TextBlock _day, _week;
    public CalendarWidget(double unit) : base(unit)
    {
        Width = unit;
        Padding = new Thickness(0);
        _week = Text("", 0.17, true, Brushes.OrangeRed);
        _day = Text("", 0.42, true);
        _week.HorizontalAlignment = _day.HorizontalAlignment = HorizontalAlignment.Center;
        Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _week, _day } };
        Cursor = Cursors.Hand;
        MouseLeftButtonUp += (_, _) =>
        {
            var cal = new System.Windows.Controls.Calendar { DisplayDate = DateTime.Today, SelectedDate = DateTime.Today };
            MakePopup(cal).IsOpen = true;
        };
        Every(TimeSpan.FromMinutes(1), Refresh);
    }
    public override void Refresh()
    {
        _week.Text = DateTime.Now.ToString("ddd");
        _day.Text = DateTime.Now.Day.ToString();
    }
}

public sealed class WeatherWidget : WidgetBase
{
    private readonly TextBlock _icon, _temp, _sub;
    public WeatherWidget(double unit) : base(unit)
    {
        _icon = Text("…", 0.42);
        _temp = Text("--°", 0.3, true);
        _sub = Text(App.Current.SettingsData.WeatherCity, 0.18, false, Dim);
        _icon.Margin = new Thickness(0, 0, unit * 0.1, 0);
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _icon, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _temp, _sub } } }
        };
        Every(TimeSpan.FromMinutes(20), Refresh);
    }
    public override async void Refresh()
    {
        var s = App.Current.SettingsData;
        var w = await WeatherService.GetAsync(s.WeatherLat, s.WeatherLon);
        if (w == null) { _sub.Text = s.WeatherCity + " · 离线"; return; }
        var (icon, text) = WeatherService.Describe(w.Code);
        _icon.Text = icon;
        _temp.Text = $"{w.TempC:0}°";
        _sub.Text = $"{s.WeatherCity} {text} {w.Low:0}~{w.High:0}°";
        ToolTip = $"{s.WeatherCity}：{text}，{w.TempC:0.#}°C（今日 {w.Low:0}°~{w.High:0}°）";
    }
}

public sealed class SystemWidget : WidgetBase
{
    private readonly SystemStats _stats = new();
    private readonly Ring _cpu, _mem;
    private readonly TextBlock _bat, _net;
    public SystemWidget(double unit) : base(unit)
    {
        _cpu = new Ring(unit * 0.62, "CPU");
        _mem = new Ring(unit * 0.62, "内存");
        _bat = Text("", 0.2, true);
        _net = Text("", 0.18, false, Dim);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(unit * 0.12, 0, 0, 0), Children = { _bat, _net } };
        Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _cpu, _mem, info } };
        Every(TimeSpan.FromSeconds(2), Refresh);
    }
    public override void Refresh()
    {
        _stats.Sample();
        _cpu.Set(_stats.CpuPercent);
        _mem.Set(_stats.MemPercent);
        _bat.Text = _stats.BatteryPercent is { } b ? $"{(_stats.Charging ? "⚡" : "🔋")}{b}%" : "🔌";
        double kb = _stats.NetKBps;
        _net.Text = kb > 1024 ? $"{kb / 1024:0.0} MB/s" : $"{kb:0} KB/s";
    }

    private sealed class Ring : Grid
    {
        private readonly Path _arc = new() { StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly TextBlock _label;
        private readonly double _size;
        private readonly string _name;
        public Ring(double size, string name)
        {
            _size = size; _name = name;
            Width = Height = size;
            Margin = new Thickness(2, 0, 2, 0);
            Children.Add(new Ellipse { Stroke = Theme.TileBorder, StrokeThickness = 3 });
            _arc.Stroke = Theme.Accent;
            Children.Add(_arc);
            _label = new TextBlock { FontSize = size * 0.28, Foreground = Theme.Foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Children.Add(_label);
        }
        public void Set(double pct)
        {
            _label.Text = $"{pct:0}";
            ToolTip = $"{_name} {pct:0}%";
            _arc.Stroke = pct > 85 ? Brushes.OrangeRed : Theme.Accent;
            double r = (_size - 3) / 2, c = _size / 2;
            double a = Math.Clamp(pct, 0.1, 99.9) / 100 * 2 * Math.PI;
            var start = new Point(c, c - r);
            var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
            var fig = new PathFigure { StartPoint = start, Segments = { new ArcSegment(end, new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true) } };
            _arc.Data = new PathGeometry { Figures = { fig } };
        }
    }
}

public sealed class MediaWidget : WidgetBase
{
    private readonly TextBlock _title, _artist;
    private Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager? _mgr;

    public MediaWidget(double unit) : base(unit)
    {
        _title = Text("未在播放", 0.22, true);
        _artist = Text("", 0.18, false, Dim);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = unit * 2.6, Children = { _title, _artist } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(unit * 0.12, 0, 0, 0) };
        buttons.Children.Add(Btn("⏮", async s => await s.TrySkipPreviousAsync()));
        buttons.Children.Add(Btn("⏯", async s => await s.TryTogglePlayPauseAsync()));
        buttons.Children.Add(Btn("⏭", async s => await s.TrySkipNextAsync()));
        Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { text, buttons } };
        Every(TimeSpan.FromSeconds(3), Refresh);
    }

    private UIElement Btn(string glyph, Func<Windows.Media.Control.GlobalSystemMediaTransportControlsSession, Task> act)
    {
        var t = Text(glyph, 0.3);
        t.Margin = new Thickness(Unit * 0.06, 0, Unit * 0.06, 0);
        t.Cursor = Cursors.Hand;
        t.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            var s = (await Manager())?.GetCurrentSession();
            if (s != null) { try { await act(s); } catch { } }
            Refresh();
        };
        return t;
    }

    private async Task<Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager?> Manager()
    {
        try { return _mgr ??= await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch { return null; }
    }

    public override async void Refresh()
    {
        var s = (await Manager())?.GetCurrentSession();
        if (s == null) { _title.Text = "未在播放"; _artist.Text = ""; return; }
        try
        {
            var p = await s.TryGetMediaPropertiesAsync();
            _title.Text = string.IsNullOrWhiteSpace(p.Title) ? "正在播放" : p.Title;
            _artist.Text = p.Artist ?? "";
        }
        catch { }
    }
}

public sealed class TodoWidget : WidgetBase
{
    private readonly TextBlock _count;
    public TodoWidget(double unit) : base(unit)
    {
        _count = Text("", 0.26, true);
        Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { Text("☑", 0.36), _count } };
        _count.Margin = new Thickness(unit * 0.08, 0, 0, 0);
        Cursor = Cursors.Hand;
        MouseLeftButtonUp += (_, _) => Open();
    }

    public override void Refresh() => _count.Text = App.Current.SettingsData.Todos.Count.ToString();

    private void Open()
    {
        var s = App.Current.SettingsData;
        var list = new StackPanel { Width = 260 };
        Popup? popup = null;
        void Render()
        {
            list.Children.Clear();
            list.Children.Add(new TextBlock { Text = "待办", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Fore, Margin = new Thickness(0, 0, 0, 8) });
            foreach (var item in s.Todos.ToList())
            {
                var cb = new CheckBox { Content = new TextBlock { Text = item, Foreground = Fore, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 3, 0, 3) };
                cb.Checked += (_, _) => { s.Todos.Remove(item); s.Save(); Refresh(); Render(); };
                list.Children.Add(cb);
            }
            var box = new TextBox { Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(6, 4, 6, 4) };
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(box.Text)) return;
                s.Todos.Add(box.Text.Trim()); s.Save(); Refresh(); Render();
            };
            list.Children.Add(box);
            box.Loaded += (_, _) => box.Focus();
        }
        Render();
        popup = MakePopup(list);
        popup.IsOpen = true;
    }
}
