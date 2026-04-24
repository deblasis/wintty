using System;
using System.Collections.Generic;

namespace Ghostty.Core.Config;

/// <summary>
/// Owns the libghostty config lifecycle: load, reload, and
/// change notification. Consumers subscribe to ConfigChanged
/// and re-read values they care about.
/// </summary>
public interface IConfigService : IDisposable
{
    /// <summary>Fired on the UI thread after a successful config reload.</summary>
    event Action<IConfigService>? ConfigChanged;

    /// <summary>Path to the active config file.</summary>
    string ConfigFilePath { get; }

    /// <summary>Whether auto-reload-config is enabled.</summary>
    bool AutoReloadEnabled { get; }

    /// <summary>Whether windows-settings-ui is enabled.</summary>
    bool SettingsUiEnabled { get; }

    /// <summary>
    /// Background opacity from config (0.0 fully transparent, 1.0 opaque).
    /// </summary>
    double BackgroundOpacity { get; }

    /// <summary>
    /// Windows-only: true when tabs render in a vertical sidebar
    /// instead of the default horizontal strip. Default false.
    /// Backed by the "vertical-tabs" key in the user's config file.
    /// </summary>
    bool VerticalTabs { get; }

    /// <summary>
    /// Windows-only: true when the command palette groups entries
    /// by category. Default false. Backed by the
    /// "command-palette-group-commands" key.
    /// </summary>
    bool CommandPaletteGroupCommands { get; }

    /// <summary>
    /// Windows-only: backdrop material for the command palette.
    /// One of "acrylic", "mica", "opaque". Default "acrylic".
    /// Backed by the "command-palette-background" key.
    /// </summary>
    string CommandPaletteBackground { get; }

    /// <summary>
    /// Minimum log level for the Windows shell's diagnostic logger,
    /// read from the <c>log-level</c> config key. One of
    /// <c>trace</c>, <c>debug</c>, <c>info</c>, <c>warn</c>, <c>error</c>,
    /// <c>off</c>. Defaults to <c>info</c> when unset.
    /// Parsed into a <c>LoggerFilterOptions.MinLevel</c> by
    /// <c>Ghostty.Core.Logging.LoggingBootstrap</c>.
    /// </summary>
    string LogLevel { get; }

    /// <summary>
    /// Comma-separated <c>CATEGORY=LEVEL</c> pairs overriding
    /// <see cref="LogLevel"/> per-category, read from the
    /// <c>log-filter</c> config key. Empty when unset.
    /// </summary>
    string LogFilter { get; }

    /// <summary>
    /// Window theme from config: "light", "dark", "system", "auto", or
    /// "ghostty" (palette-derived chrome). Controls both the XAML
    /// ElementTheme and the DWM title bar chrome.
    /// </summary>
    string WindowTheme { get; }

    /// <summary>
    /// Background color from config as packed 0x00RRGGBB (no alpha).
    /// Used by the "auto" window-theme mode to derive light/dark from
    /// the terminal background luminance.
    /// </summary>
    uint BackgroundColor { get; }

    /// <summary>
    /// Re-read config from disk and apply it. Returns true on
    /// success, false if the reload failed (old config stays active).
    /// </summary>
    bool Reload();

    /// <summary>
    /// Temporarily suppress or resume file-system watcher events.
    /// Call with true before writing to the config file, false after,
    /// to avoid a redundant reload from your own save.
    /// </summary>
    void SuppressWatcher(bool suppress);

    /// <summary>
    /// Number of real diagnostics from the last load/reload. This
    /// excludes "unknown field" noise for keys we know are
    /// Windows-only (see <see cref="WindowsOnlyKeys"/>); those are
    /// surfaced separately via <see cref="WindowsOnlyKeysUsed"/>.
    /// </summary>
    int DiagnosticsCount { get; }

    /// <summary>Get diagnostic message at the given index.</summary>
    string GetDiagnostic(int index);

    /// <summary>
    /// Windows-only config keys that appeared in the user's config
    /// during the last load/reload. libghostty flags these as unknown
    /// but we handle them ourselves, so the settings UI surfaces them
    /// as an informational notice (not an error).
    /// </summary>
    IReadOnlyList<string> WindowsOnlyKeysUsed { get; }

    /// <summary>
    /// Parsed user-defined profiles from <c>profile.&lt;id&gt;.*</c>
    /// lines. Keys are lowercase-ASCII ids. Replaced on every reload.
    /// See <c>Ghostty.Core.Profiles.IProfileConfigSource</c> for the
    /// narrow interface that <c>ProfileRegistry</c> depends on.
    /// </summary>
    IReadOnlyDictionary<string, Ghostty.Core.Profiles.ProfileDef> ParsedProfiles { get; }

    /// <summary>
    /// Ids from <c>profile-order</c>, empty when unset.
    /// </summary>
    IReadOnlyList<string> ProfileOrder { get; }

    /// <summary>
    /// Value of <c>default-profile</c>, or null when unset.
    /// </summary>
    string? DefaultProfileId { get; }

    /// <summary>
    /// Ids for which <c>profile.&lt;id&gt;.hidden = true</c> appears.
    /// </summary>
    IReadOnlySet<string> HiddenProfileIds { get; }

    /// <summary>
    /// Non-fatal warnings emitted by the profile parser during the
    /// last reload.
    /// </summary>
    IReadOnlyList<string> ProfileWarnings { get; }
}
