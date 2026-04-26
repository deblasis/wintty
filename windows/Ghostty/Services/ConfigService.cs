using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Ghostty.Core.Config;
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
/// successful reload so consumers can re-read values they depend on.
/// </summary>
internal sealed class ConfigService : IConfigService, Ghostty.Core.Profiles.IProfileConfigSource
{
    private GhosttyConfig _config;
    private GhosttyApp _app;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly Lock _timerLock = new();
    private readonly DispatcherQueue _dispatcher;
    private volatile bool _suppressWatcher;

    public event Action<IConfigService>? ConfigChanged;
    public string ConfigFilePath { get; }
    public bool AutoReloadEnabled { get; private set; }
    public bool SettingsUiEnabled { get; private set; }
    public double BackgroundOpacity { get; private set; } = 1.0;

    // Cached during ReadFlagsCore while _configFileCache is populated;
    // the cache is nulled in ReadFlags' finally, so expression-bodied
    // getters reading GetFileValue would always return defaults outside
    // of reload. Backing-field pattern matches the existing
    // BackgroundStyle / BackgroundTint* properties below.
    public bool VerticalTabs { get; private set; }
    public bool CommandPaletteGroupCommands { get; private set; }
    public string CommandPaletteBackground { get; private set; } = "acrylic";
    public string LogLevel { get; private set; } = "info";
    public string LogFilter { get; private set; } = string.Empty;

    private static readonly string[] CommandPaletteBackgroundAllowed =
        { "acrylic", "mica", "opaque" };

    public string BackgroundStyle { get; private set; } = "frosted";
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
    public uint ForegroundColor { get; private set; } = 0x00FFFFFF;
    public uint BackgroundColor { get; private set; } = 0x001E1E2E;
    public uint? CursorColor { get; private set; }
    public uint? CursorTextColor { get; private set; }
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
    /// top of <see cref="ReadFlags"/> and cleared when it exits. Every
    /// file-read helper on this class reads from here instead of
    /// reopening the file; otherwise one reload turns into one
    /// <see cref="File.ReadLines"/> per key looked up, which the config
    /// watcher then amplifies on every save. Keys are case-insensitive
    /// (matches ghostty's own config parser), each key maps to the
    /// list of raw values in the order they appear in the file.
    /// </summary>
    private Dictionary<string, List<string>>? _configFileCache;

    /// <summary>
    /// Same idea as <see cref="_configFileCache"/> but for the active
    /// theme file resolved from the config (handling the light:X,dark:Y
    /// split on the OS scheme). Null when there's no active theme or
    /// the theme file is missing.
    /// </summary>
    private Dictionary<string, List<string>>? _activeThemeFileCache;

    /// <summary>
    /// The current config handle. Passed to <see cref="GhosttyHost"/>
    /// so it can create the app with the loaded config.
    /// </summary>
    public GhosttyConfig ConfigHandle => _config;

    public ConfigService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        NativeMethods.Init(UIntPtr.Zero, IntPtr.Zero);

        _config = NativeMethods.ConfigNew();
        NativeMethods.ConfigLoadDefaultFiles(_config);
        NativeMethods.ConfigFinalize(_config);

        var pathStr = NativeMethods.ConfigOpenPath();
        var rawPath = pathStr.Ptr != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(pathStr.Ptr, (int)pathStr.Len) ?? string.Empty
            : string.Empty;
        // Normalize mixed separators from Zig (forward slash) + Windows
        // (backslash) so the path looks clean in UI and logs.
        ConfigFilePath = Path.GetFullPath(rawPath);

        CacheDiagnostics();
        ReadFlags();
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
        // Don't reload before the app is created -- the initial config
        // is the one passed to ghostty_app_new and must stay alive.
        if (_app.Handle == IntPtr.Zero) return false;

        GhosttyConfig newConfig;
        try
        {
            newConfig = NativeMethods.ConfigNew();
            NativeMethods.ConfigLoadDefaultFiles(newConfig);
            NativeMethods.ConfigFinalize(newConfig);
        }
        catch (Exception ex)
        {
            StaticLoggers.ConfigService.LogReloadFailed(ex);
            return false;
        }

        var oldConfig = _config;

        // Suppress the watcher for the duration of the update so our
        // own config swap doesn't trigger a redundant file-change reload.
        var wasSuppressed = _suppressWatcher;
        _suppressWatcher = true;

        NativeMethods.AppUpdateConfig(_app, newConfig);

        _config = newConfig;
        CacheDiagnostics();
        ReadFlags();

        if (oldConfig.Handle != IntPtr.Zero)
            NativeMethods.ConfigFree(oldConfig);

        _suppressWatcher = wasSuppressed;

        _dispatcher.TryEnqueue(() =>
        {
            ConfigChanged?.Invoke(this);
            ProfileConfigChanged?.Invoke();
        });
        return true;
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
    public void SuppressWatcher(bool suppress) => _suppressWatcher = suppress;

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

    private void ReadFlags()
    {
        // Snapshot the config file once up front, and the active theme
        // file once after we know which one to read. Everything below
        // that looks up Windows-only keys or theme colors goes through
        // these caches, so the whole reload is bounded by at most two
        // File.ReadLines calls regardless of how many keys we probe.
        _configFileCache = LoadIniFile(ConfigFilePath);
        var activeTheme = ResolveActiveThemeName();
        var themePath = string.IsNullOrEmpty(activeTheme) ? null : ResolveThemePath(activeTheme);
        _activeThemeFileCache = themePath is null ? null : LoadIniFile(themePath);

        try
        {
            ReadFlagsCore();
        }
        finally
        {
            _configFileCache = null;
            _activeThemeFileCache = null;
        }
    }

    private void ReadFlagsCore()
    {
        AutoReloadEnabled = GetBool("auto-reload-config");
        // windows-settings-ui is a Windows-only key not in the Zig
        // config schema, so read it from the config file directly.
        SettingsUiEnabled = string.Equals(
            GetFileValue("windows-settings-ui", "false"),
            "true", StringComparison.OrdinalIgnoreCase);
        // Clamp here so all consumers get a safe [0,1] value without
        // needing their own validation. WindowTransparencyState also
        // clamps defensively as a standalone value type.
        BackgroundOpacity = Math.Clamp(GetDouble("background-opacity", 1.0), 0.0, 1.0);
        // Windows-only UI keys migrated from the legacy ui-settings.json
        // shim. Cached here because GetFileValue only works while
        // _configFileCache is populated (ReadFlags' scope).
        VerticalTabs = WindowsOnlyKeyParsers.ParseBool(
            GetFileValue("vertical-tabs", ""),
            defaultValue: false);
        CommandPaletteGroupCommands = WindowsOnlyKeyParsers.ParseBool(
            GetFileValue("command-palette-group-commands", ""),
            defaultValue: false);
        CommandPaletteBackground = WindowsOnlyKeyParsers.ParseStringAllowed(
            GetFileValue("command-palette-background", ""),
            allowed: CommandPaletteBackgroundAllowed,
            defaultValue: "acrylic");
        // Windows-only logger keys. Parsing into LogLevel/filter rules
        // happens in LoggingBootstrap; here we just surface the raw
        // strings so reloads can re-read them without parser knowledge.
        LogLevel = GetFileValue("log-level", "info");
        LogFilter = GetFileValue("log-filter", string.Empty);

        // background-style is a Windows-only key not in the Zig config
        // schema, so we read it directly from the config file.
        BackgroundStyle = GetFileValue("background-style", "frosted");
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

        // For background and foreground we go through GetThemeValue first
        // because libghostty's _config was finalized with the default
        // (.light) conditional state, so for a pair theme in dark mode
        // it would return the LIGHT theme's colors. GetThemeValue resolves
        // the active theme name (light vs dark) based on OS state.
        BackgroundColor = ResolveThemedColor("background", 0x001E1E2E);
        ForegroundColor = ResolveThemedColor("foreground", 0x00FFFFFF);

        // cursor-color is a TerminalColor (tagged union) in the Zig
        // config, so it can't be read via ghostty_config_get as a
        // simple color. Read from the resolved config files instead.
        var cursorHex = GetThemeValue("cursor-color");
        if (!string.IsNullOrEmpty(cursorHex))
        {
            var parsed = ParseHexColor(cursorHex);
            CursorColor = parsed is not null
                ? ((uint)parsed.Value.R << 16) | ((uint)parsed.Value.G << 8) | parsed.Value.B
                : ForegroundColor;
        }
        else
        {
            CursorColor = ForegroundColor;
        }

        var cursorTextHex = GetThemeValue("cursor-text");
        if (!string.IsNullOrEmpty(cursorTextHex))
        {
            var parsed = ParseHexColor(cursorTextHex);
            CursorTextColor = parsed is not null
                ? ((uint)parsed.Value.R << 16) | ((uint)parsed.Value.G << 8) | parsed.Value.B
                : BackgroundColor;
        }
        else
        {
            CursorTextColor = BackgroundColor;
        }

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
            GetFileValue("mouse-hide-while-typing", "false"),
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
        // and profile-order) are read through GetFileValue which hits
        // the parsed line cache populated at the top of ReadFlags.
        // The profile.<id>.* regex and hidden-id extraction need the
        // raw file text though, and _configFileCache stores parsed
        // pairs rather than the original bytes, so this second read is
        // deliberate -- the reload is already cold-path and the config
        // is small. Readers of the five profile-view properties see a
        // consistent snapshot via the single volatile _profileView
        // assignment below.
        var rawConfigText = File.Exists(ConfigFilePath)
            ? File.ReadAllText(ConfigFilePath)
            : string.Empty;
        var view = Ghostty.Core.Config.ConfigServiceProfileParser.ParseAll(
            rawConfigText,
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
        ForegroundColor = fg;
        BackgroundColor = bg;
        CursorColor = cursor ?? fg;
        CursorTextColor = cursorText ?? bg;
        if (palette.Length >= 16)
            Array.Copy(palette, AnsiPalette, 16);
        ConfigChanged?.Invoke(this);
    }

    private unsafe bool GetBool(string key)
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
            return found && result != 0;
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
    /// current OS color scheme. For a single theme, returns it as-is.
    /// </summary>
    private string ResolveActiveThemeName()
    {
        var raw = GetFileValue("theme", "");
        if (string.IsNullOrEmpty(raw)) return "";

        var (light, dark) = ThemeParser.ParseThemePair(raw);
        if (light is null || dark is null) return raw;

        return IsOsDark() ? dark : light;
    }

    /// <summary>
    /// Find the theme file on disk by name. Looks in the user themes
    /// directory next to the config file. Returns null if not found.
    /// </summary>
    private string? ResolveThemePath(string themeName)
    {
        var configDir = Path.GetDirectoryName(ConfigFilePath);
        if (string.IsNullOrEmpty(configDir)) return null;
        var themePath = Path.Combine(configDir, "themes", themeName);
        return File.Exists(themePath) ? themePath : null;
    }

    /// <summary>
    /// Read a color, preferring user config over the active theme file
    /// over the libghostty default. This is needed because libghostty's
    /// _config is finalized with the default (.light) conditional state,
    /// so for pair themes in dark mode it returns the wrong colors.
    /// </summary>
    private uint ResolveThemedColor(string key, uint defaultValue)
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
        return GetColor(key, defaultValue);
    }

    /// <summary>
    /// Detect OS dark mode via the shared <see cref="OsTheme"/> helper
    /// so this and <see cref="WindowThemeManager"/> can't drift on the
    /// "which byte means dark" heuristic.
    /// </summary>
    private static bool IsOsDark() => OsTheme.IsDark();

    /// <summary>
    /// Read the first non-empty value for a Windows-only config key
    /// from the cached snapshot of the config file populated by
    /// <see cref="ReadFlags"/>. Keys not in the Zig config schema
    /// cannot be read via <c>ghostty_config_get</c>, so we parse the
    /// file ourselves.
    /// </summary>
    private string GetFileValue(string key, string defaultValue)
        => _configFileCache is not null
            && _configFileCache.TryGetValue(key, out var list)
            && list.Count > 0
            ? list[0]
            : defaultValue;

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
    /// Load a ghostty-style ini file into a key/value dictionary. Empty
    /// lines and #-prefixed comments are skipped, empty values are
    /// ignored entirely (matching the old per-key scanners), and keys
    /// are matched case-insensitively. Returns an empty dictionary if
    /// the path is missing or unreadable.
    /// </summary>
    private static Dictionary<string, List<string>> LoadIniFile(string? path)
    {
        var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return dict;

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex < 0) continue;
            var k = trimmed[..eqIndex].Trim();
            if (k.Length == 0) continue;
            var v = trimmed[(eqIndex + 1)..].Trim();
            if (v.Length == 0) continue;
            if (!dict.TryGetValue(k, out var list))
            {
                list = new List<string>(1);
                dict[k] = list;
            }
            list.Add(v);
        }
        return dict;
    }

    /// <summary>
    /// Read a color config value. libghostty returns colors as
    /// <c>ghostty_config_color_s { r: u8, g: u8, b: u8 }</c>.
    /// We pack it into 0x00RRGGBB for easy consumption.
    /// </summary>
    private unsafe uint GetColor(string key, uint defaultValue)
    {
        // ghostty_config_color_s is 3 bytes: r, g, b (no padding).
        byte r = 0, g = 0, b = 0;
        // Stack a 3-byte buffer for the color struct.
        byte* colorBuf = stackalloc byte[3];
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        fixed (byte* keyPtr = keyBytes)
        {
            var found = NativeMethods.ConfigGet(
                _config,
                (IntPtr)colorBuf,
                (IntPtr)keyPtr,
                (UIntPtr)keyBytes.Length);
            if (!found) return defaultValue;
            r = colorBuf[0];
            g = colorBuf[1];
            b = colorBuf[2];
        }
        return ((uint)r << 16) | ((uint)g << 8) | b;
    }

    /// <summary>
    /// Like <see cref="GetColor"/> but returns null when the key is
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
        uint[] defaults =
        [
            0x000000, 0xCC0000, 0x00CC00, 0xCCCC00,
            0x0000CC, 0xCC00CC, 0x00CCCC, 0xCCCCCC,
            0x666666, 0xFF0000, 0x00FF00, 0xFFFF00,
            0x0000FF, 0xFF00FF, 0x00FFFF, 0xFFFFFF,
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
        if (_watcher != null) return;
        var dir = Path.GetDirectoryName(ConfigFilePath);
        var file = Path.GetFileName(ConfigFilePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return;
        if (!Directory.Exists(dir)) return;

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += (s, e) => OnFileChanged(s, e);
    }

    private void StopWatcher()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        if (_suppressWatcher) return;
        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                _dispatcher.TryEnqueue(() => Reload());
            }, null, 300, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        StopWatcher();
        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
        }
        if (_config.Handle != IntPtr.Zero)
            NativeMethods.ConfigFree(_config);
    }
}

internal static partial class ConfigServiceLogExtensions
{
    // LogLevel.Error (not Warning) because a failed reload leaves the
    // previous config in place; the user's edit silently stops being
    // applied, which is a genuine functional degradation.
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Config.ReloadFailed,
                   Level = LogLevel.Error,
                   Message = "[ConfigService] Reload failed to create new config")]
    internal static partial void LogReloadFailed(
        this ILogger<ConfigService> logger, System.Exception ex);

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
