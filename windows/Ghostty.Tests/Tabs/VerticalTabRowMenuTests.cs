using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// VerticalTabHost used to swallow ListViewItem right-clicks
/// ("leave it to whatever per-item flyout exists") but no flyout
/// was attached. Chrome fuzz then missed Switch to horizontal tabs.
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
        Assert.Contains("isVertical: true", source);
    }
}
