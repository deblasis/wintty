using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;

namespace Ghostty.Accessibility;

/// <summary>
/// Raises a UIA notification from an element, for state a screen reader
/// has to hear about even though it is nowhere near the user's focus.
///
/// AutomationProperties carry state correctly but only answer questions;
/// nothing asks about a tab the user is not on. A notification is the one
/// UIA event that is spoken regardless of where focus sits, which is what
/// "something happened over there" needs.
/// </summary>
internal static class UiaAnnouncer
{
    /// <summary>
    /// Speak <paramref name="text"/> from <paramref name="source"/>.
    /// <paramref name="activityId"/> groups related announcements so a
    /// client can coalesce or filter them.
    /// </summary>
    internal static void Announce(FrameworkElement? source, string text, string activityId)
    {
        if (source is null || string.IsNullOrEmpty(text)) return;
        if (!AnyoneListening()) return;

        var peer = FrameworkElementAutomationPeer.FromElement(source)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(source)
            ?? new FrameworkElementAutomationPeer(source);
        peer.RaiseNotificationEvent(
            AutomationNotificationKind.Other,
            // MostRecent, not All: a burst of bells is one situation, and
            // a queue of stale "tab rang" lines read out minutes later is
            // worse than the latest one.
            AutomationNotificationProcessing.MostRecent,
            text,
            activityId);
    }

    /// <summary>
    /// Whether anything is listening. Gated like TerminalAutomationPeer's
    /// raises: crossing the UIA boundary with nobody advised is work for
    /// nothing. There is no AutomationEvents.Notification to ask
    /// ListenerExists about, so the gate is the screen-reader flag plus
    /// LiveRegionChanged, which the automation clients that want
    /// announcements but do not set the flag advise for.
    /// </summary>
    private static bool AnyoneListening() =>
        ScreenReaderDetector.IsRunning()
        || AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged);
}
