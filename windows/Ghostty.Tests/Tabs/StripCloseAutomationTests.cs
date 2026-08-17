using System.IO;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// Expanded vertical-strip rows have a 22x22 icon-only close button.
/// Chrome fuzz found them only via TabList+Button geometry; Narrator
/// and Find-Name skipped them.
/// </summary>
public class StripCloseAutomationTests
{
    [Fact]
    public void ExpandedRowCloseButton_HasAccessibleName()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(
            "Ghostty.Tests.Tabs.VerticalTabStrip.xaml");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var xaml = reader.ReadToEnd();
        Assert.Contains("Click=\"OnRowCloseClick\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Close tab\"", xaml);
    }
}
