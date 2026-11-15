using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DynamicIsland;

/// macOS-style desktop widgets.
/// One transparent, screen-sized window attached to the desktop (behind all windows,
/// still visible on Show Desktop). Empty areas are click-through. Cards can be dragged by their title.
public partial class DesktopWidgets : Window
{
    static readonly CultureInfo En = new("en-US");
    static readonly FontFamily UiFont = new("Segoe UI Variable Display, Segoe UI");
    // Glass: the wallpaper behind each card is blurred + a thin tint layer
    // "Light glass + dark text" style (like macOS widgets on a light wallpaper)
    static readonly Brush CardBg = Ui.Brush(0xB3, 0xFF, 0xFF, 0xFF);         // fallback when the wallpaper can't be read
    static readonly Brush GlassTint = Ui.Brush(0x9E, 0xFF, 0xFF, 0xFF);      // ~62% white layer over the blurred wallpaper
    static readonly Brush CardEdge = Ui.Brush(0xBF, 0xFF, 0xFF, 0xFF);
    static readonly Brush Ink = Ui.Brush(0xFF, 0x1D, 0x1D, 0x1F);            // primary text
    static readonly Brush Sub = Ui.Brush(0xCC, 0x3C, 0x3C, 0x43);            // secondary text
    static readonly Brush Faint = Ui.Brush(0x8C, 0x3C, 0x3C, 0x43);
    static readonly Brush Blue = Ui.Brush(0xFF, 0x1A, 0x73, 0xE8);
    // Accent colors darkened for contrast on a light background
    static readonly Brush Red = Ui.Brush(0xFF, 0xD9, 0x30, 0x25);
    static readonly Brush Green = Ui.Brush(0xFF, 0x1E, 0x8E, 0x3E);
    static readonly Brush Orange = Ui.Brush(0xFF, 0xC9, 0x6A, 0x00);
    static readonly Brush ProgressBlue = Ui.Brush(0xFF, 0x0A, 0x6C, 0xDF);

    const string SettingsKey = @"Software\DynamicIsland";
    static readonly string LayoutPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "widgets-v2.json");   // v2 = new layout (big center clock)

    readonly Canvas _canvas = new();
    readonly Dictionary<string, Point> _layout = new();
    readonly Dictionary<string, double> _scale = new();   // content zoom per widget (1 = normal)
    readonly Dictionary<string, (double W, double H)> _size = new();   // card width & height (0 = automatic)
    const double MinScale = 0.7, MaxScale = 1.8;
    readonly DispatcherTimer _clockTimer = new();
    readonly DispatcherTimer _perfTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    // Content that gets updated
    readonly StackPanel _calendarBody = new(), _notesBody = new(), _prayerBody = new();
    readonly TextBlock _prayerCity = new(), _prayerNext = new();
    readonly TextBlock _cpuText = new(), _memText = new(), _memDetail = new();
    readonly Border _cpuBar = new(), _memBar = new();
    readonly Polyline _cpuGraph = new();
    readonly Queue<double> _cpuHistory = new();

    public static bool Enabled
    {
        get
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(SettingsKey); return k?.GetValue("DesktopWidgets") is not int v || v != 0; }
            catch { return true; }
        }
        set
        {
            try { using var k = Registry.CurrentUser.CreateSubKey(SettingsKey); k.SetValue("DesktopWidgets", value ? 1 : 0, RegistryValueKind.DWord); }
            catch { }
        }
    }

    public DesktopWidgets()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Title = "Desktop Widgets";
        FontFamily = UiFont;
        Content = _canvas;
        PlaceOnWorkArea();
        SystemParameters.StaticPropertyChanged += (_, e) => { if (e.PropertyName == nameof(SystemParameters.WorkArea)) PlaceOnWorkArea(); };

        // Big clock in the center; cards stacked in left & right columns (center left empty)
        BuildBigClock();
        AddCard("prayer", PrayerCard(), 0);
        AddCard("upnext", UpNextCard(), 0);
        AddCard("calendar", CalendarCard(), 0);
        AddCard("links", QuickLinksCard(), 0);
        AddCard("focus", FocusCard(), 1);
        AddCard("deadlines", DeadlinesCard(), 1);
        AddCard("notes", NotesCard(), 1);
        AddCard("performance", PerformanceCard(), 1);
        AddCard("weather", WeatherCard(), 1);
        PrayerTimes.LocationChanged += () => Dispatcher.BeginInvoke(RebuildPrayer);

        _clockTimer.Tick += (_, _) => UpdateClockAndCalendar();
        _perfTimer.Tick += (_, _) => { if (!Perf.Fullscreen && !DesktopCovered()) UpdatePerformance(); };   // not visible = no need to redraw
        NotesStore.Changed += () => { RebuildNotes(); RebuildDeadlines(); RebuildUpNext(); };
        Perf.Changed += () => _perfTimer.Interval = Perf.Scale(2);

        // Wallpaper changed → rebuild the glass
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.Desktop) Dispatcher.BeginInvoke(BuildBlurredWallpaper);
        };

        Loaded += async (_, _) =>
        {
            BuildBlurredWallpaper();
            // First run: try Windows location; if not allowed, keep the default city (change via right-click)
            if (!PrayerTimes.HasSavedLocation) await PrayerTimes.DetectLocationAsync();
            UpdateClockAndCalendar();
            RebuildNotes();
            RebuildDeadlines();
            RebuildQuickLinks();
            UpdatePerformance();
            UpdateFocus();
            _perfTimer.Start();
            // Once card sizes are known: stack the columns & place the clock
            await Dispatcher.InvokeAsync(() => { StackColumns(); PlaceBigClock(); }, DispatcherPriority.ContextIdle);
            StartWeather();
        };
        SizeChanged += (_, _) => { StackColumns(); PlaceBigClock(); };
    }

    void PlaceOnWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left; Top = wa.Top; Width = wa.Width; Height = wa.Height;
        foreach (var c in _cards) UpdateGlass(c);
        UpdateClockGlass();
    }

    // ================= Attached to the desktop =================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        // Tool window (not in Alt+Tab) & doesn't steal focus
        SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80 | 0x08000000);
        // Owner = desktop (Progman): stays visible on Show Desktop (Win+D)
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero) SetWindowLongPtr(h, -8 /* GWLP_HWNDPARENT */, progman);
        HwndSource.FromHwnd(h)?.AddHook(WndProc);
        SetWindowPos(h, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    /// Always at the bottom of the z-order (above the wallpaper, below all windows).
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0046 /* WM_WINDOWPOSCHANGING */)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.hwndInsertAfter = new IntPtr(1);
            pos.flags &= ~0x0004u;   // clear SWP_NOZORDER
            Marshal.StructureToPtr(pos, lParam, false);
        }
        return IntPtr.Zero;
    }

    // ================= Cards & dragging =================

    /// Cards without a saved position are stacked automatically in their column (0 = left, 1 = right).
    readonly List<(Border Card, int Column, bool Auto)> _columns = new();

    void AddCard(string id, (Border Card, FrameworkElement Handle) parts, int column)
    {
        var (card, handle) = parts;
        if (_layout.Count == 0 && _scale.Count == 0) LoadLayout();
        bool auto = !_layout.TryGetValue(id, out var p);
        var pos = auto ? new Point(28, 24) : p;
        _columns.Add((card, column, auto));
        var scale = new ScaleTransform(_scale.GetValueOrDefault(id, 1.0), _scale.GetValueOrDefault(id, 1.0));
        card.RenderTransform = scale;   // scaled from the top-left corner
        Canvas.SetLeft(card, pos.X);
        Canvas.SetTop(card, pos.Y);
        _canvas.Children.Add(card);
        _cards.Add(card);
        card.SizeChanged += (_, _) => { UpdateGlass(card); if (IsLoaded) StackColumns(); };

        // Drag by the card title; position is saved
        Point? grab = null;
        handle.Cursor = Cursors.SizeAll;
        handle.MouseLeftButtonDown += (_, e) => { grab = e.GetPosition(card); handle.CaptureMouse(); e.Handled = true; };
        handle.MouseMove += (_, e) =>
        {
            if (grab is not Point g || e.LeftButton != MouseButtonState.Pressed) return;
            var m = e.GetPosition(_canvas);
            Canvas.SetLeft(card, Math.Clamp(m.X - g.X * scale.ScaleX, 0, Math.Max(0, ActualWidth - card.ActualWidth * scale.ScaleX)));
            Canvas.SetTop(card, Math.Clamp(m.Y - g.Y * scale.ScaleY, 0, Math.Max(0, ActualHeight - card.ActualHeight * scale.ScaleY)));
            UpdateGlass(card);   // glass follows the card
        };
        handle.MouseLeftButtonUp += (_, _) =>
        {
            if (grab == null) return;
            grab = null;
            handle.ReleaseMouseCapture();
            _layout[id] = new Point(Canvas.GetLeft(card), Canvas.GetTop(card));
            MarkPlaced(card);
            SaveLayout();
        };

        // ---- Resize: drag the grip in the bottom-right corner (shown on hover) ----
        var grip = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,12 L 12,0 M 5,12 L 12,5 M 10,12 L 12,10"),
            Stroke = Faint, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        };
        var gripBox = new Border
        {
            Width = 22, Height = 22, Background = Brushes.Transparent, Child = grip, Padding = new Thickness(5),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -14, -12), Cursor = Cursors.SizeNWSE, Opacity = 0,
            ToolTip = "Drag to resize (width & height) · Ctrl + scroll to zoom · double-click to reset"
        };
        var inner = card.Child;
        card.Child = null;
        var layer = new Grid();
        // Content is wrapped & clipped on its own; the resize grip is NOT clipped (it sits outside the content area)
        layer.Children.Add(new Border { Child = inner, ClipToBounds = true });
        layer.Children.Add(gripBox);
        card.Child = layer;
        card.MouseEnter += (_, _) => gripBox.Opacity = 1;
        card.MouseLeave += (_, _) => { if (!gripBox.IsMouseCaptured) gripBox.Opacity = 0; };

        // Default size (used on reset) & saved size
        double defaultWidth = card.Width, defaultHeight = card.Height;
        if (_size.TryGetValue(id, out var sz))
        {
            if (sz.W > 0) card.Width = sz.W;
            if (sz.H > 0) card.Height = sz.H;
        }

        Point? resizeStart = null;
        double startW = 0, startH = 0;
        gripBox.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                // Back to the default size
                card.Width = defaultWidth;
                card.Height = defaultHeight;
                _size.Remove(id);
                ApplyScale(1);
                Save();
                e.Handled = true;
                return;
            }
            resizeStart = e.GetPosition(_canvas);
            startW = card.ActualWidth;
            startH = card.ActualHeight;
            gripBox.CaptureMouse();
            e.Handled = true;
        };
        gripBox.MouseMove += (_, e) =>
        {
            if (resizeStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
            var m = e.GetPosition(_canvas);
            // Width & height change freely: drag right = wider, drag down = taller
            card.Width = Math.Clamp(startW + (m.X - start.X) / scale.ScaleX, 220, 760);
            card.Height = Math.Clamp(startH + (m.Y - start.Y) / scale.ScaleY, 110, 1000);
            UpdateGlass(card);
        };
        gripBox.MouseLeftButtonUp += (_, _) =>
        {
            if (resizeStart == null) return;
            resizeStart = null;
            gripBox.ReleaseMouseCapture();
            if (!card.IsMouseOver) gripBox.Opacity = 0;
            _size[id] = (card.Width, card.Height);
            Save();
        };

        // Ctrl + scroll over a card = zoom its content (text & numbers) proportionally
        card.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            ApplyScale(scale.ScaleX + (e.Delta > 0 ? 0.05 : -0.05));
            Save();
            e.Handled = true;
        };

        void ApplyScale(double v)
        {
            v = Math.Round(Math.Clamp(v, MinScale, MaxScale), 3);
            scale.ScaleX = scale.ScaleY = v;
            _scale[id] = v;
            UpdateGlass(card);
        }

        void Save()
        {
            _layout[id] = new Point(Canvas.GetLeft(card), Canvas.GetTop(card));
            MarkPlaced(card);
            SaveLayout();
        }
    }

    /// The user placed this card manually → no longer auto-stacked.
    void MarkPlaced(Border card)
    {
        int i = _columns.FindIndex(c => ReferenceEquals(c.Card, card));
        if (i >= 0 && _columns[i].Auto) _columns[i] = (card, _columns[i].Column, false);
    }

    /// Stack cards top to bottom; when a column is full, continue in the next column inward.
    void StackColumns()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        const double margin = 28, top = 24, gap = 16, colW = 320;
        for (int column = 0; column <= 1; column++)
        {
            double y = top, shift = 0;
            foreach (var (card, col, auto) in _columns)
            {
                if (col != column) continue;
                double s = card.RenderTransform is ScaleTransform st ? st.ScaleX : 1;
                double h = card.ActualHeight * s, w = card.ActualWidth * s;
                if (!auto) continue;
                if (y > top && y + h > ActualHeight - gap) { y = top; shift += colW + gap; }   // column full
                double x = column == 0 ? margin + shift : ActualWidth - margin - shift - w;
                Canvas.SetLeft(card, Math.Max(0, x));
                Canvas.SetTop(card, y);
                UpdateGlass(card);
                y += h + gap;
            }
        }
    }

    static Border Card(UIElement content, double width) => new()
    {
        Width = width, Background = CardBg, BorderBrush = CardEdge, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(22), Padding = new Thickness(18, 14, 18, 16), Child = content,
        Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.16,
                                        Color = Color.FromRgb(0x28, 0x28, 0x5A) }
    };

    // ================= Glass effect from the wallpaper =================
    // Cards sit on the wallpaper, so the wallpaper itself is blurred (once, at low resolution),
    // and each card shows the exact piece right behind it.

    readonly List<Border> _cards = new();
    DrawingGroup? _blurredWall;

    void BuildBlurredWallpaper()
    {
        _blurredWall = null;
        try
        {
            var sb = new System.Text.StringBuilder(520);
            if (!SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, (uint)sb.Capacity, sb, 0)) return;
            var path = sb.ToString();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 640;   // small is enough, it gets blurred anyway
            bmp.EndInit();
            bmp.Freeze();

            // "Fill" wallpaper mode: fills the screen, excess cropped
            var img = new Image { Source = bmp, Width = sw, Height = sh, Stretch = Stretch.UniformToFill,
                                  Effect = new BlurEffect { Radius = 60, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance } };
            var canvas = new Canvas { Width = sw, Height = sh, ClipToBounds = true };
            canvas.Children.Add(img);
            canvas.Measure(new Size(sw, sh));
            canvas.Arrange(new Rect(0, 0, sw, sh));
            var rtb = new RenderTargetBitmap((int)(sw / 8), (int)(sh / 8), 12, 12, PixelFormats.Pbgra32);   // 1/8 resolution
            rtb.Render(canvas);
            rtb.Freeze();

            var group = new DrawingGroup();
            group.Children.Add(new ImageDrawing(rtb, new Rect(0, 0, sw, sh)));
            group.Children.Add(new GeometryDrawing(GlassTint, null, new RectangleGeometry(new Rect(0, 0, sw, sh))));
            group.Freeze();
            _blurredWall = group;
        }
        catch { _blurredWall = null; }
        foreach (var c in _cards) UpdateGlass(c);
        UpdateClockGlass();
    }

    /// Card background = the blurred wallpaper right behind it.
    void UpdateGlass(Border card)
    {
        if (_blurredWall == null) { card.Background = CardBg; return; }
        double s = card.RenderTransform is ScaleTransform st ? st.ScaleX : 1;   // on-screen size
        var rect = new Rect(Left + Canvas.GetLeft(card), Top + Canvas.GetTop(card),
                            Math.Max(1, card.ActualWidth * s), Math.Max(1, card.ActualHeight * s));
        if (card.Background is DrawingBrush db && !db.IsFrozen) db.Viewbox = rect;
        else card.Background = new DrawingBrush(_blurredWall)
        {
            Viewbox = rect, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill
        };
    }

    static TextBlock Text(string text, double size, FontWeight weight, Brush? color = null, Thickness? margin = null) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, Foreground = color ?? Ink,
        Margin = margin ?? new Thickness(0), TextTrimming = TextTrimming.CharacterEllipsis
    };

    static TextBlock Header(string text, Brush color) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = color,
        Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent
    };

    // ================= Clock & date =================

    void UpdateClockAndCalendar()
    {
        var now = DateTime.Now;
        UpdateBigClock(now);
        RebuildCalendar();
        RebuildPrayer();
        RebuildUpNext();
        RebuildDeadlines();
        // Next tick exactly when the minute changes
        _clockTimer.Stop();
        _clockTimer.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(50);
        _clockTimer.Start();
    }

    // ================= Calendar & agenda =================

    (Border, FrameworkElement) CalendarCard()
    {
        var sp = new StackPanel();
        var header = Header("CALENDAR", Red);
        sp.Children.Add(header);
        sp.Children.Add(_calendarBody);
        return (Card(sp, 320), header);
    }

    void RebuildCalendar()
    {
        _calendarBody.Children.Clear();
        var today = DateTime.Today;
        var first = new DateTime(today.Year, today.Month, 1);

        _calendarBody.Children.Add(Text(today.ToString("MMMM yyyy", En), 15, FontWeights.Bold, margin: new Thickness(0, 0, 0, 6)));

        var grid = new UniformGrid { Columns = 7 };
        foreach (var d in new[] { "M", "T", "W", "T", "F", "S", "S" })
            grid.Children.Add(new TextBlock { Text = d, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Faint, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 3) });
        int offset = ((int)first.DayOfWeek + 6) % 7;
        for (int i = 0; i < offset; i++) grid.Children.Add(new TextBlock());
        for (int day = 1; day <= DateTime.DaysInMonth(today.Year, today.Month); day++)
        {
            bool isToday = day == today.Day;
            grid.Children.Add(new Border
            {
                Width = 26, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 1, 0, 1),
                Background = isToday ? Blue : Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = day.ToString(), FontSize = 12, FontWeight = isToday ? FontWeights.Bold : FontWeights.Medium,
                    Foreground = isToday ? Brushes.White : Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            });
        }
        _calendarBody.Children.Add(grid);

        // Today's events (from the connected Google Calendar)
        _calendarBody.Children.Add(Text("TODAY", 10.5, FontWeights.Bold, Faint, new Thickness(0, 10, 0, 4)));
        var events = CalendarWidget.Upcoming.Where(e => e.Start.Date <= today && e.End > DateTime.Now).Take(8).ToList();   // taller card = more events visible
        if (events.Count == 0) _calendarBody.Children.Add(Text("No more events today", 12.5, FontWeights.Normal, Sub));
        foreach (var e in events)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            row.Children.Add(new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = e.Color, Margin = new Thickness(0, 1, 8, 1) });
            var t = new StackPanel();
            t.Children.Add(Text(e.Title, 12.5, FontWeights.SemiBold));
            t.Children.Add(Text(e.AllDay ? "All day" : $"{e.Start.ToString("h:mm tt", En)} – {e.End.ToString("h:mm tt", En)}", 11, FontWeights.Normal, Sub));
            row.Children.Add(t);
            _calendarBody.Children.Add(row);
        }
    }

    // ================= Notes / To-do =================

    (Border, FrameworkElement) NotesCard()
    {
        var dp = new DockPanel { LastChildFill = true };
        var header = Header("NOTES", Orange);
        DockPanel.SetDock(header, Dock.Top);
        dp.Children.Add(header);
        // Scrollable when the card is shortened; the taller the card, the more notes visible
        dp.Children.Add(new ScrollViewer
        {
            Content = _notesBody, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        var card = Card(dp, 300);
        card.Height = 250;   // can be made taller with the resize grip
        return (card, header);
    }

    void RebuildNotes()
    {
        _notesBody.Children.Clear();
        var open = NotesStore.Notes.Where(n => n.Status != NotesView.Status.Done)
                                   .OrderBy(n => n.Status switch { NotesView.Status.Progress => 0, NotesView.Status.Waiting => 1, _ => 2 })
                                   .ToList();
        if (open.Count == 0)
        {
            _notesBody.Children.Add(Text("All done 🎉", 13, FontWeights.SemiBold, Sub));
            return;
        }
        foreach (var n in open.Take(30))
        {
            var color = n.Status switch { NotesView.Status.Progress => ProgressBlue, NotesView.Status.Waiting => Orange, _ => Faint };
            // Check circle: click = Done (like macOS Reminders)
            var check = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderBrush = color, BorderThickness = new Thickness(1.6),
                Background = Brushes.Transparent, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand, ToolTip = "Mark as done"
            };
            var note = n;
            check.MouseEnter += (_, _) => check.Background = Ui.Brush(0x40, 0x1E, 0x8E, 0x3E);
            check.MouseLeave += (_, _) => check.Background = Brushes.Transparent;
            check.MouseLeftButtonUp += (_, _) => NotesStore.SetStatus(note, NotesView.Status.Done);

            var texts = new StackPanel();
            texts.Children.Add(Text(n.Text, 13, FontWeights.SemiBold));
            texts.Children.Add(Text(n.Status switch { NotesView.Status.Progress => "In Progress", NotesView.Status.Waiting => "Waiting", _ => "To Do" },
                                    10.5, FontWeights.SemiBold, color));
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            row.Children.Add(check);
            row.Children.Add(texts);
            _notesBody.Children.Add(row);
        }
        if (open.Count > 30) _notesBody.Children.Add(Text($"+{open.Count - 30} more", 11.5, FontWeights.SemiBold, Sub));
    }

    // ================= Prayer times =================

    (Border, FrameworkElement) PrayerCard()
    {
        var sp = new StackPanel();
        var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 2) };
        _prayerCity.FontSize = 11; _prayerCity.FontWeight = FontWeights.SemiBold; _prayerCity.Foreground = Sub;
        DockPanel.SetDock(_prayerCity, Dock.Right);
        head.Children.Add(_prayerCity);
        var title = Header("PRAYER TIMES", Green);
        title.Margin = new Thickness(0);
        head.Children.Add(title);
        sp.Children.Add(head);
        _prayerNext.FontSize = 20; _prayerNext.FontWeight = FontWeights.SemiBold; _prayerNext.Foreground = Ink;
        _prayerNext.Margin = new Thickness(0, 4, 0, 8);
        _prayerNext.TextTrimming = TextTrimming.CharacterEllipsis;
        sp.Children.Add(_prayerNext);
        sp.Children.Add(_prayerBody);

        var card = Card(sp, 300);
        card.ToolTip = "Right-click to change location";
        card.ContextMenu = PrayerMenu();
        return (card, head);
    }

    /// Right-click: automatic location or pick a city.
    ContextMenu PrayerMenu()
    {
        var menu = new ContextMenu();
        var auto = new MenuItem { Header = "Use my location (Windows Location)" };
        auto.Click += async (_, _) =>
        {
            if (!await PrayerTimes.DetectLocationAsync())
                MessageBox.Show("Windows didn't share your location.\nTurn on Settings → Privacy & security → Location, or pick a city.",
                                "Prayer Times");
        };
        menu.Items.Add(auto);
        menu.Items.Add(new Separator());
        foreach (var city in PrayerTimes.Cities)
        {
            var item = new MenuItem { Header = city.Name, IsCheckable = true };
            item.Click += (_, _) => PrayerTimes.SetLocation(city);
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) =>
        {
            foreach (var mi in menu.Items.OfType<MenuItem>())
                mi.IsChecked = mi.Header as string == PrayerTimes.Current.Name;
        };
        return menu;
    }

    void RebuildPrayer()
    {
        _prayerBody.Children.Clear();
        var loc = PrayerTimes.Current;
        _prayerCity.Text = loc.Name;
        var now = DateTime.Now;
        var today = PrayerTimes.For(now.Date, loc);

        // Next prayer (Imsak & Sunrise are not prayer times)
        var next = today.FirstOrDefault(p => p.Time > now && p.Name is not ("Imsak" or "Sunrise"))
                   ?? PrayerTimes.For(now.Date.AddDays(1), loc).First(p => p.Name == "Fajr");
        var left = next.Time - now;
        _prayerNext.Text = $"{next.Name} in " + (left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m");

        foreach (var p in today)
        {
            bool isNext = p.Name == next.Name && p.Time.Date == next.Time.Date;
            bool passed = p.Time <= now && !isNext;
            bool minor = p.Name is "Imsak" or "Sunrise";
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            var name = Text(p.Name, 13, isNext ? FontWeights.Bold : FontWeights.SemiBold, isNext ? Brushes.White : passed || minor ? Sub : Ink);
            var time = Text(p.Time.ToString("HH:mm", En), 13, isNext ? FontWeights.Bold : FontWeights.SemiBold, isNext ? Brushes.White : passed || minor ? Sub : Ink);
            time.HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(name);
            row.Children.Add(time);
            _prayerBody.Children.Add(new Border
            {
                Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(-8, 0, -8, 1),
                Background = isNext ? Green : Brushes.Transparent
            });
        }
    }

    // ================= CPU & Memory =================

    (Border, FrameworkElement) PerformanceCard()
    {
        var sp = new StackPanel();
        var header = Header("PERFORMANCE", Green);
        sp.Children.Add(header);
        sp.Children.Add(Meter("CPU", _cpuText, _cpuBar, Green));
        _cpuGraph.Stroke = Green; _cpuGraph.StrokeThickness = 1.5; _cpuGraph.Height = 34; _cpuGraph.Margin = new Thickness(0, 4, 0, 8);
        _cpuGraph.Stretch = Stretch.None;
        sp.Children.Add(new Border { Height = 34, ClipToBounds = true, Child = _cpuGraph, Margin = new Thickness(0, 2, 0, 8) });
        sp.Children.Add(Meter("Memory", _memText, _memBar, Blue));
        _memDetail.FontSize = 11; _memDetail.Foreground = Sub; _memDetail.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(_memDetail);
        return (Card(sp, 300), header);
    }

    static Grid Meter(string label, TextBlock value, Border bar, Brush color)
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.Children.Add(Text(label, 13, FontWeights.SemiBold));
        value.FontSize = 13; value.FontWeight = FontWeights.Bold; value.Foreground = Ink;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        g.Children.Add(value);
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.Brush(0x1A, 0x00, 0x00, 0x00), Margin = new Thickness(0, 5, 0, 0) };
        bar.Height = 6; bar.CornerRadius = new CornerRadius(3); bar.Background = color; bar.HorizontalAlignment = HorizontalAlignment.Left; bar.Width = 0;
        var host = new Grid();
        host.Children.Add(track);
        host.Children.Add(new Border { Child = bar, Margin = new Thickness(0, 5, 0, 0) });
        Grid.SetRow(host, 1);
        g.Children.Add(host);
        return g;
    }

    long _lastIdle, _lastKernel, _lastUser;

    void UpdatePerformance()
    {
        // Bar width follows the card width (cards can be widened)
        double barWidth = (_cpuBar.Parent as FrameworkElement)?.ActualWidth is double bw && bw > 0 ? bw : 262;
        // CPU: system time delta since the last reading
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long di = idle - _lastIdle, dk = kernel - _lastKernel, du = user - _lastUser;
            if (_lastKernel != 0 && dk + du > 0)
            {
                double cpu = Math.Clamp(1.0 - (double)di / (dk + du), 0, 1);
                _cpuText.Text = $"{cpu:P0}";
                _cpuBar.Width = barWidth * cpu;
                _cpuHistory.Enqueue(cpu);
                while (_cpuHistory.Count > 40) _cpuHistory.Dequeue();
                var pts = new PointCollection();
                int i = 0;
                foreach (var v in _cpuHistory) pts.Add(new Point(i++ * (barWidth / 39), 32 - v * 30));
                _cpuGraph.Points = pts;
            }
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
        }
        // Memory
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            double used = mem.ullTotalPhys - mem.ullAvailPhys;
            _memText.Text = $"{mem.dwMemoryLoad}%";
            _memBar.Width = barWidth * mem.dwMemoryLoad / 100.0;
            _memDetail.Text = $"{used / 1073741824.0:0.0} GB of {mem.ullTotalPhys / 1073741824.0:0} GB used";
        }
    }

    // ================= Saved layout =================

    /// Saved position & size: { "prayer": [x, y, scale, w, h], ... }
    void LoadLayout()
    {
        try
        {
            if (!File.Exists(LayoutPath)) return;
            var raw = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(LayoutPath));
            if (raw == null) return;
            foreach (var (id, v) in raw)
            {
                if (v.Length >= 2) _layout[id] = new Point(v[0], v[1]);
                if (v.Length >= 3) _scale[id] = Math.Clamp(v[2], MinScale, MaxScale);
                if (v.Length >= 5) _size[id] = (v[3], v[4]);
            }
        }
        catch { }
    }

    void SaveLayout()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LayoutPath)!);
            File.WriteAllText(LayoutPath, JsonSerializer.Serialize(_layout.ToDictionary(kv => kv.Key,
                kv => _size.TryGetValue(kv.Key, out var z)
                    ? new[] { kv.Value.X, kv.Value.Y, _scale.GetValueOrDefault(kv.Key, 1.0), double.IsNaN(z.W) ? 0 : z.W, double.IsNaN(z.H) ? 0 : z.H }
                    : new[] { kv.Value.X, kv.Value.Y, _scale.GetValueOrDefault(kv.Key, 1.0) })));
        }
        catch { }
    }

    /// Desktop covered by a maximized (or fullscreen) window → widgets not visible.
    /// Used to skip unnecessary redraws (a screen-wide transparent window is expensive to redraw).
    public static bool DesktopCovered()
    {
        if (Perf.Fullscreen) return true;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        return IsZoomed(fg);
    }

    // ================= Win32 =================

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfo(uint action, uint param, System.Text.StringBuilder value, uint winIni);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
