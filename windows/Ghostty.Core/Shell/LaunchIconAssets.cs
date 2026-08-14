namespace Ghostty.Core.Shell;

/// <summary>
/// One rung of the launch-icon ladder: a WinUI asset scale and the pixel
/// size the PNG for it is generated at.
/// </summary>
/// <param name="Scale">The number in the <c>.scale-xxx</c> file suffix.</param>
/// <param name="Pixels">Edge length of the generated PNG, in pixels.</param>
public readonly record struct LaunchIconRung(int Scale, int Pixels)
{
    /// <summary>Name this rung ships under in the app's Assets folder.</summary>
    public string FileName => $"SplashIcon.scale-{Scale}.png";
}

/// <summary>
/// The launch-icon ladder the cold-start splash draws from.
///
/// <para>Two sides have to agree on it: the icon generator that writes
/// the PNGs (<c>dist/windows/IconGen</c>, which links this file rather
/// than referencing this assembly) and the splash window that picks one
/// to draw. They used to hold a copy each, kept in step by a comment.
/// Renaming or renumbering a rung on one side left the other asking for
/// files that no longer existed, and the splash's answer to a missing
/// file is to paint a bare coloured rectangle -- a silent failure with
/// no build error in front of it.</para>
/// </summary>
public static class LaunchIconAssets
{
    // Sized off the largest the icon ever draws at
    // (LaunchIconMetrics.MaxSizeDips) so every on-screen size is a
    // downsample, which stays sharp; the 40 DIP AppIcon ladder would
    // have to upscale even its largest rung. Derived rather than
    // listed so the ladder follows if that clamp ever moves -- pick a
    // clamp every scale divides cleanly, since the truncation here is
    // silent and only the tests' expected pixel sizes would notice.
    private static readonly int[] ScalePercents = [100, 150, 200, 400];

    /// <summary>
    /// The rungs, ascending by pixel size. Both consumers rely on that
    /// order: the splash takes the first rung at least as large as the
    /// size it is drawing, and falls back to the last as the largest
    /// shipped.
    /// </summary>
    public static IReadOnlyList<LaunchIconRung> Rungs { get; } =
    [
        .. ScalePercents.Select(scale =>
            new LaunchIconRung(scale, LaunchIconMetrics.MaxSizeDips * scale / 100)),
    ];
}
