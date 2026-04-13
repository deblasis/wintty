using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Commands;
using Ghostty.Controls;
using Ghostty.Core.Hosting;
using Ghostty.Core.Tabs;
using Ghostty.Dialogs;
using Ghostty.Hosting;
using Ghostty.Interop;
using Ghostty.Input;
using Ghostty.Core.Panes;
using Ghostty.Services;
using Ghostty.Panes;
using Ghostty.Settings;
using Ghostty.Shell;
using Ghostty.Tabs;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
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
    private readonly ConfigService _configService;
    private readonly ConfigFileEditor _configEditor;
    private readonly PaneHostFactory _factory;
    private readonly TabManager _tabManager;
    private readonly PaneActionRouter _router;
    private readonly DialogTracker _dialogs = new();
    private readonly UiSettings _uiSettings;
    // Kept as a field so the ColorValuesChanged subscription is not GC'd.
    private readonly Windows.UI.ViewManagement.UISettings _systemUiSettings;

    private readonly TabHost _horizontalTabHost;
    private readonly VerticalTabHost _verticalTabHost;
    private ITabHost _tabHost;

    private readonly LayoutCoordinator _layout;
    private readonly TitleBarCoordinator _titleBar;
    private readonly TaskbarHost _taskbar;
    private readonly WindowThemeManager _themeManager;
    private readonly ShellThemeService _shellTheme;
    private readonly ThemePreviewService _themePreview;

    // Tracks the currently applied backdrop style so we can skip
    // redundant SystemBackdrop swaps on config reload.
    private string _currentBackdropStyle = "";

    private GradientTintVisual? _gradientVisual;
    private Window? _settingsWindow;

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
    // SetClassLongPtr and CreateSolidBrush are not in CsWin32 0.3.269
    // metadata for this platform target; keep hand-written.
    private const int GCLP_HBRBACKGROUND = -10;

    [LibraryImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CreateSolidBrush(uint crColor);

    // GetWindowLong, GetWindowPlacement, ShowWindow, WINDOWPLACEMENT,
    // WINDOW_STYLE, and SHOW_WINDOW_CMD are provided by CsWin32.

    // Captured from Content.XamlRoot at registration time (in the
    // one-shot Content.Loaded handler). Read by App.OnAnyWindowClosedInternal
    // on Window.Closed to remove the WindowsByRoot entry; reading
    // Content.XamlRoot directly at Closed time can return null because
    // WinUI 3 tears Content down before Closed fires.
    internal XamlRoot? RegisteredRoot { get; private set; }

    internal MainWindow(ConfigService configService, GhosttyHost bootstrapHost, HostLifetimeSupervisor supervisor)
        : this(configService, bootstrapHost, supervisor, seedTab: null)
    {
    }

    /// <summary>
    /// Full ctor. <paramref name="seedTab"/>, when non-null, is
    /// adopted as the sole initial tab (used by Move Tab to New
    /// Window); when null, the normal "create a fresh tab via the
    /// factory" path runs. <paramref name="bootstrapHost"/> is the
    /// app-owning GhosttyHost built once in App.xaml.cs; this window
    /// constructs its OWN per-window GhosttyHost from it using the
    /// shared-app ctor.
    /// </summary>
    private MainWindow(
        ConfigService configService,
        GhosttyHost bootstrapHost,
        HostLifetimeSupervisor supervisor,
        TabModel? seedTab)
    {
        InitializeComponent();

        _configService = configService;
        _configEditor = new ConfigFileEditor(configService.ConfigFilePath);

        // Build this window's per-window GhosttyHost around the shared
        // app. Each per-window host has its OWN per-window surface
        // dictionary; routing to this host from the bootstrap host's
        // libghostty callbacks happens via App._hostBySurface (populated
        // by the per-window host's Register / Adopt paths).
        _host = new GhosttyHost(
            DispatcherQueue,
            bootstrapHost.App.Handle,
            supervisor);
        // NOTE: configService.SetApp is already done by App.xaml.cs on
        // the bootstrap host. We do NOT call it again here.

        // Register with App.WindowsByRoot once the XamlRoot is live.
        // We capture the XamlRoot into RegisteredRoot so the
        // App.OnAnyWindowClosedInternal handler can remove the entry on
        // Window.Closed even if Content.XamlRoot has gone null by then
        // (which WinUI 3 does during window teardown).
        if (Content is FrameworkElement fe)
        {
            fe.Loaded += OnContentLoadedOnce;
            void OnContentLoadedOnce(object s, RoutedEventArgs e)
            {
                fe.Loaded -= OnContentLoadedOnce;
                RegisteredRoot = fe.XamlRoot;
                if (RegisteredRoot != null)
                {
                    App.WindowsByRoot[RegisteredRoot] = this;
                }
            }
        }

        // Detect initial system theme and notify libghostty so conditional
        // config blocks (e.g. palette dark/light) take effect immediately.
        // UISettings.Foreground is white in dark mode and black in light mode,
        // so R > 128 reliably distinguishes the two without needing the UWP
        // ApplicationTheme enum (which isn't available outside a UWP package).
        _systemUiSettings = new Windows.UI.ViewManagement.UISettings();
        var initialFg = _systemUiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground);
        var initialDark = initialFg.R > 128;
        Ghostty.Interop.NativeMethods.AppSetColorScheme(
            _host.App,
            initialDark ? Ghostty.Interop.GhosttyColorScheme.Dark : Ghostty.Interop.GhosttyColorScheme.Light);

        // Subscribe to runtime theme changes. ColorValuesChanged fires on a
        // background thread, so dispatch back to the UI thread before calling
        // into libghostty (which expects UI-thread callers for App-level ops).
        _systemUiSettings.ColorValuesChanged += (s, _) =>
        {
            var fg = s.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground);
            var dark = fg.R > 128;
            DispatcherQueue.TryEnqueue(() =>
                Ghostty.Interop.NativeMethods.AppSetColorScheme(
                    _host.App,
                    dark ? Ghostty.Interop.GhosttyColorScheme.Dark : Ghostty.Interop.GhosttyColorScheme.Light));
        };

        // Apply initial backdrop (Mica when opaque, transparent when
        // background-opacity < 1). Also sets the Win32 class brush
        // and RootGrid background to match.
        ApplyBackdropStyle();
        ApplyGradientTint();

        // Extend content into the title bar: remove the system-drawn
        // title bar chrome and let TabHost's TabView strip render
        // where the title bar used to be. Must be set before the
        // TabHost is parented so the content area is sized without
        // the default title bar row.
        ExtendsContentIntoTitleBar = true;

        // Apply window-theme from config. The manager resolves the
        // config value ("light"/"dark"/"system"/"auto") to a concrete
        // dark/light choice and sets both ElementTheme on the XAML root
        // and the DWM immersive dark mode attribute for the title bar
        // caption buttons (which XAML cannot control when
        // ExtendsContentIntoTitleBar is true).
        _themeManager = new WindowThemeManager(configService, DispatcherQueue);
        ApplyTheme();
        _themeManager.ThemeChanged += _ => ApplyTheme();

        _shellTheme = new ShellThemeService(configService);
        _shellTheme.ThemeChanged += ApplyShellTheme;
        _themePreview = new ThemePreviewService(configService, DispatcherQueue);
        _themePreview.ListThemesRequested += OnListThemesRequested;

        _factory = new PaneHostFactory(_host);
        _tabManager = new TabManager(
            () => _factory.Create(),
            seed: seedTab);
        _router = new PaneActionRouter(_tabManager);
        _uiSettings = UiSettings.Load();
        RestoreWindowPlacement();

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

        // Apply initial shell theme now that tab hosts exist.
        ApplyShellTheme();

        // Parent every existing and future PaneHost into the shared
        // container declared in MainWindow.xaml. This is the single
        // owner for PaneHost lifetime in the visual tree — both tab
        // hosts read from it without ever reparenting.
        foreach (var t in _tabManager.Tabs) AddPaneHost(t);
        SwapActivePane();
        _tabManager.TabAdded += (_, t) =>
        {
            AddPaneHost(t);
            SwapActivePane();
            // Apply current cursor color to new tabs' pane borders.
            UpdateCursorAccentColors();
        };
        _tabManager.TabRemoved += (_, t) => RemovePaneHost(t);
        _tabManager.ActiveTabChanged += (_, _) => SwapActivePane();

        // Apply initial cursor-color-derived pane border.
        UpdateCursorAccentColors();

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

        // App-targeted actions (OpenConfig, ReloadConfig) fire on the
        // bootstrap host because libghostty sends them with target=app,
        // not target=surface. The per-window _host never receives them.
        // Only the primary window subscribes; adopted windows (tab detach)
        // skip this to avoid stacking duplicate handlers.
        if (seedTab is null)
        {
            var appHost = Ghostty.App.BootstrapHost!;
            appHost.OpenConfigRequested += (_, _) =>
            {
                if (configService.SettingsUiEnabled)
                {
                    var editor = new ConfigFileEditor(configService.ConfigFilePath);
                    var keybindings = new KeyBindingsProvider(configService);
                    var themeProvider = new ThemeProvider(configService);
                    // Reuse existing settings window if still open.
                    if (_settingsWindow is not null)
                    {
                        _settingsWindow.Activate();
                        return;
                    }
                    var settingsWin = new Ghostty.Settings.SettingsWindow(
                        configService, editor, keybindings, themeProvider);
                    settingsWin.Closed += (_, _) => _settingsWindow = null;
                    _settingsWindow = settingsWin;
                    settingsWin.Activate();
                    return;
                }

                var path = configService.ConfigFilePath;
                if (string.IsNullOrEmpty(path)) return;
                try
                {
                    // The config file has no extension so UseShellExecute
                    // may fail to find an associated program. Try shell
                    // execute first (respects user file associations), then
                    // fall back to notepad which can always open text files.
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = path,
                            UseShellExecute = true,
                        });
                    }
                    catch
                    {
                        System.Diagnostics.Process.Start("notepad.exe", path);
                    }
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] Failed to open config file: {ex.Message}");
                }
            };

            appHost.ReloadConfigRequested += (_, _) => configService.Reload();
        }

        // Ctrl+Shift+Scroll wheel opacity adjustment from any terminal surface.
        _host.OpacityAdjustRequested += (_, direction) => AdjustOpacity(direction);

        // Re-evaluate transparency state after every config reload so
        // Ctrl+Shift+Scroll and Settings UI changes take effect live.
        _configService.ConfigChanged += _ =>
        {
            ApplyBackdropStyle();
            UpdateAcrylicTuning();
            ApplyGradientTint();
            UpdateCursorAccentColors();

            // Re-apply shell theme after backdrop style, since
            // ApplyBackdropStyle may override RootGrid.Background.
            ApplyShellTheme();
        };

        _tabManager.LastTabClosed += (_, _) => Close();

        Closed += OnClosedAsync;
    }

    /// <summary>
    /// Build a <see cref="MainWindow"/> that adopts an existing
    /// <see cref="TabModel"/> as its sole initial tab, WITHOUT
    /// activating. Caller is responsible for positioning the window
    /// (via <see cref="Microsoft.UI.Windowing.AppWindow.MoveAndResize"/>
    /// or the like) and then calling <see cref="Window.Activate"/>.
    ///
    /// Used today by <see cref="DetachTabToNewWindow"/> for cursor-
    /// anchored placement. PR 201 Snap Layouts will call this same
    /// factory to install a snap rect before first activation so there
    /// is no visible placement flicker.
    /// </summary>
    internal static MainWindow CreateForAdoption(
        ConfigService configService,
        GhosttyHost bootstrapHost,
        HostLifetimeSupervisor supervisor,
        TabModel adoptedTab)
    {
        return new MainWindow(configService, bootstrapHost, supervisor, seedTab: adoptedTab);
    }

    /// <summary>
    /// Pre-activation hook for Snap Layouts placement. PR 199's
    /// detach flow constructs a MainWindow but MUST NOT call
    /// Activate until any snap target has been applied, otherwise
    /// the window flashes at the wrong origin. Call this once
    /// placement is done.
    /// </summary>
    internal void ActivateAfterPlacement() => Activate();

    /// <summary>
    /// Move <paramref name="tab"/> out of this window into a brand
    /// new <see cref="MainWindow"/>. The new window is positioned
    /// near the current mouse cursor on the monitor the cursor is
    /// currently on. Disabled (via the menu <c>IsEnabled</c> guard)
    /// when this window has only one tab, because moving the sole
    /// tab into a new window would be a no-op.
    /// </summary>
    internal void DetachTabToNewWindow(TabModel tab)
    {
        DetachTabToWindow(tab, newWindow =>
        {
            // Cursor-anchored placement. Size = this window's current size
            // so there is no jarring resize.
            var placement = ComputeCursorAnchoredPlacement(newWindow);
            var rect = new Windows.Graphics.RectInt32(
                placement.X, placement.Y, placement.Width, placement.Height);
            newWindow.AppWindow.MoveAndResize(rect);
        });
    }

    /// <summary>
    /// Detach <paramref name="tab"/> into a new window and snap it to
    /// the given <paramref name="zone"/> on the current monitor BEFORE
    /// activation, so there is no visible placement flicker.
    /// </summary>
    internal void DetachTabToZone(TabModel tab, Ghostty.Core.Tabs.SnapZone zone)
    {
        DetachTabToWindow(tab, newWindow =>
        {
            // Snap to zone on the source window's monitor. MoveAndResize
            // BEFORE Activate so the window never flashes at the default
            // origin.
            var display = Tabs.SnapPlacement.ResolveDisplayFor(AppWindow);
            Tabs.SnapPlacement.ApplyZone(newWindow.AppWindow, display, zone);
        });
    }

    /// <summary>
    /// Shared detach-rehost-activate logic. Detaches <paramref name="tab"/>
    /// from this window, creates a new <see cref="MainWindow"/>, rehosts
    /// the pane tree, runs <paramref name="placementAction"/> for
    /// positioning, then activates the new window.
    /// </summary>
    private void DetachTabToWindow(TabModel tab, Action<MainWindow> placementAction)
    {
        if (_tabManager.Tabs.Count <= 1)
            throw new InvalidOperationException(
                "DetachTabToWindow: guarded menu fired on single-tab window.");

        // Source-side: detach the model. The manager's TabRemoved
        // subscribers already drain visual state (RemovePaneHost in
        // this MainWindow, RemoveItem in each tab host).
        var detached = _tabManager.DetachTab(tab);

        var bootstrap = App.BootstrapHost
            ?? throw new InvalidOperationException(
                "DetachTabToWindow: no bootstrap host; App.OnLaunched did not run.");
        var supervisor = App.LifetimeSupervisor
            ?? throw new InvalidOperationException(
                "DetachTabToWindow: no lifetime supervisor; App.OnLaunched did not run.");

        // Rehost the pane tree's terminals to a fresh per-window host
        // built inside the new window. RehostTo is what actually moves
        // the surface entries out of this window's _surfaces into the
        // new window's _surfaces AND rewrites App._hostBySurface.
        var newWindow = MainWindow.CreateForAdoption(_configService, bootstrap, supervisor, detached);
        var newHost = newWindow._host;
        ((Panes.PaneHost)detached.PaneHost).RehostTo(newHost);

        placementAction(newWindow);

        // Subscribe the new window to the process-wide last-window-exit
        // handler. WindowsByRoot insertion happens inside the new
        // window's own Content.Loaded handler.
        newWindow.Closed += ((App)Application.Current).OnAnyWindowClosedInternal;

        newWindow.Activate();
    }

    /// <summary>
    /// Compute the cursor-anchored target rect for a newly built
    /// <see cref="MainWindow"/>. Queries <c>GetCursorPos</c> (via
    /// CsWin32), resolves the monitor the cursor is on via
    /// <see cref="Microsoft.UI.Windowing.DisplayArea.GetFromPoint"/>,
    /// and delegates the clamping math to
    /// <see cref="Ghostty.Core.Windows.CursorWindowPlacement.Compute"/>.
    ///
    /// DPI contract: <c>GetCursorPos</c> returns physical pixel
    /// coordinates in virtual desktop space. <c>DisplayArea.GetFromPoint</c>
    /// consumes physical pixels. The two line up without scaling.
    /// </summary>
    private Ghostty.Core.Windows.PlacementRect ComputeCursorAnchoredPlacement(MainWindow target)
    {
        PInvoke.GetCursorPos(out var pt);

        var cursorPoint = new Windows.Graphics.PointInt32(pt.X, pt.Y);
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
            cursorPoint,
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);

        var work = display?.WorkArea
            ?? new Windows.Graphics.RectInt32(0, 0, 1920, 1080);

        // Inherit the source window's current size.
        var size = AppWindow.Size;

        return Ghostty.Core.Windows.CursorWindowPlacement.Compute(
            cursorX: pt.X,
            cursorY: pt.Y,
            windowWidth: size.Width,
            windowHeight: size.Height,
            workArea: new Ghostty.Core.Windows.WorkAreaRect(
                work.X, work.Y, work.Width, work.Height));
    }

    private async void OnClosedAsync(object sender, WindowEventArgs args)
    {
        // Persist window placement for next launch. Skip when
        // fullscreen -- restore to the normal size instead.
        if (AppWindow.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
        {
            var hwnd = new HWND(WindowNative.GetWindowHandle(this));
            var style = (WINDOW_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            var isMaximized = (style & WINDOW_STYLE.WS_MAXIMIZE) != 0;
            _uiSettings.WindowMaximized = isMaximized;

            // Save the restored (non-maximized) bounds so we don't
            // persist a maximized rect that fills the whole monitor.
            if (isMaximized)
            {
                var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
                PInvoke.GetWindowPlacement(hwnd, ref placement);
                var rc = placement.rcNormalPosition;
                _uiSettings.WindowX = rc.left;
                _uiSettings.WindowY = rc.top;
                _uiSettings.WindowWidth = rc.right - rc.left;
                _uiSettings.WindowHeight = rc.bottom - rc.top;
            }
            else
            {
                _uiSettings.WindowX = AppWindow.Position.X;
                _uiSettings.WindowY = AppWindow.Position.Y;
                _uiSettings.WindowWidth = AppWindow.Size.Width;
                _uiSettings.WindowHeight = AppWindow.Size.Height;
            }
            _uiSettings.Save();
        }

        // Close settings window if open.
        _settingsWindow?.Close();
        _settingsWindow = null;

        // Let any in-flight ContentDialog complete before tearing
        // down the libghostty host. files-community/Files # 17363
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

        _gradientVisual?.Dispose();
        _gradientVisual = null;
        _taskbar.Dispose();
        _themeManager.Dispose();

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
        // context-menu Close item -- single source of truth for
        // close confirmation lives in TabHost, which has XamlRoot.
        _router.TabCloseRequestedFromKeyboard += async (_, _) =>
        {
            await _tabHost.RequestCloseTabAsync(_tabManager.ActiveTab);
        };

        // Vertical-tabs pinned toggle via Ctrl+Shift+Space. No-op
        // when the layout is horizontal (TabHost) -- the chord is
        // registered globally but only VerticalTabHost responds.
        _router.ToggleVerticalTabsPinnedRequested += (_, _) =>
        {
            if (_tabHost is VerticalTabHost vth)
                vth.TogglePinnedFromKeyboard();
        };

        // Runtime tab-layout switch via Ctrl+Shift+, (and the strip
        // context menu's "Switch to vertical/horizontal tabs" item,
        // which share the same event path through PaneActionRouter).
        _router.ToggleTabLayoutRequested += (_, _) => ToggleTabLayout();

        _router.CommandPaletteToggleRequested += (_, _) => ToggleCommandPalette();

        // Fullscreen toggle via F11.
        _router.ToggleFullscreenRequested += (_, _) => ToggleFullscreen();
    }

    /// <summary>
    /// Apply the resolved window theme to the XAML visual tree and the
    /// DWM non-client area. Called once at startup and again whenever
    /// the <see cref="WindowThemeManager"/> detects a change.
    /// </summary>
    private void ApplyTheme()
    {
        if (Content is FrameworkElement root)
            root.RequestedTheme = _themeManager.ElementTheme;
        _themeManager.ApplyToWindow(this);

        // Caption button colors must be set explicitly when
        // ExtendsContentIntoTitleBar is true, otherwise WinUI 3
        // fills them with the system accent color.
        var tb = AppWindow.TitleBar;
        var fg = _themeManager.IsDarkMode
            ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        var fgInactive = _themeManager.IsDarkMode
            ? Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x66, 0x00, 0x00, 0x00);
        tb.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        tb.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        tb.ButtonForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = fgInactive;
        tb.ButtonHoverBackgroundColor = _themeManager.IsDarkMode
            ? Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x33, 0x00, 0x00, 0x00);
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedBackgroundColor = _themeManager.IsDarkMode
            ? Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x22, 0x00, 0x00, 0x00);
        tb.ButtonPressedForegroundColor = fg;

        // Propagate theme to tab hosts so their text/icons adapt.
        // Guard: tab hosts are created after the first ApplyTheme call.
        if (_horizontalTabHost is not null)
        {
            _horizontalTabHost.SetRequestedTheme(_themeManager.ElementTheme);
            _verticalTabHost.SetRequestedTheme(_themeManager.ElementTheme);
        }
    }

    /// <summary>
    /// Apply palette-derived colors to the window chrome when
    /// windows-shell-theme is enabled.
    /// </summary>
    private void ApplyShellTheme()
    {
        if (!_shellTheme.IsEnabled)
        {
            // Revert to standard chrome. Clear custom title bar
            // button colors back to null (system default).
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = null;
            titleBar.ButtonForegroundColor = null;
            titleBar.ButtonInactiveBackgroundColor = null;
            titleBar.ButtonInactiveForegroundColor = null;
            titleBar.ButtonHoverBackgroundColor = null;
            titleBar.ButtonHoverForegroundColor = null;

            // Reset VerticalTitleText to theme default.
            VerticalTitleText.ClearValue(TextBlock.ForegroundProperty);

            // Force ApplyBackdropStyle to re-run by clearing its
            // cache. It sets RootGrid.Background via
            // SetTransparentChrome/SetOpaqueChrome.
            _currentBackdropStyle = "";
            ApplyBackdropStyle();
            ApplyTheme();

            _horizontalTabHost.ClearShellTheme();
            _verticalTabHost.ClearShellTheme();
            return;
        }

        var tb = AppWindow.TitleBar;
        // Transparent backgrounds let the caption buttons blend
        // with whatever backdrop (Mica/Acrylic) is behind them.
        tb.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        tb.ButtonForegroundColor = _shellTheme.TitleBarForeground;
        tb.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(
            128, _shellTheme.TitleBarForeground.R,
            _shellTheme.TitleBarForeground.G,
            _shellTheme.TitleBarForeground.B);
        tb.ButtonHoverBackgroundColor = _shellTheme.TabBarBackground;
        tb.ButtonHoverForegroundColor = _shellTheme.TitleBarForeground;

        // When a transparent backdrop (frosted/crystal) is active, the
        // RootGrid must stay transparent so the SystemBackdrop shows
        // through. An opaque shell theme background would mask it.
        var needsTransparent = _currentBackdropStyle is "frosted" or "crystal";
        RootGrid.Background = new SolidColorBrush(
            needsTransparent
                ? Windows.UI.Color.FromArgb(0, 0, 0, 0)
                : Microsoft.UI.ColorHelper.FromArgb(
                    _shellTheme.TitleBarBackground.A,
                    _shellTheme.TitleBarBackground.R,
                    _shellTheme.TitleBarBackground.G,
                    _shellTheme.TitleBarBackground.B));

        VerticalTitleText.Foreground = new SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(
                _shellTheme.TitleBarForeground.A,
                _shellTheme.TitleBarForeground.R,
                _shellTheme.TitleBarForeground.G,
                _shellTheme.TitleBarForeground.B));

        // Push theme to both tab hosts.
        _horizontalTabHost.ApplyShellTheme(_shellTheme);
        _verticalTabHost.ApplyShellTheme(_shellTheme);
    }

    /// <summary>
    /// Update all accent-colored UI from the cursor color in config:
    /// pane border, selected tab indicator, vertical tab accent bar.
    /// Called on every config reload so theme changes take effect.
    /// </summary>
    private void UpdateCursorAccentColors()
    {
        var cc = _configService.CursorColor ?? _configService.ForegroundColor;
        var wuiColor = Windows.UI.Color.FromArgb(0xFF,
            (byte)(cc >> 16), (byte)(cc >> 8), (byte)cc);
        var muiColor = Microsoft.UI.ColorHelper.FromArgb(0xFF,
            (byte)(cc >> 16), (byte)(cc >> 8), (byte)cc);
        var brush = new SolidColorBrush(muiColor);

        foreach (var t in _tabManager.Tabs)
            ((PaneHost)t.PaneHost).SetActiveBorderBrush(brush);

        _horizontalTabHost.SetAccentColor(wuiColor);
        _verticalTabHost.SetAccentColor(wuiColor);
    }

    /// <summary>
    /// Apply the window backdrop based on background-style and
    /// background-opacity config values. Dispatches to the correct
    /// SystemBackdrop implementation for each preset.
    /// </summary>
    private void ApplyBackdropStyle()
    {
        var opacity = _configService.BackgroundOpacity;
        var configStyle = _configService.BackgroundStyle;

        // If the user's configured style is acrylic-based, keep the
        // acrylic backdrop alive even at opacity=1.0 so Ctrl+Shift+Scroll
        // doesn't flash between Mica and acrylic at the boundary.
        var style = (opacity >= 1.0 && configStyle != "frosted")
            ? "solid"
            : configStyle;

        // Skip if the effective style hasn't changed.
        if (style == _currentBackdropStyle && SystemBackdrop is not null)
            return;

        _currentBackdropStyle = style;

        var hwnd = WindowNative.GetWindowHandle(this);

        var (tintColor, tintOpacity, luminosityOpacity) = ResolveAcrylicTuning();

        switch (style)
        {
            case "frosted":
                if (DesktopAcrylicController.IsSupported())
                    SystemBackdrop = new AcrylicBackdrop(
                        tintColor, tintOpacity, luminosityOpacity);
                else
                    goto case "solid";
                SetTransparentChrome(hwnd);
                break;

            case "crystal":
                SystemBackdrop = new CrystalBackdrop(hwnd);
                SetTransparentChrome(hwnd);
                break;

            case "solid":
            default:
                if (MicaController.IsSupported())
                    SystemBackdrop = new MicaBackdrop();
                else
                    SystemBackdrop = null;
                SetOpaqueChrome(hwnd);
                break;
        }
    }

    /// <summary>
    /// Re-apply acrylic tuning knobs without recreating the backdrop.
    /// Called from config reload when only the tuning values changed
    /// but the backdrop type stays the same.
    /// </summary>
    private void UpdateAcrylicTuning()
    {
        if (_currentBackdropStyle != "frosted") return;
        if (SystemBackdrop is not AcrylicBackdrop current) return;

        var (tintColor, tintOpacity, luminosityOpacity) = ResolveAcrylicTuning();
        current.UpdateTuning(tintColor, tintOpacity, luminosityOpacity);
    }

    /// <summary>
    /// Resolve effective acrylic tuning values from config, applying
    /// blur-follows-opacity scaling when enabled.
    /// </summary>
    private (Windows.UI.Color tintColor, float tintOpacity, float luminosityOpacity)
        ResolveAcrylicTuning()
    {
        var tintColor = _configService.BackgroundTintColor
            ?? Windows.UI.Color.FromArgb(0, 0, 0, 0);
        var tintOpacity = _configService.BackgroundTintOpacity ?? 0.3f;
        var luminosityOpacity = _configService.BackgroundLuminosityOpacity ?? 0.3f;

        // When enabled, tint and luminosity track opacity directly:
        // 1.0 = fully opaque (solid-looking), 0.0 = fully see-through.
        // Uses the terminal background color as tint so at full opacity
        // the acrylic blends into the terminal background.
        if (_configService.BackgroundBlurFollowsOpacity)
        {
            var opacity = (float)_configService.BackgroundOpacity;
            tintOpacity = opacity;
            luminosityOpacity = opacity;

            if (_configService.BackgroundTintColor is null)
            {
                var bg = _configService.BackgroundColor;
                tintColor = Windows.UI.Color.FromArgb(0xFF,
                    (byte)(bg >> 16), (byte)(bg >> 8), (byte)bg);
            }
        }

        return (tintColor, tintOpacity, luminosityOpacity);
    }

    /// <summary>
    /// Create, update, or remove the gradient tint visual based on
    /// the current config. Called on startup and config reload.
    /// </summary>
    private void ApplyGradientTint()
    {
        var points = _configService.GradientPoints;

        if (points.Count == 0)
        {
            _gradientVisual?.Dispose();
            _gradientVisual = null;
            return;
        }

        var isOverlay = _configService.GradientBlend == "overlay";
        var gradientOpacity = _configService.GradientOpacity;

        // Recreate if blend mode changed.
        if (_gradientVisual is not null)
        {
            _gradientVisual.Dispose();
            _gradientVisual = null;
        }

        _gradientVisual = new GradientTintVisual(
            RootGrid, points, isOverlay, gradientOpacity);

        // Track opacity if blur-follows-opacity is on.
        if (_configService.BackgroundBlurFollowsOpacity)
            _gradientVisual.SetOpacity((float)_configService.BackgroundOpacity);
        else if (!isOverlay)
            _gradientVisual.SetOpacity(1f);

        _gradientVisual.ApplyAnimation(
            _configService.GradientAnimation,
            _configService.GradientSpeed);
    }

    private void SetTransparentChrome(IntPtr hwnd)
    {
        SetClassLongPtr(hwnd, GCLP_HBRBACKGROUND,
            Win32Interop.GetStockObject(Win32Interop.NULL_BRUSH));
        RootGrid.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    private void SetOpaqueChrome(IntPtr hwnd)
    {
        SetClassLongPtr(hwnd, GCLP_HBRBACKGROUND,
            CreateSolidBrush(0x000C0C0Cu));
        RootGrid.Background = new SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x0C, 0x0C, 0x0C));
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

    /// <summary>
    /// Restore the window position and size from the previous session.
    /// Validates that at least part of the window is visible on a
    /// current monitor (handles monitor disconnects, DPI changes).
    /// </summary>
    private void RestoreWindowPlacement()
    {
        var w = _uiSettings.WindowWidth;
        var h = _uiSettings.WindowHeight;
        if (w is null || h is null || w < 200 || h < 150) return;

        var x = _uiSettings.WindowX ?? 0;
        var y = _uiSettings.WindowY ?? 0;

        // Ensure the window's top-left quadrant is on a live monitor.
        // DisplayArea.GetFromPoint returns the nearest display if the
        // point is off-screen, but we check explicitly so we don't
        // place the window somewhere invisible.
        var checkPoint = new Windows.Graphics.PointInt32(
            x + Math.Min(100, w.Value / 2),
            y + Math.Min(50, h.Value / 2));
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
            checkPoint, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);

        // If the saved position is completely outside any display's
        // work area, let the OS pick a default position.
        var work = display.WorkArea;
        if (x + w.Value < work.X || x > work.X + work.Width ||
            y + h.Value < work.Y || y > work.Y + work.Height)
            return;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(w.Value, h.Value));
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));

        if (_uiSettings.WindowMaximized)
            PInvoke.ShowWindow(new HWND(WindowNative.GetWindowHandle(this)), SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED);
    }

    /// <summary>
    /// Adjust background opacity by a step. Direction: +1 = increase,
    /// -1 = decrease, 0 = reset to 1.0. Writes the new value to the
    /// config file and triggers a reload so all surfaces pick it up.
    /// Step size matches the Settings UI slider (0.05).
    /// </summary>
    private void AdjustOpacity(int direction)
    {
        const double step = 0.05;
        var current = _configService.BackgroundOpacity;
        var next = direction switch
        {
            0 => 1.0,
            _ => Math.Clamp(current + direction * step, 0.0, 1.0),
        };

        // Skip the write+reload round-trip when nothing changed.
        if (Math.Abs(next - current) < 0.001) return;

        _configService.SuppressWatcher(true);
        _configEditor.SetValue("background-opacity", next.ToString("F2"));
        _configService.SuppressWatcher(false);
        _configService.Reload();
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
            bindingActionFactory: actionKey => _ => DispatcherQueue.TryEnqueue(() => ExecuteBindingAction(actionKey)),
            opacityAction: direction => DispatcherQueue.TryEnqueue(() => AdjustOpacity(direction)));

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

    // Prevent the delegates from being GC'd while the picker holds pointers.
    private NativeMethods.InlineThemeCallback? _inlineThemeCb;
    private GhosttySurface _pickerSurface;
    private IntPtr _pickerHandle;

    private void OnListThemesRequested(object? sender, EventArgs e)
    {
        var leaf = _tabManager.ActiveTab?.PaneHost?.ActiveLeaf;
        if (leaf is null) return;

        var terminal = leaf.Terminal();
        var surfaceHandle = terminal.SurfaceHandle;
        if (surfaceHandle == IntPtr.Zero) return;

        _pickerSurface = new GhosttySurface(surfaceHandle);

        // Theme callback: apply preview/confirm colors on the UI thread.
        _inlineThemeCb = (namePtr, confirmed) =>
        {
            try
            {
                var name = Marshal.PtrToStringUTF8(namePtr);
                if (name is null) return;

                // The callback fires from the Zig/apprt thread. Dispatch
                // to the UI thread so ConfigService and ShellThemeService
                // updates happen on the right thread.
                DispatcherQueue.TryEnqueue(() =>
                {
                    _themePreview.ApplyThemePreview(name);
                });
            }
            catch { }
        };
        var cbPtr = Marshal.GetFunctionPointerForDelegate(_inlineThemeCb);

        _pickerHandle = NativeMethods.SurfaceListThemes(_pickerSurface, cbPtr);
        // Picker is now active. handleKey fires on each key event and
        // sets should_quit when the user confirms/cancels. We poll on
        // a timer to clean up, avoiding a thread that races with the
        // input redirect callback.
        if (_pickerHandle != IntPtr.Zero)
            StartPickerPoll();
    }

    private void StartPickerPoll()
    {
        // Use a DispatcherQueue timer so cleanup runs on the UI thread
        // with no race against the apprt thread's input redirect.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.Tick += (_, _) =>
        {
            if (!NativeMethods.SurfaceListThemesShouldQuit(_pickerHandle))
                return;

            timer.Stop();
            NativeMethods.SurfaceListThemesDeinit(_pickerSurface, _pickerHandle);
            _pickerHandle = IntPtr.Zero;
            _inlineThemeCb = null;
        };
        timer.Start();
    }
}
