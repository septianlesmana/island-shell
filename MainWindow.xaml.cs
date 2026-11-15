using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;
using Windows.System.Power;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    enum Mode { Compact, Expanded, Notif }

    // Notch size: width, height
    const double IdleW = 190, PlayingW = 400, CompactH = 32;   // while music plays: wide enough for the song title
    const double TrayW = 640, TrayH = 186;
    const double ControlsW = 700, ControlsH = 300;
    const double NotesW = 780, NotesH = 400;
    const double CalendarW = 900, CalendarH = 430;
    const string CalendarUrl = "https://calendar.google.com/calendar/r";
    const double NotifW = 340, NotifH = 40;

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "DynamicIsland";

    static readonly CultureInfo En = new("en-GB");
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));

    Mode _mode = Mode.Compact;
    GlobalSystemMediaTransportControlsSessionManager? _media;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _notifTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    // Shrinks the window after the closing animation finishes
    readonly DispatcherTimer _shrinkTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    bool _isPlaying, _busy, _firstTrack = true;
    bool? _wasPlugged;
    string _lastTrack = "";

    readonly List<string> _tray = new();

    public MainWindow()
    {
        InitializeComponent();

        // The transparent window is only as large as the notch. Transparent windows are fully
        // redrawn on every animation frame, so smaller is lighter. When the notch opens the window
        // grows first, and shrinks again after the notch closes.
        Top = 0;
        SetWindowBox(CompactBoxW, CompactBoxH);
        Island.SizeChanged += (_, _) => UpdateShape();
        NotesContent.Changed += UpdateNotesBadges;
        UpdateNotesBadges();   // notes saved from previous sessions
        _shrinkTimer.Tick += (_, _) =>
        {
            _shrinkTimer.Stop();
            if (_mode != Mode.Expanded) SetWindowBox(CompactBoxW, CompactBoxH);
        };


        _tick.Tick += async (_, _) => await Refresh();
        _notifTimer.Tick += (_, _) =>
        {
            _notifTimer.Stop();
            if (_mode == Mode.Notif) Apply(Island.IsMouseOver ? Mode.Expanded : Mode.Compact);
        };
        // The expanded notch closes itself when the cursor is no longer over it. The cursor position
        // is checked directly (not just MouseLeave, which can be missed while a menu/popup is open).
        _leaveTimer.Interval = TimeSpan.FromMilliseconds(150);
        _leaveTimer.Tick += (_, _) =>
        {
            if (_mode != Mode.Expanded) { _leaveTimer.Stop(); return; }
            if (Ui.CursorOver(Island) || MustStayOpen()) { _lastInside = DateTime.Now; return; }
            if (DateTime.Now - _lastInside > TimeSpan.FromMilliseconds(450))
            {
                _leaveTimer.Stop();
                Apply(Mode.Compact);
            }
        };

        Loaded += async (_, _) =>
        {
            try { _media = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
            catch { /* media controls unavailable */ }

            AutoStartItem.IsChecked = IsAutoStart();
            HookMedia();
            RebuildTray();
            await Refresh();
            Apply(Mode.Compact, animate: false);
            _tick.Interval = Perf.Scale(2);
            _tick.Start();
        };

        Perf.Changed += () =>
        {
            Visibility = Perf.Fullscreen ? Visibility.Hidden : Visibility.Visible;
            _tick.Interval = Perf.Scale(2);
            UpdateEqualizer();
        };
    }

    /// Raise the notch to the very top (above the menu bar), except while its context menu is open.
    public void RaiseToTop()
    {
        if (Visibility == Visibility.Visible && Island.ContextMenu?.IsOpen != true) Native.KeepOnTop(this);
    }

    // ================= Notes =================

    void NotesNew_Click(object sender, RoutedEventArgs e) => NotesContent.BeginNew();

    /// While music plays, the Notes badge moves right (before the equalizer) so it doesn't cover the title.
    void PositionNotesBadge()
    {
        bool music = _isPlaying;
        NotesCompactBadge.HorizontalAlignment = music ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        NotesCompactBadge.Margin = music ? new Thickness(0, 0, 24, 0) : new Thickness(0);
        MiniTitle.Margin = new Thickness(28, 0, music && NotesCompactBadge.Visibility == Visibility.Visible ? 72 : 28, 1);
    }

    /// Badge with the number of "In Progress" notes: in the Notes tab and the compact notch.
    void UpdateNotesBadges()
    {
        int n = NotesContent.InProgressCount;
        string text = n > 99 ? "99+" : n.ToString();
        NotesTabBadgeText.Text = NotesCompactBadgeText.Text = text;
        NotesTabBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotesCompactBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotesCompactBadge.ToolTip = n == 1 ? "1 note in progress" : $"{n} notes in progress";
        PositionNotesBadge();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.MakeToolWindow(this);
    }

    // ================= Periodic refresh =================

    async Task Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (Perf.Fullscreen) return;   // hidden: no work needed
            if (Island.ContextMenu?.IsOpen != true)
                Native.KeepOnTop(this); // stay above the menu bar

            CheckCharger();
            // Media is updated via events; this polling is just a fallback every ~6 seconds
            if (_mediaDirty || ++_mediaTick % 3 == 0)
            {
                _mediaDirty = false;
                await UpdateMedia();
            }
        }
        finally { _busy = false; }
    }

    void CheckCharger()
    {
        if (PowerManager.BatteryStatus == BatteryStatus.NotPresent) return;
        int pct = PowerManager.RemainingChargePercent;
        bool plugged = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;

        if (_wasPlugged.HasValue && _wasPlugged.Value != plugged)
            ShowNotif(plugged ? "\uE945" : "\uE850",
                      plugged ? $"Charging · {pct}%" : $"Charger disconnected · {pct}%",
                      plugged ? Green : Brushes.White);
        _wasPlugged = plugged;
    }

    async Task UpdateMedia()
    {
        bool wasPlaying = _isPlaying;
        var session = _media?.GetCurrentSession();
        string title = "", artist = "";
        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;

        if (session != null)
        {
            try
            {
                _isPlaying = session.GetPlaybackInfo().PlaybackStatus ==
                             GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                props = await session.TryGetMediaPropertiesAsync();
                title = props?.Title ?? "";
                artist = props?.Artist ?? "";
            }
            catch { _isPlaying = false; }
        }
        else _isPlaying = false;

        bool hasMedia = !string.IsNullOrWhiteSpace(title);
        if (!hasMedia)
        {
            MiniArt.Source = null;
            _lastTrack = "";
        }

        EqPanel.Visibility = MiniArtBox.Visibility = MiniTitle.Visibility =
            _isPlaying ? Visibility.Visible : Visibility.Collapsed;
        MiniTitle.Text = string.IsNullOrWhiteSpace(artist) ? title : $"{title} - {artist}";
        MiniTitle.ToolTip = MiniTitle.Text;
        PositionNotesBadge();

        string key = title + "|" + artist;
        if (hasMedia && key != _lastTrack)
        {
            _lastTrack = key;
            if (props != null) await LoadArt(props);
            if (!_firstTrack && _isPlaying && _mode == Mode.Compact) ShowNotif("\uE8D6", title, Green);
        }
        _firstTrack = false;

        if (wasPlaying != _isPlaying && _mode == Mode.Compact) Apply(Mode.Compact);
        UpdateEqualizer();
    }

    // ---------- Media via events (not polling every second) ----------

    bool _mediaDirty;
    int _mediaTick;
    GlobalSystemMediaTransportControlsSession? _hookedSession;

    void HookMedia()
    {
        if (_media == null) return;
        _media.CurrentSessionChanged += (_, _) => MediaChanged();
        _media.SessionsChanged += (_, _) => MediaChanged();
        HookSession();
    }

    void HookSession()
    {
        var s = _media?.GetCurrentSession();
        if (ReferenceEquals(s, _hookedSession)) return;
        if (_hookedSession != null)
        {
            _hookedSession.MediaPropertiesChanged -= Session_Changed;
            _hookedSession.PlaybackInfoChanged -= Session_Changed;
        }
        _hookedSession = s;
        if (s != null)
        {
            s.MediaPropertiesChanged += Session_Changed;
            s.PlaybackInfoChanged += Session_Changed;
        }
    }

    void Session_Changed(GlobalSystemMediaTransportControlsSession sender, object args) => MediaChanged();

    void MediaChanged() => Dispatcher.BeginInvoke(async () =>
    {
        HookSession();
        if (_busy) { _mediaDirty = true; return; }
        await UpdateMedia();
    });

    /// The equalizer animation only runs while visible. A continuously running WPF animation
    /// redraws the screen 60× per second even when not visible.
    bool _eqRunning;
    void UpdateEqualizer()
    {
        bool run = _isPlaying && _mode == Mode.Compact && !Perf.Fullscreen && !Perf.PowerSaving;
        if (run == _eqRunning) return;
        _eqRunning = run;
        if (run) StartEqualizer();
        else
            foreach (var bar in new[] { Eq1, Eq2, Eq3 })
                ((ScaleTransform)bar.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    async Task LoadArt(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        MiniArt.Source = null;
        if (props.Thumbnail == null) return;
        try
        {
            using var ras = await props.Thumbnail.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 160;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            MiniArt.Source = bmp;
        }
        catch { /* artwork unavailable */ }
    }

    // ================= Modes & animation =================

    void Apply(Mode m, bool animate = true)
    {
        var previous = _mode;
        _mode = m;
        var (ew, eh) = ExpandedSize();

        (double w, double h, double r) = m switch
        {
            Mode.Expanded => (ew, eh, 28.0),
            Mode.Notif => (NotifW, NotifH, 18.0),
            _ => (_isPlaying ? PlayingW : IdleW, CompactH, 14.0)
        };

        if (m == Mode.Expanded)
        {
            _shrinkTimer.Stop();
            // Glass is captured once when the notch starts opening (before the notch covers that area)
            if (previous != Mode.Expanded) ApplyGlass();
            // Start watching the cursor so the notch closes itself when the cursor leaves
            if (previous != Mode.Expanded) _lastInside = DateTime.Now;
            if (!_leaveTimer.IsEnabled) _leaveTimer.Start();
            SetWindowBox(ew + 80, eh + 40);
        }
        else
        {
            _shrinkTimer.Stop();
            _shrinkTimer.Start();
            ControlsContent.Child = null;   // Controls are rebuilt when the notch opens again
            NotesContent.CancelEmptyInput();
            IslandShape.Fill = Brushes.Black;           // compact: solid black like a notch
        }

        _radius = r;
        UpdateShape();
        AnimateSize(WidthProperty, w, animate);
        AnimateSize(HeightProperty, h, animate);

        Fade(CompactPanel, m == Mode.Compact, animate);
        Fade(ExpandedPanel, m == Mode.Expanded, animate);
        Fade(NotifPanel, m == Mode.Notif, animate);

        UpdateEqualizer();

        if (m == Mode.Expanded)
        {
            if (CalendarTab.IsChecked == true) CalendarView.OnOpened();
            if (ControlsTab.IsChecked == true && ControlsContent.Child == null)
                ControlsContent.Child = ControlCenter.BuildContent(wide: true, close: () => Apply(Mode.Compact));
        }
    }

    // ================= Glass effect when the notch is open =================

    // Open notch: dark glass (~65%) with white text
    static readonly Brush GlassTint = Frozen(Color.FromArgb(0xA6, 0x16, 0x16, 0x18));
    static readonly Brush GlassFallback = Brushes.Black;
    static readonly Brush GlassEdge = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// Open-notch background = the screen area behind it, blurred + a translucent dark layer.
    /// Windows has no blur for free-form windows, so the screen is "photographed" once
    /// when the notch opens (cheap: blurred at ¼ resolution). Power-saving mode: plain black.
    void ApplyGlass()
    {
        if (Perf.PowerSaving) { IslandShape.Fill = GlassFallback; return; }
        try
        {
            // Capture an area as large as the biggest notch (Calendar tab) so it covers every tab
            double w = new[] { CalendarW, ControlsW, TrayW, NotesW }.Max(), h = new[] { CalendarH, ControlsH, TrayH, NotesH }.Max();
            w += 2 * Ear;   // also covers the concave curves at both top corners (otherwise they'd be clear)
            const double pad = 40;   // extra margin so the blurred edges don't turn dark
            double scale = Native.DpiScale(this);
            double left = (SystemParameters.PrimaryScreenWidth - w) / 2 - pad;
            var shot = Native.CaptureScreen((int)Math.Round(Math.Max(0, left) * scale), 0,
                                            (int)Math.Round((w + 2 * pad) * scale), (int)Math.Round((h + pad) * scale));
            if (shot == null) { IslandShape.Fill = GlassFallback; return; }

            var img = new Image
            {
                Source = shot, Width = w + 2 * pad, Height = h + pad, Stretch = Stretch.Fill,
                Effect = new BlurEffect { Radius = 40, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance }
            };
            var canvas = new Canvas { Width = w, Height = h, ClipToBounds = true };
            Canvas.SetLeft(img, -pad);
            canvas.Children.Add(img);
            canvas.Measure(new Size(w, h));
            canvas.Arrange(new Rect(0, 0, w, h));

            // Render at ¼ resolution (fast), then scale up → smoother blur
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(w / 4), (int)Math.Ceiling(h / 4), 24, 24, PixelFormats.Pbgra32);
            rtb.Render(canvas);
            rtb.Freeze();

            var group = new DrawingGroup();
            group.Children.Add(new ImageDrawing(rtb, new Rect(0, 0, w, h)));
            group.Children.Add(new GeometryDrawing(GlassTint, null, new RectangleGeometry(new Rect(0, 0, w, h))));
            group.Freeze();
            IslandShape.Fill = new DrawingBrush(group)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Top
            };
        }
        catch { IslandShape.Fill = GlassFallback; }
    }

    // ================= Notch shape =================

    const double Ear = 12;      // width of the concave curve at the top corners (must match IslandShape's Margin)
    double _radius = 14;        // bottom corner radius

    /// Redraw the notch shape for its current size (called on every size change during animation).
    /// Top corners curve outward into the screen edge like a MacBook notch, bottom corners are rounded.
    void UpdateShape()
    {
        double w = Island.ActualWidth, h = Island.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = Math.Min(_radius, Math.Min(w, h) / 2), e = Math.Min(Ear, h / 2);
        double left = Ear, right = Ear + w;   // notch edges in IslandShape coordinates

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(left - e, 0), true, true);
            c.LineTo(new Point(right + e, 0), false, false);                                     // top edge of the screen
            c.ArcTo(new Point(right, e), new Size(e, e), 0, false, SweepDirection.Counterclockwise, false, false);
            c.LineTo(new Point(right, h - r), false, false);
            c.ArcTo(new Point(right - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(left + r, h), false, false);
            c.ArcTo(new Point(left, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(left, e), false, false);
            c.ArcTo(new Point(left - e, 0), new Size(e, e), 0, false, SweepDirection.Counterclockwise, false, false);
        }
        g.Freeze();
        IslandShape.Data = g;
    }

    // Window size for the compact / notification notch (plus room for the shadow)
    const double CompactBoxW = PlayingW + 80, CompactBoxH = NotifH + 40;   // widest compact notch (while music plays) + shadow

    void SetWindowBox(double w, double h)
    {
        w = Math.Min(w, SystemParameters.PrimaryScreenWidth);
        if (Math.Abs(Width - w) > 0.5 || double.IsNaN(Width))
        {
            Width = w;
            Left = (SystemParameters.PrimaryScreenWidth - w) / 2;
        }
        if (Math.Abs(Height - h) > 0.5 || double.IsNaN(Height)) Height = h;
    }

    /// Expanded notch size, depending on the active tab.
    (double W, double H) ExpandedSize()
    {
        if (CalendarTab.IsChecked == true)
            return (CalendarW, CalendarH);
        if (ControlsTab.IsChecked == true) return (ControlsW, ControlsH);
        if (NotesTab.IsChecked == true) return (NotesW, NotesH);
        return (TrayW, TrayH);
    }


    void AnimateSize(DependencyProperty dp, double to, bool animate)
    {
        var anim = new DoubleAnimation(to, animate ? TimeSpan.FromMilliseconds(380) : TimeSpan.Zero)
        {
            EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
        };
        Island.BeginAnimation(dp, anim);
    }

    static void Fade(UIElement el, bool show, bool animate)
    {
        el.IsHitTestVisible = show;
        var anim = new DoubleAnimation(show ? 1 : 0,
            animate ? TimeSpan.FromMilliseconds(show ? 220 : 100) : TimeSpan.Zero)
        {
            BeginTime = show && animate ? TimeSpan.FromMilliseconds(140) : TimeSpan.Zero
        };
        el.BeginAnimation(OpacityProperty, anim);
    }

    void StartEqualizer()
    {
        int i = 0;
        foreach (var bar in new[] { Eq1, Eq2, Eq3 })
        {
            var anim = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(320 + i++ * 140))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
            };
            ((ScaleTransform)bar.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    /// Short notification in the notch from other parts of the app (e.g. the desktop Focus Timer).
    public void Notify(string icon, string text, Brush iconColor) => ShowNotif(icon, text, iconColor);

    void ShowNotif(string icon, string text, Brush iconColor)
    {
        if (_mode == Mode.Expanded) return; // don't interrupt while the notch is open
        NotifIcon.Text = icon;
        NotifIcon.Foreground = iconColor;
        NotifText.Text = text;
        Apply(Mode.Notif);
        _notifTimer.Stop();
        _notifTimer.Start();
    }

    // ================= Notch interaction =================

    void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _notifTimer.Stop();
        _lastInside = DateTime.Now;
        if (_mode != Mode.Expanded) Apply(Mode.Expanded);
        if (!_leaveTimer.IsEnabled) _leaveTimer.Start();
    }

    void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_leaveTimer.IsEnabled) _leaveTimer.Start();
    }

    DateTime _lastInside = DateTime.Now;

    /// Exceptions: stays open while its context menu is open, a mouse button is held
    /// (e.g. dragging a slider), or while typing in a text box that already has text.
    bool MustStayOpen()
    {
        if (Island.ContextMenu?.IsOpen == true || NotesView.DatePopupOpen || Native.IsLeftButtonDown()) return true;
        return Island.IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox tb && tb.Text.Length > 0;
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // Still loading?
        if (CalendarContent == null || TrayContent == null || TrayTab == null || ControlsTab == null
            || ControlsContent == null || ClearTrayBtn == null || CalendarButtons == null) return;

        if (NotesTab == null || NotesContent == null || NotesNewBtn == null) return;
        bool cal = CalendarTab.IsChecked == true, tray = TrayTab.IsChecked == true, ctl = ControlsTab.IsChecked == true,
             notes = NotesTab.IsChecked == true;
        NotesContent.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
        NotesNewBtn.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
        CalendarContent.Visibility = cal ? Visibility.Visible : Visibility.Collapsed;
        ControlsContent.Visibility = ctl ? Visibility.Visible : Visibility.Collapsed;
        if (!ctl) ControlsContent.Child = null;   // rebuilt (fresh data) when the tab opens again
        TrayContent.Visibility = tray ? Visibility.Visible : Visibility.Collapsed;
        CalendarButtons.Visibility = cal ? Visibility.Visible : Visibility.Collapsed;
        ClearTrayBtn.Visibility = tray && _tray.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_mode == Mode.Expanded) Apply(Mode.Expanded);   // resize to fit the tab
    }

    void Gear_Click(object sender, RoutedEventArgs e)
    {
        var menu = Island.ContextMenu;
        if (menu == null) return;
        menu.PlacementTarget = GearBtn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ================= Google Calendar =================

    async void CalendarReload_Click(object sender, RoutedEventArgs e) => await CalendarView.RefreshAsync();

    void CalendarSetup_Click(object sender, RoutedEventArgs e) => CalendarView.OpenSetup();

    void CalendarBrowser_Click(object sender, RoutedEventArgs e) =>
        OpenPath(CalendarUrl);

    void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    /// Restart: if the project folder exists, run "Run Dynamic Island.cmd" (rebuild + run the latest
    /// version, same as the Desktop shortcut). Otherwise just restart this app.
    /// The running instance exits cleanly when the new one starts.
    void Restart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var script = FindRunScript();
            if (script != null)
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
                {
                    WorkingDirectory = Path.GetDirectoryName(script)!,
                    WindowStyle = ProcessWindowStyle.Minimized,
                    UseShellExecute = true
                });
            else if (Environment.ProcessPath is string exe)
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Environment.CurrentDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't restart Dynamic Island.\n" + ex.Message, "Dynamic Island");
        }
    }

    static string? FindRunScript()
    {
        // Look in the working folder, then walk up from the exe folder (bin\Debug\net8.0-...\ → project folder)
        var dirs = new List<string> { Environment.CurrentDirectory };
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5 && d != null; i++, d = d.Parent) dirs.Add(d.FullName);
        foreach (var dir in dirs)
        {
            var p = Path.Combine(dir, "Run Dynamic Island.cmd");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ================= Tray: temporary file shelf =================

    void Island_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        _leaveTimer.Stop();
        TrayTab.IsChecked = true;
        if (_mode != Mode.Expanded) Apply(Mode.Expanded);
    }

    void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Island_DragLeave(object sender, DragEventArgs e) => _leaveTimer.Start();

    void Island_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files)
                if (!_tray.Contains(f, StringComparer.OrdinalIgnoreCase)) _tray.Add(f);
            RebuildTray();
        }
        _leaveTimer.Start();
    }

    void ClearTray_Click(object sender, RoutedEventArgs e)
    {
        _tray.Clear();
        RebuildTray();
    }

    void RebuildTray()
    {
        TrayPanel.Children.Clear();
        foreach (var path in _tray) TrayPanel.Children.Add(BuildTrayItem(path));
        TrayHint.Visibility = _tray.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearTrayBtn.Visibility = _tray.Count > 0 && TrayTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement BuildTrayItem(string path)
    {
        var box = new StackPanel
        {
            Width = 78, Margin = new Thickness(4, 0, 4, 0),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = path
        };
        box.Children.Add(new Image { Width = 40, Height = 40, Source = Native.GetShellIcon(path) });
        box.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(path), Foreground = Brushes.White, FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        });

        // Double-click = open; drag out = copy/move elsewhere
        Point? down = null;
        box.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { OpenPath(path); down = null; return; }
            down = e.GetPosition(box);
        };
        box.MouseLeftButtonUp += (_, _) => down = null;
        box.MouseMove += (_, e) =>
        {
            if (down is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(box);
            if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
            down = null;
            DragDrop.DoDragDrop(box, new DataObject(DataFormats.FileDrop, new[] { path }),
                                DragDropEffects.Copy | DragDropEffects.Move);
            if (!File.Exists(path) && !Directory.Exists(path)) { _tray.Remove(path); RebuildTray(); }
        };

        var menu = new ContextMenu();
        void Add(string header, Action act) { var m = new MenuItem { Header = header }; m.Click += (_, _) => act(); menu.Items.Add(m); }
        Add("Open", () => OpenPath(path));
        Add("Show in File Explorer", () => Process.Start("explorer.exe", $"/select,\"{path}\""));
        menu.Items.Add(new Separator());
        Add("Remove from Tray", () => { _tray.Remove(path); RebuildTray(); });
        box.ContextMenu = menu;
        return box;
    }

    static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    // ================= Start with Windows =================

    static bool IsAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) != null;
    }

    void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        if (AutoStartItem.IsChecked) key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(AppName, throwOnMissingValue: false);
    }
}
