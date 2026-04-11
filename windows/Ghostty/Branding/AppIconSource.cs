namespace Ghostty.Branding;

/// <summary>
/// Single source of truth for the Ghostty app icon URI. Anything that
/// renders the icon inside the WinUI 3 shell binds to this. When we
/// later add a vector source, a user-selectable icon set, or runtime
/// channel detection, this is the one place that changes.
/// </summary>
internal static class AppIconSource
{
    public static Uri Current { get; } =
        new Uri("ms-appx:///Assets/AppIcon.png");
}
