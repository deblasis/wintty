using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Ghostty.Controls;
using Ghostty.Core;
using Ghostty.Core.Config;
using Ghostty.Core.Hosting;
using Ghostty.Core.Power;
using Ghostty.Hosting;
using Ghostty.Power;
using Ghostty.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Ghostty;

/// <summary>
/// Application entry point. Owns the single <c>ghostty_app_t</c>
/// (via the bootstrap <see cref="GhosttyHost"/>), the shared
/// <see cref="ConfigService"/>, and the process-wide window registry
/// (<see cref="WindowsByRoot"/>). Callback routing from libghostty is
/// centralized here via the static <see cref="_hostBySurface"/> map.
/// </summary>
public partial class App : Application
{
    // Process-global libghostty: bootstrap host owns the app handle; per-window hosts own only their surfaces.
    private ConfigService? _configService;
    private Ghostty.Accessibility.HighContrastMonitor? _highContrastMonitor;
    private ConfigFileEditor? _configEditor;
    private ConfigWriteScheduler? _configWriteScheduler;
    private Ghostty.Core.Notifications.NotificationService? _notificationService;
    private WindowsPowerStateMonitor? _powerStateMonitor;
    private Ghostty.Core.Profiles.DiscoveryService? _discoveryService;
    private Ghostty.Core.Profiles.ProfileRegistry? _profileRegistry;
    private Ghostty.Input.Win32ModifierKeyState? _modifierKeyState;
    private Ghostty.Core.Profiles.WindowsIconResolver? _iconResolver;
    private Ghostty.Core.Profiles.Tracking.IActiveProcessTracker? _activeProcessTracker;
    // Reverse lookup so the tracker's Changed event (which only knows the
    // root pid) can find the TabModel to route into. ConcurrentDictionary
    // because the Changed event fires from the tracker's timer thread.
    private readonly ConcurrentDictionary<int, Ghostty.Core.Tabs.TabModel> _tabsByPid = new();
    private DispatcherQueue? _uiDispatcher;
    private GhosttyHost? _bootstrapHost;
    private HostLifetimeSupervisor? _lifetimeSupervisor;
    private Microsoft.Extensions.Logging.ILoggerFactory? _loggerFactory;
    private Ghostty.Core.Logging.FileLoggerProvider? _fileLogSink;
    private Ghostty.Core.Logging.FilterState? _logFilters;
    // Bridge for libghostty's Zig std.log output. Installed after the
    // factory is built so Zig log lines emitted after bootstrap (config
    // reloads, surface / PTY spawns, render errors, transport verdicts)
    // land in the same file + ETW sinks as C# logs. The tiny window
    // before installation - covering state.init() banner lines inside
    // ConfigService's constructor - is an accepted gap; those banners
    // are one-shot startup info and the workaround would be to capture
    // into a pre-factory buffer that is then replayed.
    private Ghostty.Core.Logging.LibghosttyLogBridge? _zigLogBridge;

    // Singleton quake / drop-down window and its global hotkey owner.
    // Lifecycle is App-scoped (rather than per-window) so the chord
    // works from anywhere in the process and the hidden window stays
    // alive across regular window close/reopen cycles.
    private MainWindow? _quakeWindow;

    // App-wide singleton settings window: one for the whole process,
    // regardless of which window opened it (mirrors _quakeWindow). Its
    // dependencies are app-global, so it outlives the window that opened it.
    // UI-thread-only access.
    private Window? _settingsWindow;

    private Ghostty.Session.SessionManager? _sessionManager;
    private Ghostty.Hosting.WindowsGlobalHotKey? _quakeHotKey;
    private Ghostty.Hosting.WindowsSystemMenuHook? _systemMenuHook;
    private Ghostty.Shell.TrayIconService? _trayIconService;
    // Reentrancy guard for the Alt+Space system menu: TrackPopupMenu runs
    // a modal loop, and the keyboard hook keeps firing inside it, so a
    // repeat press would otherwise stack a second menu on top.
    private bool _systemMenuOpen;

    // Single-instance mode (opt-in via windows-single-instance). The election
    // itself lives in Program, which holds it in a static for the process
    // lifetime -- that static is what keeps the primary's mutex off the GC.
    // This field is the forwarding pipe server, which only a primary runs.
    private Ghostty.Hosting.SingleInstanceServer? _singleInstanceServer;


    // The quake chord comes from the quick-terminal-key config value
    // (read via ConfigService.QuickTerminalKeyChord). QuickTerminalKeyChord.Default
    // is Ctrl+backtick (MOD_CONTROL|MOD_NOREPEAT, VK_OEM_3) when the
    // user has not set the key explicitly.

    // Top-level window registry keyed by XamlRoot. Replaces the old
    // singular RootWindow and the earlier List<Window> draft: XamlRoot
    // is the identity every UserControl already has in hand, so
    // lookups from a TabHost or dialog code become O(1). UI-thread-
    // only access. Insert on MainWindow content Loaded (since the
    // XamlRoot is not available before then), remove on Closed.
    internal static readonly Dictionary<XamlRoot, MainWindow> WindowsByRoot = new();

    /// <summary>
    /// Live top-level window list view. Equivalent to
    /// <c>WindowsByRoot.Values</c>. Kept as a convenience for callers
    /// that want to iterate all windows without caring about lookup
    /// keys.
    /// </summary>
    internal static IEnumerable<MainWindow> AllWindows => WindowsByRoot.Values;

    /// <summary>
    /// Last regular (non-quake) window that received activation.
    /// Jump-list "New Tab in Current Window" lands here.
    /// </summary>
    internal static MainWindow? LastRegularWindow { get; private set; }

    internal static void NoteRegularWindowActivated(MainWindow window)
        => LastRegularWindow = window;

    // How many recently-closed tabs / windows the reopen stacks retain.
    private const int ClosedItemCapacity = 25;

    /// <summary>Shared, session-scoped, in-memory store of recently-closed
    /// tabs across all windows; injected into each window's TabManager.
    /// App-level so a tab closed in a window that later closes is still
    /// reopenable. Independent of the disk session persistence (which keeps
    /// one snapshot for next-launch restore).</summary>
    internal static readonly Core.Panes.ClosedStack<Core.Session.TabSession> ClosedTabs = new(ClosedItemCapacity);

    /// <summary>Shared store of recently-closed windows. Pushed by
    /// MainWindow.OnClosedAsync; drained by ReopenClosedWindow.</summary>
    internal static readonly Core.Panes.ClosedStack<Core.Session.WindowSession> ClosedWindows = new(ClosedItemCapacity);

    internal static GhosttyHost? BootstrapHost { get; private set; }
    internal static ConfigService? ConfigService { get; private set; }
    internal static Ghostty.Core.Profiles.IProfileRegistry? ProfileRegistry { get; private set; }
    internal static Ghostty.Session.SessionManager? SessionManager { get; private set; }
    internal static Ghostty.Core.Input.IModifierKeyState? ModifierKeyState { get; private set; }
    internal static Ghostty.Core.Profiles.IIconResolver? IconResolver { get; private set; }

    /// <summary>
    /// Process-wide tracker that watches each tab's shell process tree
    /// for foreground command changes. Per-window <see cref="MainWindow"/>
    /// instances enrol their <see cref="Ghostty.Core.Tabs.TabModel"/>s
    /// via <see cref="RegisterTabForProcessTracking"/> on
    /// <see cref="Ghostty.Core.Tabs.TabManager.TabAdded"/> and remove
    /// them on <see cref="Ghostty.Core.Tabs.TabManager.TabRemoved"/>.
    /// Null before OnLaunched runs; null after the last window closes.
    /// </summary>
    internal static Ghostty.Core.Profiles.Tracking.IActiveProcessTracker? ActiveProcessTracker { get; private set; }

    /// <summary>
    /// Process-wide power-saving-mode monitor. Null before OnLaunched
    /// runs; null after OnAnyWindowClosedInternal tears services down.
    /// </summary>
    internal static IPowerStateMonitor? PowerStateMonitor { get; private set; }

    /// <summary>
    /// Process-wide logger factory built at startup from Ghostty config.
    /// Null before OnLaunched runs; null after OnAnyWindowClosedInternal
    /// tears services down.
    /// </summary>
    internal static Microsoft.Extensions.Logging.ILoggerFactory? LoggerFactory { get; private set; }

    /// <summary>
    /// Process-wide debounced config write scheduler. All settings-UI
    /// writes to Windows-only keys go through here so rapid edits
    /// (slider drags, quick toggle mashing) coalesce to a single disk
    /// write per debounce window. Null before OnLaunched runs.
    /// </summary>
    internal static IConfigWriteScheduler? ConfigWriteScheduler { get; private set; }

    /// <summary>
    /// Shared ConfigFileEditor wrapping the user's ghostty config
    /// file. Settings pages read-modify-write through this; the
    /// Closed handler flushes and disposes after the last window
    /// shuts.
    /// </summary>
    internal static IConfigFileEditor? ConfigFileEditor { get; private set; }

    /// <summary>
    /// App-wide queue of transient in-window notices. Each window's
    /// NotificationHost binds to it; features raise notices through it without
    /// touching XAML.
    /// </summary>
    internal static Ghostty.Core.Notifications.INotificationService? NotificationService { get; private set; }

    internal static HostLifetimeSupervisor? LifetimeSupervisor { get; private set; }

    // Process-wide callback routing: surface handle -> per-window host.
    // Inserted/removed by GhosttyHost.Register/Unregister/Adopt/Detach.
    // Consulted by the bootstrap host's libghostty callbacks to forward
    // to whichever per-window host currently owns the surface.
    //
    // ConcurrentDictionary because bootstrap host's libghostty callbacks
    // (OnCloseSurface, OnWakeup, OnAction, OnReadClipboard, OnConfirmReadClipboard,
    // OnWriteClipboard) may be invoked from libghostty's thread and consult
    // this map before dispatcher-hopping. Once the owning host is found,
    // the callback hops to that host's dispatcher for any UI work.
    private static readonly ConcurrentDictionary<IntPtr, GhosttyHost> _hostBySurface = new();

    internal static int HostBySurfaceCount => _hostBySurface.Count;

    internal static void RegisterSurfaceRoute(IntPtr handle, GhosttyHost host)
        => _hostBySurface[handle] = host;

    internal static void UnregisterSurfaceRoute(IntPtr handle, GhosttyHost host)
    {
        // Only remove if we still own this entry. Guards against a
        // double-adopt path where the target host already overwrote.
        ((ICollection<KeyValuePair<IntPtr, GhosttyHost>>)_hostBySurface)
            .Remove(new KeyValuePair<IntPtr, GhosttyHost>(handle, host));
    }

    internal static bool TryGetHostForSurface(IntPtr handle, out GhosttyHost? host)
    {
        if (_hostBySurface.TryGetValue(handle, out var h)) { host = h; return true; }
        host = null;
        return false;
    }

    /// <summary>
    /// Search for a <see cref="TerminalControl"/> across all per-window
    /// hosts. Used by <see cref="GhosttyHost.IsRegistered"/> when the
    /// bootstrap host's own dictionary misses (the control may have
    /// moved to a different window's host).
    /// </summary>
    internal static bool TryFindHostForControl(TerminalControl control, [NotNullWhen(true)] out GhosttyHost? host)
    {
        foreach (var candidate in _hostBySurface.Values.Distinct())
        {
            if (candidate.ContainsControl(control))
            {
                host = candidate;
                return true;
            }
        }
        host = null;
        return false;
    }

    internal static void UnregisterHostSurfaces(GhosttyHost host)
    {
        // Drain every entry whose value equals `host`. Called from
        // GhosttyHost.Dispose to clean up routing without requiring the
        // host to remember every handle it ever saw. Snapshot the keys
        // first so we do not mutate the dictionary while enumerating it.
        foreach (var kv in _hostBySurface.ToArray())
        {
            if (ReferenceEquals(kv.Value, host))
            {
                ((ICollection<KeyValuePair<IntPtr, GhosttyHost>>)_hostBySurface)
                    .Remove(kv);
            }
        }
    }

    // Deliberately no static constructor registering a DllImport resolver:
    // registering an assembly twice throws. See Program.RegisterNativeResolver,
    // which owns the single registration for every entry path.

    public App()
    {
        // Match the OS theme before any XAML parses so the first paint
        // of every window is already in the right mode. Without this,
        // App.xaml's static RequestedTheme (previously "Dark") drew the
        // first frame of Settings / Raw Editor in dark mode even when
        // the user is on a light system, producing a visible flash when
        // WindowThemeManager later switched to Light. Application.
        // RequestedTheme is only settable before the first window is
        // created; setting it here is the one safe window.
        try
        {
            RequestedTheme = Ghostty.Services.OsTheme.IsDark()
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // UISettings can throw in certain packaged / sandboxed
            // startup edges. Fall back to the pre-existing default so
            // the app still launches; users on a system whose theme
            // doesn't match will see the old one-frame flash.
            //
            // Unhandled-exception handlers aren't wired up yet (done
            // below), so Debug.WriteLine is the most signal we can
            // surface to a devenv-attached run without taking the app
            // down. A packaged Release launch will lose this -- that's
            // acceptable for a one-frame cosmetic flash.
            System.Diagnostics.Debug.WriteLine(
                $"{AppIdentity.LogTag} OsTheme.IsDark() threw during App ctor; falling back to Dark. {ex.GetType().Name}: {ex.Message}");
            RequestedTheme = ApplicationTheme.Dark;
        }

        InitializeComponent();

        // Surface unhandled exceptions to stderr AND to a file under
        // %LOCALAPPDATA%\Wintty\ before the process dies. Without
        // this, a managed exception on the UI thread silently exits
        // with a non-descriptive code and we have nothing to debug
        // from -- especially in Release, where WER captures a dump
        // but the user is left without a human-readable pointer to
        // it. The file path is stable across Debug and Release so
        // the same path works for dev debugging and for a user who
        // needs to attach logs to a bug report.
        UnhandledException += (s, e) =>
        {
            // Before the log, not after: this tears the process down without
            // unwinding back through Application.Start, so StartGui's catch
            // never runs and this is the only chance to take the splash down.
            // An exception in OnLaunched is exactly when a splash is still up.
            Ghostty.Shell.SplashWindow.HideNow();
            LogUnhandled("UI-THREAD UNHANDLED", e.Exception.ToString());
            // Leave Handled=false so the runtime still tears the app
            // down -- we just wanted to record the exception first.
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Ghostty.Shell.SplashWindow.HideNow();
            LogUnhandled("APPDOMAIN UNHANDLED", e.ExceptionObject?.ToString() ?? "(null)");
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogUnhandled("UNOBSERVED TASK", e.Exception.ToString());
        };
    }

    private static void LogUnhandled(string tag, string detail)
    {
        // stderr mirror for terminal launches (Program.Main's
        // FreeConsole gate keeps the console attached in that case).
        try
        {
            Console.Error.WriteLine($"{AppIdentity.LogTag} {tag}:");
            Console.Error.WriteLine(detail);
            Console.Error.Flush();
        }
        catch { /* logging must not throw */ }

        // File log for GUI launches and packaged releases where there
        // is no readable console. Append so repeated crashes during
        // one session accumulate into one file.
        //
        // Three handlers (UI thread, AppDomain, TaskScheduler) can
        // fire on three different threads in quick succession during
        // a cascading crash; serialize the write or they race on the
        // file open and at least one `AppendAllText` throws an
        // `IOException`. A dead crash logger silently swallowing the
        // exception we were trying to record is exactly the failure
        // mode this whole helper was built to prevent.
        //
        // LocalApplicationData is a per-user folder. For packaged
        // (MSIX) builds Windows virtualizes this to the package's
        // private app-data directory; the file still lands somewhere
        // the user can find via the Settings app, just not the literal
        // `%LOCALAPPDATA%\Wintty\`.
        try
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(localAppData, AppIdentity.StateDirName);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            lock (_crashLogLock)
            {
                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.UtcNow:O} [{tag}]\n{detail}\n\n");
            }
        }
        catch { /* logging must not throw */ }
    }

    private static readonly object _crashLogLock = new();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Set the explicit AppUserModelID. This MUST happen before
        // any shell interop call (jump list registration, taskbar
        // icon operations, toast notifications).
        const string AppUserModelId = Ghostty.Core.AppIdentity.AumId;
        try
        {
            Windows.Win32.PInvoke.SetCurrentProcessExplicitAppUserModelID(AppUserModelId)
                .ThrowOnFailure();
        }
        catch (System.Exception ex)
        {
            // StaticLoggers.App is NullLogger until Initialize(factory)
            // runs further down in OnLaunched; AUMID + jump-list both
            // run before the factory exists (AUMID must be set before
            // any shell interop per the MSDN contract above), so these
            // warnings are silently dropped until the factory is built.
            // Same behavior as the pre-migration trace-only path which
            // only wrote to the IDE output window.
            Ghostty.Logging.StaticLoggers.App.LogAumidFailed(ex);
        }

        // Tasks first so the list exists before windows; rebuilt again
        // once ProfileRegistry is live so pinned profiles appear.
        RebuildJumpList();

        // Register for toast notifications. Unpackaged apps must call
        // Register() so AppNotificationManager wires up the COM activator
        // under the AUMID before any Show(); without it Show() throws. The
        // registration persists in the registry (we never Unregister) so the
        // app can be toast-activated later. AUMID is already set above.
        // NotificationInvoked MUST be attached before Register(), not after,
        // for two separate reasons. WinAppSDK throws ERROR_NOT_FOUND from a
        // subscribe that arrives late, and Register() picks its COM
        // class-registration flag from whether a handler exists at that
        // instant: with none attached it registers single-use, so a toast
        // click would spawn a SECOND process instead of reaching this one.
        //
        // Its own try, not folded in with Register() below: a throw from the
        // subscribe would otherwise skip the registration too, costing every
        // toast rather than only the click routing.
        try
        {
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default
                .NotificationInvoked += OnToastNotificationInvoked;
            _toastInvokedSubscribed = true;
        }
        catch (System.Exception ex)
        {
            Ghostty.Logging.StaticLoggers.App.LogToastRegisterFailed(ex);
        }

        // Exactly one Register() call may exist in the process.
        try
        {
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Register();
        }
        catch (System.Exception ex)
        {
            Ghostty.Logging.StaticLoggers.App.LogToastRegisterFailed(ex);
        }

        // Read the activation this process was started for, before the
        // single-instance gate below acts on it. Position is load-bearing: a
        // secondary forwards its launch and exits at that gate, so anything
        // probed after it never reaches a secondary at all.
        var activationUri = ProbeActivation();

        _configService = new ConfigService(DispatcherQueue.GetForCurrentThread());
        ConfigService = _configService;

        // build the factory from Ghostty config before any other service constructs an
        // ILogger<T>. Log directory under the same %LOCALAPPDATA%\Wintty root that
        // App.LogUnhandled already uses for crash.log, so a user reporting a bug only has
        // one folder to attach.
        var logDir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            Ghostty.Core.AppIdentity.StateDirName, "logs");
        var (factory, fileSink, filters) = Ghostty.Core.Logging.LoggingBootstrap.Build(
            logLevel: _configService.LogLevel,
            logFilter: _configService.LogFilter,
            fileLogDirectory: logDir);
        _loggerFactory = factory;
        _fileLogSink = fileSink;
        _logFilters = filters;
        LoggerFactory = factory;
        _configService.ConfigChanged += OnConfigChanged_ApplyLogFilters;

        // Install the libghostty log bridge now that the factory
        // exists. After this point every Zig std.log call is delivered
        // to an ILogger under category "Ghostty.Zig.<scope>".
        _zigLogBridge = new Ghostty.Core.Logging.LibghosttyLogBridge(
            factory, new Ghostty.Logging.LibghosttyLogInstaller());
        _zigLogBridge.Install();

        // populate Core-side static logger accessors for types whose call sites are
        // static (e.g., FrecencyStore static methods that can't take a ctor-injected
        // logger).
        Ghostty.Core.Logging.CoreStaticLoggers.Initialize(factory);

        // populate Ghostty-project static logger accessors for types that construct
        // before ctor-injection is possible (e.g., ConfigService is built above BEFORE
        // the factory exists, and cannot receive a logger through its ctor) and for call
        // sites inside static scopes.
        Ghostty.Logging.StaticLoggers.Initialize(factory);

        // App-wide notice queue. Constructed before the NO_COLOR check (its
        // first customer) and before any window, so a startup notice is already
        // in the collection when the first NotificationHost binds.
        _notificationService = new Ghostty.Core.Notifications.NotificationService();
        NotificationService = _notificationService;

        // NO_COLOR handling. NO_COLOR (https://no-color.org) is a user-facing
        // convention telling color-aware programs to disable ANSI color; a
        // terminal normally passes it through untouched. PowerShell 7.2+ obeys
        // it by flipping $PSStyle.OutputRendering to PlainText, which drops
        // color from everything it renders -- including a powerline prompt's
        // background segments. We HONOR it by default (per the standard), but in
        // the default "notify" mode we surface a one-time notice explaining the
        // monochrome output and offering to enable color -- which strips
        // NO_COLOR from this process's environment so tabs opened afterward
        // inherit a color-capable env (libghostty snapshots it per surface via
        // getEnvMap). "strip" enables color unconditionally at launch; "keep"
        // honors NO_COLOR silently.
        {
            var noColorLog = factory.CreateLogger("Ghostty.NoColor");

            void RemoveNoColorFromEnv()
            {
                Environment.SetEnvironmentVariable("NO_COLOR", null);
                noColorLog.LogInformation(
                    "Removed NO_COLOR from the environment so terminal colors work.");
            }

            // Persist a resolved preference so the notice does not recur. Logs
            // rather than silently dropping it if the scheduler is unavailable
            // (it is constructed just below and torn down only at shutdown, so
            // this is defensive).
            void PersistNoColorMode(string mode)
            {
                if (ConfigWriteScheduler is { } scheduler)
                    scheduler.Schedule("no-color-override", mode);
                else
                    noColorLog.LogWarning(
                        "Could not persist no-color-override={Mode}: config write scheduler unavailable.",
                        mode);
            }

            var noColorNotice = Ghostty.Core.Env.NoColorStartup.Resolve(
                present: Environment.GetEnvironmentVariable("NO_COLOR") is not null,
                overrideMode: _configService.NoColorOverride,
                removeFromEnv: RemoveNoColorFromEnv,
                persistMode: PersistNoColorMode);
            if (noColorNotice is not null) _notificationService.Show(noColorNotice);
        }

        // Single-instance gate. Acted on here -- after the logger factory
        // exists (so failures are visible in Release), but before the
        // bootstrap host, window, and DX12 renderer are created -- so a
        // secondary process forwards its launch and exits without ever
        // creating a window or paying for the renderer.
        HandleSingleInstanceGate(Program.SingleInstance);

        // Power-saving monitor. Reads power-saver-mode from config every
        // time it resolves (Func thunk decouples it from ConfigService
        // lifetime). Must be constructed on the UI thread so its
        // UISettings field gets a live DispatcherQueue for change events.
        _powerStateMonitor = new WindowsPowerStateMonitor(
            readMode: () =>
            {
                var raw = _configService?.GetRawFileValue("power-saver-mode") ?? "auto";
                return raw.Trim().ToLowerInvariant() switch
                {
                    "always" => PowerSaverMode.Always,
                    "never"  => PowerSaverMode.Never,
                    _        => PowerSaverMode.Auto,
                };
            },
            logger: factory.CreateLogger<WindowsPowerStateMonitor>());
        PowerStateMonitor = _powerStateMonitor;

        // Re-resolve whenever the user edits power-saver-mode (or any
        // other key -- cheap, and keeps this out of the reload path's
        // critical section). Named handler so we can detach symmetrically
        // at shutdown (the rest of this codebase detaches every event
        // subscription explicitly; anonymous lambda breaks that pattern).
        _configService.ConfigChanged += OnConfigChanged_NotifyPowerMonitor;

        _powerStateMonitor.Start();

        // One editor + one scheduler per process. Keeping them here
        // (instead of per-settings-window) means rapid edits coalesce
        // across window lifetimes and the file watcher sees a single
        // batched write rather than a burst. The 150ms debounce is
        // short enough that toggle clicks still feel instant when
        // committed, long enough to absorb a slider drag.
        _configEditor = new ConfigFileEditor(_configService.ConfigFilePath);
        ConfigFileEditor = _configEditor;

        var uiDispatcher = DispatcherQueue.GetForCurrentThread();
        _configWriteScheduler = new ConfigWriteScheduler(
            _configEditor,
            new SystemSchedulerTimer(factory.CreateLogger<SystemSchedulerTimer>()),
            debounce: TimeSpan.FromMilliseconds(150),
            onFlushed: () =>
            {
                // Scheduler fires on a threadpool thread. Reload()
                // raises ConfigChanged on the UI thread, so marshal
                // back; suppress the watcher so our own write does
                // not trigger a spurious second reload on top of the
                // one we explicitly request.
                //
                // Dispose() explicitly passes signal:false so the
                // common shutdown path never lands here, but a timer
                // callback that fires concurrently with Dispose (the
                // tail race) can still enqueue after _configService
                // is nulled in the shutdown finally. Re-read the
                // field on the UI thread and bail if shutdown won.
                uiDispatcher.TryEnqueue(() =>
                {
                    var cs = _configService;
                    if (cs is null) return;
                    cs.SuppressWatcher(true);
                    try { cs.Reload(); }
                    finally { cs.SuppressWatcher(false); }
                });
            },
            logger: factory.CreateLogger<ConfigWriteScheduler>());
        ConfigWriteScheduler = _configWriteScheduler;

        // Profiles discovery + composition. No UI consumer lands yet,
        // but the registry bootstraps here so future settings-UI and
        // command-palette consumers can plug in without touching this
        // file again.
        var discoveryCachePath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            Ghostty.Core.AppIdentity.StateDirName, "DiscoveryCache", "v2.json");
        var winttyVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "dev";

        var processRunner = new Ghostty.Core.Profiles.WindowsProcessRunner();
        var registryReader = new Ghostty.Core.Profiles.WindowsRegistryReader();
        var fileSystem = new Ghostty.Core.Profiles.WindowsFileSystem();

        var probes = new Ghostty.Core.Profiles.IInstalledShellProbe[]
        {
            new Ghostty.Core.Profiles.Probes.CmdProbe(fileSystem),
            new Ghostty.Core.Profiles.Probes.PowerShellProbe(fileSystem, processRunner),
            new Ghostty.Core.Profiles.Probes.WslProbe(processRunner),
            new Ghostty.Core.Profiles.Probes.GitBashProbe(registryReader, fileSystem),
            new Ghostty.Core.Profiles.Probes.Msys2Probe(fileSystem),
            new Ghostty.Core.Profiles.Probes.AzureCloudShellProbe(processRunner),
        };

        _discoveryService = new Ghostty.Core.Profiles.DiscoveryService(
            probes, fileSystem, Ghostty.Core.Logging.SystemClock.Instance,
            winttyVersion, discoveryCachePath,
            factory.CreateLogger<Ghostty.Core.Profiles.DiscoveryService>());

        _modifierKeyState = new Ghostty.Input.Win32ModifierKeyState();
        ModifierKeyState = _modifierKeyState;

        _iconResolver = new Ghostty.Core.Profiles.WindowsIconResolver(fileSystem);
        IconResolver = _iconResolver;

        // Process-wide bytes cache for the tab strip's IValueConverter.
        // The converter is synchronous (XAML binding contract); the cache
        // memoizes the first resolve so subsequent reads do not block the
        // UI thread.
        Ghostty.Tabs.TabIconBytesCache.Install(_iconResolver);

        // Cache the UI dispatcher up front: the active-process tracker
        // fires Changed from a Timer threadpool callback, and the handler
        // touches TabIconViewModel which raises PropertyChanged consumed
        // by WinUI bindings -- those need the UI thread.
        _uiDispatcher = uiDispatcher;
        _activeProcessTracker = new Ghostty.Core.Profiles.Tracking.WindowsActiveProcessTracker();
        _activeProcessTracker.Changed += OnActiveProcessChanged;
        ActiveProcessTracker = _activeProcessTracker;

        _profileRegistry = new Ghostty.Core.Profiles.ProfileRegistry(
            source: _configService,
            discover: (bypass, ct) => _discoveryService.DiscoverAsync(bypass, ct),
            dispatcher: action => uiDispatcher.TryEnqueue(() => action()),
            log: factory.CreateLogger<Ghostty.Core.Profiles.ProfileRegistry>());
        ProfileRegistry = _profileRegistry;
        _profileRegistry.ProfilesChanged += OnProfilesChangedRebuildJumpList;
        RebuildJumpList();

        // One-shot migration of the legacy ui-settings.json into the
        // real config + a placement-only window-state.json. Runs
        // before the first window opens so MainWindow's initial reads
        // of VerticalTabs / CommandPalette* see the migrated values.
        // No-op after the first successful run (detects the new file).
        Ghostty.Settings.WindowStateMigration.TryRun(_configService, _configEditor);

        // One supervisor per process. Threads lifecycle invariants
        // through every host that ever lives, including the bootstrap.
        _lifetimeSupervisor = new HostLifetimeSupervisor();
        LifetimeSupervisor = _lifetimeSupervisor;

        // Build the bootstrap host. This is the one host that owns the
        // ghostty_app_t (via the legacy ctor's AppNew call) and the one
        // host libghostty invokes. Its callback bodies consult
        // _hostBySurface to forward to whichever per-window host owns
        // the target surface.

        _bootstrapHost = new GhosttyHost(
            DispatcherQueue.GetForCurrentThread(),
            _configService.ConfigHandle,
            _lifetimeSupervisor,
            factory);
        BootstrapHost = _bootstrapHost;
        _configService.SetApp(_bootstrapHost.App);

        // App-level: High Contrast is a system-wide state and config is
        // applied app-wide, so a single monitor drives the surface override.
        // Constructed AFTER SetApp so its initial Apply() reloads into a live
        // app -- before SetApp, ConfigService.Reload() bails at the
        // _app.Handle==Zero guard and the override would never apply. Placed
        // before window creation so AppUpdateConfig lands before any surface
        // renders (no flash of the user's colors when HC is already on).
        _highContrastMonitor = new Ghostty.Accessibility.HighContrastMonitor(
            _configService, DispatcherQueue.GetForCurrentThread());

        // Session manager: owns restore decision + debounced persistence.
        // Constructed before window creation so we can decide whether to
        // rebuild a saved session or open a single default window.
        _sessionManager = new Ghostty.Session.SessionManager(
            new Ghostty.Session.SessionStore(
                factory.CreateLogger<Ghostty.Session.SessionStore>()),
            _configService,
            DispatcherQueue.GetForCurrentThread(),
            () => AllWindows);
        SessionManager = _sessionManager;

        // Jump-list argv on cold start (app not running) and on a
        // secondary forward failure both land here. Session restore must
        // not win over an explicit task/profile click.
        var coldLaunch = Ghostty.Core.JumpList.JumpListLaunch.Parse(
            Environment.GetCommandLineArgs());
        var honorJumpList = coldLaunch.Action != Ghostty.Core.JumpList.JumpListAction.None;

        var restoreState = honorJumpList ? null : _sessionManager.LoadForRestore();
        if (restoreState is { Windows.Count: > 0 })
        {
            // Only the first restored window drives the splash. There is one
            // splash per process, so arming every window would have them
            // fight over it: each Track would drag it onto the newest
            // window, uncovering the earlier ones, and whichever window
            // rendered first would dismiss it for all of them.
            var isFirstWindow = true;
            foreach (var ws in restoreState.Windows)
            {
                var restored = new MainWindow(
                    _configService, _bootstrapHost, _lifetimeSupervisor, factory, ws,
                    showLaunchIcon: isFirstWindow);
                isFirstWindow = false;
                restored.Closed += OnAnyWindowClosedInternal;
                _sessionManager.Track(restored);
                restored.Activate();
            }
        }
        else if (honorJumpList)
        {
            HandleColdStartJumpList(coldLaunch);
        }
        else
        {
            var window = new MainWindow(
                _configService, _bootstrapHost, _lifetimeSupervisor, factory,
                showLaunchIcon: true);
            window.Closed += OnAnyWindowClosedInternal;
            _sessionManager.Track(window);
            window.Activate();
        }

        // Singleton quake / drop-down window. Created hidden; summoned
        // by the global hotkey via WindowsGlobalHotKey. Same MainWindow
        // class as a regular window, just with IsQuickTerminal = true
        // for the no-taskbar / no-AltTab / close-hides behaviour.
        _quakeWindow = new MainWindow(
            _configService, _bootstrapHost, _lifetimeSupervisor, factory,
            isQuickTerminal: true);
        _quakeWindow.Closed += OnAnyWindowClosedInternal;
        _quakeWindow.Activate();          // creates the HWND
        _quakeWindow.AppWindow.Hide();    // immediately hide

        // Chord comes from quick-terminal-key config (Default = Ctrl+`).
        // MOD_NOREPEAT prevents auto-fire while the user holds the chord.
        _quakeHotKey = new Ghostty.Hosting.WindowsGlobalHotKey(
            DispatcherQueue.GetForCurrentThread(),
            factory.CreateLogger<Ghostty.Hosting.WindowsGlobalHotKey>());
        _quakeHotKey.Pressed += (_, _) => ToggleQuickTerminal();

        // Alt+Space opens the window system menu (Move / Size / Close...).
        // WinUI's input pre-translate consumes the Alt+Space key-down
        // before any window proc sees it, so a thread keyboard hook is the
        // only place to catch the chord. Scoped to this UI thread and torn
        // down on shutdown alongside the quake hotkey.
        _systemMenuHook = new Ghostty.Hosting.WindowsSystemMenuHook(
            DispatcherQueue.GetForCurrentThread(),
            hwnd =>
            {
                if (_systemMenuOpen) return;
                _systemMenuOpen = true;
                try { Ghostty.Branding.SystemMenuPopup.ShowForWindow(hwnd); }
                finally { _systemMenuOpen = false; }
            });
        _systemMenuHook.Enable();
        RegisterQuakeHotKey();

        try
        {
            _trayIconService = new Ghostty.Shell.TrayIconService(
                DispatcherQueue.GetForCurrentThread(),
                ShowOrFocusWindowsFromTray,
                CloseAllWindows);
        }
        catch (System.Exception ex)
        {
            Ghostty.Logging.StaticLoggers.App.LogTrayInitFailed(ex);
        }

        // Re-claim the chord whenever the config changes so an edited
        // quick-terminal-key takes effect without a restart.
        _configService.ConfigChanged += OnConfigReloaded_ReRegisterHotKey;

        // Subscribe to toast clicks down here, not next to Register(): the
        // handler focuses windows, and up there none exist yet. A click that
        // already arrived (the probe latches one, and a cold launch also
        // delivers it during Register()) is replayed to this subscription, so
        // nothing is lost by waiting. This must stay the FIRST subscriber --
        // see ToastActivationRelay on why a second one wired above this line
        // would swallow every cold-launch click.
        ToastActivations.Subscribe(OnToastActivated);

        // Start the single-instance forwarding server last, once the UI
        // dispatcher and logger factory exist. No-op unless this process is
        // the single-instance primary.
        StartSingleInstanceServer();
    }

    /// <summary>
    /// Act on the single-instance election Program held before
    /// <c>Application.Start</c>. A secondary hands its launch to the running
    /// primary and exits; every other role continues into a normal launch.
    /// </summary>
    private void HandleSingleInstanceGate(
        Ghostty.Core.SingleInstance.SingleInstanceElection? election)
    {
        // Null only on a path that never ran Program.StartGui, which OnLaunched
        // is not reachable from. Treated as "off", the degradation that cannot
        // cost the user a window.
        if (election is null) return;

        // Every role spelled out, including the two that do nothing: a role
        // added later should be a visible gap here rather than a silent
        // fallthrough into "launch normally".
        switch (election.Role)
        {
            case Ghostty.Core.SingleInstance.SingleInstanceRole.Disabled:
                break;

            case Ghostty.Core.SingleInstance.SingleInstanceRole.Primary:
                // The server starts at the end of OnLaunched, once the UI
                // dispatcher exists (see StartSingleInstanceServer).
                break;

            case Ghostty.Core.SingleInstance.SingleInstanceRole.Failed:
                // Reported here rather than where it happened: the election
                // runs before there is a logger factory. Launching normally is
                // worse coordination, never a lost window.
                Ghostty.Logging.StaticLoggers.App.LogSingleInstanceMutexFailed(
                    election.Failure!);
                break;

            case Ghostty.Core.SingleInstance.SingleInstanceRole.Secondary:
                ForwardLaunchToPrimary(election.Names.Pipe);
                break;
        }
    }

    /// <summary>
    /// Hand this launch to the running primary and exit the process. Returns
    /// normally instead when the forward failed, so the caller continues into
    /// an ordinary independent launch rather than dropping the user's launch.
    /// </summary>
    private void ForwardLaunchToPrimary(string pipeName)
    {
        // A toast click can be what started this process, and argv alone does
        // not say so in any form the primary can read -- the activator's own
        // token is a WinAppSDK implementation detail. Append the surface the
        // probe latched so the primary acts on the click instead of reading it
        // as a bare launch and opening a window. ForwardedArgv also strips any
        // marker the user's own command line carried, so the one the primary
        // finds is the one this process put there.
        var argv = Ghostty.Core.Activation.ToastActivation.ForwardedArgv(
            Environment.GetCommandLineArgs(), ToastActivations.Pending);

        var request = new Ghostty.Core.SingleInstance.LaunchRequest(
            Program.LaunchWorkingDirectory, argv);

        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(
                ".", pipeName, System.IO.Pipes.PipeDirection.Out);
            client.Connect(2000); // 2s: primary should answer promptly
            using var writer = new System.IO.StreamWriter(client) { AutoFlush = true };
            writer.Write(request.Serialize());
            writer.Flush();
            client.WaitForPipeDrain();
        }
        catch (Exception ex)
        {
            // Primary may be mid-shutdown or the pipe is wedged. Fall back to
            // launching normally rather than dropping the user's launch. This
            // process does not take the session over; it did not create the
            // mutex, and the next launch elects a primary again.
            Ghostty.Logging.StaticLoggers.App.LogSingleInstanceForwardFailed(ex);
            return;
        }

        // Deliberately outside the try above. Once the drain returns, the
        // primary has the request and will act on it, so nothing here may
        // divert back into a normal startup: a throw from Dispose inside that
        // try used to log a forward failure and open a second window for a
        // launch already on its way.
        //
        // Dispose rather than leaving it to teardown, so the config file
        // watcher and the native config handle go now.
        _configService?.Dispose();
        Environment.Exit(0);
    }

    /// <summary>
    /// Start the forwarding pipe server. Called near the end of OnLaunched
    /// (after _uiDispatcher is set) and only on the single-instance primary.
    /// </summary>
    private void StartSingleInstanceServer()
    {
        // The election's own name, not a fresh derivation of it. Deriving it
        // twice is how the identity gets two answers.
        if (Program.SingleInstance is not
            { Role: Ghostty.Core.SingleInstance.SingleInstanceRole.Primary } election) return;
        if (_loggerFactory is null) return;

        try
        {
            _singleInstanceServer = new Ghostty.Hosting.SingleInstanceServer(
                election.Names.Pipe,
                req => _uiDispatcher?.TryEnqueue(() => OpenWindowFromLaunch(req)),
                _loggerFactory.CreateLogger<Ghostty.Hosting.SingleInstanceServer>());
            _singleInstanceServer.Start();
        }
        catch (Exception ex)
        {
            // A primary that cannot serve simply behaves like a normal
            // window; secondaries will fail to connect and fall back to
            // independent launches.
            Ghostty.Logging.StaticLoggers.App.LogSingleInstanceServerStartFailed(ex);
            _singleInstanceServer = null;
        }
    }

    /// <summary>
    /// Open a new top-level window or tab for a launch forwarded from a
    /// secondary instance (single-instance mode) or a jump-list click.
    /// Seeded with the forwarded working directory. Runs on the UI thread.
    /// Mirrors MainWindow.OpenInNewWindow's wiring, including session Track.
    /// </summary>
    internal void OpenWindowFromLaunch(Ghostty.Core.SingleInstance.LaunchRequest req)
    {
        if (_configService is null || _bootstrapHost is null
            || _lifetimeSupervisor is null || _loggerFactory is null)
            return;

        // A notification click that spawned a secondary lands here carrying the
        // surface it was raised for. Liveness is checked BEFORE handing it to
        // the relay, because only this call site can fall through to an
        // ordinary launch: a marker naming a surface that is not here is a
        // pane that closed, or a flag a user typed on a command line an older
        // secondary forwarded verbatim, and neither may cost them the window
        // (or the --jumplist-action) they actually asked for.
        var activation = Ghostty.Core.Activation.ToastActivation
            .FromForwardedArgs(req.Args);
        if (activation.SurfaceKey is { Length: > 0 } key
            && AnyWindowHasToastSurface(key))
        {
            ToastActivations.Note(activation);
            return;
        }

        HandleJumpListLaunch(
            Ghostty.Core.JumpList.JumpListLaunch.Parse(req.Args),
            req.WorkingDirectory);
    }

    // Toast activation ---------------------------------------------------

    // Whether the NotificationInvoked subscribe succeeded, so teardown only
    // detaches what it actually attached.
    private bool _toastInvokedSubscribed;

    // The relay is where the awkward part lives (latch a click that arrives
    // before anyone can act on it, replay it to the first subscriber, fan out
    // afterwards). Static and process-lifetime because the WinRT handler is
    // wired at the very top of OnLaunched, before the instance state any
    // consumer needs exists. App keeps only the WinRT wiring.
    internal static Ghostty.Core.Activation.ToastActivationRelay ToastActivations { get; }
        = new(ex => Ghostty.Logging.StaticLoggers.App.LogToastActivationFailed(ex));

    /// <summary>
    /// Read what this process was activated for. Both kinds come off the same
    /// <c>GetActivatedEventArgs</c> call, which is the only synchronous
    /// account of the activation available: the AppNotification kind is
    /// already present when OnLaunched runs, whereas NotificationInvoked fires
    /// whenever WinAppSDK decides to dispatch it. Reading both means the
    /// single-instance forward does not depend on that timing.
    ///
    /// Returns the protocol URI, if any. The toast half is latched into the
    /// relay rather than returned, because its consumer is constructed much
    /// later in startup.
    /// </summary>
    private static Uri? ProbeActivation()
    {
        Uri? protocolUri = null;
        try
        {
            var activated = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            switch (activated.Kind)
            {
                case Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol
                    when activated.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs proto:
                    protocolUri = proto.Uri;
                    break;

                case Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification
                    when activated.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs toast:
                    ToastActivations.Note(
                        Ghostty.Core.Activation.ToastActivation.FromNotificationArguments(
                            toast.Arguments));
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[app] activation probe failed: {ex.Message}");
        }

        // The unpackaged fallback runs OUTSIDE the try above, deliberately.
        // GetActivatedEventArgs is the half that throws on an unpackaged
        // build, which is the exact case the --uri scan exists to cover:
        // nested inside that try it was unreachable precisely when it was
        // needed. Resolve keeps the precedence rule in one testable place --
        // a real protocol activation still beats anything in argv.
        return Ghostty.Core.Activation.ProtocolLaunch.Resolve(
            protocolUri, Environment.GetCommandLineArgs());
    }

    private void OnToastNotificationInvoked(
        Microsoft.Windows.AppNotifications.AppNotificationManager sender,
        Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs args)
    {
        try
        {
            ToastActivations.Note(
                Ghostty.Core.Activation.ToastActivation.FromNotificationArguments(
                    args.Arguments));
        }
        catch (System.Exception ex)
        {
            // This is a WinRT COM callback. A managed exception escaping back
            // into the activator kills the process from inside code the user
            // has no way to relate to the notification they clicked.
            Ghostty.Logging.StaticLoggers.App.LogToastActivationFailed(ex);
        }
    }

    /// <summary>
    /// The one in-repo consumer of <see cref="ToastActivations"/>: put the
    /// user back on the pane whose toast they clicked.
    /// </summary>
    private void OnToastActivated(Ghostty.Core.Activation.ToastActivation activation)
    {
        // Everything below touches windows, and this can arrive on a COM
        // callback thread. TryEnqueue is right for the replay path too, which
        // already runs on the UI thread: one dispatcher tick later is fine,
        // and it keeps a single ordering rule instead of two.
        _uiDispatcher?.TryEnqueue(() =>
        {
            var focused = false;
            try
            {
                focused = activation.SurfaceKey is { Length: > 0 } key
                    && TryFocusToastSurface(key);
            }
            catch (System.Exception ex)
            {
                Ghostty.Logging.StaticLoggers.App.LogToastActivationFailed(ex);
            }

            if (focused) return;

            // Outside the try above on purpose. The surface may be gone (its
            // pane or window closed, or the toast outlived the process that
            // raised it -- every cold launch lands here), and the scan itself
            // may have failed; either way the promise a notification click
            // makes is that the app comes forward, so this has to run even
            // when the scan threw.
            try
            {
                ShowOrFocusWindowsFromTray();
            }
            catch (System.Exception ex)
            {
                Ghostty.Logging.StaticLoggers.App.LogToastActivationFailed(ex);
            }
        });
    }

    /// <summary>
    /// Whether any live pane carries <paramref name="surfaceKey"/>. Read-only:
    /// the forwarded-launch path has to know whether a click can be honoured
    /// BEFORE it commits to honouring it, so a marker naming nothing falls
    /// through to an ordinary launch instead of eating it.
    /// </summary>
    private static bool AnyWindowHasToastSurface(string surfaceKey)
    {
        foreach (var window in AllWindows.ToArray())
        {
            if (window.HasToastSurface(surfaceKey)) return true;
        }

        return false;
    }

    /// <summary>
    /// Find the window owning the surface a toast was raised for, reveal it,
    /// select its tab and focus the pane. False when no live surface carries
    /// the key.
    /// </summary>
    private bool TryFocusToastSurface(string surfaceKey)
    {
        // Snapshot: revealing a window runs XAML handlers that can add or
        // remove registry entries under us.
        foreach (var window in AllWindows.ToArray())
        {
            try
            {
                if (!window.TryFocusToastSurface(surfaceKey)) continue;
            }
            catch (System.Exception ex)
            {
                // Per window, not per scan. A window mid-teardown throws
                // RO_E_CLOSED out of AppWindow, and one dead window must not
                // stop the search reaching a live one behind it. The net is
                // wide because this walks arbitrary window state and the cost
                // of a miss is the user's click.
                Ghostty.Logging.StaticLoggers.App.LogToastActivationFailed(ex);
                continue;
            }

            NoteWindowRevealed(window);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Keep the toggle_visibility bookkeeping honest when something other than
    /// the toggle put a window back on screen. Without this a user who hid
    /// every window, then clicked a toast, gets a restore rather than a hide
    /// from the next toggle.
    /// </summary>
    private void NoteWindowRevealed(MainWindow window)
    {
        if (!_hiddenByVisibilityToggle.Remove(window)) return;
        if (_hiddenByVisibilityToggle.Count == 0) _windowsHiddenByVisibilityToggle = false;
    }

    /// <summary>
    /// Cold-start path for jump-list argv when this process is the primary
    /// (or a secondary whose forward to the primary failed).
    /// </summary>
    private void HandleColdStartJumpList(Ghostty.Core.JumpList.JumpListLaunch launch)
        => HandleJumpListLaunch(launch, workingDirectory: "");

    private void HandleJumpListLaunch(
        Ghostty.Core.JumpList.JumpListLaunch launch,
        string workingDirectory)
    {
        var action = launch.Action == Ghostty.Core.JumpList.JumpListAction.None
            ? Ghostty.Core.JumpList.JumpListAction.NewWindow
            : launch.Action;

        if (action == Ghostty.Core.JumpList.JumpListAction.NewTab
            && TryOpenJumpListTab(launch.ProfileId))
            return;

        OpenJumpListWindow(launch.ProfileId, workingDirectory);
    }

    private bool TryOpenJumpListTab(string? profileId)
    {
        var window = LastRegularWindow is { IsQuickTerminal: false } last
            ? last
            : System.Linq.Enumerable.FirstOrDefault(
                AllWindows, w => !w.IsQuickTerminal);
        if (window is null) return false;

        // A live window is enough. Missing DefaultProfileId used to
        // return false here and OpenWindowFromLaunch fell through to
        // OpenJumpListWindow -- jump-list New Tab opened a window.
        window.OpenJumpListTab(profileId);
        window.Activate();
        return true;
    }

    private void OpenJumpListWindow(string? profileId, string workingDirectory)
    {
        Ghostty.Core.Profiles.ProfileSnapshot? snapshot = null;
        var registry = ProfileRegistry;
        var id = profileId ?? registry?.DefaultProfileId;
        if (id is not null && registry?.Resolve(id) is { } resolved)
        {
            snapshot = Ghostty.Core.Profiles.ProfileSnapshotStore.From(
                resolved, registry.Version);
            if (!string.IsNullOrEmpty(workingDirectory))
                snapshot = snapshot with { WorkingDirectory = workingDirectory };
        }

        var window = MainWindow.CreateForNewTab(
            _configService!, _bootstrapHost!, _lifetimeSupervisor!, _loggerFactory!, snapshot);
        window.Closed += OnAnyWindowClosedInternal;
        _sessionManager?.Track(window);
        _sessionManager?.RequestPersist();
        window.Activate();
    }

    private static void OnProfilesChangedRebuildJumpList(
        Ghostty.Core.Profiles.IProfileRegistry _)
        => RebuildJumpList();

    /// <summary>
    /// Rebuild the taskbar jump list from the current profile registry.
    /// Safe to call before the registry exists (tasks only) and after
    /// every ProfilesChanged. COM failures are swallowed: a missing
    /// jump list is worse UX than a crash.
    /// </summary>
    private static void RebuildJumpList()
    {
        try
        {
            var exePath = System.Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrEmpty(exePath)) return;

            var facade = new Ghostty.JumpList.CustomDestinationListFacade();
            var builder = new Ghostty.Core.JumpList.JumpListBuilder(
                facade,
                profilesProvider: () => Ghostty.Core.JumpList.JumpListProfiles.From(
                    ProfileRegistry?.Profiles
                    ?? System.Array.Empty<Ghostty.Core.Profiles.ResolvedProfile>()),
                exePath: exePath,
                appId: Ghostty.Core.AppIdentity.AumId);
            builder.Build();
        }
        catch (System.Exception ex)
        {
            // See AumidFailed: NullLogger until the factory builds.
            Ghostty.Logging.StaticLoggers.App.LogJumpListFailed(ex);
        }
    }

    /// <summary>
    /// Reopen the most recently closed window from the shared store, or no-op
    /// if empty. Reuses the WindowSession restore ctor and the same
    /// registration the startup restore loop uses.
    /// </summary>
    internal void ReopenClosedWindow()
    {
        // The services are only null before OnLaunched or after the last window
        // tears the app down; neither state has a live window to fire the chord,
        // so this guard is defensive rather than a real runtime branch.
        if (_configService is null || _bootstrapHost is null ||
            _lifetimeSupervisor is null || _loggerFactory is null) return;
        if (!ClosedWindows.TryPop(out var windowSession)) return;

        var restored = new MainWindow(
            _configService, _bootstrapHost, _lifetimeSupervisor, _loggerFactory, windowSession);
        restored.Closed += OnAnyWindowClosedInternal;
        // Track + persist so the reopened window is in the on-disk session
        // snapshot immediately, matching the new-window (OpenInNewWindow) path.
        _sessionManager?.Track(restored);
        _sessionManager?.RequestPersist();
        restored.Activate();
    }

    /// <summary>
    /// Toggle the singleton quake / drop-down terminal window. Called
    /// from PaneActionRouter.QuickTerminalToggleRequested (chord),
    /// GhosttyHost.OnAction (libghostty action callback), and the
    /// command palette. The quake window is the same MainWindow class
    /// as regular windows, just with IsQuickTerminal = true and a
    /// no-taskbar / no-AltTab / close-hides behaviour profile.
    /// </summary>
    internal void ToggleQuickTerminal()
    {
        // Off-thread callers (libghostty's action callback fires on a
        // worker thread) need the captured UI dispatcher; the
        // GetForCurrentThread() fallback returns null on those threads
        // and silently drops the toggle.
        var dispatcher = _uiDispatcher ?? DispatcherQueue.GetForCurrentThread();
        dispatcher?.TryEnqueue(() =>
        {
            _quakeWindow?.ToggleVisibility();
        });
    }

    /// <summary>
    /// Close every normal terminal window (close_all_windows). The quake
    /// window is intentionally skipped: when the last normal window closes,
    /// the internal teardown force-closes the quake window and the bootstrap
    /// host, so the process exits cleanly. Snapshot first because Close()
    /// mutates WindowsByRoot during the loop.
    /// </summary>
    internal void CloseAllWindows()
    {
        foreach (var w in AllWindows.ToList())
        {
            if (ReferenceEquals(w, _quakeWindow)) continue;
            w.Close();
        }
    }

    /// <summary>
    /// Show the app-wide settings window, or focus the existing one if it is
    /// already open. One settings window serves the whole process, so opening
    /// it from any window reuses the same instance. The caller guards on
    /// SettingsUiEnabled. UI thread only.
    /// </summary>
    internal void ShowOrActivateSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var keybindings = new KeyBindingsProvider(_configService!);
        var themeProvider = new ThemeProvider(_configService!);
        var window = new Ghostty.Settings.SettingsWindow(
            _configService!, ConfigFileEditor!, keybindings, themeProvider);
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Activate();
    }

    /// <summary>
    /// Close the app-wide settings window if it is open. Called from the last
    /// window's teardown so the settings window does not outlive the app.
    /// </summary>
    internal void CloseSettingsWindow()
    {
        _settingsWindow?.Close();
        _settingsWindow = null;
    }

    // Whether the last toggle_visibility hid the windows, and the set it hid.
    // The hide/show decision is driven by this flag rather than inferred from
    // IsVisible: a window opened between a hide and the next toggle would flip
    // an IsVisible-based check and strand the earlier-hidden windows.
    private bool _windowsHiddenByVisibilityToggle;
    private readonly List<MainWindow> _hiddenByVisibilityToggle = new();

    /// <summary>
    /// Hide/show all normal windows (toggle_visibility). If they are not
    /// currently hidden by a prior toggle, hide every visible one and remember
    /// the set; otherwise re-show exactly that set. The quake window is excluded
    /// -- it has its own global-hotkey toggle.
    /// </summary>
    internal void ToggleAllWindowsVisibility()
    {
        var normal = AllWindows.Where(w => !ReferenceEquals(w, _quakeWindow)).ToList();
        if (!_windowsHiddenByVisibilityToggle)
        {
            _hiddenByVisibilityToggle.Clear();
            foreach (var w in normal)
            {
                if (!w.AppWindow.IsVisible) continue;
                _hiddenByVisibilityToggle.Add(w);
                w.AppWindow.Hide();
            }
            // Only enter the hidden state if we actually hid something, so a
            // no-op toggle doesn't leave us unable to "restore" on the next one.
            _windowsHiddenByVisibilityToggle = _hiddenByVisibilityToggle.Count > 0;
        }
        else
        {
            // Restore exactly the windows we hid, skipping any closed since.
            foreach (var w in _hiddenByVisibilityToggle.Where(normal.Contains))
                w.AppWindow.Show();
            _hiddenByVisibilityToggle.Clear();
            _windowsHiddenByVisibilityToggle = false;
        }
    }

    /// <summary>
    /// Focus the previous/next normal window relative to <paramref name="current"/>
    /// (goto_window), wrapping at the ends. Direction: -1 previous, +1 next.
    /// </summary>
    internal void ActivateRelativeWindow(MainWindow current, int direction)
    {
        var normal = AllWindows.Where(w => !ReferenceEquals(w, _quakeWindow)).ToList();
        if (normal.Count <= 1) return;
        var idx = normal.IndexOf(current);
        if (idx < 0) return;
        var next = ((idx + direction) % normal.Count + normal.Count) % normal.Count;
        normal[next].Activate();
    }

    /// <summary>
    /// Tray icon double-click / Show menu: restore hidden windows or focus
    /// the last regular terminal window.
    /// </summary>
    private void ShowOrFocusWindowsFromTray()
    {
        if (_windowsHiddenByVisibilityToggle)
        {
            ToggleAllWindowsVisibility();
            return;
        }

        var target = LastRegularWindow ??
            System.Linq.Enumerable.FirstOrDefault(
                AllWindows, w => !w.IsQuickTerminal);
        if (target is null) return;
        if (!target.AppWindow.IsVisible)
            target.AppWindow.Show();
        target.Activate();
    }

    private void RegisterQuakeHotKey()
    {
        if (_quakeHotKey is null) return;
        var chord = _configService.QuickTerminalKeyChord;
        // Register already logs a warning + returns false when another
        // process holds the chord; the app stays usable (command-palette
        // entry + in-window chord still toggle the quake window), so there
        // is nothing to do on failure here.
        _quakeHotKey.Register(chord.Modifiers, chord.VirtualKey);
    }

    private void OnConfigReloaded_ReRegisterHotKey(Ghostty.Core.Config.IConfigService cfg)
    {
        // Register is idempotent (drops the prior chord first), so calling it
        // on every reload is safe even when the chord did not change. Marshal
        // to the UI thread (the thread that created the message-only window).
        _uiDispatcher?.TryEnqueue(RegisterQuakeHotKey);
    }

    /// <summary>
    /// Enrol <paramref name="tab"/> with the process tracker. Idempotent:
    /// re-registering the same tab is a no-op for the tracker and only
    /// re-installs the <see cref="Ghostty.Core.Tabs.TabModel.ShellPidChanged"/>
    /// subscription once via the underlying event's reentrancy semantics.
    /// Called from <see cref="MainWindow"/> on <c>TabManager.TabAdded</c>;
    /// the shell pid may be null at this point (the libghostty surface
    /// has not loaded yet) so we hook ShellPidChanged for the late path.
    /// </summary>
    internal void RegisterTabForProcessTracking(Ghostty.Core.Tabs.TabModel tab)
    {
        if (_activeProcessTracker is null) return;
        tab.ShellPidChanged += OnTabShellPidChanged;
        // Pick up a pid already set before we subscribed (e.g. a future
        // path that resolves the pid synchronously at TabManager.NewTab).
        if (tab.ShellPid is int pid)
        {
            _tabsByPid[pid] = tab;
            _activeProcessTracker.Register(pid);
        }
    }

    /// <summary>
    /// Reverse of <see cref="RegisterTabForProcessTracking"/>. Detaches
    /// the ShellPidChanged subscription and removes any registered pid
    /// from the tracker. Called from <see cref="MainWindow"/> on
    /// <c>TabManager.TabRemoved</c>.
    /// </summary>
    internal void UnregisterTabForProcessTracking(Ghostty.Core.Tabs.TabModel tab)
    {
        tab.ShellPidChanged -= OnTabShellPidChanged;
        if (tab.ShellPid is int pid)
        {
            _activeProcessTracker?.Unregister(pid);
            _tabsByPid.TryRemove(pid, out _);
        }
    }

    private void OnTabShellPidChanged(Ghostty.Core.Tabs.TabModel tab, int? newPid)
    {
        if (_activeProcessTracker is null) return;
        // Old pid may still be registered if the shell respawned without
        // an explicit unregister; drop it before adopting the new one.
        // Snapshot the entries that point at this tab so we don't leak
        // stale rows when a tab cycles through pids.
        foreach (var kv in _tabsByPid)
        {
            if (!ReferenceEquals(kv.Value, tab)) continue;
            if (newPid is int np && np == kv.Key) continue;
            _activeProcessTracker.Unregister(kv.Key);
            _tabsByPid.TryRemove(kv.Key, out _);
        }

        if (newPid is int pid)
        {
            _tabsByPid[pid] = tab;
            _activeProcessTracker.Register(pid);
        }
    }

    private void OnActiveProcessChanged(
        object? sender,
        Ghostty.Core.Profiles.Tracking.ActiveProcessChangedEventArgs e)
    {
        if (!_tabsByPid.TryGetValue(e.RootPid, out var tab)) return;
        // The tracker fires Changed from a Timer threadpool callback.
        // TabModel.OnActiveProcessChanged mutates TabIconViewModel, which
        // raises PropertyChanged consumed by WinUI bindings, so marshal
        // onto the UI thread before invoking it. Null dispatcher means we
        // are mid-shutdown; drop the event.
        _uiDispatcher?.TryEnqueue(() => tab.OnActiveProcessChanged(e.ExeBasename, e.CommandLine));
    }

    private void OnConfigChanged_ApplyLogFilters(Ghostty.Core.Config.IConfigService cfg)
    {
        if (_logFilters is null) return;
        Ghostty.Core.Logging.LoggingBootstrap.ApplyFilters(
            _logFilters, cfg.LogLevel, cfg.LogFilter);
    }

    private void OnConfigChanged_NotifyPowerMonitor(Ghostty.Core.Config.IConfigService cfg)
    {
        _powerStateMonitor?.OnConfigReloaded();
    }

    /// <summary>
    /// Called when ANY top-level <see cref="MainWindow"/> closes. The
    /// per-window <see cref="GhosttyHost"/> is already disposed by
    /// this point (via the window's own Closed path). When
    /// <see cref="WindowsByRoot"/> hits zero we dispose the bootstrap
    /// host last; its drain-last supervisor guard asserts that every
    /// per-window host already disposed in order.
    ///
    /// Visibility is <c>internal</c> so <c>MainWindow.DetachTabToNewWindow</c>
    /// can subscribe freshly-built windows to the same handler.
    /// </summary>
    internal void OnAnyWindowClosedInternal(object sender, WindowEventArgs args)
    {
        // Use the XamlRoot captured at registration time (stored on the
        // MainWindow instance) rather than re-reading w.Content.XamlRoot
        // here. By the time Window.Closed fires in WinUI 3, Content may
        // already have a null XamlRoot, so re-reading would silently
        // skip the removal and leak the entry.
        var closing = sender as MainWindow;
        if (closing is { RegisteredRoot: { } root })
            WindowsByRoot.Remove(root);

        if (ReferenceEquals(LastRegularWindow, closing))
            LastRegularWindow = System.Linq.Enumerable.FirstOrDefault(
                AllWindows, w => !w.IsQuickTerminal);

        // Detach this window's session-persistence subscriptions.
        if (closing is not null)
            _sessionManager?.Untrack(closing);

        // A deliberately-closed (non-last) window is left in the persisted
        // set on purpose: we cannot tell a single close apart from the first
        // close of a multi-window quit, so we never shrink the set on close
        // (that would lose windows on a slow quit cascade). It self-heals out
        // on the next layout/tab/move change in a surviving window. Biasing to
        // "never lose a window" over "never restore a closed one".

        if (WindowsByRoot.Count == 0)
        {
            try
            {
                // Final clean-shutdown write while the closing window's panes
                // are still alive (teardown happens below). Marks the session
                // clean so window-save-state=default restores it next launch.
                _sessionManager?.FinalizeCleanShutdown(closing);

                // Stop config reloads FIRST, before anything below frees the
                // libghostty app or the DX12 renderer. A debounced reload
                // from a last-moment config change (e.g. a window-theme
                // switch right before close) would otherwise run
                // AppUpdateConfig on freed state and crash the process with
                // a native access violation (issue #208 switch-then-close).
                _configService.BeginShutdown();

                // Stop the process tracker before any tab teardown could
                // race its Timer callback. Dispose unsubscribes the
                // Changed handler in addition to cancelling the timer,
                // so straggler tab.OnActiveProcessChanged enqueues stop
                // here. The reverse-lookup dictionary is cleared in the
                // finally block below alongside the static accessor.
                if (_activeProcessTracker is not null)
                {
                    _activeProcessTracker.Changed -= OnActiveProcessChanged;
                    _activeProcessTracker.Dispose();
                }

                // Dispose the registry first: its Dispose cancels any
                // pending discovery and unsubscribes from
                // _configService's ProfileConfigChanged event. The
                // DiscoveryService holds no unmanaged resources, so
                // we just drop the ref and let GC claim it.
                if (_profileRegistry is not null)
                {
                    _profileRegistry.ProfilesChanged -= OnProfilesChangedRebuildJumpList;
                    _profileRegistry.Dispose();
                }

                // Flush any pending debounced writes before the editor
                // is gone. Dispose waits for an in-flight timer
                // callback so disk writes happen-before the host tears
                // down the ghostty app.
                _configWriteScheduler?.Dispose();

                // Unregister the quake-mode global hotkey before the
                // bootstrap host tears down. WindowsGlobalHotKey.Dispose
                // calls UnregisterHotKey on the UI thread (same thread
                // that registered it).
                _configService.ConfigChanged -= OnConfigReloaded_ReRegisterHotKey;
                _quakeHotKey?.Dispose();
                _systemMenuHook?.Dispose();
                _trayIconService?.Dispose();
                _trayIconService = null;

                // Detach both halves of the toast wiring. AppNotificationManager.Default
                // and the relay are both process-lifetime, so a handler left
                // attached roots this App for as long as the process runs -- and
                // a relay handler that outlives the dispatcher it marshals to is
                // a click delivered into a dead window tree.
                if (_toastInvokedSubscribed)
                {
                    try
                    {
                        Microsoft.Windows.AppNotifications.AppNotificationManager.Default
                            .NotificationInvoked -= OnToastNotificationInvoked;
                    }
                    catch (System.Exception ex)
                    {
                        Ghostty.Logging.StaticLoggers.App.LogToastActivationFailed(ex);
                    }
                    _toastInvokedSubscribed = false;
                }
                ToastActivations.Reset();

                // Force-close the quake window. It does not participate
                // in WindowsByRoot (so this branch fires when the last
                // *regular* window closes), but the quake window is a
                // real top-level Window the OS will keep the process
                // alive for unless we close it explicitly. Closing it
                // triggers its own Window.Closed -> per-window host
                // dispose path before the bootstrap host disposes
                // below.
                if (_quakeWindow is not null)
                {
                    var quake = _quakeWindow;
                    _quakeWindow = null;
                    quake.Closed -= OnAnyWindowClosedInternal;
                    // Opt out of the AppWindow.Closing intercept that
                    // turns Close() into Hide() during normal user
                    // interaction. Without this the force-close below
                    // would silently hide and the process would never
                    // exit.
                    quake.RequestHardClose();
                    quake.Close();
                }

                // Stop the single-instance forwarding server before the
                // host tears down so no inbound forwarded launch races a
                // half-disposed app (the callback enqueues OpenWindowFromLaunch
                // onto the UI thread).
                _singleInstanceServer?.Dispose();

                // Bootstrap host is the LAST host. Its Dispose drains
                // _hostBySurface (asserts empty), notifies the
                // supervisor (which throws if anything is still live),
                // and calls AppFree.
                _bootstrapHost?.Dispose();

                // Dispose between host and config service: the monitor subscribes
                // to ConfigService.ConfigChanged, so tear it down before ConfigService.
                if (_configService is not null)
                {
                    _configService.ConfigChanged -= OnConfigChanged_NotifyPowerMonitor;
                }
                _powerStateMonitor?.Dispose();

                // Dispose ConfigService last: it outlives every host
                // (by design, so reload round-trips work across
                // detached windows) but does own a FileSystemWatcher
                // thread and the native config handle. Disposing here
                // stops the watcher before the process exits and frees
                // the libghostty config struct symmetrically with
                // ConfigNew + ConfigLoadDefaultFiles.
                _configService?.Dispose();

                // Dispose the libghostty log bridge before the factory.
                // Bridge.Dispose clears the native callback and sets an
                // internal disposed flag, so any Zig thread that already
                // latched the function pointer still returns to OnLog
                // but then bails on the flag check. That guarantee lets
                // the factory tear-down below proceed without racing an
                // inbound Zig log into a disposed ILoggerFactory.
                _zigLogBridge?.Dispose();

                // dispose the factory after the config service so any ConfigChanged
                // callbacks fired during ConfigService.Dispose don't race a disposed factory.
                // FileLoggerProvider.DisposeAsync flushes its channel
                // with a 2-second cap; block synchronously so the final
                // batch of log records lands on disk before process exit.
                //
                // Sync-over-async here is intentional and deadlock-free:
                // FileLoggerProvider's writer loop runs on Task.Run and
                // awaits throughout with ConfigureAwait(false), so no
                // continuation resumes on this UI SynchronizationContext.
                if (_fileLogSink is not null)
                {
                    try { _fileLogSink.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    catch { /* best-effort */ }
                }
                _loggerFactory?.Dispose();
            }
            finally
            {
                _profileRegistry = null;
                ProfileRegistry = null;
                ModifierKeyState = null;
                _modifierKeyState = null;
                IconResolver = null;
                _iconResolver = null;
                ActiveProcessTracker = null;
                _activeProcessTracker = null;
                _tabsByPid.Clear();
                _uiDispatcher = null;
                _discoveryService = null;
                _configWriteScheduler = null;
                ConfigWriteScheduler = null;
                _notificationService = null;
                NotificationService = null;
                _configEditor = null;
                ConfigFileEditor = null;
                _bootstrapHost = null;
                BootstrapHost = null;
                _lifetimeSupervisor = null;
                LifetimeSupervisor = null;
                _highContrastMonitor?.Dispose();
                _highContrastMonitor = null;
                _configService = null;
                ConfigService = null;
                _powerStateMonitor = null;
                PowerStateMonitor = null;

                _zigLogBridge = null;
                _fileLogSink = null;
                _loggerFactory = null;
                LoggerFactory = null;

                _singleInstanceServer = null;
                // Release the single-instance mutex so a relaunch can become
                // the new primary immediately after we exit.
                try { Program.SingleInstance?.Dispose(); } catch { /* ignore */ }

                // The message loop is about to end, which abandons background
                // threads. A launch whose first frame never arrived can still
                // have the splash up inside its watchdog, and that thread can
                // be inside GDI+.
                Ghostty.Shell.SplashWindow.HideNow();

                Exit();
            }
        }
    }
}

internal static partial class AppLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Startup.AumidFailed,
                   Level = LogLevel.Warning,
                   Message = "Failed to set AUMID")]
    internal static partial void LogAumidFailed(
        this ILogger<App> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Startup.JumpListFailed,
                   Level = LogLevel.Warning,
                   Message = "Failed to build jump list")]
    internal static partial void LogJumpListFailed(
        this ILogger<App> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Startup.ToastRegisterFailed,
                   Level = LogLevel.Warning,
                   Message = "Failed to register for toast notifications")]
    internal static partial void LogToastRegisterFailed(
        this ILogger<App> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Notifications.ActivationFailed,
                   Level = LogLevel.Warning,
                   Message = "Failed to act on a toast notification click")]
    internal static partial void LogToastActivationFailed(
        this ILogger<App> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.Startup.TrayInitFailed,
                   Level = LogLevel.Warning,
                   Message = "Failed to initialize notification-area tray icon")]
    internal static partial void LogTrayInitFailed(
        this ILogger<App> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.SingleInstance.MutexFailed,
                   Level = LogLevel.Warning,
                   Message = "Single-instance mutex could not be created; launching as a normal independent process.")]
    internal static partial void LogSingleInstanceMutexFailed(
        this ILogger<App> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.SingleInstance.ForwardFailed,
                   Level = LogLevel.Warning,
                   Message = "Single-instance forward to the primary failed; launching as a normal independent process.")]
    internal static partial void LogSingleInstanceForwardFailed(
        this ILogger<App> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.SingleInstance.ServerStartFailed,
                   Level = LogLevel.Warning,
                   Message = "Single-instance pipe server failed to start; secondaries will launch independently.")]
    internal static partial void LogSingleInstanceServerStartFailed(
        this ILogger<App> logger, System.Exception ex);
}
