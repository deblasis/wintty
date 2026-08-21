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

    /// <summary>
    /// The parser runs with no preprocessor symbols, so a disabled `#if`
    /// region is trivia and every guard above looks straight through it.
    /// This file has a live `#if DEMO` block, so a demo path that touched
    /// the list would ship unguarded in DEMO builds.
    /// </summary>
    [Fact]
    public void NoDisabledRegionTouchesTheList()
    {
        var hidden = ShellSource.Load(ViewModel).Root
            .DescendantTrivia()
            .Where(t => t.IsKind(SyntaxKind.DisabledTextTrivia))
            .Select(t => t.ToString())
            .Where(t => t.Contains("FilteredCommands"))
            .ToList();

        Assert.True(hidden.Count == 0,
            "A conditionally compiled region assigns FilteredCommands. It is trivia to "
            + "the parser, so the guards in this file cannot see it: "
            + string.Join("\n", hidden));
    }
}
