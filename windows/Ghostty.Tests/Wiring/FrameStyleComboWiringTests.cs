using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Ghostty.Core.Shell;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The frame-style combo has a fourth entry the backdrop combo does not:
/// "match the backdrop", which is the key being absent rather than a value
/// for it. Writing an empty frame-style instead of removing the line would
/// leave the config saying something it does not mean, and the read would
/// report it as an unusable style and fall back to the shared default --
/// which is not the same thing as inheriting.
///
/// The page cannot be loaded into a test host, so this reads its markup and
/// its source.
/// </summary>
public sealed class FrameStyleComboWiringTests
{
    private const string Key = "\"frame-style\"";
    private const string Combo = "FrameStyleCombo";

    private static ShellSource AppearancePage() =>
        ShellSource.Load("Settings.Pages.AppearancePage.xaml.cs");

    private static IReadOnlyList<XElement> ComboItems()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string Resource = "Ghostty.Tests.Settings.Pages.AppearancePage.xaml";
        using var stream = assembly.GetManifestResourceStream(Resource);
        Assert.NotNull(stream);

        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var combo = XDocument.Load(stream!).Descendants()
            .Single(e => e.Attribute(xaml + "Name")?.Value == Combo);
        return combo.Elements().ToList();
    }

    /// <summary>
    /// Four entries, and the unset one first. Task 1 established that the
    /// first item is not a neutral landing place -- on the backdrop combo it
    /// is solid. Here it deliberately is neutral, which is what lets an
    /// unmatched tag fall through to it without lying to the user.
    /// </summary>
    [Fact]
    public void The_combo_offers_the_three_styles_plus_an_unset_entry_first()
    {
        var items = ComboItems();
        Assert.Equal(4, items.Count);

        Assert.Equal(string.Empty, items[0].Attribute("Tag")?.Value);
        Assert.Equal(
            new[] { BackdropStyles.Solid, BackdropStyles.Frosted, BackdropStyles.Crystal },
            items.Skip(1).Select(i => i.Attribute("Tag")?.Value));
    }

    /// <summary>
    /// An absent key has to show as "match the backdrop", not as the style
    /// it currently resolves to. FrameStyle answers the resolved value and
    /// cannot tell the two apart, so the seed asks the file.
    /// </summary>
    [Fact]
    public void The_seed_asks_the_file_whether_the_key_is_set_at_all()
    {
        var seed = Assert.Single(
            AppearancePage().Root.Calls("SelectComboByTag"),
            c => c.Arg(0) == Combo && c.ArgumentList.Arguments.Count > 1
                && c.Arg(1).Contains("FrameStyle", StringComparison.Ordinal));

        var choice = Assert.IsType<ConditionalExpressionSyntax>(seed.ArgExpression(1));
        var asked = choice.Condition.AssertCallTo("IsConfiguredInFile");
        Assert.Equal(Key, asked.Arg(0));

        Assert.Equal("cs.FrameStyle", choice.WhenTrue.ToString());
        Assert.Equal(MatchBackdropTagName(), choice.WhenFalse.ToString());
    }

    /// <summary>
    /// The one name the seed and the write have to agree on. Two spellings
    /// of "the tag that means unset" is how the combo starts showing an
    /// entry that writes something else.
    /// </summary>
    private static string MatchBackdropTagName()
    {
        var field = AppearancePage().Root.DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .Single(v => v.Identifier.ValueText == "MatchBackdropTag");

        Assert.NotNull(field.Initializer);
        Assert.Equal("\"\"", field.Initializer!.Value.ToString());
        return "MatchBackdropTag";
    }

    [Fact]
    public void Choosing_match_backdrop_removes_the_key_instead_of_writing_an_empty_value()
    {
        var handler = AppearancePage().Method("FrameStyle_SelectionChanged");

        var unset = handler.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("IsNullOrEmpty", StringComparison.Ordinal));

        var remove = handler.Call("_editor.RemoveValue");
        Assert.Equal(Key, remove.Arg(0));
        Assert.True(
            unset.Statement.Span.Contains(remove.Span),
            "the key is removed outside the branch that recognises the unset entry, so every "
                + "selection removes it");

        var write = handler.Call("OnValueChanged");
        Assert.Equal(Key, write.Arg(0));
        Assert.False(
            unset.Statement.Span.Contains(write.Span),
            "the unset entry still writes a value; an empty frame-style is not an absent one");
    }

    /// <summary>
    /// The removal path bypasses OnValueChanged, which is where the seeding
    /// guard lives, so it needs its own. Without it, seeding the combo during
    /// construction comments the user's frame-style out of their config.
    /// </summary>
    [Fact]
    public void The_removal_path_is_guarded_against_the_seeding_pass()
    {
        var handler = AppearancePage().Method("FrameStyle_SelectionChanged");

        var guard = handler.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "_loading");
        var remove = handler.Call("_editor.RemoveValue");

        Assert.True(
            guard.SpanStart < remove.SpanStart,
            "_loading is checked after the removal, so seeding the combo erases the key");
    }
}
