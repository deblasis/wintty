using System.IO;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Search;

/// <summary>
/// PlaceholderText is not the UIA Name. Without an explicit Name the
/// fuzz harness looking for "Search scrollback" reports the bar missing
/// even when it is open.
/// </summary>
public class SearchBarAutomationTests
{
    private const string Resource = "Ghostty.Tests.Search.SearchBarControl.xaml";

    [Fact]
    public void NeedleBox_HasAccessibleNameMatchingPlaceholder()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(Resource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var xaml = reader.ReadToEnd();
        Assert.Contains("x:Name=\"NeedleBox\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Search scrollback\"", xaml);
    }
}
