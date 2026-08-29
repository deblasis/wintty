namespace Ghostty.Core.Tabs;

/// <summary>
/// What an assistive client reads for one tab in a strip.
///
/// Both strips build a row out of a panel and hand it to the item, and
/// neither item gets a usable name out of a panel on its own. An unnamed
/// NavigationViewItem falls back to its content's ToString, so every row
/// in the vertical strip read as "Ghostty.Tabs.VerticalTabNavRow"; an
/// unnamed TabViewItem gets nothing at all out of a panel header, so
/// every tab in the horizontal strip read as blank. Either way tabs with
/// different titles read identically.
///
/// Naming them from the title makes tabs with distinct titles distinct
/// to a listener. Tabs that are all untitled still collapse onto the same
/// fallback name, exactly as their visible labels collapse onto the same
/// text; position in the strip is what separates those, and the strips
/// leave PositionInSet and SizeOfSet to the framework.
///
/// The name has to be non-empty: empty is exactly the state that sent the
/// vertical strip down the ToString path in the first place.
/// </summary>
internal static class TabAccessibleText
{
    /// <summary>Accessible name for <paramref name="tab"/>.</summary>
    internal static string Name(TabModel tab) => Name(tab.EffectiveTitle);

    /// <summary>
    /// Accessible name for the tab whose title is
    /// <paramref name="effectiveTitle"/>.
    /// </summary>
    internal static string Name(string? effectiveTitle)
        => string.IsNullOrWhiteSpace(effectiveTitle)
            ? AppIdentity.ProductName
            : effectiveTitle;

    /// <summary>Transient state for <paramref name="tab"/>.</summary>
    internal static string Status(TabModel tab)
        => Status(tab.IsPinned, tab.BellRinging);

    /// <summary>
    /// Transient state for the tab, for AutomationProperties.ItemStatus.
    /// Empty when there is nothing to report.
    ///
    /// The bell is not folded into the name. A name is an identity: it is
    /// what a client matches on to find a tab, what focus announces, and
    /// what the user is told the tab is called. Appending "bell" to it
    /// renames the tab twice per bell, so a client that found it a moment
    /// ago can no longer find it, and every unrelated focus move re-reads
    /// the state. ItemStatus is the property UIA defines for exactly this
    /// -- state that rides along with an item and changes under it -- and
    /// it leaves the name alone.
    ///
    /// "Pinned" is the same kind of state: the row still has its title,
    /// and what changed is where the strip keeps it. A pin that renamed
    /// the tab would hide the one string every other surface (overview,
    /// switcher, pane title) still agrees on.
    /// </summary>
    internal static string Status(bool isPinned, bool bellRinging)
    {
        if (!isPinned) return bellRinging ? "Bell" : string.Empty;
        return bellRinging ? "Pinned, Bell" : "Pinned";
    }

    /// <summary>
    /// Spoken when a keyboard or menu pin/unpin lands. The reorder a pin
    /// performs is visible, but the zone change behind it is not: the row
    /// keeps its title, and without an announcement the listener hears an
    /// unexplained jump. Pointer drags announce nothing -- the user is
    /// watching it happen (5.6).
    /// </summary>
    internal static string PinAnnouncement(TabModel tab, bool pinned)
        => pinned ? $"Tab pinned, {Name(tab)}" : $"Tab unpinned, {Name(tab)}";

    /// <summary>
    /// Spoken when a tab starts ringing. ItemStatus is the correct
    /// property for the state but nothing reads it: NVDA does not surface
    /// ItemStatus on a tab or a list item, on focus or on change
    /// (measured). A bell nobody hears until they focus the tab that rang
    /// is a bell that told them nothing, so the same state also goes out
    /// as an announcement, which is read wherever focus happens to be.
    ///
    /// Carries the title because the point of the announcement is which
    /// tab wants attention, and the user is not looking at the strip.
    /// </summary>
    internal static string BellAnnouncement(TabModel tab)
        => $"Bell in {Name(tab)}";

    // --- Group commands (5b-2a) ---
    //
    // Group ops announce from the dispatch path, exactly like pins: the
    // router raises only for commands, so the same manager op stays silent
    // when a drag or a session restore performs it. Every text carries the
    // group title -- the title is the only handle a listener has on a
    // group, which owns no focus and no row of its own to read.

    /// <summary>Spoken when a command turns one tab into a new group.</summary>
    internal static string GroupCreatedAnnouncement(TabModel tab, TabGroup group)
        => $"New group {group.Title}, with {Name(tab)}";

    /// <summary>Spoken when a command joins one tab to an existing group.</summary>
    internal static string TabJoinedGroupAnnouncement(TabModel tab, TabGroup group)
        => $"{Name(tab)} joined group {group.Title}";

    /// <summary>
    /// Spoken when a command removes one tab from its group. The group is
    /// the pre-op membership: once the command lands, the tab answers
    /// "no group", which would make the announcement name nothing.
    /// </summary>
    internal static string TabRemovedFromGroupAnnouncement(TabModel tab, TabGroup group)
        => $"{Name(tab)} removed from group {group.Title}";

    /// <summary>
    /// Spoken when a command dissolves a group. The count is pre-op:
    /// after the dissolve the group owns no members to count, and "how
    /// many tabs just ungrouped" is the fact a listener cannot see.
    /// </summary>
    internal static string GroupDissolvedAnnouncement(TabGroup group, int memberCount)
        => $"Group {group.Title} dissolved, {memberCount} {Tabs(memberCount)} ungrouped";

    /// <summary>
    /// Spoken when a command collapses or expands a group. The bit is read
    /// live: the command has landed by the time anything announces, and a
    /// pre-op snapshot would narrate the opposite of what happened.
    /// </summary>
    internal static string GroupCollapseAnnouncement(TabGroup group)
        => group.IsCollapsed ? $"Group {group.Title} collapsed" : $"Group {group.Title} expanded";

    /// <summary>Spoken when a Close Group command starts.</summary>
    internal static string GroupCloseAnnouncement(TabGroup group, int memberCount)
        => $"Closing group {group.Title}, {memberCount} {Tabs(memberCount)}";

    private static string Tabs(int count) => count == 1 ? "tab" : "tabs";
}
