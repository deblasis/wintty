using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The opaque chrome colour is a function of the desktop's light/dark
/// setting, so a caller that hands the resolver a constant gets the old
/// near-black chrome back on a light desktop and nothing says so. The
/// resolver's own tests cannot see that: they only prove the function.
///
/// Reads the source, because the shell assembly cannot be loaded into a
/// test host.
/// </summary>
public sealed class RootGridBackgroundWiringTests
{
    private static PropertyDeclarationSyntax Property(ShellSource source, string name) =>
        Assert.Single(
            source.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.ValueText == name);

    [Fact]
    public void The_root_grid_asks_the_desktop_for_its_polarity()
    {
        var call = ShellSource.Load("MainWindow.xaml.cs")
            .Method("ApplyRootGridBackground")
            .Call("RootBackgroundResolver.Resolve");

        // Asserted on the node: a substring match here reads the same
        // against `!OsTheme.IsDark(...)`, so it would have passed the whole
        // resolution through inverted and reported the wiring intact.
        var polarity = call.ArgExpression(3).AssertCallTo("OsTheme.IsDark");

        // The window already holds a UISettings and subscribes to it. A
        // freshly activated one answers for a different moment, which is
        // the drift OsTheme's overload exists to prevent.
        Assert.Equal("_systemUiSettings", polarity.Arg(0));
    }

    /// <summary>
    /// <c>BackdropGround.Estimate</c> stays pure: it is told the polarity
    /// rather than reading the OS, so the ink and the ground it is chosen
    /// against cannot be resolved for two different desktop states.
    ///
    /// The polarity it is told is the polarity of whatever actually paints
    /// behind the ink, which the frame style decides. A solid frame paints
    /// the strip with the resolver's own opaque fill, read from the desktop,
    /// so there the estimate takes the desktop read. Frosted and crystal
    /// leave the backdrop bare and the paint follows the element theme,
    /// which is the manager's resolved answer: under an explicit
    /// <c>window-theme</c> the override wins even when the OS desktop
    /// disagrees (the caption buttons went over to the painted truth for
    /// the same reason, #235). Feeding the estimate the raw OS read
    /// unconditionally was the dark-theme half of #936: the ladder picked
    /// the black pole for ink on a window that was painting dark, and every
    /// muted row measured near 1.1:1. Feeding it the manager read
    /// unconditionally commits the mirror image: on a solid frame the ink
    /// is scored for a dark ground on a light desktop and lands near
    /// 1.06:1.
    /// </summary>
    [Fact]
    public void The_backdrop_ground_estimate_takes_the_polarity_as_an_argument()
    {
        var source = ShellSource.Load("MainWindow.xaml.cs");
        var estimate = Assert.Single(
            source.Root.Calls("Core.Shell.BackdropGround.Estimate"));

        // The polarity argument is the window theme manager's read, for
        // every ground style: the manager holds the explicit window-theme
        // when there is one, follows the system when the config does, and
        // resolves from the previewed background's luminance under auto.
        // Node-level: a conditional read or a desktop read must not slip
        // back in for either arm.
        var polarity = estimate.ArgExpression(1);
        Assert.Equal("_themeManager.IsDarkMode", polarity.ToString());

        var core = ShellSource.Load("Core.Shell.BackdropGround.cs");
        Assert.DoesNotContain(
            core.Root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().Contains("IsDark", StringComparison.Ordinal));
    }

    /// <summary>
    /// Under a solid frame the ink and the fill answer the same desktop
    /// read. The fill is <c>ChromeFillArgb</c>, the resolver's opaque
    /// colour taken from the desktop; the estimate is what the unselected
    /// titles and the caption glyphs are scored against. Two different
    /// reads -- a freshly activated UISettings answers for a different
    /// moment -- and the strip can paint one polarity while the ink was
    /// scored for the other, which is the standing ~1.06:1 quadrant this
    /// branch exists to close. The strip-side coalesce pins the same
    /// agreement from the other end: the fill wins wherever a fill is
    /// painted.
    /// </summary>
    [Fact]
    public void The_solid_ground_scores_the_ink_against_the_window_theme_that_paints_the_fill()
    {
        var source = ShellSource.Load("MainWindow.xaml.cs");

        var fill = Property(source, "ChromeFillArgb")
            .Call("RootBackgroundResolver.Resolve");
        var estimate = Property(source, "EstimatedBackdropGround")
            .Call("Core.Shell.BackdropGround.Estimate");

        // One source for both, whatever the frame style: the fill paints
        // from the window theme manager and the ink is scored against the
        // same read. The desktop read previously sat under one arm only,
        // and each fill/score disagreement shipped its own wrong-colour
        // frame (#936 and its mirror image).
        Assert.Equal(
            "_themeManager.IsDarkMode",
            fill.ArgExpression(3).ToString());
        Assert.Equal(
            "_themeManager.IsDarkMode",
            estimate.ArgExpression(1).ToString());
    }
}
