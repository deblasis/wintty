using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Unselected tab titles are drawn muted over the strip, so the ink they are
/// drawn in has to be chosen against whatever the strip actually is. That
/// used to be the palette's tab-bar shade in every case, because the strip
/// was always painted with it. It is not any more: a frosted or crystal frame
/// leaves the strip bare so the backdrop shows through, and the shade the
/// palette names is then a colour nothing renders.
///
/// The arithmetic of the choice lives in Ghostty.Core
/// (<c>ThemeResolution.ReadableMutedForeground</c>, the muting ladder) and is
/// tested there. These are the wiring: which surface every draw is made
/// against, that both layouts and both theme paths draw from the one answer,
/// that the alpha the ink is painted at is the alpha it was scored at, and
/// that every input which moves the ground re-asks. Whether the text is then
/// legible is only observable on a live window, which is how the fixed-alpha
/// rule shipped in the first place (#936).
///
/// Written against the syntax rather than the text, so the mutations that
/// matter fail: scoring against the wrong field, painting at a fixed alpha
/// the ladder never scored, and dropping the recalibration from one of the
/// pushes all keep every literal in place.
/// </summary>
public sealed class StripInkGroundWiringTests
{
    private const string VerticalStrip = "Tabs.VerticalTabStrip.xaml.cs";
    private const string HorizontalStrip = "Tabs.TabHost.xaml.cs";

    /// <summary>
    /// Each strip and the number of muted-ink draws it has: the vertical one
    /// draws on the palette path and again in its per-row fallback, the
    /// horizontal one on the palette path and again for the default path's
    /// unselected titles.
    ///
    /// The count is here rather than left to "at least one" because every
    /// rule below walks the draws it finds. A rule that walks an empty list
    /// passes for the same reason it would pass on the branch that has the
    /// bug, which pins nothing at all -- and the query going quiet is exactly
    /// what a rename of the helper does.
    /// </summary>
    public static TheoryData<string, int> BothStrips => new()
    {
        { VerticalStrip, 2 },
        { HorizontalStrip, 2 },
    };

    /// <summary>
    /// Every place a strip draws its muted ink, found by the helper it calls
    /// rather than by the method it sits in: a rule naming methods would
    /// quietly stop covering a third site the day one appears.
    /// </summary>
    private static List<InvocationExpressionSyntax> MutedInkDraws(string file, int expected)
    {
        var draws = ShellSource.Load(file).Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith(
                "MutedInkBrush", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            draws.Count == expected,
            $"expected {expected} muted-ink draw(s) in {file}, found {draws.Count}");
        return draws;
    }

    /// <summary>
    /// Every draw is made against the strip's own ground field.
    ///
    /// Reading anything else is the base branch's behaviour: the palette's
    /// tab-bar shade cannot see a bare strip, and the terminal background is
    /// the selected row's fill, a different surface.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothStrips))]
    public void TheMutedInk_IsDrawnAgainstTheStripGround(string file, int draws)
    {
        foreach (var draw in MutedInkDraws(file, draws))
            Assert.Equal("_stripBackdropPacked", draw.Arg(0));
    }

    /// <summary>
    /// The answer is painted at the alpha it was scored at, in the pole it
    /// was scored in.
    ///
    /// Both halves, because either one going stale reproduces the old bug in
    /// a new coat: scoring an opaque pole for muted ink is the mistake the
    /// first fix removed, and painting at a fixed alpha the ladder never
    /// scored is how #936's mid-grey failures shipped -- the ink cleared 4.5
    /// in the arithmetic that picked it and still rendered at 4.4.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothStrips))]
    public void TheAnswer_IsPaintedAtTheAlphaItWasScoredAt_InThePoleItWasScoredIn(
        string file, int draws)
    {
        MutedInkDraws(file, draws); // the census, so an empty file cannot pass

        var helper = ShellSource.Load(file).Method("MutedInkBrush");

        var ask = helper.Call("ThemeResolution.ReadableMutedForeground");
        Assert.Equal("ground", ask.Arg(0));
        Assert.Equal("InactiveInkAlpha", ask.Arg(1));

        var painted = Assert.Single(helper.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("FromArgb", StringComparison.Ordinal)));
        Assert.Equal("ink.Alpha", painted.Arg(0));
        Assert.Equal("(byte)(ink.Pole >> 16)", painted.Arg(1));
        Assert.Equal("(byte)(ink.Pole >> 8)", painted.Arg(2));
        Assert.Equal("(byte)ink.Pole", painted.Arg(3));
    }

    /// <summary>
    /// Nothing in either strip still scores the pole itself, at any alpha.
    /// The scoring - pole, alpha, floor - lives in ThemeResolution now; a
    /// strip that picks its own pole has a second ink rule, and a second ink
    /// rule is how the two layouts drifted apart in the first place.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothStrips))]
    public void NoStrip_StillScoresTheMutedInkItself(string file, int draws)
    {
        MutedInkDraws(file, draws);
        Assert.Empty(ShellSource.Load(file).Root
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith(
                "PreferLightForegroundAtAlpha", StringComparison.Ordinal)
                || c.CalleeText() == "ThemeResolution.PreferLightForeground"));
    }

    /// <summary>
    /// And no strip paints a brush at the preferred alpha directly: the only
    /// alpha that reaches a brush is the one the answer returned. This is the
    /// fact that fails if a site "simplifies" the helper call back into a
    /// fixed-alpha construction.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothStrips))]
    public void NoStrip_PaintsAtThePreferredAlphaDirectly(string file, int draws)
    {
        MutedInkDraws(file, draws);
        Assert.Empty(ShellSource.Load(file).Root
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("FromArgb", StringComparison.Ordinal)
                && c.ArgumentList.Arguments.Count > 0
                && c.Arg(0) == "InactiveInkAlpha"));
    }

    /// <summary>
    /// The ground is the strip's own fill while it has one, and the window's
    /// backdrop estimate while it does not.
    ///
    /// Asserted on the coalesce rather than on either operand alone: reading
    /// only the fill is the base branch's behaviour, which cannot see a bare
    /// strip, and reading only the estimate would miscalibrate the solid
    /// frame under window-theme=wintty, where the estimate is the desktop's
    /// shade and the strip is painted with the palette's.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothStrips))]
    public void TheGround_IsTheFillWhenPainted_AndTheBackdropWhenBare(string file, int draws)
    {
        MutedInkDraws(file, draws);

        var assignment = Assert.Single(
            ShellSource.Load(file).Method("RefreshShellInactiveInk")
                .DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "_stripBackdropPacked");

        var coalesce = Assert.IsType<BinaryExpressionSyntax>(assignment.Right);
        Assert.True(
            coalesce.IsKind(SyntaxKind.CoalesceExpression),
            "the ground is the fill or, failing that, the estimate");
        Assert.Equal("_chromeFillRgb", coalesce.Left.ToString());
        Assert.Equal("_chromeGroundPacked", coalesce.Right.ToString());
    }

    /// <summary>
    /// Both inputs re-ask. The window resolves the palette before it resolves
    /// the frame, so the ink drawn while the palette lands is one frame
    /// behind; only these two calls are late enough to be right, and losing
    /// either leaves the ink one config reload behind.
    /// </summary>
    [Theory]
    [InlineData(VerticalStrip, "SetChromeFill")]
    [InlineData(VerticalStrip, "SetRowSeparator")]
    [InlineData(HorizontalStrip, "SetChromeFill")]
    [InlineData(HorizontalStrip, "SetChromeGround")]
    public void EveryPushThatMovesTheGround_RecalibratesTheInk(string file, string method)
        => Assert.Single(ShellSource.Load(file).Method(method)
            .Calls("RefreshShellInactiveInk"));

    /// <summary>
    /// One read of the estimate, handed to both layouts. Resolving them
    /// separately is how the two strips end up calibrated against different
    /// surfaces for one config, which only shows on screen mid-switch when
    /// both are visible.
    /// </summary>
    [Fact]
    public void BothLayouts_AreToldTheSameGround()
    {
        var apply = ShellSource.Load("MainWindow.xaml.cs").Method("ApplyChromeSeparators");

        Assert.Equal("ground", apply.Call("_verticalTabHost.SetRowSeparator").Arg(1));
        Assert.Equal("ground", apply.Call("_horizontalTabHost.SetChromeGround").Arg(0));
    }

    /// <summary>
    /// The horizontal strip's default path draws its unselected titles from
    /// the same answer the palette path does. Leaving them on the element
    /// theme's own foreground was the one arm that never heard of the strip,
    /// and it measured 2.63:1 on stock-light against a 4.5 floor (#936) --
    /// the vertical strip had no such arm, which is why the defect was one
    /// layout wide.
    ///
    /// The draw is bound to the INACTIVE arm, not merely present in the
    /// method: the active title keeps its own full-strength brush, and a
    /// refactor that moved the muted draw into the active arm (muting the
    /// one title that must not mute, re-breaking the inactive ones) would
    /// otherwise keep every count and argument here green.
    /// </summary>
    [Fact]
    public void TheDefaultPath_DrawsItsInactiveTitlesFromTheAnswer()
    {
        var recolor = ShellSource.Load(HorizontalStrip).Method("RecolorTabText");
        var draw = Assert.Single(recolor.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("MutedInkBrush", StringComparison.Ordinal)));
        Assert.Equal("_stripBackdropPacked", draw.Arg(0));

        // The draw's enclosing else clause is the chain's last, bare else:
        // the inactive arm. (`else if` nests the if inside the clause's
        // statement, so a bare else is a clause whose statement is not an
        // if.) The active arm (`else if (active)`) holds no muted-ink draw
        // at all.
        var arm = draw.Ancestors().OfType<ElseClauseSyntax>().First();
        Assert.IsNotType<IfStatementSyntax>(arm.Statement);
        Assert.True(arm.Statement.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("MutedInkBrush", StringComparison.Ordinal))
            .Count() == 1);
        foreach (var other in recolor.DescendantNodes().OfType<ElseClauseSyntax>()
            .Where(c => !c.Equals(arm))
            // An `else if` clause's statement IS the nested if, so the
            // clause transitively contains every later arm including the
            // draw. Judge a clause by its own block: skip pure else-ifs,
            // whose nested arms appear as clauses of their own.
            .Where(c => c.Statement is not IfStatementSyntax))
            Assert.Empty(other.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(c => c.CalleeText().EndsWith("MutedInkBrush", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The close affordance takes the row's ink. It used to carry nothing and
    /// ride the element theme's button foreground, which on a light element
    /// theme over a mid-rendered strip drew dark-on-mid at 2.02:1 against a
    /// 3.0 floor (#936). Every arm feeds it: colored rows, the active row,
    /// and the inactive rows.
    /// </summary>
    [Fact]
    public void TheCloseGlyph_TakesTheRowInk()
    {
        var row = ShellSource.Load("Tabs.VerticalTabNavRow.cs");
        var applyInk = row.Method("ApplyInk");
        Assert.Equal(
            new[] { "_closeGlyph.Foreground" },
            applyInk.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Select(a => a.Left.ToString()).ToList());

        // The null contract is the guard, and the guard is the point: a null
        // ink means "no answer this pass", and the last calibrated one stands.
        // An inverted or dropped guard (writing null into the Foreground, or
        // keeping the stale brush when an answer DID arrive) must fail here.
        var guard = Assert.IsType<IfStatementSyntax>(
            applyInk.Body!.Statements.Single());
        Assert.Equal("foreground is not null", guard.Condition.ToString());
        Assert.Single(guard.Statement.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>());

        var recolor = ShellSource.Load(VerticalStrip).Method("RecolorNavItems");
        var feeds = recolor.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("ApplyInk", StringComparison.Ordinal))
            .ToList();
        // RecolorNavItems also feeds the pinned rows and the group headers;
        // this rule is the close glyph's, so the census is the nav-row feeds.
        var navRowFeeds = feeds.Where(c =>
            c.CalleeText().Contains("VerticalTabNavRow", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, navRowFeeds.Count);
    }

    /// <summary>
    /// The chip is not muted twice. Its ink is chosen to clear its floor as
    /// drawn; an element opacity on top of that composites the ink down to
    /// ~70% strength, which is the 3.55:1 member count of #936 (a single
    /// mute of the opaque-pole ink - the vertical header's count was the
    /// double-mute, #884's root cause B). 0.7 cannot clear AA on any ground,
    /// so the fix is removing the mute, not re-tuning it.
    ///
    /// Matched on every spelling an opacity can take - an initializer name,
    /// a member-access statement (`chip.Opacity = 0.7;`) and a SetValue of
    /// the property - so the mute cannot come back one cast away from the
    /// predicate.
    /// </summary>
    [Fact]
    public void TheChipCount_IsNotMutedByElementOpacity()
    {
        var addChip = ShellSource.Load(HorizontalStrip).Method("AddGroupChip");
        Assert.DoesNotContain(
            addChip.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left is IdentifierNameSyntax id && id.Identifier.ValueText == "Opacity"
                || a.Left is MemberAccessExpressionSyntax member
                && member.Name.Identifier.ValueText == "Opacity");
        Assert.Empty(addChip.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("SetValue", StringComparison.Ordinal)
                && c.ArgumentList.Arguments.Count > 0
                && c.Arg(0).Contains("Opacity", StringComparison.Ordinal)));
    }
}
