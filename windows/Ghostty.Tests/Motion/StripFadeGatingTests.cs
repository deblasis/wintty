using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Motion;

/// <summary>
/// The strip's informational fades ask the fade-bearing gate, not the
/// strip's movement route. The movement route runs only on Full (reduced
/// keeps fades, not slides), and two of the reads behind it gate
/// opacity-only fades that exist today: the swap's appear fade in TabHost
/// and a new group field's fade-in in the vertical strip. A cut on those
/// reads makes the element pop at full strength, the exact flash the
/// fade exists to avoid, so they ride SystemAnimations.Enabled (the
/// != Off mapping, the gate the bell fade and the pane glow ride), which
/// keeps a fade alive under Reduced. The strip's slides, lifts and
/// flights keep the movement route.
/// </summary>
public class StripFadeGatingTests
{
    [Fact]
    public void The_appear_fade_asks_the_fade_gate_not_the_movement_route()
    {
        var fade = ShellSource.Load("Tabs.TabHost.xaml.cs").Method("FadeInAppearing");

        // One gate read, the fade-bearing one, on the chrome family the
        // fade belongs to.
        var read = Assert.Single(fade.Calls("SystemAnimations.Enabled"));
        Assert.Contains("MotionSurfaceClass.Chrome", read.Arg(0), System.StringComparison.Ordinal);

        // The movement route has nothing to say about a fade.
        Assert.Empty(fade.Calls("TabStripMotion.Enabled"));
    }

    [Fact]
    public void The_group_field_fade_arms_off_the_fade_gate()
    {
        var strip = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

        // The pass reads the fade gate once, chrome, beside the movement
        // read it already carried.
        var read = Assert.Single(strip.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.CalleeText() == "SystemAnimations.Enabled"));
        Assert.Contains("MotionSurfaceClass.Chrome", read.Arg(0), System.StringComparison.Ordinal);

        var place = strip.Method("PlaceGroupField");

        // The fade answer arrives beside the movement answer, its own
        // parameter: one gate per law.
        Assert.Contains(place.ParameterList.Parameters,
            p => p.Type?.ToString() == "bool" && p.Identifier.ValueText == "fades");

        // The arming consults the fade answer, never the movement local:
        // the innermost if that calls FadeInGroupField (the enclosing
        // early-return's block also contains the call, so the innermost
        // span is the arming).
        var arm = place.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(c => c.CalleeText() == "FadeInGroupField"))
            .OrderBy(i => i.Span.Length)
            .First();
        Assert.Contains("fades", arm.Condition.ToString(), System.StringComparison.Ordinal);
        Assert.DoesNotContain("motion", arm.Condition.ToString(), System.StringComparison.Ordinal);
    }
}
