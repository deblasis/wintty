using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The vertical strip's runtime resource writes keep the resource graph stable
/// (deblasis/wintty#1413).
///
/// A NavigationViewItem's PointerOver and Pressed visual states are
/// deferred: their setters are realized on the first state change that needs
/// them, from {ThemeResource} references captured when the row's template
/// was applied. Every chrome pass used to replace the NavigationView's
/// BackgroundPointerOver and Pressed entries with fresh brushes and to
/// rewrite or remove the rest, and the row's next pointer-over faulted in
/// CDependencyObject::SetParent under CSetter::SetValue, with no app frame
/// on the stack.
///
/// So every runtime write into the NavigationView's resource dictionary goes
/// through one door that adds an entry ONCE, as a brush the strip owns, and
/// after that changes only that brush's Color and Opacity in place. The
/// add-once rule itself is Ghostty.Core's NavOverrideLedger, tested as
/// behaviour in NavOverrideLedgerTests; these rows keep every write on its way
/// to it.
/// "Clearing" an override paints the owned brush with the theme's own value
/// instead of removing it. The rows' own entries keep their existing doors.
///
/// The rule is checked over every dictionary mutation in the file (indexer
/// sets, Add, TryAdd, Insert, Remove, Clear, RemoveAt, a whole-dictionary
/// replacement, and the same through a local alias), so a new write anywhere
/// in it has to go through a door too, and over every other source file for
/// writes that reach the NavigationView from outside the strip.
/// </summary>
public sealed class StripResourceGraphWiringTests
{
    private const string Strip = "Tabs.VerticalTabStrip.xaml.cs";

    public static TheoryData<string, string[]> Doors => new()
    {
        // The vertical strip: the NavView door (the ledger's add delegate,
        // built in NewNavOverrideLedger) and the row doors.
        { Strip, new[] { "NewNavOverrideLedger", "ItemInkBrush", "SetItemResourceIfChanged" } },
    };

    [Theory]
    [MemberData(nameof(Doors))]
    public void ResourceEntries_AreWrittenOnlyInsideTheirDoors(string file, string[] doors)
    {
        var source = ShellSource.Load(file);
        var writes = DictionaryMutations(source.Root).Where(m => m.Kind == MutationKind.Write).ToList();

        // Non-vacuity: the file still writes resources at all, through its doors.
        Assert.NotEmpty(writes);

        var outside = writes
            .Where(w => !InsideAny(w.Node, doors))
            .Select(w => $"{EnclosingName(w.Node)}: {w.Node}")
            .ToList();
        Assert.True(outside.Count == 0,
            $"{file} writes a resource entry outside its doors ({string.Join(", ", doors)}): "
            + string.Join("; ", outside)
            + ". A runtime replacement of an entry a deferred visual-state setter references "
            + "frees the value the setter's {ThemeResource} capture points at, and the row's "
            + "first pointer-over faults in CDependencyObject::SetParent (deblasis/wintty#1413)");
    }

    [Theory]
    [MemberData(nameof(Doors))]
    public void ResourceEntries_AreNeverRemoved(string file, string[] doors)
    {
        _ = doors;
        var source = ShellSource.Load(file);
        var removes = DictionaryMutations(source.Root)
            .Where(m => m.Kind == MutationKind.RemoveOrReplace)
            .Select(m => $"{EnclosingName(m.Node)}: {m.Node}")
            .ToList();
        Assert.True(removes.Count == 0,
            $"{file} removes or replaces a resource entry or dictionary: " + string.Join("; ", removes)
            + ". A removal frees the value a row's deferred {ThemeResource} capture points at; "
            + "paint the owned brush with the theme's value instead (deblasis/wintty#1413)");
    }

    [Fact]
    public void NoOtherFile_MutatesTheNavigationViewsResources()
    {
        // The strip is the only owner of its NavigationView's dictionary. Other
        // code can reach the control (FindName("NavView"), a field), and a write
        // from there would bypass the strip's door.
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var file in ShellSource.AllFiles())
        {
            if (file.Name.EndsWith(Strip, StringComparison.Ordinal)) continue;
            scanned++;
            foreach (var m in DictionaryMutations(file.Root))
            {
                if (ReachesNavView(m.Receiver, file.Root))
                    offenders.Add($"{file.Name} {EnclosingName(m.Node)}: {m.Node}");
            }
        }

        Assert.True(scanned > 0, "no source files were scanned");
        Assert.True(offenders.Count == 0,
            "the NavigationView's resources are mutated outside the vertical strip: "
            + string.Join("; ", offenders)
            + ". Route the write through the strip's door (deblasis/wintty#1413)");
    }

    [Fact]
    public void VerticalStrip_NavDoor_IsTheLedger()
    {
        var source = ShellSource.Load(Strip);

        // The only NavView.Resources write is the ledger's add delegate, and it
        // stores the value the ledger hands it, never a caller's brush.
        var navWrites = DictionaryMutations(source.Root)
            .Where(m => m.Kind == MutationKind.Write && m.Receiver.ToString() == "NavView.Resources")
            .ToList();
        var write = Assert.Single(navWrites).Node;
        Assert.True(InsideAny(write, new[] { "NewNavOverrideLedger" }),
            $"NavView.Resources is written outside the ledger built in NewNavOverrideLedger: {write}");
        var lambda = write.Ancestors().OfType<ParenthesizedLambdaExpressionSyntax>().FirstOrDefault();
        Assert.True(lambda is not null, $"the NavView write must be the ledger's add delegate: {write}");
        var creation = lambda!.Ancestors().OfType<BaseObjectCreationExpressionSyntax>().FirstOrDefault();
        Assert.True(creation is ObjectCreationExpressionSyntax { Type: GenericNameSyntax { Identifier.Text: "NavOverrideLedger" } }
                    && creation.ArgumentList!.Arguments[0].Expression == lambda,
            "the NavView write must be the first argument to new NavOverrideLedger<...>(add, ...)");
        var stored = ((AssignmentExpressionSyntax)write).Right.ToString();
        Assert.Equal(lambda.ParameterList.Parameters[1].Identifier.Text, stored);

        // Writes go through the ledger's Write, clears through its Clear and
        // never through Write (a clear must not add a key the strip never owned).
        var door = source.Method("WriteNavOverride");
        Assert.Single(door.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "_navOverrides.Write");
        var clear = source.Method("ClearNavResource");
        Assert.Contains(clear.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "_navOverrides.Clear");
        Assert.DoesNotContain(clear.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() is "_navOverrides.Write" or "WriteNavOverride" or "SetNavResource");
        Assert.Contains(source.Method("SetNavResource").DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "WriteNavOverride");
    }

    [Fact]
    public void VerticalStrip_HasOneLedgerForItsWholeLife()
    {
        // The add-once rule holds per ledger. A second ledger owns nothing, so
        // every write through it adds again, and the add replaces the entry:
        // the #1413 replacement on every chrome pass. So the strip builds its
        // ledger once, in its constructor, into a readonly field.
        var source = ShellSource.Load(Strip);
        var (variable, field) = source.Field("_navOverrides");
        Assert.True(field.Modifiers.Any(m => m.Text == "readonly"),
            $"_navOverrides must be readonly: '{field}'");
        Assert.Null(variable.Initializer);

        var assignments = source.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() is "_navOverrides" or "this._navOverrides")
            .ToList();
        var only = Assert.Single(assignments);
        Assert.True(only.Ancestors().OfType<ConstructorDeclarationSyntax>().Any(),
            $"_navOverrides is assigned outside the constructor: {EnclosingName(only)}: {only}");
        Assert.Equal("NewNavOverrideLedger()", only.Right.ToString());

        var builds = source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() == "NewNavOverrideLedger")
            .ToList();
        Assert.True(builds.Count == 1 && builds[0].Ancestors().OfType<ConstructorDeclarationSyntax>().Any(),
            "NewNavOverrideLedger must be called once, from the constructor; found: "
            + string.Join("; ", builds.Select(b => $"{EnclosingName(b)}: {b}")));
        var news = source.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(n => n.Type is GenericNameSyntax { Identifier.Text: "NavOverrideLedger" })
            .ToList();
        Assert.True(news.Count == 1 && InsideAny(news[0], new[] { "NewNavOverrideLedger" }),
            "a NavOverrideLedger is created outside NewNavOverrideLedger: "
            + string.Join("; ", news.Select(n => $"{EnclosingName(n)}: {n}")));
    }

    [Fact]
    public void VerticalStrip_EveryKeyItWrites_IsInTheLedgerTests()
    {
        // NavOverrideLedgerTests runs the add-once rule over every key the
        // strip writes. The two lists must agree both ways: a new key in the
        // strip is a key the rule has not been shown to hold for, and a stale
        // entry there is coverage of nothing.
        var source = ShellSource.Load(Strip);
        var consts = source.Root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(m => m.Text == "const"))
            .SelectMany(f => f.Declaration.Variables)
            .Where(v => v.Initializer?.Value is LiteralExpressionSyntax)
            .ToDictionary(v => v.Identifier.Text, v => ((LiteralExpressionSyntax)v.Initializer!.Value).Token.ValueText);

        var written = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var call in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                     .Where(c => c.CalleeText() is "SetNavResource" or "ClearNavResource"))
        {
            var arg = call.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (arg is LiteralExpressionSyntax literal) written.Add(literal.Token.ValueText);
            else if (arg is IdentifierNameSyntax id && consts.TryGetValue(id.Identifier.Text, out var value)) written.Add(value);
            else if (arg is IdentifierNameSyntax { Identifier.Text: "key" }
                     && call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
                        is "SetNavResource" or "ClearNavResource") continue;
            else Assert.Fail($"{EnclosingName(call)}: {call} names its key in a way this row cannot read; use a literal or a const");
        }

        Assert.NotEmpty(written);
        var tested = new SortedSet<string>(Ghostty.Tests.Tabs.NavOverrideLedgerTests.Keys, StringComparer.Ordinal);
        var untested = written.Except(tested).ToList();
        var stale = tested.Except(written).ToList();
        Assert.True(untested.Count == 0 && stale.Count == 0,
            "NavOverrideLedgerTests.Keys and the strip's written keys disagree. Written but not tested: "
            + string.Join(", ", untested) + ". Tested but never written: " + string.Join(", ", stale));
    }
    [Fact]
    public void VerticalStrip_HoverFills_ResolveAtTheStripTheme()
    {
        // The rows' hover and pressed overrides are the strip theme's own
        // dictionary values: the app-level fallback answers at the app theme
        // and handed a dark strip the light theme's near-invisible tint.
        var source = ShellSource.Load(Strip);
        foreach (var method in new[] { "ApplyShellChrome", "ApplyDefaultSelectedTabResources" })
        {
            var calls = source.Method(method).DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
            foreach (var key in new[] { "SubtleFillColorSecondaryBrush", "SubtleFillColorTertiaryBrush" })
            {
                Assert.True(calls.Any(c => c.CalleeText().EndsWith("ResolveStripThemeBrush", StringComparison.Ordinal)
                                           && c.ArgumentList.Arguments.ToString().Contains(key)),
                    $"{method} must resolve {key} at the strip's theme (ResolveStripThemeBrush)");
            }
        }

        // The pinned shelf row's hover wash is the same strip and the same tint.
        Assert.True(source.Method("PaintShelfRow").DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(c => c.CalleeText().EndsWith("ResolveStripThemeBrush", StringComparison.Ordinal)),
            "PaintShelfRow must resolve its hover wash at the strip's theme (ResolveStripThemeBrush)");
    }

    [Fact]
    public void VerticalStrip_TheStripTheme_IsReadInOnePlace()
    {
        // The hover resolve and the clear's theme colour pick the same
        // dictionary set, from one place: StripThemeKeys (table-tested in
        // NavOverrideLedgerTests) fed the strip's ActualTheme and the High
        // Contrast state the window already pushed into _highContrast. The
        // detector runs once, at construction, so a chrome pass does not
        // P/Invoke it again per clear or hover and cannot disagree with the
        // surface the same pass paints.
        var source = ShellSource.Load(Strip);
        foreach (var method in new[] { "ResolveStripThemeBrush", "ThemeDefaultColor" })
        {
            var m = source.Method(method);
            Assert.Contains(m.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                c => c.CalleeText() == "TryFindStripThemeColor");
            var reads = m.DescendantNodes().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.Text).ToHashSet();
            Assert.False(reads.Contains("_elementTheme") || reads.Contains("RequestedTheme"),
                $"{method} must take the strip's theme from StripThemeKeyList, not read _elementTheme or RequestedTheme");
        }

        Assert.Contains(source.Method("TryFindStripThemeColor").DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "StripThemeKeyList");
        var keyList = source.Method("StripThemeKeyList");
        var forCall = Assert.Single(keyList.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "StripThemeKeys.For");
        var args = forCall.ArgumentList.Arguments;
        Assert.Equal("ActualTheme == ElementTheme.Light", args[0].ToString());
        Assert.Equal("_highContrast", args[1].ToString());

        // The window composes the OS state with the config opt-out and
        // pushes it; the strip must not read the raw detector anywhere.
        var (variable, _) = source.Field("_highContrast");
        Assert.Null(variable.Initializer);
        Assert.DoesNotContain("HighContrastDetector", source.Root.ToFullString(), StringComparison.Ordinal);
        var separator = source.Method("SetRowSeparator");
        var writes = separator.AssignsTo("_highContrast").ToList();
        Assert.True(writes.Count == 1 && writes[0].Right.ToString() == "highContrast",
            "SetRowSeparator must be the one writer of _highContrast");

        Assert.DoesNotContain("AccessibilitySettings", source.Root.ToFullString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_RepaintsTheOwnedBrush_InsteadOfRemovingIt()
    {
        // A clear must put the theme's value into the strip's own brush.
        // Naming the ledger call alone is not enough: a no-op paint delegate
        // would pass that and leave the last override colour standing.
        var clear = ShellSource.Load(Strip).Method("ClearNavResource");
        var assignments = clear.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Select(a => a.Left.ToString())
            .Where(left => left.EndsWith(".Color", StringComparison.Ordinal)
                        || left.EndsWith(".Opacity", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, assignments.Count);
        Assert.DoesNotContain(clear.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().EndsWith("Remove", StringComparison.Ordinal));
    }

    [Fact]
    public void NewNavOverrideLedger_PassesTheDictionaryLookup()
    {
        // The commit-then-throw window reopens if the optional lookup is
        // dropped: the next write would add over the entry that landed.
        var build = ShellSource.Load(Strip).Method("NewNavOverrideLedger");
        var creation = build.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(n => n.Type is GenericNameSyntax { Identifier.Text: "NavOverrideLedger" });
        Assert.Equal(3, creation.ArgumentList!.Arguments.Count);
        Assert.Contains(build.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().EndsWith("TryGetValue", StringComparison.Ordinal));
    }

    [Fact]
    public void WriteNavOverride_ChangesTheOwnedBrushInPlace()
    {
        // The door must update the brush it owns, not just call the ledger.
        // An empty update lambda passes every behavioural test in the suite.
        var door = ShellSource.Load(Strip).Method("WriteNavOverride");
        var lambda = door.DescendantNodes().OfType<LambdaExpressionSyntax>()
            .FirstOrDefault(l => l switch
            {
                SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.Text == "owned",
                ParenthesizedLambdaExpressionSyntax paren => paren.ParameterList.Parameters.Count == 1
                    && paren.ParameterList.Parameters[0].Identifier.Text == "owned",
                _ => false,
            });
        Assert.NotNull(lambda);
        var assignments = lambda!.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Select(a => a.Left.ToString())
            .Where(left => left.EndsWith(".Color", StringComparison.Ordinal)
                        || left.EndsWith(".Opacity", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, assignments.Count);
    }

    [Fact]
    public void TryFindThemeColor_WalksTheThemeDictionariesOwnMergedSets()
    {
        // A theme set can delegate part of its palette to a merged
        // dictionary. Stopping at the set's own entries would fall through
        // to the app-pinned colour for exactly those keys.
        var source = ShellSource.Load(Strip);
        Assert.Contains(source.Method("TryFindThemeColor").DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText() == "TryFindResourceColor");
        Assert.Contains(source.Method("TryFindResourceColor").DescendantNodes().OfType<IdentifierNameSyntax>(),
            i => i.Identifier.Text == "MergedDictionaries");
    }

    private enum MutationKind { Write, RemoveOrReplace }

    private readonly record struct Mutation(SyntaxNode Node, ExpressionSyntax Receiver, MutationKind Kind);

    private static readonly string[] DictionarySuffixes = { "Resources", "MergedDictionaries", "ThemeDictionaries" };

    private static bool EndsLikeADictionary(string text)
        => DictionarySuffixes.Any(s => text == s || text.EndsWith("." + s, StringComparison.Ordinal));

    // Every mutation of something dictionary-shaped: a *.Resources,
    // *.MergedDictionaries or *.ThemeDictionaries receiver, an element of one
    // (MergedDictionaries[i]), or a local initialised from one.
    private static IEnumerable<Mutation> DictionaryMutations(SyntaxNode root)
    {
        var aliases = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer?.Value is { } init
                        && (EndsLikeADictionary(Unwrap(init).ToString())
                            || (v.Parent is VariableDeclarationSyntax d && d.Type.ToString() == "ResourceDictionary")))
            .Select(v => v.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);

        bool IsDictionary(ExpressionSyntax e)
        {
            e = Unwrap(e);
            if (e is ElementAccessExpressionSyntax inner && EndsLikeADictionary(inner.Expression.ToString())) return true;
            return EndsLikeADictionary(e.ToString())
                   || (e is IdentifierNameSyntax id && aliases.Contains(id.Identifier.Text));
        }

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case AssignmentExpressionSyntax { Left: ElementAccessExpressionSyntax access } a
                    when IsDictionary(access.Expression):
                    yield return new Mutation(a, access.Expression, MutationKind.Write);
                    break;
                case AssignmentExpressionSyntax a when EndsLikeADictionary(a.Left.ToString()):
                    // A whole dictionary replaced (x.Resources = new ...).
                    yield return new Mutation(a, a.Left, MutationKind.RemoveOrReplace);
                    break;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } call
                    when IsDictionary(member.Expression):
                    var name = member.Name.Identifier.Text;
                    if (name is "Add" or "TryAdd" or "Insert")
                        yield return new Mutation(call, member.Expression, MutationKind.Write);
                    else if (name is "Remove" or "Clear" or "RemoveAt")
                        yield return new Mutation(call, member.Expression, MutationKind.RemoveOrReplace);
                    break;
            }
        }
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax e)
        => e switch
        {
            ParenthesizedExpressionSyntax p => Unwrap(p.Expression),
            CastExpressionSyntax c => Unwrap(c.Expression),
            BinaryExpressionSyntax { RawKind: (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.AsExpression } b => Unwrap(b.Left),
            _ => e,
        };

    // A receiver that names the NavigationView, directly or through a local
    // initialised from something that does (FindName("NavView"), a field).
    private static bool ReachesNavView(ExpressionSyntax receiver, SyntaxNode root)
    {
        if (receiver.ToString().Contains("NavView", StringComparison.Ordinal)) return true;
        var rootName = receiver.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault()?.Identifier.Text;
        if (rootName is null) return false;
        return root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Any(v => v.Identifier.Text == rootName
                      && v.Initializer?.Value.ToString().Contains("NavView", StringComparison.Ordinal) == true);
    }

    private static bool InsideAny(SyntaxNode node, IReadOnlyCollection<string> doors)
        => node.Ancestors().Any(a => a switch
        {
            MethodDeclarationSyntax m => doors.Contains(m.Identifier.Text),
            PropertyDeclarationSyntax p => doors.Contains(p.Identifier.Text),
            _ => false,
        });

    private static string EnclosingName(SyntaxNode node)
        => node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
           ?? node.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
           ?? node.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
           ?? "<member>";
}