using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DynamicIsland;

/// "File, Edit, View, Window, Help" menus for the active app.
/// - Classic apps with a Win32 menu (old Notepad, many tools) → their real menu titles;
///   clicking opens that app's own menu.
/// - Other apps → a generic macOS-style menu that sends standard Windows keyboard shortcuts.
static class AppMenus
{
    public record Entry(string? Text, string? Hint = null, Action? Act = null)
    {
        public static Entry Separator => new((string?)null);   // (string?) so it isn't resolved as the record copy constructor
    }

    const byte Ctrl = 0x11, Shift = 0x10, Alt = 0x12, Win = 0x5B;

    /// Send a shortcut to the active app (its window is activated first).
    static Action Keys(params byte[] keys) => async () =>
    {
        var target = Native.LastForeground;
        if (target != IntPtr.Zero) Native.Activate(target);
        await Task.Delay(120);
        Native.PressKeys(keys);
    };

    static Action OnTarget(Action<IntPtr> act) => () =>
    {
        var target = Native.LastForeground;
        if (target != IntPtr.Zero) act(target);
    };

    public static readonly (string Title, Entry[] Items)[] Generic =
    {
        ("File", new[]
        {
            new Entry("New", "Ctrl+N", Keys(Ctrl, (byte)'N')),
            new Entry("Open…", "Ctrl+O", Keys(Ctrl, (byte)'O')),
            Entry.Separator,
            new Entry("Save", "Ctrl+S", Keys(Ctrl, (byte)'S')),
            new Entry("Save As…", "Ctrl+Shift+S", Keys(Ctrl, Shift, (byte)'S')),
            Entry.Separator,
            new Entry("Print…", "Ctrl+P", Keys(Ctrl, (byte)'P')),
            Entry.Separator,
            new Entry("Close Window", "Ctrl+W", Keys(Ctrl, (byte)'W')),
        }),
        ("Edit", new[]
        {
            new Entry("Undo", "Ctrl+Z", Keys(Ctrl, (byte)'Z')),
            new Entry("Redo", "Ctrl+Y", Keys(Ctrl, (byte)'Y')),
            Entry.Separator,
            new Entry("Cut", "Ctrl+X", Keys(Ctrl, (byte)'X')),
            new Entry("Copy", "Ctrl+C", Keys(Ctrl, (byte)'C')),
            new Entry("Paste", "Ctrl+V", Keys(Ctrl, (byte)'V')),
            new Entry("Select All", "Ctrl+A", Keys(Ctrl, (byte)'A')),
            Entry.Separator,
            new Entry("Find…", "Ctrl+F", Keys(Ctrl, (byte)'F')),
            Entry.Separator,
            new Entry("Emoji & Symbols", "Win+.", Keys(Win, 0xBE)),
            new Entry("Clipboard History", "Win+V", Keys(Win, (byte)'V')),
        }),
        ("View", new[]
        {
            new Entry("Zoom In", "Ctrl+Plus", Keys(Ctrl, 0xBB)),
            new Entry("Zoom Out", "Ctrl+Minus", Keys(Ctrl, 0xBD)),
            new Entry("Actual Size", "Ctrl+0", Keys(Ctrl, (byte)'0')),
            Entry.Separator,
            new Entry("Enter Full Screen", "F11", Keys(0x7A)),
            new Entry("Reload", "F5", Keys(0x74)),
        }),
        ("Window", new[]
        {
            new Entry("Minimize", null, OnTarget(Native.Minimize)),
            new Entry("Zoom", null, OnTarget(Native.ToggleMaximize)),
            Entry.Separator,
            new Entry("Move Window to Left Side", "Win+←", Keys(Win, 0x25)),
            new Entry("Move Window to Right Side", "Win+→", Keys(Win, 0x27)),
            Entry.Separator,
            new Entry("Show All Windows", "Win+Tab", Keys(Win, 0x09)),
            Entry.Separator,
            new Entry("Close Window", "Alt+F4", Keys(Alt, 0x73)),
        }),
        ("Help", new[]
        {
            new Entry("App Help", "F1", Keys(0x70)),
            new Entry("Windows Get Help", null, () => Ui.Open("ms-contact-support:")),
        }),
    };

    /// Access key from a Win32 menu title ("&File" → 'F').
    public static char? Mnemonic(string title)
    {
        int i = title.IndexOf('&');
        return i >= 0 && i + 1 < title.Length && title[i + 1] != '&' ? title[i + 1] : title.FirstOrDefault(char.IsLetter);
    }

    public static string Clean(string title) => title.Replace("&&", "\u0001").Replace("&", "").Replace('\u0001', '&');
}
