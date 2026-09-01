using System;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Ghostty.Testing;

/// <summary>
/// One row a strip is rendering, as a filmstrip frame records it.
///
/// The layout switch's defects do not live in the manager. Collapse bits,
/// pin flags and the active index are identical either side of a switch
/// that flashes and one that does not -- the difference exists only in
/// what the two strips were HOLDING while the cross-fade ran, and for how
/// long. So the frame's oracle is the rendered inventory, not the model:
/// which rows each host had on screen, where, and how visible.
/// </summary>
/// <param name="Kind">
/// What the row stands for: <c>tab</c>, <c>pinned</c>, <c>chip</c> (the
/// horizontal strip's rendering of a collapsed run) or <c>header</c> (the
/// vertical strip's).
/// </param>
/// <param name="Label">The tab's effective title, or the group's.</param>
/// <param name="Group">The owning group's title, when the row has one.</param>
/// <param name="Active">The row stands for the manager's active tab.</param>
/// <param name="Shown">
/// Arranged and not collapsed: the row occupies space. Says nothing about
/// whether the eye can see it -- <see cref="Alpha"/> owns that.
/// </param>
/// <param name="Alpha">
/// Opacity multiplied along the chain from the row to the window root, so
/// a row inside a host that is cross-fading out reports what the eye
/// actually gets rather than the 1.0 it is set to locally.
/// </param>
/// <param name="Bounds">The row's rect in the window root's coordinates.</param>
internal readonly record struct TestSeamStripRow(
    string Kind,
    string Label,
    string? Group,
    bool Active,
    bool Shown,
    double Alpha,
    Rect Bounds);

/// <summary>
/// The measuring half of <see cref="TestSeamStripRow"/>: the two hosts
/// build their inventories with these so a horizontal row and a vertical
/// row are measured the same way and a filmstrip can compare them.
/// </summary>
internal static class TestSeamStripRowMeasure
{
    /// <summary>
    /// Describe one row. A row that cannot be measured still reports --
    /// as unshown, with a zero rect -- because a row missing from the
    /// inventory and a row present but invisible are different findings.
    /// </summary>
    /// <remarks>
    /// Also used for the host elements themselves, with kind <c>host</c>,
    /// so a lane and the rows inside it are measured by one function and
    /// an "is this row inside its strip" question cannot be answered
    /// against a differently-derived rect.
    /// </remarks>
    internal static TestSeamStripRow Row(
        FrameworkElement root,
        FrameworkElement element,
        string kind,
        string label,
        string? group,
        bool active)
    {
        var shown = element.Visibility == Visibility.Visible
            && element.ActualWidth > 0
            && element.ActualHeight > 0;
        return new TestSeamStripRow(
            kind, label, group, active,
            shown,
            shown ? AlphaTo(root, element) : 0,
            shown ? BoundsIn(root, element) : default);
    }

    /// <summary>
    /// Effective opacity: the product of every Opacity between the row and
    /// the root, zeroed by the first collapsed ancestor. The cross-fade
    /// sets opacity on the HOSTS, not on their rows, so a row's own
    /// Opacity of 1 says nothing about what is on screen mid-switch.
    /// </summary>
    private static double AlphaTo(FrameworkElement root, FrameworkElement element)
    {
        var alpha = 1.0;
        DependencyObject? node = element;
        while (node is not null && !ReferenceEquals(node, root))
        {
            if (node is UIElement ui)
            {
                if (ui.Visibility != Visibility.Visible) return 0;
                alpha *= ui.Opacity;
            }
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        // A row whose walk never reached the root is not in the window's
        // tree: unreachable is not visible.
        return node is null ? 0 : alpha;
    }

    private static Rect BoundsIn(FrameworkElement root, FrameworkElement element)
    {
        try
        {
            var origin = element.TransformToVisual(root)
                .TransformPoint(new Point(0, 0));
            return new Rect(
                origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            // Not in the same tree as root (a collapsed ancestor, or a row
            // leaving). No rect is the honest answer; the caller already
            // recorded that the row exists.
            return default;
        }
    }
}
