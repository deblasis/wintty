using System.Collections.Generic;
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
    /// Every shell file that renders a tab's name as text. A new one
    /// belongs here the day it is written. The vertical pinned square is
    /// not here on purpose: it draws no title, and the two things it does
    /// carry -- the tooltip and the accessible name -- are composed in
    /// Core (<c>TooltipText</c>, <c>TabAccessibleText.Name</c>) from the
    /// same property, which <c>TabTooltipSurfacesTests</c> guards.
    /// </summary>
    private static readonly string[] TitleSurfaceNames =
    [
        "Tabs.TabHost.xaml.cs",             // horizontal strip header
        "Tabs.VerticalTabNavRow.cs",        // vertical body row
        "Tabs.TabSwitcherPopup.xaml.cs",    // Ctrl+Tab tiles
        "Tabs.TabOverviewControl.xaml.cs",  // overview grid
        "Shell.TitleBarCoordinator.cs",     // window title (taskbar, Alt+Tab)
        "Shell.TabMorphGhost.cs",           // the layout-switch ghost
        "Commands.JumpCommandSource.cs",    // command palette entries
    ];

    /// <summary>
    /// Surfaces that can only show words, where a home tab's glyph has to
    /// become "Home": they read the model's WordTitle instead. Every
    /// surface is on exactly one of these two lists -- a text surface left
    /// unclassified is one that prints a bare tilde while the strips draw a
    /// house and the window title says "Home".
    /// </summary>
    private static readonly string[] WordSurfaceNames =
    [
        "Shell.TitleBarCoordinator.cs",     // window title (taskbar, Alt+Tab), vertical caption
        "Commands.JumpCommandSource.cs",    // command palette entries
        "Tabs.TabSwitcherPopup.xaml.cs",    // Ctrl+Tab tiles
        "Tabs.TabOverviewControl.xaml.cs",  // overview grid
        "Shell.TabMorphGhost.cs",           // the layout-switch ghost
    ];

    /// <summary>The two surfaces that draw the glyph rather than print it.</summary>
    private static readonly string[] GlyphSurfaceNames =
    [
        "Tabs.TabHost.xaml.cs",
        "Tabs.VerticalTabNavRow.cs",
    ];

    public static TheoryData<string> TitleSurfaces() => Data(TitleSurfaceNames);

    public static TheoryData<string> WordSurfaces() => Data(WordSurfaceNames);

    public static TheoryData<string> GlyphSurfaces() => Data(GlyphSurfaceNames);

    private static TheoryData<string> Data(string[] names)
    {
        var data = new TheoryData<string>();
        foreach (var name in names) data.Add(name);
        return data;
    }

    [Fact]
    public void EveryTitleSurface_IsClassifiedExactlyOnce()
    {
        Assert.Empty(WordSurfaceNames.Intersect(GlyphSurfaceNames));
        Assert.Empty(TitleSurfaceNames.Except(WordSurfaceNames).Except(GlyphSurfaceNames));
    }


    [Theory]
    [MemberData(nameof(TitleSurfaces))]
    public void EverySurface_ReadsTheComposedTitle(string source)
    {
        var root = ShellSource.Load(source).Root;
        var reads = InstanceReads(root, "EffectiveTitle") + InstanceReads(root, "WordTitle");
        Assert.True(
            reads > 0,
            $"{source} shows a tab's name but never reads EffectiveTitle or WordTitle; a " +
            "surface that composes its own string does not follow the " +
            "precedence and goes on showing the interpreter's path");
    }

    /// <summary>
    /// A surface that can only print draws the word form. The rule is about
    /// what is drawn, not about every read: a change-detection key or a
    /// diagnostic label may still hold the composed title, since neither
    /// reaches a reader's eye.
    /// </summary>
    [Theory]
    [MemberData(nameof(WordSurfaces))]
    public void AWordOnlySurface_DrawsTheWordTitle_NeverTheGlyphForm(string source)
    {
        var root = ShellSource.Load(source).Root;
        Assert.True(InstanceReads(root, "WordTitle") > 0, $"{source} never reads WordTitle");

        foreach (var drawn in Drawn(root))
        {
            Assert.DoesNotContain("EffectiveTitle", drawn);
        }
    }

    [Theory]
    [MemberData(nameof(GlyphSurfaces))]
    public void AGlyphSurface_DrawsTheComposedTitle_NotTheWordForm(string source)
    {
        var root = ShellSource.Load(source).Root;
        Assert.True(InstanceReads(root, "EffectiveTitle") > 0, $"{source} never reads EffectiveTitle");
        Assert.Equal(0, InstanceReads(root, "WordTitle"));
    }

    /// <summary>
    /// Every expression written into something a reader sees: a TextBlock's
    /// Text, or the window's Title.
    /// </summary>
    private static IEnumerable<string> Drawn(SyntaxNode root)
        => root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() is "Text" or "_window.Title"
                        || a.Left.ToString().EndsWith(".Text", System.StringComparison.Ordinal)
                        || a.Left.ToString().EndsWith(".Title", System.StringComparison.Ordinal))
            .Select(a => a.Right.ToString());

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
