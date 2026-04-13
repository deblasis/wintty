namespace Ghostty.Core.Tabs;

/// <summary>
/// Monitor-shape bucket used by SnapZoneCatalog to choose which
/// zone set to offer. Mirrors the Windows 11 shell's own heuristic:
/// the shell swaps to three-column layouts around the 21:9 aspect
/// ratio boundary, and to vertical thirds on portrait monitors.
/// </summary>
internal enum SnapMonitorShape
{
    StandardLandscape,
    UltraWideLandscape,
    Portrait,
}

/// <summary>
/// Pure-logic zone catalog. Given the physical width and height of a
/// monitor work area, returns the zone set that matches what the
/// Windows 11 shell would show for that monitor's maximize-button
/// flyout. SnapZoneCatalog has no WinUI dependency so it is directly
/// unit-testable from Ghostty.Tests.
/// </summary>
internal static partial class SnapZoneCatalog
{
    // The Windows 11 shell flips to three-column ultra-wide layouts
    // around 21:9 (2.333). 16:9 (1.78) and 16:10 (1.6) stay in the
    // standard bucket. 2.1 is the conventional cut-off.
    public const double UltraWideAspect = 2.1;

    public static SnapMonitorShape Classify(int width, int height)
    {
        if (width < height) return SnapMonitorShape.Portrait;
        double ratio = width / (double)height;
        if (ratio >= UltraWideAspect) return SnapMonitorShape.UltraWideLandscape;
        return SnapMonitorShape.StandardLandscape;
    }

    // Cached arrays returned via IReadOnlyList so no allocation on
    // every call (the picker opens on every right-click).
    private static readonly SnapZone[] StandardLandscapeZones =
    {
        SnapZone.LeftHalf, SnapZone.RightHalf,
        SnapZone.TopHalf, SnapZone.BottomHalf,
        SnapZone.TopLeftQuarter, SnapZone.TopRightQuarter,
        SnapZone.BottomLeftQuarter, SnapZone.BottomRightQuarter,
        SnapZone.Maximize,
    };

    private static readonly SnapZone[] UltraWideZones =
    {
        SnapZone.LeftHalf, SnapZone.RightHalf,
        SnapZone.LeftThird, SnapZone.MiddleThird, SnapZone.RightThird,
        SnapZone.LeftTwoThirds, SnapZone.RightTwoThirds,
        SnapZone.TopLeftQuarter, SnapZone.TopRightQuarter,
        SnapZone.BottomLeftQuarter, SnapZone.BottomRightQuarter,
        SnapZone.Maximize,
    };

    private static readonly SnapZone[] PortraitZones =
    {
        SnapZone.TopHalf, SnapZone.BottomHalf,
        SnapZone.TopThird, SnapZone.MiddleThirdHorizontal, SnapZone.BottomThird,
        SnapZone.Maximize,
    };

    public static System.Collections.Generic.IReadOnlyList<SnapZone> ZonesFor(int width, int height)
    {
        return Classify(width, height) switch
        {
            SnapMonitorShape.UltraWideLandscape => UltraWideZones,
            SnapMonitorShape.Portrait => PortraitZones,
            _ => StandardLandscapeZones,
        };
    }
}
