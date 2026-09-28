using System;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Xml.Linq;
using Ghostty.Core.Config;
using Ghostty.Core.Settings;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// The Animations card in Settings, Appearance: exactly three options
/// (Follow system, Reduced, Off, in that order, Follow system the
/// default), no scale control beside them, and the registry entries that
/// make the card reachable: a Windows-only key registration, a search
/// index entry on the right page and section, and a code-behind that
/// seeds the combo from the file and writes the tag back.
///
/// The markup is parsed as XML (a key inside a comment is not a card) and
/// the code-behind with Roslyn (a control name in a comment is not a
/// seed), the same way the sibling parity tests read these pages.
/// </summary>
public class AnimationsCardTests
{
    private const string Resource = "Ghostty.Tests.Settings.Pages.AppearancePage.xaml";

    // The XAML prefix is an alias, but the namespace it resolves to is
    // what identifies the attached property (see the parity tests).
    private static readonly XNamespace ControlsNamespace = "using:Ghostty.Controls.Settings";
    private const string ConfigKeyName = "SettingsCard.ConfigKey";

    private static XDocument PageXaml()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(Resource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return XDocument.Parse(reader.ReadToEnd());
    }

    private static XElement? AnimationsGroup() => PageXaml().Descendants()
        .FirstOrDefault(e => e.Name.LocalName == "SettingsGroup"
                             && (string?)e.Attribute("Header") == "Animations");

    private static XElement? AnimationsCard(XElement group) => group.Descendants()
        .FirstOrDefault(e => e.Name.LocalName == "SettingsCard"
                             && (string?)e.Attribute(ControlsNamespace + ConfigKeyName) == "animations");

    [Fact]
    public void The_card_lives_in_an_Animations_group()
    {
        var group = AnimationsGroup();
        Assert.NotNull(group);
        Assert.NotNull(AnimationsCard(group!));
    }

    [Fact]
    public void The_card_carries_exactly_one_combo_with_the_three_rungs_in_order()
    {
        var card = AnimationsCard(AnimationsGroup()!);
        Assert.NotNull(card);

        var combos = card!.Descendants().Where(e => e.Name.LocalName == "ComboBox").ToList();
        Assert.Single(combos);

        var items = combos[0].Elements()
            .Where(e => e.Name.LocalName == "ComboBoxItem")
            .ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(new[] { "system", "reduced", "off" },
            items.Select(i => (string?)i.Attribute("Tag")));
        Assert.Equal(new[] { "Follow system", "Reduced", "Off" },
            items.Select(i => (string?)i.Attribute("Content")));
    }

    [Fact]
    public void The_first_option_is_Follow_system()
    {
        var card = AnimationsCard(AnimationsGroup()!);
        var first = card!.Descendants()
            .First(e => e.Name.LocalName == "ComboBoxItem");
        Assert.Equal("system", (string?)first.Attribute("Tag"));
        Assert.Equal("Follow system", (string?)first.Attribute("Content"));
    }

    [Fact]
    public void The_group_carries_no_scale_control()
    {
        // The scale slider was a rejected design; its absence is part of
        // the card. Nothing in the Animations group may slide.
        var group = AnimationsGroup();
        Assert.DoesNotContain(
            group!.Descendants(), e => e.Name.LocalName == "Slider");
    }

    [Fact]
    public void The_key_is_registered_as_windows_only()
        => Assert.True(WindowsOnlyKeys.Contains("animations"),
            "the animations key is fork-side; without the registry entry " +
            "libghostty reports it as an unknown field on every parse");

    [Fact]
    public void The_setting_is_indexed_on_the_Appearance_page_under_the_Animations_group()
    {
        var entry = Assert.Single(SettingsIndex.All, e => e.Key == "animations");
        Assert.Equal("Appearance", entry.Page);
        Assert.Equal("Animations", entry.Section);
        Assert.Equal("Animations", entry.Label);
    }

    // ---- code-behind -------------------------------------------------

    private static ShellSource PageSource() =>
        ShellSource.Load("Settings.Pages.AppearancePage.xaml.cs");

    [Fact]
    public void The_page_seeds_the_combo_from_the_animations_key()
    {
        var page = PageSource();

        // The seed lives in one method, the way SeedShaderPath does, and
        // reads the file rather than the merged config. Calls matches the
        // parsed callee with its receiver, the way the wiring tests do.
        var seed = page.Method("SeedAnimationsCombo");
        var read = Assert.Single(seed.Calls("cs.GetRawFileValue"));
        Assert.Equal("\"animations\"", read.Arg(0));

        var select = Assert.Single(seed.Calls("SelectComboByTag"));
        Assert.Equal("AnimationsCombo", select.Arg(0));

        // A blank or absent key falls back to Follow system, spelled as
        // the combo's tag: the card's default is pinned in code, not in
        // the XAML's item order alone. Pinned by parsed structure - a
        // string literal in the tree - so a comment mention cannot
        // satisfy it, and exactly one literal, so a second stray would
        // not slip by either.
        Assert.Single(seed.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)
                        && l.Token.ValueText == "system"));

        // And the method is actually wired: exactly one call site in the page.
        Assert.Single(page.Root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == "SeedAnimationsCombo"));
    }

    [Fact]
    public void The_combo_writes_the_animations_key_back()
    {
        var handler = PageSource().Method("Animations_SelectionChanged");
        var write = Assert.Single(handler.Calls("OnValueChanged"));
        Assert.Equal("\"animations\"", write.Arg(0));
        // The stored value is the selected item's tag, falling back to
        // the default tag rather than to null or an empty string.
        Assert.Contains("\"system\"", write.Arg(1), StringComparison.Ordinal);
    }
}
