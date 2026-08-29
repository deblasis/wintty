using Ghostty.Core.Tabs;
using Ghostty.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Ghostty.Accessibility;

/// <summary>
/// UIA peer for a group header row: ListItem like the rows around it,
/// plus the ExpandCollapse pattern -- the only way a keyboard-only or
/// screen-reader client can fold and unfold a group. The header never
/// selects, the chevron is a pointer target, and ItemStatus reports the
/// state without offering a way to change it. Expand and collapse mirror
/// the keyboard gesture's polarity and land on the same event, so the
/// command announces and a collapse re-homes focus exactly like pressing
/// Enter on the header.
/// </summary>
internal sealed partial class VerticalTabGroupHeaderItemAutomationPeer
    : FrameworkElementAutomationPeer, IExpandCollapseProvider
{
    public VerticalTabGroupHeaderItemAutomationPeer(VerticalTabGroupHeaderItem owner)
        : base(owner) { }

    private VerticalTabGroupHeaderItem OwnerItem => (VerticalTabGroupHeaderItem)Owner;

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.ListItem;

    protected override object? GetPatternCore(PatternInterface patternInterface)
        => patternInterface == PatternInterface.ExpandCollapse
            ? this
            : base.GetPatternCore(patternInterface);

    public ExpandCollapseState ExpandCollapseState
        => OwnerItem.Tag is TabGroup { IsCollapsed: true }
            ? ExpandCollapseState.Collapsed
            : ExpandCollapseState.Expanded;

    public void Expand() => OwnerItem.RaiseGroupToggleFromPattern(collapsed: false);

    public void Collapse() => OwnerItem.RaiseGroupToggleFromPattern(collapsed: true);
}
