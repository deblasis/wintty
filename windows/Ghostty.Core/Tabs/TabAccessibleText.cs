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
    internal static string Status(TabModel tab) => Status(tab.BellRinging);

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
    /// </summary>
    internal static string Status(bool bellRinging)
        => bellRinging ? "Bell" : string.Empty;

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
}
