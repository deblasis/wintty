using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// VerticalTabHost used to hardcode width/pin/hover behind TODOs.
/// Those must stay wired to the Windows-only config keys.
/// </summary>
public class VerticalTabsConfigWiringTests
{
    [Fact]
    public void Host_ReadsConfigKeys_NotHardcodedConstants()
    {
        var host = ReadEmbedded("VerticalTabHost.xaml.cs");

        Assert.DoesNotContain("const double ExpandedWidth", host);
        Assert.DoesNotContain("const bool HoverExpandEnabled", host);
        Assert.DoesNotContain("TODO(config): vertical-tabs-width", host);
        Assert.DoesNotContain("TODO(config): vertical-tabs-pinned", host);
        Assert.DoesNotContain("TODO(config): vertical-tabs-hover-expand", host);
        Assert.Contains("VerticalTabsWidth", host);
        Assert.Contains("VerticalTabsPinned", host);
        Assert.Contains("VerticalTabsHoverExpand", host);
        // Chevron-collapse while the pointer is still on the rail does
        // not fire PointerEntered again. Resume hover from IsPointerOver.
        Assert.Contains("_pointerOverStrip", host);
        Assert.Contains("_strip.PointerEntered", host);
        Assert.Contains("BeginHoverExpand", host);
        // Overlay Width is clipped by the RootGrid strip column; hover
        // must tween the outer column instead.
        var expand = host[host.IndexOf("private void BeginHoverExpand")..];
        Assert.Contains("StripWidthChangeRequested", expand);
        Assert.Contains("VerticalTabStripState.PinnedExpanded", host);
        Assert.Contains("vertical-tabs-width", ReadEmbedded(@"Config\WindowsOnlyKeys.cs"));
        Assert.Contains("vertical-tabs-pinned", ReadEmbedded(@"Config\WindowsOnlyKeys.cs"));
        Assert.Contains("vertical-tabs-hover-expand", ReadEmbedded(@"Config\WindowsOnlyKeys.cs"));
    }

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
