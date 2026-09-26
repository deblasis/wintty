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
    /// The polarity it is told is the window's resolved theme, not the raw
    /// desktop read. <c>ApplyTheme</c> hands <c>_themeManager.ElementTheme</c>
    /// to the root, and the chrome the ink is scored against paints from
    /// that same answer: under an explicit <c>window-theme</c> the override
    /// wins even when the OS desktop disagrees (the caption buttons went
    /// over to the painted truth for the same reason, #235), and under the
    /// default the manager resolves from the OS anyway, so the
    /// system-tracking answer is unchanged. Feeding the estimate the raw OS
    /// read was the dark-theme half of #936: the ladder picked the black
    /// pole for ink on a window that was painting dark, and every muted row
    /// measured near 1.1:1.
    /// </summary>
    [Fact]
    public void The_backdrop_ground_estimate_takes_the_polarity_as_an_argument()
    {
        var source = ShellSource.Load("MainWindow.xaml.cs");
        var estimate = Assert.Single(
            source.Root.Calls("Core.Shell.BackdropGround.Estimate"));

        // Node-level, as before: a negated polarity passes any IsDark-call
        // assertion just as happily, so assert the exact expression read.
        Assert.Equal("_themeManager.IsDarkMode", estimate.ArgExpression(1).ToString());

        var core = ShellSource.Load("Core.Shell.BackdropGround.cs");
        Assert.DoesNotContain(
            core.Root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().Contains("IsDark", StringComparison.Ordinal));
    }
}
