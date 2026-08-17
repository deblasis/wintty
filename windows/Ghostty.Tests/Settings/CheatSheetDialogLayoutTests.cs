using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// The cheat sheet is a ContentDialog on the main XamlRoot. A 580px-tall
/// Wintty plus the dialog title and Close command bar cannot host a
/// Height=520 content grid — Copy as Markdown / Save... sit in row 2 and
/// get clipped. Size the list with MaxHeight and leave the grid auto-tall
/// so the action row stays on screen.
/// </summary>
public class CheatSheetDialogLayoutTests
{
    private const string Resource = "Ghostty.Tests.Settings.CheatSheetDialog.xaml";

    [Fact]
    public void ContentGrid_DoesNotForceHeightThatClipsTheActionRow()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(Resource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var doc = XDocument.Parse(reader.ReadToEnd());

        var grid = doc.Descendants().First(e => e.Name.LocalName == "Grid"
            && e.Attribute("Width") is not null);
        Assert.Null(grid.Attribute("Height"));

        var list = doc.Descendants().First(e => e.Attribute("Name")?.Value == "RowsList"
            || e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "RowsList"));
        var max = list.Attribute("MaxHeight");
        Assert.True(max is not null, "RowsList needs MaxHeight so the dialog can shrink instead of clipping Copy/Save.");

        var xaml = doc.ToString();
        Assert.Contains("Copy as Markdown", xaml);
        Assert.Contains("Save...", xaml);
    }
}
