using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Commands;
using Ghostty.Controls;
using Ghostty.Core.Config;
using Ghostty.Core.Hosting;
using Ghostty.Core.Input;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Ghostty.Dialogs;
using Ghostty.Hosting;
using Ghostty.Interop;
using Ghostty.Input;
using Ghostty.Core.Panes;
using Ghostty.Core.Shell;
using Ghostty.Core.Windows;
using Ghostty.Services;
using Ghostty.Logging;
using Ghostty.Panes;
using Ghostty.Settings;
using Ghostty.Shell;
using Ghostty.Tabs;
using Microsoft.Extensions.Logging;
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

    // Subclasses this window's WndProc to swallow WM_SYSCHAR so Alt chords
    // (e.g. the Alt+Shift+= / Alt+Shift+- splits) do not ring the Win32
    // menu beep. Disposed in OnClosedAsync to restore the original proc.
    private SysCharBeepSuppressor? _beepSuppressor;
    private readonly ConfigService _configService;
    // Single process-wide editor (owned by App). The per-instance
    // RMW lock only serializes if every writer hits the SAME editor,
    // so anything in this window that needs to mutate the config file
    // goes through App.ConfigFileEditor rather than building its own.
    private readonly IConfigFileEditor _configEditor;
    // Synchronous immediate-write helper for the window's own config
    // mutations (e.g. the opacity-adjust keybindings). Catches disk
    // failures and keeps the watcher flag balanced so an IOException
    // can't fail-fast the process or leave the watcher suppressed --
    // the same guarantee the Settings pages get. Debounced writes
    // (e.g. vertical-tabs) still go through App.ConfigWriteScheduler.
    private readonly SettingsConfigWriter _configWriter;
    // Narrower than holding ILoggerFactory: AcrylicBackdrop is rebuilt
    // per backdrop-style change in ApplyBackdropStyle(), and that is
    // the ONLY reason this window needed a way to mint loggers after
    // construction. Capturing the delegate once keeps the logger
    // surface on MainWindow honest about its scope and avoids a
    // field that a future reader could reach into for unrelated
    // things. The detach-to-new-window path reads App.LoggerFactory
    // statically (same shape as App.BootstrapHost / App.LifetimeSupervisor
    // just above it in DetachTabToNewWindow).
    private readonly Func<ILogger<AcrylicBackdrop>> _newAcrylicLogger;
    private readonly ILogger<MainWindow> _logger;
    private readonly PaneHostFactory _factory;
    private readonly TabManager _tabManager;
    private readonly PaneActionRouter _router;

    /// <summary>This window's tab manager, exposed for session capture.</summary>
    internal TabManager TabManager => _tabManager;

    /// <summary>
    /// The closed-tab store this window feeds. The quake window is excluded so
    /// its drop-down tabs never leak into the regular reopen-closed-tab history
    /// (mirrors CaptureSession's window-level quake exclusion).
    /// </summary>
    private Core.Panes.ClosedStack<Core.Session.TabSession>? ClosedTabsStore
        => IsQuickTerminal ? null : App.ClosedTabs;

    /// <summary>
    /// Raised when this (non-quake) window moves or resizes, so the
    /// session manager can debounce-persist the new geometry.
    /// </summary>
    internal event EventHandler? PositionChanged;

    // Static cache so the router's getProfiles lambda does not allocate a
    // fresh empty array on every Ctrl+Shift+N chord when ProfileRegistry is
    // not yet wired (cold-start path) or returns an unset snapshot.
    private static readonly IReadOnlyList<Ghostty.Core.Profiles.ResolvedProfile> EmptyProfiles = [];
    private readonly DialogTracker _dialogs = new();
    private readonly WindowState _windowState;
    // Kept as a field so the ColorValuesChanged subscription is not GC'd.
    private readonly Windows.UI.ViewManagement.UISettings _systemUiSettings;
    // Mirrors the current tab-strip orientation so we can detect when
    // a config reload or settings-toggle callback actually changed the
    // visible state (vs. echoing the same value we already apply).
    private bool _verticalTabsVisible;

    private readonly TabHost _horizontalTabHost;
    private readonly VerticalTabHost _verticalTabHost;
    private ITabHost _tabHost;

    private readonly LayoutCoordinator _layout;
    private readonly TitleBarCoordinator _titleBar;
    private readonly TaskbarHost _taskbar;
    private readonly WindowThemeManager _themeManager;
    private readonly ShellThemeService _shellTheme;
    private readonly ThemePreviewService _themePreview;

    // Set at the top of OnClosedAsync. Theme callbacks route through the
    // dispatcher, so a switch-then-close can leave an ApplyTheme queued to
    // run mid-teardown; this gate makes it a no-op (issue #208).
    private bool _isClosed;

    // Tracks the currently applied backdrop style so we can skip
    // redundant SystemBackdrop swaps on config reload.
    private string _currentBackdropStyle = "";

    // Win32 class-brush kind currently applied via SetClassLongPtr.
    // Cached so repeated reloads on the same style don't re-invoke
    // the Win32 call (and, for the opaque case, don't leak a fresh
    // GDI brush per reload).
    private enum ClassBrushKind { Transparent, Opaque }
    private ClassBrushKind? _lastClassBrushKind;

    // True when the HBRUSH currently installed as GCLP_HBRBACKGROUND
    // was allocated by CreateSolidBrush (and therefore must be
    // DeleteObject'd when replaced). False when it is a stock object
    // (e.g. NULL_BRUSH from GetStockObject) or the default brush the
    // WNDCLASS was registered with -- stock objects are owned by the
    // system and MUST NOT be deleted.
    private bool _classBrushOwned;

    // Last color written to RootGrid.Background. ApplyRootGridBackground
    // is the single source of truth for that property; this cache
    // skips allocating a new SolidColorBrush when nothing changed.
    private Windows.UI.Color? _lastRootBackground;

    // Vertical-mode title row: opaque fills over Mica so the top bar
    // matches the terminal palette instead of Fluent grey.
    private Windows.UI.Color? _lastVerticalTitleDragBg;
    private Windows.UI.Color? _lastVerticalTitleStripMirrorBg;
    private Windows.UI.Color? _lastVerticalTitleCaptionBg;
    private readonly Dictionary<TabModel, PropertyChangedEventHandler> _tabColorWired = new();

    // Last structural gradient config (points, blend, static opacity).
    // ApplyGradientTint rebuilds the SpriteVisual only when these
    // change; opacity/animation updates run in place on every reload.
    // Snapshot the points list into a private List so later config
    // reloads can't mutate what we are comparing against.
    private List<Ghostty.Services.GradientPoint>? _lastGradientPoints;
    private string? _lastGradientBlend;
    private float _lastGradientOpacity;

    // Tracks the last applied caption-button colors so we can skip
    // redundant TitleBar property writes. WinUI 3 marshals each
    // property setter to DWM separately, and rapid sequential writes
    // can cause a brief flash to the system accent color (blue) while
    // DWM is between updates.
    private readonly record struct CaptionColors(
        Windows.UI.Color? Bg,
        Windows.UI.Color? Fg,
        Windows.UI.Color? InactiveBg,
        Windows.UI.Color? InactiveFg,
        Windows.UI.Color? HoverBg,
        Windows.UI.Color? HoverFg,
        Windows.UI.Color? PressedBg,
        Windows.UI.Color? PressedFg);

    private CaptionColors _lastButtonColors;

    private GradientTintVisual? _gradientVisual;
    private Window? _aboutWindow;
    private InspectorWindow? _inspectorWindow;

    private CommandPaletteViewModel? _commandPaletteVm;
    private FrecencyStore? _frecencyStore;
    private Controls.TerminalControl? _previousFocusSurface;
    // Cold-start launch icon. Null on every window that was not opened
    // by App.OnLaunched -- a warm-process window reaches first render
    // fast enough that a splash would read as invented latency.
    private Shell.LaunchIconCoordinator? _launchIcon;
#if DEMO
    private Ghostty.Demo.DemoPlayer? _demoPlayer;
    private Ghostty.Demo.DemoOverlay? _demoOverlay;
    private Windows.UI.Input.Preview.Injection.InputInjector? _demoInjector;
    // True while a "keys" beat is injecting real keystrokes; suppresses the
    // demo's own abort/step/pause handler so injected keys aren't consumed.
    private volatile bool _demoInjecting;
#endif

    // Last libghostty-computed default window size (initial_size action), in
    // physical pixels. null until received; reset_window_size is a no-op
    // before then (mirrors core, which only emits initial_size when
    // window-width/window-height are configured).
    private Windows.Graphics.SizeInt32? _defaultWindowSizePx;

    // Remembered transparent opacity baseline for toggle_background_opacity.
    // null when not in the forced-opaque state.
    private double? _opacityToggleBaseline;

    /// <summary>
    /// Palette close state: prevents re-entrant close handling between
    /// ViewModel.PropertyChanged and Popup.Closed callbacks.
    /// </summary>
    private enum PaletteCloseState { Idle, ClosingFromCommand, ClosingFromToggle }
    private PaletteCloseState _paletteCloseState;

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

    // DeleteObject is in Win32Interop.

    // GetWindowLong, GetWindowPlacement, ShowWindow, WINDOWPLACEMENT,
    // WINDOW_STYLE, and SHOW_WINDOW_CMD are provided by CsWin32.

    // Captured from Content.XamlRoot at registration time (in the
    // one-shot Content.Loaded handler). Read by App.OnAnyWindowClosedInternal
    // on Window.Closed to remove the WindowsByRoot entry; reading
    // Content.XamlRoot directly at Closed time can return null because
    // WinUI 3 tears Content down before Closed fires.
    internal XamlRoot? RegisteredRoot { get; private set; }

    /// <summary>
    /// True when this window is the singleton quick (quake / drop-down)
    /// terminal. Quick terminals are hidden from the taskbar and Alt+Tab,
    /// and their close button hides the window instead of disposing it
    /// (so the global hotkey can resurrect it without reopening a fresh
    /// surface). The full positioning logic for quick terminals lives in
    /// <see cref="MoveToQuakePosition"/> and <see cref="ToggleVisibility"/>.
    /// </summary>
    internal bool IsQuickTerminal { get; }

    internal MainWindow(
        ConfigService configService,
        GhosttyHost bootstrapHost,
        HostLifetimeSupervisor supervisor,
        ILoggerFactory loggerFactory,
        bool isQuickTerminal = false,
        bool showLaunchIcon = false,
        ProfileSnapshot? initialSnapshot = null)
        : this(configService, bootstrapHost, supervisor, loggerFactory, seedTab: null,
               isQuickTerminal, showLaunchIcon: showLaunchIcon, initialSnapshot: initialSnapshot)
    {
    }

    /// <summary>
    /// Restore ctor: rebuild a window from a saved <paramref name="restore"/>
    /// session (tabs + split layout + geometry). Used at startup by
    /// <see cref="App"/> when session restoration is enabled.
    /// </summary>
    internal MainWindow(
        ConfigService configService,
        GhosttyHost bootstrapHost,
        HostLifetimeSupervisor supervisor,
        ILoggerFactory loggerFactory,
        Ghostty.Core.Session.WindowSession restore,
        bool showLaunchIcon = false)
        : this(configService, bootstrapHost, supervisor, loggerFactory,
               seedTab: null, isQuickTerminal: false, restore: restore,
               showLaunchIcon: showLaunchIcon)
    {
    }

    /// <summary>
    /// Full ctor. <paramref name="seedTab"/>, when non-null, is
    /// adopted as the sole initial tab (used by Move Tab to New
    /// Window); when null, the normal "create a fresh tab via the
    /// factory" path runs. <paramref name="bootstrapHost"/> is the
    /// app-owning GhosttyHost built once in App.xaml.cs; this window
    /// constructs its OWN per-window GhosttyHost from it using the
    /// shared-app ctor. <paramref name="loggerFactory"/> is the
    /// process-wide factory built in App.OnLaunched; MainWindow holds
    /// it to construct loggers for the per-window components it owns
    /// (GhosttyHost's clipboard trio, TaskbarHost, AcrylicBackdrop,
    /// ThemePreviewService).
    /// </summary>
    private MainWindow(
        ConfigService configService,
        GhosttyHost bootstrapHost,
        HostLifetimeSupervisor supervisor,
        ILoggerFactory loggerFactory,
        TabModel? seedTab,
        bool isQuickTerminal = false,
        Ghostty.Core.Session.WindowSession? restore = null,
        bool showLaunchIcon = false,
        ProfileSnapshot? initialSnapshot = null)
    {
        InitializeComponent();

#if DEMO
        // Demo mode key handling: while a demo runs, Esc aborts, Space/Right
        // step (stepped mode), P toggles pause (auto mode). handledEventsToo
        // so these are seen even if the focused terminal marks them handled.
        if (this.Content is Microsoft.UI.Xaml.UIElement demoRoot)
        {
            demoRoot.AddHandler(
                Microsoft.UI.Xaml.UIElement.KeyDownEvent,
                new Microsoft.UI.Xaml.Input.KeyEventHandler(OnDemoKeyDown),
                handledEventsToo: true);
        }

        // If the window loses focus while a demo runs, abort it. The "keys" beat
        // injects global input, so a recording that switches away should stop
        // rather than keep puppeting (and never risk input landing elsewhere).
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated
                && _demoPlayer is { IsRunning: true })
            {
                _demoPlayer.Abort();
            }
        };

        // Hands-free recording: WINTTY_DEMO_AUTOSTART=auto|stepped plays the demo
        // a few seconds after launch, so you can hit record then start the app.
        // The delay lets the first pane's shell come up before the type beats.
        var autoStart = Environment.GetEnvironmentVariable("WINTTY_DEMO_AUTOSTART");
        if (!string.IsNullOrEmpty(autoStart))
        {
            var mode = autoStart.Equals("stepped", StringComparison.OrdinalIgnoreCase)
                ? Ghostty.Core.Demo.DemoMode.Stepped
                : Ghostty.Core.Demo.DemoMode.Auto;
            var startTimer = DispatcherQueue.CreateTimer();
            startTimer.Interval = TimeSpan.FromSeconds(3);
            startTimer.IsRepeating = false;
            startTimer.Tick += (_, _) => { startTimer.Stop(); StartDemo(mode); };
            startTimer.Start();
        }
#endif

        IsQuickTerminal = isQuickTerminal;

        Ghostty.Branding.WindowHelper.TryApplyAppIcon(this);

        _configService = configService;
        _configEditor = App.ConfigFileEditor
            ?? throw new InvalidOperationException(
                "MainWindow: App.ConfigFileEditor is null. " +
                "App.OnLaunched must initialize it before constructing a window.");
        _configWriter = new SettingsConfigWriter(
            _configService, StaticLoggers.SettingsConfigWriter);
        _newAcrylicLogger = loggerFactory.CreateLogger<AcrylicBackdrop>;
        _logger = loggerFactory.CreateLogger<MainWindow>();

        // Build this window's per-window GhosttyHost around the shared
        // app. Each per-window host has its OWN per-window surface
        // dictionary; routing to this host from the bootstrap host's
        // libghostty callbacks happens via App._hostBySurface (populated
        // by the per-window host's Register / Adopt paths).
        _host = new GhosttyHost(
            DispatcherQueue,
            bootstrapHost.App.Handle,
            supervisor,
            loggerFactory);
        // NOTE: configService.SetApp is already done by App.xaml.cs on
        // the bootstrap host. We do NOT call it again here.

        // Register with App.WindowsByRoot once the XamlRoot is live.
        // We capture the XamlRoot into RegisteredRoot so the
        // App.OnAnyWindowClosedInternal handler can remove the entry on
        // Window.Closed even if Content.XamlRoot has gone null by then
        // (which WinUI 3 does during window teardown).
        //
        // The quake window is deliberately excluded from this registry.
        // WindowsByRoot.Count == 0 is the trigger for app shutdown; if
        // the quake (which lives the entire app lifetime) participated,
        // closing the last regular window would never drop the count to
        // zero and the process would never exit. App.OnLaunched closes
        // the quake explicitly when the last regular window goes away.
        if (!IsQuickTerminal && Content is FrameworkElement fe)
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

        if (!IsQuickTerminal)
        {
            Activated += (_, args) =>
            {
                if (args.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated)
                    App.NoteRegularWindowActivated(this);
            };
        }

        // Detect initial system theme and notify libghostty so conditional
        // config blocks (e.g. palette dark/light) take effect immediately.
        // The light/dark test itself lives in OsTheme so it cannot drift
        // between the callers that depend on it.
        _systemUiSettings = new Windows.UI.ViewManagement.UISettings();
        var initialDark = Ghostty.Services.OsTheme.IsDark(_systemUiSettings);
        var initialScheme = initialDark
            ? Ghostty.Interop.GhosttyColorScheme.Dark
            : Ghostty.Interop.GhosttyColorScheme.Light;
        Ghostty.Interop.NativeMethods.AppSetColorScheme(_host.App, initialScheme);
        // Closes the gap between ConfigService seeding its themed values in
        // its constructor and this window reading the scheme: a flip in
        // between would leave libghostty on the new scheme and the config
        // caches on the old one, with nothing to reconcile them until the
        // next flip. A no-op in the common case, since the service guards
        // on the scheme actually having moved.
        _configService.RefreshForOsColorScheme(initialDark);
        // NotifyColorSchemeChanged is not called here because no surfaces
        // exist yet at window construction time. Each surface picks up the
        // app-level scheme when it initializes. The runtime handler below
        // covers post-init OS theme changes.

        // Subscribe to runtime theme changes. ColorValuesChanged fires on a
        // background thread, so dispatch back to the UI thread before calling
        // into libghostty (which expects UI-thread callers for App-level ops).
        _systemUiSettings.ColorValuesChanged += (s, _) =>
        {
            // Classify the event's own sender rather than activating a
            // second UISettings, which could answer for a later moment.
            var dark = Ghostty.Services.OsTheme.IsDark(s);
            DispatcherQueue.TryEnqueue(() =>
            {
                var scheme = dark
                    ? Ghostty.Interop.GhosttyColorScheme.Dark
                    : Ghostty.Interop.GhosttyColorScheme.Light;
                Ghostty.Interop.NativeMethods.AppSetColorScheme(_host.App, scheme);
                _host.NotifyColorSchemeChanged(scheme);

                // The two calls above move libghostty onto the new scheme,
                // so the surfaces repaint. Anything the C# chrome derives
                // from a conditional theme is still resolved against the
                // outgoing one until this runs, which is what would leave
                // window chrome on the old palette beside a repainted
                // terminal. Self-guarding, so the per-window duplicates of
                // this handler collapse to one refresh. Handed the same
                // `dark` that drove the two calls above, so libghostty and
                // the config caches cannot describe different schemes.
                _configService.RefreshForOsColorScheme(dark);
            });
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

        // Set here rather than in XAML: Window is not a DependencyObject,
        // so no markup extension can target Title at all, and AppIdentity
        // is internal besides, which x:Bind's generated code would need
        // public. Same constraint CommandPaletteControl documents.
        //
        // TitleBarCoordinator takes the caption over further down this
        // constructor, from the active tab's EffectiveTitle, which falls
        // back to this same constant. So this is the value only until
        // then, and the two agree.
        Title = Ghostty.Core.AppIdentity.ProductName;

        // Apply window-theme from config. The manager resolves the
        // config value ("light"/"dark"/"system"/"auto") to a concrete
        // dark/light choice and sets both ElementTheme on the XAML root
        // and the DWM immersive dark mode attribute for the title bar
        // caption buttons (which XAML cannot control when
        // ExtendsContentIntoTitleBar is true).
        _themeManager = new WindowThemeManager(configService, DispatcherQueue);
        ApplyTheme();
        _themeManager.ThemeChanged += OnWindowThemeChanged;

        _shellTheme = new ShellThemeService(configService);
        _shellTheme.ThemeChanged += OnShellThemeChanged;
        _themePreview = new ThemePreviewService(
            configService,
            DispatcherQueue,
            loggerFactory.CreateLogger<ThemePreviewService>());
        _themePreview.ListThemesRequested += OnListThemesRequested;

        _factory = new PaneHostFactory(_host, configService);
        // Restore a saved session into this window when one was passed and
        // it rebuilds at least one tab; otherwise fall through to the normal
        // "fresh tab (or seedTab) via the factory" path.
        List<TabModel>? restoredTabs = null;
        if (restore is { Tabs.Count: > 0 })
        {
            var restorer = new Ghostty.Session.SessionRestorer(_factory, App.ProfileRegistry);
            var built = restorer.BuildTabs(restore);
            if (built.Count > 0) restoredTabs = built;
        }

        if (restoredTabs is not null)
        {
            _tabManager = new TabManager(
                snapshot => _factory.Create(snapshot),
                seed: restoredTabs[0],
                closedTabs: ClosedTabsStore);
            for (int i = 1; i < restoredTabs.Count; i++)
                _tabManager.AdoptTab(restoredTabs[i]);
            if (restore!.ActiveTabIndex >= 0 && restore.ActiveTabIndex < restoredTabs.Count)
                _tabManager.ActivateIndex(restore.ActiveTabIndex);
        }
        else
        {
            // Fresh window (no restore, no adopted tab): honor an
            // explicit snapshot (jump-list new window) or default-profile.
            // Must reach TabManager's factory -- attaching after spawn
            // does not update TerminalControl.Snapshot before OnLoaded.
            var snap = seedTab is null
                ? (initialSnapshot
                    ?? Ghostty.Core.Session.SessionProfileResolver.ResolveDefault(
                        App.ProfileRegistry))
                : null;
            _tabManager = new TabManager(
                snapshot => _factory.Create(snapshot),
                seed: seedTab,
                closedTabs: ClosedTabsStore,
                initialSnapshot: snap);
        }
        _router = new PaneActionRouter(
            _tabManager,
            getProfiles: () => App.ProfileRegistry?.Profiles ?? EmptyProfiles,
            openProfile: OpenProfile,
            bindingAction: ExecuteBindingAction);
        _windowState = WindowState.Load();
        // Apply the restored window geometry when restoring; otherwise use
        // the window-state.json fallback placement.
        if (restoredTabs is not null)
            ApplyGeometry(restore!.Geometry);
        else
            RestoreWindowPlacement();

        // Hand the splash this window's real rect the moment it has one,
        // rather than after the panes are built. Up to here the splash is on
        // a rect it resolved from disk before this process had a window at
        // all, and only this window knows which of the two saved sources
        // actually won -- or that both were rejected and the OS placed it.
        // Tracking from here also keeps the two together if the user drags or
        // resizes the window before the splash comes down.
        if (showLaunchIcon)
            Shell.SplashWindow.Track(WindowNative.GetWindowHandle(this));

        _horizontalTabHost = new TabHost(_tabManager, _router, _dialogs);
        _horizontalTabHost.AttachOwner(this);
        _verticalTabHost = new VerticalTabHost(_tabManager, _router, _dialogs, _host);
        _verticalTabHost.AttachOwner(this);
        _verticalTabHost.SetPrimaryIconBadge(VerticalIconBadgeHost);

        // Place both tab hosts in their RootGrid slots. The
        // horizontal host spans both columns in row 0 so its TabView
        // strip can grow under the title bar area; the vertical host
        // anchors at col 0 and spans both rows.
        // Both tab hosts are inserted at the back of the Z-order so
        // the XAML-declared VerticalTitleBar stays on top in the Row 0
        // overlap region. Without this, the expanded vertical strip
        // covers the title bar. The VerticalTitleBar's
        // Background="Transparent" already enables hit-testing for its
        // drag region.
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

        // Apply initial shell theme now that tab hosts exist, then
        // paint RootGrid.Background from the resolved state.
        ApplyShellTheme();
        ApplyRootGridBackground();
        RefreshPowerSaverIcon();

        // Bind the generic notice surface to the app-wide queue. Renders any
        // notice already raised at startup (e.g. the NO_COLOR notice). The quake
        // window shares the queue too; a transient notice showing there is
        // harmless and it stays hidden until summoned.
        if (Ghostty.App.NotificationService is { } notifications)
            NotificationHost.Attach(notifications);

        // Parent every existing and future PaneHost into the shared
        // container declared in MainWindow.xaml. This is the single
        // owner for PaneHost lifetime in the visual tree — both tab
        // hosts read from it without ever reparenting.
        foreach (var t in _tabManager.Tabs)
        {
            AddPaneHost(t);
            // The seed tab does NOT raise TabManager.TabAdded (see
            // TabManager ctor comment), so attach the process-tracker
            // bridge here for every pre-existing tab before subscribing
            // to TabAdded below. AttachProcessTracking is idempotent
            // against TabAdded re-firing for the same tab.
            AttachProcessTracking(t);
        }
        SwapActivePane();

        // Cold start: the pre-XAML splash is already covering this window's
        // rect. It comes down on our first composed frame, which is when
        // this window stops painting black and has something real to show.
        if (showLaunchIcon)
        {
            _launchIcon = new Shell.LaunchIconCoordinator(DispatcherQueue);
            _launchIcon.Arm();

            // The active tab, not tab zero: a restored session can make any
            // tab active, and a background tab never presents, so waiting on
            // tab zero's surface would mean waiting for a first render that
            // does not arrive until the user switches to it.
            if (_tabManager.ActiveTab.PaneHost is Panes.PaneHost seedHost)
            {
                seedHost.ActiveLeaf.Terminal().FirstRender += OnLaunchSurfaceFirstRender;
            }

            // Record the resolved background for the next launch's splash
            // now, rather than only on close. A session that is force-killed
            // or crashes never runs the close path, and the splash would
            // then keep falling back to the built-in default and flash a
            // mismatched colour on every subsequent start.
            var splashBackground = _configService.BackgroundColor & 0x00FFFFFFu;
            if (_windowState.BackgroundRgb != splashBackground)
            {
                _windowState.BackgroundRgb = splashBackground;
                _windowState.Save();
            }
        }

        _tabManager.TabAdded += (_, t) =>
        {
            WireTabColor(t);
            AddPaneHost(t);
            AttachProcessTracking(t);
            SwapActivePane();
            ApplyPerTabChrome();
        };
        _tabManager.TabRemoved += (_, t) =>
        {
            UnwireTabColor(t);
            DetachProcessTracking(t);
            RemovePaneHost(t);
        };
        _tabManager.ActiveTabChanged += (_, _) =>
        {
            SwapActivePane();
            ApplyPerTabChrome();
        };

        foreach (var tab in _tabManager.Tabs)
            WireTabColor(tab);

        // Apply initial cursor-color-derived pane border + tab chrome.
        ApplyPerTabChrome();

        _verticalTabsVisible = _configService.VerticalTabs;
        _tabHost = _verticalTabsVisible ? _verticalTabHost : _horizontalTabHost;

        // Make TabChromeMetrics authoritative rather than aspirational: the
        // XAML values are design-time defaults, and a comment claiming they
        // match the constant is exactly how the two drift apart.
        VerticalTitleBar.Height = Shell.TabChromeMetrics.TitleRowHeight;
        VerticalCaptionSeamCover.Margin = new Thickness(
            0, Shell.TabChromeMetrics.TitleRowHeight - 2, 0, 0);

        _layout = new LayoutCoordinator(
            StripColumn,
            TitleBarStripMirror,
            (FrameworkElement)_horizontalTabHost.HostElement,
            _verticalTabHost,
            VerticalTitleBar,
            _horizontalTabHost.IconBadge);
        _layout.Snap(_verticalTabsVisible);
        ApplyVerticalTitleBarChrome();
        ApplyCaptionButtonChrome();
        if (_verticalTabsVisible)
            _verticalTabHost.SyncSelectionFromManager();

        _titleBar = new TitleBarCoordinator(
            this,
            _tabManager,
            _horizontalTabHost,
            _verticalTabHost,
            VerticalTitleDragRegion,
            VerticalTitleText,
            VerticalCaptionInset,
            VerticalCaptionSeamCover,
            isVerticalMode: () => _tabHost is VerticalTabHost);
        _titleBar.ApplyForCurrentMode();
        _titleBar.SyncCaptionInset();

        _taskbar = new TaskbarHost(this, _tabManager, loggerFactory.CreateLogger<TaskbarHost>());

        AppWindow.Changed += (_, args) =>
        {
            _taskbar.OnAppWindowChanged(AppWindow);
            _titleBar.SyncCaptionInset();
            if (IsQuickTerminal && args.DidSizeChange && !_movingQuake && AppWindow.IsVisible)
            {
                _quakeSessionHeight = AppWindow.Size.Height;
            }
            if (!IsQuickTerminal && (args.DidPositionChange || args.DidSizeChange))
                PositionChanged?.Invoke(this, EventArgs.Empty);
        };

        WirePaneActionEvents();

        _commandPaletteVm = CreateCommandPaletteViewModel();
        CommandPaletteUI.Configure(_configService);
        CommandPaletteUI.Bind(_commandPaletteVm);
        CommandPaletteUI.ApplySettings(_configService.CommandPaletteBackground);

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

        _host.InspectorToggleRequested += (_, _) =>
            DispatcherQueue.TryEnqueue(ToggleInspector);

        // Pane/tab keybinds libghostty matched arrive already mapped to a
        // PaneAction (and already hopped to the UI thread by the host);
        // forward straight into the router that owns the pane/tab state.
        _host.PaneActionRequested += action => _router.Invoke(action);

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
                    // One settings window serves the whole process; App owns
                    // the singleton (mirrors the quake window), so opening it
                    // from any window reuses the same instance.
                    ((App)Application.Current).ShowOrActivateSettings();
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
                    _logger.LogConfigOpenFailed(ex);
                }
            };

            appHost.ReloadConfigRequested += (_, _) => configService.Reload();
        }

        // Ctrl+Shift+Scroll wheel opacity adjustment from any terminal surface.
        _host.OpacityAdjustRequested += (_, direction) => AdjustOpacity(direction);

        // Window-level keybind actions routed through the per-window router.
        // These are per-window events, so every window wires them.
        _router.GotoWindowRequested += (_, dir) =>
            ((App)Application.Current).ActivateRelativeWindow(this, dir);
        _router.ResetWindowSizeRequested += (_, _) => ResetWindowSize();
        _router.ToggleBackgroundOpacityRequested += (_, _) => ToggleBackgroundOpacity();
        _router.FloatWindowRequested += (_, mode) => ApplyFloat(mode);

        // Surface-targeted window actions raised on the per-window host.
        _host.SizeLimitRequested += (_, lim) => ApplySizeLimit(lim);
        _host.SetTabTitleRequested += (_, title) =>
            _tabManager.ActiveTab.UserOverrideTitle = string.IsNullOrWhiteSpace(title) ? null : title;
        // The host already raises these on the UI thread (OnAction dispatches
        // before invoking), so no extra hop is needed here. The dialog helper
        // is fire-and-forget and self-contains its exception handling.
        _host.PromptTitleRequested += (isTab, control) => _ = ShowPromptTitleDialogAsync(isTab, control);
        _host.PresentSurfaceRequested += PresentSurface;
        _host.InitialSizeReceived += (w, h) =>
            _defaultWindowSizePx = new Windows.Graphics.SizeInt32((int)w, (int)h);

        // Re-evaluate transparency state after every config reload so
        // Ctrl+Shift+Scroll and Settings UI changes take effect live.
        // Each step owns exactly one piece of chrome state:
        //   - ApplyBackdropStyle: SystemBackdrop + Win32 class brush
        //   - UpdateAcrylicTuning: live acrylic tint/opacity (in place)
        //   - ApplyGradientTint:   gradient visual
        //   - UpdateCursorAccentColors: pane borders + tab accents
        //   - ApplyShellTheme:     caption buttons + tab host theming
        //   - ApplyRootGridBackground: RootGrid.Background
        // Keeping these disjoint prevents any step from piggybacking
        // on another's side effects (the original cause of # 239).
        _configService.ConfigChanged += OnConfigReloadedChrome;

        // Re-evaluate the gradient and other power-gated effects whenever
        // low-power mode toggles. MainWindow runs on the UI thread so we
        // marshal back explicitly; the monitor fires on the thread-pool
        // after its 150ms debounce.
        if (Ghostty.App.PowerStateMonitor is { } powerMonitor)
        {
            powerMonitor.LowPowerChanged += OnLowPowerChanged;
        }

        _tabManager.LastTabClosed += (_, _) => Close();

        // Settings page raises this the moment the user flips the
        // vertical-tabs toggle so we animate without waiting for the
        // debounced write + ConfigChanged round-trip. The config
        // reload handler below runs the same animation for external
        // edits (raw editor save, file-watcher, external config
        // change); the _verticalTabsVisible mirror prevents double
        // animation when both paths observe the same transition.
        Ghostty.Settings.Pages.GeneralPage.VerticalTabsToggled
            += OnVerticalTabsToggledFromSettings;
        _configService.ConfigChanged += OnConfigReloaded;

        if (IsQuickTerminal)
            ApplyQuickTerminalBehaviour();

        // Kill the Win32 Alt-menu beep that the Alt+Shift split chords
        // would otherwise ring on every press. The input-site child HWND
        // that actually receives WM_SYSCHAR is created when the content
        // island is realized, so install on Activated (idempotent; it
        // retries until the input site exists, then unsubscribes).
        _beepSuppressor = new SysCharBeepSuppressor();
        Activated += OnActivatedInstallBeepSuppressor;

        Closed += OnClosedAsync;
    }

    private void OnActivatedInstallBeepSuppressor(object sender, WindowActivatedEventArgs args)
    {
        if (_beepSuppressor is null)
        {
            Activated -= OnActivatedInstallBeepSuppressor;
            return;
        }

        if (_beepSuppressor.Install(WindowNative.GetWindowHandle(this)) > 0)
            Activated -= OnActivatedInstallBeepSuppressor;
    }

    private void OnVerticalTabsToggledFromSettings(bool vertical)
    {
        if (_verticalTabsVisible == vertical) return;
        AnimateTabLayoutTo(vertical);
    }

    private void OnConfigReloaded(IConfigService cfg)
    {
        // Re-apply the command-palette backdrop live. Group-by and
        // ViewModel-construction-time settings are picked up the next
        // time the palette is instantiated, so nothing to do here for
        // CommandPaletteGroupCommands.
        CommandPaletteUI.ApplySettings(cfg.CommandPaletteBackground);

        var vertical = cfg.VerticalTabs;
        if (_verticalTabsVisible == vertical) return;
        AnimateTabLayoutTo(vertical);
    }

    private void AnimateTabLayoutTo(bool vertical)
    {
        if (_layout.IsSwitching) return;
        _verticalTabsVisible = vertical;
        _tabHost = vertical ? _verticalTabHost : _horizontalTabHost;
        // Paint caption/title chrome before the cross-fade so the OS
        // buttons and drag row do not flash stale horizontal colors.
        ApplyVerticalTitleBarChrome();
        ApplyCaptionButtonChrome();
        _titleBar.ApplyForCurrentMode();
        _layout.Animate(vertical, onCompleted: () =>
        {
            RefreshTabHostChrome();
            if (vertical)
                _verticalTabHost.SyncSelectionFromManager();
            _titleBar.ApplyForCurrentMode();
            var leaf = _tabManager.ActiveTab?.PaneHost?.ActiveLeaf;
            if (leaf is not null)
                leaf.Terminal().Focus(FocusState.Programmatic);
        });
    }

    /// <summary>
    /// Build a <see cref="MainWindow"/> that adopts an existing
    /// <see cref="TabModel"/> as its sole initial tab, WITHOUT
    /// activating. Caller is responsible for positioning the window
    /// (via <see cref="Microsoft.UI.Windowing.AppWindow.MoveAndResize"/>
    /// or the like) and then calling <see cref="Window.Activate"/>.
    ///
    /// Used today by <see cref="DetachTabToNewWindow"/> for cursor-
    /// anchored placement. Snap Layouts will call this same factory to
    /// install a snap rect before first activation so there is no
    /// visible placement flicker.
    /// </summary>
    internal static MainWindow CreateForAdoption(
        ConfigService configService,
        GhosttyHost bootstrapHost,
        HostLifetimeSupervisor supervisor,
        ILoggerFactory loggerFactory,
        TabModel adoptedTab)
    {
        return new MainWindow(
            configService, bootstrapHost, supervisor, loggerFactory,
            seedTab: adoptedTab, isQuickTerminal: false);
    }

    /// <summary>
    /// Build a <see cref="MainWindow"/> that opens with a single fresh
    /// tab seeded from <paramref name="initialSnapshot"/>. Used by
    /// <see cref="OpenProfile"/> when target is
    /// <see cref="ProfileLaunchTarget.NewWindow"/>. Mirrors the
    /// <see cref="CreateForAdoption"/> shape (same dependencies, no
    /// pre-activation flicker) but takes a <see cref="ProfileSnapshot"/>
    /// instead of an existing <see cref="TabModel"/> -- the new window
    /// owns its own tab from creation.
    /// </summary>
    internal static MainWindow CreateForNewTab(
        ConfigService configService,
        GhosttyHost bootstrapHost,
        HostLifetimeSupervisor supervisor,
        ILoggerFactory loggerFactory,
        ProfileSnapshot? initialSnapshot)
    {
        var window = new MainWindow(
            configService, bootstrapHost, supervisor, loggerFactory,
            isQuickTerminal: false,
            initialSnapshot: initialSnapshot);
        return window;
    }

    /// <summary>
    /// Pre-activation hook for Snap Layouts placement. The detach flow
    /// constructs a MainWindow but MUST NOT call Activate until any snap
    /// target has been applied, otherwise the window flashes at the
    /// wrong origin. Call this once placement is done.
    /// </summary>
    internal void ActivateAfterPlacement() => Activate();

    /// <summary>
    /// Bring the surface a toast was raised for back in front of the user.
    /// Returns false when no pane in this window carries
    /// <paramref name="surfaceKey"/>, so the caller can try the next window
    /// and ultimately fall back to plain activation.
    ///
    /// Keyed on <see cref="TerminalControl.ToastSurfaceKey"/> -- the same key
    /// the toast was grouped under and the same one focus-regain clears by --
    /// rather than the native surface handle, which can be recycled onto a
    /// different surface between raising a toast and clicking it.
    /// UI-thread only.
    /// </summary>
    internal bool TryFocusToastSurface(string surfaceKey)
    {
        foreach (var tab in _tabManager.Tabs)
        {
            var paneHost = (PaneHost)tab.PaneHost;
            foreach (var leaf in PaneTree.Leaves(paneHost.RootNode))
            {
                var terminal = leaf.Terminal();
                if (!string.Equals(terminal.ToastSurfaceKey, surfaceKey, StringComparison.Ordinal))
                    continue;

                // Tab first, then window, then pane: selecting the tab after
                // activating would show the user the old tab for a frame, and
                // focusing the pane before its tab is on screen puts focus on
                // a collapsed PaneHost.
                _tabManager.Activate(tab);
                if (!AppWindow.IsVisible) AppWindow.Show();
                Activate();
                terminal.Focus(FocusState.Programmatic);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Jump-list / single-instance "New Tab". Resolves
    /// <paramref name="profileId"/> (or the registry default) when
    /// possible; otherwise opens a tab with no snapshot. Must never
    /// no-op: the caller already decided a tab belongs on this window.
    /// </summary>
    internal void OpenJumpListTab(string? profileId)
    {
        var registry = App.ProfileRegistry;
        var id = profileId ?? registry?.DefaultProfileId;
        if (id is not null && registry is not null
            && (registry.Resolve(id)
                ?? (registry.DefaultProfileId is { } d
                    ? registry.Resolve(d)
                    : null)) is not null)
        {
            OpenProfile(id, ProfileLaunchTarget.NewTab);
            return;
        }

        _tabManager.NewTab();
    }

    /// <summary>
    /// Single funnel for the new-tab split button and the
    /// command-palette profile rows. Resolves
    /// <paramref name="profileId"/> against the registry, falling back
    /// to the registry's <c>DefaultProfileId</c> when the requested id
    /// is missing. Logs and returns when both lookups fail (cold-start
    /// empty-registry case -- the no-arg <c>TabManager.NewTab()</c>
    /// fallback path remains usable from other call sites).
    /// </summary>
    internal void OpenProfile(string profileId, ProfileLaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(profileId);

        var registry = App.ProfileRegistry;
        if (registry is null)
        {
            _logger.LogWarning("OpenProfile invoked before ProfileRegistry was wired.");
            return;
        }

        var resolved = registry.Resolve(profileId)
            ?? (registry.DefaultProfileId is { } d ? registry.Resolve(d) : null);
        if (resolved is null)
        {
            _logger.LogWarning(
                "OpenProfile: id '{ProfileId}' not found and no default available.",
                profileId);
            return;
        }

        var snapshot = ProfileSnapshotStore.From(resolved, registry.Version);

        switch (target)
        {
            case ProfileLaunchTarget.NewTab:
                _tabManager.NewTab(snapshot);
                break;
            case ProfileLaunchTarget.NewPane:
                _tabManager.ActiveTab.PaneHost.Split(
                    PaneOrientation.Horizontal, snapshot);
                break;
            case ProfileLaunchTarget.NewWindow:
                OpenInNewWindow(snapshot);
                break;
        }
    }

    private void OpenInNewWindow(ProfileSnapshot snapshot)
    {
        var bootstrap = App.BootstrapHost
            ?? throw new InvalidOperationException(
                "OpenInNewWindow: no bootstrap host; App.OnLaunched did not run.");
        var supervisor = App.LifetimeSupervisor
            ?? throw new InvalidOperationException(
                "OpenInNewWindow: no lifetime supervisor; App.OnLaunched did not run.");
        var loggerFactory = App.LoggerFactory
            ?? throw new InvalidOperationException(
                "OpenInNewWindow: no logger factory; App.OnLaunched did not run.");

        var newWindow = CreateForNewTab(
            _configService, bootstrap, supervisor, loggerFactory, snapshot);
        newWindow.Closed += ((App)Application.Current).OnAnyWindowClosedInternal;
        // Track for session persistence (mirrors App.OnLaunched) so a window
        // opened mid-session is captured and restored.
        App.SessionManager?.Track(newWindow);
        App.SessionManager?.RequestPersist();
        newWindow.Activate();
    }

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
        // Same App.* static-read shape as the bootstrap/supervisor
        // pulls just above: the detach path is an App-level concern
        // (moves a tab between windows that the App owns), so reaching
        // into App for the process-wide factory is consistent.
        var factory = App.LoggerFactory
            ?? throw new InvalidOperationException(
                "DetachTabToWindow: no logger factory; App.OnLaunched did not run.");
        var newWindow = MainWindow.CreateForAdoption(
            _configService, bootstrap, supervisor, factory, detached);
        var newHost = newWindow._host;
        ((Panes.PaneHost)detached.PaneHost).RehostTo(newHost);

        placementAction(newWindow);

        // Subscribe the new window to the process-wide last-window-exit
        // handler. WindowsByRoot insertion happens inside the new
        // window's own Content.Loaded handler.
        newWindow.Closed += ((App)Application.Current).OnAnyWindowClosedInternal;
        // Track for session persistence so a detached-to-new-window tab is
        // captured and restored.
        App.SessionManager?.Track(newWindow);
        App.SessionManager?.RequestPersist();

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
        // Stop theme application before any teardown or await.
        // WindowThemeManager routes ConfigChanged/ColorValuesChanged
        // through the dispatcher, so a switch-then-close can leave an
        // ApplyTheme queued to run mid-teardown against a dead XamlRoot/HWND
        // (issue #208). Set the gate and dispose the manager synchronously,
        // before the first await below, so no theme callback fires after
        // teardown begins.
        _isClosed = true;
        _themeManager.Dispose();

        // Close the inspector window before any surface/host teardown below.
        // Its present timer drives libghostty against the bound surface every
        // frame; closing it now runs its shutdown (stop timer + tear down the
        // swap chain) while the surface and DX12 device are still valid,
        // avoiding a use-after-free.
        _inspectorWindow?.Close();
        _inspectorWindow = null;

        // If this is the last regular window the app is exiting: the
        // bootstrap libghostty app and the DX12 renderer are about to be
        // freed (here via _host.Dispose below, and in
        // App.OnAnyWindowClosedInternal via the bootstrap host). Stop config
        // reloads NOW, before any of that, so a debounced reload from a
        // last-moment window-theme switch can't run AppUpdateConfig into
        // freed surfaces/app (issue #208). OnClosedAsync runs before
        // OnAnyWindowClosedInternal, so suppressing here closes the window
        // that the later call would miss. The last-window guard keeps
        // auto-reload working for other windows in a multi-window session;
        // App.OnAnyWindowClosedInternal still calls BeginShutdown as the
        // definitive backstop (it is idempotent).
        if (!IsQuickTerminal && Ghostty.App.WindowsByRoot.Count <= 1)
            _configService.BeginShutdown();

        // Detach from process-global event sources before we tear
        // down the dispatcher-bound state below. The ConfigService
        // outlives individual MainWindows, so a lingering subscription
        // would fire on a dead XamlRoot on the next reload. The
        // static settings-page event has the same lifetime problem
        // (multiple windows / detached tabs each leave one dangling
        // entry if not explicitly removed).
        Ghostty.Settings.Pages.GeneralPage.VerticalTabsToggled
            -= OnVerticalTabsToggledFromSettings;
        _configService.ConfigChanged -= OnConfigReloaded;
        _configService.ConfigChanged -= OnConfigReloadedChrome;
        _shellTheme.ThemeChanged -= OnShellThemeChanged;

        // CompositionTarget.Rendering is static, so a window closed before
        // its first composed frame would otherwise stay subscribed.
        _launchIcon?.Cancel();
        _launchIcon = null;
        if (Ghostty.App.PowerStateMonitor is { } powerMonitor)
        {
            powerMonitor.LowPowerChanged -= OnLowPowerChanged;
        }

        if (IsQuickTerminal)
        {
            // Remove the window-proc subclass before the HWND is torn down.
            _quakeFrame?.Dispose();
            _quakeFrame = null;

            // Persist only the session-resized height, via read-modify-write
            // so we never clobber the regular window placement other windows
            // save. The quake window's X/Y/Width are config-driven
            // (MoveToQuakePosition), so its geometry must NOT become the
            // restore placement.
            var quakeState = WindowState.Load();
            quakeState.QuakeHeight = _quakeSessionHeight;
            quakeState.Save();
        }
        else if (AppWindow.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
        {
            // Keep writing window-state.json for normal windows: it is the
            // geometry fallback used by RestoreWindowPlacement when session
            // restoration is off (window-save-state=never) or no session is
            // restored. The session file carries geometry for the restore path.
            var g = CaptureGeometry();
            _windowState.WindowMaximized = g.Maximized;
            _windowState.WindowX = g.X;
            _windowState.WindowY = g.Y;
            _windowState.WindowWidth = g.Width;
            _windowState.WindowHeight = g.Height;
            // Carried purely for the next cold start's splash, which runs
            // before any theme has been resolved and would otherwise have
            // to guess this colour.
            _windowState.BackgroundRgb = _configService.BackgroundColor & 0x00FFFFFFu;
            _windowState.Save();
        }

        // The settings window is a single app-wide instance owned by App.
        // Close it only when this is the last window closing -- closing it on
        // any window's teardown would yank it away while other windows are
        // still open. WindowsByRoot.Count <= 1 is the same "last regular
        // window" signal used above for config shutdown.
        if (Ghostty.App.WindowsByRoot.Count <= 1)
            ((App)Application.Current).CloseSettingsWindow();

        // Close About window if open.
        _aboutWindow?.Close();
        _aboutWindow = null;

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
            _logger.LogDialogDrainFailed(ex);
        }

        _gradientVisual?.Dispose();
        _gradientVisual = null;
        _slideAnimator?.Dispose();
        _taskbar.Dispose();
        // _themeManager was disposed at the top of this method, before the
        // first await, so no theme callback can fire mid-teardown (#208).

        // Capture this window for reopen-closed-window before the panes are
        // freed. CaptureSession returns null for quake/fullscreen; skip empty
        // windows (a window emptied by closing its tabs one-by-one has nothing
        // to restore — those tabs were captured individually as closed tabs).
        var closedWindow = CaptureSession();
        if (closedWindow is { Tabs.Count: > 0 })
            App.ClosedWindows.Push(closedWindow);

        // Surface lifetime is decoupled from Loaded/Unloaded
        // (see TerminalControl.DisposeSurface), so we have to
        // free every leaf in every tab explicitly before tearing
        // down the libghostty host.
        foreach (var t in _tabManager.Tabs) t.PaneHost.DisposeAllLeaves();
        _host.Dispose();

        // Restore the original WndProc before the HWND is destroyed.
        _beepSuppressor?.Dispose();
        _beepSuppressor = null;
    }

    private void AddPaneHost(TabModel tab)
    {
        var paneHost = (PaneHost)tab.PaneHost;
        paneHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        paneHost.VerticalAlignment = VerticalAlignment.Stretch;
        paneHost.Visibility = Visibility.Collapsed;
        PaneHostContainer.Children.Add(paneHost);
        paneHost.ContextMenuRequested += OnPaneContextMenuRequested;
    }

    private void RemovePaneHost(TabModel tab)
    {
        var paneHost = (PaneHost)tab.PaneHost;
        paneHost.ContextMenuRequested -= OnPaneContextMenuRequested;
        PaneHostContainer.Children.Remove(paneHost);
    }

    private void OnPaneContextMenuRequested(object? sender, Panes.PaneContextMenuRequest request)
    {
        var control = request.Control;

        // Focus the right-clicked surface so the menu's binding/pane actions
        // (which target the active surface / active pane) act on this pane.
        control.Focus(FocusState.Programmatic);

        // Use the PaneHost that raised the event (the sender) so the Zoom state
        // reflects the right-clicked pane directly, without depending on the
        // (async) focus change having settled ActiveTab.
        var paneHost = sender as Panes.PaneHost
            ?? (Panes.PaneHost)_tabManager.ActiveTab.PaneHost;

        var flyout = PaneContextMenuBuilder.Build(
            invokePaneAction: _router.Invoke,
            invokeBindingAction: ExecuteBindingAction,
            hasSelection: () => control.HasSelection,
            isZoomed: () => paneHost.IsZoomed,
            promptTabTitle: () => _ = ShowPromptTitleDialogAsync(isTab: true, control),
            promptTerminalTitle: () => _ = ShowPromptTitleDialogAsync(isTab: false, control));

        if (request.Position is { } pos)
            flyout.ShowAt(control, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = pos });
        else
            flyout.ShowAt(control);
    }

    /// <summary>
    /// Register <paramref name="tab"/> with the process-global tracker
    /// and arrange for its <see cref="TabModel.ShellPid"/> to be
    /// populated once libghostty has spawned the surface's shell. Hook
    /// PaneHost.LeafFocused (the first fire lands from PaneHost.Loaded,
    /// at which point the TerminalControl's libghostty surface exists)
    /// and query <c>ghostty_surface_foreground_pid</c>. The first leaf's
    /// surface drives the tracker root; we keep the first-spawned shell
    /// as the lineage the user originally opened (e.g. a "Bash" tab
    /// whose split panes spawn PowerShell shouldn't re-root the icon
    /// onto pwsh).
    ///
    /// Race: libghostty's <c>ghostty_surface_foreground_pid</c> returns
    /// 0 between surface init and the Termio thread completing
    /// <c>CreateProcessW</c> for the spawned shell. LeafFocused is a
    /// one-shot snapshot (focus does not change again on a cold start
    /// with a single pane), so a single failed query would leave the
    /// tab pid-less forever. We absorb the race by polling
    /// <c>TryGetShellPid</c> every 500 ms for up to 10 s once a leaf
    /// has been focused, stopping on the first non-null result.
    /// </summary>
    // 500 ms tick × 20 ticks = 10 s budget. Surfaced as named constants so
    // the interval / budget relationship is legible at a glance and a future
    // adjustment doesn't have to keep them in sync mentally.
    private static readonly TimeSpan ShellPidPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ShellPidPollBudget = TimeSpan.FromSeconds(10);

    private void AttachProcessTracking(TabModel tab)
    {
        ((App)Application.Current).RegisterTabForProcessTracking(tab);

        Microsoft.UI.Dispatching.DispatcherQueueTimer? pidPoll = null;
        int pollTicks = 0;
        int maxPollTicks = (int)(ShellPidPollBudget.TotalMilliseconds / ShellPidPollInterval.TotalMilliseconds);

        void TryAssignPid(Ghostty.Core.Panes.LeafPane leaf)
        {
            if (tab.ShellPid is not null) return;
            var pid = leaf.Terminal().TryGetShellPid();
            if (pid is null) return;
            tab.ShellPid = pid;
            // Stop polling once we have it.
            pidPoll?.Stop();
            pidPoll = null;
        }

        void StartPollingFor(Ghostty.Core.Panes.LeafPane leaf)
        {
            if (pidPoll is not null) return;       // already polling
            if (tab.ShellPid is not null) return;  // race-won by the immediate path
            pidPoll = DispatcherQueue.CreateTimer();
            pidPoll.Interval = ShellPidPollInterval;
            pidPoll.Tick += (_, _) =>
            {
                pollTicks++;
                TryAssignPid(leaf);
                if (tab.ShellPid is not null)
                {
                    pidPoll?.Stop();
                    pidPoll = null;
                    return;
                }
                if (pollTicks >= maxPollTicks)
                {
                    // Budget exhausted without a pid. Most likely cause is a
                    // libghostty regression where ghostty_surface_foreground_pid
                    // never publishes a non-zero value, leaving the active-shell
                    // icon stuck on the profile glyph. Logged so the regression
                    // is visible without attaching a debugger.
                    _logger.LogWarning(
                        "Active-shell tracker: shell pid never resolved for tab {TabId} within {BudgetMs} ms",
                        tab.Id, (int)ShellPidPollBudget.TotalMilliseconds);
                    pidPoll?.Stop();
                    pidPoll = null;
                }
            };
            pidPoll.Start();
        }

        void OnLeafFocused(object? _, Ghostty.Core.Panes.LeafPane leaf)
        {
            // Immediate attempt first (fast path: libghostty already done
            // with CreateProcessW by the time the leaf gains focus).
            TryAssignPid(leaf);
            // If still null, start (or keep) polling until libghostty
            // catches up or we hit the 10 s budget.
            if (tab.ShellPid is null) StartPollingFor(leaf);
        }
        tab.PaneHost.LeafFocused += OnLeafFocused;
        // Stash the unsubscriber so DetachProcessTracking can walk back
        // the LeafFocused handler without a side dictionary, and stop
        // any in-flight poll when the tab is removed before we resolved.
        _processTrackingDetach[tab] = () =>
        {
            tab.PaneHost.LeafFocused -= OnLeafFocused;
            pidPoll?.Stop();
            pidPoll = null;
        };
    }

    private void DetachProcessTracking(TabModel tab)
    {
        if (_processTrackingDetach.TryGetValue(tab, out var detach))
        {
            detach();
            _processTrackingDetach.Remove(tab);
        }
        ((App)Application.Current).UnregisterTabForProcessTracking(tab);
    }

    // Per-tab cleanup map for the LeafFocused subscription installed by
    // AttachProcessTracking. TabModel is unique per tab so reference
    // equality is the natural key.
    private readonly Dictionary<TabModel, Action> _processTrackingDetach = new();

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
    /// the shared debounced config writer so it survives the next
    /// launch after the standard reload pipeline picks the key up.
    /// </summary>
    internal void ToggleTabLayout()
    {
        if (_layout.IsSwitching) return;
        var toVertical = !_verticalTabsVisible;

        // Persist through the shared debounced scheduler so rapid
        // toggling (Ctrl+Shift+, held down, or the context-menu
        // sibling firing at the same time as the settings toggle)
        // still resolves to one disk write with the last value.
        Ghostty.App.ConfigWriteScheduler?.Schedule(
            "vertical-tabs", toVertical ? "true" : "false");

        AnimateTabLayoutTo(toVertical);
    }

    /// <summary>
    /// Wire the per-window <see cref="PaneActionRouter"/> events to their
    /// handlers. libghostty matches every standard chord and the
    /// Windows-only residual matcher in <see cref="Controls.TerminalControl"/>
    /// handles the rest; both feed <see cref="PaneActionRouter.Invoke"/>,
    /// which raises the events subscribed below.
    ///
    /// No <c>KeyboardAccelerator</c>s are registered. They were the source
    /// of the double-dispatch in https://github.com/deblasis/ghostty/issues/165
    /// -- WinUI 3 fires Invoked twice for an accelerator registered on a
    /// parent of the focused element, even with args.Handled and ScopeOwner
    /// set. Routing every chord through libghostty plus the residual matcher
    /// removes that path entirely.
    ///
    /// Router events are instance-scoped (no static subscriptions), so
    /// MainWindow can be closed and garbage-collected cleanly once the
    /// last tab closes.
    /// </summary>
    private void WirePaneActionEvents()
    {
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

        // Ctrl+Shift+I toggles the terminal inspector (same handler as the
        // command palette / libghostty inspector action).
        _router.InspectorToggleRequested += (_, _) => ToggleInspector();

        // Fullscreen toggle via F11.
        _router.ToggleFullscreenRequested += (_, _) => ToggleFullscreen();

        // Ctrl+Shift+F opens the in-pane scrollback search bar on the
        // active leaf. The control owns its visibility; we just route
        // the chord through TerminalControl so the right surface
        // receives the libghostty binding-action calls.
        _router.OpenSearchRequested += (_, _) =>
        {
            var leaf = _tabManager.ActiveTab?.PaneHost?.ActiveLeaf;
            var terminal = leaf?.Terminal();
            terminal?.OpenSearch();
        };

        // Quake / drop-down chord (default Ctrl+`). Forward to the
        // App singleton; App owns the quake window and its global
        // hotkey, so any MainWindow can be the chord source.
        _router.QuickTerminalToggleRequested += (_, _) =>
            ((App)Application.Current).ToggleQuickTerminal();

        _router.ShowKeybindCheatsheetRequested += (_, _) =>
            _ = Ghostty.Settings.CheatSheetLauncher.ShowAsync(
                _configService,
                Content?.XamlRoot,
                WinRT.Interop.WindowNative.GetWindowHandle(this));

        _router.ShowAboutRequested += (_, _) =>
        {
            // Reuse the existing About window if it is still open.
            if (_aboutWindow is not null)
            {
                _aboutWindow.Activate();
                return;
            }
            var aboutWin = new Ghostty.Dialogs.AboutWindow(_configService);
            aboutWin.Closed += (_, _) => _aboutWindow = null;
            _aboutWindow = aboutWin;
            aboutWin.Activate();
        };

        _router.ReopenClosedTabRequested += (_, _) => ReopenClosedTab();
        _router.ReopenClosedWindowRequested += (_, _) =>
            ((App)Application.Current).ReopenClosedWindow();

        WireTabSwitcher();
    }

    /// <summary>
    /// Reopen the most recently closed tab into this window. Rebuilds the tab
    /// (fresh shells) from the saved snapshot via the same SessionRestorer the
    /// startup restore uses, then adopts it. No-op if the store is empty.
    /// </summary>
    private void ReopenClosedTab()
    {
        if (!App.ClosedTabs.TryPop(out var tabSession)) return;

        var restorer = new Ghostty.Session.SessionRestorer(_factory, App.ProfileRegistry);
        if (restorer.BuildTab(tabSession) is not { } tab) return;

        // AdoptTab raises TabAdded -> AddPaneHost + activation, so the rebuilt
        // tab renders and focuses just like the session-restore path.
        _tabManager.AdoptTab(tab);
    }

    // Auto-dismiss timer for the Ctrl+Tab preview popup. Restarted on every
    // press so rapid cycling keeps the popup up, then it fades after a pause.
    private DispatcherTimer? _cyclePopupTimer;

    /// <summary>
    /// Wire Ctrl+Tab / Ctrl+Shift+Tab (immediate next/previous tab switch with a
    /// brief, auto-dismissing preview popup -- no hold-Ctrl semantics) and the
    /// Ctrl+Shift+E grid overview.
    /// </summary>
    private void WireTabSwitcher()
    {
        _cyclePopupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _cyclePopupTimer.Tick += (_, _) =>
        {
            _cyclePopupTimer!.Stop();
            TabSwitcherPopupHost.IsOpen = false;
        };

        _router.MruCycleRequested += (_, forward) => CycleTab(forward);
        _router.ShowTabOverviewRequested += (_, _) => ShowTabOverview();

        // Grab keyboard focus only once the popup is actually open, so arrow
        // keys / Enter / Esc work. Focusing inside ShowTabOverview (before
        // IsOpen) silently fails because the grid isn't realized yet.
        TabOverviewHost.Opened += (_, _) => TabOverviewUI.FocusGrid();

        TabOverviewUI.TabChosen += (_, tab) =>
        {
            TabOverviewHost.IsOpen = false;
            _tabManager.Activate(tab);
            FocusActiveLeaf();
        };
        TabOverviewUI.Dismissed += (_, _) =>
        {
            TabOverviewHost.IsOpen = false;
            FocusActiveLeaf();
        };
    }

    // Switch immediately to the next / previous tab in positional (tab-strip)
    // order, wrapping at the ends, then flash the preview popup. Each press
    // commits and restarts the popup's auto-dismiss timer, so repeated presses
    // cycle through tabs with the preview visible and it fades after a pause.
    private void CycleTab(bool forward)
    {
        var tabs = _tabManager.Tabs;
        if (tabs.Count < 2) return;

        var idx = tabs.IndexOf(_tabManager.ActiveTab);
        if (idx < 0) idx = 0;
        var next = forward
            ? (idx + 1) % tabs.Count
            : (idx - 1 + tabs.Count) % tabs.Count;
        var target = tabs[next];

        _tabManager.Activate(target);
        FocusActiveLeaf();

        SizeTabSwitcherPopup();
        TabSwitcherPopupUI.Show(tabs, target, _configService.FontFamily);
        TabSwitcherPopupHost.IsOpen = true;
        _cyclePopupTimer?.Stop();
        _cyclePopupTimer?.Start();
    }

    // Stretch the full-window cycle popup to the current window size so its
    // centered content sits in the middle of the window.
    private void SizeTabSwitcherPopup()
    {
        TabSwitcherPopupUI.Width = RootGrid.ActualWidth;
        TabSwitcherPopupUI.Height = RootGrid.ActualHeight;
    }

    private void ShowTabOverview()
    {
        if (_tabManager.Tabs.Count == 0) return;
        TabOverviewUI.Width = RootGrid.ActualWidth;
        TabOverviewUI.Height = RootGrid.ActualHeight;
        // Positional (tab-strip) order, not MRU: the grid is a spatial overview,
        // so tiles should match the order tabs appear in the strip. MRU order is
        // for the Ctrl+Tab cycle popup only.
        TabOverviewUI.Show(_tabManager.Tabs, _tabManager.ActiveTab, _configService.FontFamily);
        TabOverviewHost.IsOpen = true;
    }

    /// <summary>
    /// Apply the resolved window theme to the XAML visual tree and the
    /// DWM non-client area. Called once at startup and again whenever
    /// the <see cref="WindowThemeManager"/> detects a change.
    /// </summary>
    private void ApplyTheme()
    {
        // A ConfigChanged / ColorValuesChanged callback may already be
        // queued on the dispatcher when the window starts closing. By then
        // XamlRoot is null and the HWND is dying (see the RegisteredRoot
        // capture in the ctor), so touching RequestedTheme/DWM throws. We
        // cannot gate on XamlRoot == null — it is also null during the
        // ctor's first ApplyTheme(), which must still apply the initial
        // theme — so _isClosed is the startup-safe teardown signal (#208).
        if (_isClosed) return;

        if (Content is FrameworkElement root)
            root.RequestedTheme = _themeManager.ElementTheme;
        _themeManager.ApplyToWindow(this);

        // Caption button colors must be set explicitly when
        // ExtendsContentIntoTitleBar is true, otherwise WinUI 3
        // fills them with the system accent color.
        //
        // Contrast against the backdrop the buttons actually sit on (the
        // terminal background, blended through Mica/acrylic), NOT the
        // abstract element theme. window-theme=dark forces IsDarkMode=true
        // even over a light terminal palette, which painted white buttons
        // onto a near-white chrome → invisible (#235). Luminance of the
        // terminal background tracks what the user sees behind the buttons.
        bool backdropDark = ThemeResolution.PreferLightForeground(_configService.BackgroundColor);
        var fg = backdropDark
            ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        var fgInactive = backdropDark
            ? Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x66, 0x00, 0x00, 0x00);
        var hoverBg = backdropDark
            ? Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x33, 0x00, 0x00, 0x00);
        var pressedBg = backdropDark
            ? Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x22, 0x00, 0x00, 0x00);

        ApplyButtonColors(
            bg: Windows.UI.Color.FromArgb(0, 0, 0, 0),
            fg: fg,
            inactiveBg: Windows.UI.Color.FromArgb(0, 0, 0, 0),
            inactiveFg: fgInactive,
            hoverBg: hoverBg,
            hoverFg: fg,
            pressedBg: pressedBg,
            pressedFg: fg);

        // Propagate theme to tab hosts so their text/icons adapt.
        // Guard: tab hosts are created after the first ApplyTheme call.
        if (_horizontalTabHost is not null)
        {
            _horizontalTabHost.SetRequestedTheme(_themeManager.ElementTheme);
            _verticalTabHost.SetRequestedTheme(_themeManager.ElementTheme);
        }

        ApplyCaptionButtonChrome();
    }

    /// <summary>
    /// Apply palette-derived colors to the window chrome when
    /// window-theme=ghostty is set.
    /// </summary>
    private void ApplyShellTheme()
    {
        if (!_shellTheme.IsEnabled)
        {
            // Revert to standard chrome. Reset VerticalTitleText so
            // it picks up the element-theme default again.
            VerticalTitleText.ClearValue(TextBlock.ForegroundProperty);

            // Let ApplyTheme write the standard caption-button colors
            // directly. Pre-clearing the buttons to null here would
            // briefly show the system accent (blue) on close/min/max
            // before ApplyTheme overwrites them, producing a visible
            // flash on every config reload.
            ApplyTheme();

            _horizontalTabHost.ClearShellTheme();
            _verticalTabHost.ClearShellTheme();
            ApplyVerticalTitleBarChrome();
            ApplyCaptionButtonChrome();
            return;
        }

        // Caption buttons: transparent in horizontal mode, opaque in
        // vertical -- see ApplyCaptionButtonChrome().

        VerticalTitleText.Foreground = new SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(
                _shellTheme.TitleBarForeground.A,
                _shellTheme.TitleBarForeground.R,
                _shellTheme.TitleBarForeground.G,
                _shellTheme.TitleBarForeground.B));

        // Push theme to both tab hosts. RootGrid.Background is owned
        // by ApplyRootGridBackground and refreshed by the caller.
        _horizontalTabHost.ApplyShellTheme(_shellTheme);
        _verticalTabHost.ApplyShellTheme(_shellTheme);
        ApplyVerticalTitleBarChrome();
        ApplyCaptionButtonChrome();
    }

    /// <summary>
    /// Opaque title-row fills for vertical-tab mode. Without this the
    /// transparent drag region shows Mica grey over the terminal palette.
    /// </summary>
    private void ApplyVerticalTitleBarChrome()
    {
        Windows.UI.Color dragBg;
        Windows.UI.Color stripMirrorBg;
        if (_shellTheme.IsEnabled)
        {
            dragBg = _shellTheme.TitleBarBackground;
            stripMirrorBg = _shellTheme.TabBarBackground;
        }
        else
        {
            dragBg = UnpackTerminalColor(_configService.BackgroundColor);
            stripMirrorBg = dragBg;
        }

        if (_lastVerticalTitleDragBg != dragBg)
        {
            _lastVerticalTitleDragBg = dragBg;
            VerticalTitleDragRegion.Background = new SolidColorBrush(dragBg);
        }

        if (_lastVerticalTitleStripMirrorBg != stripMirrorBg)
        {
            _lastVerticalTitleStripMirrorBg = stripMirrorBg;
            VerticalTitleStripMirrorFill.Background = new SolidColorBrush(stripMirrorBg);
        }

        if (_lastVerticalTitleCaptionBg != dragBg)
        {
            _lastVerticalTitleCaptionBg = dragBg;
            var captionBrush = new SolidColorBrush(dragBg);
            VerticalTitleCaptionFill.Background = captionBrush;
            VerticalCaptionSeamCover.Background = captionBrush;
        }

        VerticalCaptionSeamCover.Visibility =
            VerticalTitleBar.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>
    /// Caption min/max/close colors. Vertical mode uses the same opaque
    /// title-bar fill as the drag region; horizontal keeps transparent
    /// buttons over the tab strip or Mica.
    /// </summary>
    private void ApplyCaptionButtonChrome()
    {
        // ApplyTheme() runs before _shellTheme exists; ApplyShellTheme and
        // the post-layout refresh below re-apply once wiring is complete.
        if (_shellTheme is null) return;

        if (_verticalTabsVisible)
        {
            Windows.UI.Color titleBg;
            Windows.UI.Color fg;
            Windows.UI.Color hoverBg;
            Windows.UI.Color pressedBg;
            if (_shellTheme.IsEnabled)
            {
                titleBg = _shellTheme.TitleBarBackground;
                fg = _shellTheme.TitleBarForeground;
                hoverBg = _shellTheme.TabBarBackground;
                pressedBg = _shellTheme.TabBarBackground;
            }
            else
            {
                titleBg = UnpackTerminalColor(_configService.BackgroundColor);
                var dark = ThemeResolution.PreferLightForeground(
                    _configService.BackgroundColor);
                fg = dark
                    ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                    : Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
                hoverBg = dark
                    ? Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                    : Windows.UI.Color.FromArgb(0x33, 0x00, 0x00, 0x00);
                pressedBg = dark
                    ? Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                    : Windows.UI.Color.FromArgb(0x22, 0x00, 0x00, 0x00);
            }

            var fgInactive = Windows.UI.Color.FromArgb(128, fg.R, fg.G, fg.B);
            ApplyButtonColors(
                bg: titleBg,
                fg: fg,
                inactiveBg: titleBg,
                inactiveFg: fgInactive,
                hoverBg: hoverBg,
                hoverFg: fg,
                pressedBg: pressedBg,
                pressedFg: fg);
            return;
        }

        if (!_shellTheme.IsEnabled) return;

        var shellFg = _shellTheme.TitleBarForeground;
        var shellFgInactive = Windows.UI.Color.FromArgb(
            128, shellFg.R, shellFg.G, shellFg.B);
        ApplyButtonColors(
            bg: Windows.UI.Color.FromArgb(0, 0, 0, 0),
            fg: shellFg,
            inactiveBg: Windows.UI.Color.FromArgb(0, 0, 0, 0),
            inactiveFg: shellFgInactive,
            hoverBg: _shellTheme.TabBarBackground,
            hoverFg: shellFg,
            pressedBg: _shellTheme.TabBarBackground,
            pressedFg: shellFg);
    }

    private static Windows.UI.Color UnpackTerminalColor(uint packed)
        => Windows.UI.Color.FromArgb(0xFF,
            (byte)(packed >> 16),
            (byte)(packed >> 8),
            (byte)packed);

    /// <summary>
    /// Re-apply tab-host chrome after a horizontal/vertical layout switch.
    /// Both hosts stay alive; the incoming one must not keep stale MUXC
    /// resources from a prior visibility cycle.
    /// </summary>
    internal void RefreshTabHostChrome()
    {
        if (_shellTheme.IsEnabled)
        {
            _horizontalTabHost.ApplyShellTheme(_shellTheme);
            _verticalTabHost.ApplyShellTheme(_shellTheme);
        }
        else
        {
            _horizontalTabHost.ClearShellTheme();
            _verticalTabHost.ClearShellTheme();
            _horizontalTabHost.SetRequestedTheme(_themeManager.ElementTheme);
            _verticalTabHost.SetRequestedTheme(_themeManager.ElementTheme);
        }

        ApplyVerticalTitleBarChrome();
        ApplyCaptionButtonChrome();
        ApplyPerTabChrome();
    }

    /// <summary>
    /// Apply caption-button colors, writing each TitleBar property
    /// only when its individual value changed. WinUI 3 marshals each
    /// setter to DWM separately; rapid sequential writes cause a brief
    /// flash to the system accent color (blue) on the close/min/max
    /// buttons while DWM is between updates. Skipping no-op writes
    /// minimizes that window.
    /// </summary>
    private void ApplyButtonColors(
        Windows.UI.Color? bg, Windows.UI.Color? fg,
        Windows.UI.Color? inactiveBg, Windows.UI.Color? inactiveFg,
        Windows.UI.Color? hoverBg, Windows.UI.Color? hoverFg,
        Windows.UI.Color? pressedBg, Windows.UI.Color? pressedFg)
    {
        var prev = _lastButtonColors;
        _lastButtonColors = new CaptionColors(
            bg, fg, inactiveBg, inactiveFg, hoverBg, hoverFg, pressedBg, pressedFg);

        var tb = AppWindow.TitleBar;
        if (prev.Bg != bg) tb.ButtonBackgroundColor = bg;
        if (prev.InactiveBg != inactiveBg) tb.ButtonInactiveBackgroundColor = inactiveBg;
        if (prev.HoverBg != hoverBg) tb.ButtonHoverBackgroundColor = hoverBg;
        if (prev.PressedBg != pressedBg) tb.ButtonPressedBackgroundColor = pressedBg;
        if (prev.Fg != fg) tb.ButtonForegroundColor = fg;
        if (prev.InactiveFg != inactiveFg) tb.ButtonInactiveForegroundColor = inactiveFg;
        if (prev.HoverFg != hoverFg) tb.ButtonHoverForegroundColor = hoverFg;
        if (prev.PressedFg != pressedFg) tb.ButtonPressedForegroundColor = pressedFg;
    }

    /// <summary>
    /// Update config-driven chrome colors: pane border and the vertical tab
    /// accent bar track cursor-color; the horizontal selected-tab background
    /// blends with the terminal background so the active tab connects to the
    /// pane below it. Called on every config reload so theme changes apply.
    /// </summary>
    private void UpdateCursorAccentColors()
    {
        var bg = _configService.BackgroundColor;
        var fg = _configService.ForegroundColor;
        var bgColor = Windows.UI.Color.FromArgb(0xFF,
            (byte)(bg >> 16), (byte)(bg >> 8), (byte)bg);
        var fgColor = Windows.UI.Color.FromArgb(0xFF,
            (byte)(fg >> 16), (byte)(fg >> 8), (byte)fg);

        _horizontalTabHost.SetSelectedTabColors(bgColor, fgColor);
        _verticalTabHost.SetSelectedTabColors(bgColor, fgColor);

        var cc = _configService.CursorColor ?? _configService.ForegroundColor;
        var wuiColor = Windows.UI.Color.FromArgb(0xFF,
            (byte)(cc >> 16), (byte)(cc >> 8), (byte)cc);
        _verticalTabHost.SetAccentColor(wuiColor);

        ApplyPerTabChrome();
    }

    /// <summary>
    /// Pane borders follow the active tab's preset color when set; tab strip
    /// backgrounds refresh in both orientations.
    /// </summary>
    private void ApplyPerTabChrome()
    {
        var cc = _configService.CursorColor ?? _configService.ForegroundColor;
        var defaultBorder = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF,
            (byte)(cc >> 16), (byte)(cc >> 8), (byte)cc));

        var active = _tabManager.ActiveTab;
        foreach (var tab in _tabManager.Tabs)
        {
            SolidColorBrush borderBrush;
            if (ReferenceEquals(tab, active) && tab.Color != TabColor.None)
            {
                var preset = TabColorPalette.Border(tab.Color);
                borderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(
                    0xFF, preset.R, preset.G, preset.B));
            }
            else
            {
                borderBrush = defaultBorder;
            }

            ((PaneHost)tab.PaneHost).SetActiveBorderBrush(borderBrush);
        }

        _horizontalTabHost.RefreshTabColors();
        _verticalTabHost.RefreshTabColors();
    }

    private void WireTabColor(TabModel tab)
    {
        if (_tabColorWired.ContainsKey(tab)) return;

        // Named handler, not a lambda literal: TabModel instances outlive
        // this window (DetachTab hands the same instance to another
        // window's TabManager), so an unremovable subscription keeps the
        // dead window alive and fires ApplyPerTabChrome on its torn-down
        // XamlRoot. Same hazard the ConfigChanged/ThemeChanged unhooks
        // in CleanupWindowSubscriptions guard against.
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(TabModel.Color))
                ApplyPerTabChrome();
        };
        ((INotifyPropertyChanged)tab).PropertyChanged += handler;
        _tabColorWired[tab] = handler;
    }

    private void UnwireTabColor(TabModel tab)
    {
        if (_tabColorWired.Remove(tab, out var handler))
            ((INotifyPropertyChanged)tab).PropertyChanged -= handler;
    }

    /// <summary>
    /// Apply the window backdrop based on background-style and
    /// background-opacity config values. Dispatches to the correct
    /// SystemBackdrop implementation for each preset.
    /// </summary>
    private void ApplyBackdropStyle()
    {
        var lowPowerActive = Ghostty.App.PowerStateMonitor?.IsLowPowerActive ?? false;

        var configOpacity = _configService.BackgroundOpacity;
        var opacity = lowPowerActive ? 1.0 : configOpacity;
        var configStyle = _configService.BackgroundStyle;

        // If the user's configured style is acrylic-based, keep the
        // acrylic backdrop alive even at opacity=1.0 so Ctrl+Shift+Scroll
        // doesn't flash between Mica and acrylic at the boundary.
        // Low-power mode overrides this: flatten unconditionally to Solid
        // (Mica) to drop the composition cost of acrylic/crystal.
        var style = lowPowerActive
            ? BackdropStyles.Solid
            : ((opacity >= 1.0 && configStyle != BackdropStyles.Frosted)
                ? BackdropStyles.Solid
                : configStyle);

        // Skip if the effective style hasn't changed.
        if (style == _currentBackdropStyle && SystemBackdrop is not null)
            return;

        _currentBackdropStyle = style;

        var hwnd = WindowNative.GetWindowHandle(this);

        var (tintColor, tintOpacity, luminosityOpacity) = ResolveAcrylicTuning();

        switch (style)
        {
            case BackdropStyles.Frosted:
                if (DesktopAcrylicController.IsSupported())
                    SystemBackdrop = new AcrylicBackdrop(
                        tintColor, tintOpacity, luminosityOpacity,
                        _newAcrylicLogger());
                else
                    goto case BackdropStyles.Solid;
                ApplyWindowClassBrush(ClassBrushKind.Transparent);
                break;

            case BackdropStyles.Crystal:
                SystemBackdrop = new CrystalBackdrop(hwnd);
                ApplyWindowClassBrush(ClassBrushKind.Transparent);
                break;

            case BackdropStyles.Solid:
            default:
                if (MicaController.IsSupported())
                    SystemBackdrop = new MicaBackdrop();
                else
                    SystemBackdrop = null;
                ApplyWindowClassBrush(ClassBrushKind.Opaque);
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
        if (_currentBackdropStyle != BackdropStyles.Frosted) return;
        if (SystemBackdrop is not AcrylicBackdrop current) return;

        var (tintColor, tintOpacity, luminosityOpacity) = ResolveAcrylicTuning();
        current.UpdateTuning(tintColor, tintOpacity, luminosityOpacity);
    }

    /// <summary>
    /// Resolve effective acrylic tuning values from config. Thin
    /// adapter: packs <see cref="Windows.UI.Color"/> into ARGB, delegates
    /// the policy to <see cref="AcrylicTintResolver"/>, unpacks back.
    /// </summary>
    private (Windows.UI.Color tintColor, float tintOpacity, float luminosityOpacity)
        ResolveAcrylicTuning()
    {
        // Low-power flattens opacity to 1.0 so the tuning resolver picks
        // the opaque fallback consistent with ApplyBackdropStyle.
        var lowPowerActive = Ghostty.App.PowerStateMonitor?.IsLowPowerActive ?? false;
        var effectiveOpacity = lowPowerActive ? 1.0 : _configService.BackgroundOpacity;

        uint? overrideArgb = _configService.BackgroundTintColor is { } c
            ? ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B
            : null;

        var t = AcrylicTintResolver.Resolve(
            tintOverrideArgb: overrideArgb,
            themeBackgroundRgb: _configService.BackgroundColor,
            tintOpacityOverride: _configService.BackgroundTintOpacity,
            luminosityOpacityOverride: _configService.BackgroundLuminosityOpacity,
            blurFollowsOpacity: _configService.BackgroundBlurFollowsOpacity,
            backgroundOpacity: effectiveOpacity);

        var tint = Windows.UI.Color.FromArgb(
            (byte)(t.TintArgb >> 24),
            (byte)(t.TintArgb >> 16),
            (byte)(t.TintArgb >> 8),
            (byte)t.TintArgb);

        return (tint, t.TintOpacity, t.LuminosityOpacity);
    }

    private void OnLowPowerChanged(object? sender, EventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                ApplyBackdropStyle();
                UpdateAcrylicTuning();
                ApplyGradientTint();
                ApplyRootGridBackground();
                RefreshPowerSaverIcon();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                    or InvalidOperationException
                                    or NullReferenceException)
            {
                // Window tore down between monitor thread-pool event and UI dispatch.
                // OnClosedAsync unsubscribes, but there's a narrow window before the
                // queued lambda runs where XAML objects may already be disposed.
            }
        });
    }

    private void RefreshPowerSaverIcon()
    {
        var monitor = Ghostty.App.PowerStateMonitor;
        var active = monitor?.IsLowPowerActive ?? false;
        PowerSaverIcon.Visibility = active
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

        var triggers = monitor?.ActiveTriggers ?? Ghostty.Core.Power.PowerSaverTrigger.None;
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(
            PowerSaverIcon,
            Ghostty.Core.Power.PowerSaverTooltipFormatter.Format(triggers));
    }

    /// <summary>
    /// Create, update, or remove the gradient tint visual based on
    /// the current config. Called on startup and config reload.
    /// Rebuilds the SpriteVisual only when structural config
    /// (points, blend, static gradient-opacity) changes; opacity and
    /// animation updates apply in place. Without this gate, every
    /// config reload tears down and recreates the visual, which
    /// visibly re-flashes the gradient on high-frequency reloads
    /// such as Ctrl+Shift+scroll for users with a gradient
    /// configured -- same bug class as # 239.
    /// </summary>
    private void ApplyGradientTint()
    {
        var points = _configService.GradientPoints;

        // Low-power mode flattens the backdrop: no animated gradient,
        // no composition work beyond the system backdrop. Treat as if
        // no points were configured so the existing teardown runs.
        var lowPowerActive = Ghostty.App.PowerStateMonitor?.IsLowPowerActive ?? false;

        if (points.Count == 0 || lowPowerActive)
        {
            _gradientVisual?.Dispose();
            _gradientVisual = null;
            // Reset the full cache key together so the three fields
            // never drift out of sync; a later non-empty apply will
            // trip structuralChange on points==null and rebuild.
            _lastGradientPoints = null;
            _lastGradientBlend = null;
            _lastGradientOpacity = 0f;
            return;
        }

        var blend = _configService.GradientBlend ?? "underlay";
        var isOverlay = blend == "overlay";
        var gradientOpacity = _configService.GradientOpacity;

        var structuralChange = _gradientVisual is null
            || _lastGradientBlend != blend
            || _lastGradientOpacity != gradientOpacity
            || _lastGradientPoints is null
            || !_lastGradientPoints.SequenceEqual(points);

        if (structuralChange)
        {
            _gradientVisual?.Dispose();
            _gradientVisual = new GradientTintVisual(
                RootGrid, points, isOverlay, gradientOpacity);
            _lastGradientPoints = [.. points];
            _lastGradientBlend = blend;
            _lastGradientOpacity = gradientOpacity;
        }

        // Track opacity if blur-follows-opacity is on.
        if (_configService.BackgroundBlurFollowsOpacity)
            _gradientVisual!.SetOpacity((float)_configService.BackgroundOpacity);
        else if (!isOverlay)
            _gradientVisual!.SetOpacity(1f);

        _gradientVisual!.ApplyAnimation(
            _configService.GradientAnimation,
            _configService.GradientSpeed);
    }

    /// <summary>
    /// Set the Win32 class background brush for the main window.
    /// The class brush is what DWM uses for the WM_ERASEBKGND fill
    /// before XAML paints; it must match the backdrop kind so there
    /// is no black flash between Win32 fill and XAML frame compose.
    ///
    /// RootGrid.Background is NOT set here -- that is the job of
    /// <see cref="ApplyRootGridBackground"/>, the single source of
    /// truth for the RootGrid background color.
    ///
    /// HBRUSH lifetime: SetClassLongPtr returns the previously
    /// installed HBRUSH. When we previously installed a
    /// CreateSolidBrush result, we must DeleteObject it; when it was
    /// a stock brush (NULL_BRUSH) or the default WNDCLASS brush, we
    /// must not. <see cref="_classBrushOwned"/> tracks that
    /// distinction.
    /// </summary>
    private void ApplyWindowClassBrush(ClassBrushKind kind)
    {
        if (_lastClassBrushKind == kind) return;
        _lastClassBrushKind = kind;

        var hwnd = WindowNative.GetWindowHandle(this);
        var (brush, owned) = kind switch
        {
            ClassBrushKind.Transparent =>
                (Win32Interop.GetStockObject(Win32Interop.NULL_BRUSH), false),
            ClassBrushKind.Opaque =>
                (CreateSolidBrush(0x000C0C0Cu), true),
            _ => throw new System.Diagnostics.UnreachableException(
                $"Unknown ClassBrushKind: {kind}"),
        };
        var previous = SetClassLongPtr(hwnd, GCLP_HBRBACKGROUND, brush);
        if (_classBrushOwned && previous != IntPtr.Zero)
            Win32Interop.DeleteObject(previous);
        _classBrushOwned = owned;
    }

    /// <summary>
    /// ShellThemeService.ThemeChanged handler. Re-applies the shell
    /// theme (caption buttons, tab hosts, title text) and refreshes
    /// the single RootGrid.Background source of truth.
    /// </summary>
    /// <summary>
    /// WindowThemeManager.ThemeChanged handler. Named (not an anonymous
    /// lambda) so it reads consistently with the other config-driven
    /// handlers; ApplyTheme is itself _isClosed-gated and the manager nulls
    /// the event on dispose, so no teardown guard is needed here.
    /// </summary>
    private void OnWindowThemeChanged(bool isDark) => ApplyTheme();

    private void OnShellThemeChanged()
    {
        // Same teardown race as ApplyTheme: ShellThemeService routes its
        // ConfigChanged through the dispatcher, so a window-theme switch
        // immediately followed by close can queue this against a dead
        // XamlRoot / AppWindow.TitleBar (issue #208). OnClosedAsync also
        // unsubscribes this handler; the gate covers the in-flight call.
        if (_isClosed) return;
        ApplyShellTheme();
        ApplyRootGridBackground();
    }

    /// <summary>
    /// ConfigService.ConfigChanged handler that re-applies window chrome
    /// (backdrop, acrylic, gradient, accent colors, shell theme, root
    /// background) after a live config reload. Each call owns exactly one
    /// disjoint piece of chrome state (see ctor note / issue # 239).
    /// </summary>
    private void OnConfigReloadedChrome(IConfigService _)
    {
        // ConfigService outlives the window and dispatches ConfigChanged,
        // so a switch-then-close can queue this against dying XAML /
        // AppWindow. Gated by _isClosed and unsubscribed in OnClosedAsync
        // (which also stops the per-window handler leak). Issue #208.
        if (_isClosed) return;
        ApplyBackdropStyle();
        UpdateAcrylicTuning();
        ApplyGradientTint();
        UpdateCursorAccentColors();
        ApplyShellTheme();
        ApplyRootGridBackground();
        RefreshPaneGutters();
    }

    /// <summary>
    /// Repaint the chrome gutter around every pane. Owns exactly that one
    /// piece of chrome state.
    ///
    /// Belongs on the config-reload path only: the gutter is filled from
    /// background and background-opacity, both of which change only on a
    /// reload. It deliberately does not ride the power-saver path, which
    /// reworks the window backdrop without touching what libghostty
    /// renders -- the gutter tracks the surface, not the backdrop.
    /// </summary>
    private void RefreshPaneGutters()
    {
        foreach (var t in _tabManager.Tabs)
            ((PaneHost)t.PaneHost).RefreshGutterBrush();
    }

    /// <summary>
    /// Paint RootGrid.Background based on current backdrop style and
    /// shell-theme state. Single source of truth so
    /// ApplyBackdropStyle and ApplyShellTheme never write this
    /// property directly. Caches the last color to avoid allocating
    /// a new SolidColorBrush on reloads where nothing changed.
    /// </summary>
    private void ApplyRootGridBackground()
    {
        var bg = _shellTheme.TitleBarBackground;
        uint shellBgArgb =
            ((uint)bg.A << 24) |
            ((uint)bg.R << 16) |
            ((uint)bg.G << 8) |
            bg.B;

        var argb = RootBackgroundResolver.Resolve(
            _currentBackdropStyle, _shellTheme.IsEnabled, shellBgArgb);

        var next = Windows.UI.Color.FromArgb(
            (byte)(argb >> 24),
            (byte)(argb >> 16),
            (byte)(argb >> 8),
            (byte)argb);

        if (_lastRootBackground == next) return;
        _lastRootBackground = next;
        RootGrid.Background = new SolidColorBrush(next);
        ApplyVerticalTitleBarChrome();
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
    /// One-shot setup applied when this window is the singleton quick
    /// (quake / drop-down) terminal:
    ///   - <c>AppWindow.IsShownInSwitchers = false</c> hides the icon
    ///     from the taskbar.
    ///   - <c>WS_EX_TOOLWINDOW</c> hides the window from the Alt+Tab
    ///     switcher (IsShownInSwitchers alone leaves it visible there).
    ///   - <c>AppWindow.Closing</c> is intercepted so the close button
    ///     hides the window instead of disposing it; the global hotkey
    ///     can then re-summon the same surface without recreating the
    ///     shell process.
    /// </summary>
    private void ApplyQuickTerminalBehaviour()
    {
        AppWindow.IsShownInSwitchers = false;

        var hwnd = new HWND(WindowNative.GetWindowHandle(this));
        var ex = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        // unchecked because WINDOW_EX_STYLE is uint-backed but
        // SetWindowLong takes int. WS_EX_LAYERED and friends set the
        // high bit on some configurations; we want a bit-preserving
        // reinterpret, not arithmetic saturation.
        PInvoke.SetWindowLong(
            hwnd,
            WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            unchecked((int)(ex | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW)));

        // Install the non-client frame subclass first. It reshapes the sizing
        // frame (drops the dark band WS_THICKFRAME leaves at the docked edge and
        // confines drag-resize to the edge opposite the dock) and, crucially,
        // suppresses the style-change notifications the borderless transition
        // below sends -- without that suppression, SetBorderAndTitleBar
        // access-violates inside the WinAppSDK windowing layer while the quake
        // window is in its early/unstable lifecycle.
        _quakeFrame = new Ghostty.Hosting.QuickTerminalFrame(
            WindowNative.GetWindowHandle(this),
            () => _configService.QuickTerminalPosition,
            // Color the kept resize strip with the terminal background so it
            // blends instead of showing Windows' default frame band.
            () => Ghostty.Interop.Win32Interop.RgbToColorRef(_configService.BackgroundColor),
            App.LoggerFactory?.CreateLogger<Ghostty.Hosting.QuickTerminalFrame>());

        // Borderless: a quake terminal is positioned by config and toggled by the
        // global hotkey, so it needs no title bar, border, caption buttons, or
        // min/max. IsResizable=true keeps a sizing frame so the window can be
        // resize-dragged. The presenter API (not raw Win32 styles) is used so
        // WinUI records the borderless state and does not re-add the title bar on
        // later activations; the suppression scope keeps the call from crashing.
        // Gated on IsInstalled: the suppression only works through the subclass,
        // so if it failed to install we skip the transition (the window keeps its
        // frame) rather than run SetBorderAndTitleBar unguarded and risk the crash.
        if (_quakeFrame.IsInstalled
            && AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            using (_quakeFrame.SuppressStyleChanges())
            {
                presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
                presenter.IsResizable = true;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }
        }

        // Borderless quake has no OS caption buttons; collapse the dead inset
        // and drop the vertical-mode title text.
        _titleBar.SetCaptionless(true);

        // Quake never shows the top vertical title bar; the strip's own wintty
        // icon sits above the tabs instead. Layout toggle stays available via
        // the Ctrl+Shift+, chord.
        _layout.SuppressVerticalTitleBar(true, _verticalTabsVisible);

        // Show the session-only pin button so the user can keep the quake
        // window open on focus loss. Invisible on regular windows by default.
        QuakePinButton.Visibility = Visibility.Visible;

        AppWindow.Closing += (_, args) =>
        {
            // The shutdown path in App.OnAnyWindowClosedInternal sets
            // _hardCloseQuake before calling Close() to opt out of the
            // hide-instead semantics. Without that escape, force-close
            // would silently turn into a hide and the process would
            // never exit when the last regular window closes.
            if (_hardCloseQuake) return;
            args.Cancel = true;
            AppWindow.Hide();
        };

        // Autohide: when the quake window loses activation to another window,
        // hide it (unless the user opted out). In-window overlays (command
        // palette, ContentDialog) do NOT deactivate the window, so this only
        // fires on a genuine switch to another app/window. Gated on
        // _autohideArmed so the activation churn during Show()/focus does not
        // self-trigger.
        Activated += OnQuakeActivated;

        // Hide the tab strip while a single tab is open; show it at 2+.
        // Tab count is already updated when these events fire.
        _tabManager.TabAdded += (_, _) => UpdateQuakeStripVisibility();
        _tabManager.TabRemoved += (_, _) => UpdateQuakeStripVisibility();
        UpdateQuakeStripVisibility();

        // Restore the per-user remembered quake height so the first show after
        // login uses it (MoveToQuakePosition applies it for top/bottom docking).
        _quakeSessionHeight = _windowState.QuakeHeight;
    }

    /// <summary>
    /// Quake-only: hide the tab strip when a single tab is open, show it
    /// at two or more. Routed through the LayoutCoordinator so it honors
    /// the current horizontal/vertical mode and survives layout toggles.
    /// </summary>
    private void UpdateQuakeStripVisibility()
    {
        if (!IsQuickTerminal) return;
        _layout.SetStripHidden(_tabManager.Tabs.Count <= 1, _verticalTabsVisible);
    }

    private void OnQuakePinChanged(object sender, RoutedEventArgs e)
    {
        _quakePinned = QuakePinButton.IsChecked == true;
    }

    private void OnQuakeActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        if (_quakePinned) return;   // user pinned the window open this session
        if (!_autohideArmed) return;
        if (!AppWindow.IsVisible) return;
        if (!_configService.QuickTerminalAutohide) return;
        Hide();
    }

    /// <summary>
    /// Opt-out flag for the quake-window close-intercept handler.
    /// Set by <c>App.OnAnyWindowClosedInternal</c> right before
    /// calling <see cref="Microsoft.UI.Xaml.Window.Close"/> on the
    /// singleton quake window during app shutdown, so the handler
    /// lets the close through instead of converting it to a hide.
    /// </summary>
    internal void RequestHardClose() => _hardCloseQuake = true;
    private bool _hardCloseQuake;

    // Lazily-built slide/fade animator for the quake window. Null until the
    // first show (the XAML content visual must exist first). Only used when
    // IsQuickTerminal and animation duration > 0.
    private QuickTerminalSlideAnimator? _slideAnimator;

    // Autohide is only armed once a show animation has fully settled, so the
    // transient activation churn during Show()/focus does not immediately
    // trigger a hide.
    private bool _autohideArmed;

    // True while a slide-out is in flight (window still IsVisible until the
    // animation completes). A toggle during this window means "bring it
    // back", so ToggleVisibility must re-show rather than hide again.
    private bool _hiding;

    // Session-only pin: while true, the quake window does not auto-hide on
    // focus loss. Toggled by the top-right pin button (quake window only).
    // Not persisted; resets on app restart.
    private bool _quakePinned;

    // User-resized height for the quake window, remembered for the session
    // so toggling hide/show preserves it instead of snapping back to the
    // quick-terminal-size config. Null until the user first resizes. Resets
    // on app restart (session-only, like the pin).
    private int? _quakeSessionHeight;

    // True while MoveToQuakePosition is programmatically moving/resizing the
    // window, so the AppWindow.Changed size-capture below ignores our own
    // resize and only records genuine user drags.
    private bool _movingQuake;

    // Window-proc subclass that drops the borderless quake window's non-client
    // sizing border (the dark band) and confines drag-resize to the edge
    // opposite the dock. Null on regular windows; lives for the quake window's
    // lifetime and is disposed on close.
    private Ghostty.Hosting.QuickTerminalFrame? _quakeFrame;

    /// <summary>
    /// Position the window per <c>quick-terminal-position</c>,
    /// <c>quick-terminal-size</c>, and <c>quick-terminal-screen</c>.
    /// The monitor adapter handles the Win32 lookup and the pure-logic
    /// resolver in <see cref="Ghostty.Core.Hosting.QuickTerminalGeometry"/>
    /// turns the config plus monitor work area into the final rect.
    /// </summary>
    public void MoveToQuakePosition()
    {
        _movingQuake = true;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var bounds = QuickTerminalMonitorResolver.Resolve(
                hwnd, _configService.QuickTerminalScreen);
            var position = _configService.QuickTerminalPosition;
            var rect = Ghostty.Core.Hosting.QuickTerminalGeometry.Resolve(
                position,
                _configService.QuickTerminalSize,
                bounds);

            // Apply a session resize (height only) for top/bottom docking.
            // Top keeps Y at the monitor top and grows downward; bottom keeps
            // its bottom edge pinned and grows upward.
            if (_quakeSessionHeight is int sessionH &&
                (position == Ghostty.Core.Hosting.QuickTerminalPosition.Top ||
                 position == Ghostty.Core.Hosting.QuickTerminalPosition.Bottom))
            {
                var h = Math.Clamp(sessionH, 100, bounds.Height);
                rect = position == Ghostty.Core.Hosting.QuickTerminalPosition.Bottom
                    ? rect with { Y = bounds.Bottom - h, Height = h }
                    : rect with { Height = h };
            }

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32
            {
                X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height,
            });
        }
        finally
        {
            // Reset on the dispatcher so any queued AppWindow.Changed from our
            // own MoveAndResize is still guarded (it can fire after this returns).
            DispatcherQueue.TryEnqueue(() => _movingQuake = false);
        }
    }

    /// <summary>
    /// Show the window at its quake position with focus on the active
    /// terminal, or hide it if currently visible. The global hotkey
    /// service in <c>App</c> wires this method up to <c>WM_HOTKEY</c>.
    /// </summary>
    public void ToggleVisibility()
    {
        // Shown and not already sliding out -> hide. If mid-slide-out, a
        // toggle means "bring it back", so fall through to Show(), which
        // cancels the hide (the animator's token guard suppresses the
        // superseded slide-out completion) and animates back in.
        if (AppWindow.IsVisible && !_hiding)
        {
            Hide();
            return;
        }
        Show();
    }

    private void Show()
    {
        _hiding = false;
        var duration = _configService.QuickTerminalAnimationDuration;
        if (duration > 0)
            _slideAnimator ??= new QuickTerminalSlideAnimator(WindowNative.GetWindowHandle(this), RootGrid);

        if (!AppWindow.IsVisible)
        {
            MoveToQuakePosition();

            // Seed the reveal's collapsed start region BEFORE the window is
            // shown. AppWindow.Show() forces an immediate present; without this
            // the first frame would be the full rectangular window before the
            // clip-reveal begins.
            if (duration > 0)
                _slideAnimator!.PrepareIn(
                    _configService.QuickTerminalPosition,
                    AppWindow.Size.Width,
                    AppWindow.Size.Height);

            AppWindow.Show();
        }

        if (duration <= 0)
        {
            _slideAnimator?.SnapToShown();
            _autohideArmed = true;
            FocusActiveLeaf();
            return;
        }

        _autohideArmed = false;
        _slideAnimator!.AnimateIn(
            _configService.QuickTerminalPosition,
            AppWindow.Size.Width,
            AppWindow.Size.Height,
            TimeSpan.FromSeconds(duration),
            onCompleted: () =>
            {
                _autohideArmed = true;
                FocusActiveLeaf();
            });
        // Focus immediately too so typing works during the slide; the
        // onCompleted re-focus is a no-op if focus already landed.
        FocusActiveLeaf();
    }

    /// <summary>
    /// Hide the quake window, animating the slide-out first when a non-zero
    /// animation duration is configured. Used by the toggle and by autohide.
    /// (Hides the AppWindow; this is not a Window override -- Window has no Hide().)
    /// </summary>
    private void Hide()
    {
        _autohideArmed = false;
        var duration = _configService.QuickTerminalAnimationDuration;
        if (duration <= 0 || _slideAnimator is null)
        {
            AppWindow.Hide();
            return;
        }

        _hiding = true;
        _slideAnimator.AnimateOut(
            _configService.QuickTerminalPosition,
            AppWindow.Size.Width,
            AppWindow.Size.Height,
            TimeSpan.FromSeconds(duration),
            onCompleted: () =>
            {
                // A re-show during the slide-out clears _hiding to cancel the
                // hide (the animator token guard already suppresses a
                // superseded Completed; this is belt-and-suspenders). Also
                // skip when the window is hard-closing on shutdown.
                if (!_hiding || _hardCloseQuake) return;
                _hiding = false;
                // Completed fires on the UI thread (same context as
                // GetElementVisual), so AppWindow.Hide() is safe; guard
                // against a COM teardown race during shutdown.
                try { AppWindow.Hide(); }
                catch (System.Runtime.InteropServices.COMException) { }
            });
    }

    private void FocusActiveLeaf()
    {
        DispatcherQueue.TryEnqueue(() =>
            _tabManager.ActiveTab?.PaneHost?.ActiveLeaf?.Terminal()
                .Focus(FocusState.Programmatic));
    }

    /// <summary>
    /// Restore the window position and size from the previous session.
    /// Validates that at least part of the window is visible on a
    /// current monitor (handles monitor disconnects, DPI changes).
    /// </summary>
    private void RestoreWindowPlacement() =>
        ApplyGeometry(new Ghostty.Core.Session.WindowGeometry
        {
            X = _windowState.WindowX,
            Y = _windowState.WindowY,
            Width = _windowState.WindowWidth,
            Height = _windowState.WindowHeight,
            Maximized = _windowState.WindowMaximized,
        });

    /// <summary>
    /// Apply a saved geometry to this window, validating that at least
    /// part of it is visible on a current monitor (handles monitor
    /// disconnects, DPI changes). Shared by the window-state.json restore
    /// path and the session restore path.
    /// </summary>
    private void ApplyGeometry(Ghostty.Core.Session.WindowGeometry geometry)
    {
        // Shared with the pre-XAML splash, which sizes itself to cover this
        // window and must accept and reject exactly the same saved rects.
        if (!Ghostty.Core.Session.WindowGeometryGate.TryNormalize(geometry, out var rect))
            return;
        var (x, y, width, height) = rect;

        // Ensure the window's top-left quadrant is on a live monitor.
        // DisplayArea.GetFromPoint returns the nearest display if the
        // point is off-screen, but we check explicitly so we don't
        // place the window somewhere invisible.
        var checkPoint = new Windows.Graphics.PointInt32(
            x + Math.Min(100, width / 2),
            y + Math.Min(50, height / 2));
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
            checkPoint, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);

        // If the saved position is completely outside any display's
        // work area, let the OS pick a default position.
        var work = display.WorkArea;
        if (x + width < work.X || x > work.X + work.Width ||
            y + height < work.Y || y > work.Y + work.Height)
            return;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));

        if (geometry.Maximized)
            PInvoke.ShowWindow(new HWND(WindowNative.GetWindowHandle(this)), SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED);
    }

    /// <summary>
    /// Capture this window's current placement (restored bounds when
    /// maximized, so a maximized rect never becomes the saved size).
    /// </summary>
    private Ghostty.Core.Session.WindowGeometry CaptureGeometry()
    {
        var hwnd = new HWND(WindowNative.GetWindowHandle(this));
        var style = (WINDOW_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var isMaximized = (style & WINDOW_STYLE.WS_MAXIMIZE) != 0;
        var g = new Ghostty.Core.Session.WindowGeometry { Maximized = isMaximized };
        if (isMaximized)
        {
            var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
            PInvoke.GetWindowPlacement(hwnd, ref placement);
            var rc = placement.rcNormalPosition;
            g.X = rc.left;
            g.Y = rc.top;
            g.Width = rc.right - rc.left;
            g.Height = rc.bottom - rc.top;
        }
        else
        {
            g.X = AppWindow.Position.X;
            g.Y = AppWindow.Position.Y;
            g.Width = AppWindow.Size.Width;
            g.Height = AppWindow.Size.Height;
        }
        return g;
    }

    /// <summary>
    /// Capture this window's geometry + ordered tabs into a serializable
    /// record, or null for the quake window (never persisted) and
    /// full-screen windows (no meaningful restore geometry).
    /// </summary>
    internal Ghostty.Core.Session.WindowSession? CaptureSession()
    {
        if (IsQuickTerminal) return null;
        if (AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
            return null;

        var win = new Ghostty.Core.Session.WindowSession
        {
            Geometry = CaptureGeometry(),
            ActiveTabIndex = _tabManager.IndexOf(_tabManager.ActiveTab),
        };
        foreach (var tab in _tabManager.Tabs)
        {
            win.Tabs.Add(Ghostty.Core.Session.SessionCapture.CaptureTab(
                tab.PaneHost.RootNode,
                tab.PaneHost.ActiveLeaf,
                tab.PaneHost.ZoomedLeaf,
                tab.ProfileId,
                tab.UserOverrideTitle));
        }
        return win;
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

        _configWriter.Write(
            () => _configEditor.SetValue("background-opacity", next.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)),
            "background-opacity");
    }

    /// <summary>Apply always-on-top (float_window). mode: 0 on, 1 off, 2 toggle.</summary>
    private void ApplyFloat(int mode)
    {
        if (AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter p) return;
        p.IsAlwaysOnTop = mode switch
        {
            0 => true,
            1 => false,
            _ => !p.IsAlwaysOnTop,
        };
    }

    /// <summary>
    /// Resize back to the libghostty-computed default (reset_window_size).
    /// No-op until an initial_size has been received (window-width/height set),
    /// matching core behaviour.
    /// </summary>
    private void ResetWindowSize()
    {
        if (_defaultWindowSizePx is not { } size) return;
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized } op)
        {
            op.Restore();
        }
        // AppWindow.Resize takes physical pixels, so the initial_size payload
        // (already in physical pixels) is passed through as-is -- unlike the
        // DIP-based PreferredMinimum/Maximum sizes in ApplySizeLimit.
        AppWindow.Resize(size);
    }

    /// <summary>
    /// Flip the configured background opacity between 1.0 and the remembered
    /// baseline (toggle_background_opacity). Persists + reloads, consistent
    /// with the Ctrl+Shift+scroll opacity behaviour.
    /// </summary>
    private void ToggleBackgroundOpacity()
    {
        var r = Ghostty.Core.Input.BackgroundOpacityToggle.Next(
            _configService.BackgroundOpacity, _opacityToggleBaseline);
        _opacityToggleBaseline = r.NewBaseline;
        if (r.OpacityToWrite is not { } next) return; // no-op (started opaque)
        _configWriter.Write(
            // Invariant culture: libghostty's config parser expects a '.'
            // decimal separator, so a comma-decimal locale would corrupt the value.
            () => _configEditor.SetValue(
                "background-opacity",
                next.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)),
            "background-opacity");
    }

    /// <summary>
    /// Apply min/max window size (size_limit). Payload is physical pixels;
    /// OverlappedPresenter wants DIPs, so scale by the window DPI. 0 means
    /// "no limit" for that dimension.
    /// </summary>
    private void ApplySizeLimit(Ghostty.Hosting.SizeLimitRequest lim)
    {
        if (AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter p) return;
        var hwnd = new HWND(WindowNative.GetWindowHandle(this));
        var dpi = PInvoke.GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        int Dip(uint px) => px == 0 ? 0 : (int)Math.Round(px / scale);

        p.PreferredMinimumWidth = Dip(lim.MinWidth);
        p.PreferredMinimumHeight = Dip(lim.MinHeight);
        p.PreferredMaximumWidth = Dip(lim.MaxWidth);
        p.PreferredMaximumHeight = Dip(lim.MaxHeight);
    }

    /// <summary>
    /// Prompt to rename either the tab or the surface (prompt_title), reusing
    /// the existing rename dialog.
    /// </summary>
    private async Task ShowPromptTitleDialogAsync(bool isTab, Controls.TerminalControl control)
    {
        var root = Content?.XamlRoot;
        if (root is null) return;

        var initial = isTab ? _tabManager.ActiveTab.UserOverrideTitle : control.CurrentTitle;
        var dlg = new RenameTabDialog(initial) { XamlRoot = root };
        using (_dialogs.Track(dlg))
        {
            ContentDialogResult outcome;
            try
            {
                // ShowAsync throws if another ContentDialog is already open;
                // a keybind can fire while one is up, and this runs as a
                // fire-and-forget continuation, so swallow that race rather
                // than let it crash the process.
                outcome = await dlg.ShowAsync();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (outcome != ContentDialogResult.Primary) return;
            var result = string.IsNullOrWhiteSpace(dlg.Result) ? null : dlg.Result;
            if (isTab)
                _tabManager.ActiveTab.UserOverrideTitle = result;
            else
                control.SetUserTitleOverride(result);
        }
    }

    /// <summary>
    /// The cold-start seed surface has presented its first frame, so the
    /// splash can go once WinUI has also composed. One-shot: later renders
    /// are irrelevant, and the coordinator latches anyway.
    /// </summary>
    private void OnLaunchSurfaceFirstRender(object? sender, EventArgs e)
    {
        if (sender is Controls.TerminalControl terminal)
            terminal.FirstRender -= OnLaunchSurfaceFirstRender;
        _launchIcon?.NotifyReady();
    }

    /// <summary>
    /// Bring this window to the front and focus the target surface, switching
    /// to its tab if it lives in a background tab (present_terminal).
    /// </summary>
    private void PresentSurface(Controls.TerminalControl target)
    {
        Activate();
        foreach (var tab in _tabManager.Tabs)
        {
            var ph = (Panes.PaneHost)tab.PaneHost;
            foreach (var leaf in PaneTree.Leaves(ph.RootNode))
            {
                if (!ReferenceEquals(leaf.Terminal(), target)) continue;
                var idx = _tabManager.IndexOf(tab);
                if (idx >= 0) _tabManager.JumpTo(idx);
                target.Focus(FocusState.Programmatic);
                return;
            }
        }
        FocusActiveLeaf();
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
            opacityAction: direction => DispatcherQueue.TryEnqueue(() => AdjustOpacity(direction)),
            canUndo: () => (_tabManager.ActiveTab?.PaneHost as Panes.PaneHost)?.CanUndo ?? false,
            canRedo: () => (_tabManager.ActiveTab?.PaneHost as Panes.PaneHost)?.CanRedo ?? false,
            isVerticalTabLayout: () => _tabHost is VerticalTabHost);

        var jump = new JumpCommandSource(
            _tabManager,
            jumpAction: (tabIdx, _) => DispatcherQueue.TryEnqueue(() => _tabManager.JumpTo(tabIdx)));

        var config = new ConfigCommandSource();

        var version = new VersionCommandSource(() => this.Content?.XamlRoot, _dialogs);

        var sources = new List<ICommandSource> { builtIn, jump, config, version };

#if DEMO
        // Demo entries appear only when WINTTY_DEMO is set, so a demo build with
        // the var unset leaves the palette unchanged. Defer to the next tick so
        // the palette popup closes before the overlay mutates the visual tree,
        // matching the other command sources.
        if (Environment.GetEnvironmentVariable("WINTTY_DEMO") is not null)
            sources.Add(new DemoCommandSource(
                mode => DispatcherQueue.TryEnqueue(() => StartDemo(mode))));
#endif

        // Null-check App services as a defensive belt (cold-start where App.ProfileRegistry
        // isn't wired yet would skip the source entirely; ProfileCommandSource itself
        // returns an empty list when its registry has no profiles).
        if (App.ProfileRegistry is not null && App.ModifierKeyState is not null)
        {
            sources.Add(new ProfileCommandSource(
                App.ProfileRegistry,
                App.ModifierKeyState,
                OpenProfile));
        }

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
            ["jump_to_prompt"] = new() { Name = "jump_to_prompt", Description = "Jump to previous/next shell prompt (OSC 133)", RequiresParameter = true, Parameters = ["-1", "1"] },
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
            groupByCategory: _configService.CommandPaletteGroupCommands,
            // Route '>' command-mode actions through the same libghostty
            // binding-action path BuiltInCommandSource uses, deferred to the
            // next tick so the palette closes before the action runs.
            commandLineDispatch: actionKey =>
                DispatcherQueue.TryEnqueue(() => ExecuteBindingAction(actionKey)));
    }

    // Toggle the inspector window for the active surface. v1: one inspector
    // window per main window; re-toggling closes it, and it stays bound to the
    // surface it opened for (re-open to retarget a different pane).
    private void ToggleInspector()
    {
        if (_inspectorWindow is not null)
        {
            _inspectorWindow.Close();
            return;
        }

        var paneHost = _tabManager.ActiveTab?.PaneHost;
        var leaf = paneHost?.ActiveLeaf;
        if (leaf is null) return;
        var surfaceHandle = leaf.Terminal().SurfaceHandle;
        if (surfaceHandle == IntPtr.Zero) return;

        // ghostty_surface_inspector lazily creates the inspector and can return
        // null; don't open a window we can't drive.
        var inspector = NativeMethods.SurfaceInspector(new GhosttySurface(surfaceHandle));
        if (inspector.Handle == IntPtr.Zero)
        {
            App.NotificationService?.Show(Ghostty.Core.Inspector.InspectorNotice.Dx12Unimplemented());
            return;
        }

        var window = new InspectorWindow(
            inspector, App.LoggerFactory?.CreateLogger<InspectorWindow>());

        // The inspector presents into the bound surface every frame. If the
        // active surface changes (pane focus/close, tab switch) that surface
        // may be torn down, so close the inspector first to avoid presenting
        // into freed state. v1 retargets by reopening on the new active pane.
        EventHandler<LeafPane> onLeafFocused = (_, _) => _inspectorWindow?.Close();
        EventHandler<TabModel> onTabChanged = (_, _) => _inspectorWindow?.Close();
        if (paneHost is not null) paneHost.LeafFocused += onLeafFocused;
        _tabManager.ActiveTabChanged += onTabChanged;

        window.Closed += (_, _) =>
        {
            if (paneHost is not null) paneHost.LeafFocused -= onLeafFocused;
            _tabManager.ActiveTabChanged -= onTabChanged;
            _inspectorWindow = null;
        };
        _inspectorWindow = window;
        window.Activate();
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

#if DEMO
    // Inject raw UTF-8 text into the active surface, exactly as typed input
    // would arrive via SurfaceText. Used by the demo player for "type"/"key".
    private void InjectDemoText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var leaf = _tabManager.ActiveTab?.PaneHost?.ActiveLeaf;
        if (leaf is null) return;

        var surfaceHandle = leaf.Terminal().SurfaceHandle;
        if (surfaceHandle == IntPtr.Zero) return;

        var surface = new GhosttySurface(surfaceHandle);
        var bytes = Encoding.UTF8.GetBytes(text);
        unsafe
        {
            fixed (byte* p = bytes)
            {
                NativeMethods.SurfaceText(surface, (IntPtr)p, (UIntPtr)bytes.Length);
            }
        }
    }

    // Write a config key=value and reload, so the demo can showcase appearance
    // settings (theme, opacity, gradients, fonts, ...) that have no action. Same
    // write+reload path AdjustOpacity uses; the change persists in the config
    // file, which is fine for a throwaway recording profile.
    private void ApplyDemoConfig(string key, string value)
    {
        _configWriter.Write(() => _configEditor.SetValue(key, value), key);
    }

    // Run a command-palette command by id without opening the palette. Lets the
    // demo "command" beat fire palette-only commands that have no PaneAction
    // (e.g. Pro "shell:open_sessions"). Returns false if no such command exists.
    private bool RunDemoCommand(string id) => _commandPaletteVm?.TryExecuteById(id) ?? false;

    // Inject REAL keystrokes (not SurfaceText) so features watching the WinUI
    // input pipeline observe them (e.g. Pro keycast chips). Goes to the focused
    // surface, which is correct during a recording. Chars are injected as Unicode
    // (WM_CHAR); special keys as virtual-key down/up.
    private Windows.UI.Input.Preview.Injection.InputInjector? DemoInjector =>
        _demoInjector ??= Windows.UI.Input.Preview.Injection.InputInjector.TryCreate();

    // SAFETY: InjectKeyboardInput is global -- it reaches whatever window is
    // foreground. Never inject unless OUR window is foreground, so the demo can
    // never leak keystrokes into another app (e.g. while focus is elsewhere).
    private bool DemoWindowIsForeground()
    {
        var hwnd = new HWND(WindowNative.GetWindowHandle(this));
        return PInvoke.GetForegroundWindow() == hwnd;
    }

    private void InjectRealChar(string s)
    {
        var injector = DemoInjector;
        if (injector is null || !DemoWindowIsForeground()) return;
        // Iterate UTF-16 code units: Unicode injection wants the surrogate pair
        // of a non-BMP rune as two separate ScanCode events, which is exactly
        // what enumerating chars produces.
        foreach (var ch in s)
        {
            var down = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo
            {
                ScanCode = ch,
                KeyOptions = Windows.UI.Input.Preview.Injection.InjectedInputKeyOptions.Unicode,
            };
            var up = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo
            {
                ScanCode = ch,
                KeyOptions = Windows.UI.Input.Preview.Injection.InjectedInputKeyOptions.Unicode
                    | Windows.UI.Input.Preview.Injection.InjectedInputKeyOptions.KeyUp,
            };
            injector.InjectKeyboardInput(new[] { down, up });
        }
    }

    private void InjectRealEnter()
    {
        var injector = DemoInjector;
        if (injector is null || !DemoWindowIsForeground()) return;
        var down = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo
        {
            VirtualKey = (ushort)Windows.System.VirtualKey.Enter,
        };
        var up = new Windows.UI.Input.Preview.Injection.InjectedInputKeyboardInfo
        {
            VirtualKey = (ushort)Windows.System.VirtualKey.Enter,
            KeyOptions = Windows.UI.Input.Preview.Injection.InjectedInputKeyOptions.KeyUp,
        };
        injector.InjectKeyboardInput(new[] { down, up });
    }

    // Lazily build the overlay (spanning the whole root grid) and the player.
    private Ghostty.Demo.DemoPlayer EnsureDemoPlayer()
    {
        if (_demoPlayer is not null) return _demoPlayer;

        _demoOverlay = new Ghostty.Demo.DemoOverlay();
        RootGrid.Children.Add(_demoOverlay);
        Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(_demoOverlay, 99);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(_demoOverlay, 99);

        _demoPlayer = new Ghostty.Demo.DemoPlayer(
            invokeAction: action => _router.Invoke(action),
            invokeBinding: ExecuteBindingAction,
            injectText: InjectDemoText,
            applyConfig: ApplyDemoConfig,
            runCommand: RunDemoCommand,
            injectRealChar: InjectRealChar,
            injectRealEnter: InjectRealEnter,
            setInjecting: v => _demoInjecting = v,
            showCaption: (text, idx, total) => _demoOverlay!.ShowCaption(text, idx, total),
            hideOverlay: () => _demoOverlay!.Hide(),
            log: App.LoggerFactory?.CreateLogger("Demo")
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        return _demoPlayer;
    }

    private async void StartDemo(Ghostty.Core.Demo.DemoMode mode)
    {
        // Guard at entry, not just inside RunAsync: a fast double-trigger would
        // otherwise start two script reads before the first sets IsRunning.
        if (_demoPlayer is { IsRunning: true }) return;

        var demoLog = App.LoggerFactory?.CreateLogger("Demo");

        // Whole body in try: EnsureDemoPlayer mutates the visual tree, so a
        // throw must not escape as an unhandled async-void exception.
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var configDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var envValue = Environment.GetEnvironmentVariable("WINTTY_DEMO");

            var path = Ghostty.Core.Demo.DemoScriptParser.ResolveScriptPath(
                envValue, exeDir, configDir, System.IO.File.Exists);
            demoLog?.LogInformation("Demo script resolved: env='{Env}' path='{Path}'", envValue, path);

            var player = EnsureDemoPlayer();
            if (path is null)
            {
                _demoOverlay!.ShowCaption("No demo script found (set WINTTY_DEMO to a .json path)");
                return;
            }

            var json = await System.IO.File.ReadAllTextAsync(path);
            var script = Ghostty.Core.Demo.DemoScriptParser.Parse(json);
            await player.RunAsync(script, mode);
        }
        catch (Exception ex)
        {
            demoLog?.LogError(ex, "Failed to start demo.");
            _demoOverlay?.ShowCaption("Demo failed to start (see logs)");
        }
    }

    private void OnDemoKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_demoPlayer is null || !_demoPlayer.IsRunning) return;

        // Ignore keys while a "keys" beat is injecting: those events are the
        // demo's own synthetic input, not the operator's abort/step/pause.
        if (_demoInjecting) return;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                _demoPlayer.Abort();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Space:
            case Windows.System.VirtualKey.Right:
                _demoPlayer.Step();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.P:
                _demoPlayer.TogglePause();
                e.Handled = true;
                break;
        }
    }
#endif

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

internal static partial class MainWindowLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.MainWindow.ConfigOpenFailed,
                   Level = LogLevel.Warning,
                   Message = "[MainWindow] Failed to open config file")]
    internal static partial void LogConfigOpenFailed(
        this ILogger<MainWindow> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.MainWindow.DialogDrainFailed,
                   Level = LogLevel.Warning,
                   Message = "DialogTracker drain failed")]
    internal static partial void LogDialogDrainFailed(
        this ILogger<MainWindow> logger, System.Exception ex);
}
