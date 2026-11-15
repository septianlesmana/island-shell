using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Security.Credentials;
using Windows.Media.Control;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace DynamicIsland;

/// macOS-style Control Center: Wi-Fi, Bluetooth, brightness, volume, Now Playing, battery.
static class ControlCenter
{
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    static readonly Brush PanelBg = Ui.Brush(0xF2, 0x1E, 0x1E, 0x20);
    static readonly Brush ModuleBg = Ui.Brush(0x1F, 0xFF, 0xFF, 0xFF);   // thin white on glass
    static readonly Brush On = Ui.Brush(0xFF, 0x0A, 0x84, 0xFF);
    static readonly Brush OffBg = Ui.Brush(0xFF, 0x48, 0x48, 0x4A);
    static readonly Brush Sub = Ui.Brush(0xA0, 0xFF, 0xFF, 0xFF);

    static Popup? _popup;

    /// Open (if not already open). Used on hover / click.
    public static void Show(FrameworkElement anchor) { if (_popup?.IsOpen != true) Toggle(anchor); }

    public static void Toggle(FrameworkElement anchor)
    {
        if (_popup?.IsOpen == true) { _popup.IsOpen = false; return; }

        Popup? popup = null;
        var content = BuildContent(wide: false, close: () => { if (popup != null) popup.IsOpen = false; });
        popup = Ui.MakePopup(anchor, content, PanelBg, alignRight: true);
        _popup = popup;
        Ui.ApplyGlass(popup, anchor, alignRight: true);
        popup.IsOpen = true;
    }

    /// Control Center content. wide = two columns (Controls tab in the Dynamic Island),
    /// otherwise one column (menu bar panel). close: closes the panel/notch.
    public static FrameworkElement BuildContent(bool wide, Action close)
    {
        var stack = new StackPanel { Width = 320 };
        var right = wide ? new StackPanel { Width = 320, Margin = new Thickness(12, 0, 0, 0) } : stack;
        var main = wide ? new StackPanel { Orientation = Orientation.Horizontal } : (Panel)stack;
        if (wide) { main.Children.Add(stack); main.Children.Add(right); }
        var wifiPage = new StackPanel { Width = wide ? 652 : 320, Visibility = Visibility.Collapsed };
        var root = new Grid();
        root.Children.Add(main);
        root.Children.Add(wifiPage);

        // ---- Row 1: Wi-Fi + Bluetooth | Battery + Quick Settings ----
        var row1 = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row1.ColumnDefinitions.Add(new ColumnDefinition());
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        row1.ColumnDefinitions.Add(new ColumnDefinition());

        var net = new StackPanel();
        var wifi = ToggleRow("\uE701", "Wi-Fi", "…");
        // Click the Wi-Fi name / arrow → network list (like the macOS Wi-Fi menu)
        var chevron = new TextBlock
        {
            Text = "\uE76C", FontFamily = Icons, FontSize = 10, Foreground = Sub, VerticalAlignment = VerticalAlignment.Center
        };
        wifi.Row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(chevron, 2);
        wifi.Row.Children.Add(chevron);
        wifi.Row.Background = Brushes.Transparent;
        wifi.Row.Cursor = Cursors.Hand;
        wifi.Row.ToolTip = "Show Wi-Fi networks";
        wifi.Row.MouseLeftButtonUp += (_, _) =>
        {
            main.Visibility = Visibility.Collapsed;
            wifiPage.Visibility = Visibility.Visible;
            BuildWifiPage(wifiPage, close, wide ? 150 : 360, back: () =>
            {
                wifiPage.Visibility = Visibility.Collapsed;
                main.Visibility = Visibility.Visible;
            });
        };
        var bt = ToggleRow("\uE702", "Bluetooth", "…");
        net.Children.Add(wifi.Row);
        net.Children.Add(bt.Row);
        row1.Children.Add(Module(net));

        var side = new StackPanel();
        var batt = BatteryTile();
        side.Children.Add(batt);
        var qs = SmallTile("\uE9E9", "Quick Settings", () => { close(); Native.OpenQuickSettings(); });
        qs.Margin = new Thickness(0, 8, 0, 0);
        side.Children.Add(qs);
        Grid.SetColumn(side, 2);
        row1.Children.Add(side);
        stack.Children.Add(row1);

        // ---- Display ----
        var display = SliderModule("Display", "\uE706", out var brightSlider, out _);
        brightSlider.IsEnabled = false;
        right.Children.Add(display);

        // ---- Sound ----
        var sound = SliderModule("Sound", "\uE767", out var volSlider, out var volIcon);
        right.Children.Add(sound);

        // ---- Now Playing ----
        var media = MediaModule(out var updateMedia);
        right.Children.Add(media);

        // ---- Fill in data (async) ----
        _ = LoadRadiosAsync(wifi, bt);

        _ = Task.Run(Brightness.Get).ContinueWith(t =>
        {
            if (t.Result is int b)
            {
                brightSlider.Value = b;
                brightSlider.IsEnabled = true;
            }
            else display.Visibility = Visibility.Collapsed;   // external monitor / PC without brightness control
        }, TaskScheduler.FromCurrentSynchronizationContext());
        var brightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        brightTimer.Tick += (_, _) => { brightTimer.Stop(); int v = (int)brightSlider.Value; Task.Run(() => Brightness.Set(v)); };
        brightSlider.ValueChanged += (_, _) => { if (brightSlider.IsEnabled) { brightTimer.Stop(); brightTimer.Start(); } };

        if (AudioVolume.Get() is (float level, bool muted))
        {
            volSlider.Value = level * 100;
            volIcon.Text = muted ? "\uE74F" : "\uE767";
        }
        else sound.Visibility = Visibility.Collapsed;
        volSlider.ValueChanged += (_, e) => { AudioVolume.Set((float)(e.NewValue / 100)); volIcon.Text = "\uE767"; };
        volIcon.Cursor = Cursors.Hand;
        volIcon.MouseLeftButtonUp += (_, _) =>
        {
            bool m = AudioVolume.ToggleMute();
            volIcon.Text = m ? "\uE74F" : "\uE767";
        };

        _ = updateMedia();
        return root;
    }

    // ================= Wi-Fi & Bluetooth =================

    record ToggleParts(Grid Row, Border Circle, TextBlock Subtitle);

    static ToggleParts ToggleRow(string glyph, string title, string subtitle)
    {
        var circle = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = OffBg, Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = glyph, FontFamily = Icons, FontSize = 14, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var sub = new TextBlock { Text = subtitle, FontFamily = UiFont, FontSize = 11, Foreground = Sub, TextTrimming = TextTrimming.CharacterEllipsis };
        var texts = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, FontFamily = UiFont, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        texts.Children.Add(sub);

        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(circle);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        return new ToggleParts(row, circle, sub);
    }

    static async Task LoadRadiosAsync(ToggleParts wifi, ToggleParts bt)
    {
        try
        {
            await Radio.RequestAccessAsync();
            var radios = await Radio.GetRadiosAsync();
            Bind(wifi, radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi), WifiName);
            Bind(bt, radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth), r => r.State == RadioState.On ? "On" : "Off");
        }
        catch
        {
            wifi.Subtitle.Text = bt.Subtitle.Text = "Unavailable";
        }
    }

    static void Bind(ToggleParts parts, Radio? radio, Func<Radio, string> subtitle)
    {
        if (radio == null) { parts.Subtitle.Text = "Not available"; return; }
        void Show()
        {
            parts.Circle.Background = radio.State == RadioState.On ? On : OffBg;
            parts.Subtitle.Text = subtitle(radio);
        }
        Show();
        parts.Circle.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;   // don't also open the Wi-Fi list
            try
            {
                await radio.SetStateAsync(radio.State == RadioState.On ? RadioState.Off : RadioState.On);
                await Task.Delay(400);
                Show();
            }
            catch { }
        };
    }

    static string WifiName(Radio r)
    {
        if (r.State != RadioState.On) return "Off";
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p?.IsWlanConnectionProfile == true)
                return p.WlanConnectionProfileDetails.GetConnectedSsid() is { Length: > 0 } ssid ? ssid : "Connected";
            return p != null ? "On · " + p.ProfileName : "Not connected";
        }
        catch { return "On"; }
    }

    // ================= Wi-Fi network list =================

    record WifiNet(WiFiAvailableNetwork Network, string Ssid, byte Bars, bool Secured);

    static void BuildWifiPage(StackPanel page, Action close, double listHeight, Action back)
    {
        page.Children.Clear();

        // Header: ‹ Wi-Fi
        var head = new DockPanel { Margin = new Thickness(2, 0, 2, 8) };
        var backBtn = new TextBlock
        {
            Text = "\uE76B", FontFamily = Icons, FontSize = 12, Foreground = Brushes.White, Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4), Background = Brushes.Transparent
        };
        backBtn.MouseLeftButtonUp += (_, _) => back();
        head.Children.Add(backBtn);
        var refresh = new TextBlock
        {
            Text = "\uE72C", FontFamily = Icons, FontSize = 12, Foreground = Sub, Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4), Background = Brushes.Transparent,
            ToolTip = "Scan again"
        };
        DockPanel.SetDock(refresh, Dock.Right);
        head.Children.Add(refresh);
        head.Children.Add(Ui.Text("Wi-Fi", 15, FontWeights.Bold, margin: new Thickness(6, 0, 0, 0)));
        page.Children.Add(head);

        var status = Ui.Text("Searching for networks…", 12, FontWeights.Normal, Sub, new Thickness(4, 0, 0, 6));
        page.Children.Add(status);
        var list = new StackPanel();
        page.Children.Add(new ScrollViewer
        {
            Content = list, MaxHeight = listHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        var settings = Ui.Text("Wi-Fi Settings…", 12.5, FontWeights.SemiBold, Ui.Brush(0xFF, 0x0A, 0x84, 0xFF), new Thickness(4, 8, 0, 0));
        settings.Cursor = Cursors.Hand;
        settings.MouseLeftButtonUp += (_, _) => { close(); Ui.Open("ms-settings:network-wifi"); };
        page.Children.Add(settings);

        async Task Load(bool scan)
        {
            try
            {
                if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed)
                {
                    status.Text = "Windows blocked access to Wi-Fi. Turn on Settings → Privacy & security → Location → \"Let desktop apps access your location\".";
                    status.TextWrapping = TextWrapping.Wrap;
                    return;
                }
                var adapters = await WiFiAdapter.FindAllAdaptersAsync();
                var adapter = adapters.FirstOrDefault();
                if (adapter == null) { status.Text = "No Wi-Fi adapter found"; return; }

                Show(adapter, await ConnectedSsid(adapter));   // show the last results first
                if (scan)
                {
                    status.Text = "Searching for networks…";
                    await adapter.ScanAsync();
                    Show(adapter, await ConnectedSsid(adapter));
                }
            }
            catch (Exception ex) { status.Text = "Couldn't scan: " + ex.Message; status.TextWrapping = TextWrapping.Wrap; }
        }

        void Show(WiFiAdapter adapter, string? connected)
        {
            var nets = adapter.NetworkReport.AvailableNetworks
                .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
                .GroupBy(n => n.Ssid)
                .Select(g => g.OrderByDescending(n => n.SignalBars).First())
                .Select(n => new WifiNet(n, n.Ssid, n.SignalBars,
                    n.SecuritySettings.NetworkAuthenticationType is not (NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None)))
                .OrderByDescending(n => n.Ssid == connected)
                .ThenByDescending(n => n.Bars)
                .ToList();

            list.Children.Clear();
            status.Text = nets.Count == 0 ? "No networks found" : "";
            status.Visibility = nets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var current = nets.Where(n => n.Ssid == connected).ToList();
            var others = nets.Where(n => n.Ssid != connected).ToList();
            if (current.Count > 0)
            {
                foreach (var n in current) list.Children.Add(NetworkRow(n, true, adapter, close, () => _ = Load(false)));
                list.Children.Add(new Border { Height = 1, Background = Ui.Brush(0x26, 0xFF, 0xFF, 0xFF), Margin = new Thickness(6, 4, 6, 6) });
            }
            if (others.Count > 0)
                list.Children.Add(Ui.Text("Other Networks", 11.5, FontWeights.SemiBold, Sub, new Thickness(6, 0, 0, 4)));
            foreach (var n in others) list.Children.Add(NetworkRow(n, false, adapter, close, () => _ = Load(false)));
        }

        refresh.MouseLeftButtonUp += (_, _) => _ = Load(true);
        _ = Load(true);
    }

    static async Task<string?> ConnectedSsid(WiFiAdapter adapter)
    {
        try
        {
            var profile = await adapter.NetworkAdapter.GetConnectedProfileAsync();
            return profile?.IsWlanConnectionProfile == true
                ? profile.WlanConnectionProfileDetails.GetConnectedSsid() ?? profile.ProfileName
                : null;
        }
        catch { return null; }
    }

    static Border NetworkRow(WifiNet n, bool connected, WiFiAdapter adapter, Action close, Action reload)
    {
        // Signal icon: 1–4 bars
        string signal = n.Bars switch { <= 1 => "\uE872", 2 => "\uE873", 3 => "\uE874", _ => "\uE701" };
        var icon = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 10, 0),
            Background = connected ? On : OffBg,
            Child = new TextBlock
            {
                Text = signal, FontFamily = Icons, FontSize = 12, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var name = Ui.Text(n.Ssid, 13, connected ? FontWeights.SemiBold : FontWeights.Normal);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = VerticalAlignment.Center;
        var lockIcon = new TextBlock
        {
            Text = n.Secured ? "\uE72E" : "", FontFamily = Icons, FontSize = 11, Foreground = Sub,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
        };

        var row = new DockPanel();
        row.Children.Add(icon);
        DockPanel.SetDock(lockIcon, Dock.Right);
        row.Children.Add(lockIcon);
        row.Children.Add(name);

        var border = new Border
        {
            Child = row, Padding = new Thickness(6, 4, 8, 4), CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent, Cursor = connected ? Cursors.Arrow : Cursors.Hand,
            ToolTip = connected ? "Connected" : $"Join {n.Ssid}"
        };
        border.MouseEnter += (_, _) => border.Background = Ui.Brush(0x26, 0xFF, 0xFF, 0xFF);
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        if (!connected)
        {
            border.MouseLeftButtonUp += async (_, _) =>
            {
                name.Text = $"{n.Ssid} — connecting…";
                // Saved / open networks connect right away
                var result = await adapter.ConnectAsync(n.Network, WiFiReconnectionKind.Automatic);
                if (result.ConnectionStatus != WiFiConnectionStatus.Success && n.Secured)
                {
                    close();
                    var password = WifiPasswordDialog.Ask(n.Ssid);
                    if (password == null) return;
                    result = await adapter.ConnectAsync(n.Network, WiFiReconnectionKind.Automatic,
                                                        new PasswordCredential { Password = password });
                    if (result.ConnectionStatus != WiFiConnectionStatus.Success)
                        MessageBox.Show($"Couldn't join \"{n.Ssid}\" ({result.ConnectionStatus}).", "Wi-Fi",
                                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (result.ConnectionStatus != WiFiConnectionStatus.Success)
                    name.Text = $"{n.Ssid} — couldn't connect";
                else reload();
            };
        }
        return border;
    }

    // ================= Modules =================

    static Border Module(UIElement child) => new()
    {
        Background = ModuleBg, CornerRadius = new CornerRadius(12), Padding = new Thickness(10), Child = child
    };

    static Border BatteryTile()
    {
        var text = new StackPanel();
        if (PowerManager.BatteryStatus == BatteryStatus.NotPresent)
        {
            text.Children.Add(Ui.Text("Power", 13, FontWeights.SemiBold));
            text.Children.Add(Ui.Text("Plugged in", 11, FontWeights.Normal, Sub));
        }
        else
        {
            int pct = PowerManager.RemainingChargePercent;
            bool plugged = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
            text.Children.Add(Ui.Text($"Battery {pct}%", 13, FontWeights.SemiBold));
            text.Children.Add(Ui.Text(plugged ? (pct >= 100 ? "Fully charged" : "Charging") : "On battery", 11, FontWeights.Normal, Sub));
        }
        var tile = Module(text);
        tile.Cursor = Cursors.Hand;
        tile.ToolTip = "Battery Settings";
        tile.MouseLeftButtonUp += (_, _) => Ui.Open("ms-settings:batterysaver");
        return tile;
    }

    static Border SmallTile(string glyph, string title, Action click)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(Ui.Text(title, 12.5, FontWeights.SemiBold, margin: new Thickness(8, 0, 0, 0)));
        var tile = Module(sp);
        tile.Cursor = Cursors.Hand;
        tile.MouseLeftButtonUp += (_, _) => click();
        return tile;
    }

    static Border SliderModule(string title, string glyph, out Slider slider, out TextBlock icon)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(title, 13, FontWeights.SemiBold, margin: new Thickness(0, 0, 0, 6)));
        var row = new DockPanel();
        icon = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
        row.Children.Add(icon);
        slider = new Slider { Minimum = 0, Maximum = 100, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(slider);
        sp.Children.Add(row);
        var m = Module(sp);
        m.Margin = new Thickness(0, 0, 0, 8);
        return m;
    }

    static Border MediaModule(out Func<Task> update)
    {
        var art = new Image { Width = 44, Height = 44, Stretch = Stretch.UniformToFill };
        art.Clip = new RectangleGeometry(new Rect(0, 0, 44, 44), 8, 8);
        var artBox = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(8), Background = OffBg, Child = art };
        var title = Ui.Text("Not Playing", 13, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var artist = Ui.Text("", 11.5, FontWeights.Normal, Sub);
        artist.TextTrimming = TextTrimming.CharacterEllipsis;
        var texts = new StackPanel { Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        texts.Children.Add(artist);

        GlobalSystemMediaTransportControlsSessionManager? manager = null;
        TextBlock play = MediaButton("\uE768");
        var prev = MediaButton("\uE892");
        var next = MediaButton("\uE893");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(prev);
        buttons.Children.Add(play);
        buttons.Children.Add(next);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(artBox);
        Grid.SetColumn(texts, 1); grid.Children.Add(texts);
        Grid.SetColumn(buttons, 2); grid.Children.Add(buttons);

        async Task Update()
        {
            try
            {
                manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var s = manager.GetCurrentSession();
                if (s == null) { title.Text = "Not Playing"; artist.Text = ""; art.Source = null; return; }
                var props = await s.TryGetMediaPropertiesAsync();
                title.Text = string.IsNullOrWhiteSpace(props?.Title) ? "Not Playing" : props!.Title;
                artist.Text = props?.Artist ?? "";
                play.Text = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "\uE769" : "\uE768";
                if (props?.Thumbnail != null)
                {
                    using var ras = await props.Thumbnail.OpenReadAsync();
                    using var stream = ras.AsStreamForRead();
                    var bmp = new BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = 96;
                    bmp.StreamSource = stream; bmp.EndInit(); bmp.Freeze();
                    art.Source = bmp;
                }
                else art.Source = null;
            }
            catch { }
        }
        update = Update;

        async void Act(Func<GlobalSystemMediaTransportControlsSession, Task> act)
        {
            var s = manager?.GetCurrentSession();
            if (s == null) return;
            try { await act(s); await Task.Delay(300); await Update(); } catch { }
        }
        prev.MouseLeftButtonUp += (_, _) => Act(async s => await s.TrySkipPreviousAsync());
        play.MouseLeftButtonUp += (_, _) => Act(async s => await s.TryTogglePlayPauseAsync());
        next.MouseLeftButtonUp += (_, _) => Act(async s => await s.TrySkipNextAsync());

        return Module(grid);
    }

    static TextBlock MediaButton(string glyph) => new()
    {
        Text = glyph, FontFamily = Icons, FontSize = 15, Foreground = Brushes.White, Cursor = Cursors.Hand,
        Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent
    };
}

/// Laptop screen brightness via WMI (not available for external monitors).
static class Brightness
{
    public static int? Get()
    {
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\WMI"), new SelectQuery("WmiMonitorBrightness"));
            foreach (ManagementObject o in s.Get()) using (o) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    public static void Set(int value)
    {
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\WMI"), new SelectQuery("WmiMonitorBrightnessMethods"));
            foreach (ManagementObject o in s.Get())
                using (o) o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)Math.Clamp(value, 0, 100) });
        }
        catch { }
    }
}

/// Windows master volume via Core Audio.
static class AudioVolume
{
    static IAudioEndpointVolume? Endpoint()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) != 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj);
            return obj as IAudioEndpointVolume;
        }
        catch { return null; }
    }

    public static (float Level, bool Muted)? Get()
    {
        var ep = Endpoint();
        if (ep == null) return null;
        ep.GetMasterVolumeLevelScalar(out float level);
        ep.GetMute(out bool muted);
        return (level, muted);
    }

    public static void Set(float level)
    {
        var ep = Endpoint();
        if (ep == null) return;
        var ctx = Guid.Empty;
        ep.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx);
        ep.SetMute(false, ref ctx);
    }

    public static bool ToggleMute()
    {
        var ep = Endpoint();
        if (ep == null) return false;
        ep.GetMute(out bool muted);
        var ctx = Guid.Empty;
        ep.SetMute(!muted, ref ctx);
        return !muted;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

/// Shared UI helpers for menu bar panels.
static class Ui
{
    static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");

    public static Brush Brush(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    public static TextBlock Text(string text, double size, FontWeight weight, Brush? color = null, Thickness? margin = null) => new()
    {
        Text = text, FontFamily = UiFont, FontSize = size, FontWeight = weight,
        Foreground = color ?? Brushes.White, Margin = margin ?? new Thickness(0)
    };

    /// Floating panel below the menu bar: rounded corners + shadow, closes on outside click.
    public static Popup MakePopup(FrameworkElement anchor, UIElement content, Brush bg, bool alignRight)
    {
        var panel = new Border
        {
            Background = bg, BorderBrush = Brush(0x33, 0xFF, 0xFF, 0xFF), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(10), Margin = new Thickness(12, 2, 12, 16),
            Child = content,
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 6, Direction = 270, Opacity = 0.45 }
        };
        var popup = new Popup
        {
            Child = panel, PlacementTarget = anchor, AllowsTransparency = true, StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade, VerticalOffset = 3
        };
        // Only one panel open at a time, and it closes itself when the cursor leaves
        AutoClose(popup, panel, anchor);

        if (alignRight)
        {
            // Right edge of the panel aligned with the right edge of the button
            popup.Placement = PlacementMode.Custom;
            popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[]
            {
                new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 12, targetSize.Height + 3),
                                         PopupPrimaryAxis.Horizontal)
            };
        }
        else popup.Placement = PlacementMode.Bottom;
        return popup;
    }

    // ---------- Glass effect for menu bar panels ----------

    static readonly Brush GlassTint = Brush(0xA6, 0x16, 0x16, 0x18);
    static readonly Brush GlassFallback = Brush(0xF2, 0x1E, 0x1E, 0x20);

    /// Glass background: the screen area behind the panel (screen px x,y; size in DIPs),
    /// blurred + a translucent dark layer. Captured once before the panel is shown.
    public static Brush GlassBrush(double xPx, double yPx, double wDip, double hDip, double scale)
    {
        if (Perf.PowerSaving || wDip <= 0 || hDip <= 0) return GlassFallback;
        try
        {
            const double pad = 30;   // extra margin so the blurred edges don't turn dark
            var shot = Native.CaptureScreen((int)Math.Round(Math.Max(0, xPx - pad * scale)), (int)Math.Round(Math.Max(0, yPx - pad * scale)),
                                            (int)Math.Round((wDip + 2 * pad) * scale), (int)Math.Round((hDip + 2 * pad) * scale));
            if (shot == null) return GlassFallback;
            var img = new Image
            {
                Source = shot, Width = wDip + 2 * pad, Height = hDip + 2 * pad, Stretch = Stretch.Fill,
                Effect = new BlurEffect { Radius = 40, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance }
            };
            var canvas = new Canvas { Width = wDip, Height = hDip, ClipToBounds = true };
            Canvas.SetLeft(img, -pad);
            Canvas.SetTop(img, -pad);
            canvas.Children.Add(img);
            canvas.Measure(new Size(wDip, hDip));
            canvas.Arrange(new Rect(0, 0, wDip, hDip));

            // Blurred at ¼ resolution for speed
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(wDip / 4), (int)Math.Ceiling(hDip / 4), 24, 24, PixelFormats.Pbgra32);
            rtb.Render(canvas);
            rtb.Freeze();

            var group = new DrawingGroup();
            group.Children.Add(new ImageDrawing(rtb, new Rect(0, 0, wDip, hDip)));
            group.Children.Add(new GeometryDrawing(GlassTint, null, new RectangleGeometry(new Rect(0, 0, wDip, hDip))));
            group.Freeze();
            var brush = new DrawingBrush(group) { Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }
        catch { return GlassFallback; }
    }

    /// Apply the glass background to a MakePopup panel, based on where the panel appears.
    public static void ApplyGlass(Popup popup, FrameworkElement anchor, bool alignRight)
    {
        if (popup.Child is not Border panel) return;
        try
        {
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = panel.DesiredSize;   // including margin
            var m = panel.Margin;
            double scale = Native.DpiScale(anchor);
            var origin = anchor.PointToScreen(new Point(0, 0));
            double ox = alignRight ? anchor.ActualWidth - size.Width + 12 : popup.HorizontalOffset;
            double oy = anchor.ActualHeight + 3;
            panel.Background = GlassBrush(origin.X + (ox + m.Left) * scale, origin.Y + (oy + m.Top) * scale,
                                          size.Width - m.Left - m.Right, size.Height - m.Top - m.Bottom, scale);
        }
        catch { }
    }

    // ---------- Menu bar panels: one open, auto-close ----------

    static Popup? _current;
    static readonly List<(FrameworkElement Anchor, Action Open)> _anchors = new();

    /// Menu bar buttons that have a panel. While a panel is open, hovering another button
    /// switches panels immediately (the open panel captures the mouse, so it's checked here).
    public static void RegisterAnchor(FrameworkElement anchor, Action open) => _anchors.Add((anchor, open));

    /// A menu bar panel is open (used to switch panels instantly when the cursor moves).
    public static bool AnyOpen => _current?.IsOpen == true;

    /// Auto-close when the cursor has been outside the panel (and its button) for more than 0.4 s.
    /// extraInside: extra areas that count as "inside", e.g. a submenu.
    public static void AutoClose(Popup popup, FrameworkElement panel, FrameworkElement anchor, Func<bool>? extraInside = null)
    {
        var lastInside = DateTime.Now;
        var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        watch.Tick += (_, _) =>
        {
            if (!popup.IsOpen) { watch.Stop(); return; }
            foreach (var (other, open) in _anchors)
            {
                if (ReferenceEquals(other, anchor) || !CursorOver(other)) continue;
                popup.IsOpen = false;
                watch.Stop();
                anchor.Dispatcher.BeginInvoke(open);
                return;
            }
            bool inside = CursorOver(panel) || CursorOver(anchor) || extraInside?.Invoke() == true
                          || Native.IsLeftButtonDown();   // dragging a slider, etc.
            if (inside) lastInside = DateTime.Now;
            else if (DateTime.Now - lastInside > TimeSpan.FromMilliseconds(400)) popup.IsOpen = false;
        };
        popup.Opened += (_, _) =>
        {
            // Close any other open panel
            if (_current != null && !ReferenceEquals(_current, popup) && _current.IsOpen) _current.IsOpen = false;
            _current = popup;
            lastInside = DateTime.Now;
            watch.Start();
        };
        popup.Closed += (_, _) => watch.Stop();
    }

    /// Whether the cursor is over this element (screen pixels).
    public static bool CursorOver(FrameworkElement el)
    {
        try
        {
            if (!el.IsVisible || PresentationSource.FromVisual(el) == null) return false;
            var (x, y) = Native.CursorPos();
            var tl = el.PointToScreen(new Point(0, 0));
            var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
            return x >= tl.X - 2 && x <= br.X + 2 && y >= tl.Y - 2 && y <= br.Y + 2;
        }
        catch { return false; }
    }

    public static void Open(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { }
    }
}
