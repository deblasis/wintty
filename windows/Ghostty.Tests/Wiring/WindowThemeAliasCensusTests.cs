using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Ghostty.Core.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// One home for the <c>window-theme</c> alias rule, enforced by sweeping the
/// managed sources for anything that compares a config value against
/// "wintty" or "ghostty" outside <c>WindowThemeAlias</c>.
///
/// WHAT THIS DOES NOT COVER, and cannot:
///
/// <list type="bullet">
/// <item>The rule as written without either literal.
/// <c>ThemeResolution.ResolveIsDark</c> and <c>TracksSystem</c> route both
/// spellings through a <c>_</c> discard arm, so neither string is there to
/// match and this sweep is blind to an implementation of the very rule it
/// polices. A census reporting "no violations" over a file like that is the
/// false confidence guards like this exist to prevent, so both spellings are
/// pinned through those two functions by explicit behaviour tests in
/// <c>WindowThemeAliasTests</c> instead. The same blindness covers any
/// comparison that reaches the spelling through a const or a variable.</item>
/// <item>XAML. Roslyn does not read markup, and the combo entry the settings
/// page offers carries the value in <c>Tag="wintty"</c>. One targeted
/// XDocument check below pins that one combo; every other <c>.xaml</c> in the
/// tree is unread by anything here.</item>
/// <item>Sources outside the two embedded source prefixes.
/// <c>NativeMethods.cs</c> and <c>LibGhosttyBuildInfo.cs</c> are embedded
/// under a different logical name for the interop tests and are not in this
/// sweep. Neither is the Zig side, the C header, or the config
/// documentation.</item>
/// <item>Conditional regions guarded by a symbol other than <c>DEMO</c>.
/// Each file is parsed twice, with DEMO defined and undefined, so both sides
/// of a DEMO branch are seen; a region under any other symbol is disabled
/// trivia in both parses and is not read at all.</item>
/// </list>
/// </summary>
public sealed class WindowThemeAliasCensusTests
{
    private const string SourcePrefix = "Ghostty.Tests.Interop.Sources.Ghostty.";

    /// <summary>The file the rule is allowed to live in.</summary>
    private const string Home = "Core.Config.WindowThemeAlias.cs";

    private static readonly string[] AliasSpellings = ["wintty", "ghostty"];

    /// <summary>
    /// Method names whose literal argument is being compared against
    /// something, as opposed to being passed as data.
    /// </summary>
    private static readonly string[] Comparers =
        ["Equals", "Compare", "CompareOrdinal", "CompareTo", "Contains", "StartsWith", "EndsWith", "IndexOf"];

    /// <summary>
    /// A comparison the sweep finds and is right not to fail on, keyed by
    /// the file and by what is being compared. Keyed on the subject rather
    /// than on the literal because the literal is identical either way:
    /// <c>Program.cs</c> asks the same question of a library name that
    /// <c>ShellThemeService</c> used to ask of a config value.
    ///
    /// Every entry has to still match something. An exemption whose site has
    /// moved on is a hole left open for the next comparison that lands in
    /// that file.
    /// </summary>
    private static readonly Exemption[] NotConfigComparisons =
    [
        new("Program.cs", "name",
            "the native library name a DllImport resolver was asked to load"),
        new("Core.Version.KittyLogo.cs", "termProgram",
            "the TERM_PROGRAM value a shell inherited, which also names WezTerm"),
        new("Core.Config.ThemeSearchPath.cs", "AppDirectoryNames",
            "on-disk config directory names, both spellings of which still exist"),
        new("Core.Shell.LaunchTextureSource.cs", "AppDirectoryNames",
            "on-disk asset directory names, same two directories"),
    ];

    private sealed record Exemption(string File, string Subject, string Why);

    private sealed record Site(string File, int Line, string Subject, string Source);

    [Fact]
    public void Nothing_outside_the_alias_compares_a_config_value_to_either_spelling()
    {
        var offenders = ComparisonSites()
            .Where(s => s.File != Home)
            .Where(s => !NotConfigComparisons.Any(e => e.File == s.File && e.Subject == s.Subject))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "window-theme alias comparisons outside " + Home + ":\n"
                + string.Join("\n", offenders.Select(o => $"  {o.File}({o.Line}): {o.Source}"))
                + "\nRoute these through WindowThemeAlias.IsPaletteHued, or add an "
                + "exemption saying what non-config value is being compared.");
    }

    [Fact]
    public void Every_exemption_still_names_a_real_site()
    {
        var sites = ComparisonSites();

        var stale = NotConfigComparisons
            .Where(e => !sites.Any(s => s.File == e.File && s.Subject == e.Subject))
            .ToList();

        Assert.True(
            stale.Count == 0,
            "exemptions that no longer match anything, so they only widen the sweep's "
                + "blind spot: "
                + string.Join(", ", stale.Select(e => $"{e.File} ({e.Subject}: {e.Why})")));
    }

    /// <summary>
    /// A sweep that stopped matching reports the same "no violations" as a
    /// clean tree. Name the files the rule is actually about, including the
    /// two in <c>Ghostty.Core</c> that a shell-only enumeration would miss.
    /// </summary>
    [Fact]
    public void The_sweep_reaches_the_files_the_rule_is_about()
    {
        var files = Corpus().Select(c => c.File).Distinct().ToList();

        Assert.Contains(Home, files);
        Assert.Contains("Program.cs", files);
        Assert.Contains("Services.ShellThemeService.cs", files);
        Assert.Contains("Settings.Pages.AppearancePage.xaml.cs", files);
        Assert.Contains("Core.Version.KittyLogo.cs", files);
        Assert.Contains("Core.Windows.ThemeResolution.cs", files);

        // The matcher recognises the canonical shape, in the one file
        // entitled to it. Without this, "no violations" elsewhere could as
        // easily mean the matcher stopped recognising comparisons.
        Assert.Contains(ComparisonSites(), s => s.File == Home);
    }

    /// <summary>
    /// The census only proves the literals are gone. A predicate deleted
    /// outright passes it just as well as one routed, so pin the routing.
    /// </summary>
    [Fact]
    public void The_shell_theme_predicate_is_the_shared_one()
    {
        var property = ShellSource.Load("Services.ShellThemeService.cs").Root
            .DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "IsEnabled");

        var body = property.ExpressionBody;
        Assert.NotNull(body);

        var call = body!.Expression.AssertCallTo("WindowThemeAlias.IsPaletteHued");
        Assert.Equal("_configService.WindowTheme", call.Arg(0));
    }

    [Fact]
    public void The_settings_combo_folds_the_alias_with_the_shared_helper()
    {
        var method = ShellSource.Load("Settings.Pages.AppearancePage.xaml.cs")
            .Method("SelectWindowTheme");

        var call = method.Call("WindowThemeAlias.Canonicalize");
        Assert.Equal("theme", call.Arg(0));
    }

    /// <summary>
    /// The one XAML site, checked as markup because the sweep above cannot
    /// see it at all. The combo has to offer the spelling
    /// <see cref="WindowThemeAlias.Canonicalize"/> produces, or the fold
    /// lands on a tag that is not there and the page falls through to the
    /// first item.
    /// </summary>
    [Fact]
    public void The_window_theme_combo_carries_the_canonical_spelling()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Ghostty.Tests.Settings.Pages.AppearancePage.xaml");
        Assert.NotNull(stream);

        var tags = XDocument.Load(stream!).Descendants()
            .Single(e => e.Attribute(xaml + "Name")?.Value == "WindowThemeCombo")
            .Elements()
            .Select(e => e.Attribute("Tag")?.Value)
            .ToList();

        Assert.Contains(WindowThemeAlias.Canonicalize("ghostty"), tags);
        Assert.DoesNotContain("ghostty", tags);
    }

    private static List<Site> ComparisonSites()
    {
        var found = new List<Site>();
        foreach (var (file, tree) in Corpus())
        {
            foreach (var literal in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>())
            {
                if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) continue;
                if (!AliasSpellings.Contains(literal.Token.ValueText, StringComparer.OrdinalIgnoreCase))
                    continue;

                var subject = SubjectOf(literal);
                if (subject is null) continue;

                found.Add(new Site(
                    file,
                    tree.GetLineSpan(literal.Span).StartLinePosition.Line + 1,
                    subject,
                    (literal.Parent?.Parent ?? literal.Parent ?? literal).ToString()));
            }
        }

        // Both parses see the same site, and a file can be swept twice
        // without being reported twice.
        return found.DistinctBy(s => (s.File, s.Line, s.Subject)).ToList();
    }

    /// <summary>
    /// What <paramref name="literal"/> is being compared against, or null
    /// when it is not in a comparison at all.
    ///
    /// Collection elements count. A comparison written as
    /// <c>Aliases.Contains(theme)</c> puts the two spellings in an
    /// initializer and the comparison somewhere else entirely, which is a
    /// straightforward way around a sweep that only looks at operands.
    /// </summary>
    private static string? SubjectOf(LiteralExpressionSyntax literal)
    {
        if (literal.FirstAncestorOrSelf<PatternSyntax>() is { } pattern)
        {
            return pattern.Ancestors()
                .Select(a => a switch
                {
                    IsPatternExpressionSyntax e => e.Expression.ToString(),
                    SwitchExpressionSyntax e => e.GoverningExpression.ToString(),
                    SwitchStatementSyntax e => e.Expression.ToString(),
                    _ => null,
                })
                .FirstOrDefault(s => s is not null);
        }

        switch (literal.Parent)
        {
            case BinaryExpressionSyntax binary
                when binary.IsKind(SyntaxKind.EqualsExpression)
                    || binary.IsKind(SyntaxKind.NotEqualsExpression):
                return (binary.Left == literal ? binary.Right : binary.Left).ToString();

            case CaseSwitchLabelSyntax label:
                return label.FirstAncestorOrSelf<SwitchStatementSyntax>()?.Expression.ToString();

            case ArgumentSyntax argument
                when argument.Parent is ArgumentListSyntax list
                    && list.Parent is InvocationExpressionSyntax call
                    && IsComparer(call.CalleeText()):
                return OtherOperandOf(list, literal)
                    ?? (call.Expression as MemberAccessExpressionSyntax)?.Expression.ToString();

            // ["wintty", "ghostty"] and new[] { "wintty", "ghostty" }.
            case ExpressionElementSyntax element when element.Parent is CollectionExpressionSyntax:
            case InitializerExpressionSyntax:
                return NamedOwnerOf(literal);
        }

        return null;
    }

    private static bool IsComparer(string callee) =>
        Comparers.Contains(callee.Split('.').Last(), StringComparer.Ordinal);

    /// <summary>
    /// The first argument that is neither the literal itself nor the
    /// <c>StringComparison</c> the call was given, which is the value under
    /// test in every overload the tree uses.
    /// </summary>
    private static string? OtherOperandOf(ArgumentListSyntax list, LiteralExpressionSyntax literal) =>
        list.Arguments
            .Where(a => a.Expression != literal)
            .Select(a => a.Expression.ToString())
            .FirstOrDefault(s => !s.StartsWith("StringComparison.", StringComparison.Ordinal));

    private static string NamedOwnerOf(SyntaxNode node) =>
        node.FirstAncestorOrSelf<VariableDeclaratorSyntax>()?.Identifier.ValueText
            ?? node.FirstAncestorOrSelf<PropertyDeclarationSyntax>()?.Identifier.ValueText
            ?? node.Parent!.ToString();

    /// <summary>
    /// Every embedded managed source, parsed once with <c>DEMO</c> defined
    /// and once without.
    ///
    /// Deliberately not <c>ShellSource.AllShellSources</c>, which excludes
    /// <c>Ghostty.Core</c>: two of the four sites this rule is about live
    /// there, and a sweep that never reads them would report them clean.
    /// </summary>
    private static List<(string File, SyntaxTree Tree)> Corpus()
    {
        var sources = ShellSource.AllUnder(SourcePrefix);
        Assert.True(sources.Count > 0, "no embedded managed sources found under " + SourcePrefix);

        var parsed = new List<(string, SyntaxTree)>();
        foreach (var (tail, text) in sources)
        {
            foreach (var symbols in new[] { new[] { "DEMO" }, Array.Empty<string>() })
            {
                parsed.Add((
                    tail,
                    CSharpSyntaxTree.ParseText(
                        text, new CSharpParseOptions(preprocessorSymbols: symbols))));
            }
        }

        return parsed;
    }
}
