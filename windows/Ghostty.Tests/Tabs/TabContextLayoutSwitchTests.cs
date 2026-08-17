using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// Horizontal TabView fills with tabs; the empty-strip right-click
/// that hosts StripContextMenuBuilder's layout switch disappears.
/// The per-tab menu has to offer the same switch or a 3-tab window
/// cannot leave horizontal layout without a key chord.
/// </summary>
public class TabContextLayoutSwitchTests
{
    [Fact]
    public void TabContextMenu_OffersSwitchToVerticalTabs()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("TabContextMenuBuilder.cs", System.StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var source = reader.ReadToEnd();
        Assert.Contains("Switch to vertical tabs", source);
        Assert.Contains("toggleTabLayout", source);
    }

    [Fact]
    public void TabContextMenu_OffersSwitchToHorizontalTabsWhenVertical()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("TabContextMenuBuilder.cs", System.StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var source = reader.ReadToEnd();
        Assert.Contains("isVertical", source);
        Assert.Contains("Switch to horizontal tabs", source);
    }
}
