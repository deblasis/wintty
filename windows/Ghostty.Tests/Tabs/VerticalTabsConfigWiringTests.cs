using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// VerticalTabHost used to hardcode width and pin state. Those must stay
/// wired to config: the Settings UI and WindowsOnlyKeys still offer
/// vertical-tabs-width and vertical-tabs-pinned, so a host that ignores
/// them turns shipped settings into no-ops with no other symptom.
/// </summary>
public class VerticalTabsConfigWiringTests
{
    [Fact]
    public void Host_ReadsConfigKeys_NotHardcodedConstants()
    {
        var host = ReadEmbedded("VerticalTabHost.xaml.cs");

        Assert.DoesNotContain("const double ExpandedWidth", host);
        Assert.Contains("VerticalTabsWidth", host);
        Assert.Contains("VerticalTabsPinned", host);
        Assert.Contains("ConfigChanged", host);
    }

    [Fact]
    public void Host_UnsubscribesConfigChangedOnUnload()
    {
        var host = ReadEmbedded("VerticalTabHost.xaml.cs");
        Assert.Contains("cfg.ConfigChanged -= OnConfigChanged", host);
    }

    /// <summary>
    /// Cold start has no LayoutCoordinator subscriber yet, so a pinned
    /// sidebar must be applied directly rather than through the tween
    /// event, which would be dropped.
    /// </summary>
    [Fact]
    public void Host_AppliesPinnedOnColdStart()
    {
        var host = ReadEmbedded("VerticalTabHost.xaml.cs");
        Assert.Contains("StripWidthChangeRequested is null", host);
    }

    [Fact]
    public void Strip_UsesNavigationViewNotLegacyChevronState()
    {
        var host = ReadEmbedded("VerticalTabHost.xaml.cs");
        var strip = ReadEmbedded("VerticalTabStrip.xaml.cs");
        Assert.Contains("_strip.OpenPaneLength", host);
        Assert.Contains("PaneDisplayMode=\"LeftCompact\"", ReadEmbedded("VerticalTabStrip.xaml"));
        Assert.DoesNotContain("ChevronToggled", strip);
        Assert.DoesNotContain("VerticalTabStripState", host);
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
