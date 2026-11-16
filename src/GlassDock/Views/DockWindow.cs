using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using GlassDock.Native;
using GlassDock.Services;
using GlassDock.Widgets;

namespace GlassDock.Views;

/// <summary>
/// Two windows work together:
///  - GlassBackdrop: the pill-shaped bar. DWM blurs whatever is behind it in real time (no redraw by us).
///  - DockWindow (this): a transparent, taller overlay holding icons and widgets, so magnified icons can rise
///    above the bar like the macOS dock.
/// </summary>
public sealed class DockWindow : Window
{
    private readonly GlassBackdrop _backdrop = new();
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly Border _bar;
    private double _unit = 52;
    private const double MaxScale = 1.6;
    private int _appBarMsg;
    private bool _appBarRegistered;
    private Point _dragStart;
    private FrameworkElement? _dragItem;

    private AppSettings S => App.Current.SettingsData;

    public DockWindow()
    {
        Title = "GlassDock";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        AllowDrop = true;

        _bar = new Border { Child = _row, HorizontalAlignment = HorizontalAlignment.Center, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        Content = new Grid { Children = { _bar } };

        _row.MouseMove += (_, e) => Magnify(e.GetPosition(_row).X);
        _row.MouseLeave += (_, _) => ResetMagnify();
        _bar.SizeChanged += (_, _) => PlaceBackdrop();

        ContextMenu = BuildDockMenu();
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = DragDropEffects.Copy | DragDropEffects.Move; e.Handled = true; };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, (ex | Win32.WS_EX_TOOLWINDOW) & ~Win32.WS_EX_APPWINDOW);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
        Loaded += (_, _) => { _backdrop.Owner = this; _backdrop.Show(); Rebuild(); };
        Closed += (_, _) => { UnregisterAppBar(); _backdrop.Close(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(Position);
    }

    // ---------------- layout ----------------

    public void Rebuild()
    {
        _unit = S.Size switch { DockSize.Small => 40, DockSize.Large => 64, _ => 52 };
        Theme.Use(S.Style);
        _backdrop.ApplyStyle(S.Style, _unit);

        _row.Children.Clear();
        _row.Margin = new Thickness(_unit * 0.22, 0, _unit * 0.22, 0);
        _row.Children.Add(StartButton());
        foreach (var entry in S.Entries)
        {
            var el = BuildEntry(entry);
            if (el == null) continue;
            el.Tag = entry;
            HookReorder(el);
            _row.Children.Add(el);
        }
        _bar.VerticalAlignment = S.Edge == DockEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _row.VerticalAlignment = _bar.VerticalAlignment;
        double pad = _unit * 0.16;
        _bar.Padding = new Thickness(0, pad, 0, pad);
        Position();
    }

    private FrameworkElement? BuildEntry(DockEntry e) => e.Kind switch
    {
        "app" when e.App != null => AppIcon(e.App, e),
        "folder" => FolderIcon(e),
        "separator" => new Border { Width = 1, Height = _unit * 0.7, Margin = new Thickness(_unit * 0.15, 0, _unit * 0.15, _unit * 0.15), Background = Theme.TileBorder, VerticalAlignment = VerticalAlignment.Center },
        "widget" when e.WidgetId != null => WidgetWithMenu(e),
        _ => null
    };

    private FrameworkElement? WidgetWithMenu(DockEntry e)
    {
        var w = WidgetRegistry.Create(e.WidgetId!, _unit);
        if (w == null) return null;
        w.VerticalAlignment = S.Edge == DockEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        w.ContextMenu = ItemMenu(e, isApp: false);
        return w;
    }

    private FrameworkElement IconTile(ImageSource? img, string label, double size)
    {
        var tile = new Border
        {
            Width = size, Height = size,
            Margin = new Thickness(_unit * 0.06, 0, _unit * 0.06, 0),
            Background = Brushes.Transparent,
            ToolTip = label,
            Cursor = Cursors.Hand,
            VerticalAlignment = S.Edge == DockEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            RenderTransformOrigin = new Point(0.5, S.Edge == DockEdge.Top ? 0 : 1),
            LayoutTransform = new ScaleTransform(1, 1),
            Child = img != null
                ? new Image { Source = img, Stretch = Stretch.Uniform, Margin = new Thickness(size * 0.06) }
                : new TextBlock { Text = label.Length > 0 ? label[..1] : "?", FontSize = size * 0.45, Foreground = Theme.Foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        RenderOptions.SetBitmapScalingMode(tile, BitmapScalingMode.HighQuality);
        tile.SetValue(IsMagnifiableProperty, true);
        return tile;
    }

    private FrameworkElement AppIcon(PinnedApp app, DockEntry entry)
    {
        var tile = IconTile(IconCache.Get(app.Path), app.Name, _unit);
        tile.MouseLeftButtonUp += (_, e) => { if (_dragItem == null) { Bounce(tile); IconCache.Launch(app); } };
        tile.ContextMenu = ItemMenu(entry, isApp: true);
        return tile;
    }

    private FrameworkElement FolderIcon(DockEntry entry)
    {
        var apps = entry.FolderApps ?? new();
        var grid = new UniformGrid { Rows = 2, Columns = 2, Margin = new Thickness(_unit * 0.12) };
        foreach (var a in apps.Take(4)) grid.Children.Add(new Image { Source = IconCache.Get(a.Path), Margin = new Thickness(1) });
        var tile = new Border
        {
            Width = _unit, Height = _unit,
            Margin = new Thickness(_unit * 0.06, 0, _unit * 0.06, 0),
            CornerRadius = new CornerRadius(_unit * 0.24),
            Background = Theme.TileBackground,
            BorderBrush = Theme.TileBorder,
            BorderThickness = new Thickness(1),
            ToolTip = entry.FolderName ?? "文件夹",
            Cursor = Cursors.Hand,
            VerticalAlignment = S.Edge == DockEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            LayoutTransform = new ScaleTransform(1, 1),
            Child = grid
        };
        tile.SetValue(IsMagnifiableProperty, true);
        tile.MouseLeftButtonUp += (_, _) => { if (_dragItem == null) OpenFolder(tile, entry); };
        tile.ContextMenu = ItemMenu(entry, isApp: false);
        return tile;
    }

    private void OpenFolder(FrameworkElement anchor, DockEntry entry)
    {
        var panel = new WrapPanel { MaxWidth = _unit * 5.5 };
        Popup? popup = null;
        foreach (var a in entry.FolderApps ?? new())
        {
            var item = new StackPanel { Width = _unit * 1.3, Margin = new Thickness(4), Cursor = Cursors.Hand };
            item.Children.Add(new Image { Source = IconCache.Get(a.Path), Width = _unit * 0.9, Height = _unit * 0.9 });
            item.Children.Add(new TextBlock { Text = a.Name, Foreground = Theme.Foreground, FontSize = 12, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var app = a;
            item.MouseLeftButtonUp += (_, _) => { popup!.IsOpen = false; IconCache.Launch(app); };
            var cm = new ContextMenu();
            var rm = new MenuItem { Header = "移出文件夹" };
            rm.Click += (_, _) =>
            {
                entry.FolderApps!.Remove(app);
                S.Entries.Insert(S.Entries.IndexOf(entry) + 1, new DockEntry { Kind = "app", App = app });
                if (entry.FolderApps.Count == 0) S.Entries.Remove(entry);
                S.Save(); popup!.IsOpen = false; Rebuild();
            };
            cm.Items.Add(rm);
            item.ContextMenu = cm;
            panel.Children.Add(item);
        }
        var title = new TextBlock { Text = entry.FolderName ?? "文件夹", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.Foreground, Margin = new Thickness(4, 0, 0, 8) };
        popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = S.Edge == DockEdge.Top ? PlacementMode.Bottom : PlacementMode.Top,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = Theme.PopupBackground, BorderBrush = Theme.TileBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16), Padding = new Thickness(12),
                Child = new StackPanel { Children = { title, panel } }
            }
        };
        popup.IsOpen = true;
    }

    private FrameworkElement StartButton()
    {
        var glyph = new TextBlock { Text = "⊞", FontSize = _unit * 0.55, Foreground = Theme.Foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border { Width = _unit, Height = _unit, Child = glyph, ToolTip = "开始", Cursor = Cursors.Hand, Background = Brushes.Transparent,
            VerticalAlignment = S.Edge == DockEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom, LayoutTransform = new ScaleTransform(1, 1) };
        b.SetValue(IsMagnifiableProperty, true);
        b.MouseLeftButtonUp += (_, _) => OpenStartMenu();
        return b;
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    private static void OpenStartMenu() { keybd_event(0x5B, 0, 0, UIntPtr.Zero); keybd_event(0x5B, 0, 2, UIntPtr.Zero); }

    // ---------------- magnification ----------------

    public static readonly DependencyProperty IsMagnifiableProperty =
        DependencyProperty.RegisterAttached("IsMagnifiable", typeof(bool), typeof(DockWindow), new PropertyMetadata(false));

    private void Magnify(double mouseX)
    {
        if (!S.Magnify) return;
        double range = _unit * 2.6;
        foreach (FrameworkElement child in _row.Children)
        {
            if (!(bool)child.GetValue(IsMagnifiableProperty) || child.LayoutTransform is not ScaleTransform st) continue;
            var center = child.TranslatePoint(new Point(child.ActualWidth / 2, 0), _row).X;
            double d = Math.Abs(mouseX - center);
            double scale = d >= range ? 1 : 1 + (MaxScale - 1) * (Math.Cos(d / range * Math.PI) + 1) / 2;
            st.ScaleX = st.ScaleY = scale;
        }
    }

    private void ResetMagnify()
    {
        foreach (FrameworkElement child in _row.Children)
        {
            if (child.LayoutTransform is not ScaleTransform st || st.ScaleX == 1) continue;
            var anim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase() };
            anim.Completed += (_, _) => { st.BeginAnimation(ScaleTransform.ScaleXProperty, null); st.BeginAnimation(ScaleTransform.ScaleYProperty, null); st.ScaleX = st.ScaleY = 1; };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    private void Bounce(FrameworkElement el)
    {
        var tt = new TranslateTransform();
        el.RenderTransform = tt;
        double dir = S.Edge == DockEdge.Top ? 1 : -1;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(500) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(dir * _unit * 0.3, KeyTime.FromPercent(0.4), new QuadraticEase()));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new BounceEase { Bounces = 1 }));
        tt.BeginAnimation(TranslateTransform.YProperty, a);
    }

    // ---------------- menus ----------------

    private ContextMenu BuildDockMenu()
    {
        var m = new ContextMenu();
        void Add(string text, Action act) { var i = new MenuItem { Header = text }; i.Click += (_, _) => act(); m.Items.Add(i); }
        Add("小组件库…", () => new WidgetLibraryWindow().Show());
        Add("添加应用…", AddAppDialog);
        Add("新建文件夹", () => { S.Entries.Add(new DockEntry { Kind = "folder", FolderName = "新文件夹", FolderApps = new() }); S.Save(); Rebuild(); });
        Add("添加分隔线", () => { S.Entries.Add(new DockEntry { Kind = "separator" }); S.Save(); Rebuild(); });
        m.Items.Add(new Separator());
        Add("设置…", () => new SettingsWindow().Show());
        Add("退出 GlassDock", () => App.Current.Quit());
        return m;
    }

    private ContextMenu ItemMenu(DockEntry entry, bool isApp)
    {
        var m = new ContextMenu();
        if (isApp)
        {
            var move = new MenuItem { Header = "移到文件夹" };
            foreach (var f in S.Entries.Where(x => x.Kind == "folder"))
            {
                var mi = new MenuItem { Header = f.FolderName };
                var folder = f;
                mi.Click += (_, _) => { (folder.FolderApps ??= new()).Add(entry.App!); S.Entries.Remove(entry); S.Save(); Rebuild(); };
                move.Items.Add(mi);
            }
            var nf = new MenuItem { Header = "新建文件夹…" };
            nf.Click += (_, _) =>
            {
                var name = Prompt("文件夹名称", "新文件夹");
                if (name == null) return;
                int idx = S.Entries.IndexOf(entry);
                S.Entries[idx] = new DockEntry { Kind = "folder", FolderName = name, FolderApps = new() { entry.App! } };
                S.Save(); Rebuild();
            };
            move.Items.Add(nf);
            m.Items.Add(move);
        }
        if (entry.Kind == "folder")
        {
            var rn = new MenuItem { Header = "重命名…" };
            rn.Click += (_, _) => { var n = Prompt("文件夹名称", entry.FolderName ?? ""); if (n != null) { entry.FolderName = n; S.Save(); Rebuild(); } };
            m.Items.Add(rn);
        }
        var remove = new MenuItem { Header = "从 Dock 移除" };
        remove.Click += (_, _) => { S.Entries.Remove(entry); S.Save(); Rebuild(); };
        m.Items.Add(remove);
        m.Items.Add(new Separator());
        foreach (MenuItem mi in BuildDockMenu().Items.OfType<MenuItem>().ToList())
        {
            var copy = new MenuItem { Header = mi.Header };
            var src = mi;
            copy.Click += (_, _) => src.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            m.Items.Add(copy);
        }
        return m;
    }

    private void AddAppDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "程序或快捷方式|*.exe;*.lnk;*.url|所有文件|*.*", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames) Pin(f);
        S.Save(); Rebuild();
    }

    private void Pin(string path)
    {
        var app = new PinnedApp { Name = Path.GetFileNameWithoutExtension(path), Path = path };
        int sep = S.Entries.FindIndex(x => x.Kind is "separator" or "widget");
        S.Entries.Insert(sep < 0 ? S.Entries.Count : sep, new DockEntry { Kind = "app", App = app });
    }

    private static string? Prompt(string title, string value)
    {
        var box = new TextBox { Text = value, Margin = new Thickness(12), MinWidth = 220 };
        var ok = new Button { Content = "确定", IsDefault = true, Margin = new Thickness(12, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(16, 4, 16, 4) };
        var w = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize, Topmost = true, Content = new StackPanel { Children = { box, ok } } };
        ok.Click += (_, _) => w.DialogResult = true;
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }

    // ---------------- drag & drop ----------------

    private void HookReorder(FrameworkElement el)
    {
        el.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); _dragItem = null; };
        el.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragItem != null) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) < 8 && Math.Abs(p.Y - _dragStart.Y) < 8) return;
            _dragItem = el;
            el.Opacity = 0.5;
            DragDrop.DoDragDrop(el, new DataObject("GlassDock.Entry", el.Tag), DragDropEffects.Move);
            el.Opacity = 1;
            Dispatcher.BeginInvoke(() => _dragItem = null, System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        int target = InsertIndexAt(e.GetPosition(_row).X);
        if (e.Data.GetData("GlassDock.Entry") is DockEntry moved)
        {
            int from = S.Entries.IndexOf(moved);
            if (from < 0) return;
            S.Entries.RemoveAt(from);
            if (target > from) target--;
            S.Entries.Insert(Math.Clamp(target, 0, S.Entries.Count), moved);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files.Reverse())
                S.Entries.Insert(Math.Clamp(target, 0, S.Entries.Count), new DockEntry { Kind = "app", App = new PinnedApp { Name = Path.GetFileNameWithoutExtension(f), Path = f } });
        }
        else if (e.Data.GetData("GlassDock.Widget") is string widgetId)
        {
            S.Entries.Insert(Math.Clamp(target, 0, S.Entries.Count), new DockEntry { Kind = "widget", WidgetId = widgetId });
        }
        else return;
        S.Save();
        Rebuild();
    }

    /// <summary>Index into S.Entries before which a drop at x lands (the start button is child 0 and not an entry).</summary>
    private int InsertIndexAt(double x)
    {
        int idx = 0;
        foreach (FrameworkElement child in _row.Children)
        {
            if (child.Tag is not DockEntry entry) continue;
            var mid = child.TranslatePoint(new Point(child.ActualWidth / 2, 0), _row).X;
            if (x < mid) return S.Entries.IndexOf(entry);
            idx = S.Entries.IndexOf(entry) + 1;
        }
        return idx;
    }

    // ---------------- positioning / AppBar ----------------

    private void Position()
    {
        var wa = SystemParameters.WorkArea;
        double screenW = SystemParameters.PrimaryScreenWidth, screenH = SystemParameters.PrimaryScreenHeight;
        double gap = 8;
        double barH = _unit * 1.32;
        double winH = barH + _unit * (MaxScale - 1) + gap + 4;

        Width = screenW;
        Height = winH;
        Left = 0;
        Top = S.Edge == DockEdge.Top ? 0 : screenH - winH;
        _bar.Margin = S.Edge == DockEdge.Top ? new Thickness(0, gap, 0, 0) : new Thickness(0, 0, 0, gap);

        if (S.ReserveScreenSpace) RegisterAppBar(barH + gap * 2); else UnregisterAppBar();
        Dispatcher.BeginInvoke(PlaceBackdrop, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void PlaceBackdrop()
    {
        if (!IsLoaded || _bar.ActualWidth == 0) return;
        var tl = _bar.TranslatePoint(new Point(0, 0), this);
        double barH = _unit * 1.32;
        double y = S.Edge == DockEdge.Top ? tl.Y : tl.Y + _bar.ActualHeight - barH;
        _backdrop.Place(Left + tl.X, Top + y, _bar.ActualWidth, barH);
        // only the bar area (plus magnify head-room above it) should catch the mouse
    }

    private void RegisterAppBar(double thickness)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var abd = new Win32.APPBARDATA { cbSize = Marshal.SizeOf<Win32.APPBARDATA>(), hWnd = hwnd };
        if (!_appBarRegistered)
        {
            _appBarMsg = Win32.RegisterWindowMessage("GlassDockAppBar");
            abd.uCallbackMessage = _appBarMsg;
            Win32.SHAppBarMessage(Win32.ABM_NEW, ref abd);
            _appBarRegistered = true;
        }
        int sw = (int)(SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX);
        int sh = (int)(SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY);
        int th = (int)(thickness * dpi.DpiScaleY);
        abd.uEdge = S.Edge == DockEdge.Top ? Win32.ABE_TOP : Win32.ABE_BOTTOM;
        abd.rc = S.Edge == DockEdge.Top
            ? new Win32.RECT { left = 0, top = 0, right = sw, bottom = th }
            : new Win32.RECT { left = 0, top = sh - th, right = sw, bottom = sh };
        Win32.SHAppBarMessage(Win32.ABM_QUERYPOS, ref abd);
        if (S.Edge == DockEdge.Top) abd.rc.bottom = abd.rc.top + th; else abd.rc.top = abd.rc.bottom - th;
        Win32.SHAppBarMessage(Win32.ABM_SETPOS, ref abd);
    }

    private void UnregisterAppBar()
    {
        if (!_appBarRegistered) return;
        var abd = new Win32.APPBARDATA { cbSize = Marshal.SizeOf<Win32.APPBARDATA>(), hWnd = new WindowInteropHelper(this).Handle };
        Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref abd);
        _appBarRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // ABN_POSCHANGED = 1: another appbar / taskbar moved; re-assert our position.
        if (_appBarRegistered && msg == _appBarMsg && wParam.ToInt32() == 1) Dispatcher.BeginInvoke(Position);
        return IntPtr.Zero;
    }
}

/// <summary>The blurred pill behind the dock. Never redraws itself; DWM composites the blur.</summary>
public sealed class GlassBackdrop : Window
{
    private readonly Border _sheen = new();
    public GlassBackdrop()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        IsHitTestVisible = false;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });
        Content = _sheen;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            // click-through + no taskbar button
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW | 0x20 /*TRANSPARENT*/ | Win32.WS_EX_NOACTIVATE);
            var src = HwndSource.FromHwnd(hwnd);
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        };
    }

    private DockStyle _style;
    private double _unit;

    public void ApplyStyle(DockStyle style, double unit)
    {
        _style = style; _unit = unit;
        // liquid-glass sheen: a soft top highlight and an inner rim, drawn once (static brushes)
        bool dark = GlassEffect.IsDark(style);
        _sheen.CornerRadius = new CornerRadius(unit * 0.42);
        _sheen.BorderThickness = new Thickness(1);
        _sheen.BorderBrush = new LinearGradientBrush(Color.FromArgb(dark ? (byte)90 : (byte)200, 255, 255, 255), Color.FromArgb(30, 255, 255, 255), 90);
        _sheen.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(dark ? (byte)40 : (byte)90, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.45),
                new GradientStop(Color.FromArgb(dark ? (byte)20 : (byte)35, 255, 255, 255), 1),
            }
        };
        if (IsLoaded) GlassEffect.Apply(this, style);
        else Loaded += (_, _) => GlassEffect.Apply(this, _style);
    }

    public void Place(double x, double y, double w, double h)
    {
        Left = x; Top = y; Width = w; Height = h;
        // round the blur itself on Win10 too
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int r = (int)(_unit * 0.42 * 2 * dpi.DpiScaleX);
        var rgn = CreateRoundRectRgn(0, 0, (int)(w * dpi.DpiScaleX) + 1, (int)(h * dpi.DpiScaleY) + 1, r, r);
        SetWindowRgn(hwnd, rgn, true);
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
}
