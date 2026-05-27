namespace Ghostty.Core.Search;

/// <summary>
/// Implemented by the per-pane host that owns a libghostty surface.
/// The search bar control drives the host through this interface; the
/// host translates each call into a libghostty binding action against
/// the surface. Kept in Ghostty.Core so SearchBarControl can depend on
/// the contract without taking a dependency on the WinUI assembly.
/// </summary>
public interface ISearchHost
{
    /// <summary>
    /// Start or update the active search with <paramref name="needle"/>.
    /// An empty needle cancels the search without closing the UI.
    /// </summary>
    void StartSearch(string needle);

    /// <summary>Step to the next match.</summary>
    void NavigateNext();

    /// <summary>Step to the previous match.</summary>
    void NavigatePrevious();

    /// <summary>End the search and tear down any UI.</summary>
    void EndSearch();
}
