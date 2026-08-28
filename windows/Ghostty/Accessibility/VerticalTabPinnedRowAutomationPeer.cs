using Ghostty.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;

namespace Ghostty.Accessibility;

/// <summary>
/// UIA peer for an icon-only pinned tab row. Reports ListItem, matching
/// the body rows MUXC types for itself, and claims keyboard focusability
/// explicitly: the row is a tab stop the strip's key handler moves focus
/// through, and a peer that does not say so leaves the whole shelf
/// invisible to a keyboard-only client's focus traversal.
///
/// The framework raises the focus-changed event through whatever peer the
/// focused element reports, so this peer existing is what makes a screen
/// reader track shelf focus at all.
/// </summary>
internal sealed partial class VerticalTabPinnedRowAutomationPeer
    : FrameworkElementAutomationPeer
{
    public VerticalTabPinnedRowAutomationPeer(VerticalTabPinnedRow owner)
        : base(owner) { }

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.ListItem;

    protected override bool IsKeyboardFocusableCore()
        => Owner is VerticalTabPinnedRow
           {
               IsTabStop: true,
               Visibility: Visibility.Visible,
           };
}
