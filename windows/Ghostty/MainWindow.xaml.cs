using System;
using System.Runtime.InteropServices;
using Ghostty.Controls;
using Ghostty.Hosting;
using Ghostty.Input;
using Ghostty.Panes;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinRT.Interop;

namespace Ghostty;

public sealed partial class MainWindow : Window
{
    private readonly GhosttyHost _host;
    private readonly PaneHost _paneHost;
    private LeafPane? _activeLeaf;

    // Dedup guard for KeyboardAccelerator double-dispatch. WinUI 3
    // fires accelerator Invoked twice for a single key event when the
    // accelerator is registered on a parent of the focused element,
    // even with args.Handled = true and ScopeOwner explicitly set.
    //
    // Workaround: remember which action just fired and swallow any
    // subsequent Invoked for the same action until the next KeyUp
    // resets the flag. Framework dupes arrive inside the same physical
    // keypress (no KeyUp between them) so they are filtered, while
    // legitimate user repeats (KeyDown -> KeyUp -> KeyDown) come
    // through unmodified. This replaces an earlier 150 ms wall-clock
    // window that ate muscle-memory double-splits.
    //
    // Tracked in https://github.com/deblasis/ghostty/issues/165
    private PaneAction? _acceleratorFiredThisKeyDown;

    // Win32 interop for the window class background brush. WinUI 3 hosts
    // the XAML island inside a Win32 HWND whose WNDCLASS hbrBackground
    // defaults to white. During an interactive drag-resize, DWM paints
    // any uncovered window pixels with that brush BEFORE WinUI 3 gets a
    // chance to extend its XAML content into the new area, producing a
    // visible white flash at the leading edge of the drag. Replacing the
    // class brush with a dark solid brush makes the flash invisible
    // against any dark color scheme.
    private const int GCLP_HBRBACKGROUND = -10;

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    public MainWindow()
    {
        InitializeComponent();

        _host = new GhosttyHost(DispatcherQueue);

        // Match the RootGrid background (#0C0C0C). Win32 COLORREF is 0x00BBGGRR.
        var hwnd = WindowNative.GetWindowHandle(this);
        var brush = CreateSolidBrush(0x000C0C0Cu);
        SetClassLongPtr(hwnd, GCLP_HBRBACKGROUND, brush);

        // Mica is only available on Windows 11 22H1+ with a supported GPU.
        // Our TargetPlatformMinVersion is still 19041 (Windows 10), so a
        // bare `new MicaBackdrop()` would silently fail on older systems
        // and leave the window with a transparent black backdrop. Probe
        // MicaController.IsSupported() and skip the backdrop on unsupported
        // hosts; XAML's default Window background takes over.
        //
        // Custom title bar with ExtendsContentIntoTitleBar comes in a
        // follow-up PR so we can focus on the terminal plumbing here.
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop();

        _paneHost = new PaneHost(_host, terminalFactory: () => new TerminalControl());
        RootGrid.Children.Add(_paneHost);
        InstallPaneAccelerators();

        _paneHost.LeafFocused += OnLeafFocused;
        _paneHost.LastLeafClosed += (_, _) => Close();

        Closed += (_, _) =>
        {
            // Surface lifetime is decoupled from Loaded/Unloaded
            // (see TerminalControl.DisposeSurface), so we have to
            // free all leaves explicitly before tearing down the host.
            _paneHost.DisposeAllLeaves();
            _host.Dispose();
        };
    }

    /// <summary>
    /// Re-route the window title when the active pane changes. We
    /// unsubscribe the previous active leaf's TitleChanged so a
    /// background pane setting its title via OSC does not affect the
    /// window chrome.
    /// </summary>
    private void OnLeafFocused(object? sender, LeafPane leaf)
    {
        if (_activeLeaf is { } previous)
            previous.Terminal.TitleChanged -= OnActiveLeafTitleChanged;
        _activeLeaf = leaf;
        leaf.Terminal.TitleChanged += OnActiveLeafTitleChanged;
        Title = leaf.Terminal.CurrentTitle ?? "Ghostty";
    }

    private void OnActiveLeafTitleChanged(object? sender, string title)
    {
        Title = title;
    }

    /// <summary>
    /// Install one <see cref="KeyboardAccelerator"/> per binding from
    /// <see cref="KeyBindings.Default"/>. Each accelerator dispatches
    /// through <see cref="PaneActionRouter.Invoke"/>, so adding a new
    /// pane chord is one line in <see cref="KeyBindings.Default"/> and
    /// one case in <see cref="PaneActionRouter.Invoke"/>.
    ///
    /// Why KeyboardAccelerators rather than KeyDown / PreviewKeyDown:
    /// WinUI 3 routed key events ALL bubble (despite the "Preview"
    /// naming inherited from WPF, PreviewKeyDown does not tunnel). The
    /// focused TerminalControl receives KeyDown first, and would
    /// forward the chord to libghostty if we did nothing. Accelerators
    /// fire AFTER routed key events but BEFORE the framework gives up,
    /// AND only when the focused element has not marked the event
    /// handled - so TerminalControl actively short-circuits known
    /// chords (it asks the same KeyBindings registry) to let the
    /// accelerator fire.
    ///
    /// Bindings live in <see cref="KeyBindings.Default"/>; a future PR
    /// will replace that with a config-driven loader and nothing here
    /// has to change.
    /// </summary>
    private void InstallPaneAccelerators()
    {
        foreach (var binding in KeyBindings.Default.All)
        {
            var accel = new KeyboardAccelerator
            {
                Modifiers = binding.Modifiers,
                Key = binding.Key,
                // Pin the accelerator scope to _paneHost. Without this,
                // WinUI 3 dispatches the same accelerator twice for
                // a single key event (once from the focused element's
                // search up the tree, once from the host search down),
                // and Split runs twice per Ctrl+Shift+D.
                ScopeOwner = _paneHost,
            };
            accel.Invoked += (_, args) =>
            {
                args.Handled = true;
                // WinUI 3 fires Invoked twice per key event for an
                // accelerator on a parent of the focused element, even
                // with args.Handled = true and ScopeOwner set. Swallow
                // the second dispatch inside the same physical keypress
                // by remembering which action just fired; KeyUp on the
                // host clears the flag, so the next KeyDown dispatches
                // normally. See https://github.com/deblasis/ghostty/issues/165.
                if (_acceleratorFiredThisKeyDown == binding.Action) return;
                _acceleratorFiredThisKeyDown = binding.Action;
                PaneActionRouter.Invoke(binding.Action, _paneHost);
            };
            _paneHost.KeyboardAccelerators.Add(accel);
        }

        // Reset the per-keydown dedup flag on KeyUp so a user's rapid
        // second Ctrl+Shift+D is not eaten as a framework dupe. Handled
        // on the host (not the focused terminal) because KeyUp routes
        // up the tree and the host is always on the path.
        _paneHost.KeyUp += (_, _) => _acceleratorFiredThisKeyDown = null;

        // Suppress the auto-generated "Ctrl+Shift+D" tooltip that
        // KeyboardAccelerators normally show on hover. PaneHost fills
        // the window content area, so the tooltip would float over the
        // terminal grid for any chord we register. Set once on the
        // host since the placement mode is per-element, not
        // per-accelerator.
        _paneHost.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }
}
