using System;

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

    /// <summary>Number of diagnostics from the last load/reload.</summary>
    int DiagnosticsCount { get; }

    /// <summary>Get diagnostic message at the given index.</summary>
    string GetDiagnostic(int index);
}
