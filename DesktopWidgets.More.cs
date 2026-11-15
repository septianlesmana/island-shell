using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DynamicIsland;

/// Additional widgets for the new layout: big center clock, Up Next, Focus Timer, Deadlines,
/// Quick Links and Weather.
public partial class DesktopWidgets
{
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly FontFamily SymbolFont = new("Segoe UI Symbol");

    // ================= Big glass clock in the center =================
    // Hours & minutes stacked ("03" above "38"). Digits are filled with blurred wallpaper (glass) + white sheen,
    // with a white outline & soft shadow so they read on light and dark wallpapers.

    readonly Grid _bigClock = new() { IsHitTestVisible = false };
    readonly System.Windows.Shapes.Path _clockGlass = new(), _clockFace = new();
    readonly TextBlock _clockDate = new();
    string _clockShown = "";

    void BuildBigClock()
    {
        _clockDate.FontFamily = UiFont;
        _clockDate.FontSize = 24;
        _clockDate.FontWeight = FontWeights.SemiBold;
        _clockDate.Foreground = Brushes.White;
        _clockDate.HorizontalAlignment = HorizontalAlignment.Center;
        _clockDate.Margin = new Thickness(0, 0, 0, 10);
        _clockDate.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 1, Direction = 270, Opacity = 0.5, Color = Colors.Black };

        _clockFace.Stroke = Ui.Brush(0xF2, 0xFF, 0xFF, 0xFF);
        _clockFace.StrokeThickness = 2.2;
        _clockFace.StrokeLineJoin = PenLineJoin.Round;
        _clockFace.Fill = new LinearGradientBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 90);

        var digits = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 6, Direction = 270, Opacity = 0.28, Color = Color.FromRgb(0x10, 0x10, 0x30) }
        };
        digits.Children.Add(_clockGlass);
        digits.Children.Add(_clockFace);

        var sp = new StackPanel();
        sp.Children.Add(_clockDate);
        sp.Children.Add(digits);
        _bigClock.Children.Add(sp);
        _bigClock.SizeChanged += (_, _) => PlaceBigClock();
        _canvas.Children.Insert(0, _bigClock);   // bottom-most: cards always sit above the clock
    }

    void UpdateBigClock(DateTime now)
    {
        _clockDate.Text = now.ToString("dddd, MMMM d", En);
        var text = now.ToString("hh", En) + now.ToString("mm", En);   // 4 digits, e.g. 03 | 38
        if (text == _clockShown) return;
        _clockShown = text;

        double size = Math.Clamp(SystemParameters.WorkArea.Height * 0.21, 140, 280);
        var typeface = new Typeface(UiFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Geometry Line(string t) =>
            new FormattedText(t, En, FlowDirection.LeftToRight, typeface, size, Brushes.White, dip).BuildGeometry(new Point(0, 0));

        var g1 = Line(text[..2]);
        var g2 = Line(text[2..]);
        Rect b1 = g1.Bounds, b2 = g2.Bounds;
        double w = Math.Max(b1.Width, b2.Width), gap = size * 0.09;
        g1.Transform = new TranslateTransform((w - b1.Width) / 2 - b1.X, -b1.Y);
        g2.Transform = new TranslateTransform((w - b2.Width) / 2 - b2.X, b1.Height + gap - b2.Y);
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        group.Children.Add(g1);
        group.Children.Add(g2);
        group.Freeze();
        _clockGlass.Data = group;
        _clockFace.Data = group;
        Dispatcher.BeginInvoke(new Action(UpdateClockGlass), DispatcherPriority.Loaded);
    }

    void PlaceBigClock()
    {
        if (ActualWidth <= 0 || _bigClock.ActualWidth <= 0) return;
        Canvas.SetLeft(_bigClock, (ActualWidth - _bigClock.ActualWidth) / 2);
        Canvas.SetTop(_bigClock, Math.Max(0, (ActualHeight - _bigClock.ActualHeight) / 2 - ActualHeight * 0.04));
        Dispatcher.BeginInvoke(new Action(UpdateClockGlass), DispatcherPriority.Loaded);
    }

    /// Digit fill = the blurred wallpaper right behind it.
    void UpdateClockGlass()
    {
        if (_clockGlass.Data is not Geometry g || g.Bounds.IsEmpty) return;
        if (_blurredWall == null || !_clockGlass.IsVisible) { _clockGlass.Fill = Ui.Brush(0x40, 0xFF, 0xFF, 0xFF); return; }
        try
        {
            var b = g.Bounds;
            var o = _clockGlass.TranslatePoint(b.TopLeft, _canvas);
            var rect = new Rect(Left + o.X, Top + o.Y, Math.Max(1, b.Width), Math.Max(1, b.Height));
            _clockGlass.Fill = new DrawingBrush(_blurredWall) { Viewbox = rect, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        }
        catch { }
    }

    // ================= Up Next =================

    readonly StackPanel _upNextBody = new();

    (Border, FrameworkElement) UpNextCard()
    {
        var sp = new StackPanel();
        var header = Header("UP NEXT", Blue);
        sp.Children.Add(header);
        sp.Children.Add(_upNextBody);
        return (Card(sp, 300), header);
    }

    void RebuildUpNext()
    {
        _upNextBody.Children.Clear();
        var now = DateTime.Now;

        // Next event (or the one in progress)
        var ev = CalendarWidget.Upcoming.Where(e => !e.AllDay && e.End > now).OrderBy(e => e.Start).FirstOrDefault();
        if (ev != null)
            _upNextBody.Children.Add(UpRow(ev.Color, "", ev.Title,
                $"{ev.Start.ToString("h:mm tt", En)} – {ev.End.ToString("h:mm tt", En)}",
                ev.Start <= now ? "Now" : Until(ev.Start)));

        // Next prayer
        var p = NextPrayer(now);
        _upNextBody.Children.Add(UpRow(Green, "", p.Name, p.Time.ToString("HH:mm", En) + " · " + PrayerTimes.Current.Name, Until(p.Time)));

        // Nearest deadline (from Notes)
        var due = NotesStore.Notes.Where(n => n.Due != null && n.Status != NotesView.Status.Done)
                                  .OrderBy(n => n.Due).FirstOrDefault();
        if (due?.Due is DateTime d)
        {
            int days = (d.Date - DateTime.Today).Days;
            _upNextBody.Children.Add(UpRow(days <= 0 ? Red : days <= 2 ? Orange : ProgressBlue, "", due.Text,
                NotesView.DueLabel(d), DaysLabel(days)));
        }
        if (_upNextBody.Children.Count > 0 && _upNextBody.Children[_upNextBody.Children.Count - 1] is FrameworkElement last) last.Margin = new Thickness(0);
    }

    static PrayerTimes.Prayer NextPrayer(DateTime now)
    {
        var loc = PrayerTimes.Current;
        return PrayerTimes.For(now.Date, loc).FirstOrDefault(p => p.Time > now && p.Name is not ("Imsak" or "Sunrise"))
               ?? PrayerTimes.For(now.Date.AddDays(1), loc).First(p => p.Name == "Fajr");
    }

    static string Until(DateTime t)
    {
        var left = t - DateTime.Now;
        if (left.TotalMinutes < 1) return "Now";
        if (left.TotalHours < 1) return $"in {(int)Math.Ceiling(left.TotalMinutes)}m";
        if (t.Date == DateTime.Today || left.TotalHours < 12) return $"in {(int)left.TotalHours}h {left.Minutes}m";
        if (t.Date == DateTime.Today.AddDays(1)) return "Tomorrow";
        return t.ToString("ddd d", En);
    }

    static string DaysLabel(int days) => days switch
    {
        < 0 => "Overdue",
        0 => "Today",
        1 => "Tomorrow",
        _ => $"{days}d"
    };

    static UIElement UpRow(Brush color, string glyph, string title, string sub, string right)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
        var icon = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = color, Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = glyph, FontFamily = IconFont, FontSize = 14, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var r = Text(right, 12, FontWeights.Bold, color);
        r.VerticalAlignment = VerticalAlignment.Center;
        r.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(r, Dock.Right);
        row.Children.Add(r);
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(Text(title, 13, FontWeights.SemiBold));
        t.Children.Add(Text(sub, 11, FontWeights.Normal, Sub));
        row.Children.Add(t);
        return row;
    }

    // ================= Focus Timer (Pomodoro 25 / 5) =================

    enum FocusMode { Focus, Break }
    static readonly TimeSpan FocusLength = TimeSpan.FromMinutes(25), BreakLength = TimeSpan.FromMinutes(5), LongBreakLength = TimeSpan.FromMinutes(15);

    FocusMode _focusMode = FocusMode.Focus;
    TimeSpan _focusLeft = FocusLength, _focusTotal = FocusLength;
    DateTime _focusEnd;
    bool _focusRunning;
    int _focusSessions;
    DateTime _focusDay = DateTime.Today;
    readonly DispatcherTimer _focusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly TextBlock _focusTime = new(), _focusLabel = new(), _focusStartText = new();
    readonly ColumnDefinition _focusFill = new(), _focusRest = new();

    (Border, FrameworkElement) FocusCard()
    {
        var sp = new StackPanel();
        var header = Header("FOCUS TIMER", Red);
        sp.Children.Add(header);

        _focusTime.FontSize = 44;
        _focusTime.FontWeight = FontWeights.SemiBold;
        _focusTime.Foreground = Ink;
        Typography.SetNumeralAlignment(_focusTime, FontNumeralAlignment.Tabular);
        _focusTime.Margin = new Thickness(0, -6, 0, 0);
        sp.Children.Add(_focusTime);
        _focusLabel.FontSize = 12;
        _focusLabel.FontWeight = FontWeights.SemiBold;
        _focusLabel.Foreground = Sub;
        sp.Children.Add(_focusLabel);

        // Remaining-time bar
        var bar = new Grid { Height = 6, Margin = new Thickness(0, 10, 0, 12) };
        bar.ColumnDefinitions.Add(_focusFill);
        bar.ColumnDefinitions.Add(_focusRest);
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Ui.Brush(0x1A, 0x00, 0x00, 0x00) });
        Grid.SetColumnSpan(bar.Children[0], 2);
        var fill = new Border { CornerRadius = new CornerRadius(3), Background = Red };
        bar.Children.Add(fill);
        sp.Children.Add(bar);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Pill(_focusStartText, "Start", true, ToggleFocus));
        buttons.Children.Add(Pill(new TextBlock(), "Reset", false, ResetFocus));
        buttons.Children.Add(Pill(new TextBlock(), "Skip", false, () => CompleteFocus(skipped: true)));
        sp.Children.Add(buttons);

        _focusTimer.Tick += (_, _) =>
        {
            _focusLeft = _focusEnd - DateTime.Now;
            if (_focusLeft <= TimeSpan.Zero) CompleteFocus(skipped: false);
            else if (!DesktopCovered()) UpdateFocus();   // time keeps running; the display only updates when visible
        };
        return (Card(sp, 300), header);
    }

    static Border Pill(TextBlock label, string text, bool primary, Action click)
    {
        label.Text = text;
        label.FontSize = 12.5;
        label.FontWeight = FontWeights.SemiBold;
        label.Foreground = primary ? Brushes.White : Ink;
        var normal = primary ? Red : Ui.Brush(0x1F, 0x00, 0x00, 0x00);
        var hover = primary ? Ui.Brush(0xFF, 0xB8, 0x26, 0x1C) : Ui.Brush(0x33, 0x00, 0x00, 0x00);
        var b = new Border
        {
            Child = label, Background = normal, CornerRadius = new CornerRadius(13), Padding = new Thickness(14, 4, 14, 5),
            Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand
        };
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = normal;
        b.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        return b;
    }

    void ToggleFocus()
    {
        if (_focusRunning)
        {
            _focusLeft = _focusEnd - DateTime.Now;
            _focusRunning = false;
            _focusTimer.Stop();
        }
        else
        {
            _focusEnd = DateTime.Now + _focusLeft;
            _focusRunning = true;
            _focusTimer.Start();
        }
        UpdateFocus();
    }

    void ResetFocus()
    {
        _focusRunning = false;
        _focusTimer.Stop();
        _focusLeft = _focusTotal;
        UpdateFocus();
    }

    void CompleteFocus(bool skipped)
    {
        _focusRunning = false;
        _focusTimer.Stop();
        if (_focusDay != DateTime.Today) { _focusDay = DateTime.Today; _focusSessions = 0; }
        string message;
        if (_focusMode == FocusMode.Focus)
        {
            if (!skipped) _focusSessions++;
            _focusMode = FocusMode.Break;
            _focusTotal = _focusSessions > 0 && _focusSessions % 4 == 0 ? LongBreakLength : BreakLength;
            message = $"Focus done — take a {(int)_focusTotal.TotalMinutes}-min break";
        }
        else
        {
            _focusMode = FocusMode.Focus;
            _focusTotal = FocusLength;
            message = "Break over — time to focus";
        }
        _focusLeft = _focusTotal;
        UpdateFocus();
        if (skipped) return;
        try { SystemSounds.Asterisk.Play(); } catch { }
        foreach (Window w in Application.Current.Windows)
            if (w is MainWindow island) island.Notify("", message, Red);
    }

    void UpdateFocus()
    {
        var left = _focusLeft < TimeSpan.Zero ? TimeSpan.Zero : _focusLeft;
        _focusTime.Text = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
        if (_focusDay != DateTime.Today) { _focusDay = DateTime.Today; _focusSessions = 0; }
        string done = _focusSessions == 1 ? "1 session today" : $"{_focusSessions} sessions today";
        _focusLabel.Text = (_focusMode == FocusMode.Focus ? "Focus" : "Break") + (_focusRunning ? "" : _focusLeft == _focusTotal ? " · ready" : " · paused") + " · " + done;
        _focusStartText.Text = _focusRunning ? "Pause" : _focusLeft == _focusTotal ? "Start" : "Resume";
        double p = _focusTotal.TotalSeconds > 0 ? Math.Clamp(left.TotalSeconds / _focusTotal.TotalSeconds, 0, 1) : 0;
        _focusFill.Width = new GridLength(Math.Max(p, 0.0001), GridUnitType.Star);
        _focusRest.Width = new GridLength(Math.Max(1 - p, 0.0001), GridUnitType.Star);
    }

    // ================= Deadlines (from Notes with a due date) =================

    readonly StackPanel _deadlinesBody = new();

    (Border, FrameworkElement) DeadlinesCard()
    {
        var sp = new StackPanel();
        var header = Header("DEADLINES", Red);
        sp.Children.Add(header);
        sp.Children.Add(_deadlinesBody);
        return (Card(sp, 300), header);
    }

    void RebuildDeadlines()
    {
        _deadlinesBody.Children.Clear();
        var list = NotesStore.Notes.Where(n => n.Due != null && n.Status != NotesView.Status.Done)
                                   .OrderBy(n => n.Due).ToList();
        if (list.Count == 0)
        {
            _deadlinesBody.Children.Add(Text("No deadlines", 13, FontWeights.SemiBold, Sub));
            _deadlinesBody.Children.Add(Text("Set a due date in Notes (calendar icon or “+ Due date”)", 11.5, FontWeights.Normal, Faint, new Thickness(0, 2, 0, 0)));
            return;
        }
        foreach (var n in list.Take(6))
        {
            var due = n.Due!.Value;
            int days = (due.Date - DateTime.Today).Days;
            var color = days < 0 ? Red : days <= 2 ? Orange : Sub;
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

            var right = Text(DaysLabel(days), 12, FontWeights.Bold, days <= 2 ? color : Ink);
            right.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(right, Dock.Right);
            var texts = new StackPanel();
            texts.Children.Add(Text(n.Text, 13, FontWeights.SemiBold));
            texts.Children.Add(Text(due.ToString("dddd, d MMM", En), 11, FontWeights.Normal, Sub));
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            row.Children.Add(check);
            row.Children.Add(right);
            row.Children.Add(texts);
            _deadlinesBody.Children.Add(row);
        }
        if (list.Count > 6) _deadlinesBody.Children.Add(Text($"+{list.Count - 6} more", 11.5, FontWeights.SemiBold, Sub));
    }

    // ================= Quick Links =================

    public record QuickLink(string Name, string Url);

    static readonly string LinksPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "quicklinks.json");

    static readonly QuickLink[] DefaultLinks =
    {
        new("GitHub", "https://github.com"),
        new("Gmail", "https://mail.google.com"),
        new("Google Drive", "https://drive.google.com"),
        new("Calendar", "https://calendar.google.com"),
        new("YouTube", "https://www.youtube.com"),
        new("Microsoft Learn", "https://learn.microsoft.com"),
    };

    static readonly Color[] TileColors =
    {
        Color.FromRgb(0x1A, 0x73, 0xE8), Color.FromRgb(0x0F, 0x6C, 0xBD), Color.FromRgb(0x7B, 0x3F, 0xE4), Color.FromRgb(0x1E, 0x8E, 0x3E),
        Color.FromRgb(0x25, 0xA2, 0x5A), Color.FromRgb(0xD9, 0x30, 0x25), Color.FromRgb(0xC9, 0x6A, 0x00), Color.FromRgb(0x00, 0x89, 0x7B)
    };

    readonly UniformGrid _linksBody = new() { Columns = 4 };

    (Border, FrameworkElement) QuickLinksCard()
    {
        var sp = new StackPanel();
        var header = Header("QUICK LINKS", Blue);
        sp.Children.Add(header);
        sp.Children.Add(_linksBody);
        var card = Card(sp, 300);
        card.ToolTip = "Right-click to edit links";
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Edit links… (opens quicklinks.json)" };
        edit.Click += (_, _) =>
        {
            LoadLinks();   // make sure the file exists
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{LinksPath}\"") { UseShellExecute = true }); } catch { }
        };
        var reload = new MenuItem { Header = "Reload links" };
        reload.Click += (_, _) => RebuildQuickLinks();
        menu.Items.Add(edit);
        menu.Items.Add(reload);
        card.ContextMenu = menu;
        return (card, header);
    }

    static List<QuickLink> LoadLinks()
    {
        try
        {
            if (File.Exists(LinksPath))
            {
                var list = JsonSerializer.Deserialize<List<QuickLink>>(File.ReadAllText(LinksPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (list != null) return list.Where(l => !string.IsNullOrWhiteSpace(l.Url)).ToList();
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LinksPath)!);
            File.WriteAllText(LinksPath, JsonSerializer.Serialize(DefaultLinks, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        return DefaultLinks.ToList();
    }

    void RebuildQuickLinks()
    {
        _linksBody.Children.Clear();
        int i = 0;
        foreach (var link in LoadLinks().Take(12))
        {
            var name = string.IsNullOrWhiteSpace(link.Name) ? link.Url : link.Name;
            var initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
            var tile = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = link.Url };
            var icon = new Border
            {
                Width = 42, Height = 42, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(TileColors[i++ % TileColors.Length]),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = initials, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            tile.Children.Add(icon);
            var label = Text(name, 10.5, FontWeights.SemiBold, Sub, new Thickness(2, 4, 2, 0));
            label.TextAlignment = TextAlignment.Center;
            tile.Children.Add(label);
            tile.MouseEnter += (_, _) => icon.Opacity = 0.85;
            tile.MouseLeave += (_, _) => icon.Opacity = 1;
            var url = link.Url;
            tile.MouseLeftButtonUp += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                e.Handled = true;
            };
            _linksBody.Children.Add(tile);
        }
    }

    // ================= Weather (Open-Meteo, no API key) =================

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    readonly TextBlock _wCity = new(), _wIcon = new(), _wTemp = new(), _wDesc = new(), _wRange = new();
    readonly UniformGrid _wHours = new() { Columns = 5, Margin = new Thickness(0, 10, 0, 0) };

    (Border, FrameworkElement) WeatherCard()
    {
        var sp = new StackPanel();
        var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 4) };
        _wCity.FontSize = 11; _wCity.FontWeight = FontWeights.SemiBold; _wCity.Foreground = Sub;
        DockPanel.SetDock(_wCity, Dock.Right);
        head.Children.Add(_wCity);
        var title = Header("WEATHER", Blue);
        title.Margin = new Thickness(0);
        head.Children.Add(title);
        sp.Children.Add(head);

        var main = new DockPanel();
        _wIcon.FontFamily = SymbolFont; _wIcon.FontSize = 38; _wIcon.Foreground = Orange;
        _wIcon.Margin = new Thickness(0, 0, 10, 0); _wIcon.VerticalAlignment = VerticalAlignment.Center;
        main.Children.Add(_wIcon);
        _wTemp.FontSize = 40; _wTemp.FontWeight = FontWeights.Light; _wTemp.Foreground = Ink;
        _wTemp.Margin = new Thickness(0, 0, 12, 0); _wTemp.VerticalAlignment = VerticalAlignment.Center;
        main.Children.Add(_wTemp);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _wDesc.FontSize = 13; _wDesc.FontWeight = FontWeights.SemiBold; _wDesc.Foreground = Ink; _wDesc.TextTrimming = TextTrimming.CharacterEllipsis;
        _wRange.FontSize = 11; _wRange.Foreground = Sub; _wRange.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(_wDesc);
        info.Children.Add(_wRange);
        main.Children.Add(info);
        sp.Children.Add(main);
        sp.Children.Add(_wHours);

        _wDesc.Text = "Loading…";
        return (Card(sp, 300), head);
    }

    void StartWeather()
    {
        _weatherTimer.Tick += async (_, _) => await RefreshWeather();
        _weatherTimer.Start();
        PrayerTimes.LocationChanged += () => Dispatcher.BeginInvoke(new Action(async () => await RefreshWeather()));
        _ = RefreshWeather();
    }

    async Task RefreshWeather()
    {
        if (Perf.Fullscreen && !string.IsNullOrEmpty(_wTemp.Text)) return;   // fullscreen app running: postpone
        var loc = PrayerTimes.Current;
        _wCity.Text = loc.Name;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var url = "https://api.open-meteo.com/v1/forecast?latitude=" + loc.Lat.ToString("0.####", inv) +
                      "&longitude=" + loc.Lng.ToString("0.####", inv) +
                      "&current=temperature_2m,weather_code,is_day" +
                      "&hourly=temperature_2m,weather_code,precipitation_probability" +
                      "&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=2";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var cur = root.GetProperty("current");
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            int code = cur.GetProperty("weather_code").GetInt32();
            bool isDay = !cur.TryGetProperty("is_day", out var dayEl) || dayEl.GetInt32() == 1;
            var daily = root.GetProperty("daily");
            double hi = daily.GetProperty("temperature_2m_max")[0].GetDouble();
            double lo = daily.GetProperty("temperature_2m_min")[0].GetDouble();

            var hourly = root.GetProperty("hourly");
            var times = hourly.GetProperty("time").EnumerateArray().Select(t => DateTime.Parse(t.GetString()!, inv)).ToList();
            var temps = hourly.GetProperty("temperature_2m");
            var codes = hourly.GetProperty("weather_code");
            var rain = hourly.GetProperty("precipitation_probability");
            var hourNow = DateTime.Now.Date.AddHours(DateTime.Now.Hour);
            int start = Math.Max(0, times.FindIndex(t => t >= hourNow));
            int Rain(int i) => rain[i].ValueKind == JsonValueKind.Number ? rain[i].GetInt32() : 0;
            int rainSoon = Enumerable.Range(start, Math.Min(3, times.Count - start)).Select(Rain).DefaultIfEmpty(0).Max();

            var (icon, desc, color) = WeatherInfo(code, isDay);
            _wIcon.Text = icon;
            _wIcon.Foreground = color;
            _wTemp.Text = $"{Math.Round(temp)}°";
            _wDesc.Text = desc;
            _wRange.Text = $"H {Math.Round(hi)}°  L {Math.Round(lo)}°  ·  Rain {rainSoon}% next 3h";

            _wHours.Children.Clear();
            for (int i = start + 1; i <= start + 5 && i < times.Count; i++)
            {
                var (hIcon, _, hColor) = WeatherInfo(codes[i].GetInt32(), times[i].Hour is >= 6 and < 18);
                var col = new StackPanel();
                col.Children.Add(Center(Text(times[i].ToString("HH:mm", En), 10.5, FontWeights.SemiBold, Sub)));
                var ic = Center(Text(hIcon, 16, FontWeights.Normal, hColor, new Thickness(0, 2, 0, 1)));
                ic.FontFamily = SymbolFont;
                col.Children.Add(ic);
                col.Children.Add(Center(Text($"{Math.Round(temps[i].GetDouble())}°", 12, FontWeights.SemiBold)));
                int r = Rain(i);
                col.Children.Add(Center(Text(r >= 20 ? $"{r}%" : " ", 10, FontWeights.SemiBold, ProgressBlue)));
                _wHours.Children.Add(col);
            }
        }
        catch
        {
            if (string.IsNullOrEmpty(_wTemp.Text)) _wDesc.Text = "Weather unavailable (offline?)";
        }
    }

    static TextBlock Center(TextBlock t) { t.HorizontalAlignment = HorizontalAlignment.Center; return t; }

    /// WMO weather code → symbol, description, color.
    static (string Icon, string Desc, Brush Color) WeatherInfo(int code, bool day) => code switch
    {
        0 => (day ? "☀" : "☾", day ? "Clear" : "Clear night", day ? Orange : ProgressBlue),
        1 or 2 => ("⛅", code == 1 ? "Mostly clear" : "Partly cloudy", Orange),
        3 => ("☁", "Cloudy", Sub),
        45 or 48 => ("☁", "Fog", Sub),
        >= 51 and <= 57 => ("☂", "Drizzle", ProgressBlue),
        >= 61 and <= 67 or >= 80 and <= 82 => ("☔", code >= 80 ? "Rain showers" : "Rain", ProgressBlue),
        >= 71 and <= 77 or 85 or 86 => ("❄", "Snow", ProgressBlue),
        >= 95 => ("⛈", "Thunderstorm", Red),
        _ => ("☁", "Cloudy", Sub)
    };
}
