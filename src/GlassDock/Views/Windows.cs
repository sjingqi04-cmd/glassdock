using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassDock.Services;
using GlassDock.Widgets;

namespace GlassDock.Views;

/// <summary>Settings: style, size, position, clock, taskbar, autostart, weather city. Changes apply live.</summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    private AppSettings S => App.Current.SettingsData;

    public SettingsWindow()
    {
        if (_open != null) { _open.Activate(); Loaded += (_, _) => Close(); return; }
        _open = this;
        Closed += (_, _) => _open = null;

        Title = "GlassDock 设置";
        Width = 440; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        FontSize = 14;

        var root = new StackPanel { Margin = new Thickness(24) };

        root.Children.Add(Header("外观"));
        root.Children.Add(Row("样式", Combo(new[] { "透明玻璃", "磨砂玻璃", "染色玻璃", "深色", "浅色", "午夜" }, (int)S.Style,
            i => { S.Style = (DockStyle)i; Apply(); })));
        root.Children.Add(Row("尺寸", Combo(new[] { "小", "中", "大" }, (int)S.Size, i => { S.Size = (DockSize)i; Apply(); })));
        root.Children.Add(Row("位置", Combo(new[] { "屏幕底部", "屏幕顶部" }, (int)S.Edge, i => { S.Edge = (DockEdge)i; Apply(); })));
        root.Children.Add(Check("鼠标悬停时放大图标", S.Magnify, v => { S.Magnify = v; Apply(); }));
        root.Children.Add(Check("24 小时制时钟", S.Use24HourClock, v => { S.Use24HourClock = v; Apply(); }));

        root.Children.Add(Header("系统"));
        root.Children.Add(Check("隐藏 Windows 任务栏（GlassDock 退出或崩溃时自动恢复）", S.HideWindowsTaskbar, v => { S.HideWindowsTaskbar = v; S.Save(); App.Current.ApplyTaskbar(); }));
        root.Children.Add(Check("为 Dock 预留屏幕空间（窗口不会盖住它）", S.ReserveScreenSpace, v => { S.ReserveScreenSpace = v; Apply(); }));
        root.Children.Add(Check("开机启动", S.StartWithWindows, v => { S.StartWithWindows = v; S.Save(); try { Autostart.Set(v); } catch { } }));

        root.Children.Add(Header("天气"));
        var city = new TextBox { Text = S.WeatherCity, Width = 160, Padding = new Thickness(6, 3, 6, 3) };
        var status = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var go = new Button { Content = "更新", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0) };
        go.Click += async (_, _) =>
        {
            status.Text = "查找中…";
            var r = await WeatherService.GeocodeAsync(city.Text.Trim());
            if (r == null) { status.Text = "没找到这个城市"; return; }
            S.WeatherCity = r.Value.name; S.WeatherLat = r.Value.lat; S.WeatherLon = r.Value.lon;
            status.Text = $"已设为 {r.Value.name}";
            Apply();
        };
        root.Children.Add(Row("城市", new StackPanel { Orientation = Orientation.Horizontal, Children = { city, go, status } }));

        root.Children.Add(new TextBlock
        {
            Text = "GlassDock 0.1 · MIT 开源 · 无账号、无服务器、无遥测\n设置保存在程序目录的 data 文件夹（不可写时存到 %AppData%\\GlassDock）",
            Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 18, 0, 0), TextWrapping = TextWrapping.Wrap
        });
        Content = root;
    }

    private void Apply() { S.Save(); App.Current.RebuildDock(); }

    private static TextBlock Header(string s) => new() { Text = s, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };

    private static UIElement Row(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(control, 1);
        g.Children.Add(t); g.Children.Add(control);
        return g;
    }

    private static ComboBox Combo(string[] items, int selected, Action<int> changed)
    {
        var c = new ComboBox { ItemsSource = items, SelectedIndex = selected, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        c.SelectionChanged += (_, _) => changed(c.SelectedIndex);
        return c;
    }

    private static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, 5, 0, 5) };
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }
}

/// <summary>Widget gallery: click "添加" or drag a card onto the dock.</summary>
public sealed class WidgetLibraryWindow : Window
{
    private static WidgetLibraryWindow? _open;

    public WidgetLibraryWindow()
    {
        if (_open != null) { _open.Activate(); Loaded += (_, _) => Close(); return; }
        _open = this;
        Closed += (_, _) => _open = null;

        Title = "小组件库";
        Width = 520; Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        Topmost = true;

        var wrap = new WrapPanel { Margin = new Thickness(16) };
        foreach (var w in WidgetRegistry.All)
        {
            var add = new Button { Content = "添加", Padding = new Thickness(14, 3, 14, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            var id = w.Id;
            add.Click += (_, _) =>
            {
                App.Current.SettingsData.Entries.Add(new DockEntry { Kind = "widget", WidgetId = id });
                App.Current.SettingsData.Save();
                App.Current.RebuildDock();
            };
            var card = new Border
            {
                Width = 150, Margin = new Thickness(6), Padding = new Thickness(12),
                CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromRgb(242, 243, 247)),
                Cursor = Cursors.SizeAll,
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = w.Name, FontSize = 15, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = w.Description, FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) },
                        add
                    }
                }
            };
            card.MouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not Button)
                    DragDrop.DoDragDrop(card, new DataObject("GlassDock.Widget", id), DragDropEffects.Copy);
            };
            wrap.Children.Add(card);
        }
        Content = new DockPanel
        {
            Children =
            {
                new TextBlock { Text = "点“添加”放到 Dock 末尾，或直接把卡片拖到 Dock 上想放的位置。在 Dock 上拖动可重新排列，右键可移除。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 16, 22, 0), Foreground = Brushes.DimGray },
                new ScrollViewer { Content = wrap }
            }
        };
        DockPanel.SetDock(((DockPanel)Content).Children[0], System.Windows.Controls.Dock.Top);
    }
}
