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

        // Greyed from the same property at build and on every Opening.
        var declared = build.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.Text == local);
        Assert.Contains(declared.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "IsEnabled" && a.Right.ToString() == "tab.ActionableCwd is not null");
        Assert.Contains(build.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == $"{local}.IsEnabled" && a.Right.ToString() == "tab.ActionableCwd is not null");

        // The click reads ActionableCwd, and nothing else off the tab.
        var click = ClickHandler(build, local);
        var reads = click.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Expression is IdentifierNameSyntax { Identifier.Text: "tab" })
            .Select(m => m.Name.Identifier.Text)
            .Distinct().ToList();
        Assert.Equal(["ActionableCwd"], reads);
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
