using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// VerticalTabHost shows TabContextMenuBuilder menu on NavigationViewItem
/// right-click (strip background still uses StripContextMenuBuilder).
/// </summary>
public class VerticalTabRowMenuTests
{
    [Fact]
    public void StripContextRequested_BuildsPerTabMenuOnRow()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("VerticalTabHost.xaml.cs", System.StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var source = reader.ReadToEnd();
        Assert.Contains("TabContextMenuBuilder.Build", source);
        Assert.Contains("TabFromElement", source);
        Assert.Contains("isVertical: true", source);
    }
}
