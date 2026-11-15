using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.FileProperties;
using IOPath = System.IO.Path;

namespace DynamicIsland;

public class DockEntry
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonIgnore] public string? Target { get; set; }
    [JsonIgnore] public bool IsTrash { get; set; }
    /// An app that is open but not kept in the Dock (like macOS)
    [JsonIgnore] public bool IsRunningOnly { get; set; }
}

public partial class DockWindow : Window
{
    // macOS-style icon size & magnification
    const double BaseSize = 44;     // normal size
    const double MaxScale = 1.7;    // icon right under the cursor = 1.7x
    const double Spread = 64;       // how far neighbors also grow (px)
    const double Gap = 6;
    // Height reserved at the bottom of the screen for the Dock: maximized windows stop above it
    const double ReservedHeight = 72;

    // macOS-style auto-hide
    const double HideOffset = 100;                                   // how far the Dock slides down (px)
    static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(600);
    const string SettingsKey = @"Software\DynamicIsland";

    // macOS-style icons: drawn on a 128 px canvas, then scaled down
    const double TileSize = 128, TileRadius = 29;                    // ~22% corner radius like macOS icons
    static readonly Brush TileBrush = new LinearGradientBrush(
        Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xE9, 0xE9, 0xEE), 90);
    static readonly Brush TileBorder = new SolidColorBrush(Color.FromArgb(0x1F, 0, 0, 0));
    static readonly Dictionary<ImageSource, bool> FullBleedCache = new();

    static readonly Brush DotBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));

    static readonly string ConfigPath = IOPath.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "dock.json");

    record ItemView(DockEntry Entry, Border Icon, Ellipse Dot, Border Badge, TextBlock BadgeText);

    // macOS-style red notification badge
    static readonly Brush BadgeBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
    static readonly Dictionary<string, string?> LnkAumidCache = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, NotificationCounts.AppNotifs> _notifCounts = new();

    // When each icon was last opened (FILETIME UTC). Notifications that arrived before that count
    // as seen, so the badge disappears once the app is opened, like on macOS.
    static readonly string SeenPath = IOPath.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "badge-seen.json");
    Dictionary<string, long> _seen = LoadSeen();
    bool _notifBusy;

    List<DockEntry> _entries = new();
    readonly DockEntry _trash = new() { FilePath = "shell:RecycleBinFolder", Name = "Recycle Bin", IsTrash = true };
    Image? _trashImage;
    bool? _trashFull;
    readonly List<ItemView> _views = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    int _tickCount;

    // Drag icons to reorder
    readonly Dictionary<FrameworkElement, DockEntry> _itemEntries = new();
    DockEntry? _dragEntry;
    FrameworkElement? _dragItem;
    Point _dragStart;
    bool _dragging;

    readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    bool _autoHide = LoadAutoHide();
    bool _minimalIcons = LoadMinimalIcons();
    bool _shown = true;
    DateTime _lastInside = DateTime.Now.AddSeconds(1.5);   // shown briefly right after launch

    // Open app windows (refreshed every second)
    List<Native.AppWindow> _windows = new();
    readonly List<string> _runningOnly = new();   // .exe paths, in the order they were opened
    bool _rebuildPending;
    static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public DockWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Reposition();
            _entries = LoadConfig();
            foreach (var en in _entries) en.Target ??= ResolveTarget(en.FilePath);
            Native.StartForegroundTracking();
            RefreshWindows();
            Rebuild();
            _timer.Start();
            _ = RefreshNotificationsAsync();
            AutoHideItem.IsChecked = _autoHide;
            MinimalIconsItem.IsChecked = _minimalIcons;
            UpdateTimers();
        };

        // Fullscreen / power saving changed → adjust the Dock's work
        Perf.Changed += () =>
        {
            Visibility = Perf.Fullscreen ? Visibility.Hidden : Visibility.Visible;
            UpdateTimers();
            if (!Perf.Fullscreen) Rebuild();   // icon shadows follow power-saving mode
        };
        _hoverTimer.Tick += (_, _) => CheckHover();
        Native.ForegroundChanged += OnForegroundChanged;
        NotificationCounts.Cleared += () => _ = RefreshNotificationsAsync();
        IconsPanel.PreviewMouseLeftButtonUp += (_, e) => { if (EndDrag()) e.Handled = true; };
        IconsPanel.LostMouseCapture += (_, _) => EndDrag();
        _timer.Tick += (_, _) =>
        {
            if (Perf.Fullscreen) return;   // Dock hidden by a fullscreen app: rest
            _tickCount++;
            if (_tickCount % 2 == 0)
            {
                Native.HideTaskbar();   // keep the Windows taskbar hidden
                BringToTop();           // keep the Dock above other windows
            }

            // While the Dock is auto-hidden, dots & badges aren't visible,
            // so refreshing every 4 seconds is enough; refreshed immediately when it appears.
            bool visible = _shown || !_autoHide;
            if (visible || _tickCount % 4 == 0) RefreshWindows();
            if (visible && _tickCount % 5 == 0) UpdateTrashIcon();
            if (_tickCount % (visible ? 4 : 15) == 0) _ = RefreshNotificationsAsync();
        };
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea)) Reposition();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.MakeToolWindow(this);
        // Reserve space at the bottom of the screen (replacing the taskbar's space)
        // When auto-hiding, the Dock reserves no space (windows can use the full screen)
        if (!_autoHide) ReserveSpace();
    }

    void ReserveSpace() =>
        Native.RegisterBottomAppBar(this, (int)Math.Round(ReservedHeight * Native.DpiScale(this)));

    // ---------- Notification badges ----------

    async Task RefreshNotificationsAsync()
    {
        if (_notifBusy) return;
        _notifBusy = true;
        try
        {
            _notifCounts = await Task.Run(NotificationCounts.Read);
            UpdateBadges();
        }
        catch { }
        finally { _notifBusy = false; }
    }

    void UpdateBadges()
    {
        foreach (var v in _views)
        {
            int count = v.Entry.IsTrash ? 0 : Math.Max(NotificationCountFor(v.Entry), TitleCountFor(v.Entry));
            v.Badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            v.BadgeText.Text = count > 99 ? "99+" : count.ToString();
        }
    }

    /// Match notification senders to icons: by Store app ID, the ID stored in the shortcut,
    /// or the .exe name (e.g. "chrome" ↔ "Chrome", "OUTLOOK" ↔ "Microsoft.Office.OUTLOOK.EXE.15").
    /// Unread count in the window title, e.g. "(3) WhatsApp", "Inbox (12) - Gmail".
    static readonly Regex TitleCount = new(@"(?:^|[\s\-|·])\((\d{1,4})\)(?=\s|$)");

    int TitleCountFor(DockEntry entry)
    {
        int max = 0;
        foreach (var w in WindowsFor(entry))
        {
            var m = TitleCount.Match(w.Title);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n)) max = Math.Max(max, n);
        }
        return max;
    }

    int NotificationCountFor(DockEntry entry)
    {
        if (_notifCounts.Count == 0) return 0;

        var ids = EntryIds(entry);
        foreach (var w in WindowsFor(entry))
        {
            if (w.Aumid != null) ids.Add(w.Aumid);
            if (w.WindowAumid != null) ids.Add(w.WindowAumid);
        }

        string? exeName = ExeOf(entry) is string exe ? IOPath.GetFileNameWithoutExtension(exe) : null;
        if (exeName != null && exeName.Length < 3) exeName = null;

        int total = 0;
        long seen = _seen.GetValueOrDefault(entry.FilePath);
        foreach (var (app, n) in _notifCounts)
        {
            bool match = ids.Any(id => id.Equals(app, StringComparison.OrdinalIgnoreCase)) ||
                         (exeName != null && app.Contains(exeName, StringComparison.OrdinalIgnoreCase));
            // App badge = the app's own unread count; otherwise count new notifications
            if (match) total += n.Badge ?? n.Toasts.Count(t => t > seen);
        }
        return total;
    }

    /// Keep the Dock above all windows (except while a context menu / icon drag is in progress).
    void BringToTop()
    {
        if (Visibility != Visibility.Visible || Mouse.Captured != null || Glass.ContextMenu?.IsOpen == true) return;
        Native.KeepOnTop(this);
    }

    /// App opened / its window became active → its notifications count as seen.
    void MarkSeen(DockEntry entry)
    {
        if (entry.IsTrash || NotificationCountFor(entry) == 0) return;
        _seen[entry.FilePath] = DateTime.UtcNow.ToFileTimeUtc();
        try
        {
            Directory.CreateDirectory(IOPath.GetDirectoryName(SeenPath)!);
            File.WriteAllText(SeenPath, JsonSerializer.Serialize(_seen));
        }
        catch { }
        UpdateBadges();
    }

    void OnForegroundChanged()
    {
        // Another app became active → Windows sometimes puts it above the Dock; raise the Dock again
        BringToTop();

        var h = Native.LastForeground;
        foreach (var v in _views)
            if (!v.Entry.IsTrash && WindowsFor(v.Entry).Any(w => w.Hwnd == h)) { MarkSeen(v.Entry); break; }
    }

    static Dictionary<string, long> LoadSeen()
    {
        try
        {
            if (File.Exists(SeenPath))
                return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(SeenPath))
                       ?? new();
        }
        catch { }
        return new();
    }

    // ---------- Auto-hide ----------

    void CheckHover()
    {
        if (!_autoHide || Visibility != Visibility.Visible) return;

        var (cx, cy) = Native.CursorPos();
        var (sw, sh) = Native.PrimaryScreenPx();
        bool atEdge = cx >= 0 && cx < sw && cy >= sh - 2;   // cursor touches the bottom edge of the screen

        bool overDock = false;
        if (_shown && Glass.IsVisible)
        {
            try
            {
                double scale = Native.DpiScale(this);
                var tl = Glass.PointToScreen(new Point(0, 0));
                var br = Glass.PointToScreen(new Point(Glass.ActualWidth, Glass.ActualHeight));
                double grow = (BaseSize * (MaxScale - 1) + 16) * scale;   // room for magnified icons
                overDock = cy >= tl.Y - grow && cy <= br.Y + 10 * scale &&
                           cx >= tl.X - 24 * scale && cx <= br.X + 24 * scale;
            }
            catch { /* not shown yet */ }
        }

        bool menuOpen = Mouse.Captured != null || Glass.ContextMenu?.IsOpen == true;
        var now = DateTime.Now;
        if (atEdge || overDock || menuOpen)
        {
            if (now > _lastInside) _lastInside = now;
            if (!_shown) SetShown(true);
        }
        else if (_shown && now - _lastInside > HideDelay) SetShown(false);
    }

    void SetShown(bool show)
    {
        _shown = show;
        var anim = new DoubleAnimation(show ? 0 : HideOffset, TimeSpan.FromMilliseconds(show ? 280 : 240))
        {
            EasingFunction = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        DockShift.BeginAnimation(TranslateTransform.YProperty, anim);
        if (!show) Icons_MouseLeave(this, null!);   // restore icon sizes
        else
        {
            BringToTop();
            // Just appeared: refresh dots, badges and the Recycle Bin once
            RefreshWindows();
            UpdateTrashIcon();
            _ = RefreshNotificationsAsync();
        }
        UpdateTimers();
    }

    /// Tune timer frequency to the current state so it doesn't burden the CPU/battery.
    void UpdateTimers()
    {
        _timer.Interval = Perf.Scale(1);

        // Bottom-edge cursor detection is only needed when auto-hiding & not fullscreen
        if (!_autoHide || Perf.Fullscreen) { _hoverTimer.Stop(); return; }
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(_shown ? 80 : (Perf.PowerSaving ? 150 : 100));
        if (!_hoverTimer.IsEnabled) _hoverTimer.Start();
    }

    void AutoHide_Click(object sender, RoutedEventArgs e)
    {
        _autoHide = AutoHideItem.IsChecked;
        SaveAutoHide(_autoHide);
        if (_autoHide)
        {
            Native.RemoveAppBar(this);
            _lastInside = DateTime.Now;
        }
        else
        {
            ReserveSpace();
            SetShown(true);
        }
        UpdateTimers();
    }

    static bool LoadAutoHide()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return key?.GetValue("DockAutoHide") is not int v || v != 0;   // default: on
        }
        catch { return true; }
    }

    // Minimal icons (demo style) or the apps' original icons; default: minimal
    static bool LoadMinimalIcons()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return key?.GetValue("DockMinimalIcons") is not int v || v != 0;
        }
        catch { return true; }
    }

    void MinimalIcons_Click(object sender, RoutedEventArgs e)
    {
        _minimalIcons = MinimalIconsItem.IsChecked;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("DockMinimalIcons", _minimalIcons ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { }
        Rebuild();
    }

    static void SaveAutoHide(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("DockAutoHide", on ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        Native.RemoveAppBar(this);
        base.OnClosed(e);
    }

    void Reposition()
    {
        // Screen-wide transparent window; clicks on empty areas pass through.
        // Sits at the bottom of the screen, inside the reserved space.
        Width = SystemParameters.PrimaryScreenWidth;
        Left = 0;
        Top = SystemParameters.PrimaryScreenHeight - Height;
    }

    // ---------- Building icons ----------

    void Rebuild()
    {
        ComputeOwners();
        IconsPanel.Children.Clear();
        _views.Clear();
        _itemEntries.Clear();
        foreach (var entry in _entries) IconsPanel.Children.Add(BuildItem(entry));
        foreach (var path in _runningOnly)
            IconsPanel.Children.Add(BuildItem(new DockEntry
            {
                FilePath = path, Target = path, Name = NiceName(path), IsRunningOnly = true
            }));

        // Separator, then the Recycle Bin, like the Trash on macOS
        IconsPanel.Children.Add(new Rectangle
        {
            Width = 1, Height = BaseSize - 6, Fill = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)),
            Margin = new Thickness(5, 0, 5, 10), VerticalAlignment = VerticalAlignment.Bottom
        });
        IconsPanel.Children.Add(BuildItem(_trash));

        EmptyHint.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRunningDots();
        UpdateBadges();
    }

    FrameworkElement BuildItem(DockEntry entry)
    {
        if (!entry.IsTrash) entry.Target ??= ResolveTarget(entry.FilePath);

        var img = new Image { Stretch = Stretch.Uniform, Width = TileSize, Height = TileSize };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        // 128 px icon canvas: rounded "tile" background + icon image, scaled down by a Viewbox
        var tile = new Border { Width = TileSize, Height = TileSize, CornerRadius = new CornerRadius(TileRadius) };
        var face = new Grid { Width = TileSize, Height = TileSize };
        face.Children.Add(tile);
        face.Children.Add(img);

        // Notification count in the top-right corner (grows with the icon)
        var badgeText = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 30, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 2)
        };
        var badge = new Border
        {
            MinWidth = 48, Height = 48, CornerRadius = new CornerRadius(24), Background = BadgeBrush,
            BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -14, -14, 0), Child = badgeText,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        face.Children.Add(badge);

        var icon = new Border
        {
            Width = BaseSize, Height = BaseSize,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new Viewbox { Child = face, Stretch = Stretch.Uniform },
            ToolTip = entry.Name,
            RenderTransform = new TranslateTransform()
        };
        ToolTipService.SetPlacement(icon, PlacementMode.Top);
        ToolTipService.SetInitialShowDelay(icon, 150);
        icon.MouseLeftButtonUp += (_, _) => Launch(entry, icon);
        icon.ContextMenu = entry.IsTrash ? BuildTrashMenu()
                         : entry.IsRunningOnly ? BuildRunningMenu(entry)
                         : BuildItemMenu(entry);

        var dot = new Ellipse
        {
            Width = 4, Height = 4, Fill = DotBrush, Opacity = 0,
            Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Center
        };

        var stack = new StackPanel { Margin = new Thickness(Gap / 2, 0, Gap / 2, 0), VerticalAlignment = VerticalAlignment.Bottom };
        stack.Children.Add(icon);
        stack.Children.Add(dot);

        _views.Add(new ItemView(entry, icon, dot, badge, badgeText));
        if (!entry.IsTrash && !entry.IsRunningOnly)
        {
            _itemEntries[stack] = entry;
            icon.PreviewMouseLeftButtonDown += (_, e) =>
            {
                _dragEntry = entry;
                _dragItem = stack;
                _dragStart = e.GetPosition(IconsPanel);
                _dragging = false;
            };
        }
        if (entry.IsTrash) { _trashImage = img; _trashFull = null; UpdateTrashIcon(); }
        else if (_minimalIcons && DockIcons.For(entry) is DockIcons.Style style)
        {
            DockIcons.Apply(style, tile, face, img, TileSize, TileRadius);
            face.Effect = Perf.PowerSaving ? null : new DropShadowEffect { BlurRadius = 14, ShadowDepth = 4, Direction = 270, Opacity = 0.32, Color = Colors.Black };
            face.CacheMode = new BitmapCache { RenderAtScale = 1, SnapsToDevicePixels = false };
        }
        else _ = SetIconAsync(entry, img, tile, face);
        return stack;
    }

    ContextMenu BuildTrashMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo(_trash.FilePath) { UseShellExecute = true });
        var empty = new MenuItem { Header = "Empty Recycle Bin…" };
        empty.Click += (_, _) => { Native.EmptyRecycleBin(this); _trashFull = null; UpdateTrashIcon(); };
        menu.Items.Add(open);
        menu.Items.Add(empty);
        return menu;
    }

    void UpdateTrashIcon()
    {
        if (_trashImage == null) return;
        bool full = Native.IsRecycleBinFull();
        if (_trashFull == full) return;
        _trashFull = full;
        _trashImage.Source = Native.GetRecycleBinIcon(full);
    }

    ContextMenu BuildItemMenu(DockEntry entry)
    {
        var menu = new ContextMenu();
        void Add(string header, Action act) { var m = new MenuItem { Header = header }; m.Click += (_, _) => act(); menu.Items.Add(m); }

        Add("Open", () => Process.Start(new ProcessStartInfo(entry.FilePath) { UseShellExecute = true }));
        Add("Show in File Explorer", () => Process.Start("explorer.exe", $"/select,\"{entry.FilePath}\""));
        menu.Items.Add(new Separator());
        Add("Move Left", () => Move(entry, -1));
        Add("Move Right", () => Move(entry, +1));
        menu.Items.Add(new Separator());
        Add("Remove from Dock", () => { _entries.Remove(entry); SaveConfig(); Rebuild(); });
        AddCloseItem(menu, entry);
        return menu;
    }

    ContextMenu BuildRunningMenu(DockEntry entry)
    {
        var menu = new ContextMenu();
        var keep = new MenuItem { Header = "Keep in Dock" };
        keep.Click += (_, _) =>
        {
            _runningOnly.Remove(entry.FilePath);
            _entries.Add(new DockEntry { FilePath = entry.FilePath, Name = entry.Name });
            SaveConfig();
            Rebuild();
        };
        var show = new MenuItem { Header = "Show in File Explorer" };
        show.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{entry.FilePath}\"");
        menu.Items.Add(keep);
        menu.Items.Add(show);
        AddCloseItem(menu, entry);
        return menu;
    }

    /// "Close" only appears when the app has open windows.
    void AddCloseItem(ContextMenu menu, DockEntry entry)
    {
        var close = new MenuItem { Header = "Quit" };
        close.Click += (_, _) => { foreach (var w in WindowsFor(entry)) Native.Close(w.Hwnd); };
        var sep = new Separator();
        menu.Items.Add(sep);
        menu.Items.Add(close);
        menu.Opened += (_, _) =>
        {
            var vis = WindowsFor(entry).Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            close.Visibility = sep.Visibility = vis;
        };
    }

    void Move(DockEntry entry, int dir)
    {
        int i = _entries.IndexOf(entry), j = i + dir;
        if (i < 0 || j < 0 || j >= _entries.Count) return;
        (_entries[i], _entries[j]) = (_entries[j], _entries[i]);
        SaveConfig();
        Rebuild();
    }

    static async Task SetIconAsync(DockEntry entry, Image img, Border tile, Grid face)
    {
        await LoadIconAsync(entry, img);
        if (img.Source is BitmapSource bmp) StyleAsMacIcon(bmp, img, tile, face);
    }

    /// Icons that already fill a full square get rounded corners;
    /// other icons are placed on a rounded white tile, like macOS icons.
    static void StyleAsMacIcon(BitmapSource bmp, Image img, Border tile, Grid face)
    {
        // Shadow off in power-saving mode (this effect is computed by the GPU whenever an icon grows)
        face.Effect = Perf.PowerSaving ? null : new DropShadowEffect
        {
            BlurRadius = 14, ShadowDepth = 4, Direction = 270, Opacity = 0.32, Color = Colors.Black
        };
        // Icon + shadow are drawn once and cached as a bitmap; magnifying just scales
        // that bitmap instead of redrawing on every mouse move.
        face.CacheMode = new BitmapCache { RenderAtScale = 1, SnapsToDevicePixels = false };

        if (!FullBleedCache.TryGetValue(bmp, out bool full))
            FullBleedCache[bmp] = full = IsFullBleed(bmp);

        if (full)
        {
            tile.Background = null;
            tile.BorderThickness = new Thickness(0);
            img.Width = img.Height = TileSize;
            img.Stretch = Stretch.UniformToFill;
            img.Clip = new RectangleGeometry(new Rect(0, 0, TileSize, TileSize), TileRadius, TileRadius);
        }
        else
        {
            tile.Background = TileBrush;
            tile.BorderBrush = TileBorder;
            tile.BorderThickness = new Thickness(1.5);
            img.Width = img.Height = TileSize * 0.70;
            img.Stretch = Stretch.Uniform;
            img.Clip = null;
        }
    }

    /// True if the icon already fills a full square (edges & near-corners are not transparent).
    static bool IsFullBleed(BitmapSource src)
    {
        try
        {
            var b = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = b.PixelWidth, h = b.PixelHeight;
            if (w < 8 || h < 8) return false;

            var points = new[]
            {
                (0.18, 0.18), (0.82, 0.18), (0.18, 0.82), (0.82, 0.82),   // near the corners
                (0.5, 0.08), (0.5, 0.92), (0.08, 0.5), (0.92, 0.5)        // middle of each edge
            };
            int opaque = 0;
            var px = new byte[4];
            foreach (var (fx, fy) in points)
            {
                b.CopyPixels(new Int32Rect((int)(fx * (w - 1)), (int)(fy * (h - 1)), 1, 1), px, 4, 0);
                if (px[3] > 200) opaque++;
            }
            return opaque >= 7;
        }
        catch { return false; }
    }

    static async Task LoadIconAsync(DockEntry entry, Image img)
    {
        // Store apps: get the icon from the Windows Shell
        if (AumidOf(entry) != null)
        {
            if (!IconCache.TryGetValue(entry.FilePath, out var app))
            {
                app = Native.GetShellItemImage(entry.FilePath, 128) ?? Native.GetShellIcon(entry.FilePath);
                if (app != null) IconCache[entry.FilePath] = app;
            }
            img.Source = app;
            return;
        }

        // Shortcuts with their own icon (.ico), e.g. Chrome/Edge web apps such as WhatsApp Web:
        // their target is chrome_proxy.exe (a generic icon), so use the icon listed in the shortcut.
        if (ShortcutIcon(entry.FilePath) is string ico)
        {
            if (!IconCache.TryGetValue(ico, out var icon))
            {
                icon = LoadIcoFile(ico);
                if (icon != null) IconCache[ico] = icon;
            }
            if (icon != null) { img.Source = icon; return; }
        }

        // Get a high-resolution icon from the target file (without the shortcut arrow)
        string source = entry.Target != null && File.Exists(entry.Target) ? entry.Target : entry.FilePath;
        if (IconCache.TryGetValue(source, out var cached)) { img.Source = cached; return; }
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(source);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 128, ThumbnailOptions.ResizeThumbnail);
            if (thumb != null && thumb.Size > 0)
            {
                using var stream = thumb.AsStreamForRead();
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();
                img.Source = IconCache[source] = bmp;
                return;
            }
        }
        catch { /* fall back to the backup icon */ }

        var fallback = Native.GetShellItemImage(source, 128) ?? Native.GetShellIcon(entry.FilePath);
        if (fallback != null) IconCache[source] = fallback;
        img.Source = fallback;
    }

    // ---------- Magnification ----------

    // ---------- Drag to reorder ----------

    void HandleDrag(MouseEventArgs e)
    {
        if (_dragEntry == null || _dragItem == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }

        var p = e.GetPosition(IconsPanel);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragStart.X) < 8 && Math.Abs(p.Y - _dragStart.Y) < 8) return;
            _dragging = true;
            _dragItem.Opacity = 0.55;
            IconsPanel.CaptureMouse();
        }

        // New index = number of fixed icons (other than the dragged one) whose center is left of the cursor
        int target = 0;
        foreach (var child in IconsPanel.Children.OfType<FrameworkElement>())
        {
            if (child == _dragItem || !_itemEntries.ContainsKey(child)) continue;
            double center = child.TranslatePoint(new Point(child.ActualWidth / 2, 0), IconsPanel).X;
            if (center < p.X) target++;
        }
        int current = IconsPanel.Children.IndexOf(_dragItem);
        if (current >= 0 && target != current)
        {
            IconsPanel.Children.RemoveAt(current);
            IconsPanel.Children.Insert(target, _dragItem);
        }
    }

    /// Finish dragging: save the new order. True if a drag was in progress (the click doesn't launch the app).
    bool EndDrag()
    {
        bool wasDragging = _dragging;
        var item = _dragItem;
        _dragEntry = null;
        _dragItem = null;
        _dragging = false;
        if (!wasDragging) return false;

        if (item != null) item.Opacity = 1;
        if (IconsPanel.IsMouseCaptured) IconsPanel.ReleaseMouseCapture();

        _entries = IconsPanel.Children.OfType<FrameworkElement>()
                             .Where(_itemEntries.ContainsKey).Select(c => _itemEntries[c]).ToList();
        SaveConfig();
        Dispatcher.BeginInvoke(new Action(Rebuild), DispatcherPriority.Background);
        return true;
    }

    void Icons_MouseMove(object sender, MouseEventArgs e)
    {
        HandleDrag(e);
        double mouseX = e.GetPosition(IconsPanel).X;
        foreach (var v in _views)
        {
            double centerX = v.Icon.TranslatePoint(new Point(v.Icon.ActualWidth / 2, 0), IconsPanel).X;
            double d = mouseX - centerX;
            double scale = 1 + (MaxScale - 1) * Math.Exp(-(d * d) / (2 * Spread * Spread));
            double size = BaseSize * scale;

            v.Icon.BeginAnimation(WidthProperty, null);
            v.Icon.BeginAnimation(HeightProperty, null);
            v.Icon.Width = v.Icon.Height = size;
        }
    }

    void Icons_MouseLeave(object sender, MouseEventArgs e)
    {
        foreach (var v in _views)
        {
            var anim = new DoubleAnimation(BaseSize, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            v.Icon.BeginAnimation(WidthProperty, anim);
            v.Icon.BeginAnimation(HeightProperty, anim);
        }
        if (_rebuildPending) { _rebuildPending = false; Rebuild(); }
    }

    // ---------- Launching apps ----------

    void Launch(DockEntry entry, Border icon)
    {
        MarkSeen(entry);
        if (entry.IsTrash)
        {
            Process.Start(new ProcessStartInfo(entry.FilePath) { UseShellExecute = true });
            return;
        }

        // Already open? Bring it to front; if it's already in front, minimize it
        // (or switch to the next window when there are several).
        var wins = WindowsFor(entry);
        if (wins.Count > 0)
        {
            var fg = Native.LastForeground;
            int i = wins.FindIndex(w => w.Hwnd == fg);
            if (i >= 0 && !Native.IsMinimized(fg))
            {
                if (wins.Count == 1) Native.Minimize(fg);
                else Native.Activate(wins[(i + 1) % wins.Count].Hwnd);
            }
            else Native.Activate(wins[0].Hwnd);
            return;
        }

        try
        {
            if (AumidOf(entry) != null) Process.Start("explorer.exe", entry.FilePath);   // Store app
            else Process.Start(new ProcessStartInfo(entry.FilePath) { UseShellExecute = true });
            Bounce(icon);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open {entry.Name}.\n{ex.Message}", "Dock",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    static void Bounce(Border icon)
    {
        var anim = new DoubleAnimation(0, -16, TimeSpan.FromMilliseconds(220))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ((TranslateTransform)icon.RenderTransform).BeginAnimation(TranslateTransform.YProperty, anim);
    }

    // ---------- Open apps ----------

    static string? AumidOf(DockEntry entry) =>
        entry.FilePath.StartsWith(Native.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
            ? entry.FilePath.Substring(Native.AppsFolderPrefix.Length) : null;

    static string? ExeOf(DockEntry entry)
    {
        var t = entry.Target ?? entry.FilePath;
        return t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t : null;
    }

    static bool SameApp(string exeA, string exeB) =>
        exeA.Equals(exeB, StringComparison.OrdinalIgnoreCase) ||
        IOPath.GetFileName(exeA).Equals(IOPath.GetFileName(exeB), StringComparison.OrdinalIgnoreCase);

    /// This app's windows, front-most first.
    List<Native.AppWindow> WindowsFor(DockEntry entry) => _windows.Where(w => Matches(entry, w)).ToList();

    // Each window belongs to at most one icon (recomputed on every refresh)
    Dictionary<IntPtr, DockEntry> _owners = new();

    bool Matches(DockEntry entry, Native.AppWindow w)
    {
        if (_owners.TryGetValue(w.Hwnd, out var owner)) return ReferenceEquals(owner, entry);
        // "Running" icon (not kept in the Dock): unowned windows with the same app key
        return entry.IsRunningOnly && AppKey(w).Equals(entry.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    /// The icon's app ID: Store/PWA app ID, or the ID stored in the .lnk shortcut.
    static List<string> EntryIds(DockEntry entry)
    {
        var ids = new List<string>();
        if (AumidOf(entry) is string aumid) ids.Add(aumid);
        if (entry.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            if (!LnkAumidCache.TryGetValue(entry.FilePath, out var lnkId))
                LnkAumidCache[entry.FilePath] = lnkId = Native.GetShortcutAumid(entry.FilePath);
            if (lnkId != null) ids.Add(lnkId);
        }
        return ids;
    }

    /// Edge/Chrome web app (PWA), e.g. "web.whatsapp.com-4DFCD4DE_…!App".
    static readonly Regex PwaId = new(@"^[a-z0-9.-]+\.[a-z]{2,}-[0-9A-F]{6,}_", RegexOptions.IgnoreCase);
    static readonly string[] Browsers = { "msedge.exe", "chrome.exe", "msedge_proxy.exe", "chrome_proxy.exe" };

    /// Decide which icon owns each window: ID match first, then PWAs by title, finally .exe name.
    void ComputeOwners()
    {
        var owners = new Dictionary<IntPtr, DockEntry>();
        var idOf = _entries.ToDictionary(e => e, EntryIds);
        bool Same(string? a, string b) => a != null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

        foreach (var w in _windows)
        {
            var owner = _entries.FirstOrDefault(e => idOf[e].Any(id => Same(w.Aumid, id) || Same(w.WindowAumid, id)));

            if (owner == null && Browsers.Contains(IOPath.GetFileName(w.ExePath), StringComparer.OrdinalIgnoreCase))
            {
                // Web app window: match the app name (first word) against the window title
                owner = _entries.FirstOrDefault(e =>
                    AumidOf(e) is string id && PwaId.IsMatch(id) &&
                    e.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { Length: >= 3 } word &&
                    w.Title.Contains(word, StringComparison.OrdinalIgnoreCase));
            }

            owner ??= _entries.FirstOrDefault(e =>
                AumidOf(e) == null && ExeOf(e) is string exe && SameApp(w.ExePath, exe));

            if (owner != null) owners[w.Hwnd] = owner;
        }
        _owners = owners;
    }

    /// App key for "running" icons: AUMID for Store apps, .exe path for others.
    static string AppKey(Native.AppWindow w) =>
        w.Aumid != null ? Native.AppsFolderPrefix + w.Aumid : w.ExePath;

    void RefreshWindows()
    {
        _windows = Native.GetAppWindows();
        ComputeOwners();

        // Open apps not in the Dock → shown to the right of the pinned icons
        var open = _windows.Where(w => !_entries.Any(e => Matches(e, w)))
                           .Select(AppKey)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToList();

        bool changed = _runningOnly.RemoveAll(p => !open.Contains(p, StringComparer.OrdinalIgnoreCase)) > 0;
        foreach (var p in open)
            if (!_runningOnly.Contains(p, StringComparer.OrdinalIgnoreCase)) { _runningOnly.Add(p); changed = true; }

        if (changed)
        {
            // Don't rebuild while the cursor is over the Dock (icons are magnified)
            if (IconsPanel.IsMouseOver) _rebuildPending = true;
            else Rebuild();
        }
        else UpdateRunningDots();
        UpdateBadges();
    }

    void UpdateRunningDots()
    {
        foreach (var v in _views)
            v.Dot.Opacity = !v.Entry.IsTrash && WindowsFor(v.Entry).Count > 0 ? 1 : 0;
    }

    // ---------- Adding apps ----------

    void Dock_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Dock_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddPaths(files);
    }

    void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose apps for the Dock",
            Filter = "Apps & shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
            Multiselect = true,
            DereferenceLinks = false
        };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames);
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (_entries.Any(x => x.FilePath.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
            _entries.Add(new DockEntry { FilePath = p, Name = NiceName(p) });
        }
        SaveConfig();
        Rebuild();
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        _entries = Defaults();
        SaveConfig();
        Rebuild();
    }

    void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // ---------- Storage ----------

    static List<DockEntry> LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var list = JsonSerializer.Deserialize<List<DockEntry>>(File.ReadAllText(ConfigPath));
                if (list != null) return list;
            }
        }
        catch { /* corrupted file → use defaults */ }
        return Defaults();
    }

    void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(IOPath.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore save failures */ }
    }

    void SyncTaskbar_Click(object sender, RoutedEventArgs e)
    {
        var pins = TaskbarPins();
        if (pins.Count == 0)
        {
            MessageBox.Show("No apps are pinned to the Windows taskbar.", "Dock");
            return;
        }
        _entries = pins;
        SaveConfig();
        RefreshWindows();
        Rebuild();
    }

    /// Apps pinned to the Windows taskbar, in taskbar order.
    static List<DockEntry> TaskbarPins()
    {
        try
        {
            return Native.GetTaskbarPins()
                         .Select(p => new DockEntry { FilePath = p, Name = NiceName(p), Target = ResolveTarget(p) })
                         .ToList();
        }
        catch { return new(); }
    }

    static List<DockEntry> Defaults()
    {
        var pins = TaskbarPins();
        if (pins.Count > 0) return pins;

        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = new[]
        {
            IOPath.Combine(win, "explorer.exe"),
            IOPath.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
            IOPath.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
            IOPath.Combine(roaming, @"Spotify\Spotify.exe"),
            IOPath.Combine(local, @"Programs\Microsoft VS Code\Code.exe"),
            IOPath.Combine(win, @"System32\notepad.exe"),
        };
        return candidates.Where(File.Exists)
                         .Select(p => new DockEntry { FilePath = p, Name = NiceName(p) })
                         .ToList();
    }

    static string NiceName(string path)
    {
        if (path.StartsWith(Native.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            return Native.GetShellDisplayName(path) ?? path.Substring(Native.AppsFolderPrefix.Length).Split('!')[0];
        try
        {
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) return desc;
            }
        }
        catch { }
        return IOPath.GetFileNameWithoutExtension(path);
    }

    /// The .ico/.png file set as a shortcut's (.lnk) icon; null if none.
    static string? ShortcutIcon(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(path);
            string loc = link.IconLocation ?? "";             // "C:\...\App.ico,0"
            int comma = loc.LastIndexOf(',');
            if (comma > 0) loc = loc[..comma];
            loc = Environment.ExpandEnvironmentVariables(loc.Trim().Trim('"'));
            return (loc.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || loc.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                   && File.Exists(loc) ? loc : null;
        }
        catch { return null; }
    }

    /// Load an icon from an .ico file (largest size) or a .png.
    static BitmapSource? LoadIcoFile(string path)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var best = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
            best?.Freeze();
            return best;
        }
        catch { return null; }
    }

    /// Get the target file of a shortcut (.lnk); otherwise return the path unchanged.
    static string ResolveTarget(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return path;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(path);
            string target = link.TargetPath;
            if (string.IsNullOrWhiteSpace(target))
            {
                // The File Explorer taskbar shortcut sometimes has no target path
                if (IOPath.GetFileNameWithoutExtension(path).Contains("Explorer", StringComparison.OrdinalIgnoreCase))
                    return IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                return path;
            }
            return target;
        }
        catch { return path; }
    }
}
