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
        foreach (TabColor color in System.Enum.GetValues<TabColor>())
        {
            var name = TabColorPalette.LocalizedName(color);
            Assert.False(string.IsNullOrWhiteSpace(name));
        }
    }
}
