namespace Ghostty.Core.Tabs;

/// <summary>
/// The set of Snap Layouts zones Ghostty offers. This is the union of
/// the zones shown for standard-landscape, ultra-wide, and portrait
/// monitors; SnapZoneCatalog picks the subset actually rendered by
/// the picker based on the current monitor's aspect ratio.
///
/// Values intentionally mirror the Windows 11 shell's maximize-button
/// flyout layout. There is no public API to query the shell's own
/// preset list, so this enum is the source of truth.
/// </summary>
internal enum SnapZone
{
    Maximize,

    // Halves (landscape and portrait both use these).
    LeftHalf,
    RightHalf,
    TopHalf,
    BottomHalf,

    // Quarters (standard landscape + ultra-wide).
    TopLeftQuarter,
    TopRightQuarter,
    BottomLeftQuarter,
    BottomRightQuarter,

    // Ultra-wide vertical thirds.
    LeftThird,
    MiddleThird,
    RightThird,
    LeftTwoThirds,
    RightTwoThirds,

    // Portrait horizontal thirds. Named with the "Horizontal" suffix
    // so the middle row does not collide with the ultra-wide
    // MiddleThird (vertical column).
    TopThird,
    MiddleThirdHorizontal,
    BottomThird,
}
