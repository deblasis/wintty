using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The keybindings page rebuilt its whole list on every config reload, and
/// rebuilding a WinUI list is <c>Items.Clear()</c> plus an <c>Add</c> per row
/// (WinUiList, because ItemsSource cannot marshal these records). That
/// re-realizes every container, so the viewport goes back to the top and the
/// reader's place in a 900-row list is gone. Reloads are not rare and are not
/// about the keybinds: every settings page routes its writes through the
/// debounced scheduler, so dragging a slider on another page scrolled this list
/// home mid-browse.
/// <para>
/// The filter now compares against the rows on screen and returns early when
/// they are unchanged (<c>KeybindCatalog.RowsMatch</c>, tested there). What is
/// pinned here is that the comparison stands between the filter and the
/// replace -- a skip that happens after the rebuild is the defect with extra
/// steps.
/// </para>
/// </summary>
public sealed class KeybindingsListRefreshWiringTests
{
    private static ShellSource Page() => ShellSource.Load("Settings.Pages.KeybindingsPage.xaml.cs");

    [Fact]
    public void The_replace_is_skipped_when_the_filtered_rows_are_unchanged()
    {
        var applyFilter = Page().Method("ApplyFilter");

        var compare = applyFilter.Call("KeybindCatalog.RowsMatch");
        var replace = applyFilter.Call("WinUiList.ReplaceItems");

        Assert.True(
            compare.SpanStart < replace.SpanStart,
            "the rows are replaced without asking whether they changed, which "
            + "is the scroll reset this guards");

        // The early return the comparison buys: a rebuild that changed nothing
        // has to leave the list alone rather than replace it with an equal one.
        var skip = applyFilter.Body!.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("RowsMatch", System.StringComparison.Ordinal));
        Assert.Contains(
            "return",
            skip.Statement.ToString(),
            System.StringComparison.Ordinal);
        Assert.True(
            skip.SpanStart < replace.SpanStart,
            "the skip sits after the replace, so the list is re-realized either way");
    }

    [Fact]
    public void The_rows_on_screen_are_the_ones_the_comparison_asked_about()
    {
        var applyFilter = Page().Method("ApplyFilter");

        // Compared against what the list is showing, not against a freshly
        // built list: comparing two fresh ones would answer "equal" always.
        Assert.Equal("_shownRows", applyFilter.Call("KeybindCatalog.RowsMatch").Arg(0));
        Assert.Equal("_shownRows = rows", applyFilter.AssignsTo("_shownRows").Single().ToString());
    }

    /// <summary>
    /// Rebuild is the path every config reload takes, so it is the one that
    /// matters: it must reach the filter rather than pushing rows itself.
    /// </summary>
    [Fact]
    public void The_reload_path_goes_through_the_filter()
    {
        var rebuild = Page().Method("Rebuild");

        rebuild.Call("ApplyFilter");
        Assert.Empty(rebuild.Calls("WinUiList.ReplaceItems"));
    }
}
