using System;
using System.Runtime.InteropServices;
using Ghostty.Controls;
using Ghostty.Core.Panes;
using Ghostty.Core.Tabs;
using Ghostty.Core.Taskbar;
using Ghostty.Taskbar;
using Ghostty.Hosting;
using Ghostty.Input;
using Ghostty.Panes;
using Ghostty.Tabs;
using Microsoft.UI.Xaml.Controls;
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
    private readonly PaneHostFactory _factory;
    private readonly TabManager _tabManager;
    private TaskbarList3Facade? _taskbarFacade;
    private TaskbarProgressCoordinator? _taskbarCoordinator;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _taskbarTickTimer;
    private readonly ITabHost _tabHost;
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
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop();

        // Extend content into the title bar: remove the system-drawn
        // title bar chrome and let TabHost's TabView strip render
        // where the title bar used to be. The caption buttons
        // (min / max / close) are still drawn by the system on the
        // right; TabView's TabStripFooter reserves room for them.
        // Must be set before the TabHost is parented so the content
        // area is sized without the default title bar row.
        ExtendsContentIntoTitleBar = true;

        _factory = new PaneHostFactory(_host);
        _tabManager = new TabManager(() => _factory.Create());
        _tabHost = CreateTabHost(_tabManager);
        RootGrid.Children.Add(_tabHost.HostElement);

        // Declare the drag region AFTER _tabHost is in the visual
        // tree so DragRegion (the TabStripFooter Grid) is live.
        // Clicking anywhere in that Grid drags the window; the tabs
        // themselves are hit-test targets and remain interactive.
        SetTitleBar(_tabHost.DragRegion as FrameworkElement);

        InstallPaneAccelerators();

        _tabManager.ActiveTabChanged += (_, _) => HookActiveTabTitle();
        _tabManager.WindowTitleChanged += (_, _) => Title = _tabManager.ActiveTab.EffectiveTitle;
        HookActiveTabTitle();

        _tabManager.LastTabClosed += (_, _) => Close();

        // Taskbar progress: wire a TaskbarProgressCoordinator through
        // a real ITaskbarList3 facade, driven by a 2s DispatcherQueueTimer.
        //
        // Narrow try/catch on COMException only: if CoCreateInstance /
        // HrInit genuinely fails (explorer down, session 0, etc.) the
        // indicator is a nice-to-have and we keep the window alive.
        // Any other exception (NRE, OOM, arg errors) is a real bug and
        // we let it propagate so it is visible in debug and telemetry.
        try
        {
            // Reuse the HWND resolved for the class-brush fix above.
            var facade = new TaskbarList3Facade(hwnd);
            var coord = new TaskbarProgressCoordinator(
                _tabManager,
                facade,
                () => DateTime.UtcNow);
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.IsRepeating = true;
            // Capture the coordinator locally rather than dereferencing
            // the field via `!`, so a partially-torn-down state cannot
            // NRE here.
            timer.Tick += (_, _) => coord.Tick();
            timer.Start();

            _taskbarFacade = facade;
            _taskbarCoordinator = coord;
            _taskbarTickTimer = timer;

            // Pause cycling when minimized so the taskbar does not
            // churn while the window is hidden. AppWindow.Changed
            // fires on presenter state transitions. Store the handler
            // so Closed can detach it.
            _appWindowChanged = (_, _) =>
            {
                if (this.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
                {
                    if (op.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                        coord.Pause();
                    else
                        coord.Resume();
                }
            };
            this.AppWindow.Changed += _appWindowChanged;
        }
        catch (COMException ex)
        {
            // Log loudly in debug; in release the taskbar indicator is
            // simply absent for this window's lifetime.
            System.Diagnostics.Debug.WriteLine(
                $"Taskbar progress wiring failed: 0x{ex.HResult:X8} {ex.Message}");
            System.Diagnostics.Debug.Fail("Taskbar progress wiring failed", ex.ToString());
        }

        Closed += (_, _) =>
        {
            // Tear down the taskbar chain before disposing the host.
            // Order: stop timer, detach window state handler, dispose
            // coordinator (unhooks TabManager), dispose facade (releases
            // the ITaskbarList3 RCW).
            _taskbarTickTimer?.Stop();
            if (_appWindowChanged is not null)
            {
                this.AppWindow.Changed -= _appWindowChanged;
                _appWindowChanged = null;
            }
            _taskbarCoordinator?.Dispose();
            _taskbarCoordinator = null;
            _taskbarFacade?.Dispose();
            _taskbarFacade = null;

            // Surface lifetime is decoupled from Loaded/Unloaded
            // (see TerminalControl.DisposeSurface), so we have to
            // free every leaf in every tab explicitly before tearing
            // down the libghostty host.
            foreach (var t in _tabManager.Tabs) t.PaneHost.DisposeAllLeaves();
            _host.Dispose();
        };
    }

    /// <summary>
    /// Pick which <see cref="ITabHost"/> implementation to use based
    /// on the stubbed <c>vertical-tabs</c> config flag. Horizontal
    /// (<see cref="TabHost"/>) is the default; vertical
    /// (<see cref="VerticalTabHost"/>) opts in.
    /// </summary>
    private ITabHost CreateTabHost(TabManager manager)
    {
        // TODO(config): vertical-tabs (bool, default false)
        const bool verticalTabs = false;
        return verticalTabs
            ? (ITabHost)new VerticalTabHost(manager, _host)
            : new TabHost(manager);
    }

    /// <summary>
    /// Subscribe the active tab's active leaf to live title-change
    /// updates and write the title into <see cref="TabModel.ShellReportedTitle"/>.
    /// Re-runs every time the active tab changes or the active leaf
    /// within the active tab changes. The leaf-title hook lives here
    /// (not in <see cref="TabManager"/>) because it touches WinUI
    /// types that Ghostty.Core cannot reach.
    /// </summary>
    private void HookActiveTabTitle()
    {
        if (_activeLeaf is { } previous)
            previous.Terminal().TitleChanged -= OnLiveTitleChanged;

        var tab = _tabManager.ActiveTab;
        var leaf = tab.PaneHost.ActiveLeaf;
        _activeLeaf = leaf;
        leaf.Terminal().TitleChanged += OnLiveTitleChanged;
        tab.ShellReportedTitle = leaf.Terminal().CurrentTitle;
        Title = tab.EffectiveTitle;

        tab.PaneHost.LeafFocused -= OnActiveTabLeafFocused;
        tab.PaneHost.LeafFocused += OnActiveTabLeafFocused;
    }

    private void OnActiveTabLeafFocused(object? sender, LeafPane leaf)
    {
        if (_activeLeaf is { } previous)
            previous.Terminal().TitleChanged -= OnLiveTitleChanged;
        _activeLeaf = leaf;
        leaf.Terminal().TitleChanged += OnLiveTitleChanged;
        _tabManager.ActiveTab.ShellReportedTitle = leaf.Terminal().CurrentTitle;
    }

    private void OnLiveTitleChanged(object? sender, string title)
    {
        // TabManager raises WindowTitleChanged in response, which
        // sets Title via the constructor's subscription.
        _tabManager.ActiveTab.ShellReportedTitle = title;
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
            var captured = binding;
            var accel = new KeyboardAccelerator
            {
                Modifiers = captured.Modifiers,
                Key = captured.Key,
                // Pin the accelerator scope to _tabHost. Without this,
                // WinUI 3 dispatches the same accelerator twice for a
                // single key event (once from the focused element's
                // search up the tree, once from the host's search
                // down), and Split runs twice per Ctrl+Shift+D.
                ScopeOwner = _tabHost.HostElement,
            };
            accel.Invoked += (_, args) =>
            {
                args.Handled = true;
                // WinUI 3 fires Invoked twice per key event for an
                // accelerator on a parent of the focused element even
                // with Handled set and ScopeOwner pinned. Swallow the
                // second dispatch inside the same physical keypress
                // by remembering which action just fired; KeyUp on
                // the host clears the flag so the next KeyDown
                // dispatches normally.
                // See https://github.com/deblasis/ghostty/issues/165.
                if (_acceleratorFiredThisKeyDown == captured.Action) return;
                _acceleratorFiredThisKeyDown = captured.Action;
                PaneActionRouter.Invoke(
                    captured.Action,
                    _tabManager,
                    onTabCloseRequested: OnTabCloseRequestedFromKeyboard,
                    onToggleVerticalTabsPinned: OnToggleVerticalTabsPinnedFromKeyboard);
            };
            _tabHost.HostElement.KeyboardAccelerators.Add(accel);
        }

        _tabHost.HostElement.KeyUp += (_, _) => _acceleratorFiredThisKeyDown = null;
        _tabHost.HostElement.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    // Route a keyboard-driven full-tab close through the shared
    // RequestCloseTabAsync path so the confirmation dialog is the
    // same code path as the per-tab X button and the context-menu
    // Close item.
    //
    // async void is unavoidable here: this is invoked from a
    // synchronous Action<TabManager> boundary inside the router. An
    // unhandled exception from an async void method tears down the
    // whole process, so we log and surface — but we do NOT swallow
    // silently: Debug.Fail in debug builds makes bugs noisy, and the
    // release path still logs to the debug stream for post-mortem.
    private async void OnTabCloseRequestedFromKeyboard(TabManager mgr)
    {
        try
        {
            if (!ReferenceEquals(mgr, _tabManager)) return;
            await _tabHost.RequestCloseTabAsync(_tabManager.ActiveTab);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MainWindow] OnTabCloseRequestedFromKeyboard failed: {ex}");
            System.Diagnostics.Debug.Fail("Tab close from keyboard threw", ex.ToString());
        }
    }

    // Vertical-tabs pinned toggle via Ctrl+Shift+Space. No-op
    // when the layout is horizontal (TabHost) — the chord is
    // registered globally but only VerticalTabHost responds.
    private void OnToggleVerticalTabsPinnedFromKeyboard(TabManager mgr)
    {
        if (!ReferenceEquals(mgr, _tabManager)) return;
        if (_tabHost is VerticalTabHost vth)
            vth.TogglePinnedFromKeyboard();
    }
}
