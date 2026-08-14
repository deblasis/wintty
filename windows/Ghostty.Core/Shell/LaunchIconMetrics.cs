namespace Ghostty.Core.Shell;

/// <summary>
/// Picks the on-screen size of the launch icon for a given window size.
/// Pure so the sizing curve is testable without a window.
///
/// The icon is a fraction of the window's smaller edge rather than a
/// fixed size: a splash icon tuned for a full-screen terminal swamps a
/// small one, and one tuned for a small window looks lost in a large
/// one. The clamps stop it going comically large on an ultrawide or
/// shrinking to an unreadable speck on a tiny quake window.
/// </summary>
public static class LaunchIconMetrics
{
    /// <summary>Upper clamp, in DIPs. Also the size used when the window size is unknown.</summary>
    public const int MaxSizeDips = 160;

    /// <summary>Lower clamp, in DIPs.</summary>
    public const int MinSizeDips = 48;

    /// <summary>Fraction of the window's smaller edge the icon aims for.</summary>
    public const double WindowFraction = 0.25;

    /// <param name="windowWidth">Window width in DIPs.</param>
    /// <param name="windowHeight">Window height in DIPs.</param>
    public static int Resolve(double windowWidth, double windowHeight)
    {
        // Math.Min rather than a ternary: every comparison against NaN is
        // false, so `w < h ? w : h` would quietly return the other edge and
        // skip the guard below. Math.Min propagates NaN.
        var smallerEdge = System.Math.Min(windowWidth, windowHeight);

        // No usable size yet (window not laid out, or a garbage value from
        // a stale state file). Full size is the safer guess: too big reads
        // as deliberate, too small reads as broken.
        if (double.IsNaN(smallerEdge) || smallerEdge <= 0) return MaxSizeDips;

        var target = smallerEdge * WindowFraction;
        if (target >= MaxSizeDips) return MaxSizeDips;
        if (target <= MinSizeDips) return MinSizeDips;
        return (int)System.Math.Round(target);
    }
}
