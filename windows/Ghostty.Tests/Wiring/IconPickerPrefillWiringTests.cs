using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// "ICON PICKER DEAD PREFILL - Settings/IconPickerDialog.xaml.cs ~17
/// declares InitialSpec; ProfileIconPickerControl.xaml.cs ~62 sets it; no
/// other reference (grep-verified). The Change dialog never shows the
/// current icon. Prefill the bundled row / fields from InitialSpec."
///
/// The decision -- which tab, which row, which spelling of a code point --
/// is <c>IconPickerPrefill</c> in Core, unit-tested by
/// <c>IconPickerPrefillTests</c>. What is left here cannot be loaded into
/// this test host (WinUI 3 is the same blocker that keeps the page smoke
/// tests un-automated), so these guards read the dialog's syntax tree:
/// they prove the mapping is applied to the three fields, and applied after
/// the grid has the rows an index refers to.
/// </summary>
public class IconPickerPrefillWiringTests
{
    private static ShellSource Dialog() => ShellSource.Load("Settings.IconPickerDialog.xaml.cs");

    private static AssignmentExpressionSyntax[] Assigns(SyntaxNode node, string target)
        => node.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == target)
            .ToArray();

    [Fact]
    public void The_dialog_reads_the_icon_it_was_opened_with()
    {
        // A property that is declared and set from outside, and read by
        // nobody, is what "no other reference (grep-verified)" looked like
        // from inside: the caller did its half of the work and nothing
        // answered.
        var reads = Dialog().Root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(i => i.Identifier.ValueText == "InitialSpec")
            .ToArray();

        Assert.NotEmpty(reads);
    }

    [Fact]
    public void One_method_applies_the_prefill_to_all_three_fields()
    {
        // Split across three methods is the shape that rots: one tab gets
        // wired up and the other two do not, and every test here still
        // passes. One method owns the prefill, and it reads InitialSpec, so
        // it cannot be a leftover that runs on something else.
        var method = Assert.Single(Dialog().Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => Assigns(m, "BundledGrid.SelectedIndex").Length > 0
                     && Assigns(m, "Mdl2Input.Text").Length > 0
                     && Assigns(m, "PickedPathLabel.Text").Length > 0));

        Assert.Contains(
            method.DescendantNodes().OfType<IdentifierNameSyntax>(),
            i => i.Identifier.ValueText == "InitialSpec");
    }

    [Fact]
    public void The_prefill_runs_after_the_grid_has_its_rows()
    {
        // Selecting the current brand is an index into ItemsSource. Run
        // first, it is an index into no rows, and the row the user came to
        // change is the one that opens unselected.
        var loaded = Dialog().Method("OnLoaded");

        var itemsSource = Assert.Single(Assigns(loaded, "BundledGrid.ItemsSource"));
        var prefill = Assert.Single(
            loaded.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.CalleeText().EndsWith(
                    "PrefillFromCurrentIcon", StringComparison.Ordinal)));

        Assert.True(
            itemsSource.SpanStart < prefill.SpanStart,
            "the prefill selects a bundled row by index, so it has to run "
            + "after BundledGrid.ItemsSource is set");
    }

    [Fact]
    public void Opening_the_dialog_does_not_count_as_picking()
    {
        // PickedSpec is what OK commits, and the control writes it straight
        // to config. Seeding it from the prefill would make "open the
        // dialog, look, press OK" a config write, so the prefill has to
        // clear whatever its own field assignment seeded.
        var method = Assert.Single(Dialog().Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => Assigns(m, "Mdl2Input.Text").Length > 0));

        Assert.NotEmpty(Assigns(method, "PickedSpec"));
    }
}
