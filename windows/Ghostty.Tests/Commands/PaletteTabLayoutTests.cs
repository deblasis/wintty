using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Commands;

/// <summary>
/// Palette must expose a discoverable tab-layout switch (search "vertical").
/// </summary>
public class PaletteTabLayoutTests
{
    [Fact]
    public void BuiltInCommandSource_OffersContextualTabLayoutSwitch()
    {
        var source = ReadEmbedded("BuiltInCommandSource.cs");
        Assert.Contains("AddTabLayoutSwitchCommand", source);
        Assert.Contains("Switch to Vertical Tabs", source);
        Assert.Contains("Switch to Horizontal Tabs", source);
        Assert.Contains("isVerticalTabLayout", source);
    }

    [Fact]
    public void CommandPaletteViewModel_SearchesDescriptions()
    {
        var source = ReadEmbedded("CommandPaletteViewModel.cs");
        Assert.Contains("c.Description.Contains(query", source);
    }

    private static string ReadEmbedded(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
