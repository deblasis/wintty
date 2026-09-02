using System;
using System.Collections.Generic;
using System.ComponentModel;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// A group has no "no color" state, and its paint sites index the palette
/// with no guard. These pin that invariant at the model, because guarding
/// writers is what let None through in the first place.
///
/// There are two writers, not three: <c>PaneActionRouter.RequestColorGroup</c>
/// (which both group pickers funnel through) and <c>TabManager.RestoreGroup</c>.
/// The pickers are two CONSTRUCTION sites sharing one writer, and it was the
/// shared writer that got overlooked.
///
/// The invariant is "the colour has a palette entry", which is stronger than
/// "the colour is not None" and is the one the paint sites actually need.
/// </summary>
public class TabGroupColorTests
{
    [Fact]
    public void Group_color_defaults_to_a_paintable_swatch()
    {
        var group = new TabGroup();

        Assert.NotEqual(TabColor.None, group.Color);
        Assert.Equal(TabColorPalette.DefaultGroupColor, group.Color);
        Assert.True(TabColorPalette.Colors.ContainsKey(group.Color));
    }

    [Fact]
    public void Setting_group_color_to_None_falls_back_instead_of_storing_it()
    {
        var group = new TabGroup { Color = TabColor.Red };

        group.Color = TabColor.None;

        Assert.Equal(TabColorPalette.DefaultGroupColor, group.Color);
    }

    [Fact]
    public void A_None_write_that_changes_nothing_raises_nothing()
    {
        // The group already holds the default, so coercing None to the
        // default is not a change -- the INPC contract is about the value
        // that lands, not the value that was offered. Without this the ink
        // pass would repaint on every no-op write.
        var group = new TabGroup();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)group).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        group.Color = TabColor.None;

        Assert.Empty(raised);
    }

    [Fact]
    public void A_None_write_that_does_change_the_value_raises_once()
    {
        var group = new TabGroup { Color = TabColor.Teal };
        var raised = new List<string?>();
        ((INotifyPropertyChanged)group).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        group.Color = TabColor.None;

        Assert.Single(raised);
        Assert.Equal(nameof(TabGroup.Color), raised[0]);
    }

    [Fact]
    public void A_restored_group_saved_as_None_comes_back_paintable()
    {
        // RestoreGroup documents that it repairs rather than crashes on
        // corrupt saved state. A colour is saved state like any other: a
        // session written before the picker stopped offering None replays
        // None here on every launch.
        var mgr = new TabManager((_) => new FakePaneHost());
        var group = mgr.RestoreGroup(
            Guid.NewGuid(), "restored", TabColor.None,
            collapsed: false, new[] { mgr.ActiveTab });

        Assert.Equal(TabColorPalette.DefaultGroupColor, group.Color);
    }

    [Fact]
    public void A_group_colour_with_no_palette_entry_falls_back_too()
    {
        // Not hypothetical. GroupSession.Color is a plain TabColor and the
        // session context defines no string converter, so it round-trips as a
        // NUMBER -- and System.Text.Json does not check that a numeric enum is
        // a defined member. A hand-edited session, or one written by a build
        // with an extra swatch, replays an undefined value through
        // RestoreGroup on every launch. Guarding None alone let that reach the
        // same mid-paint crash under a different integer.
        var mgr = new TabManager((_) => new FakePaneHost());
        var group = mgr.RestoreGroup(
            Guid.NewGuid(), "from a newer build", (TabColor)42,
            collapsed: false, new[] { mgr.ActiveTab });

        Assert.Equal(TabColorPalette.DefaultGroupColor, group.Color);
        TabColorPalette.Background(group.Color, selected: false);
    }

    [Fact]
    public void The_palette_names_the_value_it_refused_not_always_None()
    {
        // The message exists so a crash names the argument and the rule. It
        // said "TabColor.None has no preset" for every miss, which is a lie
        // for the out-of-range case -- the one most in need of reading
        // literally, since it arrives from a file rather than from code.
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(
            () => TabColorPalette.Border((TabColor)42));

        Assert.Equal((TabColor)42, thrown.ActualValue);
        Assert.Contains("not a declared TabColor", thrown.Message);
        // The None branch's wording must not reach a value that is not None.
        Assert.DoesNotContain("no tint", thrown.Message);
    }

    [Fact]
    public void Every_group_paint_helper_takes_every_colour_a_group_can_hold()
    {
        // The end-to-end claim: whatever anyone writes, the helpers the
        // chip swatch, chip ink, run label, vertical header and switcher
        // card call cannot throw.
        // Declared members plus values that are not members at all, since the
        // restore path can produce either.
        var offeredValues = Enum.GetValues<TabColor>()
            .Concat(new[] { (TabColor)42, (TabColor)(-1), (TabColor)int.MaxValue });

        foreach (var offered in offeredValues)
        {
            var group = new TabGroup { Color = offered };

            TabColorPalette.Background(group.Color, selected: false);
            TabColorPalette.Background(group.Color, selected: true);
            TabColorPalette.Border(group.Color);
            TabColorPalette.EffectiveBackgroundRgb(group.Color, selected: false, 0x1E1E1E);
            TabColorPalette.ForegroundRgb(group.Color, selected: false, 0x1E1E1E);
        }
    }

    [Fact]
    public void EnsureGroupColor_replaces_None_and_passes_everything_else_through()
    {
        // Not a [Theory]: TabColor is internal, so it cannot appear in a
        // public test signature (CS0051).
        Assert.Equal(TabColorPalette.DefaultGroupColor,
            TabColorPalette.EnsureGroupColor(TabColor.None));

        foreach (var color in Enum.GetValues<TabColor>())
        {
            if (color == TabColor.None) continue;
            Assert.Equal(color, TabColorPalette.EnsureGroupColor(color));
        }
    }

    [Fact]
    public void The_palette_refuses_None_by_name_rather_than_by_dictionary_miss()
    {
        // None is still invalid for the tab helpers -- it means "no tint",
        // and each tab paint site picks a different brush for it. What
        // changed is that the refusal says so: the old failure was a bare
        // KeyNotFoundException raised inside a paint pass, naming neither
        // the argument nor the rule.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TabColorPalette.Background(TabColor.None, selected: false));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TabColorPalette.Border(TabColor.None));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TabColorPalette.EffectiveBackgroundRgb(TabColor.None, selected: false, 0x1E1E1E));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TabColorPalette.ForegroundRgb(TabColor.None, selected: false, 0x1E1E1E));
        // The field helpers reach the palette through the same private
        // composite the tab tint does, and they are listed here for a reason
        // that is not symmetry. That composite was introduced in the same
        // change that added them, and its first version indexed the
        // dictionary -- which compiles, and passes every value test in this
        // file, because a declared colour never misses. Nothing but this
        // named the difference, so the field path would have carried the bare
        // dictionary miss back onto the paint path for the whole family while
        // the suite stayed green.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TabColorPalette.FieldBackgroundRgb(TabColor.None, 0x1E1E1E));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TabColorPalette.FieldForegroundRgb(TabColor.None, 0x1E1E1E));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TabColorPalette.FieldBackgroundRgb((TabColor)42, 0x1E1E1E));

        var thrown = Assert.Throws<ArgumentOutOfRangeException>(
            () => TabColorPalette.Border(TabColor.None));
        Assert.Contains("no tint", thrown.Message);
    }

    [Fact]
    public void A_tab_still_holds_None_because_that_is_how_a_tint_is_cleared()
    {
        // The coercion is the GROUP's rule. If it ever leaks onto TabModel
        // the tab palette loses its "no colour" state and every tab paints
        // tinted, so this is the guard against fixing the wrong type.
        var mgr = new TabManager((_) => new FakePaneHost());
        var tab = mgr.ActiveTab;

        tab.Color = TabColor.Red;
        tab.Color = TabColor.None;

        Assert.Equal(TabColor.None, tab.Color);
    }
}
