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
    /// Single entry point for closing a tab. Shows the multi-pane
    /// confirmation dialog if needed and only then closes.
    /// </summary>
    Task RequestCloseTabAsync(TabModel tab);
}
