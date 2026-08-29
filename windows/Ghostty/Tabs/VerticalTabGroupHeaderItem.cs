using Ghostty.Accessibility;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Tabs;

/// <summary>
/// The NavigationViewItem a group header renders as, and the only seam a
/// custom automation peer can be attached through: a plain
/// NavigationViewItem lets a screen reader read the collapse state off
/// ItemStatus but offers no pattern to change it. The item carries the
/// toggle EVENT rather than toggling itself, so a pattern invoke lands on
/// the same command path the keyboard gesture uses -- through the strip
/// to the router, which announces. The pointer chevron keeps its direct,
/// silent toggle.
/// </summary>
internal sealed partial class VerticalTabGroupHeaderItem : NavigationViewItem
{
    public VerticalTabGroupHeaderItem()
    {
    }

    /// <summary>Raised by the UIA ExpandCollapse pattern only, never by pointer.</summary>
    internal event EventHandler<(TabGroup Group, bool Collapsed)>? GroupToggleRequested;

    protected override AutomationPeer OnCreateAutomationPeer()
        => new VerticalTabGroupHeaderItemAutomationPeer(this);

    internal void RaiseGroupToggleFromPattern(bool collapsed)
    {
        if (Tag is TabGroup group)
            GroupToggleRequested?.Invoke(this, (group, collapsed));
    }
}
