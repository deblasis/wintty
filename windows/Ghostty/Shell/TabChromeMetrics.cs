using Microsoft.UI.Xaml;

namespace Ghostty.Shell;

/// <summary>
/// Shared tab-strip / title-row geometry so the app icon lands on the
/// same pixel in horizontal and vertical layouts.
/// </summary>
internal static class TabChromeMetrics
{
    /// <summary>Title row height -- horizontal TabView strip and vertical title bar.</summary>
    public const double TitleRowHeight = 34;

    /// <summary>
    /// App icon inset from the window's top-left chrome origin. TabView
    /// hosts the badge in LeftContentPresenter (no TabView.Padding there),
    /// so both orientations must apply this explicitly.
    /// </summary>
    public static Thickness AppIconMargin { get; } = new(2, 2, 0, 0);
}
