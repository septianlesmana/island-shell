using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ical.Net.CalendarComponents;
using IcalCalendar = Ical.Net.Calendar;
using Ical.Net.DataTypes;

namespace DynamicIsland;

/// Fantastical-style calendar widget: month view (left) + agenda (right),
/// data from Google Calendar secret iCal addresses.
public class CalendarWidget : UserControl
{
    public class CalSource
    {
        public string Url { get; set; } = "";
        public string Color { get; set; } = "#34C759";
    }

    public record Ev(DateTime Start, DateTime End, bool AllDay, string Title, Brush Color);

    /// Upcoming events (used by the "Up Next" widgets).
    public static IReadOnlyList<Ev> Upcoming { get; private set; } = Array.Empty<Ev>();

    static readonly CultureInfo En = new("en-US");
    static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "calendars.json");
    // Google Calendar colors: Peacock, Sage, Tangerine, Grape, Tomato, Banana, Blueberry
    static readonly string[] Palette = { "#039BE5", "#33B679", "#F4511E", "#8E24AA", "#D50000", "#F6BF26", "#3F51B5" };
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Google Calendar-style light theme: white card, dark text
    static readonly Brush White = Brushes.White;
    static readonly Brush Ink = Frozen(Color.FromRgb(0x20, 0x21, 0x24));       // primary text
    static readonly Brush Card = Brushes.White;                                // widget background
    static readonly Brush Gray = Frozen(Color.FromRgb(0x5F, 0x63, 0x68));
    static readonly Brush DimGray = Frozen(Color.FromRgb(0xB0, 0xB4, 0xBA));   // days of other months
    static readonly Brush Blue = Frozen(Color.FromRgb(0x4C, 0x8D, 0xF6));      // Google blue
    static readonly Brush Red = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    static readonly Brush Line = Frozen(Color.FromRgb(0xDA, 0xDC, 0xE0));
    static readonly Brush WeekRow = Frozen(Color.FromRgb(0xF1, 0xF3, 0xF4));
    // Day colors by number of events (1, 2, 3, 4+), like Fantastical's "heat map"
    // Day colors by number of events (1, 2, 3, 4+), using Google Calendar colors:
    // Peacock (blue), Sage (green), Tangerine (orange), Tomato (red)
    static readonly Brush[] Heat =
    {
        Frozen(Color.FromArgb(0xE6, 0x03, 0x9B, 0xE5)), Frozen(Color.FromArgb(0xE6, 0x33, 0xB6, 0x79)),
        Frozen(Color.FromArgb(0xE6, 0xF4, 0x51, 0x1E)), Frozen(Color.FromArgb(0xE6, 0xD5, 0x00, 0x00))
    };
    static readonly Brush TodayBrush = Frozen(Color.FromRgb(0x1A, 0x73, 0xE8));   // Google blue for today

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    readonly Grid _monthGrid = new();
    readonly TextBlock _monthText = new() { FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Ink };
    readonly TextBlock _yearText = new() { FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Red };
    readonly TextBlock _dayHeader = new() { FontSize = 15, FontWeight = FontWeights.Bold };
    readonly TextBlock _dayHeaderDate = new() { FontSize = 15, Margin = new Thickness(6, 0, 0, 0) };
    readonly TextBlock _status = new() { FontSize = 11, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _agenda = new();
    readonly Grid _main = new();
    readonly Border _setup;
    readonly TextBox _urlBox;
    readonly Button _cancelBtn;

    List<CalSource> _sources;
    readonly List<(IcalCalendar Cal, Brush Color)> _calendars = new();
    readonly object _calLock = new();
    List<Ev> _events = new();
    (DateTime From, DateTime To) _range;

    DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    DateTime _selected = DateTime.Today;
    DateTime _lastFetch = DateTime.MinValue;
    DateTime _renderedToday = DateTime.Today;
    bool _loading;

    public CalendarWidget()
    {
        // ----- Left: month calendar -----
        var left = new DockPanel();
        var nav = new Grid { Height = 30, Margin = new Thickness(0, 0, 0, 4) };
        nav.Children.Add(NavButton("", HorizontalAlignment.Left, () => ShowMonth(_month.AddMonths(-1))));
        nav.Children.Add(NavButton("", HorizontalAlignment.Right, () => ShowMonth(_month.AddMonths(1))));
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand,
            ToolTip = "Go to Today"
        };
        _yearText.Margin = new Thickness(6, 0, 0, 0);
        title.Children.Add(_monthText);
        title.Children.Add(_yearText);
        title.MouseLeftButtonUp += (_, _) => { _selected = DateTime.Today; ShowMonth(new DateTime(_selected.Year, _selected.Month, 1)); };
        nav.Children.Add(title);
        DockPanel.SetDock(nav, Dock.Top);
        left.Children.Add(nav);
        left.Children.Add(_monthGrid);

        // ----- Right: agenda -----
        var right = new Grid { Margin = new Thickness(20, 4, 4, 0) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition());
        var head = new DockPanel { Margin = new Thickness(0, 2, 0, 10) };
        DockPanel.SetDock(_status, Dock.Right);
        head.Children.Add(_status);
        var headText = new StackPanel { Orientation = Orientation.Horizontal };
        headText.Children.Add(_dayHeader);
        headText.Children.Add(_dayHeaderDate);
        head.Children.Add(headText);
        right.Children.Add(head);
        Grid.SetRow(_agenda, 1);
        right.Children.Add(_agenda);

        var add = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = TodayBrush,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.Hand, ToolTip = "New Event in Google Calendar",
            Child = new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
                Foreground = White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        add.MouseLeftButtonUp += (_, _) => Open(
            $"https://calendar.google.com/calendar/r/eventedit?dates={_selected:yyyyMMdd}T090000/{_selected:yyyyMMdd}T100000");
        Grid.SetRow(add, 1);
        right.Children.Add(add);

        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) });
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        _main.ColumnDefinitions.Add(new ColumnDefinition());
        _main.Children.Add(left);
        var divider = new Border { Background = Line, Margin = new Thickness(0, 6, 0, 6) };
        Grid.SetColumn(divider, 1);
        _main.Children.Add(divider);
        Grid.SetColumn(right, 2);
        _main.Children.Add(right);

        // ----- Settings panel (paste iCal addresses) -----
        _urlBox = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64, FontSize = 12,
            Background = Frozen(Color.FromRgb(0xF1, 0xF3, 0xF4)), Foreground = Ink, CaretBrush = Ink,
            BorderBrush = Line, Padding = new Thickness(6, 4, 6, 4),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _cancelBtn = PillButton("Cancel", false, () => ShowSetup(false));
        _setup = BuildSetup();

        var root = new Grid();
        root.Children.Add(_main);
        root.Children.Add(_setup);
        // Rounded white card (like Google Calendar)
        Content = new Border
        {
            Background = Card, CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 8, 12, 10),
            Child = root
        };

        _sources = LoadConfig();
        ShowMonth(_month);
        if (_sources.Count == 0) ShowSetup(true);
        else _ = RefreshAsync();
    }

    // ================= Called from the notch =================

    /// Notch opened: reload data if stale, and jump to today if the date changed.
    public void OnOpened()
    {
        if (DateTime.Today != _renderedToday)
        {
            _renderedToday = DateTime.Today;
            _selected = DateTime.Today;
            ShowMonth(new DateTime(_selected.Year, _selected.Month, 1));
        }
        else RenderAgenda();
        if (_sources.Count > 0 && DateTime.Now - _lastFetch > TimeSpan.FromMinutes(10)) _ = RefreshAsync();
    }

    public void OpenSetup() => ShowSetup(true);

    public async Task RefreshAsync()
    {
        if (_loading || _sources.Count == 0) return;
        _loading = true;
        _status.Text = "Loading…";
        try
        {
            var loaded = new List<(IcalCalendar, Brush)>();
            string? error = null;
            foreach (var src in _sources)
            {
                try
                {
                    var url = src.Url.Trim();
                    if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
                    var text = await Http.GetStringAsync(url);
                    var cal = await Task.Run(() => IcalCalendar.Load(text));
                    var brush = (Brush)new BrushConverter().ConvertFromString(src.Color)!;
                    brush.Freeze();
                    loaded.Add((cal, brush));
                }
                catch (Exception ex) { error = ex.Message; }
            }

            lock (_calLock)
            {
                _calendars.Clear();
                _calendars.AddRange(loaded);
            }
            _lastFetch = DateTime.Now;
            _range = default;
            await LoadEventsAsync();
            _status.Text = error == null ? "" : "Some calendars failed to load";
            _status.ToolTip = error;
        }
        finally { _loading = false; }
    }

    // ================= Data =================

    async Task LoadEventsAsync()
    {
        // Range: one week before the visible month to 6 weeks after, plus 2 weeks of agenda from the selected date
        var from = _month.AddDays(-7);
        var to = new[] { _month.AddDays(49), _selected.AddDays(15) }.Max();
        if (_range.From <= from && _range.To >= to && _range != default) { Render(); return; }

        var events = await Task.Run(() => Expand(from, to));
        _events = events;
        var now = DateTime.Now;
        var upcoming = await Task.Run(() => Expand(now.Date, now.Date.AddDays(8)));
        Upcoming = upcoming.Where(e => e.End > now).ToList();
        _range = (from, to);
        Render();
    }

    List<Ev> Expand(DateTime from, DateTime to)
    {
        var list = new List<Ev>();
        lock (_calLock)
        {
            foreach (var (cal, color) in _calendars)
            {
                try
                {
                    foreach (var o in cal.GetOccurrences(new CalDateTime(from), new CalDateTime(to)))
                    {
                        if (o.Source is not CalendarEvent ev) continue;
                        if (string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;

                        bool allDay = ev.IsAllDay;
                        DateTime start = allDay ? o.Period.StartTime.Value.Date : o.Period.StartTime.AsSystemLocal;
                        DateTime end = o.Period.EndTime == null ? start
                                     : allDay ? o.Period.EndTime.Value.Date : o.Period.EndTime.AsSystemLocal;
                        if (allDay && end <= start) end = start.AddDays(1);

                        string title = string.IsNullOrWhiteSpace(ev.Summary) ? "(No title)" : ev.Summary.Trim();
                        list.Add(new Ev(start, end, allDay, title, color));
                    }
                }
                catch { /* skip broken calendars */ }
            }
        }
        return list.OrderBy(e => e.Start).ThenBy(e => e.AllDay ? 0 : 1).ToList();
    }

    IEnumerable<Ev> EventsOn(DateTime day) =>
        _events.Where(e => e.Start < day.AddDays(1) && e.End > day);

    // ================= View =================

    void ShowMonth(DateTime month)
    {
        _month = new DateTime(month.Year, month.Month, 1);
        if (_selected.Year != _month.Year || _selected.Month != _month.Month)
            _selected = _month.Year == DateTime.Today.Year && _month.Month == DateTime.Today.Month ? DateTime.Today : _month;
        Render();
        _ = LoadEventsAsync();
    }

    void Render()
    {
        RenderMonth();
        RenderAgenda();
    }

    void RenderMonth()
    {
        _monthText.Text = En.TextInfo.ToTitleCase(_month.ToString("MMMM", En));
        _yearText.Text = _month.Year.ToString();

        var g = _monthGrid;
        g.Children.Clear();
        g.RowDefinitions.Clear();
        g.ColumnDefinitions.Clear();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        for (int c = 0; c < 7; c++) g.ColumnDefinitions.Add(new ColumnDefinition());
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        for (int r = 0; r < 6; r++) g.RowDefinitions.Add(new RowDefinition());

        var today = DateTime.Today;
        // Weeks start on Monday
        int offset = ((int)_month.DayOfWeek + 6) % 7;
        var first = _month.AddDays(-offset);

        // Column headers: week number + day names
        Add(g, Label("CW", 10, Gray), 0, 0);
        string[] days = { "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN" };
        int todayCol = ((int)today.DayOfWeek + 6) % 7;
        for (int c = 0; c < 7; c++)
            Add(g, Label(days[c], 10.5, c == todayCol ? TodayBrush : Gray), 0, c + 1);

        // Week-number column divider
        var vline = new Border { Width = 1, Background = Line, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRowSpan(vline, 7);
        g.Children.Add(vline);

        for (int r = 0; r < 6; r++)
        {
            var weekStart = first.AddDays(r * 7);
            bool thisWeek = today >= weekStart && today < weekStart.AddDays(7);
            if (thisWeek)
            {
                var band = new Border { Background = WeekRow, CornerRadius = new CornerRadius(8) };
                Grid.SetRow(band, r + 1);
                Grid.SetColumnSpan(band, 8);
                g.Children.Add(band);
            }
            int week = ISOWeek.GetWeekOfYear(weekStart);
            Add(g, Label(week.ToString(), 11, thisWeek ? TodayBrush : Gray), r + 1, 0);

            for (int c = 0; c < 7; c++)
            {
                var day = weekStart.AddDays(c);
                Add(g, DayCell(day), r + 1, c + 1);
            }
        }
    }

    FrameworkElement DayCell(DateTime day)
    {
        var today = DateTime.Today;
        bool inMonth = day.Month == _month.Month;
        bool isToday = day == today;
        bool isSelected = day == _selected;
        int count = inMonth ? EventsOn(day).Count() : 0;

        Brush bg = Brushes.Transparent;
        if (isToday) bg = TodayBrush;
        else if (count > 0) bg = Heat[Math.Min(count, 4) - 1];

        // Larger, bolder numbers so they stay readable on the glass background
        var text = new TextBlock
        {
            Text = day.Day.ToString(), FontSize = 15,
            FontWeight = isToday || count > 0 ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = isToday || count > 0 ? White : inMonth ? Ink : DimGray,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var cell = new Border
        {
            Background = bg, CornerRadius = new CornerRadius(8), Margin = new Thickness(2),
            BorderBrush = isSelected && !isToday ? TodayBrush : null,
            BorderThickness = new Thickness(isSelected && !isToday ? 1.5 : 0),
            Child = text, Cursor = Cursors.Hand,
            ToolTip = count > 0 ? count == 1 ? "1 event" : $"{count} events" : null
        };
        cell.MouseLeftButtonUp += (_, _) =>
        {
            _selected = day;
            if (day.Month != _month.Month) ShowMonth(new DateTime(day.Year, day.Month, 1));
            else { Render(); _ = LoadEventsAsync(); }
        };
        return cell;
    }

    void RenderAgenda()
    {
        var today = DateTime.Today;
        bool isToday = _selected == today;
        _dayHeader.Text = isToday ? "TODAY" : _selected.ToString("dddd", En).ToUpper(En);
        _dayHeader.Foreground = isToday ? TodayBrush : Ink;
        _dayHeaderDate.Text = _selected.ToString("M/d/yy", En);
        _dayHeaderDate.Foreground = isToday ? TodayBrush : Gray;

        _agenda.Children.Clear();
        if (_sources.Count == 0) return;

        // Only events on the selected day (no agenda for other dates)
        var selectedEnd = _selected.AddDays(1);
        var items = _events.Where(e => e.Start < selectedEnd && e.End > _selected).ToList();
        if (items.Count == 0)
        {
            _agenda.Children.Add(Label(_loading ? "Loading events…" : isToday ? "No events today" : "No events",
                                       13, Gray, HorizontalAlignment.Left));
            return;
        }

        const int maxRows = 7;
        int shown = 0;
        // All-day events on the selected day: colored labels on top
        var pills = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var e in items.Where(e => e.AllDay && e.Start <= _selected && e.End > _selected))
        {
            pills.Children.Add(new Border
            {
                Background = e.Color, CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 2, 8, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock { Text = e.Title, Foreground = White, FontSize = 13, FontWeight = FontWeights.SemiBold }
            });
            shown++;
        }
        if (pills.Children.Count > 0) _agenda.Children.Add(pills);

        var rest = items.Where(e => !(e.AllDay && e.Start <= _selected && e.End > _selected)).ToList();
        int rows = 0;
        foreach (var e in rest)
        {
            if (rows >= maxRows) break;
            _agenda.Children.Add(EventRow(e, e.Start >= selectedEnd || e.Start < _selected));
            rows++;
            shown++;
        }

        int more = items.Count - shown;
        if (more > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(new Border
            {
                Width = 4, Height = 16, CornerRadius = new CornerRadius(2), Margin = new Thickness(4, 0, 12, 0),
                Background = new LinearGradientBrush(new GradientStopCollection
                {
                    new(Color.FromRgb(0xFF, 0x45, 0x3A), 0), new(Color.FromRgb(0xFF, 0xD6, 0x0A), 0.5),
                    new(Color.FromRgb(0x34, 0xC7, 0x59), 1)
                }, 90)
            });
            row.Children.Add(new TextBlock { Text = more == 1 ? "1 more item" : $"{more} more items", Foreground = Ink, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
            _agenda.Children.Add(row);
        }
    }

    FrameworkElement EventRow(Ev e, bool showDate)
    {
        var now = DateTime.Now;
        bool past = e.End <= now;
        bool ongoing = e.Start <= now && e.End > now && !e.AllDay;

        string when = e.AllDay ? "All day"
                    : ongoing ? $"Now · until {e.End.ToString("h:mm tt", En)}"
                    : $"{e.Start.ToString("h:mm tt", En)} – {e.End.ToString("h:mm tt", En)}";
        if (showDate) when = $"{e.Start.ToString("MMM d", En)}, {when}";

        var box = new Border
        {
            Width = 13, Height = 13, CornerRadius = new CornerRadius(3), BorderBrush = e.Color,
            BorderThickness = new Thickness(1.6), Margin = new Thickness(0, 3, 12, 0), VerticalAlignment = VerticalAlignment.Top
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = when, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = ongoing ? Red : Gray
        });
        text.Children.Add(new TextBlock
        {
            Text = e.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Ink,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var row = new DockPanel
        {
            Margin = new Thickness(0, 0, 40, 7), Opacity = past ? 0.45 : 1,
            Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Open in Google Calendar"
        };
        row.Children.Add(box);
        row.Children.Add(text);
        row.MouseLeftButtonUp += (_, _) =>
            Open($"https://calendar.google.com/calendar/r/day/{e.Start.Year}/{e.Start.Month}/{e.Start.Day}");
        return row;
    }

    // ================= Settings =================

    Border BuildSetup()
    {
        var steps = new TextBlock
        {
            Foreground = Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10),
            Text = "1. Open Google Calendar Settings (button below).\n" +
                   "2. On the left, pick your calendar under \"Settings for my calendars\".\n" +
                   "3. Scroll to \"Integrate calendar\" and copy \"Secret address in iCal format\".\n" +
                   "4. Paste it here. You can add several calendars, one address per line."
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(PillButton("Save", true, SaveSetup));
        buttons.Children.Add(_cancelBtn);
        buttons.Children.Add(PillButton("Open Google Calendar Settings", false,
            () => Open("https://calendar.google.com/calendar/r/settings")));

        var stack = new StackPanel { MaxWidth = 620, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = "Connect Google Calendar", Foreground = Ink, FontSize = 17, FontWeight = FontWeights.Bold });
        stack.Children.Add(steps);
        stack.Children.Add(_urlBox);
        stack.Children.Add(new TextBlock
        {
            Text = "This address is private: anyone who has it can see your events. It is stored only on this PC.",
            Foreground = DimGray, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(buttons);

        return new Border
        {
            Background = Card, Child = stack, Visibility = Visibility.Collapsed, Padding = new Thickness(10)
        };
    }

    void ShowSetup(bool show)
    {
        if (show) _urlBox.Text = string.Join(Environment.NewLine, _sources.Select(s => s.Url));
        _cancelBtn.Visibility = _sources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _setup.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _main.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        if (show) _urlBox.Focus();
    }

    void SaveSetup()
    {
        var urls = _urlBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                               .Where(u => u.Contains("://")).Distinct().ToList();
        _sources = urls.Select((u, i) =>
                       _sources.FirstOrDefault(s => s.Url == u) ?? new CalSource { Url = u, Color = Palette[i % Palette.Length] })
                       .ToList();
        SaveConfig();
        ShowSetup(false);
        if (_sources.Count == 0) { ShowSetup(true); return; }
        _ = RefreshAsync();
    }

    static List<CalSource> LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<List<CalSource>>(File.ReadAllText(ConfigPath)) ?? new();
        }
        catch { }
        return new();
    }

    void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_sources, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ================= Helpers =================

    static void Add(Grid g, UIElement el, int row, int col)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        g.Children.Add(el);
    }

    static TextBlock Label(string text, double size, Brush color, HorizontalAlignment align = HorizontalAlignment.Center) => new()
    {
        Text = text, FontSize = size, Foreground = color, FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
    };

    static Border NavButton(string glyph, HorizontalAlignment align, Action click)
    {
        var b = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Brushes.Transparent,
            HorizontalAlignment = align, Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12,
                Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        b.MouseEnter += (_, _) => b.Background = Frozen(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    static Button PillButton(string text, bool primary, Action click)
    {
        var b = new Button
        {
            Content = text, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, Focusable = false,
            Template = PillTemplate(primary ? TodayBrush : Frozen(Color.FromRgb(0x5F, 0x63, 0x68)))
        };
        b.Click += (_, _) => click();
        return b;
    }

    static ControlTemplate PillTemplate(Brush bg)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, bg);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.PaddingProperty, new Thickness(14, 5, 14, 6));
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(TextElement.ForegroundProperty, Brushes.White);
        text.SetValue(TextElement.FontSizeProperty, 12.5);
        border.AppendChild(text);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
