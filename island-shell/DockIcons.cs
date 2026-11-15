using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DynamicIsland;

/// "Minimal" Dock icons (as in the demo image): rounded gradient tile + simple symbol.
/// Apps are recognized by name / path; unrecognized apps keep their original icon.
static class DockIcons
{
    public record Style(Color Top, Color Bottom, string Glyph, bool Stroke, Color Ink);

    // Symbols in a 36×36 box (scaled automatically)
    const string Folder = "M2,9 A3,3 0 0 1 5,6 L13,6 L16,9 L31,9 A3,3 0 0 1 34,12 L34,28 A3,3 0 0 1 31,31 L5,31 A3,3 0 0 1 2,28 Z";
    const string Globe = "M4,18 A14,14 0 1 1 32,18 A14,14 0 1 1 4,18 Z M18,4 A6.5,14 0 0 1 18,32 A6.5,14 0 0 1 18,4 Z M4,18 L32,18 M7,11 L29,11 M7,25 L29,25";
    const string Chat = "M18,4 C10,4 4,9.5 4,16 C4,19.6 5.9,22.8 8.9,25 L7.5,31 L13.1,27.2 C14.6,27.7 16.3,28 18,28 C26,28 32,22.5 32,16 C32,9.5 26,4 18,4 Z";
    const string Code = "M12,10 L5,18 L12,26 M24,10 L31,18 L24,26 M20,7 L16,29";
    const string Mail = "M5,8 L31,8 L31,28 L5,28 Z M6,9 L18,19 L30,9";
    const string Notes = "M8,4 L28,4 L28,32 L8,32 Z M13,12 L23,12 M13,18 L23,18 M13,24 L19,24";
    const string Sparkle = "M18,4 L18,32 M6,11 L30,25 M6,25 L30,11";
    const string Scissors = "M6,26 A4,4 0 1 1 14,26 A4,4 0 1 1 6,26 Z M22,26 A4,4 0 1 1 30,26 A4,4 0 1 1 22,26 Z M12.5,23 L26,5 M23.5,23 L10,5";
    const string Pulse = "M3,19 L10,19 L14,9 L20,28 L24,17 L33,17";
    const string Terminal = "M7,9 L14,16 L7,23 M17,26 L30,26";
    const string Gear = "M13,18 A5,5 0 1 1 23,18 A5,5 0 1 1 13,18 Z M18,4 L18,9 M18,27 L18,32 M4,18 L9,18 M27,18 L32,18 M8,8 L11.5,11.5 M24.5,24.5 L28,28 M8,28 L11.5,24.5 M24.5,11.5 L28,8";
    const string Music = "M13,26 L13,8 L29,5 L29,22 M5,26 A4,4 0 1 1 13,26 A4,4 0 1 1 5,26 Z M21,22 A4,4 0 1 1 29,22 A4,4 0 1 1 21,22 Z";
    const string Document = "M9,4 L22,4 L28,10 L28,32 L9,32 Z M22,4 L22,10 L28,10 M13,17 L24,17 M13,22 L24,22 M13,27 L20,27";
    const string AppWindow = "M5,7 L31,7 L31,29 L5,29 Z M5,13 L31,13 M9,10 L9.5,10 M13,10 L13.5,10";
    const string Photo = "M4,7 L32,7 L32,29 L4,29 Z M7,26 L15,17 L21,23 L25,19 L30,25";
    const string Calculator = "M8,4 L28,4 L28,32 L8,32 Z M12,9 L24,9 L24,13 L12,13 Z M13,19 L13.5,19 M18,19 L18.5,19 M23,19 L23.5,19 M13,24 L13.5,24 M18,24 L18.5,24 M23,24 L23.5,24 M13,28 L13.5,28 M18,28 L18.5,28 M23,28 L23.5,28";

    static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    static readonly Color White = Colors.White;

    // Order matters: web apps (e.g. WhatsApp Web in Chrome) are checked before their browser.
    static readonly (string[] Keys, Style Style)[] Rules =
    {
        (new[] { "whatsapp" }, new(C(0x67E57F), C(0x22B14C), Chat, false, White)),
        (new[] { "teams" }, new(C(0x8B92F0), C(0x5059C9), Chat, false, White)),
        (new[] { "telegram", "discord", "slack", "messenger" }, new(C(0x6FC3FF), C(0x2A8BF2), Chat, false, White)),
        (new[] { "outlook", "mail", "thunderbird" }, new(C(0x4FA3FF), C(0x1565C0), Mail, true, White)),
        (new[] { "visual studio", "\\code.exe", "vscode", "cursor" }, new(C(0x3D3D4E), C(0x1F1F2A), Code, true, C(0x7EE0FF))),
        (new[] { "notepad", "onenote", "sticky", "notes", "obsidian", "notion" }, new(C(0xFFE27A), C(0xFFC93C), Notes, true, C(0x8A6D1F))),
        (new[] { "claude", "chatgpt", "copilot", "gemini" }, new(C(0xFF9F7A), C(0xE8743B), Sparkle, true, White)),
        (new[] { "snipping", "screensketch", "screenclip" }, new(C(0xFF7A8A), C(0xE53950), Scissors, true, White)),
        (new[] { "task manager", "taskmgr" }, new(C(0x6FE08A), C(0x2DA44E), Pulse, true, White)),
        (new[] { "terminal", "powershell", "\\cmd.exe", "command prompt" }, new(C(0x2C2C34), C(0x101014), Terminal, true, White)),
        (new[] { "settings", "control panel" }, new(C(0x9AA0A8), C(0x5F656E), Gear, true, White)),
        (new[] { "spotify", "music", "media player", "vlc" }, new(C(0xFF9F7A), C(0xFF5E62), Music, true, White)),
        (new[] { "winword", "word" }, new(C(0x4F8EF7), C(0x1E5BC6), Document, true, White)),
        (new[] { "excel" }, new(C(0x3DBE6E), C(0x16794A), Document, true, White)),
        (new[] { "powerpnt", "powerpoint" }, new(C(0xFF8A5C), C(0xD24726), Document, true, White)),
        (new[] { "acrobat", "pdf" }, new(C(0xFF6B6B), C(0xC62828), Document, true, White)),
        (new[] { "saplogon", "sap logon", "sap gui" }, new(C(0x5B7BB5), C(0x2C4A86), AppWindow, true, White)),
        (new[] { "photos", "paint", "gallery" }, new(C(0xFFB86B), C(0xF06292), Photo, true, White)),
        (new[] { "calculator", "calc" }, new(C(0x5A5A66), C(0x2E2E36), Calculator, true, C(0xFFB340))),
        (new[] { "msedge", "edge" }, new(C(0x3FD0C9), C(0x0F7ACB), Globe, true, White)),
        (new[] { "chrome", "firefox", "brave", "opera", "browser" }, new(C(0xFFFFFF), C(0xE6EAF2), Globe, true, C(0x2A7DE1))),
        (new[] { "explorer" }, new(C(0x5AC8FA), C(0x1E88E5), Folder, false, White)),
    };

    public static Style? For(DockEntry e)
    {
        var key = $"{e.Name} {System.IO.Path.GetFileName(e.FilePath)} {e.Target}".ToLowerInvariant();
        foreach (var (keys, style) in Rules)
            foreach (var k in keys)
                if (key.Contains(k)) return style;
        return null;
    }

    /// Replace the icon canvas content (tile + image) with the minimal symbol.
    public static void Apply(Style s, Border tile, Grid face, Image img, double size, double radius)
    {
        tile.Background = new LinearGradientBrush(s.Top, s.Bottom, 90);
        tile.BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0));
        tile.BorderThickness = new Thickness(1.5);
        tile.CornerRadius = new CornerRadius(radius);
        img.Visibility = Visibility.Collapsed;
        var ink = new SolidColorBrush(s.Ink);
        var path = new Path
        {
            Data = Geometry.Parse(s.Glyph), Stretch = Stretch.Uniform,
            Width = size * 0.56, Height = size * 0.56,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        if (s.Stroke) { path.Stroke = ink; path.StrokeThickness = size * 0.055; }
        else path.Fill = ink;
        face.Children.Insert(face.Children.IndexOf(tile) + 1, path);
    }
}
