using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// A transient notice must actually leave on its own, and Enter or Space
/// must dismiss a focused one; both live in AddBar, the only place a bar is
/// born.
/// </summary>
public sealed class NotificationHostWiringTests
{
    private static ShellSource Host() => ShellSource.Load("Controls.Notifications.NotificationHost.xaml.cs");

    /// <summary>
    /// WinUI folds a StackPanel's own Margin into its DesiredSize even with
    /// zero children, so leaving the host Visible while empty would keep the
    /// Auto dock row -- and the host's own opaque background -- permanently
    /// open (fix round 1, finding 1). Starting Collapsed is what makes "no
    /// notices" actually mean zero height.
    /// </summary>
    [Fact]
    public void Constructor_StartsCollapsed()
    {
        var ctor = Host().Root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "NotificationHost");
        var setsCollapsed = ctor.Body!.Statements
            .OfType<ExpressionStatementSyntax>()
            .Any(s => s.Expression is AssignmentExpressionSyntax
            {
                Left: IdentifierNameSyntax { Identifier.ValueText: "Visibility" },
            } assignment && assignment.Right.ToString() == "Visibility.Collapsed");
        Assert.True(
            setsCollapsed,
            "the constructor must start the host Collapsed, or an empty StackPanel's own Margin "
            + "keeps the Auto dock row permanently open");
    }

    /// <summary>
    /// Every path that changes <see cref="_bars"/> -- adding a notice,
    /// removing one, and the two bulk-clear paths -- has to agree on the
    /// same collapse-when-empty decision, found by shape (a method that sets
    /// Visibility from _bars.Count) rather than by a hardcoded name, so this
    /// does not just re-assert one method's name back at it.
    /// </summary>
    [Fact]
    public void VisibilityTracksWhetherAnyBarIsActive()
    {
        var src = Host();

        // Not restricted to block-bodied methods: UpdateVisibility itself is
        // an expression-bodied one, whose assignment lives under
        // ExpressionBody rather than Body -- a Body-only search would find
        // zero methods and fail this fact for a reason that has nothing to
        // do with what it is meant to pin.
        var updater = src.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left is IdentifierNameSyntax { Identifier.ValueText: "Visibility" }
                    && a.Right.ToString().Contains("_bars.Count")))
            .ToList();
        Assert.True(
            updater.Count == 1,
            $"expected exactly one method that sets Visibility from _bars.Count, found {updater.Count}");
        var helperName = updater[0].Identifier.ValueText;

        foreach (var caller in new[] { "AddBar", "RemoveBar", "OnUnloaded", "OnActiveChanged" })
        {
            var calls = src.Method(caller).Body!.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression.ToString());
            Assert.Contains(
                calls,
                c => c == helperName);
        }
    }

    [Fact]
    public void AddBar_ArmsTheAutoDismissTimer_WhenTheNoticeAsksForOne()
    {
        var body = Host().Method("AddBar").Body!.ToString();
        Assert.Contains("AutoDismissAfter", body);
        Assert.Contains("CreateTimer", body);
    }

    [Fact]
    public void AddBar_HandlesEnterAndSpace()
    {
        var body = Host().Method("AddBar").Body!.ToString();
        Assert.Contains("VirtualKey.Enter", body);
        Assert.Contains("VirtualKey.Space", body);
        Assert.Contains("FocusOnShow", body);
    }

    [Fact]
    public void RemoveBar_StopsTheTimer_AndReturnsFocus()
    {
        var body = Host().Method("RemoveBar").Body!.ToString();
        Assert.Contains("Stop()", body);
        Assert.Contains("FocusReturn", body);
    }
}
