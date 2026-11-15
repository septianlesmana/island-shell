using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DynamicIsland;

/// "macOS Look": makes Windows look like macOS using Windows' own settings
/// (light/dark mode, transparency, macOS blue accent, wallpaper, hidden desktop icons).
/// All previous values are backed up first to %AppData%\DynamicIsland\theme-backup.json, so
/// "Restore Windows Look" restores everything exactly as it was.
static class MacTheme
{
    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const string Dwm = @"Software\Microsoft\Windows\DWM";
    const string Accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    const string Desktop = @"Control Panel\Desktop";
    const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");
    static readonly string BackupPath = Path.Combine(Dir, "theme-backup.json");

    // Values that get changed: (key, value name)
    static readonly (string Key, string Name)[] Values =
    {
        (Personalize, "AppsUseLightTheme"), (Personalize, "SystemUsesLightTheme"),
        (Personalize, "EnableTransparency"), (Personalize, "ColorPrevalence"),
        (Dwm, "ColorPrevalence"), (Dwm, "AccentColor"), (Dwm, "ColorizationColor"), (Dwm, "ColorizationAfterglow"),
        (Accent, "AccentColorMenu"), (Accent, "StartColorMenu"), (Accent, "AccentPalette"),
        (Desktop, "WallpaperStyle"), (Desktop, "TileWallpaper"),
    };

    public static bool IsApplied => File.Exists(BackupPath);

    /// Apply the macOS-style look. dark = dark mode (matches the dark glass menu bar & notch).
    public static void Apply(bool dark = true)
    {
        if (!IsApplied) SaveBackup();

        using (var k = Registry.CurrentUser.CreateSubKey(Personalize))
        {
            k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            k.SetValue("EnableTransparency", 1, RegistryValueKind.DWord);   // Windows transparency effects
            k.SetValue("ColorPrevalence", 0, RegistryValueKind.DWord);      // taskbar/Start without accent color
        }

        // macOS blue accent (#007AFF) + its shades
        byte[] palette =
        {
            0x99, 0xCC, 0xFF, 0x00,  0x66, 0xB2, 0xFF, 0x00,  0x33, 0x99, 0xFF, 0x00,  0x00, 0x7A, 0xFF, 0x00,
            0x00, 0x62, 0xCC, 0x00,  0x00, 0x4A, 0x99, 0x00,  0x00, 0x31, 0x66, 0x00,  0x00, 0x19, 0x33, 0x00,
        };
        using (var k = Registry.CurrentUser.CreateSubKey(Accent))
        {
            k.SetValue("AccentPalette", palette, RegistryValueKind.Binary);
            k.SetValue("AccentColorMenu", unchecked((int)0xFFFF7A00), RegistryValueKind.DWord);   // ABGR
            k.SetValue("StartColorMenu", unchecked((int)0xFFCC6200), RegistryValueKind.DWord);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(Dwm))
        {
            k.SetValue("ColorPrevalence", 0, RegistryValueKind.DWord);     // neutral window title bars like macOS
            k.SetValue("AccentColor", unchecked((int)0xFFFF7A00), RegistryValueKind.DWord);
            k.SetValue("ColorizationColor", unchecked((int)0xC4007AFF), RegistryValueKind.DWord);
            k.SetValue("ColorizationAfterglow", unchecked((int)0xC4007AFF), RegistryValueKind.DWord);
        }

        // macOS-style abstract wallpaper ("Fill" mode)
        var file = dark ? "wallpaper-macos.jpg" : "wallpaper-macos-light.jpg";
        var src = FindWallpaper(file) ?? FindWallpaper("wallpaper-macos.jpg");
        if (src != null)
        {
            Directory.CreateDirectory(Dir);
            var dst = Path.Combine(Dir, file);
            File.Copy(src, dst, overwrite: true);
            using (var k = Registry.CurrentUser.CreateSubKey(Desktop))
            {
                k.SetValue("WallpaperStyle", "10");
                k.SetValue("TileWallpaper", "0");
            }
            SetWallpaper(dst);
        }

        SetDesktopIconsVisible(false);   // clean desktop like macOS
        Broadcast();
    }

    /// Restore all settings as they were before "macOS Look".
    public static void Restore()
    {
        if (!IsApplied) return;
        var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath)) ?? new Backup();

        foreach (var (key, name) in Values)
        {
            using var k = Registry.CurrentUser.CreateSubKey(key);
            if (!backup.Values.TryGetValue(key + "|" + name, out var v) || v == null) { k.DeleteValue(name, false); continue; }
            switch (v.Kind)
            {
                case "dword": k.SetValue(name, int.Parse(v.Data), RegistryValueKind.DWord); break;
                case "binary": k.SetValue(name, Convert.FromBase64String(v.Data), RegistryValueKind.Binary); break;
                default: k.SetValue(name, v.Data, RegistryValueKind.String); break;
            }
        }
        if (!string.IsNullOrEmpty(backup.Wallpaper)) SetWallpaper(backup.Wallpaper);
        SetDesktopIconsVisible(!backup.IconsHidden);
        Broadcast();
        File.Delete(BackupPath);
    }

    // ---------- Backup ----------

    class Stored { public string Kind { get; set; } = ""; public string Data { get; set; } = ""; }
    class Backup
    {
        public Dictionary<string, Stored?> Values { get; set; } = new();
        public string? Wallpaper { get; set; }
        public bool IconsHidden { get; set; }
    }

    static void SaveBackup()
    {
        var b = new Backup { Wallpaper = GetWallpaper(), IconsHidden = DesktopIconsHidden() };
        foreach (var (key, name) in Values)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            object? v = k?.GetValue(name);
            b.Values[key + "|" + name] = v switch
            {
                int i => new Stored { Kind = "dword", Data = i.ToString() },
                byte[] bytes => new Stored { Kind = "binary", Data = Convert.ToBase64String(bytes) },
                string s => new Stored { Kind = "string", Data = s },
                _ => null
            };
        }
        Directory.CreateDirectory(Dir);
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(b, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------- Windows helpers ----------

    /// Current mode (true = dark), read from Windows settings.
    public static bool IsDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Personalize);
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    static string? FindWallpaper(string file)
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var p = Path.Combine(dir, "Assets", file);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static string? GetWallpaper()
    {
        var sb = new StringBuilder(520);
        return SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, (uint)sb.Capacity, sb, 0) ? sb.ToString() : null;
    }

    static void SetWallpaper(string path) =>
        SystemParametersInfo(0x0014 /* SPI_SETDESKWALLPAPER */, 0, new StringBuilder(path), 0x01 | 0x02 /* UPDATEINIFILE | SENDCHANGE */);

    static bool DesktopIconsHidden()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Advanced);
        return k?.GetValue("HideIcons") is int v && v != 0;
    }

    /// Show/hide desktop icons (same as right-click desktop → View → Show desktop icons).
    static void SetDesktopIconsVisible(bool visible)
    {
        if (DesktopIconsHidden() == !visible) return;
        var view = FindDesktopView();
        if (view != IntPtr.Zero) SendMessage(view, 0x0111 /* WM_COMMAND */, new IntPtr(0x7402), IntPtr.Zero);
    }

    static IntPtr FindDesktopView()
    {
        var progman = FindWindow("Progman", null);
        var view = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero) return view;
        // Sometimes the desktop view is moved into one of the WorkerW windows
        var worker = IntPtr.Zero;
        while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
        {
            view = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero) return view;
        }
        return IntPtr.Zero;
    }

    /// Tell Windows & apps that the theme/colors changed (so it shows immediately).
    static void Broadcast()
    {
        SendMessageTimeout(new IntPtr(0xFFFF) /* HWND_BROADCAST */, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero,
                           "ImmersiveColorSet", 0x0002 /* ABORTIFHUNG */, 1000, out _);
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "WindowsThemeElement", 0x0002, 1000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
}
