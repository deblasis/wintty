using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The shell-side half of the strip polish, pinned in source because
/// WinUI cannot load headlessly: the home glyph, the settling dim, the
/// pinned square's tooltip, the first-render bridge, and the new-tab
/// chord opening the default profile.
/// </summary>
public class TabStripPolishWiringTests
{
    private const string HomeGlyph = "\\uE80F";

    /// <summary>
    /// Both strips carry the glyph as an escape rather than the raw
    /// private-use character, which a source scan cannot see, and neither
    /// prints a literal tilde.
    /// </summary>
    [Theory]
    [InlineData("Tabs.VerticalTabNavRow.cs")]
    [InlineData("Tabs.TabHost.xaml.cs")]
    public void TheStrip_SpellsTheHomeGlyphAsAnEscape(string source)
    {
        var root = ShellSource.Load(source).Root;
        Assert.Contains(root.DescendantNodes().OfType<LiteralExpressionSyntax>(),
            l => l.Token.Text.Contains(HomeGlyph));
        Assert.DoesNotContain(root.DescendantNodes().OfType<LiteralExpressionSyntax>(),
            l => l.Token.ValueText == "~");
    }

    /// <summary>
    /// The glyph the strips build is the home one, and it reaches the tree.
    /// Asserting the constant exists proves neither: an icon initialised
    /// with the moon's glyph, or one never added to a panel, leaves a home
    /// tab drawing the wrong thing or nothing at all.
    /// </summary>
    [Theory]
    [InlineData("Tabs.VerticalTabNavRow.cs", "textRow.Children.Add")]
    [InlineData("Tabs.TabHost.xaml.cs", "iconRow.Children.Add")]
    public void TheHomeGlyph_IsBuiltFromTheHomeConstant_AndAddedToTheTree(string source, string add)
    {
        var root = ShellSource.Load(source).Root;
        static bool IsHomeIcon(ExpressionSyntax? value)
            => value is ObjectCreationExpressionSyntax
            {
                Type: IdentifierNameSyntax { Identifier.Text: "FontIcon" },
                Initializer: { } init,
            } && init.Expressions.OfType<AssignmentExpressionSyntax>().Any(a =>
                a.Left.ToString() == "Glyph" && a.Right.ToString() == "HomeGlyph");

        // The row keeps it in a field, the horizontal strip in a local.
        var name = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => IsHomeIcon(v.Initializer?.Value))
            .Select(v => v.Identifier.Text)
            .Concat(root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => IsHomeIcon(a.Right))
                .Select(a => a.Left.ToString()))
            .Distinct()
            .Single();

        Assert.Contains(root.Calls(add), c => c.Arg(0) == name);
    }

    /// <summary>
    /// The horizontal anatomy pass switches on element type, and the home
    /// arm is guarded while the bell arm is not, so the guarded one has to
    /// come first. Reordered, the bell arm swallows the glyph and its
    /// visibility is never written -- an ordering that IS the behaviour.
    /// </summary>
    [Fact]
    public void TheHomeArm_PrecedesTheUnguardedFontIconArm()
    {
        var anatomy = ShellSource.Load("Tabs.TabHost.xaml.cs").Method("ApplyPinnedTabAnatomy");
        var sections = anatomy.DescendantNodes().OfType<SwitchSectionSyntax>().ToList();
        var home = sections.FindIndex(s => s.Labels.ToString().Contains("HomeGlyph"));
        var bare = sections.FindIndex(s =>
            s.Labels.ToString().Contains("FontIcon") && !s.Labels.ToString().Contains("HomeGlyph"));

        Assert.True(home >= 0 && bare >= 0, "both FontIcon arms must exist");
        Assert.True(home < bare, "the guarded home arm must precede the unguarded FontIcon arm");
    }

    /// <summary>
    /// The body row swaps the pair: at home the glyph shows and the title
    /// does not, both decided by the model's own judgement. Asserting the
    /// glyph merely exists would survive a row that went on printing the
    /// tilde beside it.
    /// </summary>
    [Fact]
    public void TheBodyRow_ShowsTheGlyphInsteadOfTheTitle()
    {
        var refresh = ShellSource.Load("Tabs.VerticalTabNavRow.cs").Method("Refresh");
        var writes = refresh.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().EndsWith(".Visibility", System.StringComparison.Ordinal))
            .ToDictionary(a => a.Left.ToString(), a => a.Right.ToString());

        Assert.Equal("tab.IsHome ? Visibility.Collapsed : Visibility.Visible", writes["_title.Visibility"]);
        Assert.Equal("tab.IsHome ? Visibility.Visible : Visibility.Collapsed", writes["_home.Visibility"]);
    }

    /// <summary>
    /// The horizontal strip does the same swap inside its anatomy pass,
    /// where a pinned tab hides both.
    /// </summary>
    [Fact]
    public void TheHorizontalTab_ShowsTheGlyphInsteadOfTheTitle()
    {
        var anatomy = ShellSource.Load("Tabs.TabHost.xaml.cs").Method("ApplyPinnedTabAnatomy");
        var writes = anatomy.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().EndsWith(".Visibility", System.StringComparison.Ordinal))
            .Select(a => a.Right.ToString())
            .ToList();

        Assert.Contains("pinned || tab.IsHome ? Visibility.Collapsed : Visibility.Visible", writes);
        Assert.Contains("pinned || !tab.IsHome ? Visibility.Collapsed : Visibility.Visible", writes);
    }

    /// <summary>
    /// The start is carried by the icon alone: the dim belongs to idle, and
    /// reusing it would put a resting tab's ink on the tab the user just
    /// opened and is looking at.
    /// </summary>
    [Theory]
    [InlineData("Tabs.VerticalTabNavRow.cs", "_title.Opacity")]
    [InlineData("Tabs.VerticalTabPinnedRow.cs", "_iconSlot.Opacity")]
    public void TheDimBelongsToIdleAlone(string source, string target)
    {
        var write = ShellSource.Load(source).Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == target);
        Assert.Equal("tab.IsIdle ? IdleOpacity : 1.0", write.Right.ToString());
    }

    /// <summary>
    /// Both vertical row kinds hear the flag: the strip keeps two binding
    /// lists, and a nameof present in only one of them leaves that row kind
    /// deaf while a file-wide search still finds the literal.
    /// </summary>
    [Fact]
    public void BothVerticalRowKindsListenForTheSettlingFlag()
    {
        var bindings = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs").Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "AotBinding.Create"
                        && i.ArgumentList.Arguments.Any(a => a.ToString() == "nameof(TabModel.IsSettling)"))
            .ToList();
        Assert.Equal(2, bindings.Count);
    }

    /// <summary>
    /// The horizontal strip's arm exists AND does the one thing it is for:
    /// an empty arm satisfies a search for the property name.
    /// </summary>
    [Fact]
    public void TheHorizontalStrip_TellsAssistiveClientsATabIsStarting()
    {
        var arm = ShellSource.Load("Tabs.TabHost.xaml.cs").Method("AddItem").DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("nameof(TabModel.IsSettling)"));
        Assert.Contains(arm.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.Expression.ToString() == "ApplyItemAccessibleText");
    }

    /// <summary>
    /// A pinned tab on the horizontal strip is the icon presenter alone,
    /// and the presenter's own tooltip would win over the item's. The
    /// anatomy pass hands the tooltip back to the item while pinned.
    /// </summary>
    [Fact]
    public void ThePinnedSquare_LetsTheItemsTooltipSpeak()
    {
        var anatomy = ShellSource.Load("Tabs.TabHost.xaml.cs").Method("ApplyPinnedTabAnatomy");
        var set = anatomy.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "icon.ShowsTooltip");
        Assert.Equal("!pinned", set.Right.ToString());
    }

    /// <summary>
    /// The first render reaches the tab: the pane host raises the
    /// interface event where it already learns a leaf has painted.
    /// </summary>
    [Fact]
    public void TheFirstRender_CrossesTheHostInterface()
    {
        var host = ShellSource.Load("Panes.PaneHost.cs");

        // The raise is a top-level statement of the handler, not nested in
        // the glow lookup: a pane too small to glow still paints, and its
        // tab must still stop reading as starting.
        var handler = host.Method("OnLeafFirstRender");
        var call = handler.Body!.Statements.OfType<ExpressionStatementSyntax>()
            .Select(s => s.Expression)
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression.ToString() == "RaiseFirstRendered");
        Assert.Same(handler.Body, call.Parent!.Parent);

        // ... and the raise itself reaches the interface event.
        var raise = host.Method("RaiseFirstRendered");
        Assert.Contains(raise.DescendantNodes().OfType<ConditionalAccessExpressionSyntax>(),
            c => c.Expression.ToString() == "FirstRendered"
                 && c.WhenNotNull is InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax { Name.Identifier.Text: "Invoke" } });
    }

    /// <summary>
    /// Every field the harness reads is one the seam still emits. A rename
    /// here turns a live assertion into a no-op that reads null as false,
    /// which is the shape that lets a harness pass on silence.
    /// </summary>
    [Theory]
    [InlineData("hover")]
    [InlineData("settling")]
    [InlineData("home")]
    [InlineData("renderedHomeGlyph")]
    [InlineData("renderedHomeGlyphH")]
    public void TheSeam_StillEmitsTheFieldTheHarnessReads(string field)
    {
        var seam = ShellSource.Load("Testing.TestSeam.cs").Root.ToString();
        Assert.True(
            seam.Contains($"\"{field}\"", System.StringComparison.Ordinal),
            $"the seam no longer writes '{field}', so any harness assertion on it is silently dead");
    }

    /// <summary>
    /// ctrl+t opens the default profile, the same tab the + button opens,
    /// and falls back to a bare tab only when there is no default to open.
    /// </summary>
    [Fact]
    public void TheNewTabChord_OpensTheDefaultProfile()
    {
        var router = ShellSource.Load("Input.PaneActionRouter.cs");
        var arm = router.Root.DescendantNodes().OfType<SwitchSectionSyntax>()
            .Single(s => s.Labels.Any(l => l.ToString().Contains("PaneAction.NewTab")));
        // The arm defers to a method rather than calling the bare NewTab.
        Assert.DoesNotContain(arm.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.Expression.ToString() == "_tabs.NewTab");
        var callee = arm.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression.ToString()).Single();

        // The registry's own answer, opened when there is one and a bare tab
        // when there is not -- pinned as parsed, because both the condition
        // and the branches invert into something that still reads right.
        var method = router.Method(callee);
        Assert.Contains(method.DescendantNodes().OfType<ConditionalAccessExpressionSyntax>(),
            c => c.Expression.ToString() == "_getDefaultProfileId");

        var branch = method.DescendantNodes().OfType<IfStatementSyntax>().First();
        Assert.Equal("defaultId is not null && _openProfile is not null", branch.Condition.ToString());
        Assert.Contains(branch.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            i => i.Expression.ToString() == "_openProfile"
                 && i.ArgumentList.Arguments[1].Expression.ToString() == "ProfileLaunchTarget.NewTab");
        Assert.NotNull(branch.Else);
        Assert.Contains(branch.Else!.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.Expression.ToString() == "_tabs.NewTab");
    }
}
