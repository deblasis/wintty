using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Ghostty.Clipboard;
using Ghostty.Controls;
using Ghostty.Core.Clipboard;
using Ghostty.Core.Hosting;
using Ghostty.Core.Interop;
using Ghostty.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Ghostty.Hosting;

/// <summary>
/// Per-window owner of the libghostty surface registry and the runtime
/// callback surface. Each host has its OWN per-window
/// <see cref="_surfaces"/> dictionary. The bootstrap host additionally
/// owns the libghostty callback delegates and the <c>ghostty_app_t</c>.
///
/// Bootstrap vs per-window:
///   - Bootstrap host: created once by <see cref="App.OnLaunched"/> via
///     the legacy ctor. Owns the <c>_wakeupCb</c>, <c>_actionCb</c>,
///     etc. delegate fields. Libghostty calls these. Their bodies
///     consult <see cref="App.TryGetHostForSurface"/> to find the
///     per-window host that currently owns a given surface, then forward
///     there. Owns <c>ghostty_app_t</c> and calls <c>AppFree</c> on
///     Dispose.
///   - Per-window host: created by each <see cref="MainWindow"/> via
///     the shared-app ctor. Has NO delegate fields, NO <c>AppNew</c>
///     call. Wraps the same <c>ghostty_app_t</c> (borrowed, not owned).
///     Dispose does NOT call <c>AppFree</c>.
///
/// Lifetime: the <see cref="HostLifetimeSupervisor"/> enforces the
/// drain-last invariant -- every per-window host must Dispose before
/// the bootstrap host.
/// </summary>
internal sealed class GhosttyHost : IDisposable
{
    private GhosttyConfig _config;
    private GhosttyApp _app;

    // Lifetime state. The bootstrap host gets a HostLifetimeState
    // marked IsBootstrap = true; per-window hosts get PerWindow().
    // Dispose consults this instead of a bare _sharesApp bool.
    private readonly IAppHandleOwnership _ownership;

    /// <summary>
    /// UTC timestamp of the most recent key event seen by any
    /// <see cref="Ghostty.Controls.TerminalControl"/> bound to this
    /// host. Read by <see cref="Tabs.VerticalTabHost"/>'s
    /// hover-expand suppression to decide whether the user is
    /// mid-typing (popping the sidebar in that case would feel
    /// jarring and could interfere with an IME composition).
    /// </summary>
    public DateTime LastKeystrokeTimestamp { get; private set; } = DateTime.MinValue;

    internal void NoteKeystroke() => LastKeystrokeTimestamp = DateTime.UtcNow;

    public event EventHandler? CommandPaletteToggleRequested;
    public event EventHandler? OpenConfigRequested;
    public event EventHandler? ReloadConfigRequested;

    /// <summary>
    /// Raised when a terminal surface requests an opacity adjustment
    /// (Ctrl+Shift+scroll wheel). The int argument is the direction:
    /// +1 = increase, -1 = decrease.
    /// </summary>
    public event EventHandler<int>? OpacityAdjustRequested;

    /// <summary>
    /// Called by <see cref="TerminalControl"/> when Ctrl+Shift+Wheel
    /// is detected. Dispatches to the UI thread and raises
    /// <see cref="OpacityAdjustRequested"/>.
    /// </summary>
    internal void RequestOpacityAdjust(int direction)
    {
        _dispatcher.TryEnqueue(() =>
            OpacityAdjustRequested?.Invoke(this, direction));
    }

    private ClipboardBridge? _clipboardBridge;

    // Delegates must be retained as fields; P/Invoke hands out native
    // function pointers the GC cannot track. Only the BOOTSTRAP host
    // assigns these; per-window hosts leave them null.
    private GhosttyWakeupCb? _wakeupCb;
    private GhosttyActionCb? _actionCb;
    private GhosttyReadClipboardCb? _readClipboardCb;
    private GhosttyConfirmReadClipboardCb? _confirmReadClipboardCb;
    private GhosttyWriteClipboardCb? _writeClipboardCb;
    private GhosttyCloseSurfaceCb? _closeSurfaceCb;

    // Per-window surface dictionary. ALWAYS per-host, never shared.
    // Callbacks routed to this host (by App.xaml.cs's _hostBySurface
    // map) consult this dictionary to resolve XamlRoot and dispatcher.
    // The legacy ctor and the shared-app ctor both create a fresh
    // dictionary; nothing is passed in.
    private readonly ConcurrentDictionary<IntPtr, TerminalControl> _surfaces = new();
    private readonly DispatcherQueue _dispatcher;

    public GhosttyApp App => _app;

    /// <summary>
    /// Bootstrap ctor: owns <c>ghostty_app_t</c>, used by
    /// <c>App.OnLaunched</c> exactly once. This is the one host
    /// libghostty invokes. Its callback bodies consult
    /// <see cref="App.TryGetHostForSurface"/> to forward to whichever
    /// per-window host owns the target surface.
    /// </summary>
    public GhosttyHost(
        DispatcherQueue dispatcher,
        GhosttyConfig config,
        HostLifetimeSupervisor supervisor,
        ILoggerFactory loggerFactory)
    {
        _dispatcher = dispatcher;
        _ownership = new SupervisedOwnership(
            supervisor.RegisterBootstrap(),
            supervisor);
        _config = config;

        _wakeupCb = OnWakeup;
        _actionCb = OnAction;
        _readClipboardCb = OnReadClipboard;
        _confirmReadClipboardCb = OnConfirmReadClipboard;
        _writeClipboardCb = OnWriteClipboard;
        _closeSurfaceCb = OnCloseSurface;

        // Build the clipboard bridge after all delegate fields are assigned.
        var clipboardBackend = new WinUiClipboardBackend(
            _dispatcher,
            loggerFactory.CreateLogger<WinUiClipboardBackend>());
        var clipboardConfirmer = new DialogClipboardConfirmer(
            _dispatcher,
            xamlRootProvider: ResolveXamlRootForSurface,
            loggerFactory.CreateLogger<DialogClipboardConfirmer>());
        var clipboardService = new ClipboardService(clipboardBackend, clipboardConfirmer);
        _clipboardBridge = new ClipboardBridge(
            _dispatcher,
            clipboardService,
            resolveSurface: ResolveSurfaceFromUserdata,
            isSurfaceAlive: IsSurfaceAlive,
            loggerFactory.CreateLogger<ClipboardBridge>());

        var runtime = new GhosttyRuntimeConfig
        {
            Userdata = IntPtr.Zero,
            SupportsSelectionClipboard = 0,
            WakeupCb = Marshal.GetFunctionPointerForDelegate(_wakeupCb),
            ActionCb = Marshal.GetFunctionPointerForDelegate(_actionCb),
            ReadClipboardCb = Marshal.GetFunctionPointerForDelegate(_readClipboardCb),
            ConfirmReadClipboardCb = Marshal.GetFunctionPointerForDelegate(_confirmReadClipboardCb),
            WriteClipboardCb = Marshal.GetFunctionPointerForDelegate(_writeClipboardCb),
            CloseSurfaceCb = Marshal.GetFunctionPointerForDelegate(_closeSurfaceCb),
        };

        _app = NativeMethods.AppNew(runtime, _config);
    }

    /// <summary>
    /// Construct a per-window GhosttyHost that wraps an existing
    /// process-global <see cref="GhosttyApp"/> owned by
    /// <c>App.xaml.cs</c>. Each per-window host has its OWN per-window
    /// <see cref="_surfaces"/> dictionary. The app handle is NOT freed
    /// on <see cref="Dispose"/>.
    ///
    /// CRITICAL: This ctor does NOT assign callback delegates
    /// (<c>_wakeupCb</c>, <c>_actionCb</c>, etc). Libghostty's
    /// <c>AppNew</c> was called in the BOOTSTRAP host and bound to the
    /// bootstrap's delegate instances. The bootstrap host is the
    /// callback receiver; it forwards to the correct per-window host
    /// via <c>App._hostBySurface</c>.
    /// </summary>
    public GhosttyHost(
        DispatcherQueue dispatcher,
        IntPtr sharedApp,
        HostLifetimeSupervisor supervisor,
        ILoggerFactory loggerFactory)
    {
        _dispatcher = dispatcher;
        _ownership = new SupervisedOwnership(
            supervisor.RegisterPerWindow(),
            supervisor);
        _app = new GhosttyApp(sharedApp);
        // Per-window hosts do not own or read _config; the bootstrap host
        // manages the single GhosttyConfig. Left as default intentionally.

        // NOTE: NO callback delegate assignments here. See the ctor
        // docstring above for the full reason. Libghostty calls the
        // bootstrap host's _actionCb etc, not ours.

        var clipboardBackend = new WinUiClipboardBackend(
            _dispatcher,
            loggerFactory.CreateLogger<WinUiClipboardBackend>());
        var clipboardConfirmer = new DialogClipboardConfirmer(
            _dispatcher,
            xamlRootProvider: ResolveXamlRootForSurface,
            loggerFactory.CreateLogger<DialogClipboardConfirmer>());
        var clipboardService = new ClipboardService(clipboardBackend, clipboardConfirmer);
        _clipboardBridge = new ClipboardBridge(
            _dispatcher,
            clipboardService,
            resolveSurface: ResolveSurfaceFromUserdata,
            isSurfaceAlive: IsSurfaceAlive,
            loggerFactory.CreateLogger<ClipboardBridge>());
    }

    /// <summary>
    /// Returns true if <paramref name="control"/> is registered in this
    /// host's per-window surface dictionary. Used by the process-wide
    /// <see cref="App.TryFindHostForControl"/> search.
    /// </summary>
    internal bool ContainsControl(TerminalControl control)
    {
        foreach (var tc in _surfaces.Values)
        {
            if (ReferenceEquals(tc, control))
                return true;
        }
        return false;
    }

    public void Register(GhosttySurface surface, TerminalControl control)
    {
        if (surface.Handle == IntPtr.Zero) return;
        var added = _surfaces.TryAdd(surface.Handle, control);
        Debug.Assert(added, "surface handle collision in GhosttyHost registry");
        Ghostty.App.RegisterSurfaceRoute(surface.Handle, this);
    }

    public void Unregister(GhosttySurface surface)
    {
        if (surface.Handle == IntPtr.Zero) return;
        _surfaces.TryRemove(surface.Handle, out _);
        Ghostty.App.UnregisterSurfaceRoute(surface.Handle, this);
    }

    /// <summary>
    /// Move a surface-handle registration into this host's per-window
    /// dictionary, and update the process-wide routing map so the
    /// bootstrap host's callbacks will route to this host next. Mirror
    /// of <see cref="Unregister"/> on the source host plus
    /// <see cref="Register"/> on this one, intended for cross-window
    /// pane reparenting via <see cref="Ghostty.Panes.PaneHost.RehostTo"/>.
    /// UI thread only.
    ///
    /// Race window: between the source host's <see cref="Detach"/> and
    /// this host's <see cref="Adopt"/>, a libghostty callback for the
    /// moving surface can arrive, consult <see cref="App.TryGetHostForSurface"/>,
    /// miss, and silently drop. The spec (Risk 3) already accepts this:
    /// "one update lost is tolerable". An async progress state will
    /// resynchronize on the next OSC 9;4.
    /// </summary>
    public void Adopt(GhosttySurface surface, TerminalControl control)
    {
        if (surface.Handle == IntPtr.Zero) return;
        _surfaces[surface.Handle] = control;
        Ghostty.App.RegisterSurfaceRoute(surface.Handle, this);
    }

    /// <summary>
    /// Remove a surface-handle registration from this host's per-window
    /// dictionary and the process-wide routing map. Pair with
    /// <see cref="Adopt"/> on the target host. UI thread only.
    /// </summary>
    public void Detach(GhosttySurface surface)
    {
        if (surface.Handle == IntPtr.Zero) return;
        _surfaces.TryRemove(surface.Handle, out _);
        Ghostty.App.UnregisterSurfaceRoute(surface.Handle, this);
    }

    /// <summary>
    /// Notify all surfaces owned by this host that the OS color scheme
    /// changed. Mirrors GTK's handleStyleManagerDark which calls
    /// surface.core().colorSchemeCallback() for each surface after
    /// the app-level colorSchemeEvent.
    ///
    /// UI thread only. <see cref="Adopt"/> / <see cref="Detach"/>
    /// mutate <see cref="_surfaces"/> on the UI thread by contract; the
    /// only background-thread writers that can coexist are the
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>'s own snapshot
    /// enumerator guarantees. Called from <c>MainWindow</c> inside a
    /// <c>DispatcherQueue.TryEnqueue</c> after <c>UISettings.ColorValuesChanged</c>.
    /// </summary>
    internal void NotifyColorSchemeChanged(GhosttyColorScheme scheme)
    {
        foreach (var (handle, _) in _surfaces)
        {
            var surface = new GhosttySurface(handle);
            NativeMethods.SurfaceSetColorScheme(surface, scheme);
        }
    }

    /// <summary>
    /// Bootstrap/per-window dispose invariant: the bootstrap host
    /// (<see cref="IAppHandleOwnership.State"/>.<c>IsBootstrap</c>)
    /// MUST be disposed LAST, after every per-window host. App.xaml.cs's
    /// <c>OnAnyWindowClosedInternal</c> handler enforces this by only
    /// disposing the bootstrap host when <c>WindowsByRoot</c> is empty.
    /// Disposing out of order trips the
    /// <see cref="HostLifetimeSupervisor.NotifyDisposed"/> guard.
    /// </summary>
    public void Dispose()
    {
        // Clear this host's surfaces before touching the app.
        _surfaces.Clear();

        // Remove any entries we own from the process-wide routing map.
        Ghostty.App.UnregisterHostSurfaces(this);

        // Mark disposed BEFORE notifying the supervisor. If
        // NotifyDisposed throws (drain-last violation), the state
        // is still correctly flagged as disposed.
        _ownership.State.MarkDisposed();
        _ownership.NotifyDisposed();

        // Only the bootstrap host frees the app. The drain-last
        // invariant (enforced by _ownership.NotifyDisposed above)
        // guarantees every per-window host has already cleared its
        // _surfaces and its _hostBySurface entries by the time we
        // reach this line, so AppFree is safe.
        if (_ownership.State.OwnsApp && _app.Handle != IntPtr.Zero)
        {
            // Hard assert: no stray surface entries remain in the
            // routing map. If this fires, some per-window host
            // leaked a Register without a matching Detach.
            Debug.Assert(
                Ghostty.App.HostBySurfaceCount == 0,
                "Bootstrap host disposing with live routing entries.");
            NativeMethods.AppFree(_app);
        }

        // Config lifetime is owned by ConfigService; do not free here.
        _app = default;
        _config = default;
        _wakeupCb = null;
        _actionCb = null;
        _readClipboardCb = null;
        _confirmReadClipboardCb = null;
        _writeClipboardCb = null;
        _closeSurfaceCb = null;
        _clipboardBridge = null;
    }

    private void OnWakeup(IntPtr userdata)
    {
        // Fires on libghostty's thread. Hop to the UI dispatcher so the
        // tick (and any resulting draws) lands on the right queue.
        _dispatcher.TryEnqueue(() =>
        {
            if (_app.Handle != IntPtr.Zero) NativeMethods.AppTick(_app);
        });
    }

    // ghostty_target_s tag values, mirroring ghostty.h ghostty_target_tag_e.
    private const int GhosttyTargetApp = 0;
    private const int GhosttyTargetSurface = 1;

    /// <summary>
    /// Try to resolve the per-window host that currently owns the
    /// given surface handle. Checks this host's own _surfaces first
    /// (fast path for single-window), then falls back to the
    /// process-wide App._hostBySurface routing map.
    /// </summary>
    private bool TryResolveControl(IntPtr surfaceHandle, out TerminalControl? control)
    {
        // Fast path: surface is in this host's own dictionary.
        if (_surfaces.TryGetValue(surfaceHandle, out control))
            return true;

        // Multi-window path: consult the process-wide routing map.
        if (Ghostty.App.TryGetHostForSurface(surfaceHandle, out var targetHost) && targetHost is not null)
        {
            if (targetHost._surfaces.TryGetValue(surfaceHandle, out control))
                return true;
        }

        control = null;
        return false;
    }

    private byte OnAction(GhosttyApp _, IntPtr targetPtr, IntPtr actionPtr)
    {
        if (actionPtr == IntPtr.Zero || targetPtr == IntPtr.Zero) return 0;

        var tag = (GhosttyActionTag)Marshal.ReadInt32(actionPtr);
        var targetTag = Marshal.ReadInt32(targetPtr);
        if (targetTag == GhosttyTargetApp)
        {
            switch (tag)
            {
                case GhosttyActionTag.OpenConfig:
                    _dispatcher.TryEnqueue(() =>
                        OpenConfigRequested?.Invoke(this, EventArgs.Empty));
                    return 1;

                case GhosttyActionTag.ReloadConfig:
                    _dispatcher.TryEnqueue(() =>
                        ReloadConfigRequested?.Invoke(this, EventArgs.Empty));
                    return 1;

                default:
                    return 0;
            }
        }

        if (targetTag != GhosttyTargetSurface) return 0;
        var surfaceHandle = Marshal.ReadIntPtr(targetPtr, 8);
        if (!TryResolveControl(surfaceHandle, out var control) || control is null) return 0;

        switch (tag)
        {
            case GhosttyActionTag.ToggleCommandPalette:
                _dispatcher.TryEnqueue(() =>
                    CommandPaletteToggleRequested?.Invoke(this, EventArgs.Empty));
                return 1;

            case GhosttyActionTag.SetTitle:
            {
                var titlePtr = Marshal.ReadIntPtr(actionPtr, 8);
                var title = Marshal.PtrToStringUTF8(titlePtr) ?? string.Empty;
                _dispatcher.TryEnqueue(() =>
                {
                    if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                        c.RaiseTitleChanged(title);
                });
                return 1;
            }

            case GhosttyActionTag.RingBell:
            {
                PInvoke.MessageBeep(MESSAGEBOX_STYLE.MB_OK);
                return 1;
            }

            case GhosttyActionTag.CloseWindow:
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                        c.RaiseCloseRequested();
                });
                return 1;
            }

            case GhosttyActionTag.Scrollbar:
            {
                GhosttyActionScrollbar s;
                unsafe
                {
                    s = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<GhosttyActionScrollbar>(
                        (void*)(actionPtr + 8));
                }

                if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                    c.QueueScrollbarChanged(s.Total, s.Offset, s.Len);
                return 1;
            }

            case GhosttyActionTag.ProgressReport:
            {
                var state = (GhosttyProgressState)Marshal.ReadInt32(actionPtr, 8);
                var rawPct = (sbyte)Marshal.ReadByte(actionPtr, 12);
                int pct = rawPct < 0 ? 0 : rawPct;
                var tabState = state switch
                {
                    GhosttyProgressState.Remove        => Ghostty.Core.Tabs.TabProgressState.None,
                    GhosttyProgressState.Set           => Ghostty.Core.Tabs.TabProgressState.Normal(pct),
                    GhosttyProgressState.Error         => Ghostty.Core.Tabs.TabProgressState.Error(pct),
                    GhosttyProgressState.Indeterminate => Ghostty.Core.Tabs.TabProgressState.Indeterminate,
                    GhosttyProgressState.Pause         => Ghostty.Core.Tabs.TabProgressState.Paused(pct),
                    _ => Ghostty.Core.Tabs.TabProgressState.None,
                };
                _dispatcher.TryEnqueue(() =>
                {
                    if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                        c.RaiseProgressChanged(tabState);
                });
                return 1;
            }

            default:
                return 0;
        }
    }

    private byte OnReadClipboard(IntPtr userdata, GhosttyClipboard kind, IntPtr state)
        => (_clipboardBridge?.HandleRead(userdata, kind, state) ?? false) ? (byte)1 : (byte)0;

    private void OnConfirmReadClipboard(IntPtr userdata, IntPtr str, IntPtr state, GhosttyClipboardRequest request)
        => _clipboardBridge?.HandleConfirm(userdata, str, state, request);

    private void OnWriteClipboard(IntPtr userdata, GhosttyClipboard kind, IntPtr content, UIntPtr count, byte confirm)
        => _clipboardBridge?.HandleWrite(userdata, kind, content, count, confirm != 0);

    private void OnCloseSurface(IntPtr userdata, byte processAlive)
    {
        if (userdata == IntPtr.Zero) return;

        var control = GCHandle.FromIntPtr(userdata).Target as TerminalControl;
        if (control is null) return;
        if (!IsRegistered(control)) return;
        _dispatcher.TryEnqueue(() =>
        {
            if (IsRegistered(control)) control.RaiseCloseRequested();
        });
    }

    private bool IsRegistered(TerminalControl control)
    {
        // Check this host's own _surfaces first.
        foreach (var c in _surfaces.Values)
            if (ReferenceEquals(c, control)) return true;

        // Check all per-window hosts via the process-wide routing map.
        // This handles the case where a surface was moved to another
        // window's host but the callback still arrived on the bootstrap.
        if (Ghostty.App.TryFindHostForControl(control, out _))
            return true;

        return false;
    }

    // Clipboard bridge helpers -------------------------------------------

    private IntPtr ResolveSurfaceFromUserdata(IntPtr userdata)
    {
        if (userdata == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var handle = GCHandle.FromIntPtr(userdata);
            if (handle.IsAllocated && handle.Target is TerminalControl ctrl)
                return ctrl.SurfaceHandle;
        }
        catch (InvalidOperationException) { }
        return IntPtr.Zero;
    }

    private bool IsSurfaceAlive(IntPtr surface)
    {
        if (surface == IntPtr.Zero) return false;
        // Check this host's own dictionary first.
        if (_surfaces.ContainsKey(surface)) return true;
        // Fall back to process-wide routing map for multi-window.
        return Ghostty.App.TryGetHostForSurface(surface, out _);
    }

    private XamlRoot? ResolveXamlRootForSurface(IntPtr surface)
    {
        // Look up the TerminalControl that owns this specific surface so
        // the confirmation dialog lands on the originating window.
        if (surface != IntPtr.Zero)
        {
            // Check this host first.
            if (_surfaces.TryGetValue(surface, out var owner))
            {
                var ownerRoot = owner.XamlRoot;
                if (ownerRoot is not null) return ownerRoot;
            }
            // Fall back to process-wide routing.
            if (Ghostty.App.TryGetHostForSurface(surface, out var targetHost) && targetHost is not null)
            {
                if (targetHost._surfaces.TryGetValue(surface, out var remoteOwner))
                {
                    var remoteRoot = remoteOwner.XamlRoot;
                    if (remoteRoot is not null) return remoteRoot;
                }
            }
        }

        // Last resort: any live control in this host.
        foreach (var ctrl in _surfaces.Values)
        {
            var root = ctrl.XamlRoot;
            if (root is not null) return root;
        }
        return null;
    }
}
