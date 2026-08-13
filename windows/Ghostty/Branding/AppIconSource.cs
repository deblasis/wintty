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

    /// <summary>
    /// Launch icon (96 DIP ladder) faded over a cold-start window.
    /// A separate asset because <see cref="Current"/> tops out at
    /// 160 px and would upscale at this size.
    /// </summary>
    public static Uri Splash { get; } =
        new Uri("ms-appx:///Assets/SplashIcon.png");
}
