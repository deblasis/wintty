using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Ghostty.Core.Accessibility;
using Ghostty.Core.Config;
using Ghostty.Core.Env;
using Ghostty.Core.Interop;
using Ghostty.Core.Shell;
using Ghostty.Core.Themes;
using Ghostty.Interop;
using Ghostty.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Ghostty.Services;

/// <summary>
/// A single gradient color point with normalized position, color, and radius.
/// </summary>
internal readonly record struct GradientPoint(
    float X, float Y, Windows.UI.Color Color, float Radius);

/// <summary>
/// Owns the libghostty config lifecycle: init, load from disk,
/// reload, and file-system watching (behind <c>auto-reload-config</c>).
/// Fires <see cref="ConfigChanged"/> on the UI thread after every
/// successful reload so consumers can re-read values they depend on, and
/// again on an OS light/dark flip (see
/// <see cref="RefreshForOsColorScheme"/>) -- the file has not changed
/// there, but what a conditional theme resolves to may have.
/// </summary>
internal sealed partial class ConfigService : IConfigService, Ghostty.Core.Profiles.IProfileConfigSource
{
    private GhosttyConfig _config;
    private GhosttyApp _app;
    private ConfigFileWatcher? _watcher;
    private readonly DispatcherQueue _dispatcher;
    private volatile bool _suppressWatcher;

    // How many deliveries a declined reload has asked the watcher for in a
    // row, and the cap. Reset by any applied reload, so the budget is per
    // stretch of unreadability rather than per session. Three debounce
    // periods is about a second, which covers an editor or an indexer
    // holding the file and stops short of retrying forever at one config
    // rebuild per 300ms.
    //
    // Counted on a look being SCHEDULED, not on the decline: an ask nobody
    // took would spend the budget on reloads that never happened and then
    // stop asking with nothing having been tried. Only the watcher takes this
    // ask (ConfigLookAgain.AskWatcher): an unreadable load has already
    // blocked for the loader's own retry window, and a look of the service's
    // own would block again. UI thread only, like everything Reload touches.
    // See the decline in Reload.
    private int _declinedReloadRetries;
    private const int MaxDeclinedReloadRetries = 3;

    // The shrink confirmations' own budget, with the same shape and the
    // same reset. It is a separate counter because the two ask about
    // different stretches: this one counts asks about a file that went
    // away, while _declinedReloadRetries counts asks about a file that
    // would not open. Sharing one counter let a gave-up unreadable
    // stretch arrive spent at the first shrink decline, and IsPersistentShrink
    // read that as the confirmation budget being exhausted, so the shrink
    // applied as a deletion with no confirming asks at all. UI thread only,
    // like everything Reload touches. See the decline in Reload.
    private int _shrinkConfirms;
    private const int MaxShrinkConfirms = 3;

    // The vanish confirmations' budget, and a third counter for the reason
    // the second one exists. This counts asks about the WATCHED file being
    // gone, which the watcher raises; _shrinkConfirms counts asks about a
    // count that dropped, which a reload raises about files the watcher
    // never sees. A deletion can present as both, and sharing a counter
    // would let one stretch arrive spent at the other's first observation
    // and confirm it with no asks of its own, which is exactly what sharing
    // cost between the other two.
    //
    // The whole wiring is an object from Ghostty.Core rather than a counter
    // and two calls here because nothing executes this file: Ghostty.Tests
    // holds no reference to the shell project, so every test about this
    // class reads the source with Roslyn and asserts on its shape. A budget
    // of zero restores the #1146 defect exactly, and no source-shape test
    // can see that; neither could one see the budget-restore call deleted,
    // which was measured passing. Over there a test drives the real wiring
    // against a real watcher. UI thread only, like everything the watcher's
    // delivery reaches. See OnConfigFileVanished.
    private readonly ConfigVanishProtocol _vanishProtocol;

    // How many default config files existed when the config in force was
    // built. Seeded at construction and moved only by a reload that is
    // applied, so a reload can tell "this user configures nothing" from
    // "a config file this session was running on was not there for the
    // instant I looked at it", which is what an editor's atomic save looks
    // like from outside.
    //
    // A count rather than a flag because the default files are layered and
    // there are three of them: a user migrated from Ghostty has
    // ghostty/config.ghostty as well, so the one being saved can be missing
    // while another still reads, and the load reports a perfectly good
    // "loaded" with one file fewer. UI thread only, same as _config, which
    // it describes. Written only by RecordDefaultFiles. See the guard in
    // Reload.
    private int _defaultFilesFound;

    // Set by BeginShutdown when the app is tearing down so a queued or
    // debounced reload can't call into a libghostty app that is about to
    // be (or has been) freed. volatile because the watcher / debounce-timer
    // callbacks read it from the thread pool. Issue #208: a window-theme
    // switch immediately followed by close left a debounced Reload to run
    // AppUpdateConfig on the freed app -> native access violation.
    private volatile bool _shuttingDown;

    // The config the live views are showing while the command palette
    // previews a theme, or zero. Never replaces _config: the committed
    // config stays exactly what the file says, which is what makes a revert
    // exact (show _config again) rather than a re-read that could differ.
    // Owned here and freed as soon as the app has been handed anything else,
    // since the app clones what it is given and keeps no pointer to it.
    private GhosttyConfig _previewConfig;
    private string? _previewThemeName;

    public event Action<IConfigService>? ConfigChanged;
    public string ConfigFilePath { get; }
    public bool AutoReloadEnabled { get; private set; }
    public bool ReloadEnvironment { get; private set; } = true;
    public bool SettingsUiEnabled { get; private set; }
    public double BackgroundOpacity { get; private set; } = 1.0;

    /// <summary>
    /// The <c>command</c> the user set, or null when they set none. Read from
    /// libghostty rather than the file, so an included file and
    /// <c>--command</c> on the command line count, and a default that
    /// finalize filled in (cmd.exe) does not. New panes nobody picked a
    /// profile for run it unless <c>default-profile</c> is set
    /// (<see cref="Ghostty.Core.Profiles.PaneCommandPolicy"/>,
    /// deblasis/wintty#1136).
    /// </summary>
    public Ghostty.Core.Profiles.ConfiguredCommand? ConfiguredCommand { get; private set; }

    /// <summary>
    /// Whether <c>default-profile</c> is set, which makes it win over
    /// <see cref="ConfiguredCommand"/> for every new pane.
    /// </summary>
    public bool DefaultProfileSet => !string.IsNullOrWhiteSpace(DefaultProfileId);

    // Cached during ReadFlagsCore so typed getters do not have to consult
    // _configFileCache on every read; the backing-field pattern keeps the
    // hot path allocation-free and matches BackgroundStyle / BackgroundTint*
    // below. The cache itself now survives past ReadFlagsCore (live readers
    // like IsConfiguredInFile depend on that), so the backing-field copy is
    // no longer required for correctness, only for the no-lookup-per-read
    // optimization.
    public bool VerticalTabs { get; private set; }
    public int VerticalTabsWidth { get; private set; }
        = Ghostty.Core.Config.WindowsOnlyKeyParsers.VerticalTabsWidthDefault;
    public bool VerticalTabsPinned { get; private set; }
    public bool VerticalTabsHoverExpand { get; private set; }
    // A newly spawned pane's border glows while its shell starts up.
    // Default true; `pane-startup-glow = false` turns it off. Read by
    // MainWindow and pushed to each PaneHost with the tab border colors.
    public bool PaneStartupGlow { get; private set; } = true;
    public bool CommandPaletteGroupCommands { get; private set; }
    public bool WindowsHighContrast { get; private set; } = true;
    public string CommandPaletteBackground { get; private set; } = "acrylic";
    public string NoColorOverride { get; private set; } = NoColorPolicy.Default;
    // Read here but consumed by the hang watchdog via the seed App hands
    // it in OnLaunched and again on every ConfigChanged: the watchdog
    // arms before this service exists, and reloads must not leave it on
    // a stale scope.
    public Ghostty.Core.Diagnostics.HangDumpMode HangDump { get; private set; }
        = Ghostty.Core.Diagnostics.HangDumpMode.Triage;

    // The High Contrast palette to layer on top of the user's config, or
    // null when HC is inactive/opted-out: the one asked for, and the one the
    // running config was built with. Requested by HighContrastMonitor;
    // Wanted is consumed by every config build, Applied by
    // HighContrastBackground. Only touched on the UI thread (the monitor
    // marshals its events, and Reload runs on the UI thread).
    private readonly HighContrastOverrideLatch _highContrast = new();

    // One reload a declined reload schedules for itself when the watcher
    // could not take its ask, which is always the case with
    // auto-reload-config off. Every question a decline asks is answered by a
    // later load, and without this none came: a deleted config file cost a
    // second High Contrast toggle, and a deleted layered file (one of two)
    // refused every reload until restart (wintty#1155). ConfigLookAgain.Ask
    // is the declined reload's side of it, the vanish protocol's
    // lookAgainAfter the other.
    private readonly ConfigLookAgain _lookAgain;
    public string LogLevel { get; private set; } = "info";
    public string LogFilter { get; private set; } = string.Empty;

    private static readonly string[] CommandPaletteBackgroundAllowed =
        { "acrylic", "mica", "opaque" };

    public string BackgroundStyle { get; private set; } = BackdropStyles.Default;

    /// <summary>
    /// Material for the window chrome, as opposed to the terminal's own
    /// backdrop. Never null and never unset downstream: an absent
    /// <c>frame-style</c> means "match the backdrop", and that is resolved
    /// once here so no consumer has to know the inheritance exists and a
    /// later one cannot resolve it differently. An unusable value falls
    /// back to <see cref="BackdropStyles.Default"/> rather than inheriting,
    /// because a typo that quietly picked up another key's value reads
    /// exactly like the typo working.
    /// </summary>
    public string FrameStyle { get; private set; } = BackdropStyles.Default;
    public Windows.UI.Color? BackgroundTintColor { get; private set; }
    public float? BackgroundTintOpacity { get; private set; }
    public float? BackgroundLuminosityOpacity { get; private set; }
    public bool BackgroundBlurFollowsOpacity { get; private set; }
    public IReadOnlyList<GradientPoint> GradientPoints { get; private set; } = [];
    public string GradientAnimation { get; private set; } = "static";
    public float GradientSpeed { get; private set; } = 1.0f;
    public string GradientBlend { get; private set; } = "overlay";
    public float GradientOpacity { get; private set; } = 0.05f;
    public string WindowTheme { get; private set; } = "auto";
    public Ghostty.Core.Hosting.WindowSaveState WindowSaveState { get; private set; }
        = Ghostty.Core.Hosting.WindowSaveState.Default;
    public uint ForegroundColor { get; private set; } = 0x00FFFFFF;
    // libghostty's own compile-time default. Held only between construction
    // and the first ReadFlags; anything else here reads as a terminal
    // background that no terminal is painted with.
    public uint BackgroundColor { get; private set; } = 0x00282C34;
    public uint? CursorColor { get; private set; }
    public uint? CursorTextColor { get; private set; }
    // Explicit chrome accent. Null when the user hasn't set accent-color;
    // ShellThemeService then falls back to cursor-color and finally the
    // palette. Kept nullable (unlike CursorColor, which is always
    // populated via the foreground fallback) so the "unset" state is
    // distinguishable downstream and the settings UI can show the row
    // as "no override".
    public uint? AccentColor { get; private set; }
    public uint[] AnsiPalette { get; private set; } = new uint[16];
    public string CurrentTheme { get; private set; } = "";

    // Terminal settings snapshot (for settings UI to display current values).
    public string CursorStyle { get; private set; } = "block";
    public bool CursorBlink { get; private set; }
    public bool MouseHideWhileTyping { get; private set; }
    // scrollback-limit is bytes in ghostty, not lines. Zig default is
    // 10_000_000 (10 MB) per terminal surface -- see Config.zig.
    public int ScrollbackLimit { get; private set; } = 10_000_000;

    // Font settings snapshot (for settings UI to display current values).
    public string FontFamily { get; private set; } = "";
    public double FontSize { get; private set; } = 13.0;

    // Bell settings, read from config each reload. See ReadFlagsCore.
    public Ghostty.Core.Bell.BellFeatures BellFeatures { get; private set; }
        = Ghostty.Core.Bell.BellFeatures.FromBits(0);
    public string? BellAudioPath { get; private set; }
    public double BellAudioVolume { get; private set; } = 0.5;

    /// <summary>
    /// Where the quake window docks on the chosen monitor.
    /// Default `top` matches upstream Ghostty.
    /// </summary>
    public Ghostty.Core.Hosting.QuickTerminalPosition QuickTerminalPosition =>
        Ghostty.Core.Hosting.QuickTerminalPositionExtensions.Parse(
            GetString("quick-terminal-position", "top"));

    /// <summary>
    /// Which monitor the quake window targets.
    /// Default `main` matches upstream Ghostty.
    /// </summary>
    public Ghostty.Core.Hosting.QuickTerminalScreen QuickTerminalScreen =>
        Ghostty.Core.Hosting.QuickTerminalScreenExtensions.Parse(
            GetString("quick-terminal-screen", "main"));

    /// <summary>
    /// When the resize overlay (the cols x rows pill shown while a pane
    /// is resized) is allowed to appear. Default `after-first` matches
    /// upstream Ghostty.
    /// </summary>
    public Ghostty.Core.ResizeOverlay.ResizeOverlayMode ResizeOverlayMode =>
        Ghostty.Core.ResizeOverlay.ResizeOverlayModeExtensions.Parse(
            GetString("resize-overlay", "after-first"));

    /// <summary>
    /// Where the resize overlay pill sits inside the pane. Default
    /// `center` matches upstream Ghostty.
    /// </summary>
    public Ghostty.Core.ResizeOverlay.ResizeOverlayPosition ResizeOverlayPosition =>
        Ghostty.Core.ResizeOverlay.ResizeOverlayPositionExtensions.Parse(
            GetString("resize-overlay-position", "center"));

    /// <summary>
    /// How long the resize overlay stays visible after the last size
    /// change, in milliseconds. Default 750 matches upstream Ghostty.
    /// </summary>
    public int ResizeOverlayDurationMs =>
        GetDurationMs("resize-overlay-duration", 750);

    /// <summary>
    /// Raw pane undo/redo <c>undo-timeout</c> value, in milliseconds. Read
    /// live from the libghostty <c>Duration</c> config handle (not the file
    /// cache, which can be stale outside ReadFlags). Returned verbatim,
    /// including <c>0</c> (upstream's "disable undo" sentinel) -- the policy
    /// interpretation lives in
    /// <see cref="Ghostty.Core.Panes.UndoPolicy.FromConfigMilliseconds"/>.
    /// Falls back to upstream Ghostty's 5s default window when the key isn't
    /// set, sharing the constant with the Core helper.
    /// </summary>
    public int UndoTimeoutMs =>
        GetDurationMs("undo-timeout", (int)Ghostty.Core.Panes.UndoPolicy.Default.Window.TotalMilliseconds);

    /// <summary>
    /// Upstream <c>confirm-close-surface</c>. Enums come back as
    /// UTF-8 strings from <c>ghostty_config_get</c>.
    /// </summary>
    public string ConfirmCloseSurface => GetString("confirm-close-surface", "true");

    /// <summary>
    /// Size of the quake window on each axis. Either axis can be
    /// null in which case the resolver uses sensible defaults
    /// (50% primary, 100% secondary).
    /// </summary>
    public Ghostty.Core.Hosting.QuickTerminalSize QuickTerminalSize => ReadQuickTerminalSize();

    /// <summary>
    /// Hide the quake window when it loses focus. libghostty defaults this
    /// to <c>false</c> on Windows (the non-mac/non-linux branch), but the
    /// canonical quake behaviour (and macOS) is hide-on-focus-loss, so we
    /// read it from the raw config file with a wintty default of <c>true</c>.
    /// An explicit <c>quick-terminal-autohide = false</c> still disables it.
    /// </summary>
    public bool QuickTerminalAutohide =>
        WindowsOnlyKeyParsers.ParseBool(
            GetFileValue("quick-terminal-autohide", ""),
            defaultValue: true);

    /// <summary>
    /// Slide/fade duration in seconds. libghostty key (f64, default 0.2,
    /// platform-independent). 0 disables animation (instant show/hide).
    /// Clamped to a sane ceiling so a typo can't freeze the window mid-slide.
    /// </summary>
    public double QuickTerminalAnimationDuration =>
        Math.Clamp(GetDouble("quick-terminal-animation-duration", 0.2), 0.0, 2.0);

    /// <summary>
    /// Global hotkey that toggles the quake window. Wintty-only key, read
    /// from the raw config file. Parse failure falls back to the built-in
    /// Ctrl+backtick chord so the user is never left without a hotkey.
    /// </summary>
    public Ghostty.Core.Input.QuickTerminalKeyChord QuickTerminalKeyChord =>
        Ghostty.Core.Input.QuickTerminalKeyChord.Parse(
            GetFileValue("quick-terminal-key", ""))
        ?? Ghostty.Core.Input.QuickTerminalKeyChord.Default;

    /// <summary>
    /// Parsed light theme name from a conditional theme pair, or null
    /// if the theme is a single (non-conditional) value.
    /// </summary>
    public string? LightTheme { get; private set; }

    /// <summary>
    /// Parsed dark theme name from a conditional theme pair, or null
    /// if the theme is a single (non-conditional) value.
    /// </summary>
    public string? DarkTheme { get; private set; }

    public int DiagnosticsCount => _diagnosticMessages.Count;

    public IReadOnlyList<string> WindowsOnlyKeysUsed => _windowsOnlyKeysUsed;

    // Profile view is published atomically via a single volatile
    // ProfileView reference so non-UI consumers see a consistent
    // five-field set without tearing. IProfileConfigSource is public,
    // so while today's only consumer (ProfileRegistry) reads via the
    // UI-dispatched event, future worker-thread consumers are safe.
    public IReadOnlyDictionary<string, Ghostty.Core.Profiles.ProfileDef> ParsedProfiles => _profileView.ParsedProfiles;
    public IReadOnlyList<string> ProfileOrder => _profileView.ProfileOrder;
    public string? DefaultProfileId => _profileView.DefaultProfileId;
    public IReadOnlySet<string> HiddenProfileIds => _profileView.HiddenProfileIds;
    public IReadOnlyList<string> ProfileWarnings => _profileView.ProfileWarnings;

    public event Action? ProfileConfigChanged;

    /// <summary>
    /// Filtered diagnostic messages from the last load/reload.
    /// "Unknown field" errors for <see cref="WindowsOnlyKeys"/> are
    /// excluded; those keys are collected in
    /// <see cref="_windowsOnlyKeysUsed"/> and surfaced as info.
    /// </summary>
    private readonly List<string> _diagnosticMessages = new();

    /// <summary>
    /// Windows-only keys that showed up in the user's config during
    /// the last load, in file order. Populated by parsing the
    /// "unknown field" diagnostics (libghostty's <c>Diagnostic</c> C
    /// struct only exposes the formatted message, not the separate
    /// key field).
    /// </summary>
    private readonly List<string> _windowsOnlyKeysUsed = new();

    /// <summary>
    /// Parallel set for O(1) dedup of <see cref="_windowsOnlyKeysUsed"/>
    /// without scanning the list on every diagnostic.
    /// </summary>
    private readonly HashSet<string> _windowsOnlyKeysSeen =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Ghostty.Core.Config.ProfileView EmptyProfileView = new(
        ParsedProfiles: new Dictionary<string, Ghostty.Core.Profiles.ProfileDef>(),
        ProfileOrder: Array.Empty<string>(),
        DefaultProfileId: null,
        HiddenProfileIds: System.Collections.Frozen.FrozenSet<string>.Empty,
        ProfileWarnings: Array.Empty<string>());

    private volatile Ghostty.Core.Config.ProfileView _profileView = EmptyProfileView;

    /// <summary>
    /// Snapshot of the config file's key/value lines, populated at the
    /// top of <see cref="ReadFlags(bool)"/> and replaced on each subsequent reload.
    /// Survives between reloads so live readers (IsConfiguredInFile,
    /// GetRawFileValue, GetFileValue) see the last-loaded state.
    /// Keys are case-insensitive; each maps to the list of raw values in
    /// file order.
    /// </summary>
    /// <summary>
    /// Whether this launch asked for no configuration at all.
    /// </summary>
    /// <remarks>
    /// libghostty honours <c>--no-config</c> by itself once the CLI args are
    /// layered in, but that only covers the keys it parses. Wintty reads its
    /// Windows-only keys straight off the config file, so without this the
    /// flag would suppress <c>font-size</c> and leave <c>vertical-tabs</c>
    /// standing, which is a worse answer than not having the flag.
    ///
    /// Read once from the process-wide reading rather than re-derived here,
    /// so this service and libghostty cannot disagree about what was asked.
    /// </remarks>
    private readonly bool _noConfig;

    /// <summary>
    /// The config file to read as configuration, or null when this launch is
    /// ignoring it.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ConfigFilePath"/>, which stays populated
    /// under <c>--no-config</c>: it is still where the file lives, and the
    /// Settings UI, the raw editor, the "open config file" command and the
    /// theme search path all need to know that whether or not it is in
    /// force. Every read that puts the file's contents into effect goes
    /// through this one instead, so suppressing the flag is one assignment
    /// rather than a condition repeated at each read site.
    /// </remarks>
    private string? ConfigSourcePath => _noConfig ? null : ConfigFilePath;

    /// <summary>
    /// Whether this launch passed <c>--no-config</c>. The host branches on
    /// it to hand out <see cref="Ghostty.Core.Config.NoConfigFileEditor"/>
    /// instead of a real editor, and the startup migrator skips its config
    /// appends under it: the flag must mean nothing reads AND nothing
    /// writes the config file.
    /// </summary>
    public bool NoConfig => _noConfig;

    private Dictionary<string, List<string>>? _configFileCache;

    /// <summary>
    /// Snapshot of the active theme file's key/value lines. The theme is
    /// resolved via ResolveActiveThemeName / ResolveThemePath, accounting
    /// for the light:X,dark:Y split on the OS color scheme. Populated at
    /// the top of <see cref="ReadFlags(bool)"/> whenever a theme is active,
    /// replaced on each subsequent reload, and survives between reloads so
    /// theme-color readers can consult it from any call into the service.
    /// Null when there is no active theme or the theme file is missing.
    /// </summary>
    private Dictionary<string, List<string>>? _activeThemeFileCache;

    /// <summary>
    /// OS colour scheme the cached themed values were resolved against.
    /// A conditional <c>theme = light:X,dark:Y</c> picks its file from
    /// this at reload time, so every colour read out of
    /// <see cref="_activeThemeFileCache"/> is only valid while the OS
    /// scheme still matches. Written by <see cref="ReadFlags(bool)"/>, compared
    /// by <see cref="RefreshForOsColorScheme"/>.
    /// </summary>
    private bool _themedValuesAreForDarkOs;

    /// <summary>
    /// The current config handle. Passed to <see cref="GhosttyHost"/>
    /// so it can create the app with the loaded config.
    /// </summary>
    public GhosttyConfig ConfigHandle => _config;

    public ConfigService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        // The vanish wiring, built here because the watcher it asks is
        // created later: the ask reads the field lazily, at report time.
        // The count is read the same way, so a report always sees what
        // the last applied reload recorded.
        //
        // The accept LOGS and nothing else. Lowering the count here would
        // write it from a second place, and it would write it before the
        // gate reads it three statements into Reload, which turns the
        // decision into a side effect of call order that nothing pins. The
        // count falls the ordinary way instead, on the applied path, which
        // is reached because Reload carries a proven vanish past the gate.
        //
        // The look-again is for when the watcher took no ask: with
        // auto-reload-config off, the default, there is no watcher at all.
        // It reloads once, at the floor, which is the first moment a
        // deletion can be proven, so the reload that opened the question
        // (a High Contrast toggle) lands on that one instead of being lost.
        //
        // The delay is clamped to the floor: nothing a decline asks for waits
        // longer, and a clamp is what keeps a bad delay from throwing inside
        // Reload or leaving the question waiting.
        _lookAgain = new ConfigLookAgain(
            watcherAsk: () => _watcher?.Resettle() == true,
            new SystemSchedulerTimer(StaticLoggers.ConfigWatcherTimer),
            onLook: () => _dispatcher.TryEnqueue(OnLookAgain),
            maxDelay: ConfigVanishConfirmer.DefaultFloor);
        _vanishProtocol = new ConfigVanishProtocol(
            sessionDefaultFilesFound: () => _defaultFilesFound,
            ask: () => _watcher?.Resettle() == true,
            onAccept: () =>
                StaticLoggers.ConfigService.LogConfigFileVanished(ConfigFilePath),
            lookAgainAfter: _lookAgain.Schedule);

        // ConfigNew allocates from libghostty's global allocator. A failed
        // ghostty_init leaves the global state in place but torn down, so the
        // allocator reached here would be a deinitialized one: no trap, in any
        // build mode, just a corrupted heap and a crash somewhere later with
        // no managed stack and nothing pointing back here.
        //
        // That makes this check the only one there is, which is why it throws
        // rather than asserting. Debug.Assert compiles out of Release, and
        // Release is where an unexplained native crash costs the most.
        //
        // Program.MainImpl owns the init call and makes it before starting
        // WinUI, so this states a precondition rather than enforcing a policy.
        // Initializing from here would put a process-lifetime decision partway
        // down the object graph, and its exit-on-failure would run inside
        // Application.Start's initialization callback where StartGui's catch
        // cannot see it.
        if (!Program.IsGhosttyInitialized)
        {
            throw new InvalidOperationException(
                "ghostty_init must run at the composition root before any " +
                "libghostty export that touches global state.");
        }

        _noConfig = Program.ConfigOverrides.NoConfig;

        var isOsDark = OsTheme.IsDark();

        _config = NativeMethods.ConfigNew();
        var defaultFiles = NativeMethods.ConfigLoadDefaultFiles(
            _config, out var defaultFilesFound);

        // Startup is the one place that may create a config file: this is a
        // first run exactly when the load above found none. Everything that
        // rebuilds the config of a running app goes through BuildLiveConfig,
        // which never creates, and NativeMethods says why.
        //
        // The flag must mean nothing reads AND nothing writes the config
        // file, in either spelling. CliAliases sets _noConfig for
        // `--no-config` and for `--config-default-files=false` alike, which
        // is what gates this create, the path resolution below, and the seed
        // write after it. libghostty refuses its own create under the flag
        // as well, and that half is not redundant either: the export has
        // callers that never see this shell's command line, and the flag
        // has to mean the same thing at the ABI.
        var created = !_noConfig
            && defaultFiles == ConfigFilesFound.Absent
            && NativeMethods.ConfigCreateDefaultFile();

        NativeMethods.ConfigLoadCliArgs(_config);
        NativeMethods.ConfigLoadRecursiveFiles(_config);
        // Before finalize: that is where the theme is applied, and the
        // scheme decides which half of a light/dark pair (the user's, or
        // the built-in one) gets applied. Without it this handle resolves
        // every conditional against light and reports colours the terminal
        // is not rendering.
        NativeMethods.ConfigSetColorScheme(_config, ToScheme(isOsDark));
        NativeMethods.ConfigFinalize(_config);

        // --no-config resolves without the create, either spelling: the
        // flag ignores the file, so it must not even leave an empty one
        // behind (the native openPath creates dir + file when missing; the
        // no-create variant performs the same resolution without that side
        // effect).
        var pathStr = _noConfig
            ? NativeMethods.ConfigOpenPathNoCreate()
            : NativeMethods.ConfigOpenPath();
        var rawPath = pathStr.Ptr != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(pathStr.Ptr, (int)pathStr.Len) ?? string.Empty
            : string.Empty;
        // Normalize mixed separators from Zig (forward slash) + Windows
        // (backslash) so the path looks clean in UI and logs.
        ConfigFilePath = Path.GetFullPath(rawPath);

        // Belt for a root the composition root's guard did not predict
        // (Program.GuardTestConfigRoot checks the xdg root before
        // libghostty runs; this checks what actually resolved). Covers
        // every read below, which all key off ConfigFilePath, and the
        // seed write that follows.
        Ghostty.Core.Config.TestConfigGuard.AssertUnderTemp(
            ConfigFilePath, "resolved config path");

        SeedConfigIfEmpty();

        // What Reload's guard compares against. Taken from the count the same
        // load returned, plus the starter file if one was just written,
        // rather than from File.Exists, so the two cannot disagree about what
        // counts as a config file (an empty one does, and preferredXdgPath's
        // resolution of ConfigFilePath does not think so).
        RecordDefaultFiles(created ? defaultFilesFound + 1 : defaultFilesFound);

        CacheDiagnostics();

        try
        {
            ReadFlags(isOsDark);
        }
        catch (Exception ex)
        {
            // ReadFlags reads files, so it can fail on a config another
            // process holds. Reload and RefreshForOsColorScheme already
            // treat that as recoverable; this one used to be the exception,
            // and an unhandled throw here kills the process inside
            // App.OnLaunched with no window and no message.
            //
            // Every value it would have set has a default, so a failed read
            // leaves a consistent snapshot rather than a torn one, and the
            // first successful reload replaces it wholesale.
            StaticLoggers.ConfigService.LogSnapshotRefreshFailed(ex);

            // Consistent is not the same as usable, and the retry has to
            // stay open. ReadFlags records the scheme its caches hold as its
            // last act, so a throw leaves the field at its default of false.
            // On a light desktop that is accidentally the truth, and
            // RefreshForOsColorScheme's "already on this scheme" guard would
            // then decline every retry for the life of the process, leaving
            // the chrome on field defaults that no build renders. Point the
            // flag at the scheme we do not have so the next flip goes
            // through.
            _themedValuesAreForDarkOs = !isOsDark;
        }
    }

    private static Ghostty.Core.Interop.GhosttyColorScheme ToScheme(bool isDark)
        => isDark
            ? Ghostty.Core.Interop.GhosttyColorScheme.Dark
            : Ghostty.Core.Interop.GhosttyColorScheme.Light;

    /// <summary>
    /// Mac Ghostty seeds a comment header when it creates the config
    /// file for the first time. On Windows, ghostty_config_open_path()
    /// creates the file empty -- so on first launch (or whenever the
    /// user has deleted the contents) we drop in the same starter
    /// header so they don't stare at a blank file.
    ///
    /// Sequencing note: libghostty already loaded the (empty) file at
    /// ConfigNew+LoadDefaultFiles above, so the seeded comments only
    /// take effect on the next reload. That's fine -- every seeded
    /// line is a comment, so the loaded config is functionally
    /// identical to "no file" anyway.
    ///
    /// A zero-byte file is also what an in-place save looks like from
    /// outside while it holds its breath, so the write takes the file
    /// exclusively and re-checks the length under that hold (issue
    /// #1138 is the wider version of that window). A save holding the
    /// file refuses the open and the seed waits for the next launch.
    /// A writer whose handle was already open with a permissive share
    /// mode can still land content beside the hold; the seed truncates
    /// first, so what it leaves is itself rather than a hybrid of
    /// itself and that write's tail.
    /// </summary>
    private void SeedConfigIfEmpty()
    {
        // Outside the try on purpose: the catch below exists so a writable-
        // config-dir hiccup cannot take startup down, and a guard refusal
        // is the one failure that must not be smoothed over. The
        // composition-root check normally refuses long before here; this
        // is the write boundary's own belt.
        Ghostty.Core.Config.TestConfigGuard.AssertUnderTemp(
            ConfigFilePath, "seed write");

        try
        {
            // Nothing is being read from it, so nothing should be written to
            // it either. Seeding under --no-config would have the flag create
            // the very file it exists to ignore.
            if (_noConfig) return;
            if (!File.Exists(ConfigFilePath)) return;
            if (new FileInfo(ConfigFilePath).Length != 0) return;

            var seed =
                "# This is the configuration file for Wintty (a Windows fork of Ghostty).\n" +
                "#\n" +
                "# All available options and their defaults can be listed with\n" +
                "# `wintty +show-config --default --docs`. Each option below is\n" +
                "# commented out with the default value. Uncomment it and set\n" +
                "# your preferred value to change it.\n" +
                "#\n" +
                "# Config docs:  https://ghostty.org/docs/config\n" +
                "# Config path:  " + ConfigFilePath + "\n";

            // Exclusive, and re-checked under the hold: the checks above are
            // the cheap way past the common case, and this open is the
            // decision. A save in flight holds the file, so the open throws
            // and the seed skips rather than landing on top of it.
            using (var stream = new FileStream(
                ConfigFilePath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                if (stream.Length != 0) return;

                // Truncate, because the write below is at offset 0 and a
                // writer whose handle was already open with a permissive
                // share mode can still land content beside this hold: seed
                // bytes plus that write's tail would be a file neither of
                // us wrote. Truncating makes the seed's write total; that
                // racing save's content is lost either way, this way it is
                // not corrupted.
                stream.SetLength(0);

                var bytes = System.Text.Encoding.UTF8.GetBytes(seed);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            // Don't crash startup over a writable-config-dir hiccup;
            // the diagnostic still lands in the log stream so it's
            // discoverable.
            StaticLoggers.ConfigService.LogSeedFailed(ex);
        }
    }

    /// <summary>
    /// Must be called after <c>ghostty_app_new()</c> so reloads can
    /// push the new config into the running app via
    /// <c>ghostty_app_update_config</c>.
    /// </summary>
    public void SetApp(GhosttyApp app)
    {
        _app = app;
        if (AutoReloadEnabled) StartWatcher();
    }

    public bool Reload()
    {
        // No reloads once teardown has begun: the libghostty app (and the
        // DX12 renderer it drives) may already be freed, so AppUpdateConfig
        // would dereference freed state and crash natively (issue #208,
        // switch-then-close use-after-free). The actual safety guarantee is
        // that BeginShutdown runs to completion on the UI thread before
        // AppFree, and Reload (also UI thread) re-checks this flag below
        // just before the native call -- so a reload enqueued before
        // shutdown but pumped after it is fenced off. The flag, not the
        // timer cancellation, is what closes the race.
        if (_shuttingDown) return false;

        // Don't reload before the app is created -- the initial config
        // is the one passed to ghostty_app_new and must stay alive. Note
        // this does NOT cover the post-AppFree case: nothing zeroes _app on
        // teardown, so _shuttingDown is the only guard against the freed app.
        if (_app.Handle == IntPtr.Zero) return false;

        // Sampled once for the whole reload. Sampling again for ReadFlags
        // would let a desktop flip between the two land a config resolved
        // against one scheme in caches recorded as holding the other, and
        // the second sample is the one the retry guard believes -- so the
        // first would never be corrected.
        var isOsDark = OsTheme.IsDark();

        // One read of the requested High Contrast palette for the whole
        // reload, handed to the build, and the palette the build reports it
        // actually layered, which is what an applied reload records: not one
        // asked for since, and not one whose override file could not be
        // written.
        var highContrastWanted = _highContrast.Wanted;
        HighContrastColors? highContrastBuilt;

        GhosttyConfig newConfig;
        ConfigFilesFound defaultFiles;
        int defaultFilesFound;
        try
        {
            // The same build a palette theme preview uses, without the
            // preview's overlay: see BuildLiveConfig for the layering.
            newConfig = BuildLiveConfig(
                overlayPath: null, isOsDark, highContrastWanted,
                out defaultFiles, out defaultFilesFound, out highContrastBuilt);
        }
        catch (Exception ex)
        {
            StaticLoggers.ConfigService.LogReloadFailed(ex);
            return false;
        }

        // This load's verdict is the evidence the vanish question runs on,
        // so it is fed in here rather than from the watcher's existence
        // check. Absence is an observation; anything else answers it. The
        // protocol holds both halves and says why (wintty#1155).
        //
        // A verdict its own count contradicts is neither. The gate refuses
        // it below for exactly that reason, and feeding it here would let a
        // disagreeing load confirm a deletion the gate exists to refuse, so
        // the question is left exactly as it was. That is the safe direction
        // twice over: the stretch neither advances on an untrusted absence
        // nor resets on an untrusted read.
        var vanishConfirmed = ConfigReloadGate.VerdictAndCountAgree(
            defaultFiles, defaultFilesFound)
            && _vanishProtocol.Observed(defaultFiles);

        // A reload only applies a config it could actually read. The rule and
        // its reasons are ConfigReloadGate's, in Ghostty.Core so they can be
        // tested without a libghostty or a window.
        if (ConfigReloadGate.Decide(defaultFiles, defaultFilesFound, _defaultFilesFound)
            == ConfigReloadDecision.Decline)
        {
            // A shrink that is still a shrink after its whole ask budget is
            // a deletion of a layered file the watcher does not watch, not a
            // save in flight: no rename is coming for it. The config in hand
            // was built from every file that does exist, so applying it is
            // the user's configuration as it now stands, and the applied
            // path below records the lower count. Refusing instead would be
            // permanent, which is the lockout of issue #676 one layer
            // removed: the count cannot fall from anywhere else.
            //
            // A confirmed vanish reaches the same conclusion about the
            // WATCHED file and takes the same path, for the same reason: the
            // count has just fallen to nothing, so freeing this config and
            // declining would discard one just proven correct and make the
            // user ask again. It costs one self-heal window, a file
            // reappearing before the next reload, spent after the floor has
            // already passed.
            if (!vanishConfirmed && !ConfigReloadGate.IsPersistentShrink(
                    defaultFiles, defaultFilesFound, _defaultFilesFound,
                    _shrinkConfirms, MaxShrinkConfirms))
            {
                NativeMethods.ConfigFree(newConfig);
                StaticLoggers.ConfigService.LogReloadKeptRunningConfig(
                    ConfigFilePath,
                    !ConfigReloadGate.VerdictAndCountAgree(defaultFiles, defaultFilesFound)
                        ? "its verdict and its file count disagreed"
                        : defaultFiles == ConfigFilesFound.Unreadable
                            ? "it could not be read"
                            : ConfigReloadGate.IsCountShrink(
                                  defaultFiles, defaultFilesFound, _defaultFilesFound)
                                ? $"only {defaultFilesFound} of the " +
                                  $"{_defaultFilesFound} layered config files were there"
                                : "it was not there");

                if (ConfigReloadGate.ShouldRetry(
                        defaultFiles, _declinedReloadRetries, MaxDeclinedReloadRetries))
                {
                    // Counted only when the watcher actually scheduled the
                    // delivery: an ask nobody took is not a look, and counting
                    // it would spend the budget on reloads that never happened.
                    //
                    // The watcher's ask ALONE, never a look of the service's
                    // own. The loader's open already retries a sharing
                    // violation for about four seconds on this thread before
                    // it answers Unreadable, so every look we added was another
                    // four-second freeze. A declined request stays wanted and
                    // rides the next reload that comes for any other reason.
                    if (_lookAgain.AskWatcher()) _declinedReloadRetries++;
                }
                else if (ConfigReloadGate.ShouldConfirmShrink(
                        defaultFiles, defaultFilesFound, _defaultFilesFound,
                        _shrinkConfirms, MaxShrinkConfirms))
                {
                    // The same scheduled-only count, on the shrink budget:
                    // asks about a file that went away are not asks about a
                    // file that would not open, and one counter holding both
                    // is what let a spent unreadable budget skip these asks.
                    //
                    // With no watcher this used to be the whole lockout of
                    // wintty#1155 on the shrink leg: the watcher's ask was the
                    // only one counted, so the budget never moved, the shrink
                    // was never persistent, and a user who deleted one of two
                    // layered files had every reload refused until restart.
                    if (_lookAgain.Ask()) _shrinkConfirms++;
                }
                else if (defaultFiles == ConfigFilesFound.Unreadable &&
                         _declinedReloadRetries == MaxDeclinedReloadRetries)
                {
                    // Exactly on the attempt that spends the budget, so the
                    // warning is one per stretch of unreadability rather than one
                    // per settle. The counter carries past the cap for that.
                    _declinedReloadRetries++;
                    StaticLoggers.ConfigService.LogReloadGaveUp(
                        MaxDeclinedReloadRetries, ConfigFilePath);
                }
                return false;
            }

            // Reached only by carrying past the gate, which takes either a
            // proven vanish or a shrink that outlived its asks. A proven
            // vanish has already been accounted for, at the moment it was
            // proven, by the protocol's accept; logging it again here would
            // report one deletion twice.
            if (!vanishConfirmed)
            {
                StaticLoggers.ConfigService.LogReloadDefaultFilesShrunk(
                    _defaultFilesFound, defaultFilesFound, ConfigFilePath);
            }
        }

        // Any applied reload ends the run: the next lock is a new one and
        // the next shrink is a fresh question. The vanish question is
        // answered by this load's verdict, above, not here: a file that
        // comes back unreadable answers it and never reaches an applied
        // reload.
        _declinedReloadRetries = 0;
        _shrinkConfirms = 0;

        var oldConfig = _config;

        // Suppress the watcher for the duration of the update so our
        // own config swap doesn't trigger a redundant file-change reload.
        var wasSuppressed = _suppressWatcher;
        _suppressWatcher = true;

        // Final fence right before the native call: if teardown began while
        // we were building newConfig, bail rather than push into a freed
        // app. Keeps the freed-pointer guard local to AppUpdateConfig so a
        // future refactor (await mid-method, AppFree off the UI thread)
        // can't silently reopen the #208 race. Free the config we created.
        if (_shuttingDown)
        {
            NativeMethods.ConfigFree(newConfig);
            _suppressWatcher = wasSuppressed;
            return false;
        }

        NativeMethods.AppUpdateConfig(_app, newConfig);

        // Any palette theme preview is over: the app now holds a clone of
        // the committed config, so the preview config is no longer
        // referenced by anything and a later revert has nothing to take back.
        ReleaseThemePreviewConfig();

        _config = newConfig;
        // Moved with the config, not with the answer: a reload that bailed at
        // either fence above did not apply anything, so it must not change
        // what the next one compares against.
        RecordDefaultFiles(defaultFilesFound);
        _highContrast.MarkApplied(highContrastBuilt, attempted: highContrastWanted);
        try
        {
            CacheDiagnostics();
            ReadFlags(isOsDark);
        }
        catch (Exception ex)
        {
            // ReadFlags reads files, so this is reachable on a locked or
            // half-written config -- most likely right after a save, while
            // the editor still holds the file. Reload runs inside a
            // dispatcher lambda (the watcher marshals through one), where
            // an escape is an unhandled UI-thread exception rather than a
            // failed reload, so it is caught broadly rather than by type.
            //
            // Swallowed rather than rethrown because the native swap above
            // already succeeded: libghostty is on the new config and the
            // surfaces will repaint from it. What is left stale, or torn
            // partway, is the C# snapshot, which the next reload rebuilds.
            //
            // Deliberately not reported through the return value. Callers
            // read that as "a reload happened, so expect the ConfigChanged
            // echo" -- AppearancePage counts on it to skip re-seeding its
            // own editors, and every other false leg returns before the
            // event fires. Returning false here while still firing would
            // tear an open picker down mid-drag. The distinct log message
            // is where this failure is reported.
            StaticLoggers.ConfigService.LogSnapshotRefreshFailed(ex);
        }
        finally
        {
            // Restored first: leaving _suppressWatcher latched makes
            // OnFileChanged return early on every subsequent edit, so
            // auto-reload-config silently stops working for the rest of
            // the session with nothing logged. Freeing oldConfig matters
            // too -- it is leaked for the process lifetime otherwise --
            // but a fault there must not cost us the latch.
            _suppressWatcher = wasSuppressed;

            if (oldConfig.Handle != IntPtr.Zero)
                NativeMethods.ConfigFree(oldConfig);
        }

        // Fired even when the snapshot failed: libghostty has moved, so
        // suppressing this would leave the chrome painting against a
        // terminal that already repainted.
        //
        // Contained per subscriber. This body is a DispatcherQueueHandler, so
        // an exception escaping it fail-fasts the whole process with a
        // stowed exception that leaves no managed stack behind -- which is
        // how a reload landing mid-startup, while the window chrome was
        // still being built, presented as an unattributable 0xC000027B on
        // first launch. See ConfigChangeFanOut for the second reason.
        _dispatcher.TryEnqueue(() =>
        {
            ConfigChangeFanOut.InvokeAll(ConfigChanged, this, LogChangedHandlerFault);
            ConfigChangeFanOut.InvokeAll(ProfileConfigChanged, LogChangedHandlerFault);
        });
        return true;
    }

    /// <summary>
    /// Re-resolve the config values that depend on the OS colour scheme,
    /// and notify. The guard is on the scheme itself, not on whether a
    /// conditional theme is configured, so a plain theme refreshes to the
    /// same values and notifies anyway -- a flip is rare enough that the
    /// redundant fan-out is cheaper than a second thing to keep in sync.
    ///
    /// A conditional <c>theme = light:X,dark:Y</c> picks its file when the
    /// config is read, so an OS light/dark flip leaves every colour in the
    /// theme cache pointing at the wrong palette. libghostty handles its
    /// own side of the flip -- <c>AppSetColorScheme</c> moves the
    /// conditional state and the surfaces repaint -- but the C# chrome
    /// reads these cached values, so without this it keeps painting the
    /// outgoing palette next to freshly repainted terminals.
    ///
    /// This itself does no reloading: the config file has not changed, so
    /// re-parsing it would push a redundant AppUpdateConfig through to the
    /// renderer, and only the file-derived caches are rebuilt. Note that a
    /// subscriber can still reload downstream -- under High Contrast,
    /// HighContrastMonitor rebuilds its override from system colours,
    /// which do move on a flip, and hands it back through
    /// SetHighContrastOverride.
    /// </summary>
    /// <param name="isOsDark">
    /// The scheme the caller observed. Passed in rather than sampled here
    /// so the value that drove <c>AppSetColorScheme</c> is the same one
    /// the caches are rebuilt against; sampling again could disagree with
    /// it and leave the two sides describing different schemes.
    /// </param>
    public void RefreshForOsColorScheme(bool isOsDark)
    {
        // Only the shutdown fence applies. Reload's second guard is about
        // the pre-creation window before SetApp; by the time a window can
        // observe a scheme change the app exists, so it would never fire.
        // _shuttingDown is what actually matters: ReadFlagsCore reads the
        // config handle, and ConfigChanged subscribers touch XAML, neither
        // of which survives teardown.
        if (_shuttingDown) return;

        // Every window observes ColorValuesChanged, so this runs once per
        // window per flip and only the first call finds a difference. Also
        // filters the ColorValuesChanged firings that carry no scheme
        // change at all -- accent colour edits and High Contrast toggles
        // raise the same event.
        if (_themedValuesAreForDarkOs == isOsDark) return;

        try
        {
            ReadFlags(isOsDark);
        }
        catch (Exception ex)
        {
            // ReadFlags reads three files (the config, the theme, and the
            // config again for the profile pass). Windows fires this event
            // unattended on the auto light/dark schedule, and this runs
            // inside a dispatcher lambda, so letting an IO failure escape
            // would take the process down with every session in it.
            //
            // Whichever read failed, the scheme flag and the theme cache
            // agree, so the palette is either wholly the old one or wholly
            // the new one and the next flip is still able to move it.
            StaticLoggers.ConfigService.LogThemeRefreshFailed(ex);
        }

        // Notified even when the read above threw. A failure partway can
        // still leave the caches holding the incoming palette, and since
        // the flag advances with them the guard would decline every retry
        // -- so staying quiet here is what would strand the chrome, not
        // what protects it. On the legs where nothing moved, subscribers
        // re-read the values they already had.
        //
        // Deferred rather than invoked inline so the fan-out does not run
        // on top of the ColorValuesChanged dispatcher frame, and to match
        // the shape Reload already uses. The caches have finished settling
        // either way -- ReadFlags is synchronous and has returned.
        //
        // Contained for the same reason as Reload's fan-out, and it matters
        // more here: this leg fires unattended on the OS light/dark schedule
        // and on every high-contrast toggle, so a faulting subscriber would
        // kill the app while nobody was touching it.
        _dispatcher.TryEnqueue(
            () => ConfigChangeFanOut.InvokeAll(ConfigChanged, this, LogChangedHandlerFault));
    }

    /// <summary>
    /// Enumerate the compiled default keybinds only (no user config files),
    /// for the default-vs-user source diff in the keybindings settings page.
    /// Deliberately skips <c>ConfigLoadDefaultFiles</c> so the user's file
    /// doesn't taint the baseline. The throwaway config is freed immediately.
    /// </summary>
    public IReadOnlyList<Ghostty.Core.Input.EnumeratedKeybind> EnumerateDefaultKeybinds()
    {
        var def = NativeMethods.ConfigNew();
        try
        {
            NativeMethods.ConfigFinalize(def);
            return KeybindEnumerator.Enumerate(def);
        }
        finally
        {
            NativeMethods.ConfigFree(def);
        }
    }

    public string GetDiagnostic(int index)
    {
        if (index < 0 || index >= _diagnosticMessages.Count) return string.Empty;
        return _diagnosticMessages[index];
    }

    /// <summary>
    /// Temporarily suppress or resume file-system watcher events.
    /// Used by <see cref="ConfigFileEditor"/> during writes so our
    /// own save does not trigger a redundant reload.
    /// </summary>
    public void SuppressWatcher(bool suppress)
    {
        _suppressWatcher = suppress;

        // Lifting the suppression is the end of this service's own write,
        // and that write ends any stretch the two ask budgets were
        // counting. Its events were swallowed, so no delivery reloads on
        // it, and the reload a caller makes afterwards can still decline
        // (the write landing unreadable, the AppUpdateConfig fence), which
        // is the one road to a reset it had. A budget left spent here hands
        // the next stretch of unreadability a gave-up warning with no ask
        // ever having been tried in it.
        //
        // The vanish question is deliberately NOT touched: its stretches
        // open and close on load verdicts alone, and a write is not a
        // verdict.
        if (!suppress)
        {
            _declinedReloadRetries = 0;
            _shrinkConfirms = 0;
        }
    }

    /// <summary>
    /// Set (or clear, with null) the High Contrast override palette and
    /// reload so the layered colors take effect. Called by
    /// <c>HighContrastMonitor</c> on the UI thread. Skips the reload when the
    /// palette is unchanged, so spurious palette-change events don't churn the
    /// config.
    /// </summary>
    public void SetHighContrastOverride(HighContrastColors? colors)
    {
        // Skipped only when the running config already carries this palette.
        // A declined reload leaves the request wanted but not applied, so it
        // rides the next reload that applies, including the one the vanish
        // question schedules for itself, and a repeat of the request tries
        // again rather than being answered "already on this palette". The
        // latch says why one field could not do both.
        if (!_highContrast.Request(colors)) return;
        Reload();
    }

    /// <summary>
    /// The High Contrast background the override layers over the palette,
    /// packed 0x00RRGGBB, or null when HC is off or opted out.
    /// </summary>
    /// <remarks>
    /// <see cref="BackgroundColor"/> keeps resolving from the config and
    /// theme files: the override is layered into the native config only, and
    /// making the C# cache HC-aware would move every chrome consumer that
    /// reads it under HC (VerticalTitleInk, the drag region, the title bar)
    /// onto colours #790 verified as they are. This is the one place the
    /// colour the terminal will actually settle on is answerable, which is
    /// what the splash needs.
    /// </remarks>
    ///
    /// Read from what was APPLIED, not what was asked for: a request whose
    /// reload declined is not on screen yet, and the splash must match what
    /// is.
    public uint? HighContrastBackground =>
        _highContrast.Applied is { } hc
            ? Ghostty.Core.Accessibility.HighContrastConfigWriter.ColorRefToRgb(hc.Background)
            : null;

    private void CacheDiagnostics()
    {
        _diagnosticMessages.Clear();
        _windowsOnlyKeysUsed.Clear();
        _windowsOnlyKeysSeen.Clear();

        var count = (int)NativeMethods.ConfigDiagnosticsCount(_config);
        for (int i = 0; i < count; i++)
        {
            var diag = NativeMethods.ConfigGetDiagnostic(_config, (uint)i);
            if (diag.Message == IntPtr.Zero) continue;
            var message = Marshal.PtrToStringUTF8(diag.Message);
            if (string.IsNullOrEmpty(message)) continue;

            // Filter "unknown field" diagnostics for keys we know are
            // Windows-only; surface them via WindowsOnlyKeysUsed instead.
            if (WindowsOnlyKeys.TryExtractUnknownFieldKey(message, out var key))
            {
                if (WindowsOnlyKeys.IsProfileSubkey(key))
                {
                    // profile.<id>.<subkey> keys are handled by
                    // ProfileRegistry; suppress the diagnostic entirely
                    // without surfacing a per-subkey entry in
                    // WindowsOnlyKeysUsed (would flood the settings UI
                    // notice list for a many-profile config).
                    continue;
                }
                if (WindowsOnlyKeys.IsInternalKey(key))
                {
                    // internal.<name> keys are app-private knobs read
                    // directly from the raw config file; they aren't
                    // public Windows-only config, so we don't surface
                    // them via WindowsOnlyKeysUsed either.
                    continue;
                }
                if (WindowsOnlyKeys.IsAgentDetectKey(key))
                {
                    // agent-detect.<name> keys are custom tab-icon
                    // process detectors read directly from the raw
                    // config file (Pro TabIconsConfig); like internal.*
                    // they aren't public Windows-only config, so we
                    // suppress the diagnostic silently rather than
                    // surface it.
                    continue;
                }
                if (WindowsOnlyKeys.Contains(key))
                {
                    if (_windowsOnlyKeysSeen.Add(key))
                        _windowsOnlyKeysUsed.Add(key);
                    continue;
                }
            }

            _diagnosticMessages.Add(message);
        }
    }

    /// <summary>
    /// Rebuild the file-derived caches and re-read every flag from them.
    /// </summary>
    /// <param name="isOsDark">
    /// Scheme to resolve a conditional theme against. Taken as a parameter
    /// rather than sampled here so each caller names where its value came
    /// from, and so the theme file that gets loaded and the scheme recorded
    /// for it cannot come from two different reads.
    /// </param>
    private void ReadFlags(bool isOsDark)
    {
        // Snapshot the config file once up front, and the active theme
        // file once after we know which one to read. Everything below
        // that looks up Windows-only keys or theme colors goes through
        // these caches, so the whole reload is bounded by at most two
        // File.ReadLines calls regardless of how many keys we probe.
        _configFileCache = LoadIniFile(ConfigSourcePath);
        // No theme configured is not "no theme": libghostty applies its
        // built-in light/dark pair in that case, so the chrome has to
        // resolve against the same one or it frames a pane in colours the
        // pane is not filled with. Asked for by scheme rather than cached,
        // because a flip re-enters here with the other one.
        //
        // Whether that happened is asked of libghostty, not of the config
        // file. `theme` can be set in a file pulled in by `config-file`,
        // which ResolveActiveThemeName cannot see, and reading it as "no
        // theme" would paint the chrome from the built-in pair while the
        // terminal renders the theme the user actually asked for.
        //
        // A configured-but-unresolvable theme deliberately does not land
        // here: libghostty leaves the compile-time colours in place for
        // that, and substituting the built-in pair would drift again.
        if (NativeMethods.ConfigThemeIsBuiltin(_config))
        {
            _activeThemeFileCache = LoadBuiltinTheme(isOsDark);
        }
        else
        {
            var activeTheme = ResolveActiveThemeName(isOsDark);
            _activeThemeFileCache = ResolveThemePath(activeTheme) is { } themePath
                ? LoadIniFile(themePath)
                : null;
        }

        // Immediately after the assignment it certifies, so the two cannot
        // disagree. Both failure legs then stay consistent: a throw from
        // either LoadIniFile above leaves stale flag and stale caches, which
        // the next flip retries; a throw from ReadFlagsCore below leaves
        // fresh flag and fresh caches, which is the right palette. Recording
        // it at either end of this method instead lets one leg strand the
        // service claiming a scheme its caches do not hold, and the guard in
        // RefreshForOsColorScheme would then decline every retry.
        _themedValuesAreForDarkOs = isOsDark;

        // _configFileCache and _activeThemeFileCache deliberately survive
        // past ReadFlagsCore. Live readers (IsConfiguredInFile, GetRawFileValue,
        // GetFileValue) are consulted from UI page constructors and event
        // handlers that run between reloads, not just during reload, so the
        // caches must persist or those readers always return the empty-cache
        // fallback. Each reload overwrites both fields at the top of ReadFlags,
        // so memory cost stays bounded (~few KB per reload, no accumulation).
        ReadFlagsCore();
    }

    private void ReadFlagsCore()
    {
        AutoReloadEnabled = GetBool("auto-reload-config");
        // Read from the parsed config like every Zig key, so includes and
        // CLI flags decide it the same way for the app's own terminals
        // (libghostty) and everything the app builds itself.
        ReloadEnvironment = GetBool("reload-env", whenNotFound: true);
        ConfiguredCommand = ReadConfiguredCommand();
        // windows-settings-ui is a fork-added Zig field, so libghostty
        // parses it and there is no unknown-field diagnostic to suppress;
        // that is why it is deliberately absent from WindowsOnlyKeys. It
        // is still read from the file cache here, which only covers the
        // top-level config file: a value set in an included file or a
        // conditional block won't be seen.
        //
        // Reading it from that cache is also what settles the Settings UI
        // under --no-config, and settles it the right way: the cache is
        // empty, so the UI is off and the only remaining OpenConfig path
        // hands the file to an external editor. A Settings window whose
        // toggles wrote to a file this session is ignoring would be the
        // confusing outcome, and this avoids it without a second rule.
        SettingsUiEnabled = string.Equals(
            GetFileValue("windows-settings-ui", "false"),
            "true", StringComparison.OrdinalIgnoreCase);
        // Clamp here so all consumers get a safe [0,1] value without
        // needing their own validation. WindowTransparencyState also
        // clamps defensively as a standalone value type.
        BackgroundOpacity = Math.Clamp(GetDouble("background-opacity", 1.0), 0.0, 1.0);
        // Windows-only UI keys. The backing fields avoid a dictionary lookup
        // on every property read; GetFileValue now works at any time after
        // the first reload, so the copies are an optimization, not a
        // correctness requirement.
        VerticalTabs = WindowsOnlyKeyParsers.ParseBool(
            GetFileValue("vertical-tabs", ""),
            defaultValue: false);
        VerticalTabsWidth = WindowsOnlyKeyParsers.ParseIntClamped(
            GetFileValue("vertical-tabs-width", ""),
            fallback: WindowsOnlyKeyParsers.VerticalTabsWidthDefault,
            min: WindowsOnlyKeyParsers.VerticalTabsWidthMin,
            max: WindowsOnlyKeyParsers.VerticalTabsWidthMax);
        VerticalTabsPinned = WindowsOnlyKeyParsers.ParseBool(
            GetFileValue("vertical-tabs-pinned", ""),
            defaultValue: false);
        VerticalTabsHoverExpand = WindowsOnlyKeyParsers.ParseBool(
            GetFileValue("vertical-tabs-hover-expand", ""),
            defaultValue: false);
        PaneStartupGlow = WindowsOnlyKeyParsers.ParseBool(
            GetFileValue("pane-startup-glow", ""),
            defaultValue: true);
        CommandPaletteGroupCommands = WindowsOnlyKeyParsers.ParseBool(
            GetFileValue("command-palette-group-commands", ""),
            defaultValue: false);
        // No windows-single-instance here on purpose. It is read once, before
        // Application.Start, by the election in Program: a value that can be
        // re-read on every reload would let this service disagree with the role
        // the process is actually running under.
        WindowsHighContrast = WindowsOnlyKeyParsers.ParseBool(
            GetFileValue("windows-high-contrast", ""),
            defaultValue: true);
        CommandPaletteBackground = WindowsOnlyKeyParsers.ParseStringAllowed(
            GetFileValue("command-palette-background", ""),
            allowed: CommandPaletteBackgroundAllowed,
            defaultValue: "acrylic");
        NoColorOverride = WindowsOnlyKeyParsers.ParseStringAllowed(
            GetFileValue("no-color-override", ""),
            allowed: NoColorPolicy.Allowed,
            defaultValue: NoColorPolicy.Default);
        HangDump = Ghostty.Core.Diagnostics.HangDump.Parse(
            GetFileValue("hang-dump", ""));
        // Windows-only logger keys. Parsing into LogLevel/filter rules
        // happens in LoggingBootstrap; here we just surface the raw
        // strings so reloads can re-read them without parser knowledge.
        LogLevel = GetFileValue("log-level", "info");
        LogFilter = GetFileValue("log-filter", string.Empty);

        // Bell. bell-features is a packed struct returned as a c_uint
        // bitfield; decode via BellFeatures.FromBits. bell-audio-path is a
        // ?Path union that does not round-trip through ghostty_config_get,
        // so read the raw string from the file cache and resolve it like the
        // Zig Path semantics. bell-audio-volume clamps to [0,1].
        BellFeatures = Ghostty.Core.Bell.BellFeatures.FromBits(GetUInt("bell-features", 0));
        BellAudioPath = Ghostty.Core.Bell.BellAudioPath.Resolve(
            GetFileValue("bell-audio-path", ""),
            Path.GetDirectoryName(ConfigFilePath),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        BellAudioVolume = Math.Clamp(GetDouble("bell-audio-volume", 0.5), 0.0, 1.0);

        // background-style is a Windows-only key not in the Zig config
        // schema, so we read it directly from the config file.
        BackgroundStyle = NormalizeStyle(
            "background-style", GetFileValue("background-style", BackdropStyles.Default));
        // Reads BackgroundStyle, so it has to stay below it: resolved first,
        // an unset frame-style inherits whatever the previous reload left
        // behind. Absence is the whole distinction here, which is why this
        // asks whether the key is present rather than handing GetFileValue a
        // default it could not tell apart from a configured value.
        FrameStyle = TryGetFileValue("frame-style", out var rawFrameStyle)
            ? NormalizeStyle("frame-style", rawFrameStyle)
            : BackgroundStyle;
        BackgroundTintColor = ParseHexColor(GetFileValue("background-tint-color", ""));
        BackgroundTintOpacity = ParseFloat(GetFileValue("background-tint-opacity", ""));
        BackgroundLuminosityOpacity = ParseFloat(GetFileValue("background-luminosity-opacity", ""));
        BackgroundBlurFollowsOpacity = string.Equals(
            GetFileValue("background-blur-follows-opacity", "false"),
            "true", StringComparison.OrdinalIgnoreCase);
        var rawPoints = GetAllFileValues("background-gradient-point");
        var points = new List<GradientPoint>();
        foreach (var raw in rawPoints)
        {
            if (points.Count >= 5) break;
            var pt = ParseGradientPoint(raw);
            if (pt is not null) points.Add(pt.Value);
        }
        GradientPoints = points;
        GradientAnimation = GetFileValue("background-gradient-animation", "static");
        GradientSpeed = ParseFloat(GetFileValue("background-gradient-speed", "")) ?? 1.0f;
        GradientBlend = GetFileValue("background-gradient-blend", "overlay");
        GradientOpacity = ParseFloat(GetFileValue("background-gradient-opacity", "")) ?? 0.05f;
        WindowTheme = GetString("window-theme", "auto");
        WindowSaveState = Ghostty.Core.Hosting.WindowSaveStateExtensions.Parse(
            GetString("window-save-state", "default"));

        // Resolved from the config and theme text rather than through
        // ghostty_config_get. The native handle is finalized against the
        // scheme that was current when it was built, and an OS light/dark
        // flip re-enters here without rebuilding it, so on the far side of
        // a flip it still answers for the outgoing scheme. The text path
        // takes isOsDark per call and does not have that problem.
        //
        // The defaults are libghostty's own compile-time colours, reached
        // only on a build with no built-in theme; otherwise the built-in
        // theme has already supplied both.
        BackgroundColor = ResolveThemedColor("background", 0x00282C34);
        ForegroundColor = ResolveThemedColor("foreground", 0x00FFFFFF);
        (CursorColor, CursorTextColor) = ResolveCursorColors(ForegroundColor, BackgroundColor);

        // accent-color is a Windows-only key (no Zig schema entry).
        // Read from the user's config file only -- not the active theme
        // -- so accent-color is purely a user override; themes don't
        // get to paint the chrome.
        AccentColor = ThemeParser.TryParseHexRgb(GetFileValue("accent-color", ""), out var accentPacked)
            ? accentPacked
            : null;

        CurrentTheme = GetFileValue("theme", "");
        var (parsedLight, parsedDark) = ThemeParser.ParseThemePair(CurrentTheme);
        LightTheme = parsedLight;
        DarkTheme = parsedDark;

        // Terminal settings (used by settings UI for initial display).
        // Read from the config file cache instead of ghostty_config_get:
        // some keys (booleans, enums, repeatable lists like font-family)
        // don't round-trip cleanly through the native getter.
        CursorStyle = GetFileValue("cursor-style", "block");
        CursorBlink = string.Equals(
            GetFileValue("cursor-style-blink", "false"),
            "true", StringComparison.OrdinalIgnoreCase);
        MouseHideWhileTyping = string.Equals(
            GetFileValue("mouse-hide-while-typing", "true"),
            "true", StringComparison.OrdinalIgnoreCase);
        if (int.TryParse(
                GetFileValue("scrollback-limit", "10000000"),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var scrollback))
        {
            // Upper bound is int.MaxValue (~2 GB) so we don't silently
            // truncate realistic byte values. Zig uses usize which is
            // wider, but the settings UI only needs to display what the
            // user typed; anything bigger than 2 GB is exotic.
            ScrollbackLimit = Math.Clamp(scrollback, 0, int.MaxValue);
        }
        else
        {
            ScrollbackLimit = 10_000_000;
        }

        // Font settings (used by settings UI for initial display).
        // font-family is a repeatable list in Zig; the file cache gives
        // us the first user-set value, which is what the settings UI
        // wants to display. font-size is f32 in Zig, but we parse the
        // raw string to avoid the f32/f64 reinterpret pitfall.
        FontFamily = GetFileValue("font-family", "");
        if (double.TryParse(
                GetFileValue("font-size", "13"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var fontSize))
        {
            FontSize = Math.Clamp(fontSize, 6.0, 72.0);
        }
        else
        {
            FontSize = 13.0;
        }

        AnsiPalette = GetAllPaletteColors();

        // Profile-view second pass. The scalar keys (default-profile
        // and profile-order) are read through GetFileValue, which hits
        // the parsed cache populated at the top of ReadFlags. The
        // profile.<id>.* regex and hidden-id extraction used to need
        // the raw file text -- ConfigServiceProfileParser.ParseAll's
        // string overload -- because _configFileCache stores parsed
        // pairs keyed by whole "profile.<id>.<subkey>" strings rather
        // than the original bytes, and nothing walked that cache
        // looking for the profile.* shape.
        //
        // That second read is gone: ProfileSourceParser now has a
        // sibling that walks the same cache instead of raw text (same
        // "profile.<id>.<subkey>" regex, applied to each cache key
        // instead of each line), taking the cache's last value per key
        // as "last occurrence wins" -- the cache already stores every
        // occurrence in file order, so its last element is the same
        // value the forward-order text scan would have landed on. A
        // reload is now bounded by the native read plus the two
        // LoadIniFile calls above (config file, active theme file),
        // not a third file open. Readers of the five profile-view
        // properties see a consistent snapshot via the single volatile
        // _profileView assignment below.
        //
        // No separate --no-config gate is needed here: _configFileCache
        // is already the suppressible one (LoadIniFile(ConfigSourcePath)
        // at the top of this method returns empty under --no-config), so
        // the profile pass inherits the same suppression the scalar
        // reads above it already have.
        var view = Ghostty.Core.Config.ConfigServiceProfileParser.ParseAll(
            _configFileCache ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
            key =>
            {
                var v = GetFileValue(key, string.Empty);
                return v.Length == 0 ? null : v;
            });
        _profileView = view;

        foreach (var warning in view.ProfileWarnings)
            StaticLoggers.ConfigService.LogProfileParseWarning(warning);
    }

    /// <summary>
    /// Apply theme colors directly without a full config reload.
    /// Used by <see cref="ThemePreviewService"/> for live preview
    /// from the +list-themes TUI.
    /// </summary>
    internal void ApplyThemeColors(uint fg, uint bg, uint? cursor, uint? cursorText, uint[] palette)
    {
        // The same fence Reload takes, for the same reason. The preview
        // service reaches this from its pipe thread through TryEnqueue, so a
        // preview or a revert enqueued just before teardown is pumped after
        // it, and the fan-out below hands every subscriber a config the
        // shutdown is already unwinding.
        if (_shuttingDown) return;

        ForegroundColor = fg;
        BackgroundColor = bg;
        CursorColor = cursor ?? fg;
        CursorTextColor = cursorText ?? bg;
        if (palette.Length >= 16)
            Array.Copy(palette, AnsiPalette, 16);

        // Invoked inline, but every caller reaches it from inside a
        // dispatcher callback, so an escaping exception fail-fasts exactly
        // as it would from the deferred fan-outs. Live theme preview also
        // calls this once per arrow key, so a subscriber that throws would
        // kill the app on a keystroke.
        ConfigChangeFanOut.InvokeAll(ConfigChanged, this, LogChangedHandlerFault);
    }

    // ---- command palette theme preview --------------------------------
    //
    // The palette's theme mode shows the highlighted theme on what the user
    // is actually looking at: every terminal in every window, and the chrome.
    // The terminal half needs a native config, because libghostty renders
    // from nothing else, so a preview builds one exactly the way Reload does
    // with a one-line `theme = <name>` layered after the user's files. That
    // makes the preview identical to what confirming would produce: the
    // user's own explicit colour keys still win over the theme, the
    // light/dark scheme is resolved the same way, High Contrast still wins
    // over everything. The committed _config is never replaced, so undoing a
    // preview is handing the app _config again, with no re-read that could
    // come out different.

    /// <summary>Whether a palette theme preview is on the live views.</summary>
    internal bool IsPreviewingTheme => _previewConfig.Handle != IntPtr.Zero;

    /// <summary>The theme a palette preview is showing, or null.</summary>
    internal string? PreviewThemeName => _previewThemeName;

    /// <summary>
    /// Show <paramref name="themeName"/> on every live terminal and on the
    /// chrome, without writing the config file or replacing the committed
    /// config. UI thread only. False when nothing changed: a name the config
    /// cannot carry, a theme file that is not there, teardown, or a failure
    /// building the config (logged).
    /// </summary>
    internal bool PreviewTheme(string themeName)
    {
        if (_shuttingDown || _app.Handle == IntPtr.Zero) return false;
        if (!ThemeCatalog.IsPersistableName(themeName)) return false;
        if (ResolveThemePath(themeName) is not { } themePath) return false;

        var isOsDark = OsTheme.IsDark();
        Dictionary<string, List<string>>? themeCache;
        GhosttyConfig preview;
        try
        {
            themeCache = LoadIniFile(themePath);
            var overlay = WriteThemePreviewOverlay(themeName);
            if (overlay is null) return false;
            preview = BuildLiveConfig(
                overlay, isOsDark, _highContrast.Wanted,
                out var previewFiles, out var previewFilesFound, out _);

            // The same gate a reload takes, for a sharper reason: a preview
            // that could not read the user's config shows the theme over pure
            // defaults, so they judge it against a configuration that is not
            // theirs and then confirm it. Showing the palette unchanged is
            // the documented fallback for a preview that cannot be built.
            if (ConfigReloadGate.Decide(previewFiles, previewFilesFound, _defaultFilesFound) ==
                ConfigReloadDecision.Decline)
            {
                NativeMethods.ConfigFree(preview);
                // The overlay was already written, and nothing later is going
                // to take it away: RevertThemePreview only runs for a preview
                // that was shown. Left behind it is a stale `theme = ` sitting
                // in the live config's include chain until the next preview
                // overwrites it.
                DeleteThemePreviewOverlay();
                StaticLoggers.ConfigService.LogThemePreviewKeptRunningConfig(
                    themeName,
                    ConfigFilePath,
                    previewFiles == ConfigFilesFound.Unreadable
                        ? "it could not be read"
                        : "it was not there");
                return false;
            }
        }
        catch (Exception ex)
        {
            StaticLoggers.ConfigService.LogThemePreviewFailed(ex, themeName);
            return false;
        }

        // The same last-moment fence Reload takes before the native call.
        if (_shuttingDown)
        {
            NativeMethods.ConfigFree(preview);
            return false;
        }

        // The chrome's half first, resolved with the same precedence
        // ReadFlags uses (user keys, then the theme, then libghostty's
        // value) but against the previewed theme and config. Order matters
        // for what the user sees: AppUpdateConfig hands the config to the
        // native renderer, which repaints the terminals on its own thread
        // without waiting for this one, while the chrome below only lands
        // when this thread finishes and composes. Pushing the config first
        // gave the terminals a head start of exactly this whole chrome
        // pass (colour derivation plus every ConfigChanged subscriber),
        // which read on screen as the tab strip recolouring a moment
        // after the content beside it. The XAML side queues its
        // compositor updates first and the terminals follow, so a preview
        // arrives as one visible step (issue #1121).
        var colors = ResolveThemeColors(themeCache, preview);
        ApplyThemeColors(colors.Foreground, colors.Background, colors.Cursor, colors.CursorText, colors.Palette);

        NativeMethods.AppUpdateConfig(_app, preview);
        // The previous preview (if any) is no longer referenced: the app
        // cloned the one it was just given.
        ReleaseThemePreviewConfig();
        _previewConfig = preview;
        _previewThemeName = themeName;
        return true;
    }

    /// <summary>
    /// Undo a palette preview: the committed config goes back on every live
    /// view, and, when <paramref name="colors"/> is not null, the chrome gets
    /// exactly those colours back (assigned, not re-derived). UI thread only.
    /// A no-op once teardown has begun, and a no-op when a reload already
    /// ended the preview; Dispose frees what is left.
    /// </summary>
    internal void RevertThemePreview(ThemePreviewColors? colors)
    {
        if (_shuttingDown) return;

        // A reload since the preview did this method's whole job already: it
        // put the committed config on every view and re-derived the chrome
        // from it, releasing the preview handle on the way. The colours on
        // screen are the reload's then, not a preview's, so the browse's
        // snapshot is not put back over them: restoring it would repaint the
        // chrome with what the config said before whatever the reload picked
        // up (an external edit, an OS scheme flip, a High Contrast change)
        // and leave it disagreeing with the terminals until the next config
        // event. Nothing changed here, so there is nothing to fan out; the
        // reload already announced its own.
        if (_previewConfig.Handle == IntPtr.Zero) return;

        // Chrome first, native config second, for the same reason the
        // preview applies them in that order: the terminals repaint on the
        // renderer's own thread the moment they are handed the config,
        // while the chrome lands when this thread has finished and
        // composes, so a revert that pushed the config first spent its
        // whole chrome pass with the strip still wearing the preview
        // (issue #1121).
        if (colors is { } c)
        {
            ForegroundColor = c.Foreground;
            BackgroundColor = c.Background;
            CursorColor = c.Cursor;
            CursorTextColor = c.CursorText;
            if (c.Palette.Length >= 16 && AnsiPalette.Length >= 16)
                Array.Copy(c.Palette, AnsiPalette, 16);
        }

        ConfigChangeFanOut.InvokeAll(ConfigChanged, this, LogChangedHandlerFault);

        if (_app.Handle != IntPtr.Zero)
        {
            NativeMethods.AppUpdateConfig(_app, _config);
            ReleaseThemePreviewConfig();
        }
    }

    /// <summary>
    /// A colour from the config the live views are showing right now: the
    /// preview's while one is up, the committed one otherwise. The test seam
    /// reads the applied theme back through this.
    /// </summary>
    internal uint? GetLiveNativeColor(string key)
        => GetColorFrom(_previewConfig.Handle != IntPtr.Zero ? _previewConfig : _config, key);

    private void ReleaseThemePreviewConfig()
    {
        var preview = _previewConfig;
        _previewConfig = default;
        _previewThemeName = null;
        if (preview.Handle != IntPtr.Zero) NativeMethods.ConfigFree(preview);
        DeleteThemePreviewOverlay();
    }

    /// <summary>
    /// A finalized config built the way every live config is: defaults, the
    /// CLI, the user's files, then <paramref name="overlayPath"/> when given,
    /// then the High Contrast override, resolved against
    /// <paramref name="isOsDark"/>. Freed here if any step throws.
    /// </summary>
    /// <param name="defaultFiles">What the default config files amounted to.
    /// Anything but <c>Loaded</c> means the user's settings are not in the
    /// returned config. <see cref="Reload"/> is what acts on it.</param>
    /// <param name="defaultFilesFound">How many default config files exist,
    /// readable or not. The verdict alone cannot see one layered file
    /// disappearing while another still reads, which is what an atomic save
    /// of the newer one looks like; the count can.</param>
    /// <param name="highContrast">The High Contrast palette to layer, or
    /// null for none: the caller's one read of the latch's Wanted, passed
    /// in so the palette built is the palette the caller records, never a
    /// second read of the latch.</param>
    /// <param name="highContrastLayered">The High Contrast palette this
    /// config actually carries: null when none was wanted, and null too when
    /// one was wanted and its override file could not be written, because
    /// the layer is then skipped. What <see cref="Reload"/> marks as applied,
    /// so the latch never names a palette the running config does not
    /// hold.</param>
    private GhosttyConfig BuildLiveConfig(
        string? overlayPath,
        bool isOsDark,
        HighContrastColors? highContrast,
        out ConfigFilesFound defaultFiles,
        out int defaultFilesFound,
        out HighContrastColors? highContrastLayered)
    {
        highContrastLayered = null;
        var config = NativeMethods.ConfigNew();
        try
        {
            // The answer is passed through rather than flattened to a bool:
            // "no config file exists" and "a config file is there and I could
            // not read it" are different situations and Reload treats them
            // differently. libghostty logs the read failure itself.
            defaultFiles = NativeMethods.ConfigLoadDefaultFiles(
                config, out defaultFilesFound);
            NativeMethods.ConfigLoadCliArgs(config);
            NativeMethods.ConfigLoadRecursiveFiles(config);
            // A palette preview's theme sits above the user's files, which is
            // where a `theme` line they wrote themselves would take effect.
            if (overlayPath is not null)
                NativeMethods.ConfigLoadFile(config, overlayPath);
            // Layer the High Contrast override last so it wins over the
            // user's colors while HC is active. Skipped when HC is off or
            // opted-out, restoring the user's config.
            //
            // Still last now that the CLI and its config-file includes load
            // above it: High Contrast is an accessibility override and has to
            // outrank anything the user asked for, a file named on the command
            // line included.
            if (highContrast is { } hcColors)
            {
                var hcBody = Ghostty.Core.Accessibility.HighContrastConfigWriter.Render(hcColors);
                var hcPath = Ghostty.Accessibility.HighContrastOverrideFile.Write(hcBody);
                if (hcPath is not null)
                {
                    NativeMethods.ConfigLoadFile(config, hcPath);
                    highContrastLayered = hcColors;
                }
            }
            NativeMethods.ConfigSetColorScheme(config, ToScheme(isOsDark));
            NativeMethods.ConfigFinalize(config);
            return config;
        }
        catch
        {
            NativeMethods.ConfigFree(config);
            throw;
        }
    }

    /// <summary>
    /// The one-line config a preview layers: exactly the line a confirm then
    /// writes into the user's file. Kept in the state directory beside the
    /// High Contrast override, never in the config directory, so the config
    /// watcher cannot see it and the config write guard has nothing to guard.
    /// </summary>
    private static string? WriteThemePreviewOverlay(string themeName)
    {
        if (ThemePreviewOverlayPath() is not { } path) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"theme = {themeName}\n");
        return path;
    }

    /// <summary>
    /// Where the preview overlay lives, or null with no state root. The file
    /// is only read while a preview config is being built from it; the next
    /// preview overwrites it.
    /// </summary>
    private static string? ThemePreviewOverlayPath()
        => string.IsNullOrEmpty(Ghostty.Core.AppStateBase.LocalRoot)
            ? null
            : Path.Combine(
                Ghostty.Core.AppStateBase.LocalRoot,
                Ghostty.Core.AppIdentity.StateDirName,
                "theme-preview.conf");

    /// <summary>
    /// Remove the preview overlay once no preview config is fed by it. The
    /// next preview rewrites the file, and nothing else reads it, so the
    /// state directory is not left holding the name of the last theme
    /// somebody browsed past. Best effort: a locked or half-gone file costs
    /// a stale entry, nothing more.
    /// </summary>
    private static void DeleteThemePreviewOverlay()
    {
        try
        {
            if (ThemePreviewOverlayPath() is { } path) File.Delete(path);
        }
        catch (Exception)
        {
            // Swallowed on purpose; see the summary.
        }
    }

    /// <summary>
    /// The chrome colours <paramref name="themeCache"/> and
    /// <paramref name="source"/> resolve to, by ReadFlags' precedence.
    /// </summary>
    private ThemePreviewColors ResolveThemeColors(
        Dictionary<string, List<string>>? themeCache, GhosttyConfig source)
    {
        var committedTheme = _activeThemeFileCache;
        _activeThemeFileCache = themeCache;
        try
        {
            var background = ResolveThemedColor("background", 0x00282C34, source);
            var foreground = ResolveThemedColor("foreground", 0x00FFFFFF, source);
            var (cursor, cursorText) = ResolveCursorColors(foreground, background);
            return new ThemePreviewColors(foreground, background, cursor, cursorText, GetAllPaletteColors());
        }
        finally
        {
            _activeThemeFileCache = committedTheme;
        }
    }

    /// <summary>
    /// cursor-color and cursor-text are TerminalColor tagged unions in the Zig
    /// config, so they cannot be read through ghostty_config_get as simple
    /// colours; they come from the user's file, then the active theme file,
    /// and follow the foreground and background when neither sets them.
    /// </summary>
    private (uint Cursor, uint CursorText) ResolveCursorColors(uint foreground, uint background)
        => (PackHex(GetThemeValue("cursor-color")) ?? foreground,
            PackHex(GetThemeValue("cursor-text")) ?? background);

    private static uint? PackHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        return ParseHexColor(hex) is { } parsed
            ? ((uint)parsed.R << 16) | ((uint)parsed.G << 8) | parsed.B
            : null;
    }

    private Ghostty.Core.Profiles.ConfiguredCommand? ReadConfiguredCommand()
    {
        var str = NativeMethods.ConfigCommand(_config, out var direct);
        try
        {
            if (str.Ptr == IntPtr.Zero || str.Len == UIntPtr.Zero) return null;
            var command = Marshal.PtrToStringUTF8(str.Ptr, (int)str.Len);
            return string.IsNullOrWhiteSpace(command)
                ? null
                : new Ghostty.Core.Profiles.ConfiguredCommand(command, IsArgv: direct != 0);
        }
        finally
        {
            NativeMethods.StringFree(str);
        }
    }

    private bool GetBool(string key) => GetBool(key, whenNotFound: false);

    /// <summary>
    /// <see cref="GetBool(string)"/> with an explicit answer for a key the
    /// config does not report: a failed read must not flip a key whose
    /// default is true (reload-env) into its opt-out.
    /// </summary>
    private unsafe bool GetBool(string key, bool whenNotFound)
    {
        byte result = 0;
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        fixed (byte* keyPtr = keyBytes)
        {
            var found = NativeMethods.ConfigGet(
                _config,
                (IntPtr)(&result),
                (IntPtr)keyPtr,
                (UIntPtr)keyBytes.Length);
            return found ? result != 0 : whenNotFound;
        }
    }

    /// <summary>
    /// Read a config key that <c>ghostty_config_get</c> serializes as a
    /// <c>c_uint</c> (a packed-struct bitfield such as <c>bell-features</c>)
    /// into a 4-byte <see cref="uint"/> buffer.
    /// </summary>
    private unsafe uint GetUInt(string key, uint defaultValue)
    {
        uint result = 0;
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        fixed (byte* keyPtr = keyBytes)
        {
            var found = NativeMethods.ConfigGet(
                _config,
                (IntPtr)(&result),
                (IntPtr)keyPtr,
                (UIntPtr)keyBytes.Length);
            return found ? result : defaultValue;
        }
    }

    private unsafe double GetDouble(string key, double defaultValue)
    {
        double result = 0;
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        fixed (byte* keyPtr = keyBytes)
        {
            var found = NativeMethods.ConfigGet(
                _config,
                (IntPtr)(&result),
                (IntPtr)keyPtr,
                (UIntPtr)keyBytes.Length);
            return found ? result : defaultValue;
        }
    }

    /// <summary>
    /// Read a libghostty <c>Duration</c>-typed config value in
    /// milliseconds. <c>Duration.cval()</c> returns a Zig <c>usize</c>, so
    /// the output buffer must be pointer-width (<see cref="nuint"/>) -
    /// using a 4-byte <c>uint</c> would let ghostty_config_get write past
    /// the buffer on 64-bit. The value is already milliseconds (cval calls
    /// asMilliseconds), so no conversion is needed here.
    /// </summary>
    private unsafe int GetDurationMs(string key, int defaultValue)
    {
        nuint result = 0;
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        fixed (byte* keyPtr = keyBytes)
        {
            var found = NativeMethods.ConfigGet(
                _config,
                (IntPtr)(&result),
                (IntPtr)keyPtr,
                (UIntPtr)keyBytes.Length);
            // Clamp rather than truncate: asMilliseconds is a c_uint that can
            // exceed int.MaxValue (~24.8 days), and a raw (int) cast would wrap
            // to a negative the timer logic would then floor to ~nothing.
            return found
                ? (int)Math.Min(result, (nuint)int.MaxValue)
                : defaultValue;
        }
    }

    /// <summary>
    /// Read the <c>quick-terminal-size</c> two-axis struct via
    /// <c>ghostty_config_get</c>. libghostty writes a
    /// <c>ghostty_qt_size_s</c> (two axes, each tagged percentage /
    /// pixels / none) into the supplied buffer. Returns a
    /// <see cref="Ghostty.Core.Hosting.QuickTerminalSize"/> with both
    /// axes null when the key is unset, which the placement resolver
    /// then fills with its defaults (50% primary, 100% secondary).
    /// </summary>
    private unsafe Ghostty.Core.Hosting.QuickTerminalSize ReadQuickTerminalSize()
    {
        Ghostty.Core.Interop.QuickTerminalSizeC raw = default;
        var keyBytes = System.Text.Encoding.UTF8.GetBytes("quick-terminal-size");
        fixed (byte* keyPtr = keyBytes)
        {
            var found = NativeMethods.ConfigGet(
                _config,
                (IntPtr)(&raw),
                (IntPtr)keyPtr,
                (UIntPtr)keyBytes.Length);
            if (!found)
            {
                return new Ghostty.Core.Hosting.QuickTerminalSize(
                    Primary: null, Secondary: null);
            }
        }
        return raw.ToManaged();
    }

    /// <summary>
    /// Read a string-typed config value (enums are returned as
    /// NUL-terminated UTF-8 strings by <c>ghostty_config_get</c>).
    /// </summary>
    private unsafe string GetString(string key, string defaultValue)
    {
        IntPtr result = IntPtr.Zero;
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        fixed (byte* keyPtr = keyBytes)
        {
            var found = NativeMethods.ConfigGet(
                _config,
                (IntPtr)(&result),
                (IntPtr)keyPtr,
                (UIntPtr)keyBytes.Length);
            if (!found || result == IntPtr.Zero) return defaultValue;
            return Marshal.PtrToStringUTF8(result) ?? defaultValue;
        }
    }

    /// <summary>
    /// Read a config key from the config file and then the active
    /// theme file. The config file takes priority (user overrides).
    /// Used for keys like cursor-color that are set by themes and
    /// can't be read via ghostty_config_get due to complex Zig types.
    /// </summary>
    private string? GetThemeValue(string key)
    {
        // Check user config first.
        var userVal = GetFileValue(key, "");
        if (!string.IsNullOrEmpty(userVal)) return userVal;

        // Fall through to the active theme file snapshot captured at
        // the start of the reload.
        return GetActiveThemeValue(key);
    }

    /// <summary>
    /// Read the first value for <paramref name="key"/> from the active
    /// theme file cache, or null when there's no active theme or the
    /// key isn't set.
    /// </summary>
    private string? GetActiveThemeValue(string key)
        => _activeThemeFileCache is not null
            && _activeThemeFileCache.TryGetValue(key, out var list)
            && list.Count > 0
            ? list[0]
            : null;

    /// <summary>
    /// Resolve the active theme name from the config file. For a
    /// conditional theme (light:X,dark:Y), picks X or Y based on the
    /// supplied scheme. For a single theme, returns it as-is.
    /// </summary>
    /// <param name="isOsDark">Scheme to pick the dark side for.</param>
    private string ResolveActiveThemeName(bool isOsDark)
        => ThemeParser.SelectForScheme(GetFileValue("theme", ""), isOsDark);

    /// <summary>
    /// Find the theme file on disk by name, searching the same directories
    /// in the same order as libghostty. Returns null if not found.
    /// </summary>
    /// <remarks>
    /// Search order and name rules live in
    /// <see cref="ThemeSearchPath"/>, which documents why they have to
    /// track theme.zig. The bundled themes are searched last, as theme.zig
    /// searches them, so a bundled theme in a light:/dark: pair resolves
    /// its dark half here too, where libghostty's handle is finalized light.
    /// </remarks>
    private string? ResolveThemePath(string themeName)
    {
        // An absolute theme is used as-is, mirroring theme.zig's own
        // openAbsolute branch. Dropping it here would leave the terminal
        // themed and the chrome on defaults.
        if (ThemeSearchPath.IsAbsolute(themeName))
            return File.Exists(themeName) ? themeName : null;

        if (!ThemeSearchPath.IsSearchableName(themeName)) return null;

        var dirs = ThemeProvider.Directories(ConfigFilePath);

        foreach (var dir in dirs)
        {
            var themePath = Path.Combine(dir, themeName);
            if (File.Exists(themePath)) return themePath;
        }
        return null;
    }

    /// <summary>
    /// Read a color, preferring user config over the active theme file
    /// over the libghostty default. This is needed because libghostty's
    /// _config is finalized with the default (.light) conditional state,
    /// so for pair themes in dark mode it returns the wrong colors.
    /// </summary>
    /// <param name="source">
    /// The native config the last-resort lookup reads: the committed one
    /// unless a palette preview is resolving against its own.
    /// </param>
    private uint ResolveThemedColor(string key, uint defaultValue, GhosttyConfig? source = null)
    {
        // 1. User config override.
        var userVal = GetFileValue(key, "");
        if (!string.IsNullOrEmpty(userVal)
            && ThemeParser.TryParseHexRgb(userVal, out var userPacked))
            return userPacked;

        // 2. Active theme file (resolved once at reload start).
        var themeVal = GetActiveThemeValue(key);
        if (!string.IsNullOrEmpty(themeVal)
            && ThemeParser.TryParseHexRgb(themeVal, out var themePacked))
            return themePacked;

        // 3. Fall back to libghostty's resolved value (light variant or
        // hard default).
        return GetColorFrom(source ?? _config, key) ?? defaultValue;
    }

    /// <summary>
    /// Read the first non-empty value for a Windows-only config key
    /// from the cached snapshot of the config file populated by
    /// <see cref="ReadFlags(bool)"/>. Keys not in the Zig config schema
    /// cannot be read via <c>ghostty_config_get</c>, so we parse the
    /// file ourselves.
    /// </summary>
    /// <remarks>
    /// Any key read here that libghostty's parser doesn't know about
    /// must also be listed in <see cref="WindowsOnlyKeys.All"/>.
    /// Otherwise its "unknown field" diagnostic reaches
    /// <see cref="CacheDiagnostics"/> unfiltered and the settings UI
    /// shows the user a config error for a setting the app honors.
    /// </remarks>
    private string GetFileValue(string key, string defaultValue)
        => _configFileCache is not null
            && _configFileCache.TryGetValue(key, out var list)
            && list.Count > 0
            ? list[0]
            : defaultValue;

    /// <summary>
    /// Same lookup as <see cref="GetFileValue"/>, reporting whether the key
    /// was there at all.
    ///
    /// A key whose absence means something other than its default needs
    /// this: <c>frame-style</c> unset means "match background-style", and
    /// with a default parameter that is indistinguishable from the user
    /// having written the default down. A sentinel string would only move
    /// the ambiguity onto whatever value was picked as the sentinel.
    /// </summary>
    private bool TryGetFileValue(string key, out string value)
    {
        if (_configFileCache is not null
            && _configFileCache.TryGetValue(key, out var list)
            && list.Count > 0)
        {
            value = list[0];
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Fold a raw style value, and say so when it was not one.
    ///
    /// The style comparisons downstream are ordinal, so a config saying
    /// "Frosted" ran solid with nothing in the log. Reported from here
    /// rather than from the fold itself because by the time the value
    /// reaches the backdrop switch there is no key name left to put in
    /// the message, and the reader needs the line of their config.
    /// </summary>
    private static string NormalizeStyle(string key, string raw)
    {
        if (BackdropStyles.TryNormalize(raw, out var style)) return style;
        StaticLoggers.ConfigService.LogUnknownBackdropStyle(key, raw, style);
        return style;
    }

    /// <summary>
    /// True iff the user's config file actually sets a value for this
    /// key. Distinguishes a user-authored override from an inherited
    /// theme/default value, which the typed accessors above conflate.
    /// </summary>
    public bool IsConfiguredInFile(string key)
        => _configFileCache is not null
            && _configFileCache.TryGetValue(key, out var list)
            && list.Count > 0;

    /// <summary>
    /// Raw cached first-line value for a config key, or empty if not
    /// set in the user's file. For UI paths that need to display a
    /// user-authored override for a key without a typed accessor on
    /// this service (e.g. selection-background).
    /// </summary>
    public string GetRawFileValue(string key) => GetFileValue(key, string.Empty);

    /// <summary>
    /// Read all values for a repeatable Windows-only config key from
    /// the cached snapshot. Returns each matching line's value in file
    /// order.
    /// </summary>
    private IReadOnlyList<string> GetAllFileValues(string key)
        => _configFileCache is not null
            && _configFileCache.TryGetValue(key, out var list)
            ? list
            : Array.Empty<string>();

    /// <summary>
    /// Load a ghostty-style ini file into a key/value dictionary.
    /// </summary>
    private static Dictionary<string, List<string>> LoadIniFile(string? path)
        => Ghostty.Core.Config.ConfigIniFile.Load(path);

    /// <summary>
    /// The built-in theme libghostty applies for <paramref name="isOsDark"/>
    /// when nothing is configured, parsed into the same shape a theme file
    /// gets. Null when the build has no built-in theme, which leaves the
    /// per-key defaults below in charge exactly as before.
    /// </summary>
    private static Dictionary<string, List<string>>? LoadBuiltinTheme(bool isOsDark)
    {
        var str = NativeMethods.ConfigBuiltinTheme(ToScheme(isOsDark));
        if (str.Ptr == IntPtr.Zero || str.Len == 0) return null;

        var text = Marshal.PtrToStringUTF8(str.Ptr, (int)str.Len);
        if (string.IsNullOrEmpty(text)) return null;

        // Static storage on the native side, so there is nothing to free.
        return Ghostty.Core.Config.ConfigIniFile.ParseText(text);
    }

    /// <summary>
    /// A simple colour from <paramref name="config"/>, packed 0x00RRGGBB, or
    /// null when the key is not found or is not a simple colour. libghostty
    /// returns colours as <c>ghostty_config_color_s { r: u8, g: u8, b: u8 }</c>
    /// (3 bytes, no padding).
    /// </summary>
    private static unsafe uint? GetColorFrom(GhosttyConfig config, string key)
    {
        if (config.Handle == IntPtr.Zero) return null;
        byte* colorBuf = stackalloc byte[3];
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        fixed (byte* keyPtr = keyBytes)
        {
            var found = NativeMethods.ConfigGet(
                config,
                (IntPtr)colorBuf,
                (IntPtr)keyPtr,
                (UIntPtr)keyBytes.Length);
            if (!found) return null;
        }
        return ((uint)colorBuf[0] << 16) | ((uint)colorBuf[1] << 8) | colorBuf[2];
    }

    /// <summary>
    /// Like <see cref="GetColorFrom"/> on the committed config: null when the key is
    /// not found or not a simple color.
    /// </summary>
    private unsafe uint? GetColorOrNull(string key)
    {
        byte* colorBuf = stackalloc byte[3];
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        fixed (byte* keyPtr = keyBytes)
        {
            var found = NativeMethods.ConfigGet(
                _config,
                (IntPtr)colorBuf,
                (IntPtr)keyPtr,
                (UIntPtr)keyBytes.Length);
            if (!found) return null;
        }
        return ((uint)colorBuf[0] << 16) | ((uint)colorBuf[1] << 8) | colorBuf[2];
    }

    /// <summary>
    /// Read all 16 palette colors. Loads the active theme's palette
    /// first (resolving light:X,dark:Y to the OS-active variant), then
    /// applies user-config overrides on top. Falls back to xterm
    /// defaults for indices that neither source sets.
    /// </summary>
    private uint[] GetAllPaletteColors()
    {
        // libghostty's own defaults, from Name.default in
        // src/terminal/color.zig -- NOT the xterm primaries. These were
        // xterm's, which meant an unconfigured install had the chrome
        // deriving from one palette while the terminal rendered another.
        // Reached only when neither the built-in theme nor a configured
        // one sets an index.
        uint[] defaults =
        [
            0x1D1F21, 0xCC6666, 0xB5BD68, 0xF0C674,
            0x81A2BE, 0xB294BB, 0x8ABEB7, 0xC5C8C6,
            0x666666, 0xD54E53, 0xB9CA4A, 0xE7C547,
            0x7AA6DA, 0xC397D8, 0x70C0B1, 0xEAEAEA,
        ];

        // Apply theme palette first (lower priority). Use the cached
        // theme file lines; re-reading the theme file here would be
        // its fifth-ish scan inside a single reload.
        if (_activeThemeFileCache is not null
            && _activeThemeFileCache.TryGetValue("palette", out var themePalette))
            ThemeParser.ApplyPaletteFromValues(themePalette, defaults);

        // Then apply user-config palette overrides on top.
        ThemeParser.ApplyPaletteFromValues(GetAllFileValues("palette"), defaults);

        return defaults;
    }

    /// <summary>
    /// Parse a hex color string (#RGB, #RRGGBB, or #AARRGGBB) into
    /// a <see cref="Windows.UI.Color"/>. Returns null if the string
    /// is empty or not a valid hex color.
    /// </summary>
    private static Windows.UI.Color? ParseHexColor(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var hex = value.TrimStart('#');
        try
        {
            return hex.Length switch
            {
                // #RGB -> expand to #RRGGBB
                3 => Windows.UI.Color.FromArgb(0xFF,
                    byte.Parse(new string(hex[0], 2), System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(new string(hex[1], 2), System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(new string(hex[2], 2), System.Globalization.NumberStyles.HexNumber)),
                // #RRGGBB
                6 => Windows.UI.Color.FromArgb(0xFF,
                    byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber)),
                // #AARRGGBB
                8 => Windows.UI.Color.FromArgb(
                    byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(hex[6..8], System.Globalization.NumberStyles.HexNumber)),
                _ => null,
            };
        }
        catch (FormatException) { return null; }
    }

    private static float? ParseFloat(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? Math.Clamp(result, 0f, 1f)
            : null;
    }

    /// <summary>
    /// Parse a gradient point string: "x,y,#color,radius".
    /// Returns null if the format is invalid.
    /// </summary>
    private static GradientPoint? ParseGradientPoint(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) return null;

        if (!float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x))
            return null;
        if (!float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y))
            return null;
        var color = ParseHexColor(parts[2]);
        if (color is null) return null;
        if (!float.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var radius))
            return null;

        return new GradientPoint(
            Math.Clamp(x, 0f, 1f),
            Math.Clamp(y, 0f, 1f),
            color.Value,
            Math.Clamp(radius, 0f, 1f));
    }

    private void StartWatcher()
    {
        // A reload re-applies --no-config, so a watched save could not
        // actually take effect; arming it would just spend a reload per
        // keystroke in the user's editor on rebuilding the same config.
        if (_noConfig) return;
        if (_watcher != null) return;

        // Editors save by swapping a temp file in, so the file is briefly
        // absent and one save arrives as a burst of events. ConfigFileWatcher
        // collapses the burst into one settle and reports no edit while the
        // file is missing, which saves a rebuild Reload would only decline.
        // Reload is what actually refuses to apply a config it could not
        // read; the watcher's check is an early-out in front of it, not the
        // guarantee (issue #676).
        //
        // It does report the file being gone, separately, and that report
        // says only "gone as of this delivery": an ordinary swap reaches it
        // whenever it straddles the hop from the timer to the delivery.
        // OnConfigFileVanished is what the session does about one, and what
        // it does first is ask again (issue #1146).
        //
        // Suppression is decided when each event arrives, not when the
        // debounce fires, so a write bracketed by SuppressWatcher stays
        // ignored even though its settle would land after the bracket.
        //
        // BeginShutdown disposes the watcher on the UI thread, and the timer
        // waits (bounded) for a callback already running. A settle only
        // enqueues, so that wait is short. The one slow callback is a rebuild
        // after a watcher error, which probes the directory and can block on
        // an unreachable share; the timer logs when the wait times out.
        var watcher = new ConfigFileWatcher(
            ConfigFilePath,
            new SystemSchedulerTimer(StaticLoggers.ConfigWatcherTimer),
            TimeSpan.FromMilliseconds(300),
            ignoreEvents: () => _suppressWatcher || _shuttingDown,
            post: deliver => _dispatcher.TryEnqueue(() => deliver()),
            onSettled: OnConfigFileSettled,
            StaticLoggers.ConfigService,
            onVanished: OnConfigFileVanished);
        var started = false;
        try
        {
            started = watcher.Start();
        }
        finally
        {
            // The watcher owns the timer, so this frees both if Start
            // refused or threw.
            if (!started) watcher.Dispose();
        }
        if (started) _watcher = watcher;
    }

    private void StopWatcher()
    {
        var watcher = _watcher;
        _watcher = null;
        watcher?.Dispose();
    }

    /// <summary>
    /// One settled edit of the config file, on the UI thread, called by the
    /// watcher's posted delivery only after it found the file present.
    /// BeginShutdown disposes the watcher, which cancels a pending settle and
    /// turns a delivery already queued into a no-op; Reload's own
    /// <c>_shuttingDown</c> check fences the rest (issue #208).
    /// </summary>
    private void OnConfigFileSettled()
    {
        if (_shuttingDown) return;

        // No vanish call here. This knows only that File.Exists was true,
        // which a dangling symlink satisfies while its open returns
        // not-found, so restoring the question on it cleared what the reload
        // below then spent and the count oscillated without ever concluding. The
        // protocol is driven by the load's VERDICT, inside Reload
        // (wintty#1155).
        Reload();
    }

    /// <summary>
    /// The watcher's delivery found the config file gone. Reload, so the
    /// load's verdict becomes the observation.
    /// </summary>
    /// <remarks>
    /// The watcher can only say the path did not exist at the instant it
    /// looked, which an ordinary save produces and which is issue #1146. The
    /// question is answered by a LOAD, so this concludes nothing itself; it
    /// goes and gets the evidence, and everything downstream hangs off the
    /// verdict in <see cref="Reload"/>.
    ///
    /// The rebuild that costs is the point rather than waste. A deleted file
    /// raises no further filesystem events, so without going and looking
    /// there is nothing to observe, and the session would refuse every
    /// reload for the life of the process: issue #676 reached through its
    /// own fix (wintty#1155).
    /// </remarks>
    private void OnConfigFileVanished()
    {
        if (_shuttingDown) return;
        Reload();
    }

    /// <summary>
    /// A look the service scheduled for itself fired, posted to the UI
    /// thread: reload, so its verdict is the next answer to whichever
    /// question asked for it. A fire that finds the question already over
    /// costs one reload of an unchanged config and nothing else.
    /// </summary>
    private void OnLookAgain()
    {
        if (_shuttingDown) return;
        Reload();
    }

    /// <summary>
    /// The one writer of <c>_defaultFilesFound</c>: how many default config
    /// files the config now in force was built from.
    /// </summary>
    /// <remarks>
    /// Pinned at zero under <c>--no-config</c>, where the config file is
    /// ignored by policy. Gating on a file this launch does not read would
    /// let its absence refuse the High Contrast and OS-theme reloads, which
    /// are the only reloads such a launch has: there is no watcher either,
    /// so nothing would ever ask again.
    /// </remarks>
    private void RecordDefaultFiles(int found) =>
        _defaultFilesFound = _noConfig ? 0 : found;

    /// <summary>
    /// Stop applying config reloads ahead of app teardown. After this,
    /// queued or debounced <see cref="Reload"/> calls no-op instead of
    /// calling <c>AppUpdateConfig</c> on a libghostty app that is about to
    /// be (or has been) freed by the bootstrap host's AppFree. Called from
    /// <c>App.OnAnyWindowClosedInternal</c> before that teardown. Idempotent;
    /// safe to call before <see cref="Dispose"/>. Issue #208.
    /// </summary>
    public void BeginShutdown()
    {
        _shuttingDown = true;
        StopWatcher();
        _lookAgain.Stop();
    }

    /// <summary>
    /// Static rather than a lambda so the fan-out does not allocate a
    /// closure per reload, and so both call sites provably report the same
    /// way.
    /// </summary>
    private static void LogChangedHandlerFault(Exception ex)
        => StaticLoggers.ConfigService.LogChangedHandlerFailed(ex);

    public void Dispose()
    {
        BeginShutdown();
        _lookAgain.Dispose();
        // A preview still up at teardown: the revert is fenced off once
        // shutdown starts, so whatever preview config is left is freed here,
        // after the app that cloned it.
        if (_previewConfig.Handle != IntPtr.Zero)
        {
            NativeMethods.ConfigFree(_previewConfig);
            _previewConfig = default;
        }
        if (_config.Handle != IntPtr.Zero)
            NativeMethods.ConfigFree(_config);
    }
}

internal static partial class ConfigServiceLogExtensions
{
    // Information, not Warning: nothing is broken and nothing is lost. The
    // running config stays in force and the save that is in flight raises its
    // own event when it lands, so the usual outcome is one of these followed
    // immediately by a reload that works. It is logged at all because the
    // alternative reading of a config file that stays away is that the user
    // deleted it, and then this line is the only account of why the app is
    // still running on settings that are no longer on disk.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.ReloadFoundNoFile,
                   Level = LogLevel.Information,
                   Message = "[ConfigService] Keeping the running config: {Path} was not applied because {Reason}")]
    internal static partial void LogReloadKeptRunningConfig(
        this ILogger<ConfigService> logger, string path, string reason);

    // Warning, unlike the line above, and logged once per stretch: the retry
    // budget is spent, so nothing is going to ask again, and the edit the
    // user saved is not in force until they save once more or restart. That
    // is a functional degradation, and this line is the only account of it.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.ReloadGaveUp,
                   Level = LogLevel.Warning,
                   Message = "[ConfigService] Config file still unreadable after {Attempts} attempts, keeping the running config until the next save: {Path}")]
    internal static partial void LogReloadGaveUp(
        this ILogger<ConfigService> logger, int attempts, string path);

    // A preview, not a reload, so it says so: the running config is being
    // kept because the preview could not be built over it, and nothing was
    // reloaded. Sharing the reload's line made a palette browse report
    // "Keeping the running config" for something that never asked to change
    // it, which reads in a log as a reload that happened.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.ThemePreviewKeptConfig,
                   Level = LogLevel.Information,
                   Message = "[ConfigService] Not previewing {Theme}: {Path} was not read because {Reason}")]
    internal static partial void LogThemePreviewKeptRunningConfig(
        this ILogger<ConfigService> logger, string theme, string path, string reason);

    // Information: the running config is untouched and nothing is lost. What
    // it records is that the session stopped treating the config file as
    // present, which is what lets later reloads apply again. A save in
    // flight never reaches here, so a line like this means the file really
    // is gone.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.ConfigFileVanished,
                   Level = LogLevel.Information,
                   Message = "[ConfigService] Config file is gone, keeping the running config: {Path}")]
    internal static partial void LogConfigFileVanished(
        this ILogger<ConfigService> logger, string path);

    // Information, and the account of a deletion the watcher cannot see:
    // the missing file is one of the layered candidates it does not watch,
    // so no event ever names it. Logged on the reload that stops refusing
    // and applies what is left, which is also what lowers the session
    // count; without that line a log would show a run of declines ending
    // in an unexplained apply.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.ReloadDefaultFilesShrunk,
                   Level = LogLevel.Information,
                   Message = "[ConfigService] A layered config file is gone for good; the session ran on {Before} default files and {Now} remain, so this reload applies the configuration that is left: {Path}")]
    internal static partial void LogReloadDefaultFilesShrunk(
        this ILogger<ConfigService> logger, int before, int now, string path);

    // Warning: the palette keeps showing whatever it showed before, and the
    // theme can still be chosen from Settings or the config file.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.ThemePreviewFailed,
                   Level = LogLevel.Warning,
                   Message = "[ConfigService] Could not preview theme {ThemeName} on the live views")]
    internal static partial void LogThemePreviewFailed(
        this ILogger<ConfigService> logger, System.Exception ex, string themeName);

    // LogLevel.Error (not Warning) because a failed reload leaves the
    // previous config in place; the user's edit silently stops being
    // applied, which is a genuine functional degradation.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.ReloadFailed,
                   Level = LogLevel.Error,
                   Message = "[ConfigService] Reload failed to create new config")]
    internal static partial void LogReloadFailed(
        this ILogger<ConfigService> logger, System.Exception ex);

    // Warning, not Error: the user just loses the first-launch comment
    // header. Functionally the config still loads and the file is
    // still editable.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.SeedFailed,
                   Level = LogLevel.Warning,
                   Message = "[ConfigService] Could not seed comment header into empty config file")]
    internal static partial void LogSeedFailed(
        this ILogger<ConfigService> logger, System.Exception ex);

    // Error: the subscriber that threw did not apply the new config, so
    // some part of the UI is now painting against a config the rest of the
    // app has moved past. Containing it keeps the process alive and leaves
    // a stack to triage, which a stowed exception does not.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.ChangedHandlerFailed,
                   Level = LogLevel.Error,
                   Message = "[ConfigService] A config-changed subscriber threw; its view of the config is now stale")]
    internal static partial void LogChangedHandlerFailed(
        this ILogger<ConfigService> logger, System.Exception ex);

    // Error, matching LogReloadFailed: the themed values keep resolving
    // against the outgoing scheme, so window chrome stays on the wrong
    // palette until something else moves it.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.ThemeRefreshFailed,
                   Level = LogLevel.Error,
                   Message = "[ConfigService] Failed to re-resolve themed values after an OS colour scheme change")]
    internal static partial void LogThemeRefreshFailed(
        this ILogger<ConfigService> logger, System.Exception ex);

    // Distinct from LogReloadFailed: there the config was never built and
    // nothing changed, here it was built and pushed and only the C#
    // snapshot is behind. The two want opposite triage, so they must not
    // share a message.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.SnapshotRefreshFailed,
                   Level = LogLevel.Error,
                   Message = "[ConfigService] Config applied natively but the C# snapshot failed to refresh")]
    internal static partial void LogSnapshotRefreshFailed(
        this ILogger<ConfigService> logger, System.Exception ex);

    // Warning, not Error: the window still comes up, the user just does
    // not get the material they asked for. The key is in the message
    // because more than one config key folds through the same parser, and
    // an unattributed complaint sends the reader down the wrong line.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.UnknownBackdropStyle,
                   Level = LogLevel.Warning,
                   Message = "[ConfigService] {Key} = '{Value}' is not a known style; "
                             + "using '{Fallback}'. Accepted: solid, frosted, crystal")]
    internal static partial void LogUnknownBackdropStyle(
        this ILogger<ConfigService> logger, string key, string value, string fallback);

    // Surfaces each warning string returned by
    // ConfigServiceProfileParser.ParseAll so admin-visible parse
    // issues (malformed profile blocks, unknown ids, etc.) land in
    // the log stream rather than only in the _profileWarnings
    // field. Warning level because the reload still succeeds --
    // the offending block is just skipped.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Profiles.ProfileParseWarning,
                   Level = LogLevel.Warning,
                   Message = "[ConfigService] profile parse: {Warning}")]
    internal static partial void LogProfileParseWarning(
        this ILogger<ConfigService> logger, string warning);
}
