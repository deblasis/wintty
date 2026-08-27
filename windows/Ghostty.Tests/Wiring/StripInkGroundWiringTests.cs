using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Unselected tab titles are drawn at 70% over the strip, so the pole they
/// are drawn in has to be chosen against whatever the strip actually is. That
/// used to be the palette's tab-bar shade in every case, because the strip
/// was always painted with it. It is not any more: a frosted or crystal frame
/// leaves the strip bare so the backdrop shows through, and the shade the
/// palette names is then a colour nothing renders.
///
/// The arithmetic of the choice lives in Ghostty.Core and is tested there.
/// These are the wiring: which surface the choice is made against, that both
/// layouts are told the same one, and that every input which moves it
/// re-asks. Whether the text is then legible is only observable on a live
/// window, which is how this shipped in the first place.
///
/// Written against the syntax rather than the text, so the mutations that
/// matter fail: swapping the two poles, scoring against the wrong field, and
/// dropping the recalibration from one of the pushes all keep every literal
/// in place.
/// </summary>
public sealed class StripInkGroundWiringTests
{
    private const string VerticalStrip = "Tabs.VerticalTabStrip.xaml.cs";
    private const string HorizontalStrip = "Tabs.TabHost.xaml.cs";

    /// <summary>
    /// Each strip and the number of muted-ink sites it has: the vertical one
    /// picks a pole on the palette path and again in the fallback, the
    /// horizontal one only on the palette path.
    ///
    /// The count is here rather than left to "at least one" because every
    /// rule below walks the sites it finds. A rule that walks an empty list
    /// passes for the same reason it would pass on the branch that has the
    /// bug, which pins nothing at all -- and the query going quiet is exactly
    /// what a rename of the helper does.
    /// </summary>
    public static TheoryData<string, int> BothStrips => new()
    {
        { VerticalStrip, 2 },
        { HorizontalStrip, 1 },
    };

    /// <summary>
    /// Every place a pole is picked for the muted ink, found by the call that
    /// picks it rather than by the method it sits in: a rule naming methods
    /// would quietly stop covering a third site the day one appears.
    /// </summary>
    private static List<ConditionalExpressionSyntax> InkPicks(string file, int expected)
    {
        var picks = ShellSource.Load(file).Root.DescendantNodes()
            .OfType<ConditionalExpressionSyntax>()
            .Where(c => c.Condition is InvocationExpressionSyntax call
                && call.CalleeText().EndsWith(
                    "PreferLightForegroundAtAlpha", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            picks.Count == expected,
            $"expected {expected} muted-ink site(s) in {file}, found {picks.Count}");
        return picks;
    }

    /// <summary>
    /// The pole is scored against the strip's own ground field, at the same
    /// alpha the ink is then painted with.
    ///
    /// Both arguments, because either one going stale reproduces the bug on
    /// its own: the wrong ground is what shipped, and scoring an opaque pole
    /// for ink that is 70% is the other half of the same mistake.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothStrips))]
    public void ThePole_IsScoredAgainstTheStripGroundAtTheInkAlpha(string file, int sites)
    {
        foreach (var pick in InkPicks(file, sites))
        {
            var call = (InvocationExpressionSyntax)pick.Condition;
            Assert.Equal("_stripBackdropPacked", call.Arg(0));
            Assert.Equal("InactiveInkAlpha", call.Arg(1));
        }
    }

    /// <summary>
    /// White on true and black on false, and both at the alpha that was
    /// scored. Swapping the branches inverts the fix into the bug on every
    /// ground at once, and it compiles.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothStrips))]
    public void ThePoles_AreNotSwapped_AndArePaintedAtTheAlphaTheyWereScoredAt(
        string file, int sites)
    {
        foreach (var pick in InkPicks(file, sites))
        {
            AssertPole(pick.WhenTrue, "0xFF");
            AssertPole(pick.WhenFalse, "0x00");
        }

        static void AssertPole(ExpressionSyntax branch, string channel)
        {
            var call = branch.AssertCallTo("FromArgb");
            Assert.Equal("InactiveInkAlpha", call.Arg(0));
            Assert.Equal(new[] { channel, channel, channel },
                new[] { call.Arg(1), call.Arg(2), call.Arg(3) });
        }
    }

    /// <summary>
    /// Nothing in either strip still picks the muted ink off the opaque
    /// luminance split. That helper answers for ink with no alpha in it, and
    /// it is one identifier away from the one these sites want.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothStrips))]
    public void NoStrip_StillPicksTheMutedInkAsIfItWereOpaque(string file, int sites)
    {
        InkPicks(file, sites);
        Assert.Empty(ShellSource.Load(file).Root
            .Calls("ThemeResolution.PreferLightForeground"));
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
    public void TheGround_IsTheFillWhenPainted_AndTheBackdropWhenBare(string file, int sites)
    {
        InkPicks(file, sites);

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
    /// the frame, so the pole picked while the palette lands is made against
    /// the previous frame's surface; only these two calls are late enough to
    /// be right, and losing either leaves the ink one config reload behind.
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
}
