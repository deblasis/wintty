using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// NavigationView replaces the strip chevron. The built-in pane toggle
/// (hamburger) is stock MUXC chrome, so the host owns its own toggle
/// button; it carries the accessible name the chevron used to.
/// </summary>
public class StripNavPaneAutomationTests
{
    [Fact]
    public void VerticalTabStrip_UsesNavigationViewLeftCompact()
    {
        var xaml = ReadEmbedded("VerticalTabStrip.xaml");
        Assert.Contains("x:Name=\"NavView\"", xaml);
        Assert.Contains("PaneDisplayMode=\"LeftCompact\"", xaml);
        Assert.Contains("IsPaneToggleButtonVisible=\"False\"", xaml);
        Assert.DoesNotContain("ChevronButton", xaml);
    }

    [Fact]
    public void VerticalTabHost_OwnsCustomPaneToggleBelowIcon()
    {
        var host = ReadEmbedded("VerticalTabHost.xaml");
        Assert.Contains("x:Name=\"IconBadgeHost\"", host);
        Assert.Contains("x:Name=\"PaneToggleButton\"", host);
        Assert.Contains("OnPaneToggleClick", host);
    }

    /// <summary>
    /// The pane toggle is a 32x32 icon-only button. Without an accessible
    /// name that tracks state, Narrator and UIA Find-Name cannot tell
    /// "expand" from "collapse". This guarantee moved off the chevron
    /// (deleted) onto PaneToggleButton and must not be lost again.
    /// </summary>
    [Fact]
    public void PaneToggle_RetargetsAccessibleNameOnState()
    {
        var host = ReadEmbedded("VerticalTabHost.xaml.cs");
        Assert.Contains("AutomationProperties.SetName(", host);
        Assert.Contains("PaneToggleButton", host);
        Assert.Contains("\"Collapse sidebar\" : \"Expand sidebar\"", host);
    }

    [Fact]
    public void VerticalTabStrip_PaneLayoutIsExternalContentSafe()
    {
        var stripCs = ReadEmbedded("VerticalTabStrip.xaml.cs");
        Assert.Contains("ApplyPaneLayout", stripCs);
        Assert.Contains("NavigationViewPaneDisplayMode.Left", stripCs);
        Assert.Contains("LeftCompact", stripCs);
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
