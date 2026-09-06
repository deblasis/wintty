using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The reduce-motion contract's wiring guards. Two halves: the system
/// animation-effects flip must be LISTENED for and must land the long
/// in-flight clocks (every gate reads the flag fresh per gesture, so
/// without a listener a clock armed before the flip plays on), and every
/// custom animation path outside the strips must read the preference at
/// all -- the audit found bell, glow and quake paths that never asked.
/// </summary>
public class AnimationsEnabledWiringTests
{
    private static ShellSource MainWindow() => ShellSource.Load("Ghostty.MainWindow.xaml.cs");

    [Fact]
    public void MainWindowSubscribesAndDetachesTheFlip()
    {
        var source = MainWindow();
        var assignments = source.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_systemUiSettings.AnimationsEnabledChanged")
            .ToList();

        // Exactly one subscribe and one detach, and the detach is the
        // teardown's: UISettings is an OS object that outlives the window,
        // so a subscription left attached points an OS callback at a
        // closed window's strip.
        var detach = Assert.Single(
            assignments, a => a.IsKind(SyntaxKind.SubtractAssignmentExpression));
        Assert.Contains(assignments, a => a.IsKind(SyntaxKind.AddAssignmentExpression));
        Assert.Contains(source.Method("OnClosedAsync")
            .DescendantNodes().OfType<AssignmentExpressionSyntax>(), a => ReferenceEquals(a, detach));

        // The event is 19041+ and the project reaches older machines: the
        // subscribe must be behind an ApiInformation probe, which is also
        // what keeps the platform analyzer quiet.
        Assert.Contains(source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText().EndsWith(".IsEventPresent"));
    }

    [Fact]
    public void FlipHandlerLandsStripMotionThroughTheDispatcher()
    {
        var handler = MainWindow().Method("OnSystemAnimationsEnabledChanged");

        // The land call exists, and it sits inside a lambda: the event
        // fires on a thread-pool thread, and the strip's clocks are UI
        // tree state. LambdaExpressionSyntax, not one of its shapes --
        // `() => {}` parses as the parenthesized form.
        var land = handler.Call("_verticalTabHost.Strip.LandAllMotion");
        Assert.True(
            land.Ancestors().OfType<LambdaExpressionSyntax>().Any(),
            "LandAllMotion must run inside the dispatcher hop, not on the callback thread");

        // The handler reads the event's own sender rather than activating
        // a second UISettings that could answer for a later moment.
        var read = handler.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Name.Identifier.ValueText == "AnimationsEnabled").ToList();
        Assert.Contains(read, m => m.Expression is IdentifierNameSyntax id
            && id.Identifier.ValueText == "sender");
    }

    [Fact]
    public void BellFadeCutsUnderReduceMotion()
    {
        var dismiss = ShellSource.Load("Ghostty.Controls.TerminalControl.xaml.cs")
            .Method("DismissBellBorder");
        Assert.Single(dismiss.Calls("Ghostty.Services.SystemAnimations.Enabled"));
    }

    [Fact]
    public void GlowOrbitIsGated()
    {
        var start = ShellSource.Load("Ghostty.Panes.PaneStartupGlow.cs")
            .Method("StartGlow");
        Assert.Single(start.Calls("Ghostty.Services.SystemAnimations.Enabled"));
    }

    [Fact]
    public void QuakeSlideIsGated()
    {
        var run = ShellSource.Load("Ghostty.Hosting.QuickTerminalSlideAnimator.cs")
            .Method("Run");
        Assert.Single(run.Calls("Ghostty.Services.SystemAnimations.Enabled"));
    }

    [Fact]
    public void EveryStripGlideNamesTheSharedCurve()
    {
        // The pin band's reflow and every strip glide must name the same
        // token: a square pushed off a row end is the same point-to-point
        // move as the neighbour gliding one pane over. The rule is "every
        // bezier factory names the token", not a usage count -- a count
        // would punish the next glide that gets tokenized.
        var reflow = ShellSource.Load("Ghostty.Tabs.TabPinBandPanel.cs")
            .Method("Reflow");
        Assert.Single(reflow.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.ValueText == "GlideBezierP1"));

        var strip = ShellSource.Load("Ghostty.Tabs.VerticalTabStrip.xaml.cs");
        var beziers = strip.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith(".CreateCubicBezierEasingFunction"))
            .ToList();
        // Load-bearing floor: the drag-neighbour glide and the pin flight
        // are two distinct call sites today.
        Assert.True(beziers.Count >= 2,
            $"expected at least the drag glide and pin flight bezier factories, found {beziers.Count}");
        foreach (var call in beziers)
        {
            Assert.Contains(call.ArgumentList.Arguments,
                a => a.ToString().Contains("GlideBezierP1", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void SwitcherTracksStayIndependent()
    {
        // Opacity and RenderTransform scale/offset are independent
        // properties; marking them dependent pins to the UI thread
        // animations the compositor could carry.
        var popup = ShellSource.Load("Ghostty.Tabs.TabSwitcherPopup.xaml.cs");
        Assert.DoesNotContain(popup.Root.DescendantTokens(),
            t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == "EnableDependentAnimation");
    }
}
