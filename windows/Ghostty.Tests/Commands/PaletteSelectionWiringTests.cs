using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Commands;

/// <summary>
/// That the view model uses the rules rather than reimplementing them.
///
/// The view model is in the WinUI project, which this assembly cannot
/// reference, so the source is all there is. Parsed rather than searched,
/// for the reason <see cref="ShellSource"/> gives.
/// </summary>
public class PaletteSelectionWiringTests
{
    private const string ViewModel = "Commands.CommandPaletteViewModel.cs";

    /// <summary>
    /// The selection is only ever set from the list as a whole, via SelectTop
    /// or Step. Indexing the list by hand is what needed a surrounding
    /// <c>if (Count &gt; 0)</c>, and that guard is what left a stale command
    /// selected behind a query matching nothing - Enter then ran a command
    /// with nothing on screen to show which.
    /// </summary>
    [Fact]
    public void TheCommandListIsNeverIndexedByHand()
    {
        var indexed = ShellSource.Load(ViewModel).Root
            .DescendantNodes().OfType<ElementAccessExpressionSyntax>()
            .Where(e => e.Expression.ToString() == "FilteredCommands")
            .Select(e => e.ToString())
            .ToList();

        Assert.True(indexed.Count == 0,
            "FilteredCommands is indexed directly, which needs a count guard around it "
            + "and leaves the selection stale when the guard skips the assignment: "
            + string.Join(", ", indexed));
    }

    /// <summary>
    /// Every path that rebuilds or empties the list sets the selection in the
    /// same breath. Asserted as "the two always travel together" rather than
    /// by naming today's paths, because the defect is a path that forgets.
    /// </summary>
    [Fact]
    public void EveryAssignmentToTheListIsMatchedBySelectTop()
    {
        var vm = ShellSource.Load(ViewModel);

        var listWrites = vm.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Count(a => a.Left.ToString() == "FilteredCommands");
        var selectTops = vm.Root.Calls("PaletteSelection.SelectTop").Count;

        Assert.True(listWrites == selectTops,
            $"FilteredCommands is assigned {listWrites} times but SelectTop is called "
            + $"{selectTops} times. A path that rebuilds the list without setting the "
            + "selection into it leaves the previous selection live and runnable.");
    }
}
