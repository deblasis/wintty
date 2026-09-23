using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The shell half of "every tab shows its active pane's title, live".
///
/// <c>TabTitlePerTabTests</c> proves the model follows a host's
/// <c>TitleChanged</c> for every tab. What it cannot see is whether the
/// real host raises it: the pane host is a WinUI type this assembly cannot
/// load, so these parse its source instead.
///
/// The two defects this closes lived in the window's title coordinator,
/// which hooked one pane (the selected tab's focused leaf) and wrote into
/// whichever tab was selected. A background tab froze (wintty#1129), and a
/// host's focus change could retitle a tab it did not belong to
/// (wintty#1128). The census below keeps that shape from coming back.
/// </summary>
public class TabTitleWiringTests
{
    private const string PaneHostFile = "Panes.PaneHost.cs";
    private const string CoordinatorFile = "Shell.TitleBarCoordinator.cs";
    private const string Forwarder = "OnTerminalTitleChanged";

    private static ShellSource Host() => ShellSource.Load(PaneHostFile);

    // The one subscription (or unsubscription) of the named handler in the
    // whole file.
    private static AssignmentExpressionSyntax Wiring(SyntaxKind kind, string evt, string handler)
    {
        var found = Host().Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(kind)
                        && a.Left is MemberAccessExpressionSyntax m
                        && m.Name.Identifier.ValueText == evt
                        && a.Right.ToString() == handler)
            .ToList();
        Assert.True(found.Count == 1,
            $"expected one '{evt} {(kind == SyntaxKind.AddAssignmentExpression ? "+=" : "-=")} {handler}', found {found.Count}");
        return found[0];
    }

    /// <summary>
    /// Every leaf's terminal is subscribed for its title from birth, in the
    /// same block that subscribes its directory, and unsubscribed beside it.
    /// Subscribing on focus is what left background panes, and so
    /// background tabs, with nobody listening. Anchored on the directory
    /// rather than on a method name, so moving the per-terminal wiring into
    /// a helper keeps this green only if the title moves with it.
    /// </summary>
    [Theory]
    [InlineData(SyntaxKind.AddAssignmentExpression)]
    [InlineData(SyntaxKind.SubtractAssignmentExpression)]
    public void EveryTerminal_IsWiredForItsTitle_BesideItsDirectory(SyntaxKind kind)
    {
        var title = Wiring(kind, "TitleChanged", Forwarder);
        var cwd = Wiring(kind, "PwdChanged", "OnTerminalPwdChanged");
        Assert.Same(
            cwd.Ancestors().OfType<BlockSyntax>().First(),
            title.Ancestors().OfType<BlockSyntax>().First());
    }
    /// <summary>
    /// Only the active leaf names the tab. The guard is checked against the
    /// active leaf's terminal, not against anything the window holds, and
    /// the forward goes through the one emitter.
    /// </summary>
    [Fact]
    public void OnlyTheActiveLeafsTitle_IsForwarded()
    {
        var body = Host().Method(Forwarder).Body!;
        var guard = body.Call("Ghostty.Core.Tabs.LiveTitleGuard.Accepts");
        Assert.Equal("sender", guard.Arg(0));
        Assert.Equal("_activeLeaf.Terminal()", guard.Arg(1));
        Assert.IsType<PrefixUnaryExpressionSyntax>(guard.Parent);
        body.Call("EmitActiveLeafTitle");
    }

    /// <summary>
    /// A focus change hands the tab the newly focused pane's title, from
    /// the same handler that hands it the directory.
    /// </summary>
    [Fact]
    public void AFocusChange_ReEmitsTheTitle()
    {
        var wire = Host().Method("WireCommonHandlers").Body!;
        var focusHandler = wire.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                         && a.Left.ToString() == "LeafFocused");
        focusHandler.Right.Call("EmitActiveLeafCwd");
        focusHandler.Right.Call("EmitActiveLeafTitle");
    }

    /// <summary>
    /// Nothing but the tab model's per-tab forwarders writes a tab's shell
    /// title, and each of those writes the tab it was wired for. A write
    /// through <c>ActiveTab</c> anywhere is the wrong-tab defect.
    /// </summary>
    [Fact]
    public void TheShellTitle_IsWrittenOnlyByPerTabForwarders()
    {
        var writers = new List<string>();
        foreach (var file in ShellSource.AllFiles())
        {
            foreach (var a in file.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (a.Left is MemberAccessExpressionSyntax m
                    && m.Name.Identifier.ValueText == "ShellReportedTitle")
                {
                    writers.Add($"{file.Name}: {a}");
                }
            }
        }

        // Non-vacuity: the two forwarders (create, adopt) must be found, or
        // this census proves nothing about the ones it did not see.
        Assert.Equal(
            new[]
            {
                "Core.Tabs.TabManager.cs: tab.ShellReportedTitle = title",
                "Core.Tabs.TabManager.cs: tab.ShellReportedTitle = title",
            },
            writers);
    }

    /// <summary>
    /// The coordinator keeps the window caption and nothing else: no
    /// terminal title subscription of its own.
    /// </summary>
    [Fact]
    public void TheTitleBarCoordinator_HooksNoPaneTitle()
    {
        var root = ShellSource.Load(CoordinatorFile).Root;
        var hooks = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is MemberAccessExpressionSyntax m
                        && (m.Name.Identifier.ValueText == "TitleChanged"
                            || m.Name.Identifier.ValueText == "LeafFocused"))
            .Select(a => a.ToString())
            .ToList();
        Assert.Empty(hooks);
    }
}
