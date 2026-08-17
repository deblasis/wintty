using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// Pins that SettingsCard's label column cannot collapse under a wide
/// Control slot. Profile rows put an icon picker plus two labeled
/// ToggleSwitches in Width=Auto; without a MinWidth on the star column
/// the header shrinks to ~1ch and wraps vertically (P/r/i/m/a/r/y).
/// </summary>
public class SettingsCardHeaderColumnTests
{
    private const string Resource = "Ghostty.Tests.Settings.Controls.SettingsCard.xaml";
    private const double MinHeaderWidth = 160;

    [Fact]
    public void HeaderColumn_HasMinWidthSoControlSlotCannotCrushIt()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(Resource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var doc = XDocument.Parse(reader.ReadToEnd());

        var defs = doc.Descendants().First(e => e.Name.LocalName == "Grid.ColumnDefinitions");
        var header = defs.Elements().First(e => e.Name.LocalName == "ColumnDefinition");
        var attr = header.Attribute("MinWidth");
        Assert.True(attr is not null, "Header ColumnDefinition needs MinWidth so Auto control content cannot crush the label.");
        Assert.True(
            double.TryParse(attr!.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            && width >= MinHeaderWidth,
            $"Header MinWidth must be >= {MinHeaderWidth}, got '{attr.Value}'.");
    }
}
