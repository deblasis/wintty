using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Ghostty.Core.Panes;
using Ghostty.Core.ResizeOverlay;
using Ghostty.Core.Windows;
using Ghostty.Core.Search;
using Ghostty.Core.Settings;
using Ghostty.Hosting;
using Ghostty.Input;
using Ghostty.Interop;
using Ghostty.Motion;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// The one-shot guard for a key another surface consumed to close
    /// itself (see <see cref="ConsumedCloseKey"/>). That surface's KeyDown
    /// closes it and focus moves inside the same keystroke, but the key's
    /// WM_CHAR was posted before the KeyDown ran, so it is delivered to
    /// whatever holds focus next: this pane or any sibling. Without the arm
    /// the pane forwards it, and Enter's '\r' runs the prompt line.
    /// </summary>
    private ConsumedCloseArm _consumedCloseArm;

    /// <summary>
    /// Arms this surface for the trailing character of a consumed close key.
    /// The window calls it on every terminal it holds when
    /// <see cref="ConsumedCloseKey.Raised"/> fires.
    /// </summary>
    internal void ArmConsumedClose(ConsumedCloseChars chars) => _consumedCloseArm.Arm(chars);

    /// <summary>
    /// The retire half of the arm: OnKeyDown's first statement, split out so
    /// the test seam drives the same retire a real key press performs (a
    /// KeyRoutedEventArgs cannot be constructed in process). Every real
    /// character follows its own KeyDown on the element that receives it, so
    /// an arm still standing when the keyboard touches this surface again is
    /// stale. Returns whether one was standing.
    /// </summary>
    internal bool RetireStaleCharacterSuppress() => _consumedCloseArm.Retire();

    /// <summary>
    /// Raised when the user requests the pane context menu over this surface.
    /// The argument is the pointer position in this control's coordinates, or
    /// null when the request came from the keyboard (Shift+F10 / Menu key).
    /// The owner (MainWindow, via PaneHost) builds and shows the flyout.
    /// </summary>
    internal event EventHandler<Windows.Foundation.Point?>? ContextMenuRequested;

    /// <summary>
    /// True when the surface has a non-empty selection. Used to gate the
    /// context-menu Copy item. False when the surface is gone.
    /// </summary>
    internal bool HasSelection =>
        _surface.Handle != IntPtr.Zero && NativeMethods.SurfaceHasSelection(_surface);

    // Set on a right-button press that we are NOT forwarding to libghostty
    // (because the program has not captured the mouse, so the click opens our
    // context menu instead). Consumed by the matching release so we never
    // forward a half-pair (press without release, or vice versa) to libghostty.
    private bool _rightButtonOpensMenu;

    // Handles ------------------------------------------------------------

    private GhosttySurface _surface;
    private IntPtr _workingDirectoryUtf8;
    private IntPtr _commandUtf8;
    // What _commandUtf8 says, kept as text: the child-exited toast names it
    // (deblasis/wintty#1193). Null when the surface runs the default shell.
    private string? _surfaceCommandText;
    private IntPtr _initialInputUtf8;
    private IntPtr _customShaderUtf8;

    /// <summary>
    /// True when this surface was created with a per-surface shader override
    /// (<see cref="PreviewCustomShader"/>), i.e. it is a gallery preview and
    /// not a real terminal pane. Latched at surface creation, because that is
    /// the one point the override property is read. Used by
    /// <see cref="Ghostty.Hosting.GhosttyHost"/> to keep a preview shader's
    /// failure out of the app-level custom-shader notice, which talks about
    /// the user's config.
    /// </summary>
    internal bool IsPreviewSurface => _isPreviewSurface;
    private bool _isPreviewSurface;

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

    // A surface that libghostty could not create (renderer device init
    // failed: a GPU-less machine or a cold-composition boot window) is a
    // "not yet", not a verdict. The creation retries on this timer and, if
    // the budget runs out, degrades to a pane without a terminal surface:
    // logged, alive, no throw. The throw this replaced killed the app from
    // the layout handler; its predecessor dereferenced the zero handle
    // natively and AV'd the process (the GPU-less startup crash).
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _surfaceRetryTimer;
    private int _surfaceRetryAttemptsLeft;

    // Retry cadence and budget for a failed surface creation. Each failed
    // attempt is cheap - the device init fails before the pty spawns, so no
    // shell is started - but the attempts still burn a loaded machine, so
    // the budget is finite: after a minute of failed attempts the pane
    // degrades and a later new tab (a fresh control) tries again with its
    // own budget. The budget counts ATTEMPTS, not ticks: a tick while the
    // pane is unmeasured (a background tab, a leaf mid-reparent) spends
    // nothing, so hiding the pane during the retry window cannot spend its
    // way to a degraded pane it never let retry.
    private static readonly TimeSpan SurfaceRetryInterval = TimeSpan.FromMilliseconds(500);
    private const int SurfaceRetryMaxAttempts = 120;

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

    // High surrogate held across CharacterReceived events. WinUI delivers
    // supplementary-plane scalars as two events; see WmCharUtf8.
    private char _pendingWmCharHigh;

    // True while WinUI is driving an IME composition session. Preedit
    // updates go through SurfacePreedit; committed text still arrives
    // via CharacterReceived after TextCompositionEnded.
    private bool _imeComposing;

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
    /// Per-surface custom shader override for preview surfaces. When set
    /// (non-empty path), the surface's renderer uses ONLY this shader,
    /// replacing the configured custom-shader list. Must be set before the
    /// control loads: it is read once at surface creation (swap it later
    /// through <see cref="SetPreviewCustomShader"/>). Regular terminal
    /// surfaces leave it null and follow the app config.
    /// </summary>
    public string? PreviewCustomShader { get; set; }

    /// <summary>
    /// Command the surface runs instead of the profile's shell. Must be set
    /// before the control loads. Preview surfaces point this at a silent,
    /// never-exiting placeholder so the pty never delivers a single byte of
    /// its own: everything on screen comes from <see cref="WriteVt"/>, which
    /// makes the preview deterministic and race-free against shell banners.
    /// Regular terminal surfaces leave it null and use the snapshot's
    /// resolved command.
    /// </summary>
    public string? PreviewCommand { get; set; }

    /// <summary>
    /// Keyboard sink for a preview surface. When set, key presses and
    /// characters are delivered here INSTEAD of the pty: the preview's
    /// placeholder child is asleep (see <see cref="PreviewCommand"/>),
    /// so its stdin is exactly where keystrokes should stop. The shader
    /// picker sets this so clicking into the preview lets the user type
    /// freely into the fake DOS session the autoplay feed drives. Null
    /// on regular terminal panes, whose keyboard path is unchanged.
    /// </summary>
    internal IPreviewInputSink? PreviewInputSink { get; set; }

    /// <summary>
    /// Take keyboard focus as soon as the surface loads. Real terminal
    /// panes want this so keyboard input starts flowing immediately.
    /// Preview hosts inside other windows (the shader picker) set false:
    /// their focus belongs to the picker's own controls, and a loading
    /// preview would otherwise steal it on every selection change.
    /// Defaults to true.
    /// </summary>
    public bool AutoFocus { get; set; } = true;

    /// <summary>
    /// The raw libghostty surface handle for this control. Used by
    /// <see cref="Ghostty.Hosting.GhosttyHost"/> to resolve a per-surface
    /// userdata pointer back to the handle for clipboard callback completion.
    /// Returns <see cref="IntPtr.Zero"/> before the surface is created or
    /// after it is disposed.
    /// </summary>
    internal IntPtr SurfaceHandle => _surface.Handle;

    /// <summary>
    /// The grid and pixel size the surface had when SurfaceNew returned: the
    /// size its pty is created at. Read by the test seam; zeros before
    /// creation.
    /// </summary>
    internal (ushort Cols, ushort Rows, uint WidthPx, uint HeightPx) SpawnSize { get; private set; }

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
    /// Swap this surface's custom shader override live. The renderer rebuilds
    /// its post-process pipeline (the config-change path) while the terminal
    /// content, scrollback, and cursor are preserved, so a preview flipping
    /// through shaders never resets. A null or empty path clears the shader.
    /// No-op once the surface is gone. Only meaningful for surfaces created
    /// with <see cref="PreviewCustomShader"/>; regular surfaces follow the
    /// app config.
    /// </summary>
    internal void SetPreviewCustomShader(string? shaderPath)
    {
        if (_surfaceDisposed || _surface.Handle == IntPtr.Zero) return;
        NativeMethods.SurfaceSetCustomShader(_surface, shaderPath);
    }

    /// <summary>
    /// Feed raw VT bytes into the terminal as if the child program wrote
    /// them: sequences update the grid, cursor, and colors exactly like pty
    /// output. Used by preview surfaces, whose placeholder child never
    /// writes, to drive their canned session.
    ///
    /// Must be called on the UI thread, like the sibling
    /// <see cref="RequestRepaint"/> and <see cref="SetPreviewCustomShader"/>.
    /// The guard below is a plain read of a non-volatile field plus a read of
    /// <c>_surface.Handle</c>, and <see cref="DisposeSurface"/> flips that
    /// field and calls <c>SurfaceFree</c> from the UI thread with no
    /// synchronization at all. An off-thread caller can therefore see the
    /// guard pass, be preempted, and hand a freed surface pointer to
    /// libghostty (an access violation, not an exception). Making this
    /// genuinely callable from a pty reader thread needs a lock or an
    /// interlocked handle swap on BOTH sides, not a comment.
    /// </summary>
    internal unsafe void WriteVt(ReadOnlySpan<byte> bytes)
    {
        if (_surfaceDisposed || bytes.IsEmpty || _surface.Handle == IntPtr.Zero) return;
        fixed (byte* p = bytes)
        {
            NativeMethods.SurfaceVtWrite(_surface, p, (nuint)bytes.Length);
        }
    }

    /// <summary>
    /// Read the full screen contents for accessibility (UIA text provider).
    /// Returns "" once the surface is gone so a screen reader polling during
    /// teardown can't touch freed native state. Takes the renderer mutex; the
    /// automation peer caches the result for 500ms.
    ///
    /// WinUI marshals automation-peer calls onto the UI thread, which is also
    /// where _surfaceDisposed is written, so the guard and the read are not
    /// racing. Feed can run on another thread for a mux pane, but libghostty
    /// serializes reads on its renderer mutex. An empty string on a
    /// transient/teardown miss is the intended graceful degradation (a screen
    /// reader must not crash on a momentary read failure).
    /// </summary>
    internal string AccessibilityReadScreenText()
    {
        if (_surfaceDisposed || _surface.Handle == IntPtr.Zero) return "";
        return NativeMethods.SurfaceReadScreenText(_surface);
    }

    /// <summary>
    /// Read the current selection's flattened-viewport offsets for accessibility,
    /// or null when there is no selection or the surface is gone.
    /// </summary>
    internal (uint OffsetStart, uint OffsetLen)? AccessibilitySelectionOffsets()
    {
        if (_surfaceDisposed || _surface.Handle == IntPtr.Zero) return null;
        var sel = NativeMethods.SurfaceReadSelection(_surface);
        return sel is { } s ? (s.OffsetStart, s.OffsetLen) : null;
    }

    /// <summary>
    /// Read the viewport cells (codepoint + resolved fg/bg) for accessibility,
    /// or null when the surface is gone or the read fails. Takes the renderer
    /// mutex; the automation peer caches the result for 500ms.
    /// </summary>
    internal Ghostty.Core.Tabs.CellGrid? AccessibilityReadViewportCells()
    {
        if (_surfaceDisposed || _surface.Handle == IntPtr.Zero) return null;
        return NativeMethods.SurfaceReadCells(_surface);
    }

    /// <summary>
    /// Where this pane's grid sits on screen, in physical pixels, or null when
    /// the control has no usable layout (never measured, collapsed in a
    /// background tab, or detached mid-reparent). Backs UIA bounding
    /// rectangles and hit-testing.
    ///
    /// Computed on demand rather than cached on a layout pass: WinUI marshals
    /// automation-peer calls onto the UI thread, so live XAML is legal here,
    /// and a cache would only add a way for the reported rectangle to describe
    /// a frame that is no longer on screen.
    /// </summary>
    internal Ghostty.Core.Accessibility.ViewportGeometry? AccessibilityViewportGeometry()
    {
        try
        {
            if (XamlRoot is not { Content: { } root } xamlRoot) return null;
            var env = xamlRoot.ContentIslandEnvironment;
            if (env is null) return null;
            nint hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(env.AppWindowId);
            if (hwnd == 0) return null;

            // The panel's offset is in DIPs relative to the window content; the
            // window's client origin is already in physical screen pixels, so
            // only the former is scaled.
            // A discarded failure here leaves the point at (0,0), and the
            // geometry then claims the terminal viewport sits at the screen
            // origin -- a plausible rectangle, so IsUsable accepts it and a
            // screen reader is told where the text is with confidence.
            var client = new System.Drawing.Point(0, 0);
            if (!PInvoke.ClientToScreen(new Windows.Win32.Foundation.HWND(hwnd), ref client))
                return null;
            var scale = xamlRoot.RasterizationScale;
            var offset = Panel.TransformToVisual(root)
                .TransformPoint(new Windows.Foundation.Point(0, 0));

            var geom = new Ghostty.Core.Accessibility.ViewportGeometry(
                client.X + offset.X * scale,
                client.Y + offset.Y * scale,
                Panel.ActualWidth * scale,
                Panel.ActualHeight * scale);
            return geom.IsUsable ? geom : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // TransformToVisual throws for an element that is not in the tree,
            // which is exactly a pane that should not be reporting geometry, and
            // the island interop can fail the same way mid-teardown. Narrow
            // rather than bare: anything else here is a bug, and swallowing it
            // would leave a screen reader silently pointed at nothing.
            return null;
        }
    }

    // Stable identity for this pane in the automation tree. Assigned once and
    // kept for the control's lifetime so a client that bound to a pane still
    // finds it after the pane is backgrounded and shown again.
    private static int _nextAutomationId;
    private readonly string _automationId =
        "TerminalGrid-" + System.Threading.Interlocked.Increment(ref _nextAutomationId)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal string AccessibilityAutomationId => _automationId;

    private Ghostty.Accessibility.TerminalAutomationPeer? _automationPeer;

    // Cache the peer so UIA re-querying a still-loaded control reuses one instance
    // (and its single announcement timer) instead of accumulating peers/timers.
    protected override Microsoft.UI.Xaml.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        => _automationPeer ??= new Ghostty.Accessibility.TerminalAutomationPeer(this);

    /// <summary>
    /// Returns the pid of the shell process attached to this surface, or
    /// null when libghostty cannot report one (surface not yet created,
    /// already disposed, or the pty has not finished spawning). The
    /// active-process tracker roots its descendant walk here, and the tab
    /// reads it once to name itself after what the pane was launched into.
    ///
    /// On Windows the pty layer tracks no foreground process group, so
    /// <c>Subprocess.getProcessInfo</c> answers <c>.foreground_pid</c>
    /// from the spawned child's own HANDLE (<c>WindowsPty</c> itself still
    /// returns null, and is bypassed for this). The pid here is therefore
    /// the shell this surface started, not whatever is in front of it now.
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

    // Latest activity on this surface, in Environment.TickCount64
    // milliseconds -- keystrokes, pointer presses, and every callback
    // libghostty fires for a real state change (title, cwd, progress,
    // bell, scrollbar). Written from both the UI thread and the
    // libghostty thread, so a plain volatile long; read by the idle
    // tracker's sweep through PaneHost.LastActivityTick.
    private long _lastActivityTick;
    internal long LastActivityTick => Volatile.Read(ref _lastActivityTick);
    private void NoteActivity() => Volatile.Write(ref _lastActivityTick, Environment.TickCount64);

    // Raisers invoked by GhosttyHost after routing an action to this leaf.
    internal void RaiseTitleChanged(string title)
    {
        // A shell pushing a title is the pane doing something: stamp
        // before anything else so the idle clock sees this even if a
        // subscriber below throws.
        NoteActivity();
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
        // OSC 9;4 is pane output; same idle-stamp contract as the title.
        NoteActivity();
        CurrentProgress = state;
        ProgressChanged?.Invoke(this, state);
    }
    internal void RaisePromptReady() => PromptReady?.Invoke(this, EventArgs.Empty);
    internal void RaiseFirstRender() => FirstRender?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The shell reported its directory (OSC 7 via the pwd action).
    /// PaneHost records it on the pane so a duplicate or restore spawns
    /// the replacement shell there; the control itself does nothing
    /// else with it.
    /// </summary>
    internal event EventHandler<string?>? PwdChanged;
    internal void RaisePwdChanged(string? pwd)
    {
        // OSC 7 / 9;9 is pane output; same idle-stamp contract as the title.
        NoteActivity();
        PwdChanged?.Invoke(this, pwd);
    }

    /// <summary>
    /// Hand <paramref name="text"/> to this surface the way committed IME
    /// text arrives: one ghostty_surface_text call, no synthesized OS input
    /// and no focus requirement. The test seam's way to make a shell run
    /// something; a caller that wants the line submitted terminates it with
    /// "\r" itself. Returns false when the surface is gone.
    /// </summary>
    internal bool TestSeamSendText(string text)
    {
        if (_surface.Handle == IntPtr.Zero || string.IsNullOrEmpty(text)) return false;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        unsafe
        {
            fixed (byte* p = bytes)
                NativeMethods.SurfaceText(_surface, (IntPtr)p, (UIntPtr)bytes.Length);
        }
        return true;
    }

#if TESTSEAM
    /// <summary>
    /// A right-click at the centre of the pane, for the test seam: the press
    /// and release halves the pointer handlers run, in order, so the capture
    /// gate and the armed flag are both on the path. Null when the pane has
    /// no size yet; false when the gate refused (the program has the mouse).
    /// </summary>
    internal bool? TestSeamRightClick()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return null;
        return BeginRightClickMenu()
            && CompleteRightClickMenu(new Windows.Foundation.Point(ActualWidth / 2, ActualHeight / 2));
    }

    /// <summary>
    /// A discrete mouse wheel, for the test seam: the notches OnPointerWheelChanged
    /// hands libghostty for a vertical wheel with no modifier held (positive
    /// scrolls up). Returns false when the surface is gone.
    /// </summary>
    internal bool TestSeamScroll(double notches)
    {
        if (_surface.Handle == IntPtr.Zero) return false;
        NoteActivity();
        NativeMethods.SurfaceMouseScroll(_surface, 0.0, notches, 0);
        return true;
    }

    /// <summary>
    /// The viewport as libghostty last reported it (total rows, the first
    /// visible row, visible rows), so a driver can see a scroll happen.
    /// </summary>
    internal (ulong Total, ulong Offset, ulong Len) TestSeamViewport
    {
        get
        {
            lock (_scrollbarLock)
                return (_pendingScrollbarTotal, _pendingScrollbarOffset, _pendingScrollbarLen);
        }
    }

    /// <summary>This pane's search bar, whose key handler the seam drives.</summary>
    internal Search.SearchBarControl TestSeamSearchBar => SearchBar;

    /// <summary>
    /// One character through the decision a real WM_CHAR takes once it is
    /// past the routed guards. A character the arm does not drop reaches the
    /// shell exactly as typing would, so the seam gates this op behind its
    /// input opt-in. Returns true when the consumed-close arm dropped it.
    /// </summary>
    internal bool TestSeamCharacter(char ch) =>
        _surface.Handle != IntPtr.Zero && HandleCharacter(ch);

    /// <summary>The keys this surface is armed for right now.</summary>
    internal ConsumedCloseChars TestSeamConsumedCloseArm => _consumedCloseArm.Armed;

    /// <summary>The panel's composition scale, so a driver can say which display scale it measured at.</summary>
    internal double TestSeamCompositionScale => Panel.CompositionScaleX;

    /// <summary>
    /// The hook's implementation: when the surface-fault seam op has armed a
    /// count, consume one and report the libghostty-null-surface failure.
    /// UI-thread-only, like everything that creates surfaces. Its
    /// declaration lives outside this region (above TryCreateSurface) so a
    /// shipping build - which compiles neither member - still sees the
    /// declaration, drops the implementation, and removes the call.
    /// </summary>
    partial void SeamFaultSurfaceNew(ref bool faulted)
    {
        if (TestSeamFaultSurfaceNew > 0)
        {
            TestSeamFaultSurfaceNew--;
            faulted = true;
        }
    }

    /// <summary>
    /// Seam fault: the number of NEXT surface creations that report the
    /// libghostty-null-surface failure. Armed by the surface-fault seam op
    /// to reproduce the GPU-less startup crash's precondition on hardware
    /// that has a working renderer.
    /// </summary>
    internal static int TestSeamFaultSurfaceNew;

    /// <summary>Seam readback: whether this control holds a live surface.</summary>
    internal bool TestSeamHasSurface => _surface.Handle != IntPtr.Zero;

    /// <summary>
    /// Seam readback: attempts left in the retry budget, or -1 when no retry
    /// is armed (never armed, succeeded, given up, or stopped).
    /// </summary>
    internal int TestSeamSurfaceRetriesLeft =>
        _surfaceRetryTimer is null ? -1 : _surfaceRetryAttemptsLeft;

    /// <summary>
    /// Seam readback: whether a creation was ATTEMPTED at least once (the
    /// latch is set before the attempt). Together with
    /// <see cref="TestSeamSurfaceRetriesLeft"/> this tells a driver
    /// "never attempted" (false/-1) apart from "gave up" (true/-1), which
    /// a bare -1 cannot.
    /// </summary>
    internal bool TestSeamSurfaceAttempted => _surfaceCreated;
#endif

    /// <summary>
    /// Notify the UIA automation peer that the terminal selection changed so
    /// assistive tech re-queries it. No-op when no automation peer has been
    /// created (i.e. no AT client is attached), so non-AT users pay nothing.
    /// </summary>
    internal void RaiseSelectionChanged() => _automationPeer?.RaiseSelectionChangedEvent();

    private bool _bellBorderActive;
    private bool _bellTitlePending;
    private BellAudioPlayer? _bellAudio;

    // Fade-out duration for the visual bell border once acknowledged.
    // Duration matches the macOS easeInOut(duration: 0.3) bell border
    // animation; the curve is the exit-fade ease-out below, because a
    // linear decay at this length reads as the border glitching away.
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
        // A bell is the pane demanding attention -- the opposite of
        // asleep. This is the control-level hook, so it fires for
        // background surfaces too (the tab-level re-emission is
        // active-leaf only, but the idle clock must see this one).
        NoteActivity();
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

        if (!Ghostty.Services.SystemAnimations.Enabled())
        {
            // Reduce-motion cut: the border leaves in the same frame the
            // dismissal lands, rather than riding a fade under a user who
            // told the system to stop moving things.
            BellOverlay.Opacity = 0.0;
            BellOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(BellBorderFadeMs)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
            },
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
        AnimationActivityRegistry.BeginStoryboard(sb, BellOverlay, "Opacity");
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
        // Scroll state changes when output moves lines through the
        // viewport, so this doubles as the streaming-output signal the
        // idle clock reads: a background tab running a build grows its
        // scrollback and stays awake without a single keystroke. Runs
        // on the libghostty thread; the stamp is a volatile write.
        NoteActivity();
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

    /// <summary>Raised once, right after this control's libghostty surface is
    /// created and registered. PaneHost uses it to start the startup glow.</summary>
    public event EventHandler? SurfaceSpawned;

    /// <summary>Raised from <see cref="DisposeSurface"/> while the surface is
    /// still valid, for holders of native state keyed on this control's
    /// surface pointer -- the window's inline theme picker keeps input,
    /// scroll and resize redirects installed on it, and handing those back
    /// writes through the pointer.
    ///
    /// Nothing at the tab level can serve them: TabManager.CloseTab frees the
    /// tab's leaves before it announces the tab is gone, so a TabRemoved
    /// subscriber already holds a dangling pointer by the time it runs.
    ///
    /// Fires at most once per control, and never for a control whose surface
    /// was never created.</summary>
    internal event EventHandler? SurfaceDisposing;

    // Distance the search bar floats from the terminal's top-right, on
    // top of whatever gutter the pane chrome reserves.
    private const double SearchBarInset = 8;

    public TerminalControl()
    {
        InitializeComponent();

        // Hold the terminal surface off the pane's edges so the chrome
        // PaneHost overlays there does not paint over live cells -- see
        // PaneChrome for why the gutter exists and how it is sized.
        // PushSurfaceSize reads Panel.ActualWidth, so insetting the panel
        // is also what keeps libghostty's grid matched to what is visible.
        //
        // Applied here rather than in XAML so PaneChrome stays the single
        // source of truth for the thicknesses, and set before the first
        // measure so the surface is never sized to the full rect even for
        // one layout pass.
        var gutter = new Thickness(Core.Panes.PaneChrome.SurfaceInset);
        Panel.Margin = gutter;

        // Overlays that read as part of the terminal content follow the
        // surface into the gutter: the scrollbar tracks the grid's right
        // edge, the resize pill positions itself within the grid, and the
        // search bar floats over it. Leaving any of them on the pane
        // bounds would sit them over the border stroke, and for the
        // search bar would quietly shrink its own inset by the gutter.
        // The bell flash and the URL banner stay on the pane bounds --
        // both are pane-edge chrome themselves.
        VerticalScrollBar.Margin = gutter;
        ResizeOverlay.Margin = gutter;
        // Assigned outright rather than added to a XAML margin: every
        // other line here is idempotent, and this block is one refactor
        // away from being re-run on attach the way ApplyGutterBrush now
        // is. Accumulating would walk the search bar inward every time.
        SearchBar.Margin = new Thickness(
            0,
            SearchBarInset + gutter.Top,
            SearchBarInset + gutter.Right,
            0);

        // Bell flash spans the pane bounds, so its stroke is what fills
        // the gutter while it is up. Sized from the gutter for that
        // reason -- a thicker chrome would otherwise leave it painting
        // over live cells again.
        BellOverlay.BorderThickness = gutter;

        ApplyGutterBrush();
    }

    // Last gutter colour written to SurfaceRoot, so a repaint
    // that resolves to the same value neither allocates a brush nor
    // dirties the pane's visual. Reloads are frequent (Ctrl+Shift+Wheel
    // walks background-opacity one step per notch) and every leaf of
    // every tab is repainted on each one, plus every leaf repaints on
    // attach.
    private uint? _lastGutterArgb;

    /// <summary>
    /// Repaint the gutter to match the terminal background. Called when
    /// the control is built, on every attach, and by MainWindow on a
    /// config reload -- the three ways this leaf's fill can fall behind
    /// the surface it abuts.
    ///
    /// This fill covers the gutter around the panel, not the band a
    /// resize exposes inside it. That band cannot be painted from here:
    /// SwapChainPanel throws on the Background setter ("Setting
    /// 'Background' property is not supported on SwapChainPanel",
    /// COMException 0x80004005, measured as a startup crash 0xC000027B
    /// during the rc.8 resize-flash work). Film evidence ties the band
    /// to the swap chain's own background color, set renderer-side:
    /// adding that fill alone took the band from pure black to
    /// near-background at the first filmed frame.
    /// </summary>
    internal void ApplyGutterBrush()
    {
        var cfg = App.ConfigService;
        if (cfg is null) return;

        // Deliberately NOT flattened under low power the way
        // ApplyBackdropStyle flattens the window backdrop. The gutter's
        // peer is the surface it abuts, not the backdrop behind it, and
        // nothing pushes a power-state opacity override into libghostty --
        // the surface keeps clearing at background-opacity whatever the
        // power state. Following the backdrop here would produce the
        // saturated frame this fill exists to avoid.
        var argb = Core.Panes.PaneChrome.GutterArgb(
            cfg.BackgroundColor, cfg.BackgroundOpacity);
        if (_lastGutterArgb == argb) return;

        // Commit the cache only after the write lands. A config reload
        // can reach a leaf whose visual tree is tearing down, and the
        // property set throws there; recording first would leave the leaf
        // believing it had painted a colour it never did, and every later
        // repaint of that value would early-out. PaneHost.RefreshGutterBrush
        // swallows that throw per leaf, so without this ordering the miss
        // would also be silent. Note ApplyRootGridBackground commits
        // first -- same hazard, not yet addressed there.
        SurfaceRoot.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        _lastGutterArgb = argb;
    }

    // Lifecycle ----------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Tree-dependent setup runs every Loaded (idempotent): walk
        // ancestors for the ScrollViewer fix, and arm the one-shot
        // LayoutUpdated handler, which creates the surface on the first
        // layout pass that gives the panel a size, or pushes the settled
        // size to a surface that already exists (a reparent).
        Panel.LayoutUpdated -= OnFirstLayoutUpdated;
        Panel.LayoutUpdated += OnFirstLayoutUpdated;
        DisableAncestorScrollViewerTabStop();

        // Repaint the gutter on every attach, not just at construction.
        // A leaf can be off the tree while the config changes -- retained
        // by the undo stack after a soft close, or mid-flight in a
        // cross-window tab detach -- and would otherwise come back
        // wearing the pre-change fill. Cheap: the colour cache makes the
        // steady-state call a comparison.
        ApplyGutterBrush();

        // SearchBar lifetime matches the control's; wiring `this` as the
        // host is idempotent so doing it on every Loaded is safe and
        // survives any visual-tree reparent.
        SearchBar.SearchHost = this;
    }

    /// <summary>
    /// The test seam's surface-creation fault hook, implemented inside the
    /// file's one #if TESTSEAM region below - everything the seam touches
    /// stays there, which is what the seam wiring guard pins. A shipping
    /// build has no implementation, so the compiler removes the declaration
    /// and every call site (argument evaluations included).
    /// </summary>
    partial void SeamFaultSurfaceNew(ref bool faulted);

    /// <summary>
    /// Create the libghostty surface, and with it the pty, at the panel's
    /// measured size. Called from the first layout pass after Loaded, not
    /// from Loaded itself, the way Windows Terminal starts its connection
    /// only once the control has a non-zero laid-out size: Loaded can report
    /// a height the first layout pass then settles a few pixels lower, and a
    /// hidden tab is not measured at all until it is first shown. Starting
    /// earlier means starting at a size the shell formats its startup output
    /// for and the pane never has.
    ///
    /// Returns false, having done nothing, while the panel has no measured
    /// size; the caller tries again on the next layout pass. A hidden or
    /// restored background tab therefore starts its shell when it is first
    /// shown. Also returns false when libghostty could not build the surface
    /// (renderer device init failed): that attempt is released and the
    /// creation retries on a timer, degrading to a surface-less pane if the
    /// budget runs out. The app stays up either way; this method never
    /// throws on the failure paths a GPU-less machine produces.
    /// </summary>
    private bool TryCreateSurface()
    {
        if (SurfacePixelSize.Initial(
                Panel.ActualWidth, Panel.ActualHeight,
                Panel.CompositionScaleX, Panel.CompositionScaleY) is not (uint initialWidth, uint initialHeight))
        {
            return false;
        }

        // Surface creation runs exactly once per control instance, even
        // across multiple reparents, and even when it fails part way.
        //
        // MainWindow's picker cleanup leans on that: comparing SurfaceHandle
        // against the pointer it saved only proves reachability while a
        // control's handle can be that pointer or zero and never a recycled
        // third one.
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
        // PreviewCommand wins over the snapshot: a preview surface never
        // wants the real shell's banner interleaving with its canned feed.
        // The same text is latched for the child-exited toast, which names
        // what ran (deblasis/wintty#1193); null means the default shell.
        var surfaceCommand = PreviewCommand is { Length: > 0 } previewCmd
            ? previewCmd
            : Snapshot is { ResolvedCommand: { Length: > 0 } }
                // An argv (-e, a direct: command) goes over as one, so it is
                // never handed to cmd.exe (#1136).
                ? Ghostty.Core.Profiles.PaneCommandPolicy.SurfaceCommand(Snapshot)
                : null;
        _surfaceCommandText = surfaceCommand;
        _commandUtf8 = surfaceCommand is not null ? AllocUtf8(surfaceCommand) : AllocEmptyUtf8();
        _initialInputUtf8 = AllocEmptyUtf8();
        // Preview shader override: set BEFORE the control loads (it is read
        // once at surface creation in OnLoaded).
        _customShaderUtf8 = string.IsNullOrEmpty(PreviewCustomShader)
            ? IntPtr.Zero
            : AllocUtf8(PreviewCustomShader);
        // Latched here rather than recomputed from PreviewCustomShader later:
        // this is the one moment the property is defined to be read, and
        // SetPreviewCustomShader swaps the surface's shader without touching
        // it. What the flag means is "this surface was born a preview", which
        // is exactly the question the shader-failure notice needs answered.
        _isPreviewSurface = _customShaderUtf8 != IntPtr.Zero;

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
        surfaceConfig.CustomShader = _customShaderUtf8;
        // The pty starts at this size. Without it libghostty would start from
        // an 800x600 px placeholder and the shell would spawn on that grid
        // (about 80x28) until a resize reached it.
        surfaceConfig.Width = initialWidth;
        surfaceConfig.Height = initialHeight;
        // A one-off launch command (-e, initial-command) closes its pane
        // when it exits 0 and stays open to show a failure, the way Windows
        // Terminal's closeOnExit "graceful" does (#1175). Every other pane
        // keeps libghostty's wait-after-command behaviour.
        surfaceConfig.CloseOnCleanExit =
            Ghostty.Core.Profiles.PaneCommandPolicy.ClosesOnCleanExit(Snapshot) ? (byte)1 : (byte)0;

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
            // The seam's fault hook is a partial method so the whole seam
            // stays in this file's one #if TESTSEAM region: TESTSEAM builds
            // implement it there; a shipping build has no implementation, and
            // the compiler removes the call (argument evaluations included).
            var seamFaulted = false;
            SeamFaultSurfaceNew(ref seamFaulted);
            if (seamFaulted)
            {
                // Seam-injected failure: this creation reports what libghostty
                // reports when the renderer device cannot be built (the
                // GPU-less startup crash's precondition), without needing that
                // hardware to reproduce it.
                _surface = default;
            }
            else
            {
                _surface = NativeMethods.SurfaceNew(app, surfaceConfig);
            }
        }
        catch (Exception ex)
        {
            // A managed exception out of the P/Invoke itself (missing
            // export, marshaling OOM) is not the GPU-less failure this
            // file tolerates - it escapes and kills the app - but the
            // attempt's references are released all the same so the
            // balance stays total right up to the death.
            SwapChainPanelInterop.Release(panelPtr);
            if (_selfHandle.IsAllocated) _selfHandle.Free();
            FreePendingSurfaceConfig();
            System.Diagnostics.Debug.WriteLine(
                $"{AppIdentity.LogTag} SurfaceNew failed: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
        // libghostty returns null when it could not build the surface - the
        // renderer's device or swap chain failed. On a GPU-less machine (or
        // in a cold-composition boot window on one) this is transient: the
        // same launch succeeds once the machine settles. Every native call
        // below dereferences the handle, so the zero handle must never reach
        // them; the pre-retry code either AV'd on it or threw from the
        // layout handler, and both killed the app at startup. Give the
        // device time to come up, then degrade to a surface-less pane.
        if (_surface.Handle == IntPtr.Zero)
        {
            SwapChainPanelInterop.Release(panelPtr);
            if (_selfHandle.IsAllocated) _selfHandle.Free();
            FreePendingSurfaceConfig();
            ScheduleSurfaceCreationRetry();
            return false;
        }
        var spawn = NativeMethods.SurfaceSize(_surface);
        SpawnSize = (spawn.Columns, spawn.Rows, spawn.WidthPx, spawn.HeightPx);

        // Drop our ref: libghostty does not retain the panel pointer.
        SwapChainPanelInterop.Release(panelPtr);
        Host.Register(_surface, this);

        // Bind the renderer's DirectComposition surface handle to the
        // panel. libghostty presents into this handle; binding it (rather
        // than the swap chain object) lets DWM composite the panel as soon
        // as the window is shown, avoiding the blank-until-focus startup
        // race, and keeps the binding valid across resizes.
        //
        // Best-effort: a bind failure must not abort surface setup, since
        // the surface is already created and registered above. Worst case
        // the panel composites on the next OS activation (the pre-fix
        // behavior), so log and continue rather than throwing.
        var swapChainHandle = NativeMethods.SurfaceGetSwapChainHandle(_surface);
        if (swapChainHandle == IntPtr.Zero)
        {
            Ghostty.Logging.StaticLoggers.App.LogWarning(
                "SwapChainPanel surface handle was null; panel left unbound");
        }
        else
        {
            try
            {
                SwapChainPanelInterop.SetSwapChainHandle(Panel, swapChainHandle);
            }
            catch (Exception ex)
            {
                Ghostty.Logging.StaticLoggers.App.LogWarning(
                    "Binding swap-chain handle to panel failed: {Message}", ex.Message);
            }
        }

        // Request focus so keyboard input starts flowing immediately.
        // Focus lives on the UserControl now, not the panel. Preview
        // surfaces (AutoFocus = false) opt out: they live inside other
        // windows whose focus must stay put. A retry-tick success can land
        // while the pane is a hidden background tab (a collapsed pane keeps
        // its last measured size, so the size gate passes), and programmatic
        // focus there would steal the keystrokes of the pane the user is
        // actually in - so only a pane that is in the visible tree asks;
        // a recovered hidden pane gains focus from the user's next click,
        // like any pane that loads late.
        if (AutoFocus && IsEffectivelyVisible())
        {
            this.Focus(FocusState.Programmatic);
        }

        // Surface exists and is registered; tell the host the shell has
        // spawned. Raised last on purpose: the startup glow reads the
        // control's bounds right after this, and anything that still has to
        // happen first belongs above this line.
        SurfaceSpawned?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // Surface-creation retry ----------------------------------------------

    /// <summary>
    /// Arm the bounded retry for a surface libghostty could not create.
    /// Ticks until a creation succeeds, the control is disposed, or the
    /// budget runs out; the last one logs and leaves a live pane without a
    /// terminal surface - degraded, never dead.
    /// </summary>
    private void ScheduleSurfaceCreationRetry()
    {
        if (_surfaceRetryTimer is not null) return;
        _surfaceRetryAttemptsLeft = SurfaceRetryMaxAttempts;
        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = SurfaceRetryInterval;
        timer.IsRepeating = true;
        timer.Tick += OnSurfaceRetryTick;
        _surfaceRetryTimer = timer;
        timer.Start();
        Ghostty.Logging.StaticLoggers.App.LogWarning(
            "Terminal surface creation failed (renderer device not ready?); retrying every {IntervalMs} ms for up to {BudgetSeconds} s",
            (int)SurfaceRetryInterval.TotalMilliseconds,
            (int)(SurfaceRetryInterval.TotalMilliseconds * SurfaceRetryMaxAttempts / 1000));
    }

    private void OnSurfaceRetryTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_surfaceDisposed || _surface.Handle != IntPtr.Zero)
        {
            StopSurfaceCreationRetry();
            return;
        }
        // The budget counts attempts, not ticks: an unmeasured pane cannot
        // attempt (the size gate in TryCreateSurface would refuse), so the
        // tick is skipped without spending. A pane the user hid mid-retry
        // keeps its budget for when it is shown again.
        if (SurfacePixelSize.Initial(
                Panel.ActualWidth, Panel.ActualHeight,
                Panel.CompositionScaleX, Panel.CompositionScaleY) is null)
        {
            return;
        }
        if (_surfaceRetryAttemptsLeft-- <= 0)
        {
            StopSurfaceCreationRetry();
            Ghostty.Logging.StaticLoggers.App.LogWarning(
                "Terminal surface creation did not succeed within the retry budget; pane left without a terminal surface");
            return;
        }
        if (TrySettleSurfaceCreation())
        {
            // Stop now rather than letting the timer survive one extra tick
            // to notice the new surface on the next pass.
            StopSurfaceCreationRetry();
        }
    }

    private void StopSurfaceCreationRetry()
    {
        if (_surfaceRetryTimer is not { } timer) return;
        _surfaceRetryTimer = null;
        timer.Stop();
        timer.Tick -= OnSurfaceRetryTick;
    }

    /// <summary>
    /// One creation attempt plus the settle work every success needs,
    /// shared by the first-layout driver and the retry timer.
    /// </summary>
    private bool TrySettleSurfaceCreation()
    {
        if (_surfaceDisposed || !TryCreateSurface()) return false;
        Panel.LayoutUpdated -= OnFirstLayoutUpdated;
        ArmResizeOverlayGrace();
        return true;
    }

    /// <summary>
    /// Whether this control sits in a visible tree: attached to a XamlRoot
    /// with no collapsed element on the path to the root. A detached or
    /// hidden pane must not pull programmatic focus when a retry brings its
    /// surface up (the user's keystrokes belong to the pane they are in).
    /// </summary>
    private bool IsEffectivelyVisible()
    {
        if (XamlRoot is null) return false;
        DependencyObject? node = this;
        while (node is not null)
        {
            if (node is UIElement element && element.Visibility != Visibility.Visible)
                return false;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return true;
    }

    /// <summary>
    /// Free the per-attempt config strings of a failed creation, the same
    /// buffers DisposeSurface frees after a successful one. The next attempt
    /// reallocates them from the still-held snapshot.
    /// </summary>
    private void FreePendingSurfaceConfig()
    {
        if (_workingDirectoryUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(_workingDirectoryUtf8);
        if (_commandUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(_commandUtf8);
        if (_initialInputUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(_initialInputUtf8);
        if (_customShaderUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(_customShaderUtf8);
        _workingDirectoryUtf8 = IntPtr.Zero;
        _commandUtf8 = IntPtr.Zero;
        _initialInputUtf8 = IntPtr.Zero;
        _customShaderUtf8 = IntPtr.Zero;
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
        if (!_surfaceCreated)
        {
            // Nothing to size yet: create the surface at this pass's size,
            // or wait for a pass that gives the panel one. A creation the
            // device refused returns false too and leaves this subscription
            // in place; the retry timer drives the next attempts.
            TrySettleSurfaceCreation();
            return;
        }

        if (_surface.Handle == IntPtr.Zero) return;
        var w = Panel.ActualWidth;
        var h = Panel.ActualHeight;
        if (w <= 0 || h <= 0) return;  // still not settled, wait for next tick
        Panel.LayoutUpdated -= OnFirstLayoutUpdated;

        var sx = Panel.CompositionScaleX > 0 ? Panel.CompositionScaleX : 1f;
        var sy = Panel.CompositionScaleY > 0 ? Panel.CompositionScaleY : 1f;
        NativeMethods.SurfaceSetContentScale(_surface, sx, sy);
        var (pw, ph) = SurfacePixelSize.FromDips(w, h, sx, sy);
        NativeMethods.SurfaceSetSize(_surface, pw, ph);

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

        // Tell the holders of surface-keyed native state first, while nothing
        // has been unwound yet: what they have to hand back is written through
        // this surface pointer, so after SurfaceFree below there is no safe
        // moment left. The latch above is what makes this fire exactly once,
        // and the handle check is what keeps it from firing for a control
        // whose surface was never created -- there is nothing installed on a
        // surface that does not exist, and a subscriber told about one would
        // be comparing its saved pointer against a zero handle.
        if (_surface.Handle != IntPtr.Zero)
        {
            try
            {
                SurfaceDisposing?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // Swallowed on purpose. Everything below this point frees
                // native memory, and PaneHost walks the leaves in a loop, so
                // an escaping exception would strand this surface and skip
                // every leaf after it. A subscriber that failed to clean up
                // after itself is a smaller problem than a teardown that
                // stops half way, so log it and keep tearing down.
                Ghostty.Logging.StaticLoggers.App.LogWarning(
                    "SurfaceDisposing subscriber threw during teardown: {Message}", ex.Message);
            }
        }

        _bellAudio?.Dispose();
        _bellAudio = null;

        Panel.LayoutUpdated -= OnFirstLayoutUpdated;
        StopSurfaceCreationRetry();

        if (_surface.Handle != IntPtr.Zero)
        {
            _imeComposing = false;
            UpdateSurfacePreedit(null);
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
        if (_customShaderUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(_customShaderUtf8);
        _customShaderUtf8 = IntPtr.Zero;

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
        SurfaceSpawned = null;
        BellRang = null;
        BellAcknowledged = null;
        // Already raised above; dropping it here is for a subscriber that did
        // not detach itself in its handler.
        SurfaceDisposing = null;
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

    // Suppress the overlay around a tab-layout switch, for the same reason
    // as the focus guard above: the switch changes the strip column, which
    // resizes every surface in the window, and none of that is a resize the
    // user asked for. Filmed at 30fps the pill sat in the middle of the
    // terminal through the whole transition and stayed after it -- the
    // loudest thing on screen during a motion nobody wants a caption on.
    //
    // Measured from the LAST note rather than the first, and MainWindow
    // notes both the start of a switch and its landing, so this only has to
    // cover the trailing resize that the landing's column collapse causes.
    // Short on purpose: a guard wide enough to span a whole switch from one
    // note would also swallow a real drag-resize that followed it.
    private static readonly TimeSpan ResizeOverlayLayoutSwitchGuard =
        TimeSpan.FromMilliseconds(250);

    // Monotonic, like _lastFocusGainedTick, and 0 means "no switch yet".
    private long _lastLayoutSwitchTick;

    /// <summary>
    /// Told by <see cref="MainWindow"/> that a tab-layout switch is
    /// starting or has just landed. Both ends are notified; see the guard
    /// above for why it is the last one that counts.
    /// </summary>
    internal void NoteLayoutSwitch() => _lastLayoutSwitchTick = Environment.TickCount64;

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
        var withinLayoutSwitchGuard =
            _lastLayoutSwitchTick != 0 &&
            Environment.TickCount64 - _lastLayoutSwitchTick
                < ResizeOverlayLayoutSwitchGuard.TotalMilliseconds;
        var allowShow =
            _resizeOverlayReady && !withinFocusGuard && !withinLayoutSwitchGuard;

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
        // The same formula the surface was created with (SurfacePixelSize),
        // so the first push after creation matches it to the pixel and is a
        // no-op rather than a startup resize.
        var (w, h) = SurfacePixelSize.FromDips(
            Panel.ActualWidth, Panel.ActualHeight,
            Panel.CompositionScaleX, Panel.CompositionScaleY);

        // Fire-and-forget. ghostty_surface_set_size records the desired
        // dimensions in an atomic and wakes the renderer thread twice:
        // the coalescing wakeup drains the mailbox and renders, and the
        // dedicated resize wake forces the frame that applies the
        // resize (a pending resize is itself a redraw reason) while
        // arming a one-shot 8 ms backstop that re-checks it. We never
        // block here, never touch draw_mutex, and never do GPU work on
        // the UI thread.
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

    // Written and read on the UI thread only. UIA looked like a second reader
    // on an RPC thread, but WinUI marshals provider calls onto the UI thread
    // (measured under Narrator and NVDA: every GetSelection landed on the same
    // managed thread as the DispatcherTimer tick), and the toast paths read
    // IsActive from inside DispatcherQueue.TryEnqueue. IsActive also walks
    // XamlRoot, which is thread-affine, so an off-thread caller would fault
    // rather than merely read this stale.
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
        if (mine == 0 || mine != PInvoke.GetForegroundWindow()) return false;
        // Foreground is not enough. Windows does not hand the foreground to
        // anyone else when a window is minimised and nothing else activates, so
        // GetForegroundWindow keeps naming a window the user cannot see -
        // measured: minimise, and it still reports itself foreground several
        // seconds later. A minimised surface is not one the user is looking at,
        // which is the whole claim this property makes.
        return !PInvoke.IsIconic(new Windows.Win32.Foundation.HWND(mine));
    }

    // Stable per-surface key for the toast Group (dedupe + focus-regain
    // clear). A fresh Guid rather than the native surface handle, so a
    // recycled handle value can never alias another surface's toasts. The
    // same control instance is resolved for both Show and ClearForSurface,
    // so the key stays consistent for the surface's lifetime.
    private readonly string _toastSurfaceKey = Guid.NewGuid().ToString();
    internal string ToastSurfaceKey => _toastSurfaceKey;

    /// <summary>
    /// The command text this surface was created to run, exactly as
    /// <see cref="TryCreateSurface"/> handed it to libghostty (an argv keeps
    /// its <c>direct:</c> marker); null when the surface runs the default
    /// shell. The child-exited toast reads it to name what ran
    /// (deblasis/wintty#1193); the policy decides how much of it is safe to
    /// show, so nothing here redacts or truncates.
    /// </summary>
    internal string? SurfaceCommandText => _surfaceCommandText;

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        // Stamp the focus instant so a focus-driven relayout in the next
        // ~500 ms does not flash the resize overlay (matches macOS).
        _lastFocusGainedTick = Environment.TickCount64;
        SetFocusState(true);
        AcknowledgeBell();
        // Route IME to the hidden TextBox sink (see TerminalControl.xaml).
        if (!SearchBar.ContainsFocus)
            ImeSink.Focus(FocusState.Programmatic);
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        ClearImeComposition();
        SetFocusState(false);
    }

    private void SetFocusState(bool focused)
    {
        if (!focused)
            _pendingWmCharHigh = '\0';

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

    // True for the SurfaceRoot copy of an event that started somewhere
    // below it -- on the panel, or on a sibling overlay that owns it. See
    // the dual-attachment rationale in the XAML.
    //
    // Source-based rather than Handled-based on purpose, so it holds for
    // the handlers that return early without marking the event (the
    // motion threshold in OnPointerMoved, the null-surface guards).
    //
    // Events that survive it came from the gutter and carry Panel-relative
    // coordinates just outside the surface. That is intended: libghostty
    // clamps them to the edge cell, which is what makes a click on a
    // pane's edge behave as a click on its outermost cell. It does mean
    // an app running mouse=a sees button reports attributed to the edge
    // cell for clicks a few DIPs outside the grid.
    private bool BubbledFromChild(object sender, RoutedEventArgs e)
        => ReferenceEquals(sender, SurfaceRoot)
           && !ReferenceEquals(e.OriginalSource, SurfaceRoot);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (BubbledFromChild(sender, e)) return;

        // Any press on the surface is the user touching this pane:
        // selection drags and scroll-to-read both count, so the idle
        // clock restarts here rather than waiting for a keystroke.
        NoteActivity();

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

        // Right-click: if the running program has captured the mouse (vim,
        // tmux mouse mode), forward the button so the program handles it.
        // Otherwise suppress forwarding and open our context menu on release.
        if (props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed
            && BeginRightClickMenu())
        {
            e.Handled = true;
            return;
        }

        SendMouseButton(e, GhosttyMouseState.Press);
    }

    /// <summary>
    /// The right-press half of the context-menu gesture: arm the menu unless
    /// the running program has captured the mouse, in which case the button
    /// belongs to it and false sends it on.
    /// </summary>
    private bool BeginRightClickMenu()
    {
        if (_surface.Handle == IntPtr.Zero || NativeMethods.SurfaceMouseCaptured(_surface))
            return false;
        _rightButtonOpensMenu = true;
        return true;
    }

    /// <summary>
    /// The right-release half: open the menu at <paramref name="position"/>
    /// if the press armed it, consuming the flag. False when nothing was
    /// armed, so the release goes to libghostty as usual.
    /// </summary>
    private bool CompleteRightClickMenu(Windows.Foundation.Point position)
    {
        if (!_rightButtonOpensMenu) return false;
        _rightButtonOpensMenu = false;
        ContextMenuRequested?.Invoke(this, position);
        return true;
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

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (BubbledFromChild(sender, e)) return;

        // Complete the suppressed right-click on its matching right-release,
        // opening the menu so it feels like a normal Windows right-click. We
        // consume the flag ONLY on the right-release: an unrelated release (or
        // a right-release that never arrives because the pointer left the
        // panel) must not clear it early and forward an orphan button to
        // libghostty. A stale flag self-heals on the next right-press.
        if (e.GetCurrentPoint(Panel).Properties.PointerUpdateKind
               == PointerUpdateKind.RightButtonReleased
            && CompleteRightClickMenu(e.GetCurrentPoint(this).Position))
        {
            e.Handled = true;
            return;
        }

        SendMouseButton(e, GhosttyMouseState.Release);
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (BubbledFromChild(sender, args)) return;

        // ContextRequested fires for both right-click and keyboard. The
        // pointer path (OnPointerPressed/Released) already handles right-click
        // with the mouse-capture gate, so here we only act on keyboard
        // invocation (Shift+F10 / Menu key), where TryGetPosition is false.
        // For the mouse case we mark it handled so WinUI does not show an
        // empty default menu, then defer to the pointer path.
        args.Handled = true;
        if (!args.TryGetPosition(this, out _))
            ContextMenuRequested?.Invoke(this, null);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (BubbledFromChild(sender, e)) return;
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
        if (BubbledFromChild(sender, e)) return;
        if (_surface.Handle == IntPtr.Zero) return;
        // Scrolling to read is touching the pane; same idle-stamp
        // contract as a pointer press.
        NoteActivity();
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
        // First, before any gate: this key's own character follows it, and
        // an arm left over from a consumed close elsewhere must not be the
        // thing that decides that character's fate.
        RetireStaleCharacterSuppress();

        if (CommandPaletteIsOpen) return;

        // Suppress forwarding while the in-pane search bar owns keyboard
        // focus: its controls are children of this visual tree, so their
        // KeyDown bubbles up here and would otherwise also run in the
        // shell. Gate on live focus rather than the bar's open state so
        // typing keeps reaching the terminal when the bar is left visible
        // after the user clicks back into the surface.
        if (SearchBar.ContainsFocus) return;

        // A preview surface is a fake session (the shader picker's DOS
        // demo): the whole keyboard belongs to that session and nothing
        // belongs to the pty. The return is keyed on the preview flag
        // alone, not on the sink, so a future preview surface that sets
        // no sink still cannot fire pane chords into the real surface.
        // Pane concerns below (chord matching, key stamping, bell
        // acknowledgment) never fire from inside a preview; a
        // WindowsOnly chord dispatching a pane action there would be a
        // bug, not a feature.
        if (_isPreviewSurface)
        {
            if (PreviewInputSink is { } sink)
            {
                // Every key press re-arms the quiet window, mapped or
                // not: the site stamps its idle clock on every keydown,
                // so holding Left or Right pauses the demo too, not
                // just keys the fake shell consumes.
                sink.NoteKeyDown();
                var mods = CurrentChordModifiers();
                if (PreviewKeyMap.TryMap(
                        (int)e.Key,
                        mods.HasFlag(Windows.System.VirtualKeyModifiers.Control),
                        mods.HasFlag(Windows.System.VirtualKeyModifiers.Shift),
                        mods.HasFlag(Windows.System.VirtualKeyModifiers.Menu),
                        out var shellKey) &&
                    sink.KeyDown(shellKey))
                {
                    e.Handled = true;
                }
            }
            return;
        }

        // Stamp the shared host so VerticalTabHost's hover-expand
        // suppression knows the user is mid-typing and holds back
        // the sidebar pop-open. Unconditional: we want every key
        // (including chords and IME composition keys) to count. The
        // same keydown also stamps this surface's idle clock.
        Host?.NoteKeystroke();
        NoteActivity();

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
        // Mirror OnKeyDown's palette gate: between the popup opening and
        // focus reaching its search box, releases still land here, and a
        // release for a press the gate dropped hands libghostty half a pair.
        if (CommandPaletteIsOpen) return;

        // Mirror OnKeyDown: while the search bar owns focus, swallow the
        // key-up too so libghostty never sees a release for a press it
        // never received.
        if (SearchBar.ContainsFocus) return;

        // The press never went to libghostty (the preview branch of
        // OnKeyDown returns before SendKey, sink or not); forwarding
        // this release would hand libghostty a release for a press it
        // never saw. Swallow every key-up on a preview surface.
        if (_isPreviewSurface) return;

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

    // Internal so the window's frame-chord router reads the modifier state
    // the same way the pane path does, rather than growing a second copy.
    internal static Windows.System.VirtualKeyModifiers CurrentChordModifiers()
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
        // There is no single VK for the Windows key, so both sides are
        // read. Without this the flag is never set and every consumer
        // sees Win+Ctrl+T as plain Ctrl+T: the frame router fired
        // new_tab on a chord the user aimed somewhere else, and a
        // `super+...` binding could never match because the modifier it
        // needs was not in the set. Match compares modifiers exactly, so
        // reporting the key also stops a Win chord from reaching a
        // binding that did not ask for it.
        if (((Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows)
                | Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.RightWindows))
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
            mods |= Windows.System.VirtualKeyModifiers.Windows;
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
            Composing = (byte)(_imeComposing ? 1 : 0),
        };
        var handled = NativeMethods.SurfaceKey(_surface, key);
        if (handled) e.Handled = true;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_surface.Handle == IntPtr.Zero) return;

        // Characters keep arriving while the command palette is open: between
        // the popup opening and focus reaching its search box this surface
        // still holds focus, and CharacterReceived is raised independently of
        // KeyDown, so OnKeyDown's palette gate alone leaves this path open.
        // The trailing character of the key that CLOSES the palette is not
        // this gate's job: the arm below is what drops it.
        if (CommandPaletteIsOpen) return;

        // WM_CHAR from the focused search bar bubbles up here too. Drop it
        // so typed characters edit the needle only and never reach
        // libghostty as terminal text. See the matching guard in OnKeyDown.
        if (SearchBar.ContainsFocus) return;

        // During IME composition, partial code units arrive as
        // CharacterReceived on some layouts. Preedit is driven by
        // TextCompositionChanged; drop stray chars until commit.
        if (_imeComposing) return;

        HandleCharacter(e.Character);
    }

    /// <summary>
    /// The character half of <see cref="OnCharacterReceived"/> below its
    /// routed guards: the consumed-close arm, the bound-chord suppress, then
    /// the preview sink or the forward to the surface. Split out so the test
    /// seam drives the same decision a real WM_CHAR takes past those guards
    /// (a CharacterReceivedRoutedEventArgs cannot be constructed in process).
    /// Returns true when the consumed-close arm dropped the character.
    /// </summary>
    private bool HandleCharacter(char ch)
    {
        // Two one-shot drops, decided together so neither can outlive the
        // character that should spend it:
        // - the consumed-close arm: the trailing character of a key another
        //   surface consumed to close itself (only the armed key's own
        //   character is dropped, so a stale arm never eats typing);
        // - the bound-chord suppress: if the matching OnKeyDown
        //   short-circuited a bound chord, the WM_CHAR that follows. WinUI 3
        //   raises CharacterReceived independently of KeyDown handling, so
        //   without it the C0 control char (e.g. U+0005 for Ctrl+E) reaches
        //   libghostty as text and the shell reads it as a readline command.
        var fate = ConsumedCloseArm.Decide(ref _consumedCloseArm, ref _suppressNextCharacter, ch);
        if (fate != CharacterFate.Forward) return fate == CharacterFate.DroppedByArm;

        // WM_CHAR goes to the fake session as typed text. Control units
        // never type into the fake line: their keys (Enter, Backspace,
        // Ctrl+C) already arrived through KeyDown, and delivering the
        // character too would double-execute them. DEL joins them because
        // it is not text. This also sidesteps depending on which of the
        // two events a given control key raises: the character path is
        // printable-only, full stop. Like the KeyDown branch, the return
        // is keyed on the preview flag alone: a preview surface without
        // a sink drops the character, it never forwards it to the pty.
        if (_isPreviewSurface)
        {
            if (PreviewInputSink is { } sink &&
                ch is >= (char)0x20 and not (char)0x7f)
            {
                sink.Character(ch);
            }
            return false;
        }

        // Forward WM_CHAR unchanged, but assemble surrogate pairs first.
        // WinUI 3 raises CharacterReceived once per UTF-16 unit; encoding
        // each unit with new Rune(ch) throws on D800–DFFF and kills the
        // UI thread. C0 filtering stays in libghostty (see above).
        Span<byte> buf = stackalloc byte[4];
        if (!WmCharUtf8.TryEncode(ch, ref _pendingWmCharHigh, buf, out var len))
            return false;
        unsafe
        {
            fixed (byte* p = buf)
            {
                NativeMethods.SurfaceText(_surface, (IntPtr)p, (UIntPtr)len);
            }
        }
        return false;
    }

    // IME composition (ImeSink TextBox, wired in XAML) -----------------

    private void OnImeCompositionStarted(object sender, TextCompositionStartedEventArgs e)
    {
        // Retire before anything else, for the reason OnKeyDown does: a
        // commit's characters arrive after TextCompositionEnded, and TSF
        // swallows the confirming key's KeyDown, so OnKeyDown never runs to
        // retire for them. Unconditional: an arm standing when any
        // composition starts belongs to a keystroke that went elsewhere.
        RetireStaleCharacterSuppress();
        if (_surface.Handle == IntPtr.Zero || SearchBar.ContainsFocus) return;
        _imeComposing = true;
    }

    private void OnImeCompositionChanged(object sender, TextCompositionChangedEventArgs e)
    {
        if (_surface.Handle == IntPtr.Zero || SearchBar.ContainsFocus) return;
        var text = ImeSink.Text;
        if (e.StartIndex >= 0 && e.Length > 0 && e.StartIndex + e.Length <= text.Length)
            UpdateSurfacePreedit(text.Substring(e.StartIndex, e.Length));
        else
            UpdateSurfacePreedit(text);
    }

    private void OnImeCompositionEnded(object sender, TextCompositionEndedEventArgs e)
    {
        if (_surface.Handle == IntPtr.Zero) return;
        ClearImeComposition();
    }

    /// <summary>Clears WinUI IME state and the libghostty preedit overlay.</summary>
    private void ClearImeComposition()
    {
        _imeComposing = false;
        if (_surface.Handle == IntPtr.Zero) return;
        UpdateSurfacePreedit(null);
        ImeSink.Text = string.Empty;
    }

    private void OnImeSinkTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_imeComposing || _surface.Handle == IntPtr.Zero || SearchBar.ContainsFocus) return;
        var text = ImeSink.Text;
        if (string.IsNullOrEmpty(text)) return;

        // A preview surface's keyboard belongs to its fake session, so
        // committed IME text goes through the sink like any other
        // printable (same filtering as OnCharacterReceived), never to
        // the sleeping placeholder child through SurfaceText.
        if (_isPreviewSurface)
        {
            if (PreviewInputSink is { } sink)
            {
                foreach (var ch in text)
                {
                    if (ch is >= (char)0x20 and not (char)0x7f)
                        sink.Character(ch);
                }
            }
            ImeSink.Text = string.Empty;
            return;
        }

        Span<byte> buf = stackalloc byte[4];
        foreach (var ch in text)
        {
            if (!WmCharUtf8.TryEncode(ch, ref _pendingWmCharHigh, buf, out var len))
                continue;
            unsafe
            {
                fixed (byte* p = buf)
                    NativeMethods.SurfaceText(_surface, (IntPtr)p, (UIntPtr)len);
            }
        }

        ImeSink.Text = string.Empty;
    }

    private void UpdateSurfacePreedit(string? text)
    {
        if (_surface.Handle == IntPtr.Zero) return;

        // A preview surface has no preedit story: its session is canned,
        // nothing behind it reads a composition, and drawing one on the
        // real grid would put IME state where it cannot be committed.
        // Suppress entirely; the matching null clear below is then a
        // no-op, which is exactly right since we never set one.
        if (_isPreviewSurface) return;

        if (string.IsNullOrEmpty(text))
        {
            NativeMethods.SurfacePreedit(_surface, IntPtr.Zero, UIntPtr.Zero);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        unsafe
        {
            fixed (byte* p = bytes)
                NativeMethods.SurfacePreedit(_surface, (IntPtr)p, (UIntPtr)bytes.Length);
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
    /// this leaf, and from the command palette. Repeated calls on an
    /// already-open bar just re-focus the needle and leave the running
    /// search, and the user's place in it, alone.
    /// </summary>
    internal void OpenSearch()
    {
        // Drop any in-flight IME preedit before the needle box takes focus.
        ClearImeComposition();
        var wasOpen = SearchBar.State.IsOpen;
        SearchBar.State.IsOpen = true;
        SearchBar.Visibility = Visibility.Visible;
        SearchBar.FocusNeedle();

        // Closing the bar ended the search inside libghostty while leaving
        // the needle text in place, so a reopen has to start it again.
        // Only on the closed -> open transition: re-issuing onto a live
        // search happens to be inert today because libghostty ignores an
        // unchanged needle, but that is its implementation detail, not a
        // contract this side should lean on.
        if (!wasOpen) SearchBar.ReissueSearch();
    }

    private void OnSearchClosed(object sender, EventArgs e)
    {
        SearchBar.Visibility = Visibility.Collapsed;
        SearchBar.State.IsOpen = false;
        SearchBar.State.MarkInactive();
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
        // libghostty's start_search reports an empty needle, and adopting it
        // would clear the box, push the empty string through the two-way
        // bind and cancel the search a debounce later. macOS and GTK guard
        // this the same way. Unreachable while the apprt consumes
        // Ctrl+Shift+F itself, but the guard is what makes that safe to
        // change.
        if (needle.Length == 0) return;
        SearchBar.State.Needle = needle;
    }

    internal void OnSearchEnded()
    {
        // No MarkInactive here: the teardown already pushes a null total,
        // which SearchState renders as no active search, so this would be
        // redundant. It is also the wrong place to invalidate from -- this
        // arrives through the dispatcher, and the ordering that keeps a
        // late delivery from landing on a reopened bar is libghostty
        // performing the binding action inline, not anything enforced here.
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
