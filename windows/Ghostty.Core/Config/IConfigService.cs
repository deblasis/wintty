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
}
