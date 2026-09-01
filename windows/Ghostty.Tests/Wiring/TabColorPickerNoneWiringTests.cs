using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The picker is opened for a group from two places and for a tab from one,
/// and only the tab admits <see cref="Ghostty.Core.Tabs.TabColor.None"/>.
/// Guarding call sites is what let None reach a group in the first place, so
/// the model coerces (TabGroupColorTests) and these guards keep the UI from
/// offering a swatch the model will refuse.
/// </summary>
public class TabColorPickerNoneWiringTests
{
    private static ShellSource Builder() => ShellSource.Load("Tabs.TabContextMenuBuilder.cs");
    private static ShellSource Window() => ShellSource.Load("Ghostty.MainWindow.xaml.cs");
    private static ShellSource Picker() => ShellSource.Load("Tabs.TabColorPalettePicker.xaml.cs");

    /// <summary>
    /// Named rather than positional, and asserted as the literal `false`:
    /// `allowNone: !isGroup` or a variable would satisfy "an argument is
    /// present" while deciding at runtime what this test exists to fix.
    /// </summary>
    private static void AssertAllowNone(ArgumentListSyntax args, string expected, string where)
    {
        var arg = args.Arguments.SingleOrDefault(a => a.NameColon?.Name.ToString() == "allowNone");
        Assert.True(arg is not null, $"{where} does not pass allowNone by name: {args}");
        Assert.Equal(expected, arg!.Expression.ToString());
    }

    [Fact]
    public void TheGroupMenu_OpensThePicker_WithoutNone()
    {
        var menu = Builder().Method("BuildGroupMenu");
        var call = menu.Call("ShowColorPicker");

        Assert.Equal("group.Color", call.Arg(1));
        AssertAllowNone(call.ArgumentList, "false", "the group menu's ShowColorPicker");
    }

    [Fact]
    public void TheTabRoute_KeepsNone_BecauseThatIsHowATintIsCleared()
    {
        // The inverse guard. Without it, "no None anywhere" would pass by
        // taking the clear-colour affordance away from every tab, which is
        // a bigger regression than the crash this fixes.
        var overloads = Builder().Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == "ShowColorPicker")
            .ToList();
        var tabRoute = Assert.Single(overloads.Where(m => m.ParameterList.Parameters
            .Any(p => p.Type?.ToString() == "TabModel")));

        var forwarded = tabRoute.Call("ShowColorPicker");
        AssertAllowNone(forwarded.ArgumentList, "true", "the per-tab ShowColorPicker");
    }

    [Fact]
    public void EveryPickerBuiltForAGroup_IsBuiltWithoutNone()
    {
        // A file-wide rule rather than one naming MainWindow's line: the
        // defect was that a second entry point existed and nobody checked
        // it, so a third must fail this rather than slip past it.
        foreach (var source in new[] { Window(), Builder(), Picker() })
        {
            var built = source.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Where(o => o.Type.ToString() == "TabColorPalettePicker")
                .Where(o => o.ArgumentList is { Arguments.Count: > 0 }
                            && o.ArgumentList.Arguments[0].ToString().Contains("group"));

            foreach (var creation in built)
                AssertAllowNone(creation.ArgumentList!, "false", creation.ToString());
        }
    }

    [Fact]
    public void ThePicker_SkipsTheNoneSwatch_OnlyWhenItWasToldTo()
    {
        var build = Picker().Method("BuildSwatches");

        var skip = build.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("TabColor.None"));

        // Polarity, both halves. `color == None` alone would drop the swatch
        // for tabs too; `!allowNone` alone would drop every swatch.
        Assert.Equal("color == TabColor.None && !allowNone", skip.Condition.ToString());
        Assert.Contains("continue", skip.Statement.ToString());

        // And the flag reaches it: BuildSwatches taking the parameter while
        // the constructor never forwards it would leave every check above
        // green and every picker unchanged.
        Assert.Contains(
            build.ParameterList.Parameters,
            p => p.Identifier.ValueText == "allowNone" && p.Type?.ToString() == "bool");
        var forwarded = Picker().Root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
            .SelectMany(c => c.Calls("BuildSwatches"))
            .Single();
        Assert.Equal("allowNone", forwarded.Arg(1));
    }
}
