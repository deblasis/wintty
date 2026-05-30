namespace Ghostty.Core.ResizeOverlay;

/// <summary>
/// Where the resize overlay pill sits inside the pane. Mirrors the
/// libghostty `resize-overlay-position` config key.
/// </summary>
public enum ResizeOverlayPosition
{
    Center,
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

public static class ResizeOverlayPositionExtensions
{
    /// <summary>
    /// Parse a libghostty-formatted enum tag. Unknown or null falls back
    /// to <see cref="ResizeOverlayPosition.Center"/> (the upstream
    /// default).
    /// </summary>
    public static ResizeOverlayPosition Parse(string? raw) => raw switch
    {
        "center" => ResizeOverlayPosition.Center,
        "top-left" => ResizeOverlayPosition.TopLeft,
        "top-center" => ResizeOverlayPosition.TopCenter,
        "top-right" => ResizeOverlayPosition.TopRight,
        "bottom-left" => ResizeOverlayPosition.BottomLeft,
        "bottom-center" => ResizeOverlayPosition.BottomCenter,
        "bottom-right" => ResizeOverlayPosition.BottomRight,
        _ => ResizeOverlayPosition.Center,
    };
}
