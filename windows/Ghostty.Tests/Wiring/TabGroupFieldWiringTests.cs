using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Tabs;
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
    /// <remarks>
    /// Four horizontally: the member's rest state, and the three states one
    /// item on a field wears (rest, pointer-over, pressed). The last three are
    /// one ink at three strengths -- MUXC owns those brushes for an unstyled
    /// tab, and writing the rest value into all of them left every member of a
    /// group unable to answer the pointer.
    /// </remarks>
    public static TheoryData<string, int> WashSites => new()
    {
        { VerticalStrip, 1 },
        { HorizontalStrip, 4 },
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
            // WashArgb or WashArgbAt: the deepened hover and pressed states are
            // the same ink at another alpha, and both must still be scored
            // against the strip's ground rather than against the composite.
            var argb = Assert.IsType<InvocationExpressionSyntax>(wash.ArgExpression(0));
            var callee = argb.CalleeText();
            Assert.True(
                callee is "TabGroupField.WashArgb" or "TabGroupField.WashArgbAt",
                $"a wash paint is built from '{callee}', which is neither of the two "
                + "helpers that keep the alpha on: " + wash);
            Assert.Equal("_stripBackdropPacked", argb.Arg(0));
        }
    }

    /// <summary>
    /// A member on a field still answers the pointer: its three unselected
    /// states are three different strengths of the wash, deepening.
    ///
    /// MUXC gives an unstyled tab its own pointer-over and pressed brushes, and
    /// the field's first draft wrote one value into all three -- so a run of
    /// members, and the chip that stands for a folded one, stopped responding
    /// to the pointer entirely. The chip is a click target: it expands the run.
    /// Equal alphas compile and look deliberate.
    /// </summary>
    [Fact]
    public void Horizontal_AWashedItemStillAnswersThePointer()
    {
        var states = ShellSource.Load(HorizontalStrip).Method("ApplyFieldWashStates");
        var keys = states.Calls("SetItemHeaderBrush").Select(c => c.Arg(1)).ToList();
        Assert.Equal(
            new[]
            {
                "\"TabViewItemHeaderBackground\"",
                "\"TabViewItemHeaderBackgroundPointerOver\"",
                "\"TabViewItemHeaderBackgroundPressed\"",
            },
            keys);

        Assert.True(
            TabGroupField.WashAlpha < TabGroupField.WashHoverAlpha
                && TabGroupField.WashHoverAlpha < TabGroupField.WashPressedAlpha,
            "the three states must deepen: equal or inverted alphas are a member "
            + "that reads as unresponsive, or one that lightens under the finger");
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

        // Banning the call outright was the rule, and it stopped being true the
        // moment anything needed to SCORE against the field. Ancestry was the
        // first replacement and it is a spelling guard, not an API one: one
        // local in between, or `new SolidColorBrush(...)` instead of the
        // helper, walks straight past it.
        //
        // So the ban stands, with a named door. A method on this allowlist is
        // one whose job is to answer "what is the ground here"; anywhere else
        // the composite is unreachable and cannot be painted.
        var scorers = new[] { "SlotGroundRgb" };
        foreach (var call in ShellSource.Load(file).Root.Calls("TabGroupField.FieldGroundRgb"))
        {
            var owner = call.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            Assert.True(
                owner is not null && scorers.Contains(owner.Identifier.ValueText),
                "the composited field ground is read outside the methods that exist to "
                + $"score against it (in '{owner?.Identifier.ValueText ?? "<none>"}'). "
                + "Painting it puts an opaque patch over Mica -- the colour this design "
                + "rejected, arriving through the scoring door.");
        }
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
    [Fact]
    public void Vertical_TheTerminalsTakeTheGroupColourAgainstTheStripGround()
    {
        var call = ShellSource.Load(VerticalStrip).Root.Call("TabGroupField.TerminalRgb");
        Assert.Equal("_stripBackdropPacked", call.Arg(0));
        // Exact, not a substring: "Color" is satisfied by TabColor.Red, by
        // _selectedBorderColor, and by any identifier with the word in it, so
        // a bar hard-coded to one preset would have passed the rule written to
        // pin it to the group's.
        Assert.Equal("group.Color", call.Arg(1));
    }

    /// <summary>
    /// The ground a slot is scored against is resolved in the order the slot is
    /// PAINTED in: a preset beats being active.
    ///
    /// ApplyTabChrome gives a preset-coloured tab the opaque preset as its
    /// selected handle, and only a tab with no preset gets
    /// _selectedTabFillBrush. Asking "is this active" first therefore answers
    /// "the terminal background" for a slot painted its own colour -- and for
    /// an active Red member of a Red group that is an end bar scored against
    /// near-black, returned unlifted, and painted onto opaque Red. 1:1, which
    /// is the defect SlotGroundRgb was added to remove, reintroduced by the
    /// order of two ifs with every literal correct.
    /// </summary>
    [Fact]
    public void Horizontal_TheSlotGroundResolvesInThePaintOrder()
    {
        var ground = ShellSource.Load(HorizontalStrip).Method("SlotGroundRgb");
        var branches = ground.DescendantNodes().OfType<IfStatementSyntax>()
            .Select(i => i.Condition.ToString())
            .ToList();

        Assert.Equal(
            new[]
            {
                "row is TabStripProjection.HorizontalRow.Item { Tab: { } tab }",
                "tab.Color != TabColor.None",
                "selected && _selectedTabFillBrush is not null",
            },
            branches);

        // And the preset is scored at the alpha the slot actually wears: an
        // active coloured tab is opaque, an inactive one is the 89-alpha tint.
        var preset = ground.Call("TabColorPalette.EffectiveBackgroundRgb");
        Assert.Equal("selected", preset.Arg(1));
    }

    /// <summary>
    /// Horizontal: each end is scored against the slot it is PAINTED on.
    ///
    /// There is no Border here -- the cap is drawn on the run's first slot and
    /// the end bar on its last -- and those two slots need not be wearing the
    /// field. The selected tab keeps the terminal background, a member with a
    /// preset keeps that preset. One ink for both ends, scored against the
    /// field, answered for a surface neither bar sits on: a Blue cap on a
    /// selected tab over a blue-ish terminal is about 2.2:1, and a Red end bar
    /// on a Red member is very nearly invisible, while the rule reports both as
    /// clearing the floor.
    /// </summary>
    [Fact]
    public void Horizontal_EachTerminalIsScoredAgainstTheSlotItIsPaintedOn()
    {
        var terminals = ShellSource.Load(HorizontalStrip)
            .Method("UpdateGroupFieldTerminals");
        var calls = terminals.Calls("TabGroupField.TerminalRgbOn");
        Assert.True(
            calls.Count == 2,
            $"expected the two ends to be inked separately, found {calls.Count}");

        // Two DIFFERENT slots, or the split bought nothing.
        var grounds = calls
            .Select(c => c.ArgExpression(0).AssertCallTo("SlotGroundRgb").Arg(0))
            .ToList();
        Assert.Equal(new[] { "rows[run.First]", "rows[run.Last]" }, grounds);
        Assert.All(calls, c => Assert.Equal("run.Group.Color", c.Arg(1)));
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
        // The sides stay zero and each end is its token or nothing -- an end
        // the viewport clipped away is DROPPED rather than slid to the edge of
        // the scroller, where it would claim a run starts or stops at whatever
        // row happens to be there. The ternaries are the whole of that rule, so
        // they are pinned as text: replacing either with the bare token brings
        // back the false terminal, and replacing either with 0 removes an end
        // the design says every run has.
        Assert.Equal(
            new[]
            {
                "0",
                "capVisible ? TabGroupField.TerminalThicknessPx : 0",
                "0",
                "endVisible ? TabGroupField.TerminalThicknessPx : 0",
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
    /// Horizontal: the ground push reaches the TERMINALS too, not only the
    /// surfaces the wash lands on.
    ///
    /// The guard above proves the call exists; it cannot see that the callee
    /// does the whole job. RefreshGroupFieldWash repainted header brushes --
    /// members and chip -- and nothing else, while a bar's Fill is only ever
    /// set by UpdateGroupFieldTerminals, which rides a pass neither
    /// SetChromeFill nor SetChromeGround reaches. So a frame-style flip moved
    /// the ground, the members repainted around the new pole, and the cap and
    /// end bar kept a colour lifted against the field they used to sit on: a
    /// Graphite bar lifted for a light field is a dark grey, and lands near
    /// 1.5:1 once that field goes dark. The method's own comment claimed it
    /// re-derived the terminals while it did not.
    /// </summary>
    [Fact]
    public void Horizontal_TheGroundPushAlsoReInksTheTerminals()
    {
        var wash = ShellSource.Load(HorizontalStrip).Method("RefreshGroupFieldWash");
        Assert.NotEmpty(wash.Calls("QueueBridgeUpdate"));
    }

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
    /// Horizontal: the terminals are suppressed while a drag is live, and
    /// suppressed the way the vertical field is -- by LEAVING them, not by
    /// hiding them.
    ///
    /// The strip's slots are TabView's reorder preview mid-drag, not the
    /// manager's order, so a run's ends are not where the projection says and
    /// an unsuppressed end bar sits over a stranger for the length of the
    /// gesture. But falling through to the collapse loop instead of returning
    /// hid every bar while the wash on the members stayed, so a run read as a
    /// tint with no beginning and no end -- and the vertical field, which
    /// returns, did not do that. Two strips, one gesture, one answer.
    /// </summary>
    [Fact]
    public void Horizontal_TheTerminalsAreSuppressedWhileADragIsLive()
    {
        var pass = ShellSource.Load(HorizontalStrip).Method("UpdateGroupFieldTerminals");
        var guard = Assert.Single(
            pass.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString().Contains("_stripDragActive", StringComparison.Ordinal));

        // Un-negated and an early return, which is the vertical rule exactly.
        // A "!" here would mean placing ONLY during a drag; falling through
        // instead of returning is the vanishing bar.
        Assert.Equal("_stripDragActive", guard.Condition.ToString());
        Assert.IsType<ReturnStatementSyntax>(guard.Statement);

        // And it stands before anything is measured, so a drag costs no
        // projection walk and no TransformToVisual sweep either.
        var firstWalk = pass.Calls("TabStripProjection.HorizontalRows").FirstOrDefault();
        Assert.True(
            firstWalk is not null && guard.SpanStart < firstWalk.SpanStart,
            "the drag guard runs after the strip has already been measured");
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
    /// Vertical: a field is retired when the MANAGER stops holding the group,
    /// and never merely because the header row was rebuilt.
    ///
    /// A field that outlives its run is a container drawn around tabs that are
    /// no longer in it, so the retirement has to happen -- but tying it to the
    /// header row's lifetime made it happen constantly. A collapse changes how
    /// many rows the strip shows; ReconcileRowOrder answers a changed count
    /// with RebuildAllItems; that removes and re-adds every group's row. So
    /// every field on the strip was destroyed and re-created on any collapse,
    /// expand, group creation or dissolution -- precisely the four events that
    /// change a field's size, and the only ones the 250ms glide exists for. It
    /// cut and faded instead, and the earlier form of this guard held that in
    /// place while reading like a safety rule.
    /// </summary>
    [Fact]
    public void Vertical_TheFieldIsRetiredWhenTheManagerDropsTheGroup()
    {
        var strip = ShellSource.Load(VerticalStrip);

        Assert.Empty(strip.Method("RemoveGroupRow").Calls("RemoveGroupField"));

        // Retired from the placement pass, and only for a group the manager
        // no longer holds -- the same pass that merely HIDES a field it could
        // not place this time round.
        var pass = strip.Method("UpdateGroupFields");
        Assert.NotEmpty(pass.Calls("RemoveGroupField"));

        var stillHeld = Assert.Single(
            pass.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString().Contains("_manager.Groups", StringComparison.Ordinal));
        Assert.Equal("_manager.Groups.Contains(group)", stillHeld.Condition.ToString());

        // Which arm does what, not merely that both exist. Swapping the two
        // bodies satisfies every assertion above while retiring every LIVE
        // group's field on every pass and keeping a dissolved group's forever
        // -- the exact inversion of the property this test is named for, with
        // the condition and the call sites all still present.
        Assert.Empty(stillHeld.Statement.Calls("RemoveGroupField"));
        Assert.Contains(
            "Visibility.Collapsed", stillHeld.Statement.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Both washed surfaces actually ask for the three-state ramp.
    ///
    /// The ramp's own test loads ApplyFieldWashStates by name, and the wash
    /// count is taken file-wide, so deleting both CALLS leaves the method as
    /// dead code, the count unchanged, and every other assertion green -- while
    /// every member of a group and every chip goes back to not answering the
    /// pointer.
    /// </summary>
    [Fact]
    public void Horizontal_BothWashedSurfacesTakeTheRamp()
    {
        var strip = ShellSource.Load(HorizontalStrip);
        Assert.NotEmpty(strip.Method("ApplyTabChrome").Calls("ApplyFieldWashStates"));
        Assert.NotEmpty(strip.Method("RefreshChip").Calls("ApplyFieldWashStates"));
    }

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
