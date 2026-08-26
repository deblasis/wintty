using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The vertical title row is the window backdrop on every path but two:
/// window-theme=wintty, which paints the whole shell from the palette on
/// purpose, and High Contrast, where translucency over a backdrop nobody
/// controls is the thing being removed.
///
/// The failure this guards is a quiet one. Painting the row again from
/// <c>_configService.BackgroundColor</c> compiles, runs, and looks fine on
/// the machine of whoever writes it, because it only goes wrong when the
/// palette and the desktop disagree. These are wiring guards: they prove
/// the row is still reached by the backdrop and that the ink is still
/// chosen by measurement. What it looks like is only observable on a live
/// window.
/// </summary>
public class VerticalTitleRowBackdropWiringTests
{
    private static ShellSource Window() => ShellSource.Load("MainWindow.xaml.cs");

    /// <summary>
    /// Every read of the terminal palette inside the row's fill decision
    /// has to be under the High Contrast branch. One that is not is the row
    /// going back to being a palette slab.
    /// </summary>
    [Fact]
    public void TheRowReadsThePalette_OnlyUnderHighContrast()
    {
        var method = Window().Method("ApplyVerticalTitleBarChrome");
        var elseBranch = method.DescendantNodes().OfType<ElseClauseSyntax>().First();

        var paletteReads = elseBranch.Calls("UnpackTerminalColor");
        Assert.NotEmpty(paletteReads);
        foreach (var read in paletteReads)
        {
            Assert.Contains(
                read.Ancestors().OfType<ConditionalExpressionSyntax>(),
                c => c.Condition.ToString() == "HighContrastChromeActive");
        }
    }

    /// <summary>
    /// The ink is scored against the estimated backdrop, not against the
    /// palette or the element theme on their own. Both of those were measured
    /// picking the wrong pole. Asserted on the argument the score is taken
    /// against, because the mutation worth catching is that argument being
    /// swapped for one of the two inputs rather than the call disappearing.
    /// </summary>
    [Fact]
    public void TheInk_IsScoredAgainstTheBackdrop()
    {
        var window = Window();

        var ink = window.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "VerticalTitleInk");
        var score = ink.Call("ThemeResolution.EnsureReadableForeground");

        // The ground is the estimate, except under High Contrast where the
        // row is painted and the palette is Windows' own colour.
        var ground = Assert.IsType<ConditionalExpressionSyntax>(
            score.ArgumentList.Arguments[0].Expression);
        Assert.Equal("HighContrastChromeActive", ground.Condition.ToString());
        Assert.Equal("_configService.BackgroundColor", ground.WhenTrue.ToString());
        Assert.Equal("EstimatedBackdropGround", ground.WhenFalse.ToString());

        // And that estimate is a real one, fed the live material rather than
        // a constant: a solid frame is not a blend at all. The material is
        // the chrome's own, not the terminal's -- see ChromeGroundStyle,
        // which falls back to the backdrop whenever the frame is not
        // covering it.
        var estimate = window.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "EstimatedBackdropGround")
            .Call("Core.Shell.BackdropGround.Estimate");
        Assert.Equal("ChromeGroundStyle", estimate.Arg(2));
    }

    /// <summary>
    /// The title text is written from that ink rather than left on the
    /// element theme's resource, which is right only while the theme and
    /// the row agree.
    /// </summary>
    [Fact]
    public void TheTitleText_TakesTheSameInk()
    {
        var method = Window().Method("ApplyVerticalTitleBarChrome");
        var assignments = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "VerticalTitleText.Foreground")
            .ToList();
        Assert.Single(assignments);
        Assert.Contains("VerticalTitleInk", assignments[0].Right.ToString());
    }
}
