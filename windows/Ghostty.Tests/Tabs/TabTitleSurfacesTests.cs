using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// A tab's name is composed once, in
/// <c>TabModel.EffectiveTitle</c>, and every surface that shows it reads that
/// one property. That is what makes a change to the precedence -- the folder
/// tier, say -- land on both strips, the switcher, the overview, the window
/// title and the palette at the same time instead of on whichever one was
/// remembered.
///
/// A surface that composed its own string would keep passing every test
/// written against the model, which is why this reads the sources: the guard
/// is that nobody re-derives the name locally.
/// </summary>
public class TabTitleSurfacesTests
{
    /// <summary>
    /// Every shell file that renders or announces a tab's name. A new one
    /// belongs here the day it is written.
    /// </summary>
    public static TheoryData<string> TitleSurfaces() => new()
    {
        "Tabs.TabHost.xaml.cs",             // horizontal strip header
        "Tabs.VerticalTabStrip.xaml.cs",    // vertical strip tooltip + order
        "Tabs.VerticalTabNavRow.cs",        // vertical body row
        "Tabs.VerticalTabPinnedRow.cs",     // vertical pinned row
        "Tabs.TabSwitcherPopup.xaml.cs",    // Ctrl+Tab tiles
        "Tabs.TabOverviewControl.xaml.cs",  // overview grid
        "Shell.TitleBarCoordinator.cs",     // window title (taskbar, Alt+Tab)
        "Shell.TabMorphGhost.cs",           // the layout-switch ghost
        "Commands.JumpCommandSource.cs",    // command palette entries
    };

    [Theory]
    [MemberData(nameof(TitleSurfaces))]
    public void EverySurface_ReadsTheComposedTitle(string source)
    {
        var reads = InstanceReads(ShellSource.Load(source).Root, "EffectiveTitle");
        Assert.True(
            reads > 0,
            $"{source} shows a tab's name but never reads EffectiveTitle; a " +
            "surface that composes its own string does not follow the " +
            "precedence and goes on showing the interpreter's path");
    }

    /// <summary>
    /// The anti-vacuity half. <c>nameof(TabModel.EffectiveTitle)</c> in a
    /// PropertyChanged guard is a static reference, not a render, and a file
    /// that dropped its render but kept its subscription would satisfy a
    /// substring match. Only an instance read counts.
    /// </summary>
    [Fact]
    public void TheReadCounter_DoesNotCountANameofAsARender()
    {
        Assert.Equal(0, InstanceReads(Parse(
            "class C { void M(string e) { if (e == nameof(TabModel.EffectiveTitle)) { } } }"),
            "EffectiveTitle"));

        Assert.Equal(1, InstanceReads(Parse(
            "class C { void M(TabModel tab) { var t = tab.EffectiveTitle; } }"),
            "EffectiveTitle"));
    }

    private static SyntaxNode Parse(string text)
        => CSharpSyntaxTree.ParseText(text).GetRoot();

    /// <summary>
    /// Reads of <c>&lt;expr&gt;.member</c> where the left side is not the type
    /// name -- somebody's tab, not <c>TabModel</c> itself.
    /// </summary>
    private static int InstanceReads(SyntaxNode root, string member)
        => root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Count(m => m.Name.Identifier.Text == member
                        && m.Expression is not IdentifierNameSyntax { Identifier.Text: "TabModel" });
}
