using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Ghostty.Clipboard;
using Ghostty.Controls;
using Ghostty.Core.Clipboard;
using Ghostty.Core.Hosting;
using Ghostty.Core.Input;
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

    private readonly ILogger<GhosttyHost> _logger;

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
    /// Raised when libghostty matches a keybind that resolves to a pane/tab
    /// apprt action this window should perform (new tab/split, focus or
    /// resize a split, switch/move tabs, fullscreen, zoom, equalize). The
    /// decoded payload is already mapped to a
    /// <see cref="Ghostty.Core.Input.PaneAction"/>; <c>MainWindow</c> forwards
    /// it straight into the <c>PaneActionRouter</c>. Raised on the surface's
    /// owning per-window host, mirroring <see cref="CommandPaletteToggleRequested"/>.
    /// </summary>
    public event Action<Ghostty.Core.Input.PaneAction>? PaneActionRequested;

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

    // Process-wide toast sink. Constructed in both ctors (stateless;
    // AppNotificationManager.Default is itself a singleton). The bootstrap
    // host uses it from OnAction (Show); per-window hosts use it from the
    // focus-regain clear forwarded by their controls.
    private readonly Ghostty.Core.Notifications.IToastNotifier _toasts;

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
        _logger = loggerFactory.CreateLogger<GhosttyHost>();
        _toasts = new Ghostty.Notifications.AppNotificationToastNotifier(
            loggerFactory.CreateLogger<Ghostty.Notifications.AppNotificationToastNotifier>());

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
        _logger = loggerFactory.CreateLogger<GhosttyHost>();
        _toasts = new Ghostty.Notifications.AppNotificationToastNotifier(
            loggerFactory.CreateLogger<Ghostty.Notifications.AppNotificationToastNotifier>());
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
        // Native callback on libghostty's thread (see OnAction for why the
        // boundary is guarded). Hop to the UI dispatcher so the tick and any
        // resulting draws land on the right queue.
        try
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_app.Handle != IntPtr.Zero) NativeMethods.AppTick(_app);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnWakeup threw at the native boundary");
        }
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

        // OnAction is a native callback invoked directly by libghostty (Zig)
        // on its own thread via the function pointer in _actionCb. Letting a
        // managed exception unwind across the ABI back into Zig is undefined
        // behavior, so guard the synchronous decode and return 0 ("not
        // handled") on failure. The _dispatcher.TryEnqueue lambdas run later
        // on the UI thread, so their exceptions are a separate concern and are
        // deliberately not covered here.
        // tag stays null until decoded so the catch does not misreport a
        // failure in the very first read as a real action.
        GhosttyActionTag? tag = null;
        try
        {
            tag = (GhosttyActionTag)Marshal.ReadInt32(actionPtr);
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

                    case GhosttyActionTag.ConfigChange:
                        // App-level config_change fires once per reload after
                        // libghostty has pushed the new config to every surface.
                        // The per-surface config_change dispatches (handled in
                        // the surface-target switch below) already force each
                        // surface to repaint, so there is nothing extra to do at
                        // the app level -- just acknowledge so libghostty stops
                        // logging it as unhandled (issue #193).
                        return 1;

                    case GhosttyActionTag.ToggleQuickTerminal:
                        // Routed to App rather than a per-window event because
                        // the quake window is a singleton owned by App; any
                        // surface (including ones in regular MainWindows) can
                        // be the source of the action.
                        _dispatcher.TryEnqueue(() =>
                            ((Ghostty.App)Microsoft.UI.Xaml.Application.Current).ToggleQuickTerminal());
                        return 1;

                    case GhosttyActionTag.MouseVisibility:
                        // Mirror mac/GTK: mouse visibility against an app target is a
                        // libghostty bug; log and absorb so we still report "handled".
                        _logger.LogWarning("MouseVisibility action received with app target; ignoring");
                        return 1;

                    default:
                        return 0;
                }
            }

            if (targetTag != GhosttyTargetSurface) return 0;
            var surfaceHandle = Marshal.ReadIntPtr(targetPtr, 8);
            if (!TryResolveControl(surfaceHandle, out var control) || control is null) return 0;

            // OnAction always runs on the bootstrap host -- the singleton
            // callback receiver. The per-window events below (CommandPalette
            // toggle, PaneActionRequested) have their subscribers on the host
            // that OWNS this surface, not on the bootstrap host. Raise them on
            // the owning host; the control-direct cases further down already
            // forward correctly because they call the resolved control itself.
            var owner = control.Host;
            if (owner is null) return 0;

            switch (tag)
            {
                case GhosttyActionTag.ToggleCommandPalette:
                    _dispatcher.TryEnqueue(() =>
                        owner.CommandPaletteToggleRequested?.Invoke(owner, EventArgs.Empty));
                    return 1;

                // Pane/tab actions libghostty matched from a keybind. The
                // payload-free tags carry no value; the directional/indexed
                // tags read their value at +8 (4-byte tag + 4-byte padding
                // before the union). ApprtActionMap turns (tag, value) into a
                // PaneAction; a null result means this apprt does not act on
                // that variant, so we return 0 and let libghostty fall back.
                case GhosttyActionTag.NewTab:
                case GhosttyActionTag.CloseTab:
                case GhosttyActionTag.ToggleFullscreen:
                case GhosttyActionTag.EqualizeSplits:
                case GhosttyActionTag.ToggleSplitZoom:
                    return DispatchPaneAction(owner, tag.Value, 0);

                case GhosttyActionTag.NewSplit:
                case GhosttyActionTag.GotoSplit:
                case GhosttyActionTag.GotoTab:
                    return DispatchPaneAction(owner, tag.Value, Marshal.ReadInt32(actionPtr, 8));

                case GhosttyActionTag.ResizeSplit:
                {
                    GhosttyActionResizeSplit rs;
                    unsafe
                    {
                        rs = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<GhosttyActionResizeSplit>(
                            (void*)(actionPtr + 8));
                    }
                    return DispatchPaneAction(owner, tag.Value, (int)rs.Direction);
                }

                case GhosttyActionTag.MoveTab:
                {
                    GhosttyActionMoveTab mt;
                    unsafe
                    {
                        mt = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<GhosttyActionMoveTab>(
                            (void*)(actionPtr + 8));
                    }
                    return DispatchPaneAction(owner, tag.Value, (int)mt.Amount);
                }

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

                case GhosttyActionTag.MouseShape:
                {
                    // ghostty_action_mouse_shape_e is a single c_int payload;
                    // read it at +8 (skipping the 4-byte tag + 4-byte padding
                    // before the union, same offset as ProgressReport.State).
                    var raw = Marshal.ReadInt32(actionPtr, 8);
                    var shape = (MouseShape)raw;
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                            c.SetMouseShape(shape);
                    });
                    return 1;
                }

                case GhosttyActionTag.MouseVisibility:
                {
                    // ghostty_action_mouse_visibility_e is a single c_int payload;
                    // read it at +8 (skipping the 4-byte tag + 4-byte padding before
                    // the union, same offset as MouseShape and ProgressReport).
                    var raw = Marshal.ReadInt32(actionPtr, 8);
                    var visibility = (MouseVisibility)raw;
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                            c.SetMouseVisibility(visibility);
                    });
                    return 1;
                }

                case GhosttyActionTag.MouseOverLink:
                {
                    GhosttyActionMouseOverLink payload;
                    unsafe
                    {
                        payload = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<GhosttyActionMouseOverLink>(
                            (void*)(actionPtr + 8));
                    }
                    // libghostty sends url=null+len=0 when the pointer leaves a link;
                    // surface that as a null hovered-URL so the C# side can clear its
                    // own hover state cleanly. Use the length-aware PtrToStringUTF8
                    // overload (vs SetTitle's null-terminated variant) because this
                    // payload carries an explicit length and the string is NOT
                    // guaranteed null-terminated. Guard the nuint -> int cast: OSC 8
                    // URLs are typically <2 KB but libghostty makes no upper-bound
                    // guarantee, and Marshal.PtrToStringUTF8(IntPtr, int) takes a
                    // signed length — silent truncation would corrupt the URL.
                    string? url;
                    if (payload.Url == IntPtr.Zero)
                    {
                        url = null;
                    }
                    else if (payload.Len > int.MaxValue)
                    {
                        // Pathologically long URL — drop the hover update rather
                        // than truncate. Realistic OSC 8 URLs never approach this.
                        return 1;
                    }
                    else
                    {
                        url = Marshal.PtrToStringUTF8(payload.Url, (int)payload.Len);
                    }
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                            c.SetHoveredLink(url);
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

                case GhosttyActionTag.StartSearch:
                {
                    // ghostty_action_start_search_s: { const char* needle; }
                    // Pointer at +8; decode the null-terminated UTF-8 needle
                    // off the libghostty thread to avoid touching it after the
                    // action call returns and libghostty frees the buffer.
                    var needlePtr = Marshal.ReadIntPtr(actionPtr, 8);
                    var needle = needlePtr == IntPtr.Zero
                        ? string.Empty
                        : Marshal.PtrToStringUTF8(needlePtr) ?? string.Empty;
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                            c.OnSearchStarted(needle);
                    });
                    return 1;
                }

                case GhosttyActionTag.EndSearch:
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                            c.OnSearchEnded();
                    });
                    return 1;
                }

                case GhosttyActionTag.SearchTotal:
                {
                    // ssize_t total; at +8. Read as nint so the layout matches
                    // the C ssize_t on both 32- and 64-bit builds; cast to long
                    // for the SearchState API.
                    var total = (long)Marshal.ReadIntPtr(actionPtr, 8);
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                            c.OnSearchTotalChanged(total);
                    });
                    return 1;
                }

                case GhosttyActionTag.SearchSelected:
                {
                    // ssize_t selected; at +8. -1 (or any negative) means no
                    // match is selected yet -- SearchState normalises display.
                    var selected = (long)Marshal.ReadIntPtr(actionPtr, 8);
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                            c.OnSearchSelectedChanged(selected);
                    });
                    return 1;
                }

                case GhosttyActionTag.PromptReady:
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                            c.RaisePromptReady();
                    });
                    return 1;
                }

                case GhosttyActionTag.FirstRender:
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                            c.RaiseFirstRender();
                    });
                    return 1;
                }

                case GhosttyActionTag.ConfigChange:
                {
                    // A live config/theme reload pushed new config into this
                    // surface. libghostty re-resolves default-colored cells,
                    // cursor style, font metrics, etc. on the next rebuild, but
                    // on Windows nothing otherwise forces that frame to be drawn
                    // after a reload -- so existing terminal content stays stale
                    // until the next keystroke. Force an immediate repaint so the
                    // re-resolved frame is presented (issues #193, #244).
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (TryResolveControl(surfaceHandle, out var c) && c is not null)
                            c.RequestRepaint();
                    });
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

                case GhosttyActionTag.DesktopNotification:
                {
                    // ghostty_action_desktop_notification_s:
                    //   { const char* title; const char* body; }
                    // title@+8, body@+16. The core already gated this on the
                    // `desktop-notifications` config before dispatching, so no
                    // config check here. Copy the strings on the libghostty
                    // thread before the buffer frees, then decide + show on the
                    // UI thread where focus state is valid.
                    var titlePtr = Marshal.ReadIntPtr(actionPtr, 8);
                    var bodyPtr = Marshal.ReadIntPtr(actionPtr, 16);
                    var title = Marshal.PtrToStringUTF8(titlePtr) ?? string.Empty;
                    var body = Marshal.PtrToStringUTF8(bodyPtr) ?? string.Empty;
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (!TryResolveControl(surfaceHandle, out var c) || c is null) return;
                        var req = Ghostty.Core.Notifications.NotificationPolicy.DesktopNotification(
                            title, body, c.ToastSurfaceKey, c.IsFocused);
                        if (req is not null) _toasts.Show(req);
                    });
                    return 1;
                }

                case GhosttyActionTag.ShowChildExited:
                {
                    // ghostty_surface_message_childexited_s:
                    //   { uint32 exit_code; uint64 runtime_ms; }
                    // The union sits at +8; within it exit_code@+0 and the
                    // 8-byte-aligned runtime_ms@+8, so read the struct at +8.
                    GhosttyChildExited info;
                    unsafe
                    {
                        info = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<GhosttyChildExited>(
                            (void*)(actionPtr + 8));
                    }
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (!TryResolveControl(surfaceHandle, out var c) || c is null) return;
                        var req = Ghostty.Core.Notifications.NotificationPolicy.ChildExited(
                            info.ExitCode, info.RuntimeMs, c.ToastSurfaceKey, c.IsFocused);
                        if (req is not null) _toasts.Show(req);
                    });
                    // Return 0 ("not handled") so the core keeps its in-terminal
                    // "Process exited. Press any key to close" fallback. The
                    // toast is additive, not a replacement for that affordance.
                    return 0;
                }

                default:
                    return 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnAction threw while handling {Tag}", tag);
            return 0;
        }
    }

    /// <summary>
    /// Map a surface-targeted pane/tab action tag (with its already decoded
    /// value) to a <see cref="Ghostty.Core.Input.PaneAction"/> and raise it
    /// on the surface's owning per-window host (where MainWindow subscribed),
    /// not the bootstrap host this callback runs on. Returns 1 when mapped
    /// and dispatched, 0 when this apprt does not act on the variant so
    /// libghostty can fall back.
    /// </summary>
    private byte DispatchPaneAction(GhosttyHost owner, GhosttyActionTag tag, int value)
    {
        var action = ApprtActionMap.Map(tag, value);
        if (action is not { } paneAction) return 0;

        _dispatcher.TryEnqueue(() =>
            owner.PaneActionRequested?.Invoke(paneAction));
        return 1;
    }

    /// <summary>
    /// Forwarded from a TerminalControl when its surface regains focus:
    /// remove any background toast we raised for that surface.
    /// </summary>
    internal void ClearSurfaceToasts(string surfaceKey) => _toasts.ClearForSurface(surfaceKey);

    /// <summary>
    /// Raise <see cref="PaneActionRequested"/> for a chord that the
    /// Windows-only residual matcher in <see cref="Controls.TerminalControl"/>
    /// resolved. libghostty owns matching for every standard action; the few
    /// Windows-only chords have no libghostty action, so the apprt matches
    /// them itself and feeds them through the same event MainWindow already
    /// forwards to the PaneActionRouter. The caller runs on the UI thread
    /// (a key-event handler), so no dispatch hop is needed.
    /// </summary>
    public void RequestPaneAction(Ghostty.Core.Input.PaneAction action) =>
        PaneActionRequested?.Invoke(action);

    // The clipboard and close-surface callbacks below are also invoked
    // directly by libghostty on its own thread (see OnAction for the
    // native-boundary rationale). Their synchronous bodies decode raw
    // pointers and handles -- PtrToStringUTF8, the content marshaller,
    // GCHandle.FromIntPtr -- any of which can throw, so each guards the
    // boundary and swallows (logs) rather than letting the exception cross
    // the ABI into Zig. The bridge's deferred dispatcher work self-guards.
    private byte OnReadClipboard(IntPtr userdata, GhosttyClipboard kind, IntPtr state)
    {
        try
        {
            return (_clipboardBridge?.HandleRead(userdata, kind, state) ?? false) ? (byte)1 : (byte)0;
        }
        catch (Exception ex)
        {
            // Returning 0 ("not handled") here cannot strand a request:
            // HandleRead only throws on its synchronous prefix, before it
            // returns true and takes on the obligation to complete the
            // clipboard request via SurfaceCompleteClipboardRequest.
            _logger.LogError(ex, "OnReadClipboard threw at the native boundary");
            return 0;
        }
    }

    private void OnConfirmReadClipboard(IntPtr userdata, IntPtr str, IntPtr state, GhosttyClipboardRequest request)
    {
        try
        {
            _clipboardBridge?.HandleConfirm(userdata, str, state, request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnConfirmReadClipboard threw at the native boundary");
        }
    }

    private void OnWriteClipboard(IntPtr userdata, GhosttyClipboard kind, IntPtr content, UIntPtr count, byte confirm)
    {
        try
        {
            _clipboardBridge?.HandleWrite(userdata, kind, content, count, confirm != 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnWriteClipboard threw at the native boundary");
        }
    }

    private void OnCloseSurface(IntPtr userdata, byte processAlive)
    {
        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnCloseSurface threw at the native boundary");
        }
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
