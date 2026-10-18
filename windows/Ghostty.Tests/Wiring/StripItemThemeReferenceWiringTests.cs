using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The vertical strip's per-item resource entries are half of what MUXC's
/// NavigationViewItem visual states resolve <c>{ThemeResource}</c>
/// references through, and every item state change -- a selection walk
/// (<c>NavigationView.ChangeSelection</c>) or the coalesced pointer-over
/// flush -- makes MUXC re-resolve the references its deferred visual-state
/// setters have cached.
///
/// A chrome pass that removes and re-adds those entries with freshly
/// allocated brushes therefore orphans the pass-before's resolved values on
/// every selection sync, and a layout switch interleaved with selections
/// then walks a reference whose value CDO the previous pass already
/// dropped, faulting in <c>CDependencyObject::SetParent</c> -- reached
/// identically from the toggle storm, the live vertical click and the
/// close teardown.
///
/// These guards pin the properties that close that class:
///
/// - brushes handed to per-item resources are shared instances, so a
///   rewrite of the same ink is a stable assignment and a removal frees
///   nothing;
/// - the item's four ink keys are written once and never removed while the
///   item lives, and the ink itself lands as an in-place Color write on one
///   per-item brush;
/// - the remaining per-pass resource writes land through the change-guarded
///   door, so an unchanged pass performs no writes at all.
/// </summary>
public sealed class StripItemThemeReferenceWiringTests
{
    private const string BrushFile = "Tabs.TabColorBrush.cs";
    private const string StripFile = "Tabs.VerticalTabStrip.xaml.cs";

    [Fact]
    public void TabColorBrush_CreatesOneInstancePerColour_BehindTheSharedCache()
    {
        var source = ShellSource.Load(BrushFile);

        // Target-typed `new(...)` parses as ImplicitObjectCreationExpression,
        // so both creation forms count; the implicit form carries no type
        // node, and in this file every `new(...)` targets the brush.
        var creations = source.Root.DescendantNodes()
            .OfType<BaseObjectCreationExpressionSyntax>()
            .Where(IsBrushCreation)
            .ToList();

        // Exactly one creation site in the whole file, and it sits inside the
        // cache factory: every public door hands out the shared instance.
        var creation = Assert.Single(creations);
        Assert.True(
            creation.Ancestors().OfType<InvocationExpressionSyntax>()
                .Any(i => i.CalleeText().EndsWith("GetOrAdd", StringComparison.Ordinal)),
            "the single SolidColorBrush creation must live inside the brush cache factory");

        // From and FromPackedRgb are pure doors onto the shared cache. The
        // factory door itself (FromPackedArgb) may hold the one creation,
        // but only inside its GetOrAdd.
        foreach (var door in new[] { "From", "FromPackedRgb" })
        {
            var body = source.Method(door);
            Assert.DoesNotContain(
                body.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>(),
                IsBrushCreation);
        }

        var factory = source.Method("FromPackedArgb");
        var factoryCreations = factory.DescendantNodes()
            .OfType<BaseObjectCreationExpressionSyntax>()
            .Where(IsBrushCreation)
            .ToList();
        Assert.True(
            factoryCreations.Count == 0
                || factoryCreations.All(c => c.Ancestors()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(i => i.CalleeText().EndsWith("GetOrAdd", StringComparison.Ordinal))),
            "the factory door's creation must sit inside its GetOrAdd");
    }

    private static bool IsBrushCreation(BaseObjectCreationExpressionSyntax creation)
    {
        if (creation is ObjectCreationExpressionSyntax explicitForm)
            return explicitForm.Type.ToString().Contains("SolidColorBrush");
        if (creation is ImplicitObjectCreationExpressionSyntax)
        {
            // A target-typed new() is a brush creation unless the target is
            // the cache dictionary's own initializer.
            var declared = creation.Ancestors()
                .OfType<VariableDeclarationSyntax>()
                .FirstOrDefault();
            return declared?.Type.ToString().Contains("ConcurrentDictionary") != true;
        }
        return false;
    }

    [Fact]
    public void MutedInkBrush_PerPassCallers_ReadItThroughTheSharedCache()
    {
        var source = ShellSource.Load(StripFile);

        var cachedDoor = source.Method("MutedInkFor");
        Assert.True(
            cachedDoor.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(i => i.CalleeText().EndsWith("GetOrAdd", StringComparison.Ordinal)),
            "MutedInkFor must hand out the cached instance: the inactive ink lands in "
            + "per-item resource entries the items' visual states resolve through, and a "
            + "fresh brush per pass orphans the previous resolved value");

        var resolve = source.Method("ResolveInactiveTextBrush");
        Assert.Contains(
            resolve.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(c => c.CalleeText()),
            c => c.EndsWith("MutedInkFor", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyItemForeground_NeverRemovesAnItemResourceKey()
    {
        var method = ShellSource.Load(StripFile).Method("ApplyItemForeground");

        // Non-vacuity: the method must still be doing its job -- writing the
        // four ink keys -- for a no-remove rule to mean anything.
        Assert.Contains(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(c => c.CalleeText()),
            c => c.EndsWith("SetItemResourceIfChanged", StringComparison.Ordinal));

        var removes = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("Remove", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            removes.Count == 0,
            "ApplyItemForeground removes item resource entries: a remove destroys the "
            + "binding object the item's cached {ThemeResource} resolutions point at, and "
            + "the next state change walks the dangling reference and faults in "
            + "CDependencyObject::SetParent. The four keys are written once and never "
            + "removed while the item lives");
    }

    [Fact]
    public void ApplyItemForeground_MutatesOnePerItemBrush_InPlace()
    {
        var method = ShellSource.Load(StripFile).Method("ApplyItemForeground");

        // The ink lands as a Color write on the per-item brush, not as a
        // brush replacement: the Color dependency-property write is the one
        // ink change MUXC's theme graph treats natively, and it short-circuits
        // when the colour did not move.
        Assert.True(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(c => c.CalleeText().EndsWith("ItemInkBrush", StringComparison.Ordinal)),
            "ApplyItemForeground must read the per-item ink brush (ItemInkBrush)");
        Assert.Contains(
            method.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString().Contains(".Color"));

        var creator = ShellSource.Load(StripFile).Method("ItemInkBrush");
        Assert.Contains(
            creator.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Select(a => a.Left.ToString()),
            left => left.Contains("ItemInkKey"));
    }

    [Fact]
    public void ApplyItemForeground_WritesItsInkKeys_ThroughTheChangeGuard()
    {
        var method = ShellSource.Load(StripFile).Method("ApplyItemForeground");

        Assert.Empty(method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().Contains("Resources[")));

        Assert.True(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(c => c.CalleeText().EndsWith("SetItemResourceIfChanged", StringComparison.Ordinal)),
            "the ink keys must be written through SetItemResourceIfChanged: a same-instance "
            + "rewrite of a live entry is the write the item's theme graph must never see");
    }

    [Fact]
    public void ApplyItemTabColor_WritesItsBackgroundKeys_ThroughTheChangeGuard()
    {
        var method = ShellSource.Load(StripFile).Method("ApplyItemTabColor");

        Assert.Empty(method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().Contains("Resources[")));

        Assert.True(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(c => c.CalleeText().EndsWith("SetItemResourceIfChanged", StringComparison.Ordinal)),
            "the NavigationViewItemBackgroundSelected* keys must go through "
            + "SetItemResourceIfChanged");
    }

    [Fact]
    public void SetItemResourceIfChanged_ReadsTheCurrentEntry_BeforeItWrites()
    {
        var method = ShellSource.Load(StripFile).Method("SetItemResourceIfChanged");

        var callees = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(c => c.CalleeText())
            .ToList();

        Assert.Contains(callees, c =>
            c.EndsWith("TryGetValue", StringComparison.Ordinal)
            || c.EndsWith("ContainsKey", StringComparison.Ordinal));
        Assert.Contains(callees, c => c.EndsWith("ReferenceEquals", StringComparison.Ordinal));
    }
}
