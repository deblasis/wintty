using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The active tab is the field: it alone is painted the terminal's own
/// ground, and it runs into the pane it belongs to with no line between.
/// Inactive tabs stay chrome. The accent is the STROKE around that field.
///
/// Both halves of that are one-token changes away from the thing they
/// replaced. The fill used to be the accent on the palette path, which made
/// the active tab a second, brighter chrome surface and had the seam cover
/// draw that accent straight across the pane's top border; putting
/// <c>AccentColor</c> back where <c>ActiveTabFill</c> now stands compiles,
/// looks deliberate, and undoes the decision. And the cover takes the tab's
/// brush INSTANCE rather than a brush mixed to the same colour, so the two
/// settle on one clock -- swapping in a copy is a tidy-up that is correct at
/// rest and wrong for every frame of the transition, which is precisely the
/// frames where a line appears between the tab and the pane.
///
/// Neither is observable without a live window, which is why they are pinned
/// on the syntax here.
/// </summary>
public sealed class ActiveTabFieldWiringTests
{
    private const string VerticalStrip = "Tabs.VerticalTabStrip.xaml.cs";
    private const string HorizontalStrip = "Tabs.TabHost.xaml.cs";
    private const string MainWindowFile = "Ghostty.MainWindow.xaml.cs";

    /// <summary>
    /// Each strip and the method that takes the palette. They are the same
    /// decision written twice, so both are held to it.
    /// </summary>
    public static TheoryData<string, string> BothStrips => new()
    {
        { HorizontalStrip, "ApplyShellTheme" },
        { VerticalStrip, "ApplyShellChrome" },
    };

    /// <summary>
    /// The palette path fills the active tab from the terminal's ground.
    ///
    /// Asserted on the property the brush is constructed from rather than on
    /// the absence of the word "accent", because the accent is still read in
    /// both files -- for the stroke, which is its job.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothStrips))]
    public void TheActiveTabIsFilledWithTheTerminalGround(string file, string applier)
    {
        var applied = ShellSource.Load(file).Method(applier);

        var fills = applied.AssignsTo("_selectedTabFillBrush").ToList();
        Assert.True(
            fills.Count == 1,
            $"expected one fill assignment in {file}.{applier}, found {fills.Count}");

        var made = Assert.IsType<ObjectCreationExpressionSyntax>(fills[0].Right);
        Assert.NotNull(made.ArgumentList);
        Assert.Equal("theme.ActiveTabFill", made.ArgumentList!.Arguments[0].ToString());
    }

    /// <summary>
    /// And inks it against that same ground, with the terminal's own
    /// foreground.
    ///
    /// Both arguments. Scoring the right ink against the wrong ground is the
    /// shape of the bug this replaced -- ink picked for the accent, painted
    /// on a surface that is not the accent -- and it leaves every literal in
    /// place.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothStrips))]
    public void TheActiveInkIsScoredAgainstTheFieldItSitsOn(string file, string applier)
    {
        var applied = ShellSource.Load(file).Method(applier);
        var scored = applied.Call("ThemeResolution.EnsureReadableForeground");

        Assert.Equal("theme.ActiveTabFill", Behind(applied, scored.Arg(0)));
        Assert.Equal("theme.ActiveTabInk", Behind(applied, scored.Arg(1)));
    }

    /// <summary>
    /// The service names those two for the role and takes them from the
    /// terminal pair, so a strip reading the wrong property is a rename away
    /// rather than a silent substitution.
    /// </summary>
    [Fact]
    public void TheServiceTakesTheFieldFromTheTerminalPair()
    {
        var recompute = ShellSource.Load("Services.ShellThemeService.cs").Method("Recompute");

        Assert.Equal("bg", Initializer(recompute, "newActiveTabFill"));
        Assert.Equal("fg", Initializer(recompute, "newActiveTabInk"));

        // The accent keeps its own source, and it is not the ground. Without
        // this the two could be quietly collapsed onto one value and every
        // rule above would still pass.
        Assert.NotEqual("bg", Initializer(recompute, "newAccent"));
    }

    /// <summary>
    /// The seam cover is handed the brush the tab is painted with.
    ///
    /// The horizontal strip raises it, so the assertion is on what the event
    /// carries. The vertical cover reads the row's own Background instead, so
    /// there the assertion is that the row is painted with the field --
    /// different mechanisms, one invariant.
    /// </summary>
    [Fact]
    public void TheCoverAndTheTabShareOneBrush()
    {
        var horizontal = ShellSource.Load(HorizontalStrip).Method("UpdateSelectedTabBridge");
        var placed = horizontal.Calls("SelectedTabSeamChanged?.Invoke")
            .Where(c => c.Arg(2) != "null")
            .ToList();
        Assert.True(
            placed.Count == 1,
            $"expected one placing raise of the seam event, found {placed.Count}");
        Assert.Equal("_field.Brush", placed[0].Arg(2));

        var vertical = ShellSource.Load(VerticalStrip).Method("UpdateSelectionRow");
        var painted = vertical.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "SelectionRow.Background")
            .ToList();
        Assert.True(
            painted.Count == 1,
            $"expected one paint of the selection row, found {painted.Count}");
        Assert.Equal("_field.Brush", painted[0].Right.ToString());
    }

    /// <summary>
    /// The settle starts from the strip's own ground and runs only when the
    /// strip's motion gate says so.
    ///
    /// The gate is the same one every other spring in the strip reads, not a
    /// second answer composed here: High Contrast collapses this to a cut,
    /// and a fill that keeps animating there is a fill the mode was supposed
    /// to have removed.
    /// </summary>
    [Theory]
    [InlineData(HorizontalStrip)]
    [InlineData(VerticalStrip)]
    public void TheFieldGrowsOutOfTheStripGround_BehindTheSharedMotionGate(string file)
    {
        var settle = ShellSource.Load(file).Root.Call("_field.Settle");

        Assert.Equal("StripGround()", settle.Arg(2));

        var gate = settle.ArgExpression(3).AssertCallTo("TabStripMotion.Enabled");
        Assert.Equal("SystemAnimationsEnabled()", gate.Arg(0));
        Assert.Equal("_highContrast", gate.Arg(1));
    }

    /// <summary>
    /// The strip's ground is the CHROME's, on both paths and in both strips.
    ///
    /// Naming StripGround() at the call site pins a spelling, not a value, and
    /// the two are very far apart here. _stripBackdropPacked had two writers:
    /// SetChromeGround, which knows what is behind the strip, and
    /// SetSelectedTabColors, which knows the terminal background -- a different
    /// surface entirely. Whichever ran last won, and which ran last differed
    /// between construction and a config reload.
    ///
    /// The vertical strip found that first, where the symptom was inactive
    /// titles calibrated against the terminal at about 2:1 until the config was
    /// touched. The horizontal strip kept the second writer, and the field
    /// inherited the bug in a form nobody would read as the same one: the
    /// flight's start colour came back equal to its target, and Settle answers
    /// a no-op move with a cut. So on the shipping default the field simply did
    /// not animate, in one strip only, while a guard asserting the argument's
    /// text stayed green.
    /// </summary>
    /// <param name="groundPush">
    /// The window's ground push, which the two strips spell differently: the
    /// horizontal strip takes it on its own call, the vertical one takes it
    /// alongside the row separator it is composed with.
    /// </param>
    [Theory]
    [InlineData(HorizontalStrip, "SetChromeGround")]
    [InlineData(VerticalStrip, "SetRowSeparator")]
    public void TheStripGroundHasOneWriter_AndItIsTheChromePush(string file, string groundPush)
    {
        var source = ShellSource.Load(file);

        var fromChrome = source.Method(groundPush).AssignsTo("_stripBackdropPacked").ToList();
        Assert.True(
            fromChrome.Count > 0,
            $"{groundPush} does not set the strip's ground, so on the palette path "
            + "the ground is whatever another method last guessed");

        var fromTerminal = source.Method("SetSelectedTabColors")
            .AssignsTo("_stripBackdropPacked").ToList();
        Assert.True(
            fromTerminal.Count == 0,
            "SetSelectedTabColors writes the strip's ground, but its argument is the "
            + "TERMINAL background. Two writers means the answer depends on call order, "
            + "and the field's flight then starts where it ends and cuts: "
            + string.Join("; ", fromTerminal.Select(a => a.ToString())));
    }

    /// <summary>
    /// A settle in flight survives the passes that are not activations.
    ///
    /// Without this the transition exists in the source and never on the
    /// screen, which is how it shipped in the first draft: the chrome pass
    /// that paints the field runs many times per activation -- a layout pass,
    /// a theme refresh, a size change, the selection row re-placing itself --
    /// and every one of them stopped the clock and wrote the target, so the
    /// fill arrived within a frame or two of leaving. Nothing observable
    /// distinguishes that from a snap, and no test that only checks the
    /// animation is CONSTRUCTED would have noticed.
    /// </summary>
    [Fact]
    public void ASettleInFlightIsNotCutShortByAPassThatIsNotAnActivation()
    {
        var settle = ShellSource.Load("Tabs.ActiveFieldFill.cs").Method("Settle");

        // The guard has to stand before the Stop, or it guards nothing.
        //
        // A braced early return is the same guard, so the shape is "an if whose
        // body is a return, or a block containing only one" -- pinning the
        // unbraced form would make a formatting choice fail the build.
        var guard = settle.DescendantNodes().OfType<IfStatementSyntax>()
            .FirstOrDefault(i => IsEarlyReturn(i.Statement)
                && i.Condition.ToString().Contains("_running", StringComparison.Ordinal));
        Assert.True(guard is not null,
            "Settle has no early return protecting a settle already in flight");

        // Any stop of a storyboard, not one spelled a particular way: the local
        // that holds the abandoned board is an implementation detail, and a
        // guard keyed to its name breaks on a rename that changes nothing.
        var stop = settle.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .First(e => e.CalleeText().EndsWith("Stop", StringComparison.Ordinal));
        Assert.True(
            guard!.SpanStart < stop.SpanStart,
            "the in-flight guard runs after the clock is stopped, so it protects nothing");

        // Every conjunct named, and named as a SET rather than as a substring
        // of the whole condition. "!moved" is satisfied by "!movedRecently",
        // and by "!moved" appearing anywhere in a condition however it is
        // combined -- including an OR, which would swallow real activations
        // and freeze the field on one colour.
        Assert.DoesNotContain(
            guard.Condition.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>(),
            b => b.IsKind(SyntaxKind.LogicalOrExpression));

        var operands = Conjuncts(guard.Condition).Select(o => o.ToString()).ToList();
        Assert.Equal(
            new[] { "!moved", "_running is not null", "_target == target" }.OrderBy(s => s),
            operands.OrderBy(s => s));
    }

    /// <summary>An if-body that is a return, braced or not.</summary>
    private static bool IsEarlyReturn(StatementSyntax body)
        => body is ReturnStatementSyntax
            || (body is BlockSyntax block
                && block.Statements.Count == 1
                && block.Statements[0] is ReturnStatementSyntax);

    /// <summary>
    /// The operands of an &amp;&amp; chain, flattened.
    ///
    /// Only &amp;&amp; is walked through. Every other expression -- including
    /// `_target == target`, which is itself a BinaryExpressionSyntax -- is a
    /// leaf. Recursing into any binary node instead would have flagged that
    /// equality as a non-AND and failed the correct code, which is exactly what
    /// the first draft of this helper did.
    ///
    /// The || case is caught by the caller rather than here: with an OR at the
    /// top this returns the whole condition as ONE operand, so the set
    /// comparison fails anyway, but it fails with a confusing message.
    /// </summary>
    private static IEnumerable<ExpressionSyntax> Conjuncts(ExpressionSyntax condition)
    {
        if (condition is BinaryExpressionSyntax binary
            && binary.IsKind(SyntaxKind.LogicalAndExpression))
        {
            foreach (var left in Conjuncts(binary.Left)) yield return left;
            foreach (var right in Conjuncts(binary.Right)) yield return right;
            yield break;
        }
        yield return condition;
    }

    /// <summary>
    /// A local one hop back, so a rule can name the value rather than the
    /// name the method happened to give it.
    /// </summary>
    private static string Behind(MethodDeclarationSyntax method, string name)
    {
        var declared = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.ValueText == name);
        if (declared?.Initializer is null) return name;

        // The packs are the shape both strips use: PackColor(theme.X).
        var initializer = declared.Initializer.Value;
        if (initializer is InvocationExpressionSyntax call
            && call.CalleeText().EndsWith("PackColor", StringComparison.Ordinal))
        {
            return call.Arg(0);
        }
        return initializer.ToString();
    }

    private static string Initializer(MethodDeclarationSyntax method, string name)
    {
        var declared = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.ValueText == name);
        Assert.True(declared?.Initializer is not null, $"no local '{name}' with an initializer");
        return declared!.Initializer!.Value.ToString();
    }

    /// <summary>
    /// The seam cover is a copy of a fact the layout already knows (the
    /// selected tab's span), and it drifts wherever that copy is not
    /// re-derived: mid-switch (the cover's cell origin moves with the
    /// collapsing lane while the span is measured in host space), during
    /// strip scroll (no enumerated event fires), and across caption-inset
    /// reflows (MinWidth moves slots without a SizeChanged). These pins are
    /// the three doors the fix closed, plus the coordinate-basis fix that
    /// makes the mid-flight door structural rather than timing-dependent.
    /// </summary>
    [Fact]
    public void TheSeamCoverCannotDrift()
    {
        var window = ShellSource.Load(MainWindowFile);

        // The cover lives in grid space, not in cell (1,1): a margin in
        // cell space is only correct while the strip column reads zero.
        var creation = window.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().Contains("_tabSeamCover"))
            .ToList();
        Assert.Contains(creation,
            a => a.Right.ToString().Contains("Microsoft.UI.Xaml.Shapes.Rectangle"));
        var source = window.Root.ToString();
        Assert.Contains("Grid.SetColumn(_tabSeamCover, 0)", source);
        Assert.Contains("Grid.SetColumnSpan(_tabSeamCover, 2)", source);

        // The seam gate refuses mid-switch placements: the landing's
        // RefreshSeam re-places into a settled frame.
        var gate = window.Method("OnSelectedTabSeamChanged");
        Assert.Contains(gate.DescendantNodes().OfType<IdentifierNameSyntax>(),
            i => i.Identifier.ValueText == "IsSwitching");

        // A successful placement re-checks while the span is still moving
        // and stops when it stops moving: the fixpoint arming.
        var bridge = ShellSource.Load(HorizontalStrip).Method("UpdateSelectedTabBridge");
        var successArm = bridge.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .SingleOrDefault(i => i.Condition.ToString().Contains("_lastSeamLeft"));
        Assert.NotNull(successArm);
        Assert.Contains(successArm.Calls("ArmBridgeRetry"), _ => true);

        // A user scroll moves the drawn tab without firing any enumerated
        // event; the scroller's ViewChanged re-derives the span. The
        // subscription must name the real handler - a neutralized no-op
        // lambda still reads as a subscription.
        var host = ShellSource.Load(HorizontalStrip).Root;
        Assert.Contains(host.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                 && a.Right is IdentifierNameSyntax id
                 && id.Identifier.ValueText == "OnTabStripScrollerViewChanged");
        var handler = ShellSource.Load(HorizontalStrip).Method("OnTabStripScrollerViewChanged");
        Assert.Contains(handler.Calls("QueueBridgeUpdate"), _ => true);
    }
}
