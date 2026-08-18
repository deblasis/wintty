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
}
