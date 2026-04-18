using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Ghostty.Controls;
using Ghostty.Core.Config;
using Ghostty.Core.Hosting;
using Ghostty.Hosting;
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
    // -- Option (b) hoist: process-global libghostty ownership. --
    //
    // Single ghostty_app_t across all top-level windows. The bootstrap
    // GhosttyHost owns the runtime callback function pointers libghostty
    // was given in AppNew, so it must stay alive until the last window
    // closes. Every MainWindow builds its own per-window GhosttyHost
    // with its own per-window _surfaces dictionary.
    //
    // Dispose order at shutdown (OnAnyWindowClosedInternal handler):
    //   1. Per-window host for the closing window disposes first, as
    //      part of MainWindow.OnClosed, and removes itself from
    //      _hostBySurface via GhosttyHost.Dispose -> UnregisterHostSurfaces.
    //   2. When WindowsByRoot is empty, bootstrap host disposes: it
    //      calls AppFree (its HostLifetimeState.OwnsApp is true).
    //   3. ConfigService disposal is handled by process exit.
    // Instance fields for the App's own lifecycle (assigned in
    // OnLaunched, cleared in OnAnyWindowClosedInternal). The matching
    // static properties below expose the same references to types
    // (MainWindow, GhosttyHost) that do not hold a reference to the
    // App instance. WinUI 3's Application is a process singleton so
    // both always agree; the duplication is the lesser evil compared
    // to casting Application.Current everywhere.
    private ConfigService? _configService;
    private ConfigFileEditor? _configEditor;
    private ConfigWriteScheduler? _configWriteScheduler;
    private GhosttyHost? _bootstrapHost;
    private HostLifetimeSupervisor? _lifetimeSupervisor;
    private Microsoft.Extensions.Logging.ILoggerFactory? _loggerFactory;
    private Ghostty.Core.Logging.FileLoggerProvider? _fileLogSink;
    private Ghostty.Core.Logging.FilterState? _logFilters;
#if SPONSOR_BUILD
    private Ghostty.Sponsor.Update.SponsorOverlayBootstrapper? _sponsorOverlay;
    internal Ghostty.Sponsor.Update.SponsorOverlayBootstrapper? SponsorOverlay => _sponsorOverlay;
    // Eagerly-initialized so MainWindow.CreateCommandPaletteViewModel
    // (which runs inside the MainWindow ctor, before _sponsorOverlay is
    // wired below) can still see a live simulator and register its
    // palette commands. The bootstrapper reuses this same instance.
    private Ghostty.Sponsor.Update.UpdateSimulator? _sharedSimulator;
    internal Ghostty.Sponsor.Update.UpdateSimulator SharedSimulator =>
        _sharedSimulator ??= new Ghostty.Sponsor.Update.UpdateSimulator();
#endif

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

    internal static GhosttyHost? BootstrapHost { get; private set; }
    internal static ConfigService? ConfigService { get; private set; }

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

    static App()
    {
        // libghostty.dll lives in a `native/` subdirectory next to this
        // assembly so its filename (ghostty.dll) does not collide with our
        // own managed Ghostty.dll on case-insensitive filesystems. The
        // LibraryImport entries use "ghostty" so we resolve that name here.
        NativeLibrary.SetDllImportResolver(
            typeof(Interop.NativeMethods).Assembly,
            (name, assembly, path) =>
            {
                if (!string.Equals(name, "ghostty", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;
                // AppContext.BaseDirectory works in all deployment modes
                // (framework-dependent, single-file, Native AOT).
                // assembly.Location returns empty under single-file and AOT.
                var candidate = Path.Combine(AppContext.BaseDirectory, "native", "ghostty.dll");
                return NativeLibrary.Load(candidate);
            });
    }

    public App()
    {
        InitializeComponent();

        // Surface unhandled exceptions to stderr AND to a file under
        // %LOCALAPPDATA%\Ghostty\ before the process dies. Without
        // this, a managed exception on the UI thread silently exits
        // with a non-descriptive code and we have nothing to debug
        // from -- especially in Release, where WER captures a dump
        // but the user is left without a human-readable pointer to
        // it. The file path is stable across Debug and Release so
        // the same path works for dev debugging and for a user who
        // needs to attach logs to a bug report.
        UnhandledException += (s, e) =>
        {
            LogUnhandled("UI-THREAD UNHANDLED", e.Exception.ToString());
            // Leave Handled=false so the runtime still tears the app
            // down -- we just wanted to record the exception first.
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
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
            Console.Error.WriteLine($"[Ghostty] {tag}:");
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
        // `%LOCALAPPDATA%\Ghostty\`.
        try
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(localAppData, "Ghostty");
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
        const string AppUserModelId = "com.deblasis.ghostty";
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

        // Build the jump list once at startup.
        try
        {
            var exePath = System.Environment.ProcessPath ?? string.Empty;
            if (!string.IsNullOrEmpty(exePath))
            {
                var facade = new Ghostty.JumpList.CustomDestinationListFacade();
                var builder = new Ghostty.Core.JumpList.JumpListBuilder(
                    facade,
                    profilesProvider: () => System.Array.Empty<Ghostty.Core.JumpList.ProfileEntry>(),
                    exePath: exePath,
                    appId: AppUserModelId);
                builder.Build();
            }
        }
        catch (System.Exception ex)
        {
            // See AumidFailed above: NullLogger until factory builds.
            Ghostty.Logging.StaticLoggers.App.LogJumpListFailed(ex);
        }

        _configService = new ConfigService(DispatcherQueue.GetForCurrentThread());
        ConfigService = _configService;

        // #259 logging: build the factory from Ghostty config before any
        // other service constructs an ILogger<T>. Log directory under the
        // same %LOCALAPPDATA%\Ghostty root that App.LogUnhandled already
        // uses for crash.log, so a user reporting a bug only has one
        // folder to attach.
        var logDir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Ghostty", "logs");
        var (factory, fileSink, filters) = Ghostty.Core.Logging.LoggingBootstrap.Build(
            logLevel: _configService.LogLevel,
            logFilter: _configService.LogFilter,
            fileLogDirectory: logDir);
        _loggerFactory = factory;
        _fileLogSink = fileSink;
        _logFilters = filters;
        LoggerFactory = factory;
        _configService.ConfigChanged += OnConfigChanged_ApplyLogFilters;

        // #259 logging: populate Core-side static logger accessors
        // for types whose call sites are static (e.g., FrecencyStore
        // static methods that can't take a ctor-injected logger).
        Ghostty.Core.Logging.CoreStaticLoggers.Initialize(factory);

        // #259 logging: populate Ghostty-project static logger accessors
        // for types that construct before ctor-injection is possible
        // (e.g., ConfigService is built above BEFORE the factory exists,
        // and cannot receive a logger through its ctor) and for call
        // sites inside static scopes.
        Ghostty.Logging.StaticLoggers.Initialize(factory);

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

        Uri? activationUri = null;
        try
        {
            var activated = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activated.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol
                && activated.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs proto)
            {
                activationUri = proto.Uri;
            }
            else
            {
                // Unpackaged fallback: command line --uri <url>.
                var argv = Environment.GetCommandLineArgs();
                for (int i = 0; i < argv.Length - 1; i++)
                {
                    if (string.Equals(argv[i], "--uri", StringComparison.Ordinal)
                        && Uri.TryCreate(argv[i + 1], UriKind.Absolute, out var u))
                    {
                        activationUri = u;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[app] protocol activation probe failed: {ex.Message}");
        }

        var window = new MainWindow(_configService, _bootstrapHost, _lifetimeSupervisor, factory);
        window.Closed += OnAnyWindowClosedInternal;
#if SPONSOR_BUILD
        // SharedSimulator was already materialized during MainWindow's
        // ctor (CreateCommandPaletteViewModel reads it to register the
        // palette commands). Pass that same instance to Wire so there's
        // one simulator driving both the palette and the update pipeline.
        _sponsorOverlay = Ghostty.Sponsor.Update.SponsorOverlayBootstrapper.Wire(
            window, _configService, DispatcherQueue.GetForCurrentThread(), SharedSimulator);
        if (activationUri is not null)
        {
            _sponsorOverlay?.Router.HandleUri(activationUri);
        }
#endif
        window.Activate();
    }

    private void OnConfigChanged_ApplyLogFilters(Ghostty.Core.Config.IConfigService cfg)
    {
        if (_logFilters is null) return;
        Ghostty.Core.Logging.LoggingBootstrap.ApplyFilters(
            _logFilters, cfg.LogLevel, cfg.LogFilter);
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
        if (sender is MainWindow w && w.RegisteredRoot is { } root)
            WindowsByRoot.Remove(root);

        if (WindowsByRoot.Count == 0)
        {
            try
            {
                // Flush any pending debounced writes before the editor
                // is gone. Dispose waits for an in-flight timer
                // callback so disk writes happen-before the host tears
                // down the ghostty app.
                _configWriteScheduler?.Dispose();

                // Bootstrap host is the LAST host. Its Dispose drains
                // _hostBySurface (asserts empty), notifies the
                // supervisor (which throws if anything is still live),
                // and calls AppFree.
#if SPONSOR_BUILD
                _sponsorOverlay?.Dispose();
                _sponsorOverlay = null;
#endif
                _bootstrapHost?.Dispose();

                // Dispose ConfigService last: it outlives every host
                // (by design, so reload round-trips work across
                // detached windows) but does own a FileSystemWatcher
                // thread and the native config handle. Disposing here
                // stops the watcher before the process exits and frees
                // the libghostty config struct symmetrically with
                // ConfigNew + ConfigLoadDefaultFiles.
                _configService?.Dispose();

                // #259 logging: dispose the factory after the config
                // service so any ConfigChanged callbacks fired during
                // ConfigService.Dispose don't race a disposed factory.
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
                _configWriteScheduler = null;
                ConfigWriteScheduler = null;
                _configEditor = null;
                ConfigFileEditor = null;
                _bootstrapHost = null;
                BootstrapHost = null;
                _lifetimeSupervisor = null;
                LifetimeSupervisor = null;
                _configService = null;
                ConfigService = null;

                _fileLogSink = null;
                _loggerFactory = null;
                LoggerFactory = null;

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
}
