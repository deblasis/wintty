using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The per-tab menu offers the directory the shell reported: copy it, or
/// open it in File Explorer. Both act on <c>TabModel.ActionableCwd</c>,
/// which is the reported path only when it is plain text and the spawn
/// policy accepts it -- never the collapsed display form, which cmd
/// cannot expand. The open path is the one that matters for safety. A
/// reported directory is bytes off the pty, and File Explorer handed a
/// file path runs the file's handler, so the menu goes through the
/// folder-only launcher, and only after a directory check that runs off
/// the UI thread.
///
/// The pair is GREYED, never hidden. The horizontal strip builds one
/// flyout per tab and reuses it, and a shell reports its directory a
/// moment after the tab opens -- hiding made the same menu change shape
/// between two openings. Greying also leaves something to learn from: a
/// shell with no integration never reports a directory at all, and an
/// absent item cannot say why.
/// </summary>
public class TabContextMenuCwdTests
{
    private static MethodDeclarationSyntax Build()
        => ShellSource.Load("Tabs.TabContextMenuBuilder.cs").Method("Build");

    [Theory]
    [InlineData("Copy Working Directory")]
    [InlineData("Open in File Explorer")]
    public void TheItem_IsBuilt_Added_AndActsOnTheActionableDirectory(string text)
    {
        var build = Build();
        var local = ItemNamed(build, text);

        Assert.Contains(build.Calls("flyout.Items.Add"), c => c.Arg(0) == local);

        // Greyed, never hidden: nothing in the method may write this item's
        // Visibility, at build time or on any later pass.
        var declared = build.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.Text == local);
        Assert.DoesNotContain(declared.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "Visibility");
        Assert.DoesNotContain(build.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == $"{local}.Visibility");

        // The click reads ActionableCwd, and nothing else off the tab.
        var click = ClickHandler(build, local);
        var reads = click.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Expression is IdentifierNameSyntax { Identifier.Text: "tab" })
            .Select(m => m.Name.Identifier.Text)
            .Distinct().ToList();
        Assert.Equal(["ActionableCwd"], reads);
    }

    [Fact]
    public void TheDirectoryGroup_KeepsItsShape()
    {
        // The separator that introduces the group is a plain rule now. A
        // rule that hid with the items is what made the menu change height.
        var build = Build();
        Assert.DoesNotContain(build.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString().StartsWith("cwdRule.", System.StringComparison.Ordinal));

        // And the hiding rule itself is gone, not merely unused.
        Assert.DoesNotContain(
            ShellSource.Load("Tabs.TabContextMenuBuilder.cs").Root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>(),
            m => m.Identifier.Text == "CwdVisibility");
    }

    [Fact]
    public void Availability_IsDecidedFromTheActionableDirectory()
    {
        var rule = ShellSource.Load("Tabs.TabContextMenuBuilder.cs").Method("CwdIsActionable");
        Assert.Equal("tab.ActionableCwd is not null", rule.ExpressionBody!.Expression.ToString());
    }

    /// <summary>
    /// The horizontal strip builds this flyout once per tab and reuses it,
    /// so a decision taken only at build time freezes at the value the tab
    /// had before its shell said anything. Both passes, or the item is
    /// permanently grey on every tab that was right-clicked early.
    /// </summary>
    [Fact]
    public void Availability_IsAppliedAtBuild_AndAgainOnEveryOpening()
    {
        var build = Build();
        var applications = build.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString() == "ApplyCwdAvailability")
            .ToList();
        Assert.Equal(2, applications.Count);

        var opening = build.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                         && a.Left.ToString() == "flyout.Opening")
            .Right;
        Assert.Single(applications, a => a.Ancestors().Any(n => n == opening));
        Assert.Single(applications, a => !a.Ancestors().Any(n => n == opening));

        // Both items go through it, not just one.
        foreach (var text in new[] { "Copy Working Directory", "Open in File Explorer" })
        {
            var local = ItemNamed(build, text);
            Assert.All(applications, a =>
                Assert.Contains(a.ArgumentList.Arguments, arg => arg.Expression.ToString() == local));
        }
    }

    /// <summary>
    /// A disabled MenuFlyoutItem raises none of the pointer events
    /// ToolTipService needs, so the reason it is unavailable rides
    /// AutomationProperties.HelpText, which Narrator reads as the item's
    /// description. Cleared again when the item is live, or a usable item
    /// carries an explanation for a state it is not in.
    /// </summary>
    [Fact]
    public void TheUnavailableReason_RidesHelpText_AndClearsWhenLive()
    {
        var apply = ShellSource.Load("Tabs.TabContextMenuBuilder.cs").Method("ApplyCwdAvailability");

        var enabled = apply.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString().EndsWith(".IsEnabled", System.StringComparison.Ordinal));

        var help = apply.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression.ToString() == "AutomationProperties.SetHelpText");

        // The same decision drives both, and the help text is null exactly
        // when the item is enabled.
        var choice = Assert.IsType<ConditionalExpressionSyntax>(
            help.ArgumentList.Arguments[1].Expression);
        Assert.Equal(enabled.Right.ToString(), choice.Condition.ToString());
        Assert.Equal("null", choice.WhenTrue.ToString());
        Assert.NotEqual("null", choice.WhenFalse.ToString());
    }

    [Fact]
    public void Open_ChecksADirectoryOffTheUiThread_ThenUsesTheFolderLauncher()
    {
        var click = ClickHandler(Build(), "openCwd");
        var statements = click.Block!.Statements;

        var launch = click.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "LaunchFolderPathAsync" });
        var exists = click.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.Text: "Directory" },
                Name.Identifier.Text: "Exists",
            });
        Assert.Equal(
            launch.ArgumentList.Arguments[0].Expression.ToString(),
            exists.ArgumentList.Arguments[0].Expression.ToString());

        // The check runs inside Task.Run, its answer lands in a local, and
        // `if (!local) return;` stands between it and the launch.
        Assert.Contains(exists.Ancestors().OfType<InvocationExpressionSyntax>(),
            i => i.Expression.ToString() == "Task.Run");
        var local = exists.Ancestors().OfType<VariableDeclaratorSyntax>().First().Identifier.Text;
        var gate = statements.OfType<IfStatementSyntax>().Single(s =>
            s.Condition is PrefixUnaryExpressionSyntax not_
            && not_.IsKind(SyntaxKind.LogicalNotExpression)
            && not_.Operand is IdentifierNameSyntax id && id.Identifier.Text == local);
        Assert.True(gate.Statement is ReturnStatementSyntax
            || gate.Statement.DescendantNodes().OfType<ReturnStatementSyntax>().Any());

        int At(SyntaxNode n) => statements.IndexOf(statements.Single(s => s.Span.Contains(n.Span)));
        Assert.True(At(exists) < At(gate), "the directory check comes after its own gate");
        Assert.True(At(gate) < At(launch), "the launch is not behind the gate");
        Assert.DoesNotContain(gate.DescendantNodes(), n => n == launch);

        // Nothing in this file can execute a file.
        Assert.DoesNotContain(
            ShellSource.Load("Tabs.TabContextMenuBuilder.cs").Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            m => m.Name.Identifier.Text is "Start" or "LaunchUriAsync" or "LaunchFileAsync");
    }

    /// <summary>The local a <c>new MenuFlyoutItem { Text = ... }</c> with this text is assigned to.</summary>
    private static string ItemNamed(MethodDeclarationSyntax build, string text)
        => build.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Initializer?.Value is ObjectCreationExpressionSyntax
            {
                Type: IdentifierNameSyntax { Identifier.Text: "MenuFlyoutItem" },
                Initializer: { } init,
            } && init.Expressions.OfType<AssignmentExpressionSyntax>().Any(a =>
                a.Left.ToString() == "Text" && a.Right is LiteralExpressionSyntax l && l.Token.ValueText == text))
            .Identifier.Text;

    /// <summary>The lambda subscribed as <c>&lt;local&gt;.Click += ...</c>.</summary>
    private static ParenthesizedLambdaExpressionSyntax ClickHandler(MethodDeclarationSyntax build, string local)
        => (ParenthesizedLambdaExpressionSyntax)build.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.IsKind(SyntaxKind.AddAssignmentExpression) && a.Left.ToString() == $"{local}.Click")
            .Right;
}
