using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.System.Power;

namespace DynamicIsland;

public partial class MenuBarWindow : Window
{
    const double BarHeight = 32;
    const double BatteryInnerWidth = 23.6;   // inner width of the battery icon (29 - border - padding)

    static readonly CultureInfo En = new("en-GB");
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
    static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
    static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F));   // text color in light mode

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Dictionary<uint, string> _nameCache = new();
    readonly int _ownPid = Environment.ProcessId;

    public MenuBarWindow()
    {
        InitializeComponent();
        // CPU-friendly: the clock updates exactly when the minute changes (not every second);
        // battery, network and active app name are updated via Windows events.
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Native.ForegroundChanged += UpdateAppName;
        // Another app became active → keep the menu bar above it
        Native.ForegroundChanged += () =>
        {
            if (Visibility != Visibility.Visible || Ui.AnyOpen) return;
            Native.KeepOnTop(this);
            // The notch (Dynamic Island) must always be above the menu bar
            var app = Application.Current;   // null while the app is shutting down
            if (app == null) return;
            foreach (Window w in app.Windows)
                if (w is MainWindow island) island.RaiseToTop();
        };
        PowerManager.RemainingChargePercentChanged += (_, _) => Dispatcher.BeginInvoke(UpdateBattery);
        PowerManager.PowerSupplyStatusChanged += (_, _) => Dispatcher.BeginInvoke(UpdateBattery);
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Dispatcher.BeginInvoke(UpdateWifi);
        Perf.Changed += () => Visibility = Perf.Fullscreen ? Visibility.Hidden : Visibility.Visible;

        // Media & notification capsules
        Loaded += async (_, _) => { await StartMediaAsync(); _ = RefreshNotifAsync(); _notifTimer.Start(); };
        _notifTimer.Tick += (_, _) => { if (!Perf.Fullscreen) _ = RefreshNotifAsync(); };
        NotificationCounts.Cleared += () => _ = RefreshNotifAsync();

        // Microphone/camera & Focus: checked every 3 seconds (not in fullscreen)
        _statusTimer.Tick += (_, _) => { if (!Perf.Fullscreen) UpdateStatus(); };
        Loaded += (_, _) => { UpdateStatus(); _statusTimer.Start(); };
        Perf.Changed += () => _statusTimer.Interval = Perf.Scale(3);
        Perf.Changed += () => { _notifTimer.Interval = Perf.Scale(10); UpdateMediaEq(); };

        HoverOpen(StartBtn, () => SystemMenu.Show(StartBtn));
        Deactivated += (_, _) => { if (!_resultMenuOpen) CloseSearchBox(); };   // click outside → the Search box closes
        Native.ForegroundChanged += BuildAppMenus;
        // If you reopened all those windows yourself, the button returns to "Show Desktop" mode
        Native.ForegroundChanged += () =>
        {
            if (!_desktopShown || Native.DesktopShown) return;
            _desktopShown = false;
            UpdateDesktopButton();
        };
        HoverOpen(StatusBtn, () => ControlCenter.Show(StatusBtn));
        // The panel appears below the hovered icon (moving to another icon moves the panel, no flicker)
        HoverOpen(WifiBtn, () => ControlCenter.Show(WifiBtn));
        HoverOpen(BatteryBtn, () => ControlCenter.Show(BatteryBtn));
        HoverOpen(ClockBtn, () => NotificationCenter.Show(ClockBtn));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.MakeToolWindow(this);
        Native.MakeNoActivate(this);

        // Reserve space at the top of the screen so maximized windows don't cover the menu bar
        double scale = Native.DpiScale(this);
        int topPx = Native.RegisterTopAppBar(this, (int)Math.Round(BarHeight * scale));
        Left = 0;
        Top = topPx / scale;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = BarHeight;
    }

    protected override void OnClosed(EventArgs e)
    {
        Native.RemoveAppBar(this);
        base.OnClosed(e);
    }

    void Refresh()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("ddd MMM d   h:mm tt", CultureInfo.GetCultureInfo("en-US"));

        UpdateWifi();
        UpdateBattery();
        UpdateAppName();

        // Next tick exactly at the start of the next minute
        _timer.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond)
                          + TimeSpan.FromMilliseconds(50);
    }

    void UpdateWifi() => WifiIcon.Opacity = NetworkInterface.GetIsNetworkAvailable() ? 1 : 0.35;

    void UpdateBattery()
    {
        if (PowerManager.BatteryStatus == BatteryStatus.NotPresent)
        {
            BatteryBox.Visibility = Visibility.Collapsed;
            return;
        }
        int pct = PowerManager.RemainingChargePercent;
        bool plugged = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;

        BatteryBox.Visibility = Visibility.Visible;
        BatteryPct.Text = pct.ToString();   // number inside the icon
        BatteryFill.Width = Math.Max(2, BatteryInnerWidth * pct / 100.0);
        BatteryFill.Background = plugged ? Green : pct <= 20 ? Red : Brushes.White;
        // Dark number over a light fill; white when the fill is low / red so it stays readable
        BatteryPct.Foreground = pct >= 45 && !(pct <= 20) ? Ink : Brushes.White;
        BatteryBtn.ToolTip = $"Battery {pct}%" + (plugged ? (pct >= 100 ? " · Fully charged" : " · Charging") : "");
    }

    void UpdateAppName()
    {
        var (hwnd, pid, cls) = Native.ForegroundInfo();
        if (hwnd == IntPtr.Zero || pid == _ownPid) return; // keep the previous name

        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd")
        {
            AppName.Text = "Desktop";
            return;
        }

        try
        {
            using var p = Process.GetProcessById((int)pid);

            // Store apps (Settings, Calculator, etc.) run inside ApplicationFrameHost
            if (p.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                AppName.Text = Native.WindowTitle(hwnd);
                return;
            }

            if (!_nameCache.TryGetValue(pid, out var name))
            {
                try { name = p.MainModule?.FileVersionInfo.FileDescription; } catch { name = null; }
                if (string.IsNullOrWhiteSpace(name)) name = p.ProcessName;
                _nameCache[pid] = name;
            }
            AppName.Text = name;
        }
        catch { /* process already exited */ }
    }

    // macOS-style system menu (the Windows Start menu still opens via the Windows key or "All Apps…")
    void Start_Click(object sender, RoutedEventArgs e) => SystemMenu.Show(StartBtn);
    // macOS-style panel (Windows Quick Settings / Notification Center can still be opened from it)
    void QuickSettings_Click(object sender, RoutedEventArgs e) => ControlCenter.Show(sender as FrameworkElement ?? StatusBtn);
    void Clock_Click(object sender, RoutedEventArgs e) => NotificationCenter.Show(ClockBtn);
    // ================= Search (expanding box in the menu bar) =================

    const double SearchWidth = 300;
    bool _searchOpen;

    // ================= Show desktop =================

    bool _desktopShown;

    void Desktop_Click(object sender, RoutedEventArgs e)
    {
        if (Native.DesktopShown) Native.RestoreDesktop();   // second click: all windows come back
        else Native.ShowDesktop();                          // first click: show the desktop
        _desktopShown = Native.DesktopShown;
        UpdateDesktopButton();
    }

    void UpdateDesktopButton()
    {
        DesktopBtn.ToolTip = _desktopShown ? "Restore all windows" : "Show Desktop";
        DesktopIcon.Opacity = _desktopShown ? 1 : 0.85;
        DesktopIcon.Text = _desktopShown ? "\uE923" : "\uE7F4";   // stacked windows / monitor
    }

    void Search_Click(object sender, RoutedEventArgs e)
    {
        if (_searchOpen) { RunSearch(); return; }   // click 🔍 again = search
        OpenSearchBox();
    }

    void OpenSearchBox()
    {
        _searchOpen = true;
        SearchBox.Text = "";
        _results.Clear();
        RenderResults();
        EnsureAppsLoaded();   // load the app list as soon as the box opens
        SearchPopup.IsOpen = true;
        SearchField.BeginAnimation(WidthProperty, new DoubleAnimation(0, SearchWidth, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        // The menu bar normally doesn't take focus; allow it temporarily so you can type
        Native.SetNoActivate(this, false);
        Activate();
        Native.ForceForeground(this);
        // Focus once the box is actually visible
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            System.Windows.Input.Keyboard.Focus(SearchBox);
        }, DispatcherPriority.Input);
    }

    void CloseSearchBox()
    {
        if (!_searchOpen) return;
        _searchOpen = false;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        anim.Completed += (_, _) => { if (!_searchOpen) SearchPopup.IsOpen = false; };
        SearchField.BeginAnimation(WidthProperty, anim);
        Native.SetNoActivate(this, true);
    }

    void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateResults();
    }

    void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Enter:
                if (_selected >= 0 && _selected < _results.Count) OpenResult(_results[_selected]);
                else RunSearch();
                e.Handled = true; break;
            case System.Windows.Input.Key.Escape: CloseSearchBox(); e.Handled = true; break;
            case System.Windows.Input.Key.Down: MoveSelection(+1); e.Handled = true; break;
            case System.Windows.Input.Key.Up: MoveSelection(-1); e.Handled = true; break;
        }
    }

    // ---------- Search results (Spotlight-style) ----------

    record SearchResult(string Title, string Subtitle, string? IconId, string Glyph, Action Open);

    static List<(string Name, string Id)>? _apps;          // app list (loaded once)
    static bool _appsLoading;
    readonly Dictionary<string, ImageSource?> _iconCache = new();
    List<SearchResult> _results = new();
    int _selected = -1;

    // Frequently searched Settings pages: name, keywords, URI
    static readonly (string Name, string Keys, string Uri)[] SettingsPages =
    {
        ("Display", "display screen monitor resolution brightness scale", "ms-settings:display"),
        ("Night light", "night light warm blue light", "ms-settings:nightlight"),
        ("Sound", "sound audio volume speaker microphone", "ms-settings:sound"),
        ("Bluetooth & devices", "bluetooth devices mouse keyboard headset", "ms-settings:bluetooth"),
        ("Wi-Fi", "wifi wi-fi wireless network internet", "ms-settings:network-wifi"),
        ("Network & internet", "network internet ethernet vpn proxy", "ms-settings:network"),
        ("Power & battery", "power battery sleep energy", "ms-settings:batterysaver"),
        ("Notifications", "notifications do not disturb focus", "ms-settings:notifications"),
        ("Personalization", "personalization theme wallpaper background colors", "ms-settings:personalization"),
        ("Background", "background wallpaper desktop", "ms-settings:personalization-background"),
        ("Colors", "colors accent dark light mode", "ms-settings:colors"),
        ("Apps", "apps installed uninstall programs", "ms-settings:appsfeatures"),
        ("Default apps", "default apps browser pdf", "ms-settings:defaultapps"),
        ("Startup apps", "startup apps login", "ms-settings:startupapps"),
        ("Mouse", "mouse pointer cursor scroll", "ms-settings:mousetouchpad"),
        ("Touchpad", "touchpad trackpad gestures", "ms-settings:devices-touchpad"),
        ("Keyboard & typing", "keyboard typing language input", "ms-settings:typing"),
        ("Printers & scanners", "printer scanner print", "ms-settings:printers"),
        ("Storage", "storage disk space cleanup", "ms-settings:storagesense"),
        ("Windows Update", "windows update updates upgrade", "ms-settings:windowsupdate"),
        ("Date & time", "date time clock timezone", "ms-settings:dateandtime"),
        ("Language & region", "language region", "ms-settings:regionlanguage"),
        ("Privacy & security", "privacy security camera microphone location permissions", "ms-settings:privacy"),
        ("Accounts", "accounts user sign-in password pin", "ms-settings:accounts"),
        ("About this PC", "about system info specs pc name", "ms-settings:about"),
        ("Multitasking", "multitasking snap windows virtual desktops", "ms-settings:multitasking"),
        ("Clipboard", "clipboard history copy paste", "ms-settings:clipboard"),
    };

    /// Load the app list on a separate thread (keeps the menu bar responsive).
    void EnsureAppsLoaded()
    {
        if (_apps != null || _appsLoading) return;
        _appsLoading = true;
        var t = new System.Threading.Thread(() =>
        {
            var apps = Native.GetAllApps()
                .Where(a => !a.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            Dispatcher.BeginInvoke(() => { _apps = apps; _appsLoading = false; UpdateResults(); });
        }) { IsBackground = true };
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
    }

    static int Score(string name, string q)
    {
        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Split(' ', '-', '.', '&').Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 1;
        if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 2;
        return -1;
    }

    void UpdateResults()
    {
        string q = SearchBox.Text.Trim();
        var results = new List<SearchResult>();
        if (q.Length > 0)
        {
            EnsureAppsLoaded();
            // Apps
            if (_apps != null)
                foreach (var (name, id) in _apps.Select(a => (a, s: Score(a.Name, q))).Where(x => x.s >= 0)
                                                 .OrderBy(x => x.s).ThenBy(x => x.a.Name.Length).Take(5).Select(x => x.a))
                {
                    string appId = id;
                    results.Add(new SearchResult(name, "Application", Native.AppsFolderPrefix + appId, "\uE71D",
                        () => Process.Start("explorer.exe", Native.AppsFolderPrefix + appId)));
                }
            // Settings
            foreach (var page in SettingsPages
                         .Select(p => (p, s: Math.Max(Score(p.Name, q) is int a && a >= 0 ? 3 - a : 0,
                                                      p.Keys.Contains(q, StringComparison.OrdinalIgnoreCase) ? 1 : 0)))
                         .Where(x => x.s > 0).OrderByDescending(x => x.s).Take(3).Select(x => x.p))
            {
                string uri = page.Uri;
                results.Add(new SearchResult(page.Name, "Settings", null, "\uE713", () => Ui.Open(uri)));
            }
            // Web & Windows Search
            results.Add(new SearchResult($"Search the web for “{q}”", "Google", null, "\uE774",
                () => Ui.Open("https://www.google.com/search?q=" + Uri.EscapeDataString(q))));
            results.Add(new SearchResult($"Search files & more for “{q}”", "Windows Search", null, "\uE721", RunWindowsSearch));
        }

        _results = results;
        _selected = results.Count > 0 ? 0 : -1;
        RenderResults();
    }

    void RenderResults()
    {
        SearchResults.Children.Clear();
        SearchResultsBox.Visibility = _results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_apps == null && _appsLoading && SearchBox.Text.Trim().Length > 0)
            SearchResults.Children.Add(new TextBlock { Text = "Loading apps…", Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), FontSize = 11.5, Margin = new Thickness(8, 4, 0, 4) });

        string? lastGroup = null;
        for (int i = 0; i < _results.Count; i++)
        {
            var r = _results[i];
            string group = r.Subtitle is "Application" ? "Applications" : r.Subtitle is "Settings" ? "Settings" : "Search";
            if (group != lastGroup)
            {
                SearchResults.Children.Add(new TextBlock
                {
                    Text = group.ToUpperInvariant(), Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                    FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, lastGroup == null ? 2 : 8, 0, 3)
                });
                lastGroup = group;
            }
            SearchResults.Children.Add(ResultRow(r, i));
        }
    }

    Border ResultRow(SearchResult r, int index)
    {
        FrameworkElement icon;
        if (r.IconId != null)
        {
            if (!_iconCache.TryGetValue(r.IconId, out var img))
                _iconCache[r.IconId] = img = Native.GetShellItemImage(r.IconId, 32);
            icon = new Image { Source = img, Width = 22, Height = 22 };
        }
        else icon = new TextBlock
        {
            Text = r.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15,
            Foreground = Brushes.White, Width = 22, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = r.Title, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = r.Subtitle, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), FontSize = 11 });
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(icon);
        sp.Children.Add(texts);

        bool sel = index == _selected;
        var row = new Border
        {
            Child = sp, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(8), Cursor = System.Windows.Input.Cursors.Hand,
            Background = sel ? new SolidColorBrush(Color.FromRgb(0x0A, 0x64, 0xD6)) : Brushes.Transparent
        };
        row.MouseEnter += (_, _) => { if (_selected != index) { _selected = index; RenderResults(); } };
        row.MouseLeftButtonUp += (_, _) => OpenResult(r);
        if (r.Subtitle == "Application" && r.IconId != null) row.ContextMenu = AppResultMenu(r);
        return row;
    }

    // ---------- Right-click on an app result: Open, Run as administrator, Open file location, Add to Dock ----------

    bool _resultMenuOpen;

    ContextMenu AppResultMenu(SearchResult r)
    {
        string shellPath = r.IconId!;
        string id = shellPath.StartsWith(Native.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
                    ? shellPath[Native.AppsFolderPrefix.Length..] : shellPath;
        string? exe = ResolveAppPath(id);

        var menu = new ContextMenu();
        MenuItem Item(string header, string glyph, Action act)
        {
            var mi = new MenuItem
            {
                Header = header,
                Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13 }
            };
            mi.Click += (_, _) => { CloseSearchBox(); try { act(); } catch { /* cancelled (e.g. UAC declined) */ } };
            menu.Items.Add(mi);
            return mi;
        }

        Item("Open", "\uE8A7", r.Open);
        Item("Run as administrator", "\uE7EF", () =>
            Process.Start(new ProcessStartInfo(exe ?? shellPath) { UseShellExecute = true, Verb = "runas" }));
        if (exe != null)
            Item("Open file location", "\uE838", () => Process.Start("explorer.exe", $"/select,\"{exe}\""));
        menu.Items.Add(new Separator());
        Item("Add to Dock", "\uE718", () =>
        {
            foreach (Window w in Application.Current.Windows)
                if (w is DockWindow dock) dock.AddPaths(new[] { shellPath });
        });
        Item("Copy path", "\uE8C8", () => Clipboard.SetText(exe ?? shellPath));

        menu.Opened += (_, _) => _resultMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _resultMenuOpen = false;
            if (_searchOpen) Dispatcher.BeginInvoke(() => { Activate(); SearchBox.Focus(); }, DispatcherPriority.Input);
        };
        return menu;
    }

    /// Desktop app IDs in AppsFolder are often "{folder GUID}\sub\app.exe" → convert to the real path.
    static string? ResolveAppPath(string id)
    {
        try
        {
            var folders = new Dictionary<string, Environment.SpecialFolder>(StringComparer.OrdinalIgnoreCase)
            {
                ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.SpecialFolder.System,
                ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = Environment.SpecialFolder.SystemX86,
                ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.SpecialFolder.ProgramFiles,
                ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.SpecialFolder.ProgramFilesX86,
                ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.SpecialFolder.Windows,
            };
            string path = id;
            if (id.StartsWith("{") && id.IndexOf('}') is int end and > 0 && folders.TryGetValue(id[..(end + 1)], out var sf))
                path = System.IO.Path.Combine(Environment.GetFolderPath(sf), id[(end + 1)..].TrimStart('\\'));
            return path.Contains('\\') && System.IO.File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    void MoveSelection(int delta)
    {
        if (_results.Count == 0) return;
        _selected = (_selected + delta + _results.Count) % _results.Count;
        RenderResults();
    }

    void OpenResult(SearchResult r)
    {
        CloseSearchBox();
        try { r.Open(); } catch { }
    }

    void RunSearch() => RunWindowsSearch();

    /// Send the query to Windows Search (apps, files, settings, web).
    async void RunWindowsSearch()
    {
        string q = SearchBox.Text.Trim();
        CloseSearchBox();
        Native.OpenSearch();
        if (q.Length == 0) return;
        await Task.Delay(500);   // wait for the Windows Search panel to open
        Native.TypeText(q);
    }

    // ================= App menus (File, Edit, View, ...) =================

    IntPtr _menuOwner = (IntPtr)(-1);
    List<Button>? _genericMenus;   // built once and reused (so they don't pile up)

    /// Build the menu titles for the active app.
    void BuildAppMenus()
    {
        var target = Native.LastForeground;
        if (target == _menuOwner) return;
        _menuOwner = target;
        AppMenusPanel.Children.Clear();
        if (target == IntPtr.Zero || AppName.Text == "Desktop") return;

        var titles = Native.GetMenuTitles(target);
        if (titles.Count > 0)
        {
            // Classic apps: use their real menu (click = open that app's own menu)
            foreach (var t in titles.Take(8))
            {
                var mn = AppMenus.Mnemonic(t);
                var btn = MenuTitle(AppMenus.Clean(t));
                btn.Click += (_, _) => { if (mn is char c) Native.OpenAppMenu(target, c); };
                AppMenusPanel.Children.Add(btn);
            }
            return;
        }

        // Other apps: a generic macOS-style menu with standard keyboard shortcuts
        if (_genericMenus == null)
        {
            _genericMenus = new List<Button>();
            foreach (var (title, items) in AppMenus.Generic)
            {
                var btn = MenuTitle(title);
                btn.Click += (_, _) => SystemMenu.ShowMenu(btn, items);
                HoverOpen(btn, () => SystemMenu.ShowMenu(btn, items));
                _genericMenus.Add(btn);
            }
        }
        foreach (var btn in _genericMenus) AppMenusPanel.Children.Add(btn);
    }

    Button MenuTitle(string text) => new()
    {
        Style = (Style)FindResource("BarBtn"),
        Content = new TextBlock { Text = text, FontSize = 12.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }
    };

    // ================= Background apps (system tray) =================

    async void Tray_Click(object sender, RoutedEventArgs e)
    {
        await Native.ShowTrayOverflowAsync();
        // Hide the taskbar again after the Windows icon panel closes
        var started = DateTime.Now;
        var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        watch.Tick += (_, _) =>
        {
            bool open = Native.IsTrayOverflowOpen();
            if ((!open && DateTime.Now - started > TimeSpan.FromSeconds(2)) || DateTime.Now - started > TimeSpan.FromSeconds(60))
            {
                watch.Stop();
                Native.TaskbarHideSuspendedUntil = DateTime.MinValue;
                Native.HideTaskbar();
            }
        };
        watch.Start();
    }

    // ================= Microphone/camera & Focus =================

    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    void UpdateStatus()
    {
        var (mic, cam) = PrivacyIndicators.Read();
        MicDot.Visibility = mic.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CamDot.Visibility = cam.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PrivacyBox.Visibility = mic.Count + cam.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var tip = new List<string>();
        if (mic.Count > 0) tip.Add("Microphone in use: " + string.Join(", ", mic));
        if (cam.Count > 0) tip.Add("Camera in use: " + string.Join(", ", cam));
        PrivacyBox.ToolTip = tip.Count > 0 ? string.Join("\n", tip) : null;

        bool? focus = FocusAssist.IsOn();
        FocusIcon.Opacity = focus == true ? 1 : 0.55;
        FocusBtn.ToolTip = focus == true ? "Focus is on (click to turn off)" : "Focus is off (click to turn on)";
    }

    async void Focus_Click(object sender, RoutedEventArgs e)
    {
        bool on = FocusAssist.IsOn() == true;
        FocusAssist.Set(!on);
        await Task.Delay(400);
        // If Windows rejects the change, open Notification Center (it has a Do not disturb button)
        if (FocusAssist.IsOn() == on) Native.OpenCalendar();
        UpdateStatus();
    }

    // ================= Media capsule =================

    GlobalSystemMediaTransportControlsSessionManager? _media;
    GlobalSystemMediaTransportControlsSession? _session;
    bool _playing, _eqOn;
    string _trackKey = "";

    async Task StartMediaAsync()
    {
        try
        {
            _media = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _media.CurrentSessionChanged += (_, _) => Dispatcher.BeginInvoke(async () => await UpdateMediaAsync());
            await UpdateMediaAsync();
        }
        catch { /* media controls unavailable */ }
    }

    async Task UpdateMediaAsync()
    {
        var s = _media?.GetCurrentSession();
        if (!ReferenceEquals(s, _session))
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= Session_Changed;
                _session.PlaybackInfoChanged -= Session_Changed;
            }
            _session = s;
            if (s != null)
            {
                s.MediaPropertiesChanged += Session_Changed;
                s.PlaybackInfoChanged += Session_Changed;
            }
        }

        string title = "", artist = "";
        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        try
        {
            if (s != null)
            {
                _playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                props = await s.TryGetMediaPropertiesAsync();
                title = props?.Title ?? "";
                artist = props?.Artist ?? "";
            }
            else _playing = false;
        }
        catch { _playing = false; }

        bool has = !string.IsNullOrWhiteSpace(title);
        // Song info is shown in the Dynamic Island, so the menu bar media capsule is hidden
        MediaPill.Visibility = Visibility.Collapsed;
        MediaText.Text = string.IsNullOrWhiteSpace(artist) ? title : $"{title} - {artist}";
        MediaPill.ToolTip = _playing ? "Pause" : "Play";

        string key = title + "|" + artist;
        if (has && key != _trackKey)
        {
            _trackKey = key;
            MediaArt.Source = null;
            try
            {
                if (props?.Thumbnail != null)
                {
                    using var ras = await props.Thumbnail.OpenReadAsync();
                    using var stream = ras.AsStreamForRead();
                    var bmp = new BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = 48;
                    bmp.StreamSource = stream; bmp.EndInit(); bmp.Freeze();
                    MediaArt.Source = bmp;
                }
            }
            catch { }
        }
        if (!has) _trackKey = "";
        UpdateMediaEq();
    }

    void Session_Changed(GlobalSystemMediaTransportControlsSession sender, object args) =>
        Dispatcher.BeginInvoke(async () => await UpdateMediaAsync());

    /// The equalizer only animates while a song plays & not in power-saving mode.
    void UpdateMediaEq()
    {
        bool on = _playing && MediaPill.Visibility == Visibility.Visible && !Perf.PowerSaving && !Perf.Fullscreen;
        MediaEq.Opacity = _playing ? 1 : 0.4;
        if (on == _eqOn) return;
        _eqOn = on;
        int i = 0;
        foreach (var bar in new[] { MEq1, MEq2, MEq3, MEq4 })
        {
            var st = (ScaleTransform)bar.RenderTransform;
            if (!on) { st.BeginAnimation(ScaleTransform.ScaleYProperty, null); continue; }
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(300 + i++ * 110))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
            });
        }
    }

    async void Media_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    // ================= Notification capsule =================

    readonly DispatcherTimer _notifTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    string? _notifApp;
    bool _notifBusy;
    DateTime _notifSeen = DateTime.MinValue;   // notifications before this time are no longer shown

    async Task RefreshNotifAsync()
    {
        if (_notifBusy) return;
        _notifBusy = true;
        try
        {
            var recent = await Task.Run(() => NotificationCounts.ReadRecent(20));
            // Only notifications from the last 30 minutes
            var fresh = recent.Where(n => DateTime.Now - n.Time < TimeSpan.FromMinutes(30) && n.Time > _notifSeen).ToList();
            var latest = fresh.FirstOrDefault();
            if (latest == null)
            {
                NotifPill.Visibility = AppBubble.Visibility = Visibility.Collapsed;
                _notifApp = null;
                return;
            }

            var icon = Native.GetShellItemImage(Native.AppsFolderPrefix + latest.AppId, 32);
            NotifIcon.Source = icon;
            NotifIcon.Visibility = icon == null ? Visibility.Collapsed : Visibility.Visible;
            string text = latest.Title.Length > 0 && latest.Body.Length > 0 ? $"{latest.Title}: {latest.Body}"
                        : latest.Title.Length > 0 ? latest.Title : latest.Body;
            NotifText.Text = text.Replace('\n', ' ');
            NotifPill.ToolTip = text;
            NotifPill.Visibility = Visibility.Collapsed;   // the notification text capsule isn't used (the icon bubble is enough)

            // Bubble: the notifying app + its notification count
            _notifApp = latest.AppId;
            int count = fresh.Count(n => n.AppId == latest.AppId);
            BubbleIcon.Source = icon;
            BubbleBadgeText.Text = count > 99 ? "99+" : count.ToString();
            BubbleBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AppBubble.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
        finally { _notifBusy = false; }
    }

    void Notif_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => NotificationCenter.Show(NotifPill);

    void Bubble_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_notifApp == null) return;
        try { Process.Start("explorer.exe", Native.AppsFolderPrefix + _notifApp); } catch { }
        _notifSeen = DateTime.Now;   // counts as seen
        AppBubble.Visibility = NotifPill.Visibility = Visibility.Collapsed;
    }

    /// A panel opens on its own when the cursor rests on its button briefly (0.25 s).
    /// If another panel is already open, it switches instantly, like the macOS menu bar.
    void HoverOpen(FrameworkElement button, Action open)
    {
        Ui.RegisterAnchor(button, open);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { timer.Stop(); if (button.IsMouseOver) open(); };
        button.MouseEnter += (_, _) =>
        {
            if (Ui.AnyOpen) open();
            else { timer.Stop(); timer.Start(); }
        };
        button.MouseLeave += (_, _) => timer.Stop();
    }
}
