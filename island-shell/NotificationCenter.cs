using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland;

/// macOS-style Notification Center (click the clock in the menu bar): date, latest notifications and next events.
static class NotificationCenter
{
    static readonly CultureInfo En = new("en-US");
    static readonly Brush PanelBg = Ui.Brush(0xF2, 0x1E, 0x1E, 0x20);
    static readonly Brush CardBg = Ui.Brush(0x1F, 0xFF, 0xFF, 0xFF);   // thin white on glass
    static readonly Brush Sub = Ui.Brush(0xA0, 0xFF, 0xFF, 0xFF);
    static readonly Brush Blue = Ui.Brush(0xFF, 0x0A, 0x84, 0xFF);
    static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase);

    static System.Windows.Controls.Primitives.Popup? _popup;

    /// Open (if not already open). Used on hover / click.
    public static void Show(FrameworkElement anchor) { if (_popup?.IsOpen != true) Toggle(anchor); }

    public static void Toggle(FrameworkElement anchor)
    {
        if (_popup?.IsOpen == true) { _popup.IsOpen = false; return; }

        var stack = new StackPanel { Width = 340 };
        var scroll = new ScrollViewer
        {
            Content = stack, MaxHeight = SystemParameters.WorkArea.Height - 60,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _popup = Ui.MakePopup(anchor, scroll, PanelBg, alignRight: true);

        // ---- Date ----
        var now = DateTime.Now;
        stack.Children.Add(Ui.Text(now.ToString("dddd, MMMM d", En), 18, FontWeights.Bold, margin: new Thickness(4, 2, 0, 0)));
        stack.Children.Add(Ui.Text(now.ToString("h:mm tt", En), 12.5, FontWeights.Normal, Sub, new Thickness(4, 0, 0, 10)));

        // ---- Notifications ----
        var notifHeader = new DockPanel { Margin = new Thickness(4, 0, 2, 6) };
        var settings = Ui.Text("Settings", 12, FontWeights.SemiBold, Blue);
        settings.Cursor = Cursors.Hand;
        settings.MouseLeftButtonUp += (_, _) => { _popup.IsOpen = false; Ui.Open("ms-settings:notifications"); };
        DockPanel.SetDock(settings, Dock.Right);
        notifHeader.Children.Add(settings);

        var clearAll = Ui.Text("Clear All", 12, FontWeights.SemiBold, Blue, new Thickness(0, 0, 14, 0));
        clearAll.Cursor = Cursors.Hand;
        clearAll.ToolTip = "Hide all current notifications here and reset badges";
        DockPanel.SetDock(clearAll, Dock.Right);
        notifHeader.Children.Add(clearAll);
        notifHeader.Children.Add(Ui.Text("Notifications", 13, FontWeights.Bold));
        stack.Children.Add(notifHeader);

        var notifList = new StackPanel();
        notifList.Children.Add(Ui.Text("Loading…", 12, FontWeights.Normal, Sub, new Thickness(4, 0, 0, 8)));
        stack.Children.Add(notifList);
        clearAll.MouseLeftButtonUp += (_, _) =>
        {
            NotificationCounts.ClearAll();
            notifList.Children.Clear();
            notifList.Children.Add(Ui.Text("No Notifications", 12, FontWeights.Normal, Sub, new Thickness(4, 0, 0, 8)));
        };

        var openWin = Ui.Text("Show all in Windows Notification Center", 12, FontWeights.SemiBold, Blue, new Thickness(4, 2, 0, 12));
        openWin.Cursor = Cursors.Hand;
        openWin.MouseLeftButtonUp += (_, _) => { _popup.IsOpen = false; Native.OpenCalendar(); };
        stack.Children.Add(openWin);

        // ---- Widget: Up Next ----
        stack.Children.Add(UpNextWidget());

        Ui.ApplyGlass(_popup, anchor, alignRight: true);
        _popup.IsOpen = true;

        _ = Task.Run(() => NotificationCounts.ReadRecent(8)).ContinueWith(t =>
        {
            notifList.Children.Clear();
            var toasts = t.Result;
            if (toasts.Count == 0)
                notifList.Children.Add(Ui.Text("No Notifications", 12, FontWeights.Normal, Sub, new Thickness(4, 0, 0, 8)));
            foreach (var n in toasts) notifList.Children.Add(NotificationCard(n));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    static Border NotificationCard(NotificationCounts.Toast n)
    {
        var head = new DockPanel();
        var time = Ui.Text(Ago(n.Time), 11, FontWeights.Normal, Sub);
        DockPanel.SetDock(time, Dock.Right);
        head.Children.Add(time);
        var app = Ui.Text(AppName(n.AppId).ToUpper(En), 10.5, FontWeights.SemiBold, Sub);
        app.TextTrimming = TextTrimming.CharacterEllipsis;
        head.Children.Add(app);

        var body = new StackPanel();
        body.Children.Add(head);
        if (n.Title.Length > 0)
        {
            var t = Ui.Text(n.Title, 13, FontWeights.SemiBold, margin: new Thickness(0, 3, 0, 0));
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            body.Children.Add(t);
        }
        if (n.Body.Length > 0)
        {
            var b = Ui.Text(n.Body, 12.5, FontWeights.Normal);
            b.TextWrapping = TextWrapping.Wrap;
            b.MaxHeight = 36;   // at most 2 lines
            b.TextTrimming = TextTrimming.CharacterEllipsis;
            body.Children.Add(b);
        }
        return new Border
        {
            Background = CardBg, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 9, 12, 10),
            Margin = new Thickness(0, 0, 0, 6), Child = body
        };
    }

    static Border UpNextWidget()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text("UP NEXT", 10.5, FontWeights.Bold, Ui.Brush(0xFF, 0xFF, 0x45, 0x3A), new Thickness(0, 0, 0, 6)));
        var events = CalendarWidget.Upcoming.Where(e => !e.AllDay || e.Start.Date >= DateTime.Today).Take(4).ToList();
        if (events.Count == 0)
            sp.Children.Add(Ui.Text("No upcoming events", 12.5, FontWeights.Normal, Sub));
        foreach (var e in events)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            var bar = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = e.Color, Margin = new Thickness(0, 1, 9, 1) };
            row.Children.Add(bar);
            var texts = new StackPanel();
            var title = Ui.Text(e.Title, 13, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            texts.Children.Add(title);
            string day = e.Start.Date == DateTime.Today ? "Today" : e.Start.Date == DateTime.Today.AddDays(1) ? "Tomorrow" : e.Start.ToString("ddd, MMM d", En);
            string when = e.AllDay ? $"{day} · All day" : $"{day} · {e.Start.ToString("h:mm tt", En)} – {e.End.ToString("h:mm tt", En)}";
            texts.Children.Add(Ui.Text(when, 11.5, FontWeights.Normal, Sub));
            row.Children.Add(texts);
            sp.Children.Add(row);
        }
        var card = new Border
        {
            Background = CardBg, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 10, 12, 4),
            Child = sp, Cursor = Cursors.Hand, ToolTip = "Open Google Calendar"
        };
        card.MouseLeftButtonUp += (_, _) => { if (_popup != null) _popup.IsOpen = false; Ui.Open("https://calendar.google.com/calendar/r"); };
        return card;
    }

    static string AppName(string id)
    {
        if (AppNames.TryGetValue(id, out var name)) return name;
        name = Native.GetShellDisplayName(Native.AppsFolderPrefix + id);
        if (string.IsNullOrWhiteSpace(name))
        {
            // "Microsoft.Office.OUTLOOK.EXE.15" → "Outlook", "Chrome" → "Chrome"
            var parts = id.Split('!')[0].Split('.', '_').Where(p => p.Length > 2 && !p.Equals("exe", StringComparison.OrdinalIgnoreCase)
                                                                    && !p.All(char.IsDigit)).ToList();
            name = parts.Count == 0 ? id : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(parts.Last().ToLowerInvariant());
        }
        return AppNames[id] = name;
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return t.ToString("MMM d", En);
    }
}
