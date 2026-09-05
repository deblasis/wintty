using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// A tab's hover text is composed once, in <c>TabModel.TooltipText</c>,
/// and every strip surface that sets a tab tooltip reads that property.
/// Before it existed each surface put the label on the tooltip, so a
/// pointer on a tab learned nothing the strip did not already show. A
/// surface that went back to the label, or composed a string of its own,
/// would keep every model test green, which is why this reads the sources.
/// </summary>
public class TabTooltipSurfacesTests
{
    /// <summary>Every shell file that sets a tooltip on a tab row or item.</summary>
    public static TheoryData<string> TooltipSurfaces() => new()
    {
        "Tabs.TabHost.xaml.cs",             // horizontal TabViewItem
        "Tabs.VerticalTabStrip.xaml.cs",    // vertical NavigationViewItem
        "Tabs.VerticalTabNavRow.cs",        // vertical body row title
        "Tabs.VerticalTabPinnedRow.cs",     // vertical pinned square
    };

    /// <summary>
    /// Every tooltip that is about a tab -- its argument mentions the tab
    /// -- is exactly the composed property: not the label, not the icon's
    /// tooltip, not a string built on the spot, not a conditional.
    /// </summary>
    [Theory]
    [MemberData(nameof(TooltipSurfaces))]
    public void EveryTabTooltip_IsTheComposedTooltip(string source)
    {
        var root = ShellSource.Load(source).Root;
        var tabTooltips = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "SetToolTip" })
            .Select(i => i.ArgumentList.Arguments.Skip(1).FirstOrDefault()?.Expression.ToString() ?? "")
            .Where(a => a.Contains("tab."))
            .ToList();

        Assert.NotEmpty(tabTooltips);
        Assert.All(tabTooltips, a => Assert.Equal("tab.TooltipText", a));
    }

    /// <summary>
    /// The body row sets its tooltip twice, at build and on every Refresh.
    /// Dropping the Refresh one keeps the theory above green through the
    /// build-time call while the tooltip goes stale after every cd.
    /// </summary>
    [Fact]
    public void TheBodyRow_RefreshesItsTooltip_WithTheTitle()
    {
        var refresh = ShellSource.Load("Tabs.VerticalTabNavRow.cs").Method("Refresh");
        var tip = refresh.Call("ToolTipService.SetToolTip");
        Assert.Equal("_title", tip.Arg(0));
        Assert.Equal("tab.TooltipText", tip.Arg(1));
    }
}
