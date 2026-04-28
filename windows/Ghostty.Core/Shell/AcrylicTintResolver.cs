namespace Ghostty.Core.Shell;

/// <summary>
/// Pure resolver for acrylic tint + opacity tuning. When the tint
/// color override is unset the resolver falls back to the terminal
/// theme background so the window inherits the active theme look
/// instead of washing out to transparent black.
///
/// Opacity values track <c>background-blur-follows-opacity</c>: when
/// enabled both tint and luminosity opacity are slaved to the window
/// opacity; otherwise user overrides apply with a shared default.
/// </summary>
public static class AcrylicTintResolver
{
    /// <summary>Default tint opacity when the user hasn't set one.</summary>
    public const float DefaultTintOpacity = 0.3f;

    /// <summary>Default luminosity opacity when the user hasn't set one.</summary>
    public const float DefaultLuminosityOpacity = 0.3f;

    /// <summary>Alpha bits OR'd into the theme background to produce an opaque tint.</summary>
    public const uint OpaqueAlphaMask = 0xFF000000u;

    /// <summary>Resolved tuning the caller hands to the acrylic controller.</summary>
    /// <param name="TintArgb">Tint color, packed as 0xAARRGGBB.</param>
    /// <param name="TintOpacity">Tint opacity in [0.0, 1.0].</param>
    /// <param name="LuminosityOpacity">Luminosity opacity in [0.0, 1.0].</param>
    public readonly record struct Tuning(
        uint TintArgb,
        float TintOpacity,
        float LuminosityOpacity);

    /// <param name="tintOverrideArgb">
    /// Packed ARGB for <c>background-tint-color</c>, or null if the key
    /// is not set in the config file.
    /// </param>
    /// <param name="themeBackgroundRgb">
    /// Theme background color; only the low 24 bits (RGB) are used, so
    /// either a packed ARGB or a 0x00RRGGBB value is accepted. The
    /// resolver applies an opaque alpha before handing it to the
    /// compositor.
    /// </param>
    /// <param name="tintOpacityOverride">User <c>background-tint-opacity</c>, or null.</param>
    /// <param name="luminosityOpacityOverride">User <c>background-luminosity-opacity</c>, or null.</param>
    /// <param name="blurFollowsOpacity"><c>background-blur-follows-opacity</c> flag.</param>
    /// <param name="backgroundOpacity">
    /// <c>background-opacity</c>; the caller is expected to clamp to [0.0, 1.0]
    /// (ConfigService does this upstream). Out-of-range or NaN values are
    /// passed through unchanged so an upstream regression surfaces in
    /// tests rather than being silently masked here.
    /// </param>
    public static Tuning Resolve(
        uint? tintOverrideArgb,
        uint themeBackgroundRgb,
        float? tintOpacityOverride,
        float? luminosityOpacityOverride,
        bool blurFollowsOpacity,
        double backgroundOpacity)
    {
        // Tint color: user override wins; otherwise the theme background
        // rendered opaque so the acrylic layer picks up its RGB.
        var tintArgb = tintOverrideArgb ?? (OpaqueAlphaMask | (themeBackgroundRgb & 0x00FFFFFFu));

        float tintOpacity;
        float luminosityOpacity;
        if (blurFollowsOpacity)
        {
            // "More transparent window" = "less tint and luminosity".
            var opacity = (float)backgroundOpacity;
            tintOpacity = opacity;
            luminosityOpacity = opacity;
        }
        else
        {
            tintOpacity = tintOpacityOverride ?? DefaultTintOpacity;
            luminosityOpacity = luminosityOpacityOverride ?? DefaultLuminosityOpacity;
        }

        return new Tuning(tintArgb, tintOpacity, luminosityOpacity);
    }
}
