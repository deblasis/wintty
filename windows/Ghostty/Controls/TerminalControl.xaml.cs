using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Ghostty.Core.Input;
using Ghostty.Core.ResizeOverlay;
using Ghostty.Core.Windows;
using Ghostty.Core.Search;
using Ghostty.Hosting;
using Ghostty.Input;
using Ghostty.Interop;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Ghostty.Controls;

/// <summary>
/// Single libghostty-backed terminal surface, hosted via WinUI 3
/// SwapChainPanel. Matches how macOS's Ghostty.Surface.swift owns
/// one ghostty_surface_t per SwiftUI view.
///
/// Config and app handle ownership lives in <see cref="Ghostty.Hosting.GhosttyHost"/>,
/// which is constructed by MainWindow and assigned via the Host property before load.
/// </summary>
public sealed partial class TerminalControl : UserControl, ISearchHost
{
    /// <summary>
    /// Set by MainWindow when the command palette opens/closes. When true,
    /// OnKeyDown returns immediately so keystrokes go to the palette's
    /// TextBox instead of libghostty. Instance-scoped so multi-window
    /// does not suppress input in unrelated windows.
    /// </summary>
    internal bool CommandPaletteIsOpen { get; set; }

    // Handles ------------------------------------------------------------

    private GhosttySurface _surface;
    private IntPtr _workingDirectoryUtf8;
    private IntPtr _commandUtf8;
    private IntPtr _initialInputUtf8;

    // The libghostty surface lifecycle is decoupled from
    // OnLoaded/OnUnloaded so that visual-tree reparenting (which fires
    // Unloaded then Loaded asynchronously) does NOT tear down the
    // running shell. The surface is created once at first Loaded and
    // freed only when PaneHost calls DisposeSurface() on the leaf
    // being closed (or when the last leaf in the window is removed).
    //
    // Without this decoupling, every pane split would Unloaded ->
    // SurfaceFree -> Loaded -> SurfaceNew on every existing leaf,
    // killing each running shell process and replacing it with a fresh
    // one. Worse, async event ordering can deliver Unloaded AFTER the
    // matching Loaded, leaving a leaf in a half-dead state with no
    // surface and no path to recover.
    private bool _surfaceCreated;
    private bool _surfaceDisposed;

    // Set in OnKeyDown when we short-circuit a bound chord; consumed
    // (and cleared) by the matching OnCharacterReceived. WinUI 3 fires
    // BOTH OnKeyDown (raw key) and OnCharacterReceived (WM_CHAR text)
    // for the same physical keypress, and they take INDEPENDENT paths
    // into libghostty (SurfaceKey vs SurfaceText). Filtering OnKeyDown
    // alone leaves OnCharacterReceived to forward the C0 control char
    // (e.g. Ctrl+E -> U+0005) which the shell happily interprets as
    // a readline command. The flag bridges the two handlers without
    // requiring CharacterReceived to re-derive the original VirtualKey.
    private bool _suppressNextCharacter;

    // Set while RaiseScrollbarChanged is writing into VerticalScrollBar.
    // Prevents the resulting Scroll event from round-tripping back into
    // libghostty as a "scroll_to_row" binding action (feedback loop).
    private bool _suppressScrollEvent;

    // Latest scrollbar state pushed from libghostty's thread. Read on
    // the UI thread by FlushPendingScrollbar. Guarded by _scrollbarLock
    // so the three row counts are read coherently.
    private readonly object _scrollbarLock = new();
    private ulong _pendingScrollbarTotal;
    private ulong _pendingScrollbarOffset;
    private ulong _pendingScrollbarLen;
    private bool _pendingScrollbarDirty;

    // Cached dispatcher delegate — avoids allocating a
    // DispatcherQueueHandler on every scrollbar update.
    private Microsoft.UI.Dispatching.DispatcherQueueHandler? _flushScrollbarHandler;

    // Pinned managed handle to `this`, passed to libghostty as the
    // per-surface userdata. Per-surface callbacks (close_surface_cb,
    // read/write clipboard) receive this pointer back so GhosttyHost can
    // resolve a callback to the owning TerminalControl without scanning
    // the surface map. Allocated immediately before SurfaceNew, freed in
    // OnUnloaded after SurfaceFree so the GC cannot move or collect this
    // control while libghostty still holds a reference.
    private GCHandle _selfHandle;

    /// <summary>
    /// The per-window libghostty host that owns the config and app
    /// handles. Must be assigned before the control loads.
    /// </summary>
    internal GhosttyHost? Host { get; set; }

    /// <summary>
    /// Profile snapshot the terminal was opened with, or null for the
    /// legacy no-profile path (cold-start fallback, keyboard
    /// Alt+Shift+D split). Set by <see cref="Ghostty.Tabs.PaneHostFactory"/>
    /// before the control loads. Read once in OnLoaded to populate
    /// surfaceConfig.Command and surfaceConfig.WorkingDirectory; ignored
    /// thereafter.
    /// </summary>
    internal Ghostty.Core.Profiles.ProfileSnapshot? Snapshot { get; set; }

    /// <summary>
    /// The raw libghostty surface handle for this control. Used by
    /// <see cref="Ghostty.Hosting.GhosttyHost"/> to resolve a per-surface
    /// userdata pointer back to the handle for clipboard callback completion.
    /// Returns <see cref="IntPtr.Zero"/> before the surface is created or
    /// after it is disposed.
    /// </summary>
    internal IntPtr SurfaceHandle => _surface.Handle;

    /// <summary>
    /// Schedule an immediate repaint of this surface. Used by the
    /// <c>config_change</c> action handler so a live config/theme reload
    /// presents a fresh frame: libghostty re-resolves default-colored cells,
    /// cursor style, font metrics, etc. against the new config on the next
    /// rebuild, but on Windows nothing otherwise forces that frame to be
    /// drawn after a reload (issues #193, #244). Must be called on the UI
    /// thread. No-op once the surface is gone so a reload racing teardown
    /// can't draw into freed native state.
    /// </summary>
    internal void RequestRepaint()
    {
        if (_surfaceDisposed || _surface.Handle == IntPtr.Zero) return;
        NativeMethods.SurfaceDraw(_surface);
    }

    /// <summary>
    /// Returns the pid of the shell process attached to this surface, or
    /// null when libghostty cannot report one (surface not yet created,
    /// already disposed, or the platform's pty layer is a stub). The
    /// active-process tracker uses this to root its descendant walk.
    /// On Windows today this currently returns null because
    /// <c>WindowsPty.getProcessInfo</c> upstream still returns 0; the
    /// wiring is in place so the tracker picks up the pid automatically
    /// once libghostty exposes it.
    /// </summary>
    internal int? TryGetShellPid()
    {
        if (_surface.Handle == IntPtr.Zero) return null;
        var pid = NativeMethods.SurfaceForegroundPid(_surface);
        if (pid == 0) return null;
        // The C api uses u64 to match macOS/Linux pids in unsigned form;
        // Windows process ids are DWORDs and Toolhelp32 takes uint, but
        // .NET's Process.Id is int and our tracker stores int, so cast
        // through int for the contract. Clamp to int.MaxValue defensively.
        return pid > int.MaxValue ? null : (int)pid;
    }

    // The raw title libghostty last pushed (shell OSC 0/2 or set_title).
    private string? _shellTitle;
    // The user's explicit per-surface override (prompt_title surface mode).
    // Beats the shell title; null means "follow the shell".
    private string? _userTitleOverride;

    /// <summary>
    /// Effective title for this surface: the user's per-surface override if
    /// set, otherwise the shell-reported title. Read by MainWindow's title
    /// coordinator to populate the tab label on focus change, so a pane with
    /// an override shows that override when focused.
    /// </summary>
    public string? CurrentTitle => _userTitleOverride ?? _shellTitle;

    // Raisers invoked by GhosttyHost after routing an action to this leaf.
    internal void RaiseTitleChanged(string title)
    {
        _shellTitle = title;
        TitleChanged?.Invoke(this, CurrentTitle ?? string.Empty);
    }

    /// <summary>
    /// Set (or clear, with null/whitespace) the user's per-surface title
    /// override. Fires TitleChanged with the new effective title.
    /// </summary>
    internal void SetUserTitleOverride(string? title)
    {
        _userTitleOverride = string.IsNullOrWhiteSpace(title) ? null : title;
        TitleChanged?.Invoke(this, CurrentTitle ?? string.Empty);
    }
    internal void RaiseCloseRequested() => CloseRequested?.Invoke(this, EventArgs.Empty);
    internal void RaiseProgressChanged(Ghostty.Core.Tabs.TabProgressState state)
    {
        CurrentProgress = state;
        ProgressChanged?.Invoke(this, state);
    }
    internal void RaisePromptReady() => PromptReady?.Invoke(this, EventArgs.Empty);
    internal void RaiseFirstRender() => FirstRender?.Invoke(this, EventArgs.Empty);

    private bool _bellBorderActive;
    private bool _bellTitlePending;
    private BellAudioPlayer? _bellAudio;

    // Fade-out duration for the visual bell border once acknowledged.
    // Matches the macOS easeInOut(duration: 0.3) bell border animation.
    private const int BellBorderFadeMs = 300;

    /// <summary>
    /// Raise the bell for this surface with the decoded bell-features.
    /// Called on the UI thread by <c>GhosttyHost.RingBell</c>. The visual
    /// border is per-surface and shown here when <c>border</c> is enabled;
    /// the BellRang event carries the features up to the tab/window
    /// consumers, which gate the title glyph on <c>title</c> and the
    /// taskbar attention badge on <c>attention</c>.
    /// </summary>
    internal void RaiseBellRang(Ghostty.Core.Bell.BellFeatures features)
    {
        if (features.Border) ShowBellBorder();
        if (features.Title) _bellTitlePending = true;
        BellRang?.Invoke(this, features);
    }

    /// <summary>Play the configured bell audio for this surface.</summary>
    internal void PlayBellAudio(string path, double volume)
    {
        _bellAudio ??= new BellAudioPlayer(Ghostty.Logging.StaticLoggers.BellAudio);
        _bellAudio.Play(path, volume);
    }

    private void ShowBellBorder()
    {
        BellOverlay.BorderBrush = ResolveBellBrush();
        BellOverlay.Visibility = Visibility.Visible;
        BellOverlay.Opacity = 1.0; // persistent; no auto-fade
        _bellBorderActive = true;
    }

    private void DismissBellBorder()
    {
        if (!_bellBorderActive) return;
        _bellBorderActive = false;

        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(BellBorderFadeMs)),
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, BellOverlay);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(fade);
        sb.Completed += (_, _) =>
        {
            // Only collapse if no new bell re-armed the border mid-fade.
            if (!_bellBorderActive) BellOverlay.Visibility = Visibility.Collapsed;
        };
        sb.Begin();
    }

    /// <summary>
    /// Acknowledge any pending bell on this surface: fade the border and
    /// tell the tab to clear its indicator. Invoked on focus gain and on
    /// keystroke, matching macOS/GTK dismissal.
    /// </summary>
    private void AcknowledgeBell()
    {
        DismissBellBorder();
        if (_bellTitlePending)
        {
            _bellTitlePending = false;
            BellAcknowledged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Microsoft.UI.Xaml.Media.Brush ResolveBellBrush()
    {
        // Tint with the system accent, matching macOS/GTK which use the
        // accent color for the bell border.
        if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var c)
            && c is Windows.UI.Color color)
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
    }

    // Called on the libghostty thread. Stashes the latest state and
    // enqueues a single UI-thread flush. Coalescing: if libghostty
    // emits multiple updates before the UI thread catches up, the
    // cached delegate runs once and reads the most recent values.
    internal void QueueScrollbarChanged(ulong total, ulong offset, ulong len)
    {
        bool needEnqueue;
        lock (_scrollbarLock)
        {
            _pendingScrollbarTotal = total;
            _pendingScrollbarOffset = offset;
            _pendingScrollbarLen = len;
            needEnqueue = !_pendingScrollbarDirty;
            _pendingScrollbarDirty = true;
        }
        if (needEnqueue)
        {
            _flushScrollbarHandler ??= FlushPendingScrollbar;
            DispatcherQueue.TryEnqueue(_flushScrollbarHandler);
        }
    }

    // UI thread. Reads the latest coalesced state and writes it into
    // the overlay ScrollBar. Guards against the feedback loop where
    // assigning ScrollBar.Value re-fires Scroll and round-trips back
    // into libghostty.
    private void FlushPendingScrollbar()
    {
        ulong total, offset, len;
        lock (_scrollbarLock)
        {
            total = _pendingScrollbarTotal;
            offset = _pendingScrollbarOffset;
            len = _pendingScrollbarLen;
            _pendingScrollbarDirty = false;
        }

        // total <= len means there is nothing off-screen to scroll to;
        // hide the bar entirely to match native "no overflow, no chrome"
        // behavior (Explorer, Edge).
        if (total <= len)
        {
            VerticalScrollBar.Visibility = Visibility.Collapsed;
            return;
        }

        // ScrollBar uses double. uint64 row counts beyond 2^53 would lose
        // precision but that would require a multi-petabyte scrollback.
        var maximum = (double)(total - len);
        var viewport = (double)len;
        var value = Math.Min((double)offset, maximum);

        _suppressScrollEvent = true;
        try
        {
            VerticalScrollBar.Maximum = maximum;
            VerticalScrollBar.ViewportSize = viewport;
            // LargeChange = page, SmallChange = single row — matches the
            // arrow-click / page-click behavior of native Windows apps.
            VerticalScrollBar.LargeChange = viewport;
            VerticalScrollBar.SmallChange = 1;
            VerticalScrollBar.Value = value;
            VerticalScrollBar.Visibility = Visibility.Visible;
        }
        finally
        {
            _suppressScrollEvent = false;
        }
    }

    private void OnScrollBarScroll(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.ScrollEventArgs e)
    {
        if (_suppressScrollEvent) return;
        if (_surface.Handle == IntPtr.Zero) return;

        // ScrollBar already clamps NewValue to [Minimum, Maximum].
        var row = (ulong)Math.Round(e.NewValue);

        // Zero-alloc path: drag events fire at pointer-move rates, so
        // we format "scroll_to_row:N" straight into a stack buffer and
        // hand libghostty a raw pointer. This is the GTK apprt's
        // vadjustment-value-changed path (src/apprt/gtk/class/
        // surface.zig::vadjValueChanged); libghostty de-duplicates
        // identical rows internally so per-pixel drag noise is cheap.
        unsafe
        {
            // 14 bytes prefix + max 20 digits for ulong = 34. Round up.
            Span<byte> buf = stackalloc byte[48];
            "scroll_to_row:"u8.CopyTo(buf);
            if (!System.Buffers.Text.Utf8Formatter.TryFormat(row, buf[14..], out int digits))
                return;
            int total = 14 + digits;
            fixed (byte* p = buf)
            {
                NativeMethods.SurfaceBindingAction(_surface, p, (UIntPtr)total);
            }
        }
    }

    // Forward wheel events that land on the ScrollBar overlay region
    // back to the existing Panel handler, so spinning the wheel near
    // the right edge still scrolls the terminal via libghostty's own
    // viewport path rather than being eaten by the bar.
    private void OnScrollBarPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        OnPointerWheelChanged(Panel, e);
    }

    /// <summary>Most recent OSC 9;4 state reported for this leaf.</summary>
    internal Ghostty.Core.Tabs.TabProgressState CurrentProgress { get; private set; }
        = Ghostty.Core.Tabs.TabProgressState.None;

    // Events raised from the runtime action callback. They always fire
    // on the UI thread: the callback itself runs on libghostty's thread
    // and uses DispatcherQueue.TryEnqueue before invoking these.
    //
    // MainWindow subscribes to update the window chrome.
    public event EventHandler<string>? TitleChanged;
    public event EventHandler? CloseRequested;
    internal event EventHandler<Ghostty.Core.Tabs.TabProgressState>? ProgressChanged;

    /// <summary>Raised when libghostty rings the bell for this surface,
    /// carrying the decoded bell-features. PaneHost forwards the active
    /// leaf's bell up; the tab title glyph and taskbar attention badge
    /// each gate on the carried features.</summary>
    internal event EventHandler<Ghostty.Core.Bell.BellFeatures>? BellRang;

    /// <summary>Raised when the user acknowledges the bell on this surface
    /// (focus gained or keystroke), so the tab title indicator can clear.</summary>
    internal event EventHandler? BellAcknowledged;


    /// <summary>Raised when the shell prompt becomes interactive (OSC 133;B).
    /// The first such event per surface marks the shell as responsive.</summary>
    public event EventHandler? PromptReady;

    /// <summary>Raised once, the first time this surface produces
    /// renderable content (libghostty first_render). Shell-agnostic, so
    /// it fires for any command; PaneHost uses it to end the startup
    /// glow.</summary>
    public event EventHandler? FirstRender;

    public TerminalControl()
    {
        InitializeComponent();
    }

    // Lifecycle ----------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Tree-dependent setup runs every Loaded (idempotent):
        // request focus, walk ancestors for the ScrollViewer fix, and
        // arm the one-shot LayoutUpdated handler so the surface size
        // gets primed once layout settles in the new parent.
        Panel.LayoutUpdated -= OnFirstLayoutUpdated;
        Panel.LayoutUpdated += OnFirstLayoutUpdated;
        DisableAncestorScrollViewerTabStop();

        // SearchBar lifetime matches the control's; wiring `this` as the
        // host is idempotent so doing it on every Loaded is safe and
        // survives any visual-tree reparent.
        SearchBar.SearchHost = this;

        // Surface creation runs exactly once per control instance,
        // even across multiple reparents. Subsequent Loaded events
        // skip this entire block.
        if (_surfaceCreated) return;
        _surfaceCreated = true;

        if (Host is null)
            throw new InvalidOperationException(
                "TerminalControl.Host must be set before the control loads.");

        var app = Host.App;

        // surface-config strings are non-null UTF-8 and live until the surface is freed;
        // allocate independent buffers so writes never alias.
        _workingDirectoryUtf8 = Snapshot is { WorkingDirectory: { Length: > 0 } wd }
            ? AllocUtf8(wd)
            : AllocEmptyUtf8();
        _commandUtf8 = Snapshot is { ResolvedCommand: { Length: > 0 } cmd }
            ? AllocUtf8(cmd)
            : AllocEmptyUtf8();
        _initialInputUtf8 = AllocEmptyUtf8();

        var panelPtr = SwapChainPanelInterop.QueryInterface(Panel);
        var surfaceConfig = NativeMethods.SurfaceConfigNew();
        surfaceConfig.PlatformTag = GhosttyPlatform.Windows;
        surfaceConfig.Platform.Windows = new GhosttyPlatformWindows
        {
            SwapChainPanel = panelPtr,
        };
        surfaceConfig.ScaleFactor = Panel.CompositionScaleX > 0 ? Panel.CompositionScaleX : 1.0;
        surfaceConfig.Context = GhosttySurfaceContext.Window;
        surfaceConfig.WorkingDirectory = _workingDirectoryUtf8;
        surfaceConfig.Command = _commandUtf8;
        surfaceConfig.InitialInput = _initialInputUtf8;

        // Pin a managed handle to `this` and pass it as per-surface userdata.
        // libghostty echoes this pointer back through close_surface_cb and the
        // clipboard callbacks; GhosttyHost decodes it via GCHandle.FromIntPtr
        // to dispatch the callback to the right control. Use Normal (not
        // Pinned) - we are not pinning bytes, only preventing GC collection
        // of the managed object behind the IntPtr.
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        surfaceConfig.Userdata = GCHandle.ToIntPtr(_selfHandle);

        try
        {
            _surface = NativeMethods.SurfaceNew(app, surfaceConfig);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Ghostty] SurfaceNew failed: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
        // Drop our ref: libghostty does not retain the panel pointer.
        SwapChainPanelInterop.Release(panelPtr);
        Host.Register(_surface, this);

        // Request focus so keyboard input starts flowing immediately.
        // Focus lives on the UserControl now, not the panel.
        this.Focus(FocusState.Programmatic);

    }

    private void DisableAncestorScrollViewerTabStop()
    {
        // Only the framework-injected ScrollViewer ABOVE the app content
        // root is parasitic; legitimate ScrollViewers (settings panes,
        // tab strips) are descendants of the app root and must keep their
        // tab stop so Tab navigation through them works (#160). Walk
        // nearest-first, find the app content root, and neuter only
        // ScrollViewers at or above it. If the root isn't reachable,
        // fall back to the original neuter-all behaviour.
        var appRoot = XamlRoot?.Content as DependencyObject;

        // First pass: record the index of the app content root.
        int rootIndex = -1;
        {
            int i = 0;
            DependencyObject? node = this;
            while (node is not null)
            {
                node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
                if (node is null) break;
                if (appRoot is not null && ReferenceEquals(node, appRoot))
                {
                    rootIndex = i;
                    break;
                }
                i++;
            }
        }

        // Second pass: neuter only the in-scope ScrollViewers.
        {
            int i = 0;
            DependencyObject? node = this;
            while (node is not null)
            {
                node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
                if (node is null) break;
                if (node is ScrollViewer sv &&
                    AncestorScrollViewerScope.InScope(i, rootIndex))
                {
                    sv.IsTabStop = false;
                }
                i++;
            }
        }
    }

    private void OnFirstLayoutUpdated(object? sender, object e)
    {
        if (_surface.Handle == IntPtr.Zero) return;
        var w = Panel.ActualWidth;
        var h = Panel.ActualHeight;
        if (w <= 0 || h <= 0) return;  // still not settled, wait for next tick
        Panel.LayoutUpdated -= OnFirstLayoutUpdated;

        var sx = Panel.CompositionScaleX > 0 ? Panel.CompositionScaleX : 1f;
        var sy = Panel.CompositionScaleY > 0 ? Panel.CompositionScaleY : 1f;
        NativeMethods.SurfaceSetContentScale(_surface, sx, sy);
        NativeMethods.SurfaceSetSize(
            _surface,
            (uint)Math.Max(1, w * sx),
            (uint)Math.Max(1, h * sy));

        // Start the resize-overlay startup grace from this first settled
        // layout (and again after any reparent that re-arms this handler),
        // so the initial layout passes do not flash the cols x rows pill.
        ArmResizeOverlayGrace();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Intentionally NO surface teardown here. WinUI 3 fires Unloaded
        // when the visual tree shifts the control to a new parent
        // (split / rebuild), and the matching Loaded fires asynchronously
        // moments later. Tearing down the surface on every Unloaded would
        // kill every existing pane's shell process on every split. The
        // surface is freed only when DisposeSurface() is called by
        // PaneHost when the leaf is actually being removed.
        //
        // We only unsubscribe the one-shot LayoutUpdated handler to make
        // sure it does not fire spuriously after the panel detaches.
        // OnLoaded re-subscribes when the control re-enters a tree.
        Panel.LayoutUpdated -= OnFirstLayoutUpdated;

        // Deliberately do NOT stop the resize-overlay grace timer here. WinUI 3
        // raises Unloaded on every reparent (split / rebuild), not just on real
        // teardown, and the matching Loaded does not guarantee a fresh settled-
        // layout pass -- so the one-shot OnFirstLayoutUpdated may never re-fire
        // to re-arm the grace. Stopping a grace armed just before this reparent
        // would latch _resizeOverlayReady false forever, so the pane would never
        // pulse the resize pill again and only the never-reparented active pane
        // would show it during a multi-pane resize. Letting the armed grace run
        // to completion guarantees readiness recovers across reparents (a tick
        // landing while detached only sets the flag true, which is harmless).
        //
        // Re-arming in OnLoaded instead is wrong: ArmResizeOverlayGrace resets
        // _resizeOverlayReady to false and restarts the window on every
        // reparent, re-blanking the pill on each split. This mirrors
        // ResizeOverlayControl._hideTimer (PR #463), which likewise keeps its
        // non-repeating timer wired across Unloaded for the same reparent reason.
    }

    /// <summary>
    /// Tear down the libghostty surface and per-control native
    /// resources. Called by <see cref="Panes.PaneHost"/> when the
    /// leaf is being closed (via Ctrl+Shift+W or process exit), and
    /// by <see cref="MainWindow"/> for any remaining leaves at window
    /// close. Idempotent.
    /// </summary>
    internal void DisposeSurface()
    {
        if (_surfaceDisposed) return;
        _surfaceDisposed = true;

        _bellAudio?.Dispose();
        _bellAudio = null;

        Panel.LayoutUpdated -= OnFirstLayoutUpdated;

        if (_surface.Handle != IntPtr.Zero)
        {
            Host?.Unregister(_surface);
            NativeMethods.SurfaceFree(_surface);
        }
        // Free the GCHandle AFTER SurfaceFree: libghostty may still touch
        // userdata during teardown (e.g. emitting a final event). Once
        // SurfaceFree returns, no callback can fire on this surface.
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        if (_workingDirectoryUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(_workingDirectoryUtf8);
        if (_commandUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(_commandUtf8);
        if (_initialInputUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(_initialInputUtf8);

        _surface = default;
        _workingDirectoryUtf8 = IntPtr.Zero;
        _commandUtf8 = IntPtr.Zero;
        _initialInputUtf8 = IntPtr.Zero;

        // Drop subscribers so MainWindow is not rooted via these events
        // after the control tears down.
        TitleChanged = null;
        CloseRequested = null;
        HoveredLinkChanged = null;
        ProgressChanged = null;
        PromptReady = null;
        FirstRender = null;
        BellRang = null;
        BellAcknowledged = null;
    }

    private static IntPtr AllocEmptyUtf8()
    {
        var p = Marshal.AllocHGlobal(1);
        Marshal.WriteByte(p, 0);
        return p;
    }

    private static IntPtr AllocUtf8(string s)
    {
        // +1 for the null terminator that Zig dereferences unconditionally.
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(s) + 1;
        var p = Marshal.AllocHGlobal(byteCount);
        // Write the UTF-8 bytes then null-terminate. Marshal.AllocHGlobal
        // does not zero-initialize, so the terminator must be explicit.
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        Marshal.WriteByte(p, bytes.Length, 0);
        return p;
    }

    // Size / scale -------------------------------------------------------

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_surface.Handle == IntPtr.Zero) return;
        PushSurfaceSize();
        UpdateResizeOverlay();
    }

    // Resize overlay -----------------------------------------------------
    //
    // The cols x rows pill mirrors macOS's SurfaceResizeOverlay. The Core
    // ResizeOverlayState decides whether a given size change should pulse
    // (mode + first-layout + dedup); the two time-based guards live here
    // because they depend on wall-clock instants this control already sees.

    // Roughly half a second of grace after the surface first sizes, during
    // which the initial layout settle (often several passes) must not flash
    // the overlay. Matches macOS's `ready` delay.
    private static readonly TimeSpan ResizeOverlayStartupGrace =
        TimeSpan.FromMilliseconds(500);

    // Suppress the overlay for this long after the pane gains focus, so a
    // focus-driven relayout does not flash it. Matches macOS's focusInstant
    // guard.
    private static readonly TimeSpan ResizeOverlayFocusGuard =
        TimeSpan.FromMilliseconds(500);

    private bool _resizeOverlayReady;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _resizeOverlayGraceTimer;
    // Monotonic (TickCount64) so an NTP/DST wall-clock jump cannot widen or
    // collapse the focus guard. 0 means "never focused yet".
    private long _lastFocusGainedTick;

    private void ArmResizeOverlayGrace()
    {
        // The first settled layout (and each reparent that re-arms this) opens
        // the grace window; once it elapses, real user resizes may pulse the
        // overlay. Reuse one one-shot timer so re-arming just restarts it
        // instead of leaking a fresh timer + closure each reparent.
        _resizeOverlayReady = false;
        if (_resizeOverlayGraceTimer is null)
        {
            _resizeOverlayGraceTimer = DispatcherQueue.CreateTimer();
            _resizeOverlayGraceTimer.Interval = ResizeOverlayStartupGrace;
            _resizeOverlayGraceTimer.IsRepeating = false;
            _resizeOverlayGraceTimer.Tick += OnResizeOverlayGraceTick;
        }
        _resizeOverlayGraceTimer.Stop();
        _resizeOverlayGraceTimer.Start();
    }

    private void OnResizeOverlayGraceTick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        _resizeOverlayReady = true;
    }

    private void UpdateResizeOverlay()
    {
        var cfg = App.ConfigService;
        if (cfg is null) return;

        // Read config fresh each pulse so hot-reload is honored with no
        // subscription to unwind on teardown.
        var mode = cfg.ResizeOverlayMode;

        // SurfaceSetSize (called above via PushSurfaceSize) recalculates the
        // grid synchronously on this thread, so this read already reflects the
        // new cols/rows; only the GPU buffer resize is deferred.
        var size = NativeMethods.SurfaceSize(_surface);

        var withinFocusGuard =
            _lastFocusGainedTick != 0 &&
            Environment.TickCount64 - _lastFocusGainedTick
                < ResizeOverlayFocusGuard.TotalMilliseconds;
        var allowShow = _resizeOverlayReady && !withinFocusGuard;

        ResizeOverlay.NotifyResize(
            size.Columns,
            size.Rows,
            mode,
            cfg.ResizeOverlayPosition,
            cfg.ResizeOverlayDurationMs,
            allowShow);
    }

    private void PushSurfaceSize()
    {
        // Read the panel's own layout bounds rather than the
        // SizeChangedEventArgs value. DPI rounding and any padding in
        // the visual tree can make the two differ by a pixel, which
        // manifests as letterboxing: the DX12 swap chain sizes off one
        // value while the compositor stretches the panel to its own
        // bounds, leaving a gap at the edges.
        var sx = Panel.CompositionScaleX > 0 ? Panel.CompositionScaleX : 1.0;
        var sy = Panel.CompositionScaleY > 0 ? Panel.CompositionScaleY : 1.0;
        var w = (uint)Math.Max(1, Panel.ActualWidth * sx);
        var h = (uint)Math.Max(1, Panel.ActualHeight * sy);

        // Fire-and-forget. ghostty_surface_set_size records the desired
        // dimensions in an atomic and wakes the renderer thread; the
        // next beginFrame on that thread (within one wakeup hop or, at
        // worst, one ~8 ms draw-timer tick) compares desired_size to
        // applied_width/height and calls ResizeBuffers before the next
        // Present. We never block here, never touch draw_mutex, and
        // never do GPU work on the UI thread.
        NativeMethods.SurfaceSetSize(_surface, w, h);
    }

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        if (_surface.Handle == IntPtr.Zero) return;
        // Push the new scale to libghostty, then recompute pixel
        // dimensions: a DPI change (e.g. moving the window between
        // monitors) shifts the pixel size even though the DIP size is
        // unchanged.
        NativeMethods.SurfaceSetContentScale(_surface, sender.CompositionScaleX, sender.CompositionScaleY);
        PushSurfaceSize();
    }

    // Focus --------------------------------------------------------------
    //
    // Focus is owned by the outer UserControl, not the SwapChainPanel -
    // see the comment in the XAML for the full reasoning. These
    // handlers fire off the UserControl's GotFocus/LostFocus routed
    // events. We still dedupe on state change as a belt-and-braces
    // guard so libghostty never sees a redundant focus event.

    private bool _focused;

    /// <summary>
    /// True only when this surface is focused AND its window is the OS
    /// foreground window — i.e. the user is actively looking at it. Used by
    /// the toast policy to suppress notifications for the surface in view.
    ///
    /// <see cref="_focused"/> alone is NOT sufficient: WinUI keeps XAML
    /// keyboard focus across window deactivation, so a backgrounded window's
    /// focused surface still reports <c>_focused == true</c>. Gating toasts
    /// on <c>_focused</c> only would wrongly suppress a notification raised
    /// while the user is in another app — exactly the case the feature
    /// exists for — so we AND in a real foreground-window check.
    /// </summary>
    internal bool IsActive => _focused && IsOwningWindowForeground();

    private bool IsOwningWindowForeground()
    {
        // Resolve this control's top-level window HWND via its XamlRoot and
        // compare to the OS foreground window. If the island environment is
        // not available yet, fail "not foreground" so we err toward showing
        // the toast rather than silently swallowing it.
        var env = XamlRoot?.ContentIslandEnvironment;
        if (env is null) return false;
        nint mine = Microsoft.UI.Win32Interop.GetWindowFromWindowId(env.AppWindowId);
        return mine != 0 && mine == PInvoke.GetForegroundWindow();
    }

    // Stable per-surface key for the toast Group (dedupe + focus-regain
    // clear). A fresh Guid rather than the native surface handle, so a
    // recycled handle value can never alias another surface's toasts. The
    // same control instance is resolved for both Show and ClearForSurface,
    // so the key stays consistent for the surface's lifetime.
    private readonly string _toastSurfaceKey = Guid.NewGuid().ToString();
    internal string ToastSurfaceKey => _toastSurfaceKey;

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        // Stamp the focus instant so a focus-driven relayout in the next
        // ~500 ms does not flash the resize overlay (matches macOS).
        _lastFocusGainedTick = Environment.TickCount64;
        SetFocusState(true);
        AcknowledgeBell();
    }

    private void OnLostFocus(object sender, RoutedEventArgs e) => SetFocusState(false);

    private void SetFocusState(bool focused)
    {
        if (_focused == focused) return;
        // Don't flip _focused before we know we can actually push the new
        // state to the surface: otherwise the next focus change after the
        // surface is recreated would be deduped against a stale value.
        if (_surface.Handle == IntPtr.Zero) return;
        _focused = focused;
        NativeMethods.SurfaceSetFocus(_surface, focused);
        var app = Host?.App ?? default;
        if (app.Handle != IntPtr.Zero) NativeMethods.AppSetFocus(app, focused);

        // Banner can only hide via libghostty re-emitting MouseOverLink with
        // a null URL — which requires the pointer to actually move out of
        // the link cell. Focus loss (Alt+Tab, click another pane) doesn't
        // move the pointer, so without this the banner would stay frozen
        // on screen until the user returned and moved the mouse.
        if (!focused) UpdateUrlHoverBanner(null);

        // On focus regain, drop any toast we raised for this surface while it
        // was in the background so a stale notification does not linger.
        if (focused) Host?.ClearSurfaceToasts(ToastSurfaceKey);
    }

    // Mouse --------------------------------------------------------------

    // Hovered OSC 8 hyperlink URL (or null when the pointer is not over
    // a link). Set by GhosttyHost in response to libghostty's
    // apprt.action.MouseOverLink. The HoveredLinkChanged event fires
    // only on transitions so consumers (status bar, tab strip) can
    // avoid redundant updates.
    internal string? HoveredLink { get; private set; }
    internal event EventHandler<string?>? HoveredLinkChanged;

    internal void SetHoveredLink(string? url)
    {
        if (string.Equals(HoveredLink, url, StringComparison.Ordinal)) return;
        HoveredLink = url;
        HoveredLinkChanged?.Invoke(this, url);
        UpdateUrlHoverBanner(url);
    }

    // Show / hide the bottom-left URL hover banner that mirrors macOS's
    // URLHoverBanner and the GTK url_left widget. libghostty already
    // gates this on Ctrl/Cmd-hover (Surface.zig:linkAtPos requires
    // ctrlOrSuper for OSC 8 hyperlink detection), so the banner only
    // appears at the "I'm about to interact with this link" moment.
    //
    // Also sets AutomationProperties.Name on the Border so screen readers
    // announce the full "Ctrl+Click to open: <url>" string instead of the
    // raw TextBlock type name. IsHitTestVisible=false in XAML keeps the
    // banner out of the pointer-event chain but does NOT remove it from
    // the UIA tree.
    private void UpdateUrlHoverBanner(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            UrlHoverBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var formatted = HoverLinkText.Format(url);
        UrlHoverBannerText.Text = formatted;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            UrlHoverBanner, formatted);
        UrlHoverBanner.Visibility = Visibility.Visible;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Take focus on the UserControl, not the panel. Guard with the
        // current focus state to avoid generating a Lost+Got pair when
        // we already have focus.
        if (!_focused) this.Focus(FocusState.Pointer);

        // Ctrl+LeftClick on an OSC 8 hyperlink: open the URL in the
        // default browser and consume the event so libghostty doesn't
        // also see a stray button-press (which would confuse apps
        // running mouse=a like vim/htop).
        var props = e.GetCurrentPoint(Panel).Properties;
        if (props.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed
            && (CurrentMods() & GhosttyMods.Ctrl) != 0
            && HoveredLink is { } url)
        {
            _ = TryLaunchHoveredLinkAsync(url);
            e.Handled = true;
            return;
        }

        SendMouseButton(e, GhosttyMouseState.Press);
    }

    private static async Task TryLaunchHoveredLinkAsync(string url)
    {
        // Best-effort launch. Malformed URLs (e.g. corrupted OSC 8) or
        // schemes the user has no handler for shouldn't crash the
        // terminal; swallow but log to Debug so a regression where valid
        // URLs stop launching doesn't disappear silently.
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                await Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[TerminalControl] TryLaunchHoveredLinkAsync failed for '{url}': {ex}");
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) =>
        SendMouseButton(e, GhosttyMouseState.Release);

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_surface.Handle == IntPtr.Zero) return;
        // ghostty_surface_mouse_pos expects unscaled coordinates (DIPs):
        // src/apprt/embedded.zig cursorPosCallback runs the input through
        // cursorPosToPixels using the surface's content scale. Multiplying
        // by CompositionScaleX/Y here would double-scale on high DPI.
        var pt = e.GetCurrentPoint(Panel).Position;

        // While the cursor is hidden by mouse-hide-while-typing,
        // suppress sub-threshold motion so libghostty's cursorPosCallback
        // doesn't fire showMouse for every DIP of sensor jitter. See
        // _lastForwardedMouseX/Y comments above. A null anchor means we
        // haven't seen a real pointer event yet, so the current position
        // becomes the anchor without forwarding (no genuine motion to
        // report).
        if (_cursorHidden && _lastForwardedMouseX is double ax && _lastForwardedMouseY is double ay)
        {
            var dx = pt.X - ax;
            var dy = pt.Y - ay;
            if (dx * dx + dy * dy < HiddenCursorMotionThresholdDips * HiddenCursorMotionThresholdDips)
                return;
        }

        _lastForwardedMouseX = pt.X;
        _lastForwardedMouseY = pt.Y;
        NativeMethods.SurfaceMousePos(_surface, pt.X, pt.Y, CurrentMods());
    }

    // libghostty's ScrollMods is a u8 packed struct (src/input/mouse.zig):
    //   bit 0       : precision (bool) — high-precision/pixel scroll
    //   bits 1..3   : momentum (u3 enum) — inertial phase (macOS-only today)
    //   bits 4..7   : padding
    // WinUI 3 does not surface AppKit-style momentum phases, so we only
    // set the precision bit. Momentum stays .none (0).
    private const int ScrollModsPrecision = 0b0000_0001;

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_surface.Handle == IntPtr.Zero) return;
        var pt = e.GetCurrentPoint(Panel);
        var rawDelta = pt.Properties.MouseWheelDelta;
        var isHorizontal = pt.Properties.IsHorizontalMouseWheel;

        // Ctrl+Shift+Wheel adjusts background opacity (matches Windows
        // Terminal). Intercept before the normal scroll path so the
        // terminal viewport does not move.
        var mods = CurrentMods();
        if (!isHorizontal
            && (mods & GhosttyMods.Ctrl) != 0
            && (mods & GhosttyMods.Shift) != 0)
        {
            Host?.RequestOpacityAdjust(rawDelta > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }

        // Detect precision input (touchpad) vs discrete mouse wheel.
        // PointerDeviceType.Touchpad is only reported when the user has a
        // precision-touchpad driver; legacy touchpads masquerade as Mouse
        // and correctly fall through to the discrete branch below.
        //
        // Precision path: Surface.zig treats the offset as pixels and
        // applies mouse_scroll_multiplier.precision. Windows touchpads
        // report small sub-WHEEL_DELTA values (~8..40 per frame) which
        // map reasonably to pixel counts, so we pass the raw delta
        // through without the /120 normalization used for wheels.
        //
        // Discrete wheel path: 120 units = one notch (WHEEL_DELTA).
        // Surface.zig multiplies this by cell_size * discrete multiplier.
        var (delta, scrollMods) = pt.PointerDeviceType switch
        {
            PointerDeviceType.Touchpad => ((double)rawDelta, ScrollModsPrecision),
            _ => (rawDelta / 120.0, 0),
        };

        NativeMethods.SurfaceMouseScroll(
            _surface,
            isHorizontal ? delta : 0.0,
            isHorizontal ? 0.0 : delta,
            scrollMods);
    }

    private void SendMouseButton(PointerRoutedEventArgs e, GhosttyMouseState state)
    {
        if (_surface.Handle == IntPtr.Zero) return;
        var props = e.GetCurrentPoint(Panel).Properties;
        GhosttyMouseButton btn = GhosttyMouseButton.Unknown;
        // Pick whichever button changed in this event. For Press/Release
        // only one bit flips, so "IsLeftButtonPressed == (state == Press)"
        // is the right test; but we can shortcut using PointerUpdateKind.
        btn = props.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed or
            PointerUpdateKind.LeftButtonReleased => GhosttyMouseButton.Left,
            PointerUpdateKind.RightButtonPressed or
            PointerUpdateKind.RightButtonReleased => GhosttyMouseButton.Right,
            PointerUpdateKind.MiddleButtonPressed or
            PointerUpdateKind.MiddleButtonReleased => GhosttyMouseButton.Middle,
            // Mouse thumb buttons (back/forward). xterm SGR mouse convention
            // encodes these as button 8 and 9; libghostty's input/mouse.zig
            // enum has Eight=8 (back) and Nine=9 (forward) reserved for this.
            PointerUpdateKind.XButton1Pressed or
            PointerUpdateKind.XButton1Released => GhosttyMouseButton.Eight,
            PointerUpdateKind.XButton2Pressed or
            PointerUpdateKind.XButton2Released => GhosttyMouseButton.Nine,
            _ => GhosttyMouseButton.Unknown,
        };
        if (btn == GhosttyMouseButton.Unknown) return;
        NativeMethods.SurfaceMouseButton(_surface, state, btn, CurrentMods());
    }

    // Tracks the family currently applied to ProtectedCursor so
    // SetMouseShape can short-circuit when libghostty re-emits the
    // same shape (common in cursor-heavy TUIs like vim/less, where
    // OSC 22 fires on every redraw). Initialised to Arrow to match
    // the WinUI default when ProtectedCursor has not been assigned.
    private MouseShapeFamily _currentMouseShapeFamily = MouseShapeFamily.Arrow;

    // Whether the mouse-hide-while-typing path has driven the cursor
    // to its invisible state. While true, SetMouseShape only tracks
    // the requested family; the visible cursor is restored from
    // _currentMouseShapeFamily by SetMouseVisibility(Visible).
    private bool _cursorHidden;

    // Last pointer position forwarded to libghostty via SurfaceMousePos.
    // Used by OnPointerMoved to suppress sub-threshold motion while the
    // cursor is hidden: libghostty's cursorPosCallback in Surface.zig
    // calls showMouse() on ANY mouse position update, so even 1-DIP
    // sensor jitter from a resting hand would flicker the cursor back on
    // immediately after every keystroke. Mac sidesteps this with NSCursor
    // OS-level filtering; on WinUI 3 we filter the forwarding ourselves.
    //
    // Nullable so the first OnPointerMoved seeds the anchor instead of
    // comparing to a literal (0, 0) origin -- a cold launch where the
    // user types before ever moving the mouse would otherwise blow past
    // the threshold immediately and un-hide the cursor on the first
    // genuine pointer event.
    private double? _lastForwardedMouseX;
    private double? _lastForwardedMouseY;

    // Threshold in DIPs. Real intentional mouse motion produces deltas
    // of 10+ DIPs between PointerMoved events; sensor jitter / hand
    // tremor is 1-2 DIPs. 5 splits the difference.
    private const double HiddenCursorMotionThresholdDips = 5.0;

    /// <summary>
    /// Set the panel cursor in response to libghostty's
    /// <c>apprt.action.MouseShape</c> (typically driven by OSC 22 from
    /// apps inside the terminal — vim, less, file managers).
    ///
    /// libghostty re-emits this action whenever the active shape
    /// changes, including transitions back to <see cref="MouseShape.Default"/>,
    /// so we don't need to reset on PointerExited.
    ///
    /// While <see cref="_cursorHidden"/> is true the visible shape
    /// is only tracked; <see cref="SetMouseVisibility"/> restores it
    /// when libghostty asks for the cursor to come back.
    /// </summary>
    internal void SetMouseShape(MouseShape shape)
    {
        var family = MouseShapeMap.ToFamily(shape);
        if (family == _currentMouseShapeFamily) return;
        _currentMouseShapeFamily = family;
        if (!_cursorHidden)
        {
            ProtectedCursor =
                InputSystemCursor.Create(MouseShapeAdapter.ToWinUI(family));
        }
    }

    /// <summary>
    /// Set the panel cursor's visibility in response to libghostty's
    /// <c>apprt.action.MouseVisibility</c> (driven by the
    /// <c>mouse-hide-while-typing</c> config: hide on text-producing key
    /// press, show on pointer motion / focus / click).
    ///
    /// Hiding swaps ProtectedCursor to a transparent custom cursor
    /// (built via <see cref="Ghostty.Hosting.InvisibleCursorFactory"/>).
    /// Showing restores ProtectedCursor to the shape that
    /// <see cref="SetMouseShape"/> last requested. The two methods
    /// coordinate through <see cref="_cursorHidden"/> and
    /// <see cref="_currentMouseShapeFamily"/> so a MouseShape arriving
    /// while hidden doesn't pop the cursor back on.
    /// </summary>
    internal void SetMouseVisibility(MouseVisibility visibility)
    {
        var newHidden = visibility == MouseVisibility.Hidden;
        if (newHidden == _cursorHidden) return;
        _cursorHidden = newHidden;
        ProtectedCursor = newHidden
            ? Ghostty.Hosting.InvisibleCursorFactory.Invisible
            : InputSystemCursor.Create(MouseShapeAdapter.ToWinUI(_currentMouseShapeFamily));
    }

    // Keyboard -----------------------------------------------------------

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (CommandPaletteIsOpen) return;

        // Suppress forwarding while the in-pane search bar owns keyboard
        // focus: its controls are children of this visual tree, so their
        // KeyDown bubbles up here and would otherwise also run in the
        // shell. Gate on live focus rather than the bar's open state so
        // typing keeps reaching the terminal when the bar is left visible
        // after the user clicks back into the surface.
        if (SearchBar.ContainsFocus) return;

        // Stamp the shared host so VerticalTabHost's hover-expand
        // suppression knows the user is mid-typing and holds back
        // the sidebar pop-open. Unconditional: we want every key
        // (including chords and IME composition keys) to count.
        Host?.NoteKeystroke();

        // Any key reaching the surface acknowledges a pending bell, fading
        // the visual border and clearing the tab indicator (matches macOS).
        AcknowledgeBell();

        // Windows-only residual match: a handful of chords have no
        // libghostty action (search-bar widget, vertical-tabs pin,
        // tab-layout switch, profile slots), so the apprt matches them
        // itself. A hit is dispatched through GhosttyHost.PaneActionRequested
        // -- the same event MainWindow forwards to PaneActionRouter for
        // libghostty-matched actions -- and the key is NOT forwarded to
        // libghostty. We mark the event handled so it stops here; every
        // standard chord falls through to SendKey and is matched inside
        // libghostty.
        //
        // We also set _suppressNextCharacter so the matching
        // OnCharacterReceived (which fires independently with the
        // WM_CHAR text) does not forward a control char to libghostty as
        // text. Without this, the shell sees the C0 control char even
        // though we filtered the key event itself.
        if (KeyBindings.WindowsOnly.Match(CurrentChordModifiers(), e.Key) is { } residualAction)
        {
            Host?.RequestPaneAction(residualAction);
            _suppressNextCharacter = true;
            e.Handled = true;
            return;
        }
        SendKey(e, GhosttyInputAction.Press);
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        // Mirror OnKeyDown: while the search bar owns focus, swallow the
        // key-up too so libghostty never sees a release for a press it
        // never received.
        if (SearchBar.ContainsFocus) return;

        // Same short-circuit so the matching key-up never reaches
        // libghostty either. Without this, libghostty would see a
        // stray release for a press it never saw. Assumes every bound
        // chord has at least one modifier; a plain unmodified bound
        // key would swallow its key-up silently here.
        var mods = CurrentChordModifiers();
        if (KeyBindings.WindowsOnly.Match(mods, e.Key) is not null)
        {
            e.Handled = true;
            return;
        }
        SendKey(e, GhosttyInputAction.Release);
    }

    private static Windows.System.VirtualKeyModifiers CurrentChordModifiers()
    {
        var mods = Windows.System.VirtualKeyModifiers.None;
        if ((Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
            mods |= Windows.System.VirtualKeyModifiers.Control;
        if ((Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
            mods |= Windows.System.VirtualKeyModifiers.Shift;
        if ((Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
            mods |= Windows.System.VirtualKeyModifiers.Menu;
        return mods;
    }

    private void SendKey(KeyRoutedEventArgs e, GhosttyInputAction action)
    {
        if (_surface.Handle == IntPtr.Zero) return;

        // The embedded apprt (src/apprt/embedded.zig) implements key+text
        // combining on Windows at comptime: ghostty_surface_key buffers a
        // keydown with no text, and the next ghostty_surface_text attaches
        // the text and dispatches through the full key encoding pipeline.
        // We just forward WM_KEYDOWN/WM_KEYUP here and forward WM_CHAR from
        // OnCharacterReceived - embedders do not implement the combining
        // themselves.
        //
        // The Keycode field carries the native Windows *scancode* (not a
        // VirtualKey). embedded.zig matches it against keycodes.entries
        // where the native column is the Win32 scancode, and derives
        // unshifted_codepoint via MapVirtualKeyW when we pass 0.
        //
        // Two scancode adjustments are required to match what the C
        // example/c-win32-terminal/src/main.c (the canonical Win32
        // embedder) computes from raw lParam:
        //
        //  1. Extended keys (arrows, navigation cluster, numpad enter,
        //     right-side modifiers) need the 0xE000 prefix or'd in.
        //     PhysicalKeyStatus.ScanCode only returns the low byte;
        //     IsExtendedKey tells us whether to set the prefix.
        //     Without this, Up/Down/Left/Right/Home/End/PgUp/PgDn never
        //     find a match in input.keycodes.entries (the table uses
        //     0xE048 etc on the Windows column) and the dispatch returns
        //     .ignored.
        //
        //  2. WinUI 3 strips ScanCode entirely for some keys that the
        //     framework treats as "navigation" (most notably Tab),
        //     reporting 0 even on the press path. Fall back to
        //     MapVirtualKey(VK, MAPVK_VK_TO_VSC) using e.Key as the
        //     virtual-key when ScanCode is 0, so the apprt sees the
        //     real scancode.
        uint scancode = e.KeyStatus.ScanCode;
        if (scancode == 0)
        {
            // Recover the OEM scancode from the VirtualKey. This handles
            // Tab and any other key WinUI 3 strips ScanCode for.
            scancode = PInvoke.MapVirtualKey((uint)e.Key, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);
        }
        if (e.KeyStatus.IsExtendedKey)
        {
            scancode |= 0xE000;
        }

        var key = new GhosttyInputKey
        {
            Action = action,
            Mods = CurrentMods(),
            ConsumedMods = GhosttyMods.None,
            Keycode = scancode,
            Text = IntPtr.Zero,
            UnshiftedCodepoint = 0,
            Composing = 0,
        };
        var handled = NativeMethods.SurfaceKey(_surface, key);
        if (handled) e.Handled = true;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_surface.Handle == IntPtr.Zero) return;

        // WM_CHAR from the focused search bar bubbles up here too. Drop it
        // so typed characters edit the needle only and never reach
        // libghostty as terminal text. See the matching guard in OnKeyDown.
        if (SearchBar.ContainsFocus) return;

        // If the matching OnKeyDown short-circuited a bound chord, drop
        // the WM_CHAR that follows. WinUI 3 raises CharacterReceived
        // independently of KeyDown handling, so without this the C0
        // control char (e.g. U+0005 for Ctrl+E) reaches libghostty as
        // text and the shell interprets it as a readline command.
        if (_suppressNextCharacter)
        {
            _suppressNextCharacter = false;
            return;
        }

        // Forward WM_CHAR unchanged. The embedded apprt's key+text combining
        // handles C0 control filtering on its side: the preceding key event
        // already produced Ctrl+C / Backspace / etc via the key encoder, and
        // the apprt drops the duplicated WM_CHAR text. Filtering here would
        // also clobber legitimate U+007F / U+001B text the core might want.
        var ch = e.Character;
        Span<byte> buf = stackalloc byte[4];
        var len = new Rune(ch).EncodeToUtf8(buf);
        unsafe
        {
            fixed (byte* p = buf)
            {
                NativeMethods.SurfaceText(_surface, (IntPtr)p, (UIntPtr)len);
            }
        }
    }

    // Mods helper --------------------------------------------------------

    private static GhosttyMods CurrentMods()
    {
        // Use Win32 GetKeyState directly. WinUI 3's InputKeyboardSource
        // surface has moved several times between releases; Win32 is
        // stable and cheap (reads a thread-local state table).
        //
        // We query the left/right variants individually so the *Right
        // flags in ghostty_mods_e are set correctly - these matter for
        // keybinds that distinguish "right alt" (AltGr) from "left alt".
        var mods = GhosttyMods.None;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_LSHIFT) & 0x8000) != 0) mods |= GhosttyMods.Shift;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_RSHIFT) & 0x8000) != 0) mods |= GhosttyMods.Shift | GhosttyMods.ShiftRight;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_LCONTROL) & 0x8000) != 0) mods |= GhosttyMods.Ctrl;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_RCONTROL) & 0x8000) != 0) mods |= GhosttyMods.Ctrl | GhosttyMods.CtrlRight;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_LMENU) & 0x8000) != 0) mods |= GhosttyMods.Alt;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_RMENU) & 0x8000) != 0) mods |= GhosttyMods.Alt | GhosttyMods.AltRight;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_LWIN) & 0x8000) != 0) mods |= GhosttyMods.Super;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_RWIN) & 0x8000) != 0) mods |= GhosttyMods.Super | GhosttyMods.SuperRight;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_CAPITAL) & 0x0001) != 0) mods |= GhosttyMods.Caps;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_NUMLOCK) & 0x0001) != 0) mods |= GhosttyMods.Num;
        return mods;
    }

    // Search --------------------------------------------------------------
    //
    // In-pane scrollback search is owned by the SearchBarControl child;
    // TerminalControl plays both the ISearchHost (UI -> libghostty) and
    // the action-callback sink (libghostty -> UI state). Visibility is
    // toggled here so the search bar disappears as soon as the user
    // dismisses it, without round-tripping through libghostty first.

    /// <summary>
    /// Show the search bar and move keyboard focus into its needle box.
    /// Called from MainWindow when the Ctrl+Shift+F chord fires against
    /// this leaf. Idempotent: repeated calls just re-focus the needle.
    /// </summary>
    internal void OpenSearch()
    {
        SearchBar.State.IsOpen = true;
        SearchBar.Visibility = Visibility.Visible;
        SearchBar.FocusNeedle();
    }

    private void OnSearchClosed(object sender, EventArgs e)
    {
        SearchBar.Visibility = Visibility.Collapsed;
        SearchBar.State.IsOpen = false;
        // Return focus to the terminal surface so the user can keep typing
        // immediately after dismissing the bar.
        this.Focus(FocusState.Programmatic);
    }

    /// <inheritdoc />
    public void StartSearch(string needle)
        => SendBindingAction("search:" + (needle ?? string.Empty));

    /// <inheritdoc />
    public void NavigateNext()
        => SendBindingAction("navigate_search:next");

    /// <inheritdoc />
    public void NavigatePrevious()
        => SendBindingAction("navigate_search:previous");

    /// <inheritdoc />
    public void EndSearch()
        => SendBindingAction("end_search");

    // Mirrors MainWindow.ExecuteBindingAction's encode-and-call pattern.
    // Heap-allocates per call (intent: low-frequency, user-driven), unlike
    // the OnScrollBarScroll hot path which uses stackalloc.
    private void SendBindingAction(string action)
    {
        if (_surface.Handle == IntPtr.Zero) return;
        var bytes = Encoding.UTF8.GetBytes(action);
        unsafe
        {
            fixed (byte* p = bytes)
            {
                NativeMethods.SurfaceBindingAction(_surface, p, (UIntPtr)bytes.Length);
            }
        }
    }

    // Mutators invoked by GhosttyHost after dispatching a search action
    // to this leaf. All four run on the UI thread because GhosttyHost
    // already DispatcherQueue.TryEnqueues the callback body.
    internal void OnSearchStarted(string needle)
    {
        SearchBar.State.Needle = needle;
    }

    internal void OnSearchEnded()
    {
        if (SearchBar.Visibility == Visibility.Visible)
        {
            SearchBar.Visibility = Visibility.Collapsed;
            SearchBar.State.IsOpen = false;
        }
    }

    internal void OnSearchTotalChanged(long total)
        => SearchBar.State.Total = total;

    internal void OnSearchSelectedChanged(long selected)
        => SearchBar.State.Selected = selected;
}
