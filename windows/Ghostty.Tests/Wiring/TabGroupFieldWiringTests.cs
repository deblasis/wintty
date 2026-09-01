using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The group field, as the two strips wire it.
///
/// The arithmetic -- which slots a run owns, what the wash and the
/// terminals come out -- is executable and lives in
/// <c>TabGroupFieldTests</c>. What no unit test can reach is whether the
/// strips ASK, whether they ask about the right surface, and whether both
/// of them do it: the shell assembly cannot be loaded into a test host, so
/// these read the source.
///
/// Written against the syntax rather than the text, because the mutations
/// that matter here all keep every literal in place: handing the painter
/// the composited ground instead of the wash (an opaque patch over Mica),
/// dropping the end bar and keeping the cap (which is the rail this design
/// replaced, drawn from the other end), washing the selected tab (which
/// breaks the seam between the active tab and its pane), and dropping the
/// negation on the run-boundary divider skip (which deletes every INTERIOR
/// separator instead of the two at the ends).
///
/// What these cannot see: what the field looks like. Whether the wash
/// reads over a given wallpaper, whether the terminals land on the pixels
/// the geometry names, and whether the glide is smooth are all live-window
/// questions.
/// </summary>
public sealed class TabGroupFieldWiringTests
{
    private const string VerticalStrip = "Tabs.VerticalTabStrip.xaml.cs";
    private const string HorizontalStrip = "Tabs.TabHost.xaml.cs";

    /// <summary>
    /// Both strips, and the projection each one's field grammar is read
    /// from. This is the parity rule in its load-bearing form: the same
    /// helper, over the reading that belongs to that layout. A strip
    /// reading the other one's projection compiles and draws a field
    /// around the wrong slots.
    /// </summary>
    public static TheoryData<string, string> BothStrips => new()
    {
        { VerticalStrip, "GroupedRows" },
        { HorizontalStrip, "HorizontalRows" },
    };

    /// <summary>
    /// Each strip's wash paints, and how many it has: the vertical one
    /// paints the field itself; the horizontal one has no single element to
    /// paint, so it washes an expanded run's members and a folded run's
    /// chip -- two sites.
    ///
    /// The count is stated rather than left to "at least one" because
    /// every rule below walks the sites it finds, and a rule that walks an
    /// empty list passes for exactly the reason it would pass on a branch
    /// that paints nothing at all.
    /// </summary>
    public static TheoryData<string, int> WashSites => new()
    {
        { VerticalStrip, 1 },
        { HorizontalStrip, 2 },
    };

    private static List<InvocationExpressionSyntax> Washes(string file, int expected)
    {
        var found = ShellSource.Load(file).Root.Calls("TabColorBrush.FromPackedArgb");
        Assert.True(
            found.Count == expected,
            $"expected {expected} wash paint(s) in {file}, found {found.Count}");
        return found;
    }

    // ---- the grammar is read from Core, per layout ---------------------

    /// <summary>
    /// Every field either strip draws comes out of one walk, in Core, over
    /// that layout's own projection. Anything else -- iterating
    /// <c>_manager.Groups</c>, counting members by hand -- is a second
    /// implementation of the run walk that the executable tests do not
    /// cover and that can disagree with the other strip.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothStrips))]
    public void EachStrip_ReadsItsRunsFromTheSharedWalk(string file, string projection)
    {
        var runs = ShellSource.Load(file).Root.Call("TabGroupField.Runs");
        var slots = runs.ArgExpression(0).AssertCallTo("TabGroupField.SlotGroups");
        Assert.Equal(
            "TabStripProjection." + projection + "(_manager)",
            Resolved(slots.ArgExpression(0)));
    }

    /// <summary>
    /// An expression as written, or -- when it is a bare local -- that
    /// local's initializer in the same method. Both strips hold the row
    /// list in a variable because they index it afterwards, and a rule
    /// that could only read an inline argument would answer "rows" and
    /// pin nothing.
    /// </summary>
    private static string Resolved(ExpressionSyntax expression)
    {
        if (expression is not IdentifierNameSyntax local) return expression.ToString();

        var scope = expression.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        Assert.NotNull(scope);
        var declared = Assert.Single(
            scope!.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            v => v.Identifier.ValueText == local.Identifier.ValueText);
        Assert.NotNull(declared.Initializer);
        return declared.Initializer!.Value.ToString();
    }

    /// <summary>
    /// And nothing else in the shell draws one. A third caller is a third
    /// field grammar; the point of putting the walk in Core was that there
    /// is one.
    /// </summary>
    [Fact]
    public void NothingOutsideTheTwoStrips_DrawsAField()
    {
        var callers = ShellSource.AllShellSources()
            .Where(s => s.Root.Calls("TabGroupField.Runs").Count > 0)
            .Select(s => s.Resource)
            .ToList();

        Assert.Equal(2, callers.Count);
        Assert.All(
            new[] { VerticalStrip, HorizontalStrip },
            expected => Assert.Contains(callers, c => c.EndsWith("." + expected, StringComparison.Ordinal)));
    }

    // ---- the wash ------------------------------------------------------

    /// <summary>
    /// The wash is handed to the painter with its alpha still on it, and
    /// scored against the strip's own ground.
    ///
    /// Both halves, because either one alone reproduces a shipped-shaped
    /// bug: <c>FieldGroundRgb</c> is one identifier away and answers with
    /// the OPAQUE composite, which paints a solid patch over Mica -- the
    /// colour this decision rejected, arriving by a different door -- and
    /// scoring against anything but the strip's ground is the mistake the
    /// muted ink already shipped once.
    /// </summary>
    [Theory]
    [MemberData(nameof(WashSites))]
    public void TheWash_KeepsItsAlpha_AndIsScoredAgainstTheStripGround(string file, int sites)
    {
        foreach (var wash in Washes(file, sites))
        {
            var argb = wash.ArgExpression(0).AssertCallTo("TabGroupField.WashArgb");
            Assert.Equal("_stripBackdropPacked", argb.Arg(0));
        }
    }

    /// <summary>
    /// Nothing in either strip paints the field as the composite the wash
    /// LANDS as. That helper exists to score contrast against, and using
    /// it as a fill is the opaque-patch bug above.
    /// </summary>
    [Theory]
    [MemberData(nameof(WashSites))]
    public void NoStrip_PaintsTheFieldAsItsCompositedGround(string file, int sites)
    {
        Washes(file, sites);
        Assert.Empty(ShellSource.Load(file).Root.Calls("TabGroupField.FieldGroundRgb"));
    }

    /// <summary>
    /// Horizontal only, and the sharpest rule here: the wash lands on the
    /// UNSELECTED handle, never on the selected one.
    ///
    /// The selected tab keeps the terminal's own background so it reads as
    /// continuous with the pane below it -- that continuity is what the
    /// whole seam-cover machinery exists to protect. Washing over it is an
    /// 8% shift on exactly the one surface that has to match another
    /// surface exactly, and it is invisible in review: the two handles are
    /// named one word apart and both are in scope at the site.
    /// </summary>
    [Fact]
    public void Horizontal_TheWashNeverLandsOnTheSelectedHandle()
    {
        var chrome = ShellSource.Load(HorizontalStrip).Method("ApplyTabChrome");
        var wash = Assert.Single(chrome.Calls("TabColorBrush.FromPackedArgb"));

        var assignment = wash.FirstAncestorOrSelf<AssignmentExpressionSyntax>();
        Assert.NotNull(assignment);
        Assert.Equal("normalHandle", assignment!.Left.ToString());
    }

    // ---- the terminals -------------------------------------------------

    /// <summary>
    /// The cap and the end bar take the group's colour, lifted against the
    /// strip's ground. A terminal painted straight from the palette is the
    /// 1.57:1 chip #882 measured, and it compiles.
    /// </summary>
    [Theory]
    [InlineData(VerticalStrip)]
    [InlineData(HorizontalStrip)]
    public void TheTerminals_TakeTheGroupColourAgainstTheStripGround(string file)
    {
        var call = ShellSource.Load(file).Root.Call("TabGroupField.TerminalRgb");
        Assert.Equal("_stripBackdropPacked", call.Arg(0));
        Assert.Contains("Color", call.Arg(1), StringComparison.Ordinal);
    }

    /// <summary>
    /// Vertical: the field is capped at the top and closed at the bottom,
    /// and open on both sides.
    ///
    /// The bottom is the whole point. A field with a cap and no end bar is
    /// the rail this design replaced -- a mark that says where a run
    /// begins and leaves its extent to be counted -- and dropping one
    /// argument of four produces exactly that.
    /// </summary>
    [Fact]
    public void Vertical_TheFieldIsCappedAtTheTopAndClosedAtTheBottom()
    {
        var paint = ShellSource.Load(VerticalStrip).Method("PaintGroupField");
        var assignment = Assert.Single(
            paint.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "field.BorderThickness");

        var thickness = Assert.IsType<ObjectCreationExpressionSyntax>(assignment.Right);
        Assert.Equal(
            new[]
            {
                "0",
                "TabGroupField.TerminalThicknessPx",
                "0",
                "TabGroupField.TerminalThicknessPx",
            },
            thickness.ArgumentList!.Arguments.Select(a => a.ToString()).ToArray());
    }

    /// <summary>
    /// Horizontal: two terminals per run, one at each end.
    ///
    /// Asserted on the offsets rather than on the count alone, because two
    /// bars drawn at the same edge is the same defect as one bar with a
    /// different symptom -- and "right" written where "left" belongs is a
    /// four-character edit.
    /// </summary>
    [Fact]
    public void Horizontal_EveryRunGetsBothATerminalAtEachEnd()
    {
        var pass = ShellSource.Load(HorizontalStrip).Method("UpdateGroupFieldTerminals");
        var placed = pass.Calls("PlaceFieldTerminal");

        Assert.Equal(2, placed.Count);
        Assert.Equal("left", placed[0].Arg(1));
        Assert.Equal("right - TabGroupField.TerminalThicknessPx", placed[1].Arg(1));
    }

    // ---- when it is placed ---------------------------------------------

    /// <summary>
    /// Each strip's field placement rides the pass that already runs on
    /// every mutation, exactly once. Its own caller list would be a second
    /// set of doors to remember, and the ones that get forgotten are the
    /// exit paths.
    /// </summary>
    [Theory]
    [InlineData(VerticalStrip, "UpdateRowSeparators", "UpdateGroupFields")]
    [InlineData(HorizontalStrip, "UpdateSelectedTabBridge", "UpdateGroupFieldTerminals")]
    public void TheFieldRidesTheStripsOwnPlacementPass(
        string file, string pass, string placement)
        => Assert.Single(ShellSource.Load(file).Method(pass).Calls(placement));

    /// <summary>
    /// Every push that moves the strip's ground re-derives the field.
    ///
    /// The wash's pole and the terminals' lift are both scored against
    /// that ground, so a push that recalibrates the muted ink and leaves
    /// the field alone is the same defect the ink already shipped once,
    /// one surface along -- and a fill-only push (a frame style change)
    /// reaches no other placement pass in either strip, so this is the
    /// only door it can arrive by.
    /// </summary>
    [Theory]
    [InlineData(VerticalStrip, "UpdateGroupFields")]
    [InlineData(HorizontalStrip, "RefreshGroupFieldWash")]
    public void EveryPushThatMovesTheGround_ReDerivesTheField(string file, string rederive)
        => Assert.Single(ShellSource.Load(file)
            .Method("RefreshShellInactiveInk").Calls(rederive));

    /// <summary>
    /// Vertical: the field is suppressed while a drag is live, the same
    /// rule and for the same reason as the horizontal terminals below --
    /// mid-drag the rows' arranged slots run ahead of their visuals, so a
    /// field placed from them brackets a run the eye can see is elsewhere.
    ///
    /// Stated here because this pass now has a second caller that does not
    /// go through the selection row's own drag check.
    /// </summary>
    [Fact]
    public void Vertical_TheFieldIsSuppressedWhileADragIsLive()
    {
        var pass = ShellSource.Load(VerticalStrip).Method("UpdateGroupFields");
        var guard = Assert.Single(
            pass.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString().Contains("_drag", StringComparison.Ordinal));

        Assert.Equal("_drag is not null", guard.Condition.ToString());
        Assert.IsType<ReturnStatementSyntax>(guard.Statement);
    }

    /// <summary>
    /// Horizontal: the terminals are suppressed while a drag is live. The
    /// strip's slots are TabView's reorder preview then, not the manager's
    /// order, so a run's ends are not where the projection says -- an
    /// unsuppressed end bar sits over a stranger for the length of the
    /// gesture.
    /// </summary>
    [Fact]
    public void Horizontal_TheTerminalsAreSuppressedWhileADragIsLive()
    {
        var pass = ShellSource.Load(HorizontalStrip).Method("UpdateGroupFieldTerminals");
        var guard = Assert.Single(
            pass.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString().Contains("_stripDragActive", StringComparison.Ordinal));

        // Negated: the placement happens when a drag is NOT live. Dropping
        // the "!" inverts the rule into placing ONLY during a drag, which
        // is a strip with no fields at rest.
        var negation = Assert.IsType<PrefixUnaryExpressionSyntax>(guard.Condition);
        Assert.True(negation.IsKind(SyntaxKind.LogicalNotExpression));
    }

    /// <summary>
    /// Vertical: the field's motion is on the strip's motion gate, read
    /// with the polarity the gate documents -- animations on, High
    /// Contrast off. An inverted second argument animates in exactly the
    /// configuration that asked for no animation.
    /// </summary>
    [Fact]
    public void Vertical_TheFieldMotionIsOnTheStripsMotionGate()
    {
        var pass = ShellSource.Load(VerticalStrip).Method("UpdateGroupFields");
        var gate = Assert.Single(pass.Calls("TabStripMotion.Enabled"));

        Assert.Equal("SystemAnimationsEnabled()", gate.Arg(0));
        Assert.IsType<IdentifierNameSyntax>(gate.ArgExpression(1));
        Assert.Equal("_highContrast", gate.Arg(1));
    }

    /// <summary>
    /// Vertical: a dissolved group's field goes out the same door its
    /// header row does. A field that outlives its run is a container drawn
    /// around tabs that are no longer in it.
    /// </summary>
    [Fact]
    public void Vertical_TheFieldIsRetiredWithItsHeaderRow()
        => Assert.Single(ShellSource.Load(VerticalStrip)
            .Method("RemoveGroupRow").Calls("RemoveGroupField"));

    /// <summary>
    /// Vertical: the generic divider is dropped at a run's two boundaries,
    /// and only there.
    ///
    /// The negation is the load-bearing character. Without it the rule
    /// reads "skip the divider between two rows of the SAME group", which
    /// deletes every interior separator in every run and leaves the two at
    /// the ends doubled up with the cap and the end bar -- the exact
    /// inverse of the intent, in one keystroke, with every literal intact.
    /// </summary>
    [Fact]
    public void Vertical_TheDividerIsDroppedWhereTheFieldsOwnRuleAlreadyDrawsOne()
    {
        var pass = ShellSource.Load(VerticalStrip).Method("UpdateRowSeparators");
        var skip = Assert.Single(
            pass.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString().Contains(".Group", StringComparison.Ordinal));

        var negation = Assert.IsType<PrefixUnaryExpressionSyntax>(skip.Condition);
        Assert.True(negation.IsKind(SyntaxKind.LogicalNotExpression));

        var compare = negation.Operand.AssertCallTo("ReferenceEquals");
        Assert.Equal("tabs[i].Group", compare.Arg(0));
        Assert.Equal("tabs[i + 1].Group", compare.Arg(1));
        Assert.IsType<ContinueStatementSyntax>(skip.Statement);
    }
}
