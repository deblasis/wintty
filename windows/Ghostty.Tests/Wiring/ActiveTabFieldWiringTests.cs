using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The active tab is the field: it alone is painted the terminal's own
/// ground, and it runs into the pane it belongs to with no line between.
/// Inactive tabs stay chrome. The accent is the STROKE around that field.
///
/// Both halves of that are one-token changes away from the thing they
/// replaced. The fill used to be the accent on the palette path, which made
/// the active tab a second, brighter chrome surface and had the seam cover
/// draw that accent straight across the pane's top border; putting
/// <c>AccentColor</c> back where <c>ActiveTabFill</c> now stands compiles,
/// looks deliberate, and undoes the decision. And the cover takes the tab's
/// brush INSTANCE rather than a brush mixed to the same colour, so the two
/// settle on one clock -- swapping in a copy is a tidy-up that is correct at
/// rest and wrong for every frame of the transition, which is precisely the
/// frames where a line appears between the tab and the pane.
///
/// Neither is observable without a live window, which is why they are pinned
/// on the syntax here.
/// </summary>
public sealed class ActiveTabFieldWiringTests
{
    private const string VerticalStrip = "Tabs.VerticalTabStrip.xaml.cs";
    private const string HorizontalStrip = "Tabs.TabHost.xaml.cs";

    /// <summary>
    /// Each strip and the method that takes the palette. They are the same
    /// decision written twice, so both are held to it.
    /// </summary>
    public static TheoryData<string, string> BothStrips => new()
    {
        { HorizontalStrip, "ApplyShellTheme" },
        { VerticalStrip, "ApplyShellChrome" },
    };

    /// <summary>
    /// The palette path fills the active tab from the terminal's ground.
    ///
    /// Asserted on the property the brush is constructed from rather than on
    /// the absence of the word "accent", because the accent is still read in
    /// both files -- for the stroke, which is its job.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothStrips))]
    public void TheActiveTabIsFilledWithTheTerminalGround(string file, string applier)
    {
        var applied = ShellSource.Load(file).Method(applier);

        var fills = applied.AssignsTo("_selectedTabFillBrush").ToList();
        Assert.True(
            fills.Count == 1,
            $"expected one fill assignment in {file}.{applier}, found {fills.Count}");

        var made = Assert.IsType<ObjectCreationExpressionSyntax>(fills[0].Right);
        Assert.NotNull(made.ArgumentList);
        Assert.Equal("theme.ActiveTabFill", made.ArgumentList!.Arguments[0].ToString());
    }

    /// <summary>
    /// And inks it against that same ground, with the terminal's own
    /// foreground.
    ///
    /// Both arguments. Scoring the right ink against the wrong ground is the
    /// shape of the bug this replaced -- ink picked for the accent, painted
    /// on a surface that is not the accent -- and it leaves every literal in
    /// place.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothStrips))]
    public void TheActiveInkIsScoredAgainstTheFieldItSitsOn(string file, string applier)
    {
        var applied = ShellSource.Load(file).Method(applier);
        var scored = applied.Call("ThemeResolution.EnsureReadableForeground");

        Assert.Equal("theme.ActiveTabFill", Behind(applied, scored.Arg(0)));
        Assert.Equal("theme.ActiveTabInk", Behind(applied, scored.Arg(1)));
    }

    /// <summary>
    /// The service names those two for the role and takes them from the
    /// terminal pair, so a strip reading the wrong property is a rename away
    /// rather than a silent substitution.
    /// </summary>
    [Fact]
    public void TheServiceTakesTheFieldFromTheTerminalPair()
    {
        var recompute = ShellSource.Load("Services.ShellThemeService.cs").Method("Recompute");

        Assert.Equal("bg", Initializer(recompute, "newActiveTabFill"));
        Assert.Equal("fg", Initializer(recompute, "newActiveTabInk"));

        // The accent keeps its own source, and it is not the ground. Without
        // this the two could be quietly collapsed onto one value and every
        // rule above would still pass.
        Assert.NotEqual("bg", Initializer(recompute, "newAccent"));
    }

    /// <summary>
    /// The seam cover is handed the brush the tab is painted with.
    ///
    /// The horizontal strip raises it, so the assertion is on what the event
    /// carries. The vertical cover reads the row's own Background instead, so
    /// there the assertion is that the row is painted with the field --
    /// different mechanisms, one invariant.
    /// </summary>
    [Fact]
    public void TheCoverAndTheTabShareOneBrush()
    {
        var horizontal = ShellSource.Load(HorizontalStrip).Method("UpdateSelectedTabBridge");
        var placed = horizontal.Calls("SelectedTabSeamChanged?.Invoke")
            .Where(c => c.Arg(2) != "null")
            .ToList();
        Assert.True(
            placed.Count == 1,
            $"expected one placing raise of the seam event, found {placed.Count}");
        Assert.Equal("_field.Brush", placed[0].Arg(2));

        var vertical = ShellSource.Load(VerticalStrip).Method("UpdateSelectionRow");
        var painted = vertical.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "SelectionRow.Background")
            .ToList();
        Assert.True(
            painted.Count == 1,
            $"expected one paint of the selection row, found {painted.Count}");
        Assert.Equal("_field.Brush", painted[0].Right.ToString());
    }

    /// <summary>
    /// The settle starts from the strip's own ground and runs only when the
    /// strip's motion gate says so.
    ///
    /// The gate is the same one every other spring in the strip reads, not a
    /// second answer composed here: High Contrast collapses this to a cut,
    /// and a fill that keeps animating there is a fill the mode was supposed
    /// to have removed.
    /// </summary>
    [Theory]
    [InlineData(HorizontalStrip)]
    [InlineData(VerticalStrip)]
    public void TheFieldGrowsOutOfTheStripGround_BehindTheSharedMotionGate(string file)
    {
        var settle = ShellSource.Load(file).Root.Call("_field.Settle");

        Assert.Equal("StripGround()", settle.Arg(2));

        var gate = settle.ArgExpression(3).AssertCallTo("TabStripMotion.Enabled");
        Assert.Equal("SystemAnimationsEnabled()", gate.Arg(0));
        Assert.Equal("_highContrast", gate.Arg(1));
    }

    /// <summary>
    /// A local one hop back, so a rule can name the value rather than the
    /// name the method happened to give it.
    /// </summary>
    private static string Behind(MethodDeclarationSyntax method, string name)
    {
        var declared = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.ValueText == name);
        if (declared?.Initializer is null) return name;

        // The packs are the shape both strips use: PackColor(theme.X).
        var initializer = declared.Initializer.Value;
        if (initializer is InvocationExpressionSyntax call
            && call.CalleeText().EndsWith("PackColor", StringComparison.Ordinal))
        {
            return call.Arg(0);
        }
        return initializer.ToString();
    }

    private static string Initializer(MethodDeclarationSyntax method, string name)
    {
        var declared = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.ValueText == name);
        Assert.True(declared?.Initializer is not null, $"no local '{name}' with an initializer");
        return declared!.Initializer!.Value.ToString();
    }
}
