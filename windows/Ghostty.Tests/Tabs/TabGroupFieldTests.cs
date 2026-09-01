using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Tabs;
using Ghostty.Core.Windows;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The group field's arithmetic: where a field starts and stops, and what
/// colour it and its two terminals come out.
///
/// Executable rather than syntactic. Every rule here runs the real helper
/// over real inputs, so a change to the constants is answered by the
/// numbers rather than by whether the constant is still spelled the same
/// way. The strips' end of it -- that they call these and place elements
/// from the answer -- is the wiring guard's job, and neither test can see
/// what the field actually looks like on a live window.
/// </summary>
public sealed class TabGroupFieldTests
{
    // The grey ramp, which is the only ground corpus that means anything
    // here: Mica leaves the strip whatever the desktop is, and a wash
    // tuned on the two built-in themes is a wash tuned on two points.
    private static IEnumerable<uint> GreyRamp()
    {
        for (uint v = 0; v <= 255; v++) yield return (v << 16) | (v << 8) | v;
    }

    // The two built-in strip shades named in #882, plus the poles.
    private static readonly uint[] NamedGrounds =
    {
        0x17181Au, // wintty-dark's strip
        0xF4F5F5u, // wintty-light's strip
        0x0C0C0Cu, // the strip's own default backdrop guess
        0x000000u,
        0xFFFFFFu,
    };

    private static double LStar(uint rgb)
    {
        var luminance = ThemeResolution.ContrastRatio(rgb, 0x000000u) * 0.05 - 0.05;
        return luminance > 0.008856
            ? 116.0 * Math.Cbrt(luminance) - 16.0
            : 903.3 * luminance;
    }

    /// <summary>
    /// The wash always moves the ground. A field that composites back to
    /// the strip it sits on is the "no field" the design decision was made
    /// against, and it is what a wash alpha of 0 -- or a pole with no
    /// headroom -- silently produces.
    /// </summary>
    [Fact]
    public void TheWash_MovesEveryGround()
    {
        foreach (var ground in GreyRamp().Concat(NamedGrounds))
        {
            Assert.True(
                TabGroupField.FieldGroundRgb(ground) != ground,
                $"the field composited back to the strip on {ground:X6}");
        }
    }

    /// <summary>
    /// And moves it far enough to see, everywhere.
    ///
    /// In L*, not in channel counts: sRGB is gamma encoded, so a fixed
    /// alpha buys markedly more visible separation at the black end than
    /// at the white end, and the mid grounds a translucent frame makes of
    /// the chrome are the worst case for both poles at once. Three L* is
    /// the floor a wash has to clear to be a field rather than a rounding
    /// error.
    /// </summary>
    [Fact]
    public void TheWash_SeparatesFromEveryGround()
    {
        foreach (var ground in GreyRamp().Concat(NamedGrounds))
        {
            var delta = Math.Abs(
                LStar(TabGroupField.FieldGroundRgb(ground)) - LStar(ground));
            Assert.True(
                delta >= 3.0,
                $"the field is {delta:F2} L* from the strip on {ground:X6}");
        }
    }

    /// <summary>
    /// And not so far that it stops being a wash.
    ///
    /// The upper bound is the half of this that a bigger alpha would quietly
    /// break: past about a dozen L* the field reads as a surface of its own
    /// and competes with the selected row's fill, which is the one element
    /// in the strip that is allowed to be a fill. A wash that loud is the
    /// colour the decision rejected, arrived at from the other side.
    /// </summary>
    [Fact]
    public void TheWash_StaysAWash()
    {
        foreach (var ground in GreyRamp().Concat(NamedGrounds))
        {
            var delta = Math.Abs(
                LStar(TabGroupField.FieldGroundRgb(ground)) - LStar(ground));
            Assert.True(
                delta <= 12.0,
                $"the field is {delta:F2} L* from the strip on {ground:X6}");
        }
    }

    /// <summary>
    /// The pole is picked, never assumed. On a near-black strip only white
    /// has headroom and on a near-white one only black does; a field that
    /// always washed one way would be invisible at one end of the ramp.
    /// </summary>
    [Theory]
    [InlineData(0x000000u, 0xFFFFFFu)]
    [InlineData(0x17181Au, 0xFFFFFFu)]
    [InlineData(0xFFFFFFu, 0x000000u)]
    [InlineData(0xF4F5F5u, 0x000000u)]
    public void TheWashPole_HasHeadroom(uint ground, uint expected)
        => Assert.Equal(expected, TabGroupField.WashInkRgb(ground));

    /// <summary>
    /// Every preset, on every ground, comes out of the terminal resolution
    /// clearing the non-text floor against the FIELD -- which is the
    /// surface the bar is drawn on, and is not the strip once the wash has
    /// landed.
    ///
    /// This is the rule the shipped header swatch fails at 1.57:1, and it
    /// is the whole reason the terminals go through EnsureVisible rather
    /// than taking the preset as given.
    /// </summary>
    [Fact]
    public void EveryTerminal_ClearsTheNonTextFloorAgainstItsField()
    {
        foreach (var color in Enum.GetValues<TabColor>())
        {
            if (color == TabColor.None) continue;

            foreach (var ground in GreyRamp().Concat(NamedGrounds))
            {
                var field = TabGroupField.FieldGroundRgb(ground);
                var bar = TabGroupField.TerminalRgb(ground, color);
                var ratio = ThemeResolution.ContrastRatio(field, bar);
                Assert.True(
                    ratio >= TabGroupField.TerminalMinContrast,
                    $"{color} terminal is {ratio:F2}:1 on ground {ground:X6}");
            }
        }
    }

    /// <summary>
    /// A preset that already clears the floor is left alone. EnsureVisible
    /// keeps identity where it can, and a rule that shifted every preset on
    /// every ground would hand back a palette nobody chose.
    /// </summary>
    [Fact]
    public void ATerminalThatAlreadyReads_IsNotShifted()
    {
        // Blue on the dark strip: comfortably past the floor already.
        var preset = TabColorPalette.Border(TabColor.Blue);
        var groupRgb = ((uint)preset.R << 16) | ((uint)preset.G << 8) | preset.B;
        Assert.Equal(groupRgb, TabGroupField.TerminalRgb(0x17181Au, TabColor.Blue));
    }

    // ---- run geometry -------------------------------------------------

    private static TabManager NewManager() => new(_ => new FakePaneHost());

    private static List<TabModel> Seed(TabManager manager, int total)
    {
        while (manager.Tabs.Count < total) manager.NewTab();
        return manager.Tabs.ToList();
    }

    private static TabGroup Group(TabManager manager, params TabModel[] members)
    {
        var group = new TabGroup();
        manager.GroupTabs(members, group);
        return group;
    }

    /// <summary>
    /// The vertical reading: a field spans its header AND its members, so
    /// the header row is inside the container it caps rather than a row
    /// sitting above one.
    /// </summary>
    [Fact]
    public void Vertical_TheFieldStartsAtTheHeaderAndEndsAtTheLastMember()
    {
        var manager = NewManager();
        var tabs = Seed(manager, 4);
        Group(manager, tabs[1], tabs[2]);

        var rows = TabStripProjection.GroupedRows(manager);
        var run = Assert.Single(TabGroupField.Runs(TabGroupField.SlotGroups(rows)));

        // slot 0 = tabs[0], 1 = header, 2 = tabs[1], 3 = tabs[2], 4 = tabs[3]
        Assert.Equal(1, run.First);
        Assert.Equal(3, run.Last);
        Assert.Equal(3, run.SlotCount);
        Assert.IsType<TabStripProjection.ProjectedRow.Header>(rows[run.First]);
    }

    /// <summary>
    /// The horizontal reading of the same state: the same one group gets
    /// the same one field, over the members alone -- that strip draws no
    /// header row, so its cap is the run's own leading edge.
    /// </summary>
    [Fact]
    public void Horizontal_TheFieldSpansTheRunsMembers()
    {
        var manager = NewManager();
        var tabs = Seed(manager, 4);
        Group(manager, tabs[1], tabs[2]);

        var rows = TabStripProjection.HorizontalRows(manager);
        var run = Assert.Single(TabGroupField.Runs(TabGroupField.SlotGroups(rows)));

        Assert.Equal(1, run.First);
        Assert.Equal(2, run.Last);
        Assert.Equal(2, run.SlotCount);
    }

    /// <summary>
    /// Layout parity, stated as the thing that can actually break: for one
    /// manager state, both strips draw a field for the same groups. The
    /// spans differ on purpose (vertical counts a header slot the
    /// horizontal strip does not render); WHICH groups get a container
    /// must not.
    /// </summary>
    [Fact]
    public void BothLayouts_DrawAFieldForTheSameGroups()
    {
        var manager = NewManager();
        var tabs = Seed(manager, 7);
        Group(manager, tabs[1], tabs[2]);
        Group(manager, tabs[4], tabs[5]);
        manager.SetPinned(tabs[0], true);
        manager.Activate(tabs[6]);

        foreach (var collapseSecond in new[] { false, true })
        {
            if (collapseSecond)
                manager.CollapseGroup(manager.Groups[1], true);

            var vertical = TabGroupField
                .Runs(TabGroupField.SlotGroups(TabStripProjection.GroupedRows(manager)))
                .Select(r => r.Group);
            var horizontal = TabGroupField
                .Runs(TabGroupField.SlotGroups(TabStripProjection.HorizontalRows(manager)))
                .Select(r => r.Group);

            Assert.Equal(manager.Groups.ToList(), vertical.ToList());
            Assert.Equal(manager.Groups.ToList(), horizontal.ToList());
        }
    }

    /// <summary>
    /// A folded group keeps its field. Vertically it is the header alone,
    /// horizontally it is the chip alone -- one slot either way, which is
    /// the shape that makes a chip self-evidently a container rather than
    /// a tab with a dot on it.
    /// </summary>
    [Fact]
    public void ACollapsedRun_IsAOneSlotFieldInBothLayouts()
    {
        var manager = NewManager();
        var tabs = Seed(manager, 3);
        Group(manager, tabs[0], tabs[1]);
        manager.Activate(tabs[2]);
        manager.CollapseGroup(manager.Groups[0], true);

        var vertical = Assert.Single(TabGroupField.Runs(
            TabGroupField.SlotGroups(TabStripProjection.GroupedRows(manager))));
        var horizontal = Assert.Single(TabGroupField.Runs(
            TabGroupField.SlotGroups(TabStripProjection.HorizontalRows(manager))));

        Assert.Equal(1, vertical.SlotCount);
        Assert.Equal(1, horizontal.SlotCount);
    }

    /// <summary>
    /// A collapsed run that holds the active tab keeps that member visible
    /// (the Edge-135 rule), and the field has to grow to cover it: the
    /// member is inside the group, so a container that stopped at the
    /// header would leave a member outside its own field.
    /// </summary>
    [Fact]
    public void ACollapsedRunHoldingTheActiveTab_KeepsThatMemberInsideItsField()
    {
        var manager = NewManager();
        var tabs = Seed(manager, 3);
        Group(manager, tabs[0], tabs[1]);
        manager.Activate(tabs[1]);
        manager.CollapseGroup(manager.Groups[0], true);

        var vertical = Assert.Single(TabGroupField.Runs(
            TabGroupField.SlotGroups(TabStripProjection.GroupedRows(manager))));
        Assert.Equal(2, vertical.SlotCount);

        // Horizontally the chip is suppressed and the member renders as
        // itself, so the field is that member's own slot.
        var horizontal = Assert.Single(TabGroupField.Runs(
            TabGroupField.SlotGroups(TabStripProjection.HorizontalRows(manager))));
        Assert.Equal(1, horizontal.SlotCount);
    }

    /// <summary>
    /// Two adjacent runs are two fields, not one. Neighbouring groups have
    /// no gap between them in the projection, so a walk that merged on
    /// "the previous slot was grouped" would draw a single container over
    /// both and lose the boundary the field exists to show.
    /// </summary>
    [Fact]
    public void TwoAdjacentRuns_AreTwoFields()
    {
        var manager = NewManager();
        var tabs = Seed(manager, 4);
        Group(manager, tabs[0], tabs[1]);
        Group(manager, tabs[2], tabs[3]);

        var runs = TabGroupField.Runs(
            TabGroupField.SlotGroups(TabStripProjection.GroupedRows(manager)));
        Assert.Equal(2, runs.Count);
        Assert.NotSame(runs[0].Group, runs[1].Group);
        Assert.True(runs[0].Last < runs[1].First);
    }

    /// <summary>
    /// No groups, no fields. The strip must not pay for a container it has
    /// no group to draw.
    /// </summary>
    [Fact]
    public void AStripWithNoGroups_DrawsNoField()
    {
        var manager = NewManager();
        Seed(manager, 3);
        Assert.Empty(TabGroupField.Runs(
            TabGroupField.SlotGroups(TabStripProjection.GroupedRows(manager))));
        Assert.Empty(TabGroupField.Runs(
            TabGroupField.SlotGroups(TabStripProjection.HorizontalRows(manager))));
    }

    /// <summary>
    /// A group whose slots are not contiguous comes back as two fields
    /// rather than one container swallowing the stranger between them. The
    /// manager's Normalize makes this shape unreachable; the point is that
    /// the walk cannot be the thing that lies about membership if it ever
    /// does arrive.
    /// </summary>
    [Fact]
    public void ASplitRun_DrawsOneFieldPerStretch()
    {
        var group = new TabGroup();
        var slots = new TabGroup?[] { group, null, group };
        var runs = TabGroupField.Runs(slots);

        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal(1, r.SlotCount));
    }

    /// <summary>
    /// The field's clocks are the strip's clocks. A field animating on
    /// numbers of its own separates from the rows it is drawn around,
    /// which looks worse than no field at all.
    /// </summary>
    /// <summary>
    /// The wash is painted as ink at an alpha, not as the composite it
    /// lands as. Handing a painter the composite is how an opaque patch
    /// ends up over Mica, and it is a one-identifier mistake: both are
    /// uints off this same class.
    /// </summary>
    [Fact]
    public void TheWashIsHandedToThePainter_WithItsAlphaStillOnIt()
    {
        foreach (var ground in new[] { 0x17181Au, 0xF4F5F5u, 0x808080u })
        {
            var argb = TabGroupField.WashArgb(ground);
            Assert.Equal(TabGroupField.WashAlpha, (byte)(argb >> 24));
            Assert.Equal(TabGroupField.WashInkRgb(ground), argb & 0x00FFFFFFu);
        }
    }

    [Fact]
    public void TheFieldMotion_BorrowsTheStripsOwnTokens()
    {
        Assert.Equal(TabStripMotion.GapGlideMs, TabGroupField.GlideMs);
        Assert.Equal(TabStripMotion.FadeMs, TabGroupField.FadeMs);
    }
}
