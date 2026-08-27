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

    /// <summary>
    /// The colour opaque chrome takes when the terminal palette is not
    /// driving it. Dark keeps the value the window has always used; light
    /// exists because a light desktop with a solid backdrop used to come up
    /// near-black around a near-white terminal.
    /// </summary>
    public static uint OpaqueChromeArgb(bool isDesktopDark) =>
        isDesktopDark ? 0xFF0C0C0Cu : 0xFFF3F3F3u;

    /// <param name="backdropStyle">Current SystemBackdrop style (see <see cref="BackdropStyles"/>).</param>
    /// <param name="shellThemeEnabled">True when window-theme=wintty and chrome is driven by the terminal palette.</param>
    /// <param name="shellThemeBgArgb">ARGB to use for the shell-theme-enabled case (typically the title bar background).</param>
    /// <param name="isDesktopDark">
    /// The desktop's light/dark setting. Only consulted when nothing else
    /// supplies a colour, but it has to be passed in every case: reading the
    /// OS from here would make the resolver answer differently for two
    /// callers on the same frame.
    /// </param>
    public static uint Resolve(
        string backdropStyle,
        bool shellThemeEnabled,
        uint shellThemeBgArgb,
        bool isDesktopDark)
    {
        if (backdropStyle is BackdropStyles.Frosted or BackdropStyles.Crystal)
            return TransparentArgb;
        return shellThemeEnabled ? shellThemeBgArgb : OpaqueChromeArgb(isDesktopDark);
    }
}
