using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The motion-gating wiring census. The two gates (Services'
/// SystemAnimations and Core's TabStripMotion.Enabled) route through
/// MotionGating, the seam that asks a registered pane-motion coordinator
/// before falling back to the legacy truth. Wiring, not behaviour: the
/// behavioural half lives in Motion.MotionGatingTests, and this file
/// pins the shape that behaviour rides on, because the shell assembly
/// cannot be loaded here and a silently unwired gate would otherwise
/// look like a green run.
///
/// The guard row is the seam's own law: the inactive path must not
/// consult the coordinator beyond the Active check, because a gate read
/// that reaches into the registration while nobody is registered is a
/// behaviour change the four legacy cells cannot see.
/// </summary>
public class MotionGatingWiringTests
{
    [Fact]
    public void SystemAnimations_routes_through_MotionGating()
    {
        var enabled = ShellSource.Load("Ghostty.Services.SystemAnimations.cs").Method("Enabled");

        // Exactly one parameter, defaulting to the ambient surface: the
        // bell fade and the pane glow are ambient effects and keep the
        // parameterless call.
        var parameter = Assert.Single(enabled.ParameterList.Parameters);
        Assert.True(
            parameter.Default?.Value.ToString().Contains("MotionSurfaceClass.Ambient") == true,
            $"the surface parameter must default to Ambient, found '{parameter.Default?.Value}'");

        var ask = Assert.Single(enabled.Calls("MotionGating.Effective"));

        // The OS read stays the second input; the unreadable-is-on
        // fail-open stays inside it.
        ask.ArgumentList.Arguments[1].Expression.AssertCallTo("ReadAnimationsEnabled");

        // High contrast is pinned off for this gate: it never read it
        // before the seam, and the literal is what keeps the legacy
        // answers bit-identical.
        var highContrast = ask.ArgumentList.Arguments[2].Expression;
        Assert.True(highContrast.IsKind(SyntaxKind.FalseLiteralExpression),
            $"the high-contrast input must stay the literal false, found '{highContrast}'");
    }

    [Fact]
    public void The_system_animation_read_still_fails_open()
    {
        var read = ShellSource.Load("Ghostty.Services.SystemAnimations.cs")
            .Method("ReadAnimationsEnabled");

        // One read and one catch, and the catch returns the literal true:
        // an unreadable state must not silently disable animation.
        Assert.Single(read.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            m => m.Name.Identifier.ValueText == "AnimationsEnabled");
        var catchClause = Assert.Single(read.DescendantNodes().OfType<CatchClauseSyntax>());
        var filter = catchClause.Filter?.ToString() ?? "";
        Assert.Contains("InvalidOperationException", filter);
        Assert.Contains("COMException", filter);
        Assert.Contains("NullReferenceException", filter);
        var caught = Assert.Single(catchClause.Block
            .DescendantNodes().OfType<ReturnStatementSyntax>());
        Assert.True(caught.Expression!.IsKind(SyntaxKind.TrueLiteralExpression),
            "the fail-open catch must return the literal true");
    }

    [Fact]
    public void The_quake_slide_asks_the_overlay_surface()
    {
        var run = ShellSource.Load("Ghostty.Hosting.QuickTerminalSlideAnimator.cs").Method("Run");
        var gate = Assert.Single(run.Calls("Ghostty.Services.SystemAnimations.Enabled"));

        var surface = Assert.IsType<MemberAccessExpressionSyntax>(
            gate.ArgumentList.Arguments[0].Expression);
        Assert.Equal("Overlay", surface.Name.Identifier.ValueText);
        // The call site spells the receiver fully qualified; the census
        // matches the tail, so a using-simplification does not red it.
        Assert.EndsWith("MotionSurfaceClass", surface.Expression.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_strip_gate_consults_the_route_before_its_own_truth()
    {
        var file = ShellSource.Load("Core.Tabs.TabStripMotion.cs");
        var enabled = file.Method("Enabled");

        // One consult, null-coalesced onto the legacy truth: no route,
        // the Core-only default, and the gate answers Legacy directly.
        var coalesce = Assert.Single(enabled.DescendantNodes().OfType<BinaryExpressionSyntax>(),
            b => b.IsKind(SyntaxKind.CoalesceExpression));
        // `Route?.Invoke(...)` parses as a conditional access wrapping
        // the invocation; unwrap it, then pin receiver, binding and call.
        var access = Assert.IsType<ConditionalAccessExpressionSyntax>(coalesce.Left);
        Assert.Equal("Route", access.Expression.ToString());
        var consult = Assert.IsType<InvocationExpressionSyntax>(access.WhenNotNull);
        var binding = Assert.IsType<MemberBindingExpressionSyntax>(consult.Expression);
        Assert.Equal("Invoke", binding.Name.Identifier.ValueText);
        coalesce.Right.AssertCallTo("Legacy");

        // And the legacy truth is the verbatim table, stated once, here:
        // animations enabled and high contrast not applied.
        var legacy = file.Method("Legacy");
        var andAlso = Assert.Single(legacy.DescendantNodes().OfType<BinaryExpressionSyntax>(),
            b => b.IsKind(SyntaxKind.LogicalAndExpression));
        Assert.Equal("animationsEnabled", Assert.IsType<IdentifierNameSyntax>(andAlso.Left).Identifier.ValueText);
        var not = Assert.IsType<PrefixUnaryExpressionSyntax>(andAlso.Right);
        Assert.True(not.IsKind(SyntaxKind.LogicalNotExpression));
        Assert.Equal("highContrast",
            Assert.IsType<IdentifierNameSyntax>(not.Operand).Identifier.ValueText);
    }

    [Fact]
    public void MotionGating_installs_the_route_at_module_load()
    {
        var file = ShellSource.Load("Ghostty.Services.MotionGating.cs");

        // The install is a module initializer: Core cannot see the shell
        // assembly, so the route is handed down before any surface can
        // ask, with no startup call a refactor could drop.
        var initializer = Assert.Single(file.Root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            m => m.AttributeLists.SelectMany(a => a.Attributes)
                .Any(a => a.Name.ToString().Contains("ModuleInitializer")));

        var assign = Assert.Single(initializer.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left is MemberAccessExpressionSyntax m
                 && m.Name.Identifier.ValueText == "Route");
        Assert.Equal("TabStripMotion.Route", assign.Left.ToString());

        // The installed lambda asks Effective about Chrome: the strip is
        // window chrome, and the route names the class it speaks for.
        var lambda = Assert.IsType<ParenthesizedLambdaExpressionSyntax>(assign.Right);
        Assert.Single(lambda.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "Effective");
        Assert.Contains(lambda.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            m => m.Expression.ToString() == "MotionSurfaceClass"
                 && m.Name.Identifier.ValueText == "Chrome");
    }

    [Fact]
    public void The_inactive_path_never_touches_the_coordinator_beyond_the_active_check()
    {
        var effective = ShellSource.Load("Ghostty.Services.MotionGating.cs").Method("Effective");

        // One guard, and the Active check leads it.
        var guard = Assert.Single(effective.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
                .Any(m => m.Name.Identifier.ValueText == "Active"
                          && (m.Expression as IdentifierNameSyntax)?.Identifier.ValueText == "PaneMotion"));

        // The whole method names PaneMotion.Current exactly once, and
        // that single reference sits inside the guard - condition or
        // statement, it is reached only past the Active check.
        var theCurrent = Assert.Single(effective.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            m => m.Name.Identifier.ValueText == "Current"
                 && (m.Expression as IdentifierNameSyntax)?.Identifier.ValueText == "PaneMotion");
        Assert.True(
            guard.Condition.DescendantNodesAndSelf().Any(n => ReferenceEquals(n, theCurrent))
            || guard.Statement.DescendantNodes().Any(n => ReferenceEquals(n, theCurrent)),
            "the one PaneMotion.Current reference must sit inside the Active guard");

        // The fallback: exactly one statement besides the guard, and it
        // consults nothing from the PaneMotion namespace - only the
        // legacy truth.
        var fallback = Assert.Single(effective.Body!.Statements,
            s => !ReferenceEquals(s, guard));
        Assert.DoesNotContain(fallback.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            m => (m.Expression as IdentifierNameSyntax)?.Identifier.ValueText == "PaneMotion");
        Assert.Single(fallback.Calls("TabStripMotion.Legacy"));
    }
}
