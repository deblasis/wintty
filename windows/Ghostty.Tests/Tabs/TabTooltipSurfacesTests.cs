using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// A tab's hover text is composed once, in the model, and every strip
/// surface that sets a tab tooltip reads it from there: the full
/// <c>TooltipText</c> on a pinned square, which shows no title of its
/// own, and <c>HoverText</c> everywhere else, which is null when the
/// hover would only repeat the label -- with the trimmed-label fallback
/// as the one allowed addition. A surface that composed its own string
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

    [Theory]
    [MemberData(nameof(TooltipSurfaces))]
    public void EveryTabTooltip_ComesFromTheModel(string source)
    {
        var root = ShellSource.Load(source).Root;
        var tabTooltips = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "SetToolTip" })
            .Select(i => i.ArgumentList.Arguments.Skip(1).FirstOrDefault()?.Expression.ToString() ?? "")
            .Where(a => a.Contains("tab."))
            .ToList();

        Assert.NotEmpty(tabTooltips);
        Assert.All(tabTooltips, a =>
        {
            Assert.DoesNotContain("EffectiveTitle", a);
            Assert.DoesNotContain("TabIcon", a);
            Assert.True(a.Contains("tab.TooltipText") || a.Contains("tab.HoverText"),
                $"'{a}' is neither the model's tooltip nor its hover text");
        });
    }

    /// <summary>
    /// The body row sets its tooltip at build and on every Refresh, from
    /// the hover text, falling back to the full tooltip only when its own
    /// TextBlock reports the label is trimmed.
    /// </summary>
    [Fact]
    public void TheBodyRow_RefreshesItsHover_WithTheTrimmedFallback()
    {
        var row = ShellSource.Load("Tabs.VerticalTabNavRow.cs");
        // Refresh delegates rather than writing the tooltip itself, so the
        // trim handler and the refresh cannot drift apart.
        Assert.Single(row.Method("Refresh").Calls("ApplyTooltip"));

        // The tooltip goes on the row, not on the title: a home row's title
        // is collapsed, and a tooltip on a collapsed element is unreachable.
        var tip = row.Method("ApplyTooltip").Call("ToolTipService.SetToolTip");
        Assert.Equal("this", tip.Arg(0));
        Assert.Equal("tab.HoverText ?? (_title.IsTextTrimmed ? tab.TooltipText : null)", tip.Arg(1));
    }

    /// <summary>The square has no label to repeat, so it always carries the full text.</summary>
    [Fact]
    public void ThePinnedSquare_CarriesTheFullTooltip()
    {
        var refresh = ShellSource.Load("Tabs.VerticalTabPinnedRow.cs").Method("Refresh");
        var tip = refresh.Call("ToolTipService.SetToolTip");
        Assert.Equal("tab.TooltipText", tip.Arg(1));
    }

    /// <summary>
    /// The collapsed rail is the same shape as the pinned square: the row's
    /// content is laid out past the rail's edge and clipped, so the item is
    /// an unlabelled icon and its tooltip is the only thing carrying the
    /// tab's identity. A hover that is null when it would repeat the label
    /// would leave it with nothing.
    /// </summary>
    [Fact]
    public void TheCollapsedRail_KeepsTheFullTooltip()
    {
        var chrome = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs").Method("ApplyItemTitleChrome");
        var tip = chrome.Call("ToolTipService.SetToolTip");
        var choice = Assert.IsType<ConditionalExpressionSyntax>(tip.ArgExpression(1));
        Assert.Equal("NavView.IsPaneOpen", choice.Condition.ToString());
        Assert.Equal("tab.HoverText", choice.WhenTrue.ToString());
        Assert.Equal("tab.TooltipText", choice.WhenFalse.ToString());
    }

    /// <summary>
    /// The horizontal header trims, or IsTextTrimmed never reports true and
    /// the fallback that gives a clipped tab its hover is dead code.
    /// </summary>
    [Fact]
    public void TheHorizontalHeader_Trims_SoTheTrimmedFallbackCanFire()
    {
        var add = ShellSource.Load("Tabs.TabHost.xaml.cs").Method("AddItem");
        var header = add.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.Text == "headerText");
        Assert.Contains("TextTrimming = TextTrimming.CharacterEllipsis", header.ToString());

        // ... and the tooltip re-derives when the trim state changes, which
        // it does on resize without the tab changing at all.
        Assert.Contains(add.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "headerText.IsTextTrimmedChanged");
    }
}
