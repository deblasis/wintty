namespace Ghostty.Core.Shell;

/// <summary>
/// Pure resolver for the color painted as RootGrid.Background on
/// the main window. Transparent backdrops always stay transparent;
/// otherwise the shell-theme color when enabled, the opaque chrome
/// color when not.
///
/// Callers must pass a lowercased backdrop style (<see cref="BackdropStyles"/>).
/// Anything that doesn't match a transparent style is treated as solid.
/// </summary>
public static class RootBackgroundResolver
{
    /// <summary>ARGB for "fully transparent, let the SystemBackdrop show through".</summary>
    public const uint TransparentArgb = 0x00000000u;

    /// <summary>ARGB for the default opaque chrome color when no shell theme is active.</summary>
    public const uint OpaqueChromeArgb = 0xFF0C0C0Cu;

    /// <param name="backdropStyle">Current SystemBackdrop style (see <see cref="BackdropStyles"/>).</param>
    /// <param name="shellThemeEnabled">True when window-theme=ghostty and chrome is driven by the terminal palette.</param>
    /// <param name="shellThemeBgArgb">ARGB to use for the shell-theme-enabled case (typically the title bar background).</param>
    public static uint Resolve(string backdropStyle, bool shellThemeEnabled, uint shellThemeBgArgb)
    {
        if (backdropStyle is BackdropStyles.Frosted or BackdropStyles.Crystal)
            return TransparentArgb;
        return shellThemeEnabled ? shellThemeBgArgb : OpaqueChromeArgb;
    }
}
