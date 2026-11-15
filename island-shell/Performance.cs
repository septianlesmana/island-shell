using System;
using System.Windows;
using System.Windows.Threading;
using Windows.System.Power;

namespace DynamicIsland;

/// Shared performance manager for the notch, menu bar and Dock:
/// - one "fullscreen app" check for all windows (instead of three),
/// - power-saving mode: when Battery Saver is on or battery ≤ 20% without a charger,
///   updates are less frequent and heavy effects (shadows, animations) are turned off,
/// - Windows efficiency mode (EcoQoS) on battery, so this process
///   runs on efficient cores at a low clock.
static class Perf
{
    /// A fullscreen app (video, game, presentation) → everything is hidden & stops working.
    public static bool Fullscreen { get; private set; }

    /// Power-saving mode is active.
    public static bool PowerSaving { get; private set; }

    /// Raised (on the UI thread) when Fullscreen or PowerSaving changes.
    public static event Action? Changed;

    static DispatcherTimer? _timer;
    static bool? _eco;

    public static void Start()
    {
        if (_timer != null) return;
        UpdatePower();
        PowerManager.EnergySaverStatusChanged += (_, _) => OnUi(UpdatePower);
        PowerManager.PowerSupplyStatusChanged += (_, _) => OnUi(UpdatePower);
        PowerManager.RemainingChargePercentChanged += (_, _) => OnUi(UpdatePower);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            bool fs = Native.IsFullscreenAppActive();
            if (fs == Fullscreen) return;
            Fullscreen = fs;
            // In fullscreen, checking every 2 seconds is enough
            _timer.Interval = TimeSpan.FromSeconds(fs ? 2 : 1);
            Changed?.Invoke();
        };
        _timer.Start();
    }

    /// Update interval: 2.5× less frequent in power-saving mode.
    public static TimeSpan Scale(double seconds) => TimeSpan.FromSeconds(PowerSaving ? seconds * 2.5 : seconds);

    static void UpdatePower()
    {
        bool hasBattery = PowerManager.BatteryStatus != BatteryStatus.NotPresent;
        bool onBattery = hasBattery && PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent;
        bool saver = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
        bool saving = saver || (onBattery && PowerManager.RemainingChargePercent <= 20);

        if (_eco != onBattery)
        {
            _eco = onBattery;
            Native.SetEcoMode(onBattery);
        }
        if (saving == PowerSaving) return;
        PowerSaving = saving;
        Changed?.Invoke();
    }

    static void OnUi(Action act) =>
        Application.Current?.Dispatcher.BeginInvoke(act, DispatcherPriority.Background);
}
