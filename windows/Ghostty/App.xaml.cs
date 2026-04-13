using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Ghostty.Controls;
using Ghostty.Core.Hosting;
using Ghostty.Hosting;
using Ghostty.Services;
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
    private GhosttyHost? _bootstrapHost;
    private HostLifetimeSupervisor? _lifetimeSupervisor;

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

        // Surface unhandled exceptions to stderr before the process dies.
        // Without this, a managed exception on the UI thread silently exits
        // with a non-descriptive code and we have nothing to debug from.
        // Stays enabled in Debug builds only -- in Release we want WER to
        // capture a real crash dump instead.
#if DEBUG
        UnhandledException += (s, e) =>
        {
            try
            {
                Console.Error.WriteLine("[Ghostty] UNHANDLED EXCEPTION on UI thread:");
                Console.Error.WriteLine(e.Exception.ToString());
                Console.Error.Flush();
            }
            catch { /* logging must not throw */ }
            // Leave Handled=false so the runtime still tears the app down --
            // we just wanted to see the exception first.
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                Console.Error.WriteLine("[Ghostty] UNHANDLED EXCEPTION (AppDomain):");
                Console.Error.WriteLine(e.ExceptionObject?.ToString() ?? "(null)");
                Console.Error.Flush();
            }
            catch { /* logging must not throw */ }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try
            {
                Console.Error.WriteLine("[Ghostty] UNOBSERVED TASK EXCEPTION:");
                Console.Error.WriteLine(e.Exception.ToString());
                Console.Error.Flush();
            }
            catch { /* logging must not throw */ }
        };
#endif
    }

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
            System.Diagnostics.Debug.WriteLine($"Failed to set AUMID: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"Failed to build jump list: {ex.Message}");
        }

        _configService = new ConfigService(DispatcherQueue.GetForCurrentThread());
        ConfigService = _configService;

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
            _lifetimeSupervisor);
        BootstrapHost = _bootstrapHost;
        _configService.SetApp(_bootstrapHost.App);

        var window = new MainWindow(_configService, _bootstrapHost, _lifetimeSupervisor);
        window.Closed += OnAnyWindowClosedInternal;
        window.Activate();
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
                // Bootstrap host is the LAST host. Its Dispose drains
                // _hostBySurface (asserts empty), notifies the
                // supervisor (which throws if anything is still live),
                // and calls AppFree.
                _bootstrapHost?.Dispose();
            }
            finally
            {
                _bootstrapHost = null;
                BootstrapHost = null;
                _lifetimeSupervisor = null;
                LifetimeSupervisor = null;
                Exit();
            }
        }
    }
}
