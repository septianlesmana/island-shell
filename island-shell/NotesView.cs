using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland;

/// "Notes" tab in the Dynamic Island: notes with To Do / In Progress / Waiting / Done status.
/// Notes are saved permanently in %AppData%\DynamicIsland\notes.json and are only removed when
/// deleted (✕ / Clear Done).
public class NotesView : UserControl
{
    public enum Status { Todo, Progress, Waiting, Done }

    public class Note
    {
        public string Text { get; set; } = "";
        public Status Status { get; set; } = Status.Todo;
        public DateTime Added { get; set; } = DateTime.Now;
        public DateTime? DoneAt { get; set; }
        public DateTime? Due { get; set; }   // due date (typed as "@25/11", "@today", "@tomorrow", "@fri")
    }

    static readonly System.Globalization.CultureInfo EnCulture = new("en-US");

    /// Extract a due date from text: "Essay @25/11" → ("Essay", 25 Nov). "@none" = remove the due date.
    /// Clear = true when the user typed "@none".
    public static (string Text, DateTime? Due, bool Clear) ParseDue(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text,
            @"(?:^|\s)@(?<d>today|tomorrow|tmr|none|mon|tue|wed|thu|fri|sat|sun|\d{1,2}[/.-]\d{1,2}(?:[/.-]\d{2,4})?)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return (text, null, false);
        var rest = (text.Remove(m.Index, m.Length)).Trim();
        if (rest.Length == 0) rest = text.Trim();
        var d = m.Groups["d"].Value.ToLowerInvariant();
        var today = DateTime.Today;
        DateTime? due = null;
        switch (d)
        {
            case "none": return (rest, null, true);
            case "today": due = today; break;
            case "tomorrow": case "tmr": due = today.AddDays(1); break;
            case "mon": case "tue": case "wed": case "thu": case "fri": case "sat": case "sun":
                var target = Array.IndexOf(new[] { "sun", "mon", "tue", "wed", "thu", "fri", "sat" }, d);
                int add = (target - (int)today.DayOfWeek + 7) % 7;
                due = today.AddDays(add == 0 ? 7 : add);
                break;
            default:
                var parts = d.Split('/', '.', '-');
                if (int.TryParse(parts[0], out var day) && int.TryParse(parts[1], out var month) && month is >= 1 and <= 12)
                {
                    int year = today.Year;
                    if (parts.Length > 2 && int.TryParse(parts[2], out var y)) year = y < 100 ? 2000 + y : y;
                    if (day >= 1 && day <= DateTime.DaysInMonth(year, month))
                    {
                        due = new DateTime(year, month, day);
                        if (parts.Length == 2 && due < today.AddDays(-31)) due = due.Value.AddYears(1);   // no year: the upcoming date
                    }
                }
                break;
        }
        if (due == null) return (text, null, false);
        return (rest, due, false);
    }

    /// "Today", "Tomorrow", "Fri 21 Nov", "Overdue · 12 Nov"
    public static string DueLabel(DateTime due)
    {
        var days = (due.Date - DateTime.Today).Days;
        return days switch
        {
            < 0 => $"Overdue · {due.ToString("d MMM", EnCulture)}",
            0 => "Due today",
            1 => "Due tomorrow",
            < 7 => $"Due {due.ToString("ddd d MMM", EnCulture)}",
            _ => $"Due {due.ToString("d MMM", EnCulture)}"
        };
    }

    static readonly string NotesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "notes.json");

    internal static List<Note> LoadNotes()
    {
        try
        {
            if (File.Exists(NotesPath))
                return JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(NotesPath)) ?? new();
        }
        catch { }
        return new();
    }

    internal static void SaveNotes(List<Note> notes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NotesPath)!);
            // Write to a temp file first, then replace → the file isn't corrupted if the PC dies while saving
            var tmp = NotesPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, NotesPath, overwrite: true);
        }
        catch { }
    }

    static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    // Dark theme (open notch = dark glass): white text
    static readonly Brush White = Brushes.White;
    static readonly Brush Ink = Brushes.White;                           // primary text
    static readonly Brush Sub = Ui.Brush(0x99, 0xFF, 0xFF, 0xFF);
    static readonly Brush Faint = Ui.Brush(0x80, 0xFF, 0xFF, 0xFF);
    static readonly Brush RowBg = Ui.Brush(0x17, 0xFF, 0xFF, 0xFF);
    static readonly Brush RowEdge = Ui.Brush(0x0F, 0xFF, 0xFF, 0xFF);
    static readonly Brush SegBg = Ui.Brush(0x40, 0x00, 0x00, 0x00);
    static readonly Brush Link = Ui.Brush(0xFF, 0x4C, 0x8D, 0xF6);

    static Brush ColorOf(Status s) => s switch
    {
        Status.Progress => Ui.Brush(0xFF, 0x0A, 0x84, 0xFF),
        Status.Waiting => Ui.Brush(0xFF, 0xFF, 0x9F, 0x0A),
        Status.Done => Ui.Brush(0xFF, 0x30, 0xD1, 0x58),
        _ => Ui.Brush(0xFF, 0x8E, 0x8E, 0x93)
    };
    static Brush ChipBgOf(Status s) => s switch
    {
        Status.Progress => Ui.Brush(0x33, 0x0A, 0x84, 0xFF),
        Status.Waiting => Ui.Brush(0x33, 0xFF, 0x9F, 0x0A),
        Status.Done => Ui.Brush(0x33, 0x30, 0xD1, 0x58),
        _ => Ui.Brush(0x1F, 0xFF, 0xFF, 0xFF)
    };
    static Brush ChipTextOf(Status s) => s switch
    {
        Status.Progress => Ui.Brush(0xFF, 0x64, 0xB5, 0xFF),
        Status.Waiting => Ui.Brush(0xFF, 0xFF, 0xC2, 0x66),
        Status.Done => Ui.Brush(0xFF, 0x6E, 0xE3, 0x8F),
        _ => Ui.Brush(0xB3, 0xFF, 0xFF, 0xFF)
    };
    static string NameOf(Status s) => s switch
    {
        Status.Progress => "In Progress", Status.Waiting => "Waiting", Status.Done => "Done", _ => "To Do"
    };

    // Shared data with the desktop Notes widget
    static List<Note> _notes => NotesStore.Notes;
    Status? _filter;   // null = All

    readonly StackPanel _filters = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 0, 8) };
    readonly Border _inputRow;
    readonly TextBox _input;
    readonly TextBlock _inputHint;
    readonly StackPanel _list = new();
    readonly TextBlock _empty;

    /// The notes changed (used by the "In Progress" badge).
    public event Action? Changed;

    public int InProgressCount => _notes.Count(n => n.Status == Status.Progress);

    public NotesView()
    {
        // Changed from the desktop widget → refresh the notch view too
        NotesStore.Changed += () => { if (!_selfNotify) Rebuild(); };
        // New note input box
        _input = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Ink, CaretBrush = Ink,
            FontFamily = UiFont, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0), Margin = new Thickness(10, 0, 8, 0)
        };
        _inputHint = new TextBlock
        {
            Text = "Type a note and press Enter…", Foreground = Sub, FontFamily = UiFont, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 1), IsHitTestVisible = false
        };
        _input.TextChanged += (_, _) => _inputHint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { AddNote(_input.Text); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideInput(); e.Handled = true; }
        };
        var keys = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
        // Due date field for new notes (optional)
        _newDueChip = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Set a due date (deadline)",
            Child = _newDueText
        };
        _newDueChip.MouseLeftButtonUp += (_, e) =>
        {
            ShowDuePicker(_newDueChip, _newDue, d => { _newDue = d; UpdateNewDueChip(); _input.Focus(); });
            e.Handled = true;
        };
        UpdateNewDueChip();
        keys.Children.Add(_newDueChip);
        keys.Children.Add(Kbd("Enter"));
        keys.Children.Add(Kbd("Esc"));
        var inputGrid = new Grid();
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.Children.Add(_inputHint);
        inputGrid.Children.Add(_input);
        Grid.SetColumn(keys, 1);
        inputGrid.Children.Add(keys);
        _inputRow = new Border
        {
            Height = 34, CornerRadius = new CornerRadius(10), Background = Ui.Brush(0x1F, 0xFF, 0xFF, 0xFF),
            BorderBrush = ColorOf(Status.Progress), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8),
            Child = inputGrid, Visibility = Visibility.Collapsed
        };

        _empty = new TextBlock
        {
            Text = "No notes yet. Click “+ New” to add one.", Foreground = Sub, FontFamily = UiFont, FontSize = 13,
            Margin = new Thickness(4, 6, 0, 0)
        };

        var footer = new DockPanel { Margin = new Thickness(2, 6, 2, 0) };
        var clearDone = new TextBlock
        {
            Text = "Clear Done", Foreground = Link, FontFamily = UiFont, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand, Background = Brushes.Transparent
        };
        clearDone.MouseLeftButtonUp += (_, _) => { _notes.RemoveAll(n => n.Status == Status.Done); Rebuild(); };
        DockPanel.SetDock(clearDone, Dock.Right);
        footer.Children.Add(clearDone);
        footer.Children.Add(new TextBlock
        {
            Text = "Saved automatically · removed only when you delete them (✕)", Foreground = Faint,
            FontFamily = UiFont, FontSize = 11
        });

        var root = new DockPanel();
        DockPanel.SetDock(_filters, Dock.Top);
        DockPanel.SetDock(_inputRow, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(_filters);
        root.Children.Add(_inputRow);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer
        {
            Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent
        });
        Content = root;
        Rebuild();
    }

    // ---------- Actions ----------

    /// "+ New" button: show the input box, ready to type.
    public void BeginNew()
    {
        _inputRow.Visibility = Visibility.Visible;
        _input.Text = "";
        Window.GetWindow(this)?.Activate();
        Dispatcher.BeginInvoke(() => { _input.Focus(); Keyboard.Focus(_input); },
                               System.Windows.Threading.DispatcherPriority.Input);
    }

    /// Typing a note (the notch must not close itself).
    public bool IsEditing => _inputRow.Visibility == Visibility.Visible && _input.IsKeyboardFocusWithin;

    // ---------- Edit ----------

    Note? _editing;
    bool _selfNotify;

    void BeginEdit(Note n)
    {
        _editing = n;
        Window.GetWindow(this)?.Activate();
        Rebuild();
    }

    void SaveEdit(Note n, string text)
    {
        _editing = null;
        text = text.Trim();
        if (text.Length > 0)   // empty text = cancel (keep the old text)
        {
            var (t, due, clear) = ParseDue(text);
            n.Text = t;
            if (due != null) n.Due = due;
            else if (clear) n.Due = null;
        }
        Rebuild();
    }

    /// Notch closing: an empty input box closes too.
    public void CancelEmptyInput()
    {
        if (_input.Text.Trim().Length == 0) HideInput();
    }

    void HideInput()
    {
        _input.Text = "";
        _newDue = null;
        UpdateNewDueChip();
        _inputRow.Visibility = Visibility.Collapsed;
    }

    void AddNote(string text)
    {
        text = text.Trim();
        if (text.Length == 0) { HideInput(); return; }
        var (t, due, _) = ParseDue(text);
        _notes.Insert(0, new Note { Text = t, Due = due ?? _newDue });
        _newDue = null;
        UpdateNewDueChip();
        _input.Text = "";   // the box stays open so you can add several notes in a row
        if (_filter is Status f && f != Status.Todo) _filter = null;
        Rebuild();
    }

    void SetStatus(Note n, Status s)
    {
        n.Status = s;
        n.DoneAt = s == Status.Done ? DateTime.Now : null;
        Rebuild();
    }

    // ---------- View ----------

    void Rebuild()
    {
        // Filters + counts
        _filters.Children.Clear();
        _filters.Children.Add(FilterChip("All", null, _notes.Count));
        foreach (var s in new[] { Status.Progress, Status.Waiting, Status.Done, Status.Todo })
            _filters.Children.Add(FilterChip(NameOf(s), s, _notes.Count(n => n.Status == s)));

        _list.Children.Clear();
        var shown = _notes.Where(n => _filter == null || n.Status == _filter).ToList();
        if (shown.Count == 0)
        {
            _empty.Text = _notes.Count == 0 ? "No notes yet. Click “+ New” to add one." : "Nothing here.";
            _list.Children.Add(_empty);
        }
        foreach (var n in shown) _list.Children.Add(Row(n));
        NotesStore.Save();
        Changed?.Invoke();
        _selfNotify = true;    // change made here: no need to rebuild twice
        NotesStore.Notify();   // refresh the desktop widget too
        _selfNotify = false;
    }

    Border FilterChip(string text, Status? status, int count)
    {
        bool on = _filter == status;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = text, Foreground = on ? Ink : Sub, FontFamily = UiFont, FontSize = 11.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal });
        sp.Children.Add(new TextBlock
        {
            Text = count.ToString(), Foreground = on ? Ink : Faint, FontFamily = UiFont,
            FontSize = 11.5, FontWeight = FontWeights.Bold, Margin = new Thickness(5, 0, 0, 0)
        });
        var chip = new Border
        {
            Child = sp, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 8, 0),
            Background = on ? Ui.Brush(0x2E, 0xFF, 0xFF, 0xFF) : Ui.Brush(0x14, 0xFF, 0xFF, 0xFF), Cursor = Cursors.Hand
        };
        chip.MouseLeftButtonUp += (_, _) => { _filter = status; Rebuild(); };
        return chip;
    }

    Border Row(Note n)
    {
        bool done = n.Status == Status.Done;
        var grid = new Grid();
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });

        var dot = new Border
        {
            Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = ColorOf(n.Status),
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(dot);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (ReferenceEquals(_editing, n))
        {
            // Edit mode: type new text, Enter = save, Esc = cancel
            var box = new TextBox
            {
                Text = n.Due is DateTime dd ? $"{n.Text} @{dd.ToString("dd'/'MM", EnCulture)}" : n.Text,
                FontFamily = UiFont, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Ink,
                CaretBrush = Ink, Background = Ui.Brush(0x26, 0xFF, 0xFF, 0xFF), BorderBrush = ColorOf(Status.Progress),
                BorderThickness = new Thickness(1), Padding = new Thickness(6, 2, 6, 3)
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { SaveEdit(n, box.Text); e.Handled = true; }
                else if (e.Key == Key.Escape) { _editing = null; Rebuild(); e.Handled = true; }
            };
            box.LostKeyboardFocus += (_, _) => { if (ReferenceEquals(_editing, n)) SaveEdit(n, box.Text); };
            Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
            texts.Children.Add(box);
            texts.Children.Add(new TextBlock { Text = "Enter to save · Esc to cancel · @25/11 = due date, @none = remove", Foreground = Sub, FontFamily = UiFont, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
        }
        else
        {
            var title = new TextBlock
            {
                Text = n.Text, Foreground = done ? Faint : Ink, FontFamily = UiFont,
                FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = "Double-click to edit", Background = Brushes.Transparent
            };
            if (done) title.TextDecorations = TextDecorations.Strikethrough;
            title.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { BeginEdit(n); e.Handled = true; } };
            texts.Children.Add(title);
            texts.Children.Add(new TextBlock
            {
                Text = done && n.DoneAt is DateTime d ? $"Done {d:h:mm tt}"
                     : n.Due is DateTime due ? DueLabel(due)
                     : $"Added {n.Added:h:mm tt}",
                Foreground = !done && n.Due is DateTime du && du.Date <= DateTime.Today ? Ui.Brush(0xFF, 0xFF, 0x6B, 0x5F) : Sub, FontFamily = UiFont, FontSize = 11
            });
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var chip = new Border
        {
            Background = ChipBgOf(n.Status), CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 2, 9, 3),
            Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = NameOf(n.Status), Foreground = ChipTextOf(n.Status), FontFamily = UiFont, FontSize = 11, FontWeight = FontWeights.SemiBold }
        };
        Grid.SetColumn(chip, 2);
        grid.Children.Add(chip);

        // Action buttons: Progress / Waiting / Done (clicking the active one = back to To Do)
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, s) in new[] { ("▶ Progress", Status.Progress), ("⏸ Waiting", Status.Waiting), ("✓ Done", Status.Done) })
        {
            bool active = n.Status == s;
            var btn = new Border
            {
                CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 3, 8, 4), Cursor = Cursors.Hand,
                Background = active ? ColorOf(s) : Brushes.Transparent,
                Child = new TextBlock { Text = label, Foreground = active ? White : Sub, FontFamily = UiFont, FontSize = 11, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal }
            };
            btn.MouseLeftButtonUp += (_, _) => SetStatus(n, active ? Status.Todo : s);
            seg.Children.Add(btn);
        }
        var segBox = new Border
        {
            Background = SegBg, CornerRadius = new CornerRadius(9), Padding = new Thickness(2), Child = seg,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(segBox, 3);
        grid.Children.Add(segBox);

        // Edit (pencil) & delete (✕) buttons
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var edit = new TextBlock
        {
            Text = "\uE70F", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = Sub, FontSize = 13,
            Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
            ToolTip = "Edit note", Background = Brushes.Transparent
        };
        edit.MouseLeftButtonUp += (_, _) => BeginEdit(n);
        var dueBtn = new TextBlock
        {
            Text = "\uE787", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Foreground = n.Due != null ? ColorOf(Status.Progress) : Sub, FontSize = 13,
            Margin = new Thickness(2, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
            ToolTip = n.Due is DateTime cur ? $"Due {cur.ToString("ddd, d MMM yyyy", EnCulture)} — click to change" : "Set due date",
            Background = Brushes.Transparent
        };
        dueBtn.MouseLeftButtonUp += (_, e) =>
        {
            ShowDuePicker(dueBtn, n.Due, d => { n.Due = d; Rebuild(); });
            e.Handled = true;
        };
        var del = new TextBlock
        {
            Text = "✕", Foreground = Sub, FontSize = 13, Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Cancel / delete note", Background = Brushes.Transparent
        };
        del.MouseLeftButtonUp += (_, _) => { _notes.Remove(n); Rebuild(); };
        tools.Children.Add(dueBtn);
        tools.Children.Add(edit);
        tools.Children.Add(del);
        Grid.SetColumn(tools, 4);
        grid.Children.Add(tools);

        return new Border
        {
            Background = RowBg, BorderBrush = RowEdge, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11), Padding = new Thickness(12, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 7), Child = grid
        };
    }

    // ================= Due date =================

    DateTime? _newDue;
    readonly Border _newDueChip;
    readonly TextBlock _newDueText = new() { FontSize = 11, FontWeight = FontWeights.SemiBold };

    /// The date picker popup is open → the notch must not auto-close.
    public static bool DatePopupOpen { get; private set; }

    void UpdateNewDueChip()
    {
        _newDueText.FontFamily = UiFont;
        if (_newDue is DateTime d)
        {
            _newDueText.Text = DueLabel(d);
            _newDueText.Foreground = White;
            _newDueChip.Background = ColorOf(Status.Progress);
            _newDueChip.ToolTip = "Click to change the due date";
        }
        else
        {
            _newDueText.Text = "+ Due date";
            _newDueText.Foreground = Ink;
            _newDueChip.Background = Ui.Brush(0x26, 0xFF, 0xFF, 0xFF);
            _newDueChip.ToolTip = "Set a due date (deadline)";
        }
    }

    /// Date picker popup: shortcuts (Today, Tomorrow, Friday, Next week) + calendar + "No due date".
    void ShowDuePicker(FrameworkElement anchor, DateTime? current, Action<DateTime?> pick)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, VerticalOffset = 6,
            AllowsTransparency = true, StaysOpen = false, PopupAnimation = PopupAnimation.Fade
        };
        void Choose(DateTime? d) { popup.IsOpen = false; Mouse.Capture(null); pick(d); }

        Border Quick(string label, DateTime? value, bool danger = false)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand,
                Background = Ui.Brush(0x1F, 0xFF, 0xFF, 0xFF),
                Child = new TextBlock { Text = label, Foreground = danger ? Ui.Brush(0xFF, 0xFF, 0x6B, 0x5F) : Ink, FontFamily = UiFont, FontSize = 11.5, FontWeight = FontWeights.SemiBold }
            };
            b.MouseEnter += (_, _) => b.Background = Ui.Brush(0x33, 0xFF, 0xFF, 0xFF);
            b.MouseLeave += (_, _) => b.Background = Ui.Brush(0x1F, 0xFF, 0xFF, 0xFF);
            b.MouseLeftButtonUp += (_, e) => { Choose(value); e.Handled = true; };
            return b;
        }

        var today = DateTime.Today;
        int toFri = ((int)DayOfWeek.Friday - (int)today.DayOfWeek + 7) % 7;
        var quick = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        quick.Children.Add(Quick("Today", today));
        quick.Children.Add(Quick("Tomorrow", today.AddDays(1)));
        quick.Children.Add(Quick("Friday", today.AddDays(toFri == 0 ? 7 : toFri)));
        quick.Children.Add(Quick("Next week", today.AddDays(7)));

        var cal = new Calendar
        {
            SelectionMode = CalendarSelectionMode.SingleDate, FirstDayOfWeek = DayOfWeek.Monday,
            DisplayDate = current ?? today, SelectedDate = current, IsTodayHighlighted = true,
            BorderThickness = new Thickness(0), Background = Brushes.White
        };
        cal.SelectedDatesChanged += (_, _) => { if (cal.SelectedDate is DateTime d && d != current) Choose(d.Date); };

        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (current != null)
        {
            var clear = Quick("No due date", null, danger: true);
            DockPanel.SetDock(clear, Dock.Right);
            footer.Children.Add(clear);
        }
        footer.Children.Add(new TextBlock
        {
            Text = current is DateTime c ? $"Due {c.ToString("dddd, d MMM yyyy", EnCulture)}" : "No due date yet",
            Foreground = Sub, FontFamily = UiFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center
        });

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "Due date", Foreground = Ink, FontFamily = UiFont, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8) });
        sp.Children.Add(quick);
        sp.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(4), Child = cal });
        sp.Children.Add(footer);

        popup.Child = new Border
        {
            Background = Ui.Brush(0xF2, 0x1E, 0x1E, 0x20), BorderBrush = Ui.Brush(0x33, 0xFF, 0xFF, 0xFF), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(12), Child = sp, Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.4 }
        };
        popup.Opened += (_, _) => DatePopupOpen = true;
        popup.Closed += (_, _) => DatePopupOpen = false;
        popup.IsOpen = true;
    }

    static Border Kbd(string text) => new()
    {
        Background = Ui.Brush(0x26, 0xFF, 0xFF, 0xFF), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 2),
        Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = Ink, FontSize = 10.5 }
    };
}

/// Shared note storage for the Notes tab (notch) and the desktop Notes widget.
public static class NotesStore
{
    public static readonly List<NotesView.Note> Notes = NotesView.LoadNotes();

    /// The notes changed (from the notch or a widget).
    public static event Action? Changed;

    static bool _notifying;

    public static void Save() => NotesView.SaveNotes(Notes);

    public static void Notify()
    {
        if (_notifying) return;   // prevent endless mutual calls
        _notifying = true;
        try { Changed?.Invoke(); }
        finally { _notifying = false; }
    }

    public static void SetStatus(NotesView.Note n, NotesView.Status s)
    {
        n.Status = s;
        n.DoneAt = s == NotesView.Status.Done ? DateTime.Now : null;
        Save();
        Notify();
    }
}
