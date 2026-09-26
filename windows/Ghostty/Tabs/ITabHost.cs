using System.Threading.Tasks;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;

namespace Ghostty.Tabs;

/// <summary>
/// Common surface that <see cref="MainWindow"/> programs against
/// regardless of which tab layout is in use. Both
/// <see cref="TabHost"/> (horizontal TabView) and
/// <c>VerticalTabHost</c> (vertical sidebar) implement this.
///
/// The interface deliberately exposes the host as a
/// <see cref="FrameworkElement"/> so MainWindow can install
/// KeyboardAccelerators, subscribe to KeyUp, and set ScopeOwner
/// through the inherited UIElement surface — no parallel API.
/// </summary>
internal interface ITabHost
{
    /// <summary>
    /// The visual element to add to the window's content. Also
    /// receives keyboard accelerators and KeyUp subscriptions
    /// (UIElement members) from MainWindow.
    /// </summary>
    FrameworkElement HostElement { get; }

    /// <summary>The app-icon badge this host shows in its strip corner.</summary>
    FrameworkElement IconBadge { get; }

    /// <summary>
    /// The drag-region element for extended title bar mode. In
    /// horizontal layout this is the TabView's TabStripFooter; in
    /// vertical layout it's a small grab handle at the top of the
    /// window reserved for window-drag. MainWindow passes this to
    /// <c>Window.SetTitleBar</c> so clicks on the empty strip area
    /// (or grab handle) drag the window.
    /// </summary>
    UIElement DragRegion { get; }

    /// <summary>
    /// The element rendering <paramref name="tab"/> in this host, or null
    /// if the host has no row for it yet. LayoutCoordinator measures this
    /// on both hosts to morph the active tab across a layout switch.
    /// </summary>
    FrameworkElement? TabElement(TabModel tab);

    /// <summary>
    /// Single entry point for closing a tab. Shows the multi-pane
    /// confirmation dialog if needed and only then closes.
    /// </summary>
    Task RequestCloseTabAsync(TabModel tab);

    /// <summary>
    /// What this host is RENDERING right now, row by row, measured against
    /// <paramref name="root"/>. The layout-switch filmstrip's oracle.
    ///
    /// Named TestSeam* for the same reason MainWindow's accessors are: the
    /// seam's footprint stays greppable as one shape. It is a read: the
    /// pass allocates a list and measures, and changes nothing.
    /// </summary>
    System.Collections.Generic.IReadOnlyList<Testing.TestSeamStripRow>
        TestSeamRows(FrameworkElement root);

    /// <summary>
    /// The selected row's stroke, as this host is holding it right now.
    /// The active tab's whole separation from the strip rests on this
    /// 1-DIP line, and a 1-DIP line cannot be sampled reliably from a
    /// rect guessed off UIA, so the seam reports the brush it pushed and
    /// the element the stroke rides and lets a capture harness find the
    /// band in a screenshot (#931). <see cref="TestSeamRows"/>'s rule
    /// applies: a read, and honest about having nothing to report --
    /// Shown false covers "no selection", "selection parked on a chip"
    /// and "the stroke is not drawn" without the harness having to
    /// guess which.
    /// </summary>
    TabSelectionStroke TestSeamSelectionStroke();
}

/// <summary>
/// One strip's answer for its selection stroke: the brush colour actually
/// pushed, the four thicknesses actually set, and the element whose bounds
/// the stroke hugs. Argb is 0xAARRGGBB. Element is null whenever Shown is
/// false; the thicknesses are the DIP values the strip set, which the
/// harness scales by the window's rasterization scale the same way it
/// scales the rect.
/// </summary>
internal readonly record struct TabSelectionStroke(
    bool Shown,
    uint Argb,
    double Left,
    double Top,
    double Right,
    double Bottom,
    FrameworkElement? Element);
