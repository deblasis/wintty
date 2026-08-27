using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Four separate literals used to stand in for the background-style
/// default, and one of them disagreed: the settings combo fell through to
/// its first item, which is solid, while every other reader fell back to
/// frosted. A config the page could not match therefore showed a material
/// the window was not drawing.
///
/// These pin the four sites onto <c>BackdropStyles.Default</c> and onto
/// the validating normaliser. The shell assembly cannot be loaded into a
/// test host, so this reads the source; it is parsed rather than searched
/// so a rewrite that keeps the name and drops the behaviour still moves
/// the tree.
/// </summary>
public sealed class BackdropStyleDefaultWiringTests
{
    private const string Default = "BackdropStyles.Default";

    private static ShellSource ConfigService() => ShellSource.Load("Services.ConfigService.cs");

    private static ShellSource AppearancePage() =>
        ShellSource.Load("Settings.Pages.AppearancePage.xaml.cs");

    [Fact]
    public void The_BackgroundStyle_property_starts_at_the_shared_default()
    {
        var property = ConfigService().Root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "BackgroundStyle");

        Assert.NotNull(property.Initializer);
        Assert.Equal(Default, property.Initializer!.Value.ToString());
    }

    /// <summary>
    /// The read has two halves that can drift apart: what an absent key
    /// falls back to, and what an unusable one falls back to. Both are
    /// asserted, because a normaliser wrapping a hardcoded default still
    /// leaves two answers in the file.
    /// </summary>
    [Fact]
    public void The_background_style_read_normalises_and_defaults_through_the_constant()
    {
        var read = ConfigService().Method("ReadFlagsCore").Calls("NormalizeStyle")
            .Single(c => c.Arg(0) == "\"background-style\"");

        var inner = read.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText() == "GetFileValue");
        Assert.Equal("\"background-style\"", inner.Arg(0));
        Assert.Equal(Default, inner.Arg(1));
    }

    [Fact]
    public void NormalizeStyle_folds_through_BackdropStyles_and_reports_what_it_rejected()
    {
        var method = ConfigService().Method("NormalizeStyle");

        var fold = method.Call("BackdropStyles.TryNormalize");
        Assert.Equal("raw", fold.Arg(0));

        // Naming the key is the whole point of routing the read through a
        // method instead of calling TryNormalize inline: by the time the
        // value reaches the backdrop switch there is no key left to name.
        var report = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText().EndsWith("LogUnknownBackdropStyle", System.StringComparison.Ordinal));
        Assert.Equal("key", report.Arg(0));
        Assert.Equal("raw", report.Arg(1));
    }

    /// <summary>
    /// The defect itself. An unmatched tag must land on the caller's
    /// stated fallback, and only fall to index 0 when that is absent too.
    /// </summary>
    [Fact]
    public void SelectComboByTag_prefers_a_stated_fallback_over_the_first_item()
    {
        var method = AppearancePage().Method("SelectComboByTag");

        Assert.Single(
            method.ParameterList.Parameters,
            p => p.Identifier.ValueText == "fallbackTag");

        var guard = method.Body!.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("fallbackTag", System.StringComparison.Ordinal));

        var firstItem = method.Body!.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString().EndsWith("SelectedIndex", System.StringComparison.Ordinal));

        Assert.True(
            guard.SpanStart < firstItem.SpanStart,
            "SelectedIndex = 0 runs before the fallback tag is consulted, so the fallback is dead code");
    }

    [Fact]
    public void The_backdrop_combo_is_seeded_and_saved_against_the_shared_default()
    {
        var page = AppearancePage();

        var seeds = page.Root.Calls("SelectComboByTag")
            .Where(c => c.Arg(0) == "BackgroundStyleCombo")
            .ToList();
        Assert.Equal(2, seeds.Count);

        // One seed carries a config value plus the fallback; the other has
        // no config to read and names the default outright.
        var fromConfig = Assert.Single(seeds, c => c.ArgumentList.Arguments.Count == 3);
        Assert.Equal(Default, fromConfig.Arg(2));
        var withoutConfig = Assert.Single(seeds, c => c.ArgumentList.Arguments.Count == 2);
        Assert.Equal(Default, withoutConfig.Arg(1));

        var write = page.Method("BackgroundStyle_SelectionChanged").Call("OnValueChanged");
        Assert.Equal("\"background-style\"", write.Arg(0));
        Assert.Contains(Default, write.Arg(1), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A literal that survives is a fifth default waiting to disagree with
    /// the other four. Scoped to the two files the constant replaced
    /// literals in; the combo's own XAML tags stay literal because XAML
    /// cannot name a C# constant.
    /// </summary>
    [Theory]
    [InlineData("Services.ConfigService.cs")]
    [InlineData("Settings.Pages.AppearancePage.xaml.cs")]
    public void No_bare_style_literal_survives(string file)
    {
        var literals = ShellSource.Load(file).Root.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(l => l.Token.ValueText)
            .Where(v => v is "frosted" or "crystal" or "solid")
            .ToList();

        Assert.True(
            literals.Count == 0,
            $"{file} still spells a backdrop style as a literal: " + string.Join(", ", literals));
    }
}
