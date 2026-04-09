using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Ghostty.Clipboard;
using Ghostty.Controls;
using Ghostty.Core.Clipboard;
using Ghostty.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Ghostty.Hosting;

/// <summary>
/// Per-window owner of the libghostty config + app handles and the
/// runtime callback surface. Holds a dictionary mapping
/// <see cref="GhosttySurface"/> handles to the <see cref="TerminalControl"/>
/// that owns them so action-callback <c>target</c> arguments can be routed
/// to the correct leaf.
///
/// Lifetime: created once by <see cref="MainWindow"/> before any terminal
/// surface is constructed, disposed when the window closes. The app handle
/// is passed to each <see cref="TerminalControl"/> via its
/// <see cref="TerminalControl.Host"/> property before it is loaded.
/// </summary>
internal sealed class GhosttyHost : IDisposable
{
    private GhosttyConfig _config;
    private GhosttyApp _app;

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

    private ClipboardBridge? _clipboardBridge;

    // Delegates must be retained as fields; P/Invoke hands out native
    // function pointers the GC cannot track.
    private GhosttyWakeupCb? _wakeupCb;
    private GhosttyActionCb? _actionCb;
    private GhosttyReadClipboardCb? _readClipboardCb;
    private GhosttyConfirmReadClipboardCb? _confirmReadClipboardCb;
    private GhosttyWriteClipboardCb? _writeClipboardCb;
    private GhosttyCloseSurfaceCb? _closeSurfaceCb;

    // ConcurrentDictionary: Register/Unregister run on the UI thread but
    // lookups happen on libghostty's callback thread in OnAction and
    // OnCloseSurface. A plain Dictionary would race here.
    private readonly ConcurrentDictionary<IntPtr, TerminalControl> _surfaces = new();
    private readonly DispatcherQueue _dispatcher;

    public GhosttyApp App => _app;

    public GhosttyHost(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        NativeMethods.Init(UIntPtr.Zero, IntPtr.Zero);

        _config = NativeMethods.ConfigNew();
        NativeMethods.ConfigLoadDefaultFiles(_config);
        NativeMethods.ConfigFinalize(_config);

        _wakeupCb = OnWakeup;
        _actionCb = OnAction;
        _readClipboardCb = OnReadClipboard;
        _confirmReadClipboardCb = OnConfirmReadClipboard;
        _writeClipboardCb = OnWriteClipboard;
        _closeSurfaceCb = OnCloseSurface;

        // Build the clipboard bridge after all delegate fields are assigned.
        // The bridge takes lambdas that close over `this`, so the delegates
        // themselves are not captured -- the ordering relative to the runtime
        // config struct does not matter for correctness, but keeping it here
        // makes the construction visually adjacent to the callbacks it serves.
        var clipboardBackend = new WinUiClipboardBackend(_dispatcher);
        var clipboardConfirmer = new DialogClipboardConfirmer(
            _dispatcher,
            xamlRootProvider: ResolveXamlRootForSurface);
        var clipboardService = new ClipboardService(clipboardBackend, clipboardConfirmer);
        _clipboardBridge = new ClipboardBridge(
            _dispatcher,
            clipboardService,
            resolveSurface: ResolveSurfaceFromUserdata,
            isSurfaceAlive: IsSurfaceAlive);

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
    }

    public void Register(GhosttySurface surface, TerminalControl control)
    {
        if (surface.Handle == IntPtr.Zero) return;
        var added = _surfaces.TryAdd(surface.Handle, control);
        Debug.Assert(added, "surface handle collision in GhosttyHost registry");
    }

    public void Unregister(GhosttySurface surface)
    {
        if (surface.Handle == IntPtr.Zero) return;
        _surfaces.TryRemove(surface.Handle, out _);
    }

    public void Dispose()
    {
        // Clear before AppFree so any late callbacks libghostty emits during
        // teardown miss the lookup and become harmless no-ops.
        _surfaces.Clear();
        if (_app.Handle != IntPtr.Zero) NativeMethods.AppFree(_app);
        if (_config.Handle != IntPtr.Zero) NativeMethods.ConfigFree(_config);
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

    private bool OnAction(GhosttyApp _, IntPtr targetPtr, IntPtr actionPtr)
    {
        // ABI note: ghostty_runtime_action_cb is declared as
        //
        //   bool action_cb(ghostty_app_t, ghostty_target_s, ghostty_action_s);
        //
        // Both target and action are passed BY VALUE in C, but on the Windows
        // x64 calling convention any struct larger than 8 bytes is passed via
        // a hidden pointer to a caller-allocated copy. ghostty_target_s is
        // 16 bytes:
        //
        //   struct ghostty_target_s {
        //     ghostty_target_tag_e tag;   // int32 at offset 0
        //     // 4 bytes padding
        //     union {                     // 8-byte aligned
        //       ghostty_surface_t surface; // pointer at offset 8
        //     } target;
        //   };
        //
        // ghostty_action_s is similarly oversized. The C# delegate therefore
        // declares both as IntPtr - the actual pointers we receive point at
        // ephemeral stack copies of the structs and must be DEREFERENCED to
        // get at their contents. Treating targetPtr as if it were the surface
        // handle silently misses every dictionary lookup.
        if (actionPtr == IntPtr.Zero || targetPtr == IntPtr.Zero) return false;

        var targetTag = Marshal.ReadInt32(targetPtr);
        if (targetTag != GhosttyTargetSurface) return false;
        var surfaceHandle = Marshal.ReadIntPtr(targetPtr, 8);
        if (!_surfaces.TryGetValue(surfaceHandle, out var control)) return false;

        // ghostty_action_s layout: { int32 tag; <union> action; }
        // Union starts at offset 8 (8-byte aligned on x64).
        var tag = (GhosttyActionTag)Marshal.ReadInt32(actionPtr);
        switch (tag)
        {
            case GhosttyActionTag.SetTitle:
            {
                var titlePtr = Marshal.ReadIntPtr(actionPtr, 8);
                var title = Marshal.PtrToStringUTF8(titlePtr) ?? string.Empty;
                // Capture the surface handle, not `control`: by the time the
                // dispatched lambda runs the control may have unregistered
                // and torn down. Re-check the dictionary on the UI thread.
                _dispatcher.TryEnqueue(() =>
                {
                    if (_surfaces.TryGetValue(surfaceHandle, out var c))
                        c.RaiseTitleChanged(title);
                });
                return true;
            }

            case GhosttyActionTag.RingBell:
            {
                NativeMethods.MessageBeep(NativeMethods.MB_OK);
                return true;
            }

            case GhosttyActionTag.CloseWindow:
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (_surfaces.TryGetValue(surfaceHandle, out var c))
                        c.RaiseCloseRequested();
                });
                return true;
            }

            case GhosttyActionTag.ProgressReport:
            {
                // ghostty_action_progress_report_s sits at union offset 8
                // inside ghostty_action_s. Layout:
                //   int32 state  @ +8
                //   int8  prog   @ +12  (-1 sentinel when no percent)
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
                    if (_surfaces.TryGetValue(surfaceHandle, out var c))
                        c.RaiseProgressChanged(tabState);
                });
                return true;
            }

            default:
                return false;
        }
    }

    private bool OnReadClipboard(IntPtr userdata, GhosttyClipboard kind, IntPtr state)
        => _clipboardBridge?.HandleRead(userdata, kind, state) ?? false;

    private void OnConfirmReadClipboard(IntPtr userdata, IntPtr str, IntPtr state, GhosttyClipboardRequest request)
        => _clipboardBridge?.HandleConfirm(userdata, str, state, request);

    private void OnWriteClipboard(IntPtr userdata, GhosttyClipboard kind, IntPtr content, UIntPtr count, bool confirm)
        => _clipboardBridge?.HandleWrite(userdata, kind, content, count, confirm);

    private void OnCloseSurface(IntPtr userdata, bool processAlive)
    {
        // userdata is the GCHandle.ToIntPtr value the owning TerminalControl
        // pinned for itself before SurfaceNew. Decode it back to the managed
        // control and raise CloseRequested on the UI thread; MainWindow's
        // CloseRequested handler is what actually closes the window.
        //
        // This callback fires from libghostty's thread on two paths today:
        // (1) the user typed `exit` in the shell and then pressed any key,
        //     which makes Surface.zig encode the keystroke, notice
        //     child_exited, and call self.close().
        // (2) a binding action ran .close_surface or .close_window.
        //
        // We deliberately ignore processAlive here. Confirm-on-close lives
        // at the libghostty layer and only invokes us once the user has
        // already agreed (or wait_after_command was off).
        if (userdata == IntPtr.Zero) return;

        // Decode the GCHandle, then confirm the resulting control is still
        // registered. If Unregister already ran on the UI thread the surface
        // is being torn down and this callback is a late arrival we drop.
        // Using the thread-safe ConcurrentDictionary lookup avoids a race
        // with a GCHandle that has been freed by OnUnloaded.
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
        foreach (var c in _surfaces.Values)
            if (ReferenceEquals(c, control)) return true;
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
        // The _surfaces dictionary is the authoritative live-surface registry.
        // Checking it here is cheap (ConcurrentDictionary ContainsKey), and
        // guards against a TerminalControl whose DisposeSurface() ran between
        // the bridge's dispatch and the async continuation completing.
        return surface != IntPtr.Zero && _surfaces.ContainsKey(surface);
    }

    private XamlRoot? ResolveXamlRootForSurface(IntPtr surface)
    {
        // Look up the TerminalControl that owns this specific surface so
        // the confirmation dialog lands on the originating window. In a
        // multi-window host, falling back to any live XamlRoot would put
        // an OSC 52 dialog from a background window on top of the
        // foreground one.
        //
        // If the surface is not (or no longer) registered, fall back to
        // any live control so a request during a focus-change race still
        // gets a dialog rather than silently auto-denying. If nothing is
        // live, return null and let DialogClipboardConfirmer auto-deny --
        // the safe fallback for a security-relevant dialog.
        if (surface != IntPtr.Zero && _surfaces.TryGetValue(surface, out var owner))
        {
            var ownerRoot = owner.XamlRoot;
            if (ownerRoot is not null) return ownerRoot;
        }

        foreach (var ctrl in _surfaces.Values)
        {
            var root = ctrl.XamlRoot;
            if (root is not null) return root;
        }
        return null;
    }
}
