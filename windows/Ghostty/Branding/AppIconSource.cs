namespace Ghostty.Branding;

/// <summary>
/// Single source of truth for the Ghostty app icon URIs. Anything that
/// renders the icon inside the WinUI 3 shell binds to one of these.
/// When we later add a vector source, a user-selectable icon set, or
/// runtime channel detection, this is the one place that changes.
/// </summary>
internal static class AppIconSource
{
    /// <summary>Chrome-sized icon (40 DIP ladder): title bar badge, menus.</summary>
    public static Uri Current { get; } =
        new Uri("ms-appx:///Assets/AppIcon.png");

    // The launch splash deliberately does NOT resolve its icon through
    // here. It runs before WinUI is initialized, so ms-appx:/// cannot be
    // resolved at all; SplashWindow loads the SplashIcon.scale-*.png
    // assets from disk next to the executable instead.
}
