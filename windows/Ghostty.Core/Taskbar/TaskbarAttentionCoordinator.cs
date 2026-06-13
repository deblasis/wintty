using Ghostty.Core.Tabs;

namespace Ghostty.Core.Taskbar;

/// <summary>
/// Drives the Windows taskbar overlay "attention" badge. The badge is
/// the Windows equivalent of Ghostty's <c>bell-features = attention</c>
/// (on by default): request the user's attention when an unfocused
/// window rings the bell, until the window is refocused.
///
/// Pure-logic state machine. <see cref="OnBell"/> is wired to
/// <see cref="TabManager.BellRang"/>; <see cref="SetFocused"/> is driven
/// by the window's activation event. The WinUI-side facade writes to
/// <c>ITaskbarList3::SetOverlayIcon</c>. The <c>_attentionActive</c> guard
/// keeps it to one sink write per attention episode regardless of how many
/// bells fire.
/// </summary>
internal sealed class TaskbarAttentionCoordinator
{
    private readonly ITaskbarOverlaySink _sink;
    private bool _focused;          // false until the first activation
    private bool _attentionActive;

    public TaskbarAttentionCoordinator(TabManager manager, ITaskbarOverlaySink sink)
    {
        _sink = sink;
        // The badge is the Windows `attention` feature; gate on it. A
        // complementary taskbar flash also fires for `attention` from the
        // host (FlashWindowEx) as the "look now" signal.
        manager.BellRang += (_, features) => { if (features.Attention) OnBell(); };
    }

    /// <summary>A bell rang on the active leaf of some owned tab.</summary>
    public void OnBell()
    {
        if (_focused) return;
        if (_attentionActive) return;
        _attentionActive = true;
        _sink.SetAttention(true);
    }

    /// <summary>Window focus changed. Gaining focus clears any pending
    /// attention badge; losing focus only records the state (the badge
    /// appears later, if and when a bell actually rings).</summary>
    public void SetFocused(bool focused)
    {
        _focused = focused;
        if (focused && _attentionActive)
        {
            _attentionActive = false;
            _sink.SetAttention(false);
        }
    }
}
