namespace Ghostty.Core.Input;

/// <summary>
/// Decides the next background-opacity value when toggling between
/// fully-opaque and the configured (transparent) baseline. On Windows
/// there is no transient runtime opacity channel, so the toggle is
/// realised by writing the config value and reloading; this helper keeps
/// the decision pure and testable.
/// </summary>
public static class BackgroundOpacityToggle
{
    /// <param name="current">The current effective background opacity (0..1).</param>
    /// <param name="baseline">The remembered transparent value to restore,
    /// or null if we are not currently in the "forced opaque" state.</param>
    public static Result Next(double current, double? baseline)
    {
        // Currently transparent -> force opaque, remember where we came from.
        if (current < 1.0) return new Result(1.0, current);

        // Currently opaque with a transparent baseline -> restore it.
        if (baseline is { } b && b < 1.0) return new Result(b, null);

        // Started opaque (or baseline is itself opaque): nothing to reveal.
        return new Result(null, baseline);
    }

    /// <param name="OpacityToWrite">Value to persist + reload, or null for no-op.</param>
    /// <param name="NewBaseline">Updated remembered baseline.</param>
    public readonly record struct Result(double? OpacityToWrite, double? NewBaseline);
}
