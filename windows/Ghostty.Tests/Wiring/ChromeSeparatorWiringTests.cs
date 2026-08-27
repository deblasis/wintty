using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// With the chrome bare backdrop, the rows are two pieces of one surface and
/// the line between them is all that divides them. Two rules about that line
/// are easy to lose in a later edit and neither shows up as a crash: drawing
/// it over chrome that already separates by shade, and drawing it across the
/// join the selected row makes with the terminal, which is deliberately
/// continuous.
///
/// Wiring guards. Whether the lines land on the right pixels is only
/// observable on a live strip. Written to fail on the mutations that matter
/// rather than on the text changing: a substring match over syntax passes an
/// inserted negation, a permuted argument list, and a deleted operand, all of
/// which invert the feature.
/// </summary>
public class ChromeSeparatorWiringTests
{
    private static ShellSource Window() => ShellSource.Load("MainWindow.xaml.cs");

    /// <summary>
    /// window-theme=wintty and High Contrast both paint their own surfaces
    /// from real palettes, so a stroke over either is a second boundary drawn
    /// where there is one edge. Both operands, and both negated: the gate is
    /// "neither is painting", so dropping either half or losing a `!` widens
    /// it to a path that does not want strokes.
    /// </summary>
    [Fact]
    public void TheGate_IsExactlyNeitherPaintedPath()
    {
        var gate = Window().Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "ChromeSeparatorsWanted");

        var expression = gate.ExpressionBody?.Expression as BinaryExpressionSyntax;
        Assert.NotNull(expression);
        Assert.True(
            expression!.IsKind(SyntaxKind.LogicalAndExpression),
            "the gate must require both conditions, not either");

        var operands = new[] { expression.Left, expression.Right }
            .Select(o => o as PrefixUnaryExpressionSyntax)
            .ToList();
        Assert.All(operands, o => Assert.True(
            o is not null && o.IsKind(SyntaxKind.LogicalNotExpression),
            "each operand must be negated: the gate is 'neither is painting'"));

        var negated = operands.Select(o => o!.Operand.ToString()).ToList();
        Assert.Contains("_shellTheme.IsEnabled", negated);
        Assert.Contains("HighContrastChromeActive", negated);
    }

    /// <summary>
    /// The colour is handed over only when the gate says so, and null
    /// otherwise. Checked on the conditional's own shape, because the failure
    /// worth catching is the branches being swapped: that draws strokes on
    /// exactly the two paths that must not have them and drops them from the
    /// one that must.
    /// </summary>
    [Fact]
    public void TheColour_IsGatedAndTheBranchesAreNotSwapped()
    {
        var push = Window().Method("ApplyChromeSeparators")
            .Call("_verticalTabHost.SetRowSeparator");

        var argument = push.ArgumentList.Arguments[0].Expression;
        var conditional = Assert.IsType<ConditionalExpressionSyntax>(argument);

        Assert.Equal("ChromeSeparatorsWanted", conditional.Condition.ToString());
        Assert.Contains("ChromeSeparator.Resolve", conditional.WhenTrue.ToString());
        Assert.True(
            conditional.WhenFalse.IsKind(SyntaxKind.NullLiteralExpression),
            "the ungated path must hand over null, not a colour");
    }

    /// <summary>
    /// The stroke is derived from the surface the rows sit on, not from the
    /// terminal. Those are different colours whenever the palette and the
    /// desktop disagree, and a stroke derived from the wrong one is invisible
    /// on the surface it is drawn on.
    /// </summary>
    [Fact]
    public void TheStroke_IsDerivedFromTheGroundItIsDrawnOn()
    {
        var apply = Window().Method("ApplyChromeSeparators");
        var push = apply.Call("_verticalTabHost.SetRowSeparator");

        var resolve = apply.Calls("Core.Shell.ChromeSeparator.Resolve").Single();
        Assert.Equal("ground", resolve.Arg(0));
        // The same value is handed over as the ground the strip calibrates
        // its own text against, so the two cannot drift.
        Assert.Equal("ground", push.Arg(1));
    }

    /// <summary>
    /// No line in either gap touching the selected row, and only while that
    /// row is actually covering them. Asserted on the guarded statements
    /// being `continue` under an equality test against the active tab:
    /// negating either condition inverts the feature into drawing only the
    /// two gaps that must not be drawn, which a text match does not notice.
    /// </summary>
    [Fact]
    public void RowSeparators_SkipBothGapsTouchingTheVisibleSelectedRow()
    {
        var method = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs")
            .Method("UpdateRowSeparators");

        // The identity skips only. The method also continues past items it
        // cannot measure, and those are not what this is about.
        var skips = method.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Statement is ContinueStatementSyntax)
            .Where(i => i.Condition is InvocationExpressionSyntax c
                && c.CalleeText() == "ReferenceEquals")
            .ToList();

        Assert.Equal(2, skips.Count);
        foreach (var skip in skips)
        {
            var call = (InvocationExpressionSyntax)skip.Condition;
            Assert.Equal("_manager.ActiveTab", call.Arg(1));
        }

        var subjects = skips
            .Select(s => ((InvocationExpressionSyntax)s.Condition).Arg(0))
            .ToList();
        Assert.Contains("tabs[i]", subjects);
        Assert.Contains("tabs[i + 1]", subjects);

        // Both skips live under the visibility test, so a collapsed row -- a
        // layout morph, MUXC's first frame -- does not leave two gaps with
        // nothing drawing them and nothing hiding them either.
        Assert.All(skips, skip => Assert.Contains(
            skip.Ancestors().OfType<IfStatementSyntax>(),
            a => a.Condition.ToString() == "selectionRowVisible"));
    }
}
