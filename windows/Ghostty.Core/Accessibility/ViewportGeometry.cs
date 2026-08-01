namespace Ghostty.Core.Accessibility;

/// <summary>
/// Where the terminal grid sits on screen, in PHYSICAL pixels, which is the
/// unit UIA reports rectangles and receives hit-test points in.
/// </summary>
/// <param name="OriginX">Screen x of the grid's top-left corner.</param>
/// <param name="OriginY">Screen y of the grid's top-left corner.</param>
/// <param name="Width">Grid width on screen.</param>
/// <param name="Height">Grid height on screen.</param>
public readonly record struct ViewportGeometry(
    double OriginX,
    double OriginY,
    double Width,
    double Height)
{
    /// <summary>
    /// A grid too small to be worth reporting. A control that has never been
    /// laid out measures 0x0, and a split mid-layout can report a couple of
    /// pixels for one pass before it settles.
    /// </summary>
    public bool IsUsable => Width >= 32 && Height >= 32;
}
