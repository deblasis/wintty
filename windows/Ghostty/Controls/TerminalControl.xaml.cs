using System;
using System.Runtime.InteropServices;
using System.Text;
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
public sealed partial class TerminalControl : UserControl
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
    /// The raw libghostty surface handle for this control. Used by
    /// <see cref="Ghostty.Hosting.GhosttyHost"/> to resolve a per-surface
    /// userdata pointer back to the handle for clipboard callback completion.
    /// Returns <see cref="IntPtr.Zero"/> before the surface is created or
    /// after it is disposed.
    /// </summary>
    internal IntPtr SurfaceHandle => _surface.Handle;

    /// <summary>
    /// Last title pushed by libghostty for this surface, or null if no
    /// title has been set yet. Used by MainWindow to update the window
    /// chrome immediately on focus change without waiting for the next
    /// TitleChanged.
    /// </summary>
    public string? CurrentTitle { get; private set; }

    // Raisers invoked by GhosttyHost after routing an action to this leaf.
    internal void RaiseTitleChanged(string title)
    {
        CurrentTitle = title;
        TitleChanged?.Invoke(this, title);
    }
    internal void RaiseCloseRequested() => CloseRequested?.Invoke(this, EventArgs.Empty);
    internal void RaiseProgressChanged(Ghostty.Core.Tabs.TabProgressState state)
    {
        CurrentProgress = state;
        ProgressChanged?.Invoke(this, state);
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

        // Surface creation runs exactly once per control instance,
        // even across multiple reparents. Subsequent Loaded events
        // skip this entire block.
        if (_surfaceCreated) return;
        _surfaceCreated = true;

        if (Host is null)
            throw new InvalidOperationException(
                "TerminalControl.Host must be set before the control loads.");

        var app = Host.App;

        // Surface config. The string fields (working_directory, command,
        //    initial_input) must be non-null: Zig dereferences them
        //    unconditionally. We allocate three independent UTF-8 empty
        //    strings rather than aliasing one buffer - aliasing was a
        //    footgun if Zig ever wrote through any of them. These live
        //    until the surface is freed.
        _workingDirectoryUtf8 = AllocEmptyUtf8();
        _commandUtf8 = AllocEmptyUtf8();
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

        _surface = NativeMethods.SurfaceNew(app, surfaceConfig);
        // Drop our ref: libghostty does not retain the panel pointer.
        SwapChainPanelInterop.Release(panelPtr);
        Host.Register(_surface, this);

        // Request focus so keyboard input starts flowing immediately.
        // Focus lives on the UserControl now, not the panel.
        this.Focus(FocusState.Programmatic);

    }

    private void DisableAncestorScrollViewerTabStop()
    {
        DependencyObject? node = this;
        while (node is not null)
        {
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
            if (node is ScrollViewer sv) sv.IsTabStop = false;
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
    }

    private static IntPtr AllocEmptyUtf8()
    {
        var p = Marshal.AllocHGlobal(1);
        Marshal.WriteByte(p, 0);
        return p;
    }

    // Size / scale -------------------------------------------------------

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_surface.Handle == IntPtr.Zero) return;
        PushSurfaceSize();
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

    private void OnGotFocus(object sender, RoutedEventArgs e) => SetFocusState(true);

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
    }

    // Mouse --------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Take focus on the UserControl, not the panel. Guard with the
        // current focus state to avoid generating a Lost+Got pair when
        // we already have focus.
        if (!_focused) this.Focus(FocusState.Pointer);
        SendMouseButton(e, GhosttyMouseState.Press);
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
            _ => GhosttyMouseButton.Unknown,
        };
        if (btn == GhosttyMouseButton.Unknown) return;
        NativeMethods.SurfaceMouseButton(_surface, state, btn, CurrentMods());
    }

    // Keyboard -----------------------------------------------------------

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (CommandPaletteIsOpen) return;

        // Stamp the shared host so VerticalTabHost's hover-expand
        // suppression knows the user is mid-typing and holds back
        // the sidebar pop-open. Unconditional: we want every key
        // (including chords and IME composition keys) to count.
        Host?.NoteKeystroke();

        // Reserved-chord short-circuit: if this chord is bound to an
        // application-level KeyboardAccelerator (registered by
        // MainWindow from KeyBindings.Default), do NOT forward it to
        // libghostty and do NOT mark the event handled. WinUI 3
        // KeyboardAccelerators fire AFTER routed key events and only
        // when the focused element has not marked the event handled,
        // so the focused TerminalControl has to actively step out of
        // the way for the chord to reach the accelerator.
        //
        // We also set _suppressNextCharacter so the matching
        // OnCharacterReceived (which fires independently with the
        // WM_CHAR text, e.g. U+0005 for Ctrl+E) does not forward the
        // control char to libghostty as text. Without this, the shell
        // sees the C0 control char even though we filtered the key
        // event itself.
        if (KeyBindings.Default.Match(CurrentChordModifiers(), e.Key) is not null)
        {
            _suppressNextCharacter = true;
            return;
        }
        SendKey(e, GhosttyInputAction.Press);
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        // Same short-circuit so the matching key-up never reaches
        // libghostty either. Without this, libghostty would see a
        // stray release for a press it never saw. Assumes every bound
        // chord has at least one modifier; a plain unmodified bound
        // key would swallow its key-up silently here.
        var mods = CurrentChordModifiers();
        if (KeyBindings.Default.Match(mods, e.Key) is not null) return;
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

}
