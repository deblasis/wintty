using System.Collections.Generic;
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
        var tabRoute = Assert.Single(
            overloads, m => m.ParameterList.Parameters.Any(p => p.Type?.ToString() == "TabModel"));

        var forwarded = tabRoute.Call("ShowColorPicker");
        AssertAllowNone(forwarded.ArgumentList, "true", "the per-tab ShowColorPicker");
    }

    [Fact]
    public void EveryPickerBuiltForAGroup_IsBuiltWithoutNone()
    {
        // Swept across the WHOLE shell, not three named files. The defect was
        // that a second entry point existed and nobody checked it; a third in
        // a file this test did not think to name has to fail here rather than
        // slip past, and VerticalTabHost already owns a group context-menu
        // route that could grow one.
        var groupBuilds = new List<(string Resource, ObjectCreationExpressionSyntax Creation)>();
        var anyBuild = 0;

        foreach (var (resource, root) in ShellSource.AllShellSources())
        {
            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (creation.Type.ToString() != "TabColorPalettePicker") continue;
                if (creation.ArgumentList is not { Arguments.Count: > 0 } args) continue;
                anyBuild++;

                // "Built for a group" is decided by the argument's text, which
                // is why the count below is asserted: rename the variable and
                // this filter stops matching, silently.
                if (args.Arguments[0].ToString().Contains("group"))
                    groupBuilds.Add((resource, creation));
            }
        }

        Assert.True(anyBuild > 0, "found no TabColorPalettePicker construction at all");
        Assert.True(
            groupBuilds.Count == 1,
            "expected exactly one picker built for a group (MainWindow's strip route); "
                + "found " + groupBuilds.Count + ". A new one must pass allowNone: false, "
                + "and a disappearing one means the arg[0] text stopped saying 'group' and "
                + "this rule stopped matching: "
                + string.Join(", ", groupBuilds.Select(b => b.Resource)));

        foreach (var (_, creation) in groupBuilds)
            AssertAllowNone(creation.ArgumentList!, "false", creation.ToString());
    }

    [Fact]
    public void ThePicker_TakesTheFlagFromItsOnlyConstructor()
    {
        // No single-argument overload: every caller decides, because the
        // permissive answer is the one that was wrong.
        var ctors = Picker().Root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
            .Where(c => c.Identifier.ValueText == "TabColorPalettePicker")
            .ToList();
        var ctor = Assert.Single(ctors);
        Assert.Contains(
            ctor.ParameterList.Parameters,
            p => p.Identifier.ValueText == "allowNone" && p.Type?.ToString() == "bool");
    }

    [Fact]
    public void ThePicker_SkipsTheNoneSwatch_OnlyWhenItWasToldTo()
    {
        var build = Picker().Method("BuildSwatches");

        var skip = build.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("TabColor.None"));

        // Polarity as NODES, not as spelling. `color == None` alone would drop
        // the swatch for tabs too; `!allowNone` alone would drop every swatch.
        // Asserting the rendered string would also fail the equivalent
        // `!allowNone && color == TabColor.None`, which is the same rule.
        var and = Assert.IsType<BinaryExpressionSyntax>(skip.Condition);
        var operands = new[] { and.Left.ToString(), and.Right.ToString() };
        Assert.Contains("color == TabColor.None", operands);
        Assert.Contains("!allowNone", operands);

        // A `continue`, not the text "continue" somewhere in the statement:
        // Log("continue") satisfied the substring form.
        Assert.True(
            skip.Statement is ContinueStatementSyntax
                || skip.Statement.DescendantNodesAndSelf().OfType<ContinueStatementSyntax>().Any(),
            "the None skip does not continue the loop: " + skip.Statement);

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
