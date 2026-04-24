using System;
using System.Threading;
using Ghostty.Core.Power;
using Microsoft.Extensions.Logging;
using Windows.System.Power;
using Windows.UI.ViewManagement;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Ghostty.Power;

/// <summary>
/// Production <see cref="IPowerStateMonitor"/>. Observes WinRT
/// <see cref="PowerManager"/> and <see cref="UISettings"/> events plus
/// <c>GetSystemMetrics(SM_REMOTESESSION)</c> for RDP, feeds the signals
/// through <see cref="PowerStateResolver"/>, debounces bursts, and
/// raises <see cref="LowPowerChanged"/> only when the resolved bool
/// flips.
/// </summary>
internal sealed class WindowsPowerStateMonitor : IPowerStateMonitor, IDisposable
{
    // Debounce window for coalescing bursts. A mode flip, a battery
    // unplug, and a transparency toggle triggered by the OS power
    // profile can all land within a few ms of each other; 150 ms is
    // long enough to coalesce them without being user-perceptible.
    private const int DebounceMs = 150;

    // RDP session transitions don't raise a WinRT event, and wiring a
    // WM_WTSSESSION_CHANGE hook into MainWindow would couple this
    // service to the UI layer. Polling SM_REMOTESESSION every 5 s is
    // the spec-approved fallback: session changes are rare and a
    // 5 s latency is imperceptible in the transparency/gradient
    // gating path.
    private const int RdpPollMs = 5000;

    private readonly Func<PowerSaverMode> _readMode;
    private readonly ILogger<WindowsPowerStateMonitor> _logger;
    private readonly Lock _gate = new();

    // UISettings raises change events on the DispatcherQueue of the
    // thread that constructed it. DI resolves this singleton on the UI
    // thread, which has one.
    private readonly UISettings _uiSettings = new();

    private Timer? _debounceTimer;
    private System.Threading.Timer? _rdpPollTimer;
    private bool _started;
    private bool _disposed;

    // Last-known OS signals. Mutated under _gate on the event-source
    // threads (PowerManager events fire on a thread-pool thread); read
    // under _gate by the debounce callback.
    private bool _batterySaverOn;
    private bool _onBattery;
    private bool _transparencyEffectsOff;
    private bool _remoteSession;

    public bool IsLowPowerActive { get; private set; }
    public PowerSaverTrigger ActiveTriggers { get; private set; }

    public event EventHandler? LowPowerChanged;

    public WindowsPowerStateMonitor(
        Func<PowerSaverMode> readMode,
        ILogger<WindowsPowerStateMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(readMode);
        ArgumentNullException.ThrowIfNull(logger);
        _readMode = readMode;
        _logger = logger;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;

            // Seed initial state under the lock so the first
            // ResolveAndMaybeEmit sees a consistent snapshot even if a
            // subscribed event fires immediately after we register
            // below. Taking the lock also gives the seed writes a
            // release fence paired with the reader's acquire; on ARM64
            // this is required, on x64 it's free.
            _batterySaverOn = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
            _onBattery =
                PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent &&
                PowerManager.BatteryStatus != BatteryStatus.NotPresent;
            _transparencyEffectsOff = !_uiSettings.AdvancedEffectsEnabled;
            _remoteSession = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_REMOTESESSION) != 0;
        }

        PowerManager.EnergySaverStatusChanged += OnPowerSignalChanged;
        PowerManager.BatteryStatusChanged += OnPowerSignalChanged;
        PowerManager.PowerSupplyStatusChanged += OnPowerSignalChanged;
        _uiSettings.AdvancedEffectsEnabledChanged += OnUiSettingsChanged;

        // RDP change notifications would normally come from a
        // WM_WTSSESSION_CHANGE hook on the main window. We avoid that
        // coupling by polling instead; session transitions are rare and
        // the 5-second latency is acceptable for the transparency /
        // gradient gating path.
        _rdpPollTimer = new Timer(
            _ => OnSessionChanged(),
            state: null,
            dueTime: RdpPollMs,
            period:  RdpPollMs);

        // Publish the seeded state. Consumers subscribed before Start()
        // will get a LowPowerChanged if the initial resolution is
        // active (previous IsLowPowerActive is false by default).
        ResolveAndMaybeEmit();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            _started = false;

            PowerManager.EnergySaverStatusChanged -= OnPowerSignalChanged;
            PowerManager.BatteryStatusChanged -= OnPowerSignalChanged;
            PowerManager.PowerSupplyStatusChanged -= OnPowerSignalChanged;
            _uiSettings.AdvancedEffectsEnabledChanged -= OnUiSettingsChanged;

            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _rdpPollTimer?.Dispose();
            _rdpPollTimer = null;
        }
    }

    /// <summary>
    /// Called by the Win32 message pump when it sees
    /// <c>WM_WTSSESSION_CHANGE</c>. Re-reads the RDP signal and
    /// schedules a resolve.
    /// </summary>
    public void OnSessionChanged()
    {
        lock (_gate)
        {
            _remoteSession = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_REMOTESESSION) != 0;
        }
        ScheduleResolve();
    }

    /// <summary>
    /// Called after <see cref="Ghostty.Services.IConfigService"/>
    /// raises ConfigChanged. The mode thunk will now return a
    /// potentially different value; re-resolve so the active flag
    /// reflects the new policy.
    /// </summary>
    public void OnConfigReloaded() => ScheduleResolve();

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _disposed = true;
        }
    }

    // WinRT PowerManager static events project as EventHandler<object>
    // with a nullable sender. The three signals we subscribe to share
    // this shape, so one method handles all of them.
    private void OnPowerSignalChanged(object? sender, object e)
    {
        lock (_gate)
        {
            _batterySaverOn = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
            _onBattery =
                PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent &&
                PowerManager.BatteryStatus != BatteryStatus.NotPresent;
        }
        ScheduleResolve();
    }

    private void OnUiSettingsChanged(
        UISettings sender,
        object args)
    {
        lock (_gate)
        {
            _transparencyEffectsOff = !sender.AdvancedEffectsEnabled;
        }
        ScheduleResolve();
    }

    private void ScheduleResolve()
    {
        lock (_gate)
        {
            if (!_started) return;
            // Lazy-allocate the timer so test harnesses that create a
            // monitor but never Start it don't pay the System.Threading
            // allocation. Period is infinite - we only want one shot
            // per burst.
            _debounceTimer ??= new Timer(_ => ResolveAndMaybeEmit(), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(DebounceMs, Timeout.Infinite);
        }
    }

    private void ResolveAndMaybeEmit()
    {
        bool previousActive;
        bool nextActive;
        PowerSaverTrigger nextTriggers;

        lock (_gate)
        {
            if (!_started) return; // Queued timer callback after Stop(); drop it.

            previousActive = IsLowPowerActive;
            (nextActive, nextTriggers) = PowerStateResolver.Resolve(
                mode:                   _readMode(),
                batterySaverOn:         _batterySaverOn,
                onBattery:              _onBattery,
                transparencyEffectsOff: _transparencyEffectsOff,
                remoteSession:          _remoteSession);

            IsLowPowerActive = nextActive;
            ActiveTriggers   = nextTriggers;
        }

        // Raise the event outside the lock. Subscribers run arbitrary
        // code (title-bar glyph update, renderer re-eval) and must not
        // observe us holding _gate, or a re-entrant Stop/Dispose call
        // from a handler would deadlock.
        if (nextActive != previousActive)
        {
            _logger.LogInformation(
                "Power-saver mode {State} (triggers={Triggers})",
                nextActive ? "active" : "inactive",
                nextTriggers);
            LowPowerChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
