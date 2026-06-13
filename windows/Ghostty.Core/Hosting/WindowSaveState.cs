namespace Ghostty.Core.Hosting;

/// <summary>
/// Mirrors the core <c>window-save-state</c> config key. <c>Default</c>
/// restores the previous session only after a clean exit; <c>Always</c>
/// restores even after a crash; <c>Never</c> disables restoration.
/// Matches the macOS semantics of the same key.
/// </summary>
public enum WindowSaveState
{
    Default,
    Never,
    Always,
}

public static class WindowSaveStateExtensions
{
    /// <summary>
    /// Parse a libghostty-formatted enum tag. Unknown/null/blank falls
    /// back to <see cref="WindowSaveState.Default"/>, matching the
    /// resilient-to-config-typos philosophy used by the other enum parsers.
    /// </summary>
    public static WindowSaveState Parse(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "never" => WindowSaveState.Never,
            "always" => WindowSaveState.Always,
            _ => WindowSaveState.Default,
        };
}
