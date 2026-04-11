using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Ghostty.Commands;
using Ghostty.Controls;
using Ghostty.Core.Tabs;
using Ghostty.Dialogs;
using Ghostty.Hosting;
using Ghostty.Input;
using Ghostty.Interop;
using Ghostty.Core.Panes;
using Ghostty.Panes;
using Ghostty.Settings;
using Ghostty.Shell;
using Ghostty.Tabs;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace Ghostty;

/// <summary>
/// Composition root for the WinUI 3 shell. MainWindow holds the
/// XAML structural elements (declared in MainWindow.xaml), wires
/// the libghostty host, the tab manager, the two tab hosts, the
/// pane action router, and three coordinators that own the rest
/// of the cross-cutting plumbing:
///
///   - <see cref="LayoutCoordinator"/> handles the runtime switch
///     between horizontal and vertical layouts (cross-fade
///     Storyboard + strip-column tween + concurrent-tween guard).
///   - <see cref="TitleBarCoordinator"/> owns the title bar
///     drag-region selection, the caption-inset DPI sync, the
///     active-leaf TitleChanged hook, and the vertical-mode title
///     TextBlock binding.
///   - <see cref="TaskbarHost"/> wires the Ghostty.Core taskbar
///     progress coordinator into ITaskbarList3.
///
/// MainWindow itself only owns construction order, the dialog
/// tracker, the keyboard accelerator install, and the Win32 class
/// brush hack for resize flicker.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly GhosttyHost _host;
    private readonly PaneHostFactory _factory;
    private readonly TabManager _tabManager;
    private readonly PaneActionRouter _router;
    private readonly DialogTracker _dialogs = new();
    private readonly UiSettings _uiSettings;

    private readonly TabHost _horizontalTabHost;
    private readonly VerticalTabHost _verticalTabHost;
    private ITabHost _tabHost;

    private readonly LayoutCoordinator _layout;
    private readonly TitleBarCoordinator _titleBar;
    private readonly TaskbarHost _taskbar;

    private CommandPaletteViewModel? _commandPaletteVm;
    private FrecencyStore? _frecencyStore;
    private Controls.TerminalControl? _previousFocusSurface;

    /// <summary>
    /// Palette close state: prevents re-entrant close handling between
    /// ViewModel.PropertyChanged and Popup.Closed callbacks.
    /// </summary>
    private enum PaletteCloseState { Idle, ClosingFromCommand, ClosingFromToggle }
    private PaletteCloseState _paletteCloseState;

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

    [LibraryImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static partial IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateSolidBrush(uint crColor);

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
        // where the title bar used to be. Must be set before the
        // TabHost is parented so the content area is sized without
        // the default title bar row.
        ExtendsContentIntoTitleBar = true;

        _factory = new PaneHostFactory(_host);
        _tabManager = new TabManager(() => _factory.Create());
        _router = new PaneActionRouter(_tabManager);
        _uiSettings = UiSettings.Load();

        _horizontalTabHost = new TabHost(_tabManager, _router, _dialogs);
        _verticalTabHost = new VerticalTabHost(_tabManager, _router, _dialogs, _host);

        // Place both tab hosts in their RootGrid slots. The
        // horizontal host spans both columns in row 0 so its TabView
        // strip can grow under the title bar area; the vertical host
        // anchors at col 0 and spans both rows.
        // Both tab hosts are inserted at the back of the Z-order so
        // the XAML-declared VerticalTitleBar stays on top in the Row 0
        // overlap region. Without this, the expanded vertical strip
        // covers the layout-switch button in the title bar. The
        // VerticalTitleBar's Background="Transparent" already enables
        // hit-testing for its drag region and switch button.
        var hostElement = (FrameworkElement)_horizontalTabHost.HostElement;
        Grid.SetRow(hostElement, 0);
        Grid.SetColumn(hostElement, 0);
        Grid.SetColumnSpan(hostElement, 2);
        Canvas.SetZIndex(hostElement, -1);
        RootGrid.Children.Add(hostElement);

        Grid.SetRow(_verticalTabHost, 0);
        Grid.SetColumn(_verticalTabHost, 0);
        Grid.SetRowSpan(_verticalTabHost, 2);
        Canvas.SetZIndex(_verticalTabHost, -1);
        RootGrid.Children.Add(_verticalTabHost);

        // Parent every existing and future PaneHost into the shared
        // container declared in MainWindow.xaml. This is the single
        // owner for PaneHost lifetime in the visual tree — both tab
        // hosts read from it without ever reparenting.
        foreach (var t in _tabManager.Tabs) AddPaneHost(t);
        SwapActivePane();
        _tabManager.TabAdded += (_, t) => { AddPaneHost(t); SwapActivePane(); };
        _tabManager.TabRemoved += (_, t) => RemovePaneHost(t);
        _tabManager.ActiveTabChanged += (_, _) => SwapActivePane();

        // Tooltip chord label is sourced from KeyBindings.Default so
        // the button description cannot drift from the accelerator.
        var chord = KeyBindings.Default.Label(PaneAction.ToggleTabLayout);
        ToolTipService.SetToolTip(
            VerticalSwitchButton,
            chord is null
                ? "Switch to horizontal tabs"
                : $"Switch to horizontal tabs ({chord})");

        _tabHost = _uiSettings.VerticalTabs ? _verticalTabHost : _horizontalTabHost;

        _layout = new LayoutCoordinator(
            StripColumn,
            TitleBarStripMirror,
            (FrameworkElement)_horizontalTabHost.HostElement,
            _verticalTabHost,
            VerticalTitleBar);
        _layout.Snap(_uiSettings.VerticalTabs);

        _titleBar = new TitleBarCoordinator(
            this,
            _tabManager,
            _horizontalTabHost,
            _verticalTabHost,
            VerticalTitleDragRegion,
            VerticalTitleText,
            VerticalCaptionInset,
            isVerticalMode: () => _tabHost is VerticalTabHost);
        _titleBar.ApplyForCurrentMode();
        _titleBar.SyncCaptionInset();

        _taskbar = new TaskbarHost(this, _tabManager);

        AppWindow.Changed += (_, _) =>
        {
            _taskbar.OnAppWindowChanged(AppWindow);
            _titleBar.SyncCaptionInset();
        };

        InstallPaneAccelerators();

        _commandPaletteVm = CreateCommandPaletteViewModel();
        CommandPaletteUI.Bind(_commandPaletteVm);
        CommandPaletteUI.ApplySettings(_uiSettings.CommandPaletteBackground);

        // When the ViewModel closes itself (e.g. after executing a command),
        // sync the Popup and focus state.
        _commandPaletteVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(CommandPaletteViewModel.IsOpen)) return;
            if (_commandPaletteVm.IsOpen) return;
            if (_paletteCloseState != PaletteCloseState.Idle) return;

            _paletteCloseState = PaletteCloseState.ClosingFromCommand;
            try
            {
                CommandPalettePopup.IsOpen = false;
                SetCommandPaletteOpenOnAllTerminals(false);
                _frecencyStore?.Save();

                // Don't focus a surface that may have just been disposed
                // by the command we executed (e.g. close pane).
                // Let the tab/pane system handle focus naturally.
            }
            finally
            {
                _paletteCloseState = PaletteCloseState.Idle;
            }
        };

        // When the Popup is light-dismissed (click outside), sync the ViewModel.
        CommandPalettePopup.Closed += (_, _) =>
        {
            if (_paletteCloseState != PaletteCloseState.Idle) return;

            _paletteCloseState = PaletteCloseState.ClosingFromCommand;
            try
            {
                var wasOpen = _commandPaletteVm.IsOpen;
                _commandPaletteVm.Close();
                SetCommandPaletteOpenOnAllTerminals(false);
                // Only restore previous focus for light-dismiss (Escape, click outside).
                // For command execution, let the command's own focus handling win
                // (e.g., PaneHost focuses the new split pane).
                if (wasOpen)
                    _previousFocusSurface?.Focus(FocusState.Programmatic);
            }
            finally
            {
                _paletteCloseState = PaletteCloseState.Idle;
            }
        };

        _host.CommandPaletteToggleRequested += (_, _) =>
            DispatcherQueue.TryEnqueue(ToggleCommandPalette);

        _tabManager.LastTabClosed += (_, _) => Close();

        Closed += OnClosedAsync;
    }

    private async void OnClosedAsync(object sender, WindowEventArgs args)
    {
        // Let any in-flight ContentDialog complete before tearing
        // down the libghostty host. files-community/Files #17363
        // documents the COMException that fires otherwise.
        try
        {
            if (_dialogs.PendingCount > 0)
                await _dialogs.WhenAllClosedAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DialogTracker drain failed: {ex.Message}");
        }

        _taskbar.Dispose();

        // Surface lifetime is decoupled from Loaded/Unloaded
        // (see TerminalControl.DisposeSurface), so we have to
        // free every leaf in every tab explicitly before tearing
        // down the libghostty host.
        foreach (var t in _tabManager.Tabs) t.PaneHost.DisposeAllLeaves();
        _host.Dispose();
    }

    private void OnVerticalSwitchButtonClick(object sender, RoutedEventArgs e)
        => _router.RequestToggleTabLayout();

    private void AddPaneHost(TabModel tab)
    {
        var paneHost = (PaneHost)tab.PaneHost;
        paneHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        paneHost.VerticalAlignment = VerticalAlignment.Stretch;
        paneHost.Visibility = Visibility.Collapsed;
        PaneHostContainer.Children.Add(paneHost);
    }

    private void RemovePaneHost(TabModel tab)
    {
        var paneHost = (PaneHost)tab.PaneHost;
        PaneHostContainer.Children.Remove(paneHost);
    }

    private void SwapActivePane()
    {
        var active = (PaneHost)_tabManager.ActiveTab.PaneHost;
        foreach (UIElement child in PaneHostContainer.Children)
        {
            child.Visibility = ReferenceEquals(child, active)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Toggle between horizontal and vertical tab layouts at runtime.
    /// Triggered by Ctrl+Shift+, (comma), the title-bar icon button, and
    /// the strip context menu. Persists the choice via
    /// <see cref="UiSettings"/> so it survives the next launch.
    /// </summary>
    internal void ToggleTabLayout()
    {
        if (_layout.IsSwitching) return;
        var toVertical = !_uiSettings.VerticalTabs;
        _uiSettings.VerticalTabs = toVertical;
        _uiSettings.Save();
        _tabHost = toVertical ? _verticalTabHost : _horizontalTabHost;

        // SetTitleBar requires the target element to be visible and
        // in the visual tree. Calling it mid-animation hits
        // COMException 0x800F1000 because the incoming host is still
        // at opacity 0 / translated off-screen. Defer to the
        // Completed callback where Snap() has finalized visibility.
        //
        // The crossfade changes tab-host visibility, which causes
        // WinUI 3's focus manager to move focus away from the
        // terminal pane. Restore focus to the active terminal after
        // the animation settles.
        _layout.Animate(toVertical, onCompleted: () =>
        {
            _titleBar.ApplyForCurrentMode();
            var leaf = _tabManager.ActiveTab?.PaneHost?.ActiveLeaf;
            if (leaf is not null)
                leaf.Terminal().Focus(FocusState.Programmatic);
        });
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
    /// Router events are instance-scoped (no static subscriptions),
    /// so MainWindow can be closed and garbage-collected cleanly once
    /// the last tab closes.
    /// </summary>
    private void InstallPaneAccelerators()
    {
        // Accelerators live on RootGrid (the common ancestor of both
        // tab hosts and PaneHostContainer) so the focused
        // TerminalControl -- which is a child of PaneHostContainer,
        // NOT a descendant of any tab host -- is within scope.
        // ScopeOwner is intentionally left unset. Double-dispatch is
        // prevented by the _acceleratorFiredThisKeyDown guard below,
        // not by ScopeOwner (which would over-constrain the scope
        // and prevent the accelerator from firing at all when focus
        // is inside PaneHostContainer).
        // See https://github.com/deblasis/ghostty/issues/165.
        foreach (var binding in KeyBindings.Default.All)
        {
            var captured = binding;
            var accel = new KeyboardAccelerator
            {
                Modifiers = captured.Modifiers,
                Key = captured.Key,
            };
            accel.Invoked += (_, args) =>
            {
                args.Handled = true;
                if (_acceleratorFiredThisKeyDown == captured.Action) return;
                _acceleratorFiredThisKeyDown = captured.Action;
                _router.Invoke(captured.Action);
            };
            RootGrid.KeyboardAccelerators.Add(accel);
        }

        RootGrid.KeyUp += (_, _) => _acceleratorFiredThisKeyDown = null;
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        // Listen for keyboard-driven full-tab close. Route through
        // TabHost.RequestCloseTabAsync so the confirmation dialog
        // is the same code path as the per-tab X button and the
        // context-menu Close item — single source of truth for
        // close confirmation lives in TabHost, which has XamlRoot.
        _router.TabCloseRequestedFromKeyboard += async (_, _) =>
        {
            await _tabHost.RequestCloseTabAsync(_tabManager.ActiveTab);
        };

        // Vertical-tabs pinned toggle via Ctrl+Shift+Space. No-op
        // when the layout is horizontal (TabHost) — the chord is
        // registered globally but only VerticalTabHost responds.
        _router.ToggleVerticalTabsPinnedRequested += (_, _) =>
        {
            if (_tabHost is VerticalTabHost vth)
                vth.TogglePinnedFromKeyboard();
        };

        // Runtime tab-layout switch via Ctrl+Shift+Alt+V (and the
        // title-bar icon + context menu, which share the event path).
        _router.ToggleTabLayoutRequested += (_, _) => ToggleTabLayout();

        _router.CommandPaletteToggleRequested += (_, _) => ToggleCommandPalette();

        // Fullscreen toggle via F11.
        _router.ToggleFullscreenRequested += (_, _) => ToggleFullscreen();
    }

    /// <summary>
    /// Toggle between fullscreen and default window presenter. Uses
    /// <see cref="Microsoft.UI.Windowing.AppWindowPresenterKind"/> so
    /// the window chrome (title bar, borders) is hidden in fullscreen
    /// and restored on exit.
    /// </summary>
    private void ToggleFullscreen()
    {
        var kind = AppWindow.Presenter.Kind;
        AppWindow.SetPresenter(
            kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                ? Microsoft.UI.Windowing.AppWindowPresenterKind.Default
                : Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
    }

    private void ToggleCommandPalette()
    {
        if (_commandPaletteVm is not { } vm) return;

        if (vm.IsOpen)
        {
            _paletteCloseState = PaletteCloseState.ClosingFromToggle;
            try
            {
                vm.Close();
                CommandPalettePopup.IsOpen = false;
                SetCommandPaletteOpenOnAllTerminals(false);
                _previousFocusSurface?.Focus(FocusState.Programmatic);
            }
            finally { _paletteCloseState = PaletteCloseState.Idle; }
        }
        else
        {
            _previousFocusSurface = FocusManager.GetFocusedElement(Content.XamlRoot) as Controls.TerminalControl;

            var windowWidth = AppWindow.Size.Width;
            var paletteWidth = Math.Min(600, windowWidth * 0.9);
            CommandPalettePopup.HorizontalOffset = (windowWidth - paletteWidth) / 2;
            CommandPalettePopup.VerticalOffset = 48;
            CommandPaletteUI.Width = paletteWidth;

            vm.Open();
            CommandPalettePopup.IsOpen = true;
            SetCommandPaletteOpenOnAllTerminals(true);

            // WinUI Popups don't auto-focus their content. Dispatch the
            // focus call so it runs after the Popup finishes layout.
            DispatcherQueue.TryEnqueue(() => CommandPaletteUI.FocusSearchBox());
        }
    }

    private void SetCommandPaletteOpenOnAllTerminals(bool isOpen)
    {
        foreach (var tab in _tabManager.Tabs)
        {
            var paneHost = (Panes.PaneHost)tab.PaneHost;
            foreach (var leaf in PaneTree.Leaves(paneHost.RootNode))
                leaf.Terminal().CommandPaletteIsOpen = isOpen;
        }
    }

    private CommandPaletteViewModel CreateCommandPaletteViewModel()
    {
        _frecencyStore = FrecencyStore.Load();
        var frecency = _frecencyStore;

        // Defer command execution to the next dispatcher tick so the
        // palette closes first, avoiding visual tree contention between
        // Popup teardown and PaneHost Rebuild (e.g. close-pane).
        var builtIn = new BuiltInCommandSource(
            paneActionFactory: action => _ => DispatcherQueue.TryEnqueue(() => _router.Invoke(action)),
            bindingActionFactory: actionKey => _ => DispatcherQueue.TryEnqueue(() => ExecuteBindingAction(actionKey)));

        var jump = new JumpCommandSource(
            _tabManager,
            jumpAction: (tabIdx, _) => DispatcherQueue.TryEnqueue(() => _tabManager.JumpTo(tabIdx)));

        var config = new ConfigCommandSource();

        var sources = new List<ICommandSource> { builtIn, jump, config };

        // Build the action autocompleter with a minimal set of action schemas.
        var schemas = new Dictionary<string, ActionSchema>
        {
            ["reset"] = new() { Name = "reset", Description = "Reset the terminal", RequiresParameter = false },
            ["copy_to_clipboard"] = new() { Name = "copy_to_clipboard", Description = "Copy selection to clipboard", RequiresParameter = false },
            ["paste_from_clipboard"] = new() { Name = "paste_from_clipboard", Description = "Paste from clipboard", RequiresParameter = false },
            ["select_all"] = new() { Name = "select_all", Description = "Select all terminal content", RequiresParameter = false },
            ["increase_font_size"] = new() { Name = "increase_font_size", Description = "Increase font size", RequiresParameter = true, Parameters = ["1", "2"] },
            ["decrease_font_size"] = new() { Name = "decrease_font_size", Description = "Decrease font size", RequiresParameter = true, Parameters = ["1", "2"] },
            ["reset_font_size"] = new() { Name = "reset_font_size", Description = "Reset font size to default", RequiresParameter = false },
            ["clear_screen"] = new() { Name = "clear_screen", Description = "Clear screen and scrollback", RequiresParameter = false },
            ["scroll_to_top"] = new() { Name = "scroll_to_top", Description = "Scroll to top of scrollback", RequiresParameter = false },
            ["scroll_to_bottom"] = new() { Name = "scroll_to_bottom", Description = "Scroll to bottom", RequiresParameter = false },
            ["open_config"] = new() { Name = "open_config", Description = "Open configuration file", RequiresParameter = false },
            ["reload_config"] = new() { Name = "reload_config", Description = "Reload configuration", RequiresParameter = false },
            ["toggle_fullscreen"] = new() { Name = "toggle_fullscreen", Description = "Toggle fullscreen mode", RequiresParameter = false },
            ["equalize_splits"] = new() { Name = "equalize_splits", Description = "Equalize split panes", RequiresParameter = false },
            ["toggle_split_zoom"] = new() { Name = "toggle_split_zoom", Description = "Zoom current split", RequiresParameter = false },
        };

        var autoCompleter = new ActionAutoCompleter(schemas);

        return new CommandPaletteViewModel(
            sources,
            frecency,
            autoCompleter,
            groupByCategory: _uiSettings.CommandPaletteGroupCommands);
    }

    private void ExecuteBindingAction(string actionKey)
    {
        var leaf = _tabManager.ActiveTab?.PaneHost?.ActiveLeaf;
        if (leaf is null) return;

        var terminal = leaf.Terminal();
        var surfaceHandle = terminal.SurfaceHandle;
        if (surfaceHandle == IntPtr.Zero) return;

        var surface = new GhosttySurface(surfaceHandle);
        var actionBytes = Encoding.UTF8.GetBytes(actionKey);
        unsafe
        {
            fixed (byte* p = actionBytes)
            {
                NativeMethods.SurfaceBindingAction(surface, p, (UIntPtr)actionBytes.Length);
            }
        }
    }
}
