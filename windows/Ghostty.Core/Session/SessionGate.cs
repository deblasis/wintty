using Ghostty.Core.Hosting;

namespace Ghostty.Core.Session;

/// <summary>
/// Decides, from the config key and the last-exit cleanliness, whether to
/// restore a saved session and whether to persist at all. Matches macOS:
/// <c>default</c> restores only after a clean exit, <c>always</c> always,
/// <c>never</c> not.
/// </summary>
internal static class SessionGate
{
    public static bool ShouldRestore(WindowSaveState state, bool cleanShutdown) =>
        state switch
        {
            WindowSaveState.Never => false,
            WindowSaveState.Always => true,
            _ => cleanShutdown, // Default
        };

    /// <summary>Whether to write session state at all (Never disables persistence).</summary>
    public static bool ShouldPersist(WindowSaveState state) =>
        state != WindowSaveState.Never;
}
