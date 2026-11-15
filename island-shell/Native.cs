using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

/// Windows helper functions used by the Island, Menu Bar and Dock.
static class Native
{
    static IntPtr Handle(Window w) => new WindowInteropHelper(w).Handle;

    // ---------- Window styles ----------

    /// Hide the window from Alt+Tab.
    public static void MakeToolWindow(Window w) => AddExStyle(w, 0x80 /* WS_EX_TOOLWINDOW */);

    /// Clicks don't steal focus from the app in use.
    public static void MakeNoActivate(Window w) => AddExStyle(w, 0x08000000 /* WS_EX_NOACTIVATE */);

    /// Allow / prevent the window from taking keyboard focus (the menu bar needs focus while Search is open).
    public static void SetNoActivate(Window w, bool noActivate)
    {
        var h = Handle(w);
        int ex = GetWindowLong(h, -20);
        SetWindowLong(h, -20, noActivate ? ex | 0x08000000 : ex & ~0x08000000);
    }

    public static void ForceForeground(Window w) => SetForegroundWindow(Handle(w));

    /// Type text into the active window (supports any character).
    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char ch in text)
        {
            inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 /* UNICODE */ } } });
            inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 | 0x0002 /* KEYUP */ } } });
        }
        if (inputs.Count > 0) SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);

    static void AddExStyle(Window w, int style)
    {
        var h = Handle(w);
        SetWindowLong(h, -20, GetWindowLong(h, -20) | style);
    }

    /// Keep the window above other "always on top" windows.
    public static void KeepOnTop(Window w)
    {
        var h = Handle(w);
        if (h == IntPtr.Zero) return;
        // HWND_TOPMOST, without changing position/size (NOSIZE|NOMOVE), without stealing focus (NOACTIVATE)
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    public static double DpiScale(Visual v) =>
        PresentationSource.FromVisual(v)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    // ---------- Fullscreen & active app ----------

    public static bool IsFullscreenAppActive()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        var cls = ClassName(h);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!GetWindowRect(h, out var r)) return false;
        return r.Left <= 0 && r.Top <= 0 &&
               r.Right >= GetSystemMetrics(0) && r.Bottom >= GetSystemMetrics(1);
    }

    public static (IntPtr Hwnd, uint Pid, string Class) ForegroundInfo()
    {
        var h = GetForegroundWindow();
        GetWindowThreadProcessId(h, out uint pid);
        return (h, pid, h == IntPtr.Zero ? "" : ClassName(h));
    }

    public static string WindowTitle(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(64);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SetForegroundWindow(hwnd);
    }

    // ---------- Windows keyboard shortcuts ----------

    /// Press a key combination (e.g. Ctrl+C = 0x11, 0x43): pressed in order, released in reverse.
    public static void PressKeys(params byte[] keys)
    {
        foreach (var k in keys) keybd_event(k, 0, 0, UIntPtr.Zero);
        for (int i = keys.Length - 1; i >= 0; i--) keybd_event(keys[i], 0, 0x0002 /* KEYUP */, UIntPtr.Zero);
    }

    // ---------- Background apps (system tray) ----------

    /// While before this time, the taskbar is not hidden (used when showing tray icons).
    public static DateTime TaskbarHideSuspendedUntil { get; set; }

    static readonly string[] TrayOverflowClasses = { "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow" };

    /// The Windows "hidden icons" panel is open.
    public static bool IsTrayOverflowOpen()
    {
        foreach (var cls in TrayOverflowClasses)
        {
            var h = FindWindow(cls, null);
            if (h != IntPtr.Zero && IsWindowVisible(h)) return true;
        }
        return false;
    }

    /// Briefly show the taskbar, then open the hidden icons panel (Win+B, then Enter),
    /// so background app icons (OneDrive, antivirus, etc.) can be clicked as usual.
    public static async System.Threading.Tasks.Task ShowTrayOverflowAsync()
    {
        TaskbarHideSuspendedUntil = DateTime.Now.AddSeconds(30);
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;
        ShowWindow(tray, 5 /* SW_SHOW */);
        SetForegroundWindow(tray);
        await System.Threading.Tasks.Task.Delay(150);
        PressWin(0x42);                       // Win + B: focus the icon area
        await System.Threading.Tasks.Task.Delay(200);
        PressKeys(0x0D);                      // Enter: open "Show hidden icons"
    }

    // ---------- App menus (File, Edit, View, ...) ----------

    /// Classic (Win32) menu titles of a window, e.g. "&File", "&Edit". Empty for modern apps.
    public static List<string> GetMenuTitles(IntPtr hwnd)
    {
        var list = new List<string>();
        try
        {
            var menu = GetMenu(hwnd);
            if (menu == IntPtr.Zero) return list;
            int n = Math.Min(GetMenuItemCount(menu), 12);
            for (int i = 0; i < n; i++)
            {
                var sb = new StringBuilder(128);
                if (GetMenuString(menu, (uint)i, sb, sb.Capacity, 0x400 /* MF_BYPOSITION */) > 0)
                    list.Add(sb.ToString().Split('\t')[0]);
            }
        }
        catch { }
        return list;
    }

    /// Open an app's real menu via its access key (like pressing Alt + letter).
    public static void OpenAppMenu(IntPtr hwnd, char mnemonic)
    {
        Activate(hwnd);
        PostMessage(hwnd, 0x0112 /* WM_SYSCOMMAND */, new IntPtr(0xF100 /* SC_KEYMENU */), new IntPtr(char.ToLowerInvariant(mnemonic)));
    }

    public static void ToggleMaximize(IntPtr hwnd) => ShowWindow(hwnd, IsZoomed(hwnd) ? 9 /* RESTORE */ : 3 /* MAXIMIZE */);

    static void PressWin(byte? key = null)
    {
        const byte VK_LWIN = 0x5B; const uint UP = 0x0002;
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        if (key is byte k) { keybd_event(k, 0, 0, UIntPtr.Zero); keybd_event(k, 0, UP, UIntPtr.Zero); }
        keybd_event(VK_LWIN, 0, UP, UIntPtr.Zero);
    }

    public static void OpenStart() => PressWin();
    public static void OpenQuickSettings() => PressWin(0x41);  // Win + A
    public static void OpenCalendar() => PressWin(0x4E);       // Win + N
    public static void OpenSearch() => PressWin(0x53);         // Win + S
    // ---------- Show Desktop (without Win+D, so it can always be undone) ----------

    static List<IntPtr> _hiddenForDesktop = new();   // minimized windows, front-most first
    static DateTime _desktopShownAt;

    /// There are still windows from "Show Desktop" that haven't been restored.
    public static bool DesktopShown
    {
        get
        {
            _hiddenForDesktop.RemoveAll(h => !IsWindow(h));   // closed windows
            // Minimizing runs in the background and takes a moment; only after 3 seconds are windows that
            // aren't minimized considered reopened by you
            if (DateTime.Now - _desktopShownAt > TimeSpan.FromSeconds(3))
                _hiddenForDesktop.RemoveAll(h => !IsIconic(h));
            return _hiddenForDesktop.Count > 0;
        }
    }

    /// Minimize all app windows and remember their order.
    public static void ShowDesktop()
    {
        var wins = GetAppWindows().Select(w => w.Hwnd).Where(h => !IsIconic(h)).Distinct().ToList();
        _hiddenForDesktop = wins;
        _desktopShownAt = DateTime.Now;
        foreach (var h in wins) ShowWindowAsync(h, 7 /* SW_SHOWMINNOACTIVE */);
    }

    /// Restore the minimized windows with their original order & size (including maximized).
    public static void RestoreDesktop()
    {
        var list = _hiddenForDesktop;
        _hiddenForDesktop = new();
        // From back to front, so the window that was in front ends up in front again
        for (int i = list.Count - 1; i >= 0; i--)
            if (IsWindow(list[i])) ShowWindowAsync(list[i], 9 /* SW_RESTORE */);
        var top = list.FirstOrDefault(IsWindow);
        if (top != IntPtr.Zero) SetForegroundWindow(top);
    }

    // ---------- App bar: reserving space at screen edges ----------

    static readonly HashSet<IntPtr> _appBars = new();

    /// Register the window as an "app bar" at the top of the screen so other
    /// maximized windows don't cover it. Returns the top position (px).
    public static int RegisterTopAppBar(Window w, int heightPx) => RegisterAppBar(w, 1 /* ABE_TOP */, heightPx);

    /// Same as above, but at the bottom of the screen (for the Dock). Returns the bottom position (px).
    public static int RegisterBottomAppBar(Window w, int heightPx) => RegisterAppBar(w, 3 /* ABE_BOTTOM */, heightPx);

    static int RegisterAppBar(Window w, uint edge, int sizePx)
    {
        var abd = NewAppBarData(w);
        if (_appBars.Add(abd.hWnd))
        {
            abd.uCallbackMessage = RegisterWindowMessage("DynamicIslandAppBar");
            SHAppBarMessage(0 /* ABM_NEW */, ref abd);
        }

        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        abd.uEdge = edge;
        abd.rc = edge == 1
            ? new RECT { Left = 0, Top = 0, Right = sw, Bottom = sizePx }
            : new RECT { Left = 0, Top = sh - sizePx, Right = sw, Bottom = sh };
        SHAppBarMessage(2 /* ABM_QUERYPOS */, ref abd);
        if (edge == 1) abd.rc.Bottom = abd.rc.Top + sizePx;
        else abd.rc.Top = abd.rc.Bottom - sizePx;
        SHAppBarMessage(3 /* ABM_SETPOS */, ref abd);
        return edge == 1 ? abd.rc.Top : abd.rc.Bottom;
    }

    public static void RemoveAppBar(Window w) => RemoveAppBar(Handle(w));

    public static void RemoveAllAppBars()
    {
        foreach (var h in new List<IntPtr>(_appBars)) RemoveAppBar(h);
    }

    static void RemoveAppBar(IntPtr hwnd)
    {
        if (!_appBars.Remove(hwnd)) return;
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };
        SHAppBarMessage(1 /* ABM_REMOVE */, ref abd);
    }

    static APPBARDATA NewAppBarData(Window w) =>
        new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = Handle(w) };

    // ---------- Windows taskbar: hidden, replaced by the Dock ----------

    static int? _oldTaskbarState;

    /// Hide the Windows taskbar (all monitors). Safe to call repeatedly;
    /// called periodically by the Dock so the taskbar stays hidden after an Explorer restart.
    public static void HideTaskbar()
    {
        if (DateTime.Now < TaskbarHideSuspendedUntil) return;   // currently showing background app icons
        var main = FindWindow("Shell_TrayWnd", null);
        if (main == IntPtr.Zero) return;

        if (_oldTaskbarState == null)
        {
            // Switch to "auto-hide" so the taskbar no longer takes screen space
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = main };
            _oldTaskbarState = (int)SHAppBarMessage(4 /* ABM_GETSTATE */, ref abd);
            abd.lParam = new IntPtr(_oldTaskbarState.Value | 1 /* ABS_AUTOHIDE */);
            SHAppBarMessage(10 /* ABM_SETSTATE */, ref abd);
        }

        foreach (var h in TaskbarWindows())
            if (IsWindowVisible(h)) ShowWindow(h, 0 /* SW_HIDE */);
        GuardTaskbar(main);
    }

    // ---------- Taskbar guard: hidden again the moment Windows shows it ----------
    // (e.g. pressing the Windows key, opening Start, or an Explorer restart). Without this the taskbar
    // would be visible until the next periodic check.

    static WinEventProc? _taskbarProc;
    static IntPtr _taskbarHook;
    static uint _taskbarPid;
    static bool _restoring;

    static void GuardTaskbar(IntPtr main)
    {
        GetWindowThreadProcessId(main, out uint pid);
        if (pid == 0 || (pid == _taskbarPid && _taskbarHook != IntPtr.Zero)) return;
        // New Explorer (restarted) → re-hook its process
        if (_taskbarHook != IntPtr.Zero) UnhookWinEvent(_taskbarHook);
        _taskbarPid = pid;
        _taskbarProc ??= (_, _, h, obj, _, _, _) =>
        {
            if (obj != 0 /* OBJID_WINDOW */ || h == IntPtr.Zero || _restoring) return;
            if (DateTime.Now < TaskbarHideSuspendedUntil) return;   // intentionally shown (tray icons)
            var cls = ClassName(h);
            if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") ShowWindow(h, 0 /* SW_HIDE */);
        };
        _taskbarHook = SetWinEventHook(0x8002, 0x8002 /* EVENT_OBJECT_SHOW */, IntPtr.Zero, _taskbarProc, pid, 0, 0);
    }

    /// Show the Windows taskbar again and restore the original auto-hide setting.
    public static void RestoreTaskbar()
    {
        _restoring = true;
        if (_taskbarHook != IntPtr.Zero) { UnhookWinEvent(_taskbarHook); _taskbarHook = IntPtr.Zero; _taskbarPid = 0; }
        var main = FindWindow("Shell_TrayWnd", null);
        if (_oldTaskbarState is int state && main != IntPtr.Zero)
        {
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = main, lParam = new IntPtr(state) };
            SHAppBarMessage(10 /* ABM_SETSTATE */, ref abd);
        }
        _oldTaskbarState = null;
        foreach (var h in TaskbarWindows()) ShowWindow(h, 5 /* SW_SHOW */);
        _restoring = false;
    }

    static IEnumerable<IntPtr> TaskbarWindows()
    {
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        var h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return h;
    }

    // ---------- Open app windows (replacing taskbar buttons) ----------

    /// Aumid is only set for Store/packaged apps (WhatsApp, new Outlook, Snipping Tool, etc.)
    /// Aumid: the process package ID (Store apps). WindowAumid: the ID set on the window
    /// (e.g. Edge & Chrome web apps/PWAs). Title: window title (may contain an unread count).
    public record AppWindow(IntPtr Hwnd, string ExePath, string? Aumid, string? WindowAumid, string Title);

    static readonly Dictionary<IntPtr, string?> _windowAumidCache = new();

    static readonly Dictionary<uint, (string? Path, string? Aumid)> _pathCache = new();

    /// All windows normally shown on the taskbar / Alt+Tab, front-most first.
    public static List<AppWindow> GetAppWindows()
    {
        var list = new List<AppWindow>();
        uint own = (uint)Environment.ProcessId;
        if (_pathCache.Count > 500) _pathCache.Clear();
        if (_windowAumidCache.Count > 500) _windowAumidCache.Clear();

        EnumWindows((h, _) =>
        {
            if (!IsTaskbarWindow(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            // Store apps: the frame belongs to ApplicationFrameHost, the content to the app process
            if (ClassName(h) == "ApplicationFrameWindow") pid = UwpProcessId(h, pid);
            if (pid == own) return true;

            if (!_pathCache.TryGetValue(pid, out var info))
                _pathCache[pid] = info = ProcessInfo(pid);
            if (info.Path == null) return true;
            if (!_windowAumidCache.TryGetValue(h, out var winId))
                _windowAumidCache[h] = winId = GetWindowAumid(h);
            list.Add(new AppWindow(h, info.Path, info.Aumid, winId, WindowTitle(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static bool IsTaskbarWindow(IntPtr h)
    {
        if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return false;
        int ex = GetWindowLong(h, -20);
        bool appWindow = (ex & 0x40000) != 0;                         // WS_EX_APPWINDOW
        if ((ex & 0x80) != 0) return false;                          // WS_EX_TOOLWINDOW
        if ((ex & 0x08000000) != 0 && !appWindow) return false;      // WS_EX_NOACTIVATE
        if (GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero && !appWindow) return false;
        if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return false;
        return ClassName(h) is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    static uint UwpProcessId(IntPtr frame, uint framePid)
    {
        uint result = framePid;
        EnumChildWindows(frame, (c, _) =>
        {
            GetWindowThreadProcessId(c, out uint p);
            if (p != framePid) { result = p; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    static (string? Path, string? Aumid) ProcessInfo(uint pid)
    {
        var hp = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (hp == IntPtr.Zero) return (null, null);
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            string? path = QueryFullProcessImageName(hp, 0, sb, ref size) ? sb.ToString() : null;

            var ab = new StringBuilder(512);
            uint len = (uint)ab.Capacity;
            string? aumid = GetApplicationUserModelId(hp, ref len, ab) == 0 ? ab.ToString() : null;
            return (path, aumid);
        }
        finally { CloseHandle(hp); }
    }

    // ---------- Apps pinned to the Windows taskbar ----------

    public const string AppsFolderPrefix = @"shell:AppsFolder\";

    static readonly Regex AumidPattern =
        new(@"[A-Za-z0-9][A-Za-z0-9.\-]*_[a-z0-9]{13}![A-Za-z0-9._\-]+", RegexOptions.Compiled);

    /// Apps pinned to the taskbar, in taskbar order.
    /// Returns shortcut (.lnk) paths or "shell:AppsFolder\<AUMID>" for Store apps.
    public static List<string> GetTaskbarPins()
    {
        string folder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        var lnks = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.lnk") : Array.Empty<string>();

        byte[] blob = Array.Empty<byte>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
            if (key?.GetValue("Favorites") is byte[] b) blob = b;
        }
        catch { }

        // The pin order data contains UTF-16 text that may start at an even or odd byte
        var texts = new[]
        {
            System.Text.Encoding.Unicode.GetString(blob),
            blob.Length > 1 ? System.Text.Encoding.Unicode.GetString(blob, 1, blob.Length - 1) : ""
        };
        int BytePos(string needle)
        {
            int best = int.MaxValue;
            for (int k = 0; k < 2; k++)
            {
                int i = texts[k].IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (i >= 0) best = Math.Min(best, i * 2 + k);
            }
            return best;
        }

        var items = new List<(int Pos, string Path)>();
        foreach (var lnk in lnks)
            items.Add((BytePos(System.IO.Path.GetFileName(lnk)), lnk));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < 2; k++)
            foreach (Match m in AumidPattern.Matches(texts[k]))
                if (seen.Add(m.Value) && ShellItemExists(AppsFolderPrefix + m.Value))
                    items.Add((m.Index * 2 + k, AppsFolderPrefix + m.Value));

        return items.OrderBy(x => x.Pos).Select(x => x.Path).ToList();
    }

    // ---------- Icons & names from the Windows Shell (also for Store apps) ----------

    /// All installed apps (same as "All apps" in Start): name + ID for shell:AppsFolder.
    /// Must be called on an STA thread.
    public static List<(string Name, string Id)> GetAllApps()
    {
        var list = new List<(string, string)>();
        try
        {
            var folder = ShellItem("shell:AppsFolder");
            if (folder == null) return list;
            var bhid = new Guid("94F60519-2850-4924-AA5A-D15E84868039");   // BHID_EnumItems
            var iid = typeof(IEnumShellItems).GUID;
            folder.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var ptr);
            if (ptr == IntPtr.Zero) return list;
            var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(ptr);
            Marshal.Release(ptr);
            while (items.Next(1, out var item, out uint fetched) == 0 && fetched == 1)
            {
                try
                {
                    item.GetDisplayName(0 /* NORMALDISPLAY */, out var name);
                    item.GetDisplayName(0x80018001 /* PARENTRELATIVEPARSING */, out var id);
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) list.Add((name, id));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, out IShellItem item, out uint fetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems e);
    }

    static IShellItem? ShellItem(string parsingName)
    {
        try
        {
            var iid = typeof(IShellItem).GUID;
            return SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var obj) == 0
                ? obj as IShellItem : null;
        }
        catch { return null; }
    }

    static bool ShellItemExists(string parsingName) => ShellItem(parsingName) != null;

    public static string? GetShellDisplayName(string parsingName)
    {
        try
        {
            var item = ShellItem(parsingName);
            if (item == null) return null;
            item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var name);
            return name;
        }
        catch { return null; }
    }

    /// Large icon (with transparency) for a file, shortcut or Store app.
    public static ImageSource? GetShellItemImage(string parsingName, int size)
    {
        if (ShellItem(parsingName) is not IShellItemImageFactory factory) return null;
        if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x1 | 0x4 /* BIGGERSIZEOK | ICONONLY */, out var hbm) != 0
            || hbm == IntPtr.Zero) return null;
        try
        {
            var ds = new DIBSECTION();
            if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), ref ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero
                || ds.dsBm.bmBitsPixel != 32) return null;

            int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
            var pixels = new byte[stride * h];
            Marshal.Copy(ds.dsBm.bmBits, pixels, 0, pixels.Length);
            if (ds.dsBmih.biHeight > 0)   // bitmap is stored upside down (bottom to top)
            {
                var flipped = new byte[pixels.Length];
                for (int y = 0; y < h; y++) Buffer.BlockCopy(pixels, y * stride, flipped, (h - 1 - y) * stride, stride);
                pixels = flipped;
            }
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { DeleteObject(hbm); }
    }

    /// Cursor position and primary screen size, in pixels.
    public static (int X, int Y) CursorPos() => GetCursorPos(out var p) ? (p.X, p.Y) : (-1, -1);
    public static (int W, int H) PrimaryScreenPx() => (GetSystemMetrics(0), GetSystemMetrics(1));

    /// App ID (AppUserModelID) stored in a .lnk shortcut; used to match notifications.
    public static string? GetShortcutAumid(string path)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var store) != 0 || store == null)
                return null;
            return ReadAumid(store);
        }
        catch { return null; }
    }

    /// App ID set on a window (used by Edge/Chrome for web app windows).
    public static string? GetWindowAumid(IntPtr hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return null;
            return ReadAumid(store);
        }
        catch { return null; }
    }

    static string? ReadAumid(IPropertyStore store)
    {
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            store.GetValue(ref key, out var pv);
            try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.p) : null; }
            finally { PropVariantClear(ref pv); }
        }
        catch { return null; }
    }

    /// Windows efficiency mode (EcoQoS): run the process in power-saving mode. Enabled on battery.
    public static void SetEcoMode(bool on)
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1, ControlMask = 1 /* EXECUTION_SPEED */, StateMask = on ? 1u : 0u
            };
            SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref state,
                                  Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch { }
    }

    /// Capture a screen area (pixels). Used for the blurred glass effect behind the notch.
    public static BitmapSource? CaptureScreen(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 /* SRCCOPY */);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// The left mouse button is pressed (anywhere on screen).
    public static bool IsLeftButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);
    public static void Minimize(IntPtr hwnd) => ShowWindow(hwnd, 6 /* SW_MINIMIZE */);
    public static void Close(IntPtr hwnd) => PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);

    // ---------- Last active window (other than our own) ----------

    static WinEventProc? _fgProc;
    static IntPtr _fgHook;

    /// The other app window that was active last. Lets a Dock icon click
    /// minimize the app in front, like the Windows taskbar.
    public static IntPtr LastForeground { get; private set; }

    /// The active window changed (lets the menu bar avoid polling every second).
    public static event Action? ForegroundChanged;

    public static void StartForegroundTracking()
    {
        if (_fgHook != IntPtr.Zero) return;
        uint own = (uint)Environment.ProcessId;
        LastForeground = GetForegroundWindow();
        _fgProc = (_, _, h, _, _, _, _) =>
        {
            if (h == IntPtr.Zero) return;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != own) LastForeground = h;
            // Never let an error in one listener close the app
            try { if (Application.Current != null) ForegroundChanged?.Invoke(); } catch { }
        };
        _fgHook = SetWinEventHook(3, 3 /* EVENT_SYSTEM_FOREGROUND */, IntPtr.Zero, _fgProc, 0, 0, 0);
    }

    // ---------- Icons ----------

    /// File/folder icon from the Windows Shell (32 px).
    public static ImageSource? GetShellIcon(string path)
    {
        var info = new SHFILEINFO();
        SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), 0x100 /* SHGFI_ICON */);
        return info.hIcon == IntPtr.Zero ? null : FromHIcon(info.hIcon);
    }

    /// High-resolution Recycle Bin icon (empty / full).
    public static ImageSource? GetRecycleBinIcon(bool full)
    {
        var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        if (SHGetStockIconInfo(full ? 32u : 31u, 0 /* SHGSI_ICONLOCATION */, ref info) != 0) return null;
        if (SHDefExtractIcon(info.szPath, info.iIcon, 0, out var large, out var small, 128) != 0) return null;
        if (small != IntPtr.Zero) DestroyIcon(small);
        return large == IntPtr.Zero ? null : FromHIcon(large);
    }

    public static bool IsRecycleBinFull()
    {
        var q = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref q) == 0 && q.i64NumItems > 0;
    }

    /// Empty the Recycle Bin. Windows still shows its own confirmation dialog.
    public static void EmptyRecycleBin(Window owner) => SHEmptyRecycleBin(Handle(owner), null, 0);

    static ImageSource? FromHIcon(IntPtr hIcon)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { DestroyIcon(hIcon); }
    }

    // ---------- Win32 declarations ----------

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uCallbackMessage;
        public uint uEdge; public RECT rc; public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHSTOCKICONINFO
    {
        public uint cbSize; public IntPtr hIcon; public int iSysImageIndex; public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attribs);
        void Compare(IShellItem psi, uint hint, out int order);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DIBSECTION
    {
        public BITMAP dsBm; public BITMAPINFOHEADER dsBmih;
        public uint dsBitfield0, dsBitfield1, dsBitfield2;
        public IntPtr dshSection; public uint dsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref DIBSECTION ds);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint length, StringBuilder aumid);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr pbc, int flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder name, ref int size);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetMenu(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int max, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("shell32.dll")] static extern int SHGetStockIconInfo(uint siid, uint flags, ref SHSTOCKICONINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHDefExtractIcon(string file, int index, uint flags, out IntPtr large, out IntPtr small, uint size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);
}
