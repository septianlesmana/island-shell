using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DynamicIsland;

/// System menu like the Apple menu on macOS: drops down from the menu bar in the top-left corner.
static class SystemMenu
{
    static readonly Brush MenuBg = new SolidColorBrush(Color.FromArgb(0xF0, 0x2C, 0x2C, 0x2E));
    static readonly Brush MenuBorder = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    static readonly Brush Highlight = new SolidColorBrush(Color.FromRgb(0x0A, 0x64, 0xD6));
    static readonly Brush TextBrush = Brushes.White;
    static readonly Brush HintBrush = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
    static readonly Brush LineBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");

    static Popup? _popup;

    /// Open (if not already open). Used on hover / click.
    public static void Show(FrameworkElement anchor) { if (_popup?.IsOpen != true) Toggle(anchor); }

    public static void Toggle(FrameworkElement anchor)
    {
        if (_popup?.IsOpen == true) { _popup.IsOpen = false; return; }

        var list = new StackPanel();
        var root = new Grid();
        var panel = MenuPanel(list);
        root.Children.Add(panel);

        _popup = new Popup
        {
            Child = root, PlacementTarget = anchor, Placement = PlacementMode.Bottom,
            VerticalOffset = 3, HorizontalOffset = -2,
            AllowsTransparency = true, StaysOpen = false, PopupAnimation = PopupAnimation.Fade
        };
        var popup = _popup;
        void Run(Action act) { popup.IsOpen = false; try { act(); } catch { } }

        // "Recent Items" submenu (nested Popup so clicks inside it don't close the main menu)
        var recentList = new StackPanel();
        var recentPopup = new Popup
        {
            Child = MenuPanel(recentList), Placement = PlacementMode.Right, HorizontalOffset = -4,
            VerticalOffset = -6, AllowsTransparency = true, StaysOpen = true, PopupAnimation = PopupAnimation.Fade
        };
        root.Children.Add(recentPopup);
        popup.Closed += (_, _) => recentPopup.IsOpen = false;

        foreach (var file in RecentFiles())
        {
            var path = file;
            recentList.Children.Add(Item(Path.GetFileNameWithoutExtension(path), null,
                () => Run(() => Open(path)), recentPopup, icon: Native.GetShellIcon(path)));
        }
        if (recentList.Children.Count == 0)
            recentList.Children.Add(Item("No Items", null, null, recentPopup));

        string user = Environment.UserName;
        list.Children.Add(Item("About This PC", null, () => Run(() => Open("ms-settings:about")), recentPopup));
        list.Children.Add(Line());
        list.Children.Add(Item("System Settings…", null, () => Run(() => Open("ms-settings:")), recentPopup));
        list.Children.Add(Item("Microsoft Store…", null, () => Run(() => Open("ms-windows-store:")), recentPopup));
        list.Children.Add(Item("All Apps…", "Win", () => Run(Native.OpenStart), recentPopup));
        // Desktop widgets: show / hide (remembered)
        list.Children.Add(Item(DesktopWidgets.Enabled ? "Hide Desktop Widgets" : "Show Desktop Widgets", null, () => Run(() =>
        {
            bool on = !DesktopWidgets.Enabled;
            DesktopWidgets.Enabled = on;
            var existing = Application.Current.Windows.OfType<DesktopWidgets>().FirstOrDefault();
            if (on && existing == null) new DesktopWidgets().Show();
            else if (!on) existing?.Close();
        }), recentPopup));
        // macOS-style Windows look: light / dark (can be restored)
        const string applyInfo = "\n\n• Transparency effects\n• macOS blue accent color\n• macOS-style wallpaper\n• Hide desktop icons" +
                                 "\n\nYour current settings are saved and can be restored from this menu.";
        if (MacTheme.IsApplied)
        {
            bool dark = MacTheme.IsDark();
            list.Children.Add(Item(dark ? "Switch to macOS Light Look" : "Switch to macOS Dark Look", null,
                () => Run(() => MacTheme.Apply(dark: !dark)), recentPopup));
            list.Children.Add(Item("Restore Windows Look", null, () => Run(() =>
            {
                if (Confirm("Restore your previous Windows theme, accent color, wallpaper and desktop icons?")) MacTheme.Restore();
            }), recentPopup));
        }
        else
        {
            list.Children.Add(Item("Apply macOS Look (Light)…", null, () => Run(() =>
            {
                if (Confirm("Apply a macOS-style light look to Windows?\n\n• Light mode" + applyInfo)) MacTheme.Apply(dark: false);
            }), recentPopup));
            list.Children.Add(Item("Apply macOS Look (Dark)…", null, () => Run(() =>
            {
                if (Confirm("Apply a macOS-style dark look to Windows?\n\n• Dark mode" + applyInfo)) MacTheme.Apply(dark: true);
            }), recentPopup));
        }
        list.Children.Add(Line());
        var recentRow = Item("Recent Items", "›", null, null);
        bool recentGlass = false;
        recentRow.MouseEnter += (_, _) =>
        {
            recentPopup.PlacementTarget = recentRow;
            // Glass background for the submenu, computed once from where it appears (right of the row, offset -4/-6)
            if (!recentGlass && recentPopup.Child is Border sub)
            {
                recentGlass = true;
                try
                {
                    sub.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var m = sub.Margin;
                    double scale = Native.DpiScale(recentRow);
                    var o = recentRow.PointToScreen(new Point(recentRow.ActualWidth, 0));
                    sub.Background = Ui.GlassBrush(o.X + (-4 + m.Left) * scale, o.Y + (-6 + m.Top) * scale,
                                                   sub.DesiredSize.Width - m.Left - m.Right,
                                                   sub.DesiredSize.Height - m.Top - m.Bottom, scale);
                }
                catch { }
            }
            recentPopup.IsOpen = true;
        };
        list.Children.Add(recentRow);
        list.Children.Add(Line());
        list.Children.Add(Item("Force Quit…", "Ctrl+Shift+Esc", () => Run(() => Open("taskmgr.exe")), recentPopup));
        list.Children.Add(Line());
        list.Children.Add(Item("Sleep", null, () => Run(() => SetSuspendState(false, false, false)), recentPopup));
        list.Children.Add(Item("Restart…", null, () => Run(() =>
        {
            if (Confirm("Are you sure you want to restart your computer now?")) Shutdown("/r /t 0");
        }), recentPopup));
        list.Children.Add(Item("Shut Down…", null, () => Run(() =>
        {
            if (Confirm("Are you sure you want to shut down your computer now?")) Shutdown("/s /t 0");
        }), recentPopup));
        list.Children.Add(Line());
        list.Children.Add(Item("Lock Screen", "Win+L", () => Run(() => LockWorkStation()), recentPopup));
        list.Children.Add(Item($"Log Out {user}…", null, () => Run(() =>
        {
            if (Confirm($"Are you sure you want to log out {user}? All open apps will be closed."))
                ExitWindowsEx(0 /* EWX_LOGOFF */, 0);
        }), recentPopup));

        // Auto-close when the cursor has left the menu (and submenu / button) for more than 0.4 s
        Ui.AutoClose(popup, panel, anchor,
            () => recentPopup.IsOpen && recentPopup.Child is FrameworkElement sub && Ui.CursorOver(sub));

        // Glass background like the Dynamic Island (computed from where the menu appears: below the button, offset -2/+3)
        try
        {
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var m = panel.Margin;
            double scale = Native.DpiScale(anchor);
            var o = anchor.PointToScreen(new Point(0, 0));
            panel.Background = Ui.GlassBrush(o.X + (-2 + m.Left) * scale, o.Y + (anchor.ActualHeight + 3 + m.Top) * scale,
                                             panel.DesiredSize.Width - m.Left - m.Right,
                                             panel.DesiredSize.Height - m.Top - m.Bottom, scale);
        }
        catch { }
        popup.IsOpen = true;
    }

    /// Whether the cursor is over this element (in screen pixels).
    static bool CursorOver(FrameworkElement el)
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

    // ---------- App menus (File, Edit, View, ...) ----------

    static Popup? _appPopup;
    public static bool AppMenuOpen => _appPopup?.IsOpen == true;

    /// Show a menu with a list of entries below the button, with the same style & glass background.
    public static void ShowMenu(FrameworkElement anchor, IEnumerable<AppMenus.Entry> entries)
    {
        if (_appPopup?.IsOpen == true && ReferenceEquals(_appPopup.PlacementTarget, anchor)) return;
        if (_appPopup != null) _appPopup.IsOpen = false;

        var list = new StackPanel();
        var panel = MenuPanel(list);
        var popup = new Popup
        {
            Child = panel, PlacementTarget = anchor, Placement = PlacementMode.Bottom,
            VerticalOffset = 3, HorizontalOffset = -2,
            AllowsTransparency = true, StaysOpen = false, PopupAnimation = PopupAnimation.Fade
        };
        _appPopup = popup;
        foreach (var e in entries)
        {
            if (e.Text == null) { list.Children.Add(Line()); continue; }
            var act = e.Act;
            list.Children.Add(Item(e.Text, e.Hint, act == null ? null : () => { popup.IsOpen = false; try { act(); } catch { } }, null));
        }
        Ui.AutoClose(popup, panel, anchor);
        try
        {
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var m = panel.Margin;
            double scale = Native.DpiScale(anchor);
            var o = anchor.PointToScreen(new Point(0, 0));
            panel.Background = Ui.GlassBrush(o.X + (-2 + m.Left) * scale, o.Y + (anchor.ActualHeight + 3 + m.Top) * scale,
                                             panel.DesiredSize.Width - m.Left - m.Right,
                                             panel.DesiredSize.Height - m.Top - m.Bottom, scale);
        }
        catch { }
        popup.IsOpen = true;
    }

    // ---------- View ----------

    static Border MenuPanel(StackPanel content) => new()
    {
        Background = MenuBg, BorderBrush = MenuBorder, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(5), Margin = new Thickness(10, 2, 10, 14),
        MinWidth = 230, Child = content,
        Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 5, Direction = 270, Opacity = 0.4 }
    };

    static Border Line() => new()
    {
        Height = 1, Background = LineBrush, Margin = new Thickness(10, 5, 10, 5)
    };

    /// One menu row. onClick null = disabled row (or a submenu opener).
    static Border Item(string text, string? hint, Action? onClick, Popup? closeOnHover, ImageSource? icon = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (icon != null)
        {
            var img = new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
            grid.Children.Add(img);
        }
        var label = new TextBlock
        {
            Text = text, FontFamily = UiFont, FontSize = 13, Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 320, Opacity = onClick == null && hint != "›" ? 0.45 : 1
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (hint != null)
        {
            var h = new TextBlock
            {
                Text = hint, FontFamily = UiFont, FontSize = hint == "›" ? 15 : 12, Foreground = HintBrush,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, hint == "›" ? 2 : 0)
            };
            Grid.SetColumn(h, 2);
            grid.Children.Add(h);
        }

        var row = new Border
        {
            Child = grid, Height = 24, Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(4), Background = Brushes.Transparent
        };
        bool active = onClick != null || hint == "›";
        row.MouseEnter += (_, _) =>
        {
            if (active) row.Background = Highlight;
            if (closeOnHover != null && closeOnHover.PlacementTarget != null &&
                !IsInside(row, closeOnHover)) closeOnHover.IsOpen = false;
        };
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        if (onClick != null) row.MouseLeftButtonUp += (_, _) => onClick();
        return row;
    }

    /// Rows inside a submenu must not close that submenu.
    static bool IsInside(DependencyObject el, Popup popup)
    {
        for (var p = el; p != null; p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
            if (ReferenceEquals(p, popup.Child)) return true;
        return false;
    }

    // ---------- Actions ----------

    static string[] RecentFiles()
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            return new DirectoryInfo(dir).GetFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTime)
                .Take(10).Select(f => f.FullName).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    static void Shutdown(string args) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });

    static bool Confirm(string message) =>
        MessageBox.Show(message, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);
}
