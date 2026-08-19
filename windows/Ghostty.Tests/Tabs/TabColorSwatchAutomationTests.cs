using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// Tab color swatches were decorated Borders carrying nothing but an
/// automation name. A Border exposes no pattern and takes no focus, so a
/// client could find "Blue" and then had nothing to invoke, the keyboard
/// could not reach the palette at all, and the applied color was carried
/// by a drawn ring that no client could read.
///
/// The test project cannot host XAML, so these read the shipped markup and
/// source instead. They are aimed at the decisions that are silent when
/// broken, not at how any of it is spelled.
/// </summary>
public class TabColorSwatchAutomationTests
{
    [Fact]
    public void Swatches_TakeAutomationNameFromLocalizedName()
    {
        var source = ReadEmbedded("TabColorPalettePicker.xaml.cs");
        Assert.Contains("AutomationProperties.SetName(", source);
        Assert.Contains("TabColorPalette.LocalizedName(color)", source);
    }

    /// <summary>
    /// Without a name of its own the palette reaches a client as an
    /// anonymous list. The heading is out of the automation tree, so
    /// LabeledBy would point at nothing and the name has to be set.
    /// </summary>
    [Fact]
    public void Palette_IsNamedAfterItsHeading()
    {
        var source = ReadEmbedded("TabColorPalettePicker.xaml.cs");
        Assert.Contains("AutomationProperties.SetName(Swatches, PaletteLabel.Text)", source);

        var label = Element("TextBlock");
        Assert.Equal("Raw", (string?)label.Attribute("AutomationProperties.AccessibilityView"));
    }

    /// <summary>
    /// The applied color has to be state a client can query, not just a
    /// ring. SelectionItem is what carries it.
    /// </summary>
    [Fact]
    public void AppliedColor_IsSelected_NotOnlyRinged()
    {
        var source = ReadEmbedded("TabColorPalettePicker.xaml.cs");
        Assert.Contains(".SelectedItem = swatch", source);
        Assert.Equal("Single", (string?)Element("GridView").Attribute("SelectionMode"));
    }

    /// <summary>
    /// Every activation route the control has - click, Space, Enter and a
    /// client's Invoke - arrives as ItemClick, and IsItemClickEnabled is
    /// also what puts Invoke on the items next to SelectionItem. Verified
    /// by removing it: the pattern disappears from every swatch.
    /// </summary>
    [Fact]
    public void Activation_ArrivesAsItemClick()
    {
        var grid = Element("GridView");
        Assert.Equal("True", (string?)grid.Attribute("IsItemClickEnabled"));
        Assert.Equal("OnSwatchClick", (string?)grid.Attribute("ItemClick"));

        var source = ReadEmbedded("TabColorPalettePicker.xaml.cs");
        Assert.Contains("ItemClickEventArgs", source);
    }

    /// <summary>
    /// An item added as its own container suppresses ItemClick, which
    /// silently costs the control every one of its activation routes. The
    /// swatch has to go in as plain content and let the GridView generate
    /// the container.
    /// </summary>
    [Fact]
    public void Swatches_AreContent_NotTheirOwnContainers()
    {
        var source = StripComments(ReadEmbedded("TabColorPalettePicker.xaml.cs"));
        Assert.Contains("private FrameworkElement BuildSwatch(TabColor color)", source);
        Assert.DoesNotContain("GridViewItem", source);
    }

    /// <summary>
    /// The stock container template paints a rounded fill and a check mark
    /// over the swatch. Losing the style is a visual regression nothing
    /// else here would catch.
    /// </summary>
    [Fact]
    public void Containers_KeepTheirOwnTemplate()
    {
        Assert.Equal(
            "{StaticResource TabColorSwatchStyle}",
            (string?)Element("GridView").Attribute("ItemContainerStyle"));

        var style = Doc().Descendants()
            .Single(e => e.Name.LocalName == "Style"
                && (string?)e.Attribute(X + "Key") == "TabColorSwatchStyle");
        Assert.Equal("GridViewItem", (string?)style.Attribute("TargetType"));
    }

    /// <summary>
    /// Flattening PaletteRows only stays faithful to the macOS layout if
    /// the grid wraps where the rows ended AND fills row-major. Vertical
    /// orientation would reinterpret the same wrap count as five rows.
    /// </summary>
    [Fact]
    public void WrapPoint_MatchesPaletteRowWidth()
    {
        var width = TabColorPalette.PaletteRows[0].Length;
        Assert.All(TabColorPalette.PaletteRows, row => Assert.Equal(width, row.Length));

        var panel = Element("ItemsWrapGrid");
        Assert.Equal("Horizontal", (string?)panel.Attribute("Orientation"));
        Assert.Equal(width, int.Parse(
            (string)panel.Attribute("MaximumRowsOrColumns")!, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// ListViewBase drags the selection along with the caret by default,
    /// which would leave SelectionItem reporting where the user is looking
    /// rather than which color the tab has.
    /// </summary>
    [Fact]
    public void Selection_TracksTheAppliedColor_NotTheCaret()
    {
        Assert.Equal("False", (string?)Element("GridView").Attribute("SingleSelectionFollowsFocus"));
    }

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Doc() => XDocument.Parse(ReadEmbedded("TabColorPalettePicker.xaml"));

    private static XElement Element(string localName) =>
        Doc().Descendants().Single(e => e.Name.LocalName == localName);

    /// <summary>
    /// So an assertion about the code is not satisfied, or defeated, by
    /// prose that happens to name the same type.
    /// </summary>
    private static string StripComments(string source) =>
        string.Join('\n', source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>
    /// The .xaml half is embedded by this project's csproj; the .xaml.cs
    /// half rides the MarshalCompliance source glob. Reading either by
    /// suffix works only because ".xaml.cs" does not end with ".xaml".
    /// </summary>
    private static string ReadEmbedded(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
