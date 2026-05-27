namespace Ghostty.Core.Hosting;

internal enum DimensionKind
{
    Percentage = 1,
    Pixels = 2,
}

/// <summary>
/// One axis of a quick-terminal size specification. Either a
/// percentage of the relevant monitor dimension (0-100) or an
/// absolute pixel count.
/// </summary>
internal readonly record struct Dimension(DimensionKind Kind, double Value)
{
    public static Dimension Percentage(double v) => new(DimensionKind.Percentage, v);
    public static Dimension Pixels(uint v) => new(DimensionKind.Pixels, v);

    /// <summary>
    /// Resolve to an absolute pixel count against the parent
    /// dimension (the relevant monitor work-area span).
    /// Percentage values are clamped non-negative.
    /// </summary>
    public int ToPixels(int parentDimension) => Kind switch
    {
        DimensionKind.Percentage =>
            (int)System.Math.Max(0, System.Math.Round(parentDimension * (Value / 100.0))),
        DimensionKind.Pixels =>
            (int)System.Math.Max(0, System.Math.Round(Value)),
        _ => 0,
    };
}

/// <summary>
/// Two-axis quick-terminal size. Either axis can be null which
/// the resolver fills with a sensible default (50% on the primary
/// axis, 100% on the secondary). Mirrors the upstream
/// `quick-terminal-size` config key.
/// </summary>
internal readonly record struct QuickTerminalSize(
    Dimension? Primary,
    Dimension? Secondary);
