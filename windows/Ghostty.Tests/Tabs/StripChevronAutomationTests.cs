using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The strip chevron is a 32x32 icon-only button. Without an accessible
/// name, narrator and UIA Find-Name skip it (the profile flyout chevron
/// is also AutomationId ChevronButton but is named "Open profile menu").
/// </summary>
public class StripChevronAutomationTests
{
    [Fact]
    public void ChevronButton_DefaultsToExpandSidebarName()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(
            "Ghostty.Tests.Tabs.VerticalTabStrip.xaml");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var xaml = reader.ReadToEnd();
        Assert.Contains("x:Name=\"ChevronButton\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Expand sidebar\"", xaml);
    }

    [Fact]
    public void IsExpanded_RetargetsAccessibleName()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("VerticalTabStrip.xaml.cs", System.StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var source = reader.ReadToEnd();
        Assert.Contains("value ? \"Collapse sidebar\" : \"Expand sidebar\"", source);
        Assert.Contains("AutomationProperties.SetName(ChevronButton, label)", source);
    }
}
