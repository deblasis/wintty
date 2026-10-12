using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That the main window is born at its final size (#1808).
///
/// The defect is a birth window, not a settled state: the window's
/// creation default (1200x800 physical) was the size the shell could
/// compute its first grid from, and the correction that applied the
/// restored geometry ran a few hundred lines later -- after the host,
/// the factory and the tab seeding. On the founder's update relaunch
/// the first frame's prompt laid out for exactly the default rect
/// (1200 px at the 10 px cell is the 120-column grid the screenshot
/// caught), because the column count was computed at (or for) the
/// default size and the resize never fully invalidated it.
///
/// The WinUI assembly cannot be loaded into a test host, so this reads
/// the source. Three things are pinned and each is separately
/// defeasible:
///
///   - The early application. The constructor applies the SAME geometry
///     selection the late block makes BEFORE anything that can measure
///     the window exists: before the per-window host, before the pane
///     factory, before the first tab.
///   - The deferred show. The early application passes
///     <c>applyMaximized: false</c>: a window whose content does not
///     exist yet must not appear, and the maximized show stays with the
///     late block that runs once the panes are seeded.
///   - The idempotent late block. The late application keeps the
///     default (<c>applyMaximized: true</c>) so a maximized restore
///     still shows maximized exactly once, from the block that owns
///     showing.
///
/// What this cannot prove: that the OS truly created the window at the
/// requested size, or that no intermediate frame ever presents. Both
/// need a live window; the birth-capture round owns that.
/// </summary>
public class MainWindowSpawnSizeWiringTests
{
    private const string MainFile = "Ghostty.MainWindow.xaml.cs";

    [Fact]
    public void Constructor_AppliesGeometryBeforeAnythingMeasuresTheWindow()
    {
        var ctor = PrivateConstructor();

        // The early application's two arms. Exactly one of each runs in
        // the early block; the late block re-runs the same selection, so
        // the constructor total is two of each.
        var applies = ctor.Calls("ApplyGeometry");
        var restores = ctor.Calls("RestoreWindowPlacement");
        Assert.Equal(2, applies.Count);
        Assert.Equal(2, restores.Count);

        // ORDER is the whole fix: the first geometry call must precede
        // the host construction (the per-window surface dictionary's
        // owner), the pane factory, and the tab seeding. Span-based,
        // because the constructor is one statement list.
        var firstGeometry = Math.Min(applies[0].SpanStart, restores[0].SpanStart);
        foreach (var marker in new[] { "GhosttyHost(", "PaneHostFactory(", "new TabManager(" })
        {
            var site = ctor.DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .FirstOrDefault(n => n.ToString().Contains(marker, StringComparison.Ordinal));
            Assert.True(site is not null, $"constructor no longer constructs {marker}");
            Assert.True(
                firstGeometry < site.SpanStart,
                $"geometry must be applied before {marker}: the creation default " +
                "must never be the size anything computes from");
        }
    }

    [Fact]
    public void TheEarlyApplication_DefersTheMaximizedShow()
    {
        var ctor = PrivateConstructor();

        // Every early call passes applyMaximized: false. The late block's
        // calls pass nothing (the default, true): the show belongs to the
        // block that owns showing.
        var geometryCalls = ctor.Calls("ApplyGeometry")
            .Concat(ctor.Calls("RestoreWindowPlacement"))
            .OrderBy(c => c.SpanStart)
            .ToList();

        Assert.Equal(4, geometryCalls.Count);

        foreach (var call in geometryCalls.Take(2))
        {
            var deferred = call.ArgumentList.Arguments.FirstOrDefault(
                a => a.NameColon?.Name.Identifier.ValueText == "applyMaximized");
            Assert.True(deferred is not null,
                "the early geometry application must name applyMaximized");
            Assert.Equal("false", deferred!.Expression.ToString());
        }

        foreach (var call in geometryCalls.Skip(2))
        {
            var optedOut = call.ArgumentList.Arguments.FirstOrDefault(
                a => a.NameColon?.Name.Identifier.ValueText == "applyMaximized");
            Assert.True(optedOut is null,
                "the late application must keep the default: the maximized show is its job");
        }
    }

    [Fact]
    public void ApplyGeometry_StillShowsMaximizedByDefault()
    {
        var method = ShellSource.Load(MainFile).Method("ApplyGeometry");

        // The switch exists and defaults to showing...
        var param = method.ParameterList.Parameters
            .FirstOrDefault(p => p.Identifier.ValueText == "applyMaximized");
        Assert.NotNull(param);
        Assert.Equal("true", param!.Default?.Value?.ToString());

        // ...and the ShowWindow is gated on it (an ungated call shows a
        // window whose content does not exist yet).
        var show = Assert.Single(method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.ToString().Contains("ShowWindow", StringComparison.Ordinal)));
        // The gate is the enclosing condition: an ungated ShowWindow shows
        // a window whose content does not exist yet.
        var gate = show.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        Assert.NotNull(gate);
        Assert.Contains("applyMaximized", gate.Condition.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The private constructor the public ones delegate to: the one with
    /// the restore and quick-terminal parameters, which is where the
    /// geometry selection lives.
    /// </summary>
    private static ConstructorDeclarationSyntax PrivateConstructor()
    {
        var found = ShellSource.Load(MainFile).Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Where(c => c.ParameterList.Parameters.Count >= 8)
            .ToList();
        return Assert.Single(found);
    }
}
