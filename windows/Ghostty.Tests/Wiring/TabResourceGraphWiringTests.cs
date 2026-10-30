using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The horizontal strip's runtime resource writes keep the TabView graph
/// stable, the same rule the vertical strip follows (deblasis/wintty#1413,
/// #1427).
///
/// A TabViewItem's header states are deferred: their setters resolve
/// <c>{ThemeResource}</c> references captured when the item's template was
/// applied. Replacing or removing an entry after that frees the value such a
/// capture points at. So every TabView entry is added once and only its Color
/// changes after that, and every item entry is one brush the item owns for
/// its life.
/// </summary>
public sealed class TabResourceGraphWiringTests
{
    private const string Host = "Tabs.TabHost.xaml.cs";

    [Fact]
    public void TabViewEntries_AreWrittenOnlyInsideTheLedger()
    {
        var source = ShellSource.Load(Host);
        var writes = ElementWrites(source.Root, "TabViewControl.Resources").ToList();

        var write = Assert.Single(writes);
        Assert.True(Inside(write, "NewTabViewLedger"),
            $"TabViewControl.Resources is written outside the ledger built in NewTabViewLedger: {write}");
    }

    [Fact]
    public void ItemEntries_AreWrittenOnlyInsideTheOwnedBrushFactories()
    {
        var source = ShellSource.Load(Host);
        var writes = ElementWrites(source.Root, "item.Resources").ToList();

        // Two doors: one for the copied brushes, one for the field's
        // animating brush. A new write anywhere else is a new entry the
        // deferred setters did not capture.
        Assert.Equal(2, writes.Count);
        foreach (var write in writes)
        {
            Assert.True(Inside(write, "OwnedItemBrush", "SetItemFieldBrush"),
                $"item.Resources is written outside the owned-brush factories: {write}");
        }
    }

    [Fact]
    public void TabHost_NeverRemovesAResourceEntry()
    {
        var text = ShellSource.Load(Host).Root.ToFullString();
        Assert.DoesNotContain("TabViewControl.Resources.Remove", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TabViewControl.Resources.Clear", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TabViewControl.Resources.Add", text, StringComparison.Ordinal);
        Assert.DoesNotContain("item.Resources.Remove", text, StringComparison.Ordinal);
        Assert.DoesNotContain("item.Resources.Clear", text, StringComparison.Ordinal);
        Assert.DoesNotContain("item.Resources.Add", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTabViewResource_IsTheOneDoor()
    {
        var source = ShellSource.Load(Host);

        var write = source.Method("WriteTabViewResource");
        Assert.Single(write.CallsEndingWith("_tabViewOverrides.Write"));
        Assert.Empty(write.CallsEndingWith("_tabViewOverrides.Clear"));

        var clear = source.Method("ClearTabViewResource");
        Assert.Single(clear.CallsEndingWith("_tabViewOverrides.Clear"));
        Assert.Empty(clear.CallsEndingWith("_tabViewOverrides.Write"));
        Assert.Empty(clear.CallsEndingWith("WriteTabViewResource"));
    }

    [Fact]
    public void OwnedItemBrush_AddsOnceAndReturnsTheSameInstance()
    {
        var method = ShellSource.Load(Host).Method("OwnedItemBrush");
        var write = Assert.Single(ElementWrites(method, "item.Resources"));
        var lookup = Assert.Single(method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() == "map.TryGetValue"));
        Assert.True(lookup.SpanStart < write.SpanStart,
            "the owned brush must be looked up before the entry is first written");
    }

    [Fact]
    public void SetItemHeaderBrush_MutatesTheOwnedBrushInPlace()
    {
        var method = ShellSource.Load(Host).Method("SetItemHeaderBrush");
        var owned = Assert.Single(method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() == "OwnedItemBrush"));
        // Only the colour is handed to the factory: the caller's brush is
        // never stored, so a shared TabColorBrush cache instance can never
        // end up in an item's resources.
        Assert.Equal("brush.Color", owned.ArgumentList.Arguments[2].ToString());

        var assignments = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Select(a => a.Left.ToString())
            .Where(left => left.EndsWith(".Color", StringComparison.Ordinal)
                        || left.EndsWith(".Opacity", StringComparison.Ordinal))
            .ToList();
        // Two per branch: the supplied value and the theme fallback both
        // paint the owned brush instead of touching the dictionary.
        Assert.Equal(4, assignments.Count);
        Assert.Empty(ElementWrites(method, "item.Resources"));
    }

    [Fact]
    public void SetItemFieldBrush_InsertsOnceAndNeverReplaces()
    {
        var method = ShellSource.Load(Host).Method("SetItemFieldBrush");
        var write = Assert.Single(ElementWrites(method, "item.Resources"));
        var lookup = Assert.Single(method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() == "map.TryGetValue"));
        Assert.True(lookup.SpanStart < write.SpanStart,
            "the field brush must be inserted after the existing-entry lookup");

        // An entry that is already there is painted, not replaced.
        Assert.Contains(method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "ReferenceEquals");
    }

    [Fact]
    public void SelectedHeaderKeys_TakeTheFieldBrush()
    {
        var source = ShellSource.Load(Host);
        var chrome = source.Method("ApplyTabChrome");
        var apply = Assert.Single(chrome.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() == "ApplyTabViewItemHeaderBrushes"));
        Assert.Equal("_field.Brush", apply.ArgumentList.Arguments[2].ToString());

        var door = source.Method("ApplyTabViewItemHeaderBrushes");
        Assert.Contains(door.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "SetItemFieldBrush");
    }

    [Fact]
    public void TabHost_HasOneLedgerForItsWholeLife()
    {
        var source = ShellSource.Load(Host);
        var (variable, field) = source.Field("_tabViewOverrides");
        Assert.True(field.Modifiers.Any(m => m.Text == "readonly"),
            $"_tabViewOverrides must be readonly: '{field}'");
        Assert.Null(variable.Initializer);

        var assignments = source.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() is "_tabViewOverrides" or "this._tabViewOverrides")
            .ToList();
        var only = Assert.Single(assignments);
        Assert.True(only.Ancestors().OfType<ConstructorDeclarationSyntax>().Any(),
            "_tabViewOverrides is assigned outside the constructor");
        Assert.Equal("NewTabViewLedger()", only.Right.ToString());

        var creations = source.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(n => n.Type is GenericNameSyntax { Identifier.Text: "NavOverrideLedger" })
            .ToList();
        Assert.True(creations.Count == 1 && Inside(creations[0], "NewTabViewLedger"),
            "a NavOverrideLedger is created outside NewTabViewLedger");
    }

    [Fact]
    public void NewTabViewLedger_PassesTheDictionaryLookup()
    {
        // The commit-then-throw window reopens if the optional lookup is
        // dropped: the next write would add over the entry that landed.
        var build = ShellSource.Load(Host).Method("NewTabViewLedger");
        var creation = build.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(n => n.Type is GenericNameSyntax { Identifier.Text: "NavOverrideLedger" });
        Assert.Equal(3, creation.ArgumentList!.Arguments.Count);
        Assert.Contains(build.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().EndsWith("TryGetValue", StringComparison.Ordinal));
    }

    [Fact]
    public void TabHost_ReadsHighContrastOnlyFromTheWindow()
    {
        // The window composes the OS state with the config opt-out; a raw
        // detector read here would bypass that and disagree with the surface
        // the same pass paints.
        var source = ShellSource.Load(Host);
        Assert.DoesNotContain("HighContrastDetector", source.Root.ToFullString(), StringComparison.Ordinal);

        var (variable, _) = source.Field("_highContrast");
        Assert.Null(variable.Initializer);
        var ground = source.Method("SetChromeGround");
        var writes = ground.AssignsTo("_highContrast").ToList();
        Assert.True(writes.Count == 1 && writes[0].Right.ToString() == "highContrast",
            "SetChromeGround must be the one writer of _highContrast");
    }

    [Fact]
    public void SetChromeFill_RetriesASwallowedWriteOrClear()
    {
        var method = ShellSource.Load(Host).Method("SetChromeFill");
        var retries = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_chromeFillRetry"
                     && a.Right.ToString().StartsWith("!", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, retries.Count);
        Assert.Contains(retries, a => a.Right.ToString().StartsWith(
            "!WriteTabViewResource(\"TabViewBackground\"", StringComparison.Ordinal));
        Assert.Contains(retries, a => a.Right.ToString()
            == "!ClearTabViewResource(\"TabViewBackground\")");
        var memo = method.DescendantNodes().OfType<IfStatementSyntax>().First();
        Assert.Contains("_chromeFillSurface", memo.Condition.ToString());
    }

    [Fact]
    public void SetChromeFill_RefreshesABareSurfaceOnAThemeMove()
    {
        // A steady null fill can still move under a High Contrast theme swap
        // or an element theme flip. The resolved surface value is part of the
        // fill memo, so the next push re-resolves the bare surface even when
        // the dictionary set name is unchanged.
        var source = ShellSource.Load(Host);
        var method = source.Method("SetChromeFill");
        var memo = method.DescendantNodes().OfType<IfStatementSyntax>().First();
        Assert.Contains("_chromeFillSurface", memo.Condition.ToString());
        Assert.Contains(method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "TabViewSurfaceTheme");

        var surface = source.Method("TabViewSurfaceTheme");
        Assert.Contains(surface.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "TryThemeBrush");
        Assert.Contains(surface.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "ThemeKey");
        Assert.Contains(surface.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "color" && a.Right.ToString() == "solid.Color");
        Assert.Contains(surface.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "opacity" && a.Right.ToString() == "solid.Opacity");
        Assert.Contains(surface.DescendantNodes().OfType<TupleExpressionSyntax>(),
            t => t.Arguments.Count == 3 && t.Arguments[0].ToString() == "ThemeKey()");

        // The memo has to be assigned the tuple it just compared, or the
        // comparison is against a value that never moves.
        Assert.Contains(method.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "_chromeFillSurface"
                 && a.Right.ToString() == "surface");
    }

    [Fact]
    public void ClearShellTheme_ForcesTheNextFillPush()
    {
        // ClearShellTheme drops the cached fill; without arming the retry the
        // next same-null push would return on the memo and keep the old fill.
        var clear = ShellSource.Load(Host).Method("ClearShellTheme");
        Assert.Contains(clear.AssignsTo("_chromeFillRetry"),
            a => a.Right.ToString() == "true");
    }

    [Fact]
    public void ClearTabViewResource_PaintsTheThemeValue()
    {
        var clear = ShellSource.Load(Host).Method("ClearTabViewResource");
        Assert.Contains(clear.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "_tabViewOverrides.Clear");
        Assert.Contains(clear.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "TryThemeBrush");
        // The door must not short-circuit on Owns: a pending committed brush
        // is not in the ledger's map yet, and Clear is what adopts it.
        Assert.Empty(clear.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText().EndsWith("Owns", StringComparison.Ordinal)));
        var assignments = clear.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Select(a => a.Left.ToString())
            .Where(left => left.EndsWith(".Color", StringComparison.Ordinal)
                        || left.EndsWith(".Opacity", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, assignments.Count);
    }

    [Fact]
    public void OwnedItemBrush_AdoptsACommittedThenThrownInstall()
    {
        var method = ShellSource.Load(Host).Method("OwnedItemBrush");
        Assert.NotEmpty(method.DescendantNodes().OfType<TryStatementSyntax>());
        Assert.Contains(method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().EndsWith("TryGetValue", StringComparison.Ordinal));
        Assert.Contains(method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "ReferenceEquals");
    }

    [Fact]
    public void SetItemFieldBrush_AdoptsACommittedThenThrownInstall()
    {
        var method = ShellSource.Load(Host).Method("SetItemFieldBrush");
        Assert.NotEmpty(method.DescendantNodes().OfType<TryStatementSyntax>());
        Assert.Contains(method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().EndsWith("TryGetValue", StringComparison.Ordinal));
        Assert.Contains(method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "ReferenceEquals");
    }

    [Fact]
    public void ThemeKey_ReadsTheComposedHighContrastFlag()
    {
        var key = ShellSource.Load(Host).Method("ThemeKey");
        Assert.Contains(key.DescendantNodes().OfType<IdentifierNameSyntax>(),
            i => i.Identifier.Text == "_highContrast");
        Assert.Contains(key.DescendantNodes().OfType<LiteralExpressionSyntax>(),
            l => l.Token.ValueText == "HighContrast");
    }

    [Fact]
    public void TabHost_MarksItsResourcesDirtyOnAnAddAndOnARefusedReread()
    {
        var root = ShellSource.Load(Host).Root;
        var marks = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_tabViewResourcesDirty" && a.Right.ToString() == "true")
            .ToList();

        var add = Assert.Single(marks.Where(m => Inside(m, "WriteTabViewResource")));
        var addGuard = add.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        Assert.NotNull(addGuard);
        Assert.Equal("added", addGuard!.Condition.ToString());

        // Both item doors mark on a first insert; that is what makes the
        // template re-read an entry added after its template was applied.
        Assert.Single(marks.Where(m => Inside(m, "OwnedItemBrush")));
        Assert.Single(marks.Where(m => Inside(m, "SetItemFieldBrush")));

        // A refused enqueue already cleared the memo; the request must stay
        // owed so the entry is not left unread until a later add.
        var refused = Assert.Single(marks.Where(m => Inside(m, "RefreshTabViewTheme")));
        var refusedGuard = refused.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        Assert.NotNull(refusedGuard);
        Assert.Contains("enqueue", refusedGuard!.Condition.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<SyntaxNode> ElementWrites(SyntaxNode root, string receiver)
        => root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is ElementAccessExpressionSyntax access
                        && access.Expression.ToString() == receiver);

    private static bool Inside(SyntaxNode node, params string[] methods)
        => node.Ancestors().OfType<MethodDeclarationSyntax>()
            .Any(m => methods.Contains(m.Identifier.Text));
}
