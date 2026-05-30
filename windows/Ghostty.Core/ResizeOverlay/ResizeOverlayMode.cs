namespace Ghostty.Core.ResizeOverlay;

/// <summary>
/// When the resize overlay (the cols x rows pill shown while a pane is
/// resized) is allowed to appear. Mirrors the libghostty `resize-overlay`
/// config key (always / never / after-first).
/// </summary>
public enum ResizeOverlayMode
{
    /// <summary>Show on every grid-size change.</summary>
    Always,

    /// <summary>Never show.</summary>
    Never,

    /// <summary>
    /// Do not show on the initial layout, only on subsequent changes.
    /// This is the libghostty default and avoids flashing the overlay
    /// when a pane first appears.
    /// </summary>
    AfterFirst,
}

public static class ResizeOverlayModeExtensions
{
    /// <summary>
    /// Parse a libghostty-formatted enum tag. Unknown or null falls back
    /// to <see cref="ResizeOverlayMode.AfterFirst"/> (the upstream
    /// default), matching the resilient-to-config-typos philosophy.
    /// </summary>
    public static ResizeOverlayMode Parse(string? raw) => raw switch
    {
        "always" => ResizeOverlayMode.Always,
        "never" => ResizeOverlayMode.Never,
        "after-first" => ResizeOverlayMode.AfterFirst,
        _ => ResizeOverlayMode.AfterFirst,
    };
}
