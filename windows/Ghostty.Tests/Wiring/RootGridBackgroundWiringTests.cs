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
    /// </summary>
    [Fact]
    public void The_backdrop_ground_estimate_takes_the_polarity_as_an_argument()
    {
        var source = ShellSource.Load("MainWindow.xaml.cs");
        var estimate = Assert.Single(
            source.Root.Calls("Core.Shell.BackdropGround.Estimate"));
        // Same node-level assertion as above and for the same reason: told
        // the negation of the desktop polarity, Estimate stays just as pure
        // and answers for the wrong desktop.
        estimate.ArgExpression(1).AssertCallTo("OsTheme.IsDark");

        var core = ShellSource.Load("Core.Shell.BackdropGround.cs");
        Assert.DoesNotContain(
            core.Root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => c.CalleeText().Contains("IsDark", StringComparison.Ordinal));
    }
}
