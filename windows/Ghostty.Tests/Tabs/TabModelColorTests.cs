using System;
using System.Collections.Generic;
using System.ComponentModel;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class TabModelColorTests
{
    [Fact]
    public void Default_color_is_None()
    {
        var mgr = new TabManager((_) => new FakePaneHost());
        Assert.Equal(TabColor.None, mgr.ActiveTab.Color);
    }

    [Fact]
    public void Setting_color_raises_PropertyChanged_once()
    {
        var mgr = new TabManager((_) => new FakePaneHost());
        var tab = mgr.ActiveTab;

        var raised = new List<string?>();
        ((INotifyPropertyChanged)tab).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.Color = TabColor.Blue;

        Assert.Single(raised);
        Assert.Equal(nameof(TabModel.Color), raised[0]);
    }

    [Fact]
    public void Setting_color_to_same_value_does_not_raise()
    {
        var mgr = new TabManager((_) => new FakePaneHost());
        var tab = mgr.ActiveTab;
        tab.Color = TabColor.Red;

        var raised = new List<string?>();
        ((INotifyPropertyChanged)tab).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.Color = TabColor.Red;

        Assert.Empty(raised);
    }

    [Fact]
    public void Palette_contains_every_non_None_enum_value()
    {
        foreach (TabColor color in System.Enum.GetValues<TabColor>())
        {
            if (color == TabColor.None) continue;
            Assert.True(
                TabColorPalette.Colors.ContainsKey(color),
                $"TabColorPalette.Colors missing entry for {color}");
        }
    }

    [Fact]
    public void Palette_rows_are_five_by_two_matching_macOS()
    {
        Assert.Equal(2, TabColorPalette.PaletteRows.Length);
        Assert.All(TabColorPalette.PaletteRows, row => Assert.Equal(5, row.Length));
        Assert.Equal(TabColor.None, TabColorPalette.PaletteRows[0][0]);
    }

    [Fact]
    public void LocalizedName_covers_all_enum_values()
    {
        // The switch ends in a `_ => "None"` fallback, so a newly added
        // TabColor silently reads as "None" in the swatch tooltip unless
        // this fails first.
        foreach (TabColor color in Enum.GetValues<TabColor>())
        {
            var name = TabColorPalette.LocalizedName(color);
            Assert.False(string.IsNullOrWhiteSpace(name));
            if (color != TabColor.None)
                Assert.NotEqual("None", name);
        }
    }

    [Fact]
    public void Background_alpha_matches_selected_and_unselected()
    {
        var selected = TabColorPalette.Background(TabColor.Blue, selected: true);
        var unselected = TabColorPalette.Background(TabColor.Blue, selected: false);

        // Literal values, not the constants themselves: restating the
        // symbols on both sides passes for any value, including 0.
        Assert.Equal(255, selected.A);
        Assert.Equal(89, unselected.A);
        Assert.Equal(0x00, selected.R);
        Assert.Equal(0x7A, selected.G);
        Assert.Equal(0xFF, selected.B);
        Assert.Equal(selected.R, unselected.R);
    }

    [Fact]
    public void Foreground_on_yellow_selected_is_dark()
    {
        // Yellow preset is light -- title/icon should flip to black.
        var fg = TabColorPalette.ForegroundRgb(
            TabColor.Yellow, selected: true, stripBackdropRgb: 0x1E1E1E);
        Assert.Equal(0x000000u, fg);
    }

    // 89/255 of the preset composited over the strip fill. Exact values,
    // because "not equal to either input" passes for a transposed or
    // inverted blend just as happily as for a correct one.
    [Fact]
    public void EffectiveBackground_blends_inactive_red_over_strip()
    {
        Assert.Equal(
            0x6C2824u,
            TabColorPalette.EffectiveBackgroundRgb(TabColor.Red, selected: false, 0x1E1E1E));
    }

    [Fact]
    public void EffectiveBackground_blends_inactive_teal_over_strip()
    {
        Assert.Equal(
            0x245058u,
            TabColorPalette.EffectiveBackgroundRgb(TabColor.Teal, selected: false, 0x1E1E1E));
    }

    [Fact]
    public void EffectiveBackground_selected_ignores_strip_and_returns_preset()
    {
        Assert.Equal(
            0xFF3B30u,
            TabColorPalette.EffectiveBackgroundRgb(TabColor.Red, selected: true, 0x1E1E1E));
        Assert.Equal(
            0xFF3B30u,
            TabColorPalette.EffectiveBackgroundRgb(TabColor.Red, selected: true, 0xFFFFFF));
    }

    [Fact]
    public void EffectiveBackground_over_identical_backdrop_is_a_no_op()
    {
        // Blending a color over itself must return it unchanged at any
        // alpha; a rounding or channel-order slip shows up here.
        Assert.Equal(
            0xFF3B30u,
            TabColorPalette.EffectiveBackgroundRgb(TabColor.Red, selected: false, 0xFF3B30));
    }

    [Fact]
    public void Foreground_on_dark_inactive_tint_is_light()
    {
        // Red at 89/255 over near-black stays dark, so the label must be
        // light. This is the branch the selected:true cases never reach.
        var fg = TabColorPalette.ForegroundRgb(
            TabColor.Red, selected: false, stripBackdropRgb: 0x1E1E1E);
        Assert.Equal(0xFFFFFFu, fg);
    }

    // The group FIELD's wash. Exact values for the same reason the tab
    // tint's are exact: a transposed or inverted blend is still "not equal
    // to either input".
    [Fact]
    public void Field_wash_blends_the_preset_lightly_over_the_ground()
    {
        // Red (FF3B30) at 46/255 over 0x1E1E1E, channel by channel:
        //   R 255*.1804 + 30*.8196 = 70.6 -> 0x46
        //   G  59*.1804 + 30*.8196 = 35.2 -> 0x23
        //   B  48*.1804 + 30*.8196 = 33.2 -> 0x21
        Assert.Equal(0x462321u, TabColorPalette.FieldBackgroundRgb(TabColor.Red, 0x1E1E1E));
    }

    [Fact]
    public void Field_wash_is_lighter_than_the_tint_a_tab_takes()
    {
        // A field is a GROUND: it sits behind whole tiles, several of which
        // carry preset tints of their own. At the tab alpha the run turns
        // into one block of colour with the tiles lost inside it, so the
        // field must stay nearer the ground than an inactive tab does.
        Assert.True(TabColorPalette.FieldWashAlpha < TabColorPalette.UnselectedBackgroundAlpha);

        const uint Ground = 0x1E1E1E;
        foreach (var color in new[]
                 { TabColor.Blue, TabColor.Red, TabColor.Green, TabColor.Yellow })
        {
            var field = TabColorPalette.FieldBackgroundRgb(color, Ground);
            var tint = TabColorPalette.EffectiveBackgroundRgb(color, selected: false, Ground);
            Assert.NotEqual(tint, field);
            Assert.True(Distance(field, Ground) < Distance(tint, Ground),
                $"{color}: the field wash must sit nearer the ground than the tab tint");
        }
    }

    [Fact]
    public void Field_wash_over_its_own_colour_returns_that_colour()
    {
        // Blending a colour over itself must give it back; a channel-order
        // slip shows up here as a channel that moved by tens.
        //
        // One level of slack per channel, and only one: the blend truncates
        // rather than rounds -- the tab tint's pinned values depend on that
        // -- and at the field's alpha 59*a + 59*(1-a) lands a hair under 59
        // in binary floating point. Widening this to a tolerance would let a
        // real slip through; tightening it would mean rounding, which moves
        // numbers this file already pins.
        var washed = TabColorPalette.FieldBackgroundRgb(TabColor.Red, 0xFF3B30);
        Assert.InRange((washed >> 16) & 0xFF, 0xFEu, 0xFFu);
        Assert.InRange((washed >> 8) & 0xFF, 0x3Au, 0x3Bu);
        Assert.InRange(washed & 0xFF, 0x2Fu, 0x30u);
    }

    [Fact]
    public void Field_wash_follows_the_ground_it_is_composited_against()
    {
        // The whole reason the popup composites here instead of handing
        // XAML a translucent brush: one wash per ground, committed, rather
        // than whatever Mica makes of it over the user's wallpaper.
        Assert.NotEqual(
            TabColorPalette.FieldBackgroundRgb(TabColor.Blue, 0x1E1E1E),
            TabColorPalette.FieldBackgroundRgb(TabColor.Blue, 0xF0F0F0));
    }

    [Fact]
    public void Field_ink_is_readable_on_the_wash_in_both_polarities()
    {
        Assert.Equal(0xFFFFFFu, TabColorPalette.FieldForegroundRgb(TabColor.Blue, 0x1E1E1E));
        Assert.Equal(0x000000u, TabColorPalette.FieldForegroundRgb(TabColor.Yellow, 0xF0F0F0));
    }

    private static int Distance(uint a, uint b)
        => Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF))
         + Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF))
         + Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));
}
