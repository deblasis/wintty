using System;
using System.Runtime.InteropServices;
using System.Text;
using Ghostty.Interop;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Ghostty.Controls;

/// <summary>
/// Single libghostty-backed terminal surface, hosted via the WinUI 3
/// SwapChainPanel composition path (null HWND). Matches how macOS's
/// Ghostty.Surface.swift owns one ghostty_surface_t per SwiftUI view.
///
/// This first pass owns the whole ghostty_app_t lifetime too; when we add
/// tabs/splits the app handle will move up to MainWindow and each control
/// will only own its surface.
/// </summary>
public sealed partial class TerminalControl : UserControl
{
    // Handles ------------------------------------------------------------

    private GhosttyConfig _config;
    private GhosttyApp _app;
    private GhosttySurface _surface;
    private IntPtr _workingDirectoryUtf8;
    private IntPtr _commandUtf8;
    private IntPtr _initialInputUtf8;
    private bool _initialized;

    // Keep delegates alive for as long as the runtime config references
    // them. P/Invoke marshals the managed delegate to a native function
    // pointer that the GC has no way of tracking, so a dropped field here
    // would crash the Zig side on the first wakeup.
    private GhosttyWakeupCb? _wakeupCb;
    private GhosttyActionCb? _actionCb;
    private GhosttyReadClipboardCb? _readClipboardCb;
    private GhosttyConfirmReadClipboardCb? _confirmReadClipboardCb;
    private GhosttyWriteClipboardCb? _writeClipboardCb;
    private GhosttyCloseSurfaceCb? _closeSurfaceCb;

    // Events raised from the runtime action callback. They always fire
    // on the UI thread: the callback itself runs on libghostty's thread
    // and uses DispatcherQueue.TryEnqueue before invoking these.
    //
    // MainWindow subscribes to update the window chrome.
    public event EventHandler<string>? TitleChanged;
    public event EventHandler? CloseRequested;

    public TerminalControl()
    {
        InitializeComponent();
    }

    // Lifecycle ----------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        // ghostty_init is documented safe to call repeatedly.
        NativeMethods.Init(UIntPtr.Zero, IntPtr.Zero);

        // Skip CLI args: WinUI 3's entry point swallows them before we get
        // here. Default config files are loaded from the standard locations.
        _config = NativeMethods.ConfigNew();
        NativeMethods.ConfigLoadDefaultFiles(_config);
        NativeMethods.ConfigFinalize(_config);

        // The Zig side requires non-null callbacks even if they are no-ops;
        // storing each delegate in a field prevents GC from freeing it while
        // libghostty still holds the pointer.
        _wakeupCb = OnWakeup;
        _actionCb = OnAction;
        _readClipboardCb = OnReadClipboard;
        _confirmReadClipboardCb = OnConfirmReadClipboard;
        _writeClipboardCb = OnWriteClipboard;
        _closeSurfaceCb = OnCloseSurface;

        var runtime = new GhosttyRuntimeConfig
        {
            Userdata = IntPtr.Zero,
            SupportsSelectionClipboard = false,
            WakeupCb = Marshal.GetFunctionPointerForDelegate(_wakeupCb),
            ActionCb = Marshal.GetFunctionPointerForDelegate(_actionCb),
            ReadClipboardCb = Marshal.GetFunctionPointerForDelegate(_readClipboardCb),
            ConfirmReadClipboardCb = Marshal.GetFunctionPointerForDelegate(_confirmReadClipboardCb),
            WriteClipboardCb = Marshal.GetFunctionPointerForDelegate(_writeClipboardCb),
            CloseSurfaceCb = Marshal.GetFunctionPointerForDelegate(_closeSurfaceCb),
        };

        _app = NativeMethods.AppNew(runtime, _config);

        // Surface config. swap_chain_panel takes an ISwapChainPanelNative*
        //    which libghostty's DX12 device init uses synchronously (it calls
        //    SetSwapChain once and never stores it - see
        //    src/renderer/directx12/device.zig). We Release the COM ptr right
        //    after SurfaceNew returns to avoid leaking a ref per open/close.
        //
        //    The string fields (working_directory, command, initial_input)
        //    must be non-null: Zig dereferences them unconditionally. We
        //    allocate three independent UTF-8 empty strings rather than
        //    aliasing one buffer - aliasing was a footgun if Zig ever wrote
        //    through any of them. These live until the surface is freed.
        _workingDirectoryUtf8 = AllocEmptyUtf8();
        _commandUtf8 = AllocEmptyUtf8();
        _initialInputUtf8 = AllocEmptyUtf8();

        var surfaceConfig = NativeMethods.SurfaceConfigNew();
        surfaceConfig.PlatformTag = GhosttyPlatform.Windows;
        var panelPtr = SwapChainPanelInterop.QueryInterface(Panel);
        surfaceConfig.Platform.Windows = new GhosttyPlatformWindows
        {
            Hwnd = IntPtr.Zero,
            SwapChainPanel = panelPtr,
            SharedTextureOut = IntPtr.Zero,
            TextureWidth = 0,
            TextureHeight = 0,
        };
        surfaceConfig.ScaleFactor = Panel.CompositionScaleX > 0 ? Panel.CompositionScaleX : 1.0;
        surfaceConfig.Context = GhosttySurfaceContext.Window;
        surfaceConfig.WorkingDirectory = _workingDirectoryUtf8;
        surfaceConfig.Command = _commandUtf8;
        surfaceConfig.InitialInput = _initialInputUtf8;

        _surface = NativeMethods.SurfaceNew(_app, surfaceConfig);
        // Drop our ref to the panel: libghostty did not retain it.
        SwapChainPanelInterop.Release(panelPtr);

        // Prime the initial size when layout is actually settled.
        // SwapChainPanel.SizeChanged fires during the first layout pass,
        // which happens BEFORE Loaded, so by the time the surface exists
        // no further SizeChanged is queued until the user resizes the
        // window. Reading Panel.ActualWidth/Height here directly is
        // also unsafe: CompositionScaleX/Y is frequently reported as 1.0
        // until a later CompositionScaleChanged fires. Wait for
        // LayoutUpdated - the first one after the surface exists
        // guarantees ActualWidth/Height and the composition scale are
        // both valid - then revoke the handler.
        Panel.LayoutUpdated += OnFirstLayoutUpdated;

        // Request focus so keyboard input starts flowing immediately.
        // Focus lives on the UserControl now, not the panel.
        this.Focus(FocusState.Programmatic);
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
        // Drop the one-shot LayoutUpdated handler in case the initial size
        // never settled (zero ActualWidth/Height): otherwise it would keep
        // firing after teardown and pin the control via the closure.
        Panel.LayoutUpdated -= OnFirstLayoutUpdated;

        // Stop and detach the resize timer first so a pending tick cannot
        // observe a half-freed surface. The timer holds a strong reference
        // to this control via OnResizeTick, so leaving it pending across
        // an Unloaded would also leak.
        if (_resizeTimer is not null)
        {
            _resizeTimer.Stop();
            _resizeTimer.Tick -= OnResizeTick;
            _resizeTimer = null;
        }

        // Tear down in reverse order. Each free is a no-op on a zero handle.
        if (_surface.Handle != IntPtr.Zero) NativeMethods.SurfaceFree(_surface);
        if (_app.Handle != IntPtr.Zero) NativeMethods.AppFree(_app);
        if (_config.Handle != IntPtr.Zero) NativeMethods.ConfigFree(_config);
        if (_workingDirectoryUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(_workingDirectoryUtf8);
        if (_commandUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(_commandUtf8);
        if (_initialInputUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(_initialInputUtf8);

        _surface = default;
        _app = default;
        _config = default;
        _workingDirectoryUtf8 = IntPtr.Zero;
        _commandUtf8 = IntPtr.Zero;
        _initialInputUtf8 = IntPtr.Zero;
        _initialized = false;

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

    // Runtime callbacks --------------------------------------------------
    //
    // These fire on libghostty's thread. For this first pass we implement
    // the minimum required to avoid null-deref on the Zig side. Marshaling
    // back to the UI thread for things like clipboard and actions will be
    // added when those features are wired.

    private void OnWakeup(IntPtr userdata)
    {
        // Fires on libghostty's thread. Hop to the UI dispatcher so the
        // tick (and any resulting draws) lands on the right queue.
        //
        // OnUnloaded also runs on the UI thread, so the dispatched lambda
        // and the teardown are serialized: either Unloaded ran first and
        // the lambda sees a zero handle, or the lambda ran first and
        // Unloaded waits its turn. No lock needed.
        var dq = DispatcherQueue;
        if (dq is null) return;
        dq.TryEnqueue(() =>
        {
            if (_app.Handle != IntPtr.Zero) NativeMethods.AppTick(_app);
        });
    }

    private bool OnAction(GhosttyApp _, IntPtr targetPtr, IntPtr actionPtr)
    {
        // ghostty_action_s layout:
        //   { int32 tag; <union> action; }
        // The union is 8-byte aligned on x64 so it starts at offset 8.
        // We read the tag first and only touch the union for variants we
        // actually handle. Returning false for an unhandled tag lets the
        // core fall back to its default behavior. We also return false on
        // any path that fails to actually invoke the handler (null pointer,
        // null dispatcher) so the core never thinks we handled an action
        // we silently dropped.
        if (actionPtr == IntPtr.Zero) return false;
        var tag = (GhosttyActionTag)Marshal.ReadInt32(actionPtr);

        switch (tag)
        {
            case GhosttyActionTag.SetTitle:
            {
                // set_title_s is { const char* title }, so the first
                // pointer-sized word of the union is the UTF-8 title.
                var titlePtr = Marshal.ReadIntPtr(actionPtr, 8);
                var title = Marshal.PtrToStringUTF8(titlePtr) ?? string.Empty;
                var dq = DispatcherQueue;
                if (dq is null) return false;
                dq.TryEnqueue(() => TitleChanged?.Invoke(this, title));
                return true;
            }

            case GhosttyActionTag.RingBell:
            {
                // MessageBeep is thread-safe; no dispatcher hop needed.
                NativeMethods.MessageBeep(NativeMethods.MB_OK);
                return true;
            }

            case GhosttyActionTag.CloseWindow:
            {
                var dq = DispatcherQueue;
                if (dq is null) return false;
                dq.TryEnqueue(() => CloseRequested?.Invoke(this, EventArgs.Empty));
                return true;
            }

            default:
                // Everything else falls back to libghostty defaults.
                return false;
        }
    }

    private bool OnReadClipboard(IntPtr userdata, GhosttyClipboard kind, IntPtr state) => false;
    private void OnConfirmReadClipboard(IntPtr userdata, IntPtr str, IntPtr state, GhosttyClipboardRequest req) { }
    private void OnWriteClipboard(IntPtr userdata, GhosttyClipboard kind, IntPtr content, UIntPtr count, bool confirm) { }
    private void OnCloseSurface(IntPtr userdata, bool processAlive) { }

    // Size / scale -------------------------------------------------------

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _resizeTimer;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_surface.Handle == IntPtr.Zero) return;
        KickResizeDebounce();
    }

    private void KickResizeDebounce()
    {
        // FIXME: the DX12 renderer recreates the swap chain on every
        // set_size, which tears down GPU resources faster than the
        // compositor can follow when WinUI 3 fires SizeChanged per pixel
        // during a drag. The fix is in the renderer: ResizeBuffers instead
        // of full recreate. Until that lands, debounce here. Drop this
        // entire timer once the renderer is idempotent.
        if (_resizeTimer is null)
        {
            _resizeTimer = DispatcherQueue.CreateTimer();
            _resizeTimer.Interval = TimeSpan.FromMilliseconds(30);
            _resizeTimer.IsRepeating = false;
            _resizeTimer.Tick += OnResizeTick;
        }
        _resizeTimer.Start();
    }

    private void OnResizeTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_surface.Handle == IntPtr.Zero) return;

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
        NativeMethods.SurfaceSetSize(_surface, w, h);
    }

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        if (_surface.Handle == IntPtr.Zero) return;
        // Push the new scale to libghostty, then re-kick the debounced
        // resize path so the pixel dimensions get recomputed with the
        // new scale. DPI change (e.g. moving the window between monitors)
        // changes the pixel size even though the DIP size is unchanged.
        NativeMethods.SurfaceSetContentScale(_surface, sender.CompositionScaleX, sender.CompositionScaleY);
        KickResizeDebounce();
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
        if (_app.Handle != IntPtr.Zero) NativeMethods.AppSetFocus(_app, focused);
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

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_surface.Handle == IntPtr.Zero) return;
        var pt = e.GetCurrentPoint(Panel);
        var delta = pt.Properties.MouseWheelDelta / 120.0;  // 120 = one notch
        var isHorizontal = pt.Properties.IsHorizontalMouseWheel;
        NativeMethods.SurfaceMouseScroll(
            _surface,
            isHorizontal ? delta : 0.0,
            isHorizontal ? 0.0 : delta,
            0);  // scroll mods packed bitfield - wire when we need them
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

    private void OnKeyDown(object sender, KeyRoutedEventArgs e) =>
        SendKey(e, GhosttyInputAction.Press);

    private void OnKeyUp(object sender, KeyRoutedEventArgs e) =>
        SendKey(e, GhosttyInputAction.Release);

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
        var key = new GhosttyInputKey
        {
            Action = action,
            Mods = CurrentMods(),
            ConsumedMods = GhosttyMods.None,
            Keycode = e.KeyStatus.ScanCode,
            Text = IntPtr.Zero,
            UnshiftedCodepoint = 0,
            Composing = false,
        };
        var handled = NativeMethods.SurfaceKey(_surface, key);
        if (handled) e.Handled = true;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_surface.Handle == IntPtr.Zero) return;

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
        if ((GetKeyState(VK_LSHIFT) & 0x8000) != 0) mods |= GhosttyMods.Shift;
        if ((GetKeyState(VK_RSHIFT) & 0x8000) != 0) mods |= GhosttyMods.Shift | GhosttyMods.ShiftRight;
        if ((GetKeyState(VK_LCONTROL) & 0x8000) != 0) mods |= GhosttyMods.Ctrl;
        if ((GetKeyState(VK_RCONTROL) & 0x8000) != 0) mods |= GhosttyMods.Ctrl | GhosttyMods.CtrlRight;
        if ((GetKeyState(VK_LMENU) & 0x8000) != 0) mods |= GhosttyMods.Alt;
        if ((GetKeyState(VK_RMENU) & 0x8000) != 0) mods |= GhosttyMods.Alt | GhosttyMods.AltRight;
        if ((GetKeyState(VK_LWIN) & 0x8000) != 0) mods |= GhosttyMods.Super;
        if ((GetKeyState(VK_RWIN) & 0x8000) != 0) mods |= GhosttyMods.Super | GhosttyMods.SuperRight;
        if ((GetKeyState(VK_CAPITAL) & 0x0001) != 0) mods |= GhosttyMods.Caps;
        if ((GetKeyState(VK_NUMLOCK) & 0x0001) != 0) mods |= GhosttyMods.Num;
        return mods;
    }

    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4; // left Alt
    private const int VK_RMENU = 0xA5; // right Alt / AltGr
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_CAPITAL = 0x14;
    private const int VK_NUMLOCK = 0x90;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey);
}
