namespace Ghostty.Core.Accessibility;

/// <summary>
/// The decision of whether to layer the High Contrast override onto the
/// user's config. Kept as tested Core logic rather than living inline in
/// the WinUI monitor.
/// </summary>
public static class HighContrastState
{
    /// <param name="osHighContrast">True when Windows is in a High Contrast theme.</param>
    /// <param name="userOptOut">
    /// True when the user disabled the auto-override via
    /// <c>windows-high-contrast = false</c>.
    /// </param>
    public static bool ShouldApply(bool osHighContrast, bool userOptOut)
        => osHighContrast && !userOptOut;
}
