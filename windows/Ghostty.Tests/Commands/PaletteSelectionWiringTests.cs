using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    /// The bug was not "the assignment is missing", it was "the assignment
    /// is conditional". So the thing to forbid is a condition, not an
    /// absence: any test on the list standing between the palette and its
    /// selection puts back the case where the list empties and the
    /// selection does not.
    ///
    /// `??=` counts as conditional for the same reason - it assigns only
    /// when the previous selection was null, which is precisely the stale
    /// selection this is trying to clear.
    /// </summary>
    [Fact]
    public void TheSelectionIsNeverAssignedConditionallyOnTheList()
    {
        var guarded = ShellSource.Load(ViewModel).Root
            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "SelectedCommand")
            .Where(a => !a.IsKind(SyntaxKind.SimpleAssignmentExpression)
                || a.Ancestors().Any(n =>
                    (n is IfStatementSyntax i
                        && i.Condition.ToString().Contains("FilteredCommands"))
                    || (n is ConditionalExpressionSyntax c
                        && c.Condition.ToString().Contains("FilteredCommands"))))
            .Select(a => a.ToString())
            .ToList();

        Assert.True(guarded.Count == 0,
            "SelectedCommand is assigned under a test on FilteredCommands, or with a "
            + "compound assignment. Either way the empty case skips it and the previous "
            + "selection stays live and runnable: " + string.Join("; ", guarded));
    }

    /// <summary>
    /// Every method that rebuilds or empties the list also sets the
    /// selection into it. Paired per method rather than counted over the
    /// file: two totals can agree while the pairing is wrong, and a global
    /// tally also fails on the harmless direction, which teaches whoever
    /// hits it to rebalance the counter instead of trusting it.
    /// </summary>
    [Fact]
    public void EveryMethodThatWritesTheListAlsoSetsTheSelection()
    {
        var offenders = ShellSource.Load(ViewModel).Root
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString().EndsWith("FilteredCommands", System.StringComparison.Ordinal)))
            .Where(m => !m.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString() == "SelectedCommand"))
            .Select(m => m.Identifier.ValueText)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These methods assign FilteredCommands without setting SelectedCommand. The "
            + "selection then points into a list that no longer contains it, and Enter "
            + "runs a command that is not on screen: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// A method emptied to `{ }` passes every guard above and takes the
    /// whole Step unit-test suite with it, since nothing else calls it.
    /// </summary>
    [Fact]
    public void BothMoversStillGoThroughStep()
    {
        Assert.Equal(2, ShellSource.Load(ViewModel).Root.Calls("PaletteSelection.Step").Count);
    }

    // The guard that used to live here -- "no disabled #if region assigns
    // FilteredCommands" -- moved into ShellSource.Load, which now parses with
    // the symbol defined and refuses outright to hand back a tree that still
    // has disabled regions. So the demo block in this file is real syntax to
    // the guards above rather than trivia they look through, and the same now
    // holds for every file a test reads through Load rather than for this one
    // alone. It is not corpus-wide: AllUnder hands back raw text, and
    // ParseForCorpusScan and AllShellSources each skip that refusal on
    // purpose, for the reason each of them documents.
}
