using System;
using System.Windows;

namespace DynamicIsland;

public partial class App : Application
{
    MenuBarWindow? _menuBar;

    // Single instance: if an older instance is still running, ask it to exit cleanly
    // (restoring the taskbar & menu bar space), then start the new one.
    const string QuitEventName = "DynamicIsland.Quit";
    const string MutexName = "DynamicIsland.SingleInstance";
    System.Threading.Mutex? _single;
    System.Threading.EventWaitHandle? _quitEvent;

    void EnsureSingleInstance()
    {
        _single = new System.Threading.Mutex(false, MutexName);
        bool owned;
        try { owned = _single.WaitOne(0); }
        catch (System.Threading.AbandonedMutexException) { owned = true; }   // the old instance was force-closed
        if (!owned)
        {
            try
            {
                if (System.Threading.EventWaitHandle.TryOpenExisting(QuitEventName, out var other)) other.Set();
            }
            catch { }
            try { owned = _single.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (System.Threading.AbandonedMutexException) { owned = true; }
        }

        // Listen for quit requests from a newer instance / the "Run Dynamic Island.cmd" script
        _quitEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, QuitEventName);
        var listener = new System.Threading.Thread(() =>
        {
            _quitEvent.WaitOne();
            Dispatcher.BeginInvoke(() => Shutdown());
        }) { IsBackground = true };
        listener.Start();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EnsureSingleInstance();

        // The Dock replaces the Windows taskbar; the taskbar is restored on exit
        Native.HideTaskbar();
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            LogCrash(ev.ExceptionObject as Exception);
            Native.RemoveAllAppBars();
            Native.RestoreTaskbar();
        };
        DispatcherUnhandledException += (_, ev) =>
        {
            LogCrash(ev.Exception);
            // Errors from the Google Calendar view should not bring down the whole app
            if (ev.Exception.StackTrace?.Contains("WebView2") == true) { ev.Handled = true; return; }
            Native.RemoveAllAppBars();
            Native.RestoreTaskbar();
        };
        SessionEnding += (_, _) => Native.RestoreTaskbar();

        Perf.Start();                 // fullscreen & power saving (shared by all windows)
        Native.StartForegroundTracking();

        _menuBar = new MenuBarWindow();
        _menuBar.Show();             // top menu bar
        new DockWindow().Show();     // bottom Dock
        if (DesktopWidgets.Enabled) new DesktopWidgets().Show();   // desktop widgets
        new MainWindow().Show();     // notch / Dynamic Island (top-most)
    }

    /// Write error details to crash.log in the project folder for troubleshooting.
    static void LogCrash(Exception? ex)
    {
        try
        {
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n";
            System.IO.File.AppendAllText(System.IO.Path.Combine(Environment.CurrentDirectory, "crash.log"), text);
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"), text);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Give back the screen space used by the menu bar & Dock, then show the taskbar again
        Native.RemoveAllAppBars();
        Native.RestoreTaskbar();
        try { _single?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
