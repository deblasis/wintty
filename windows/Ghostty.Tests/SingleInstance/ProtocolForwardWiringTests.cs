using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

/// <summary>
/// The serve-side wiring of a protocol launch forwarded by a secondary
/// (single-instance mode): a <c>wintty://</c> URI -- a deep link, or the
/// jump list's Install Pending Update task -- reaches the primary as argv,
/// and nothing else in the forwarded path reads it. The WinUI assembly
/// cannot load in a test host, so what these pin is shape, the way
/// LaunchCommandWiringTests pins the bare-launch arm beside which this one
/// sits. The argv scan itself is unit-tested in ProtocolLaunchTests; the
/// latch it feeds, in ProtocolActivationRelayTests.
/// </summary>
public sealed class ProtocolForwardWiringTests
{
    private static ShellSource App => ShellSource.Load("App.xaml.cs");

    [Fact]
    public void OpenWindowFromLaunch_RoutesAForwardedUri_BeforeThePlainWindowArms()
    {
        var open = App.Method("OpenWindowFromLaunch");

        // The same argv scan a cold start runs: the URI reaches the primary
        // as --uri, because ProbeActivation ran in the secondary that
        // forwarded the launch, and the primary never sees that secondary's
        // activation arguments.
        var parse = open.CallEndingWith("ProtocolLaunch.ParseUri");

        // The parsed URI is routed, not just read: latched for the consumer
        // the same way a toast click is.
        var note = open.Call("ProtocolActivations.Note");

        // The routing belongs to the declaration it decodes, not to some
        // other variable that happens to be in scope.
        var declarator = open.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "forwardedUri");
        Assert.Equal(parse.ToString(), declarator.Initializer!.Value.ToString());

        // The note sits inside an if on the parse result AND on the absence
        // of a forwarded -e command: a launch that carries a command is a
        // window launch that happens to mention a URI, and keeps the base
        // behaviour (a window running the tool). Only a launch that is ONLY
        // a URI routes. The then-branch is a bare return: a URI launch is
        // not a window launch, so nothing in the branch may fall through to
        // the window builder, the held session, or the jump list. Falling
        // through is the defect: the forward degrades into a plain second
        // window and the URI is gone.
        var guard = note.Ancestors().OfType<IfStatementSyntax>().First();
        Assert.Equal(
            "forwardedUri is not null && forwardedCommand is null",
            guard.Condition.ToString());
        var then = guard.Statement;
        Assert.Contains(then.DescendantNodes().OfType<ReturnStatementSyntax>(),
            r => r.Expression is null);
        Assert.Empty(then.Calls("OpenJumpListWindow"));
        Assert.Empty(then.Calls("RestoreHeldSession"));
        Assert.Empty(then.Calls("HandleJumpListLaunch"));

        // Toast markers keep their existing priority: the liveness arm above
        // still decides first.
        var liveness = open.Call("AnyWindowHasToastSurface");
        Assert.True(liveness.SpanStart < parse.SpanStart);

        // And the URI arm decides before the jump-list parse: the Install
        // Pending Update task carries no --jumplist flag, so the parse below
        // reads its argv as a bare launch and opens a plain window.
        var jumplist = open.CallEndingWith("JumpListLaunch.Parse");
        Assert.True(parse.SpanStart < jumplist.SpanStart);
    }

    // Nothing between the liveness arm and the parse arm may return early.
    // The parse arm is only reached through the fall-through of the arm
    // above it, so a return in between (a re-added unconditional honour of
    // the toast click, a misplaced restore) makes the URI routing
    // unreachable for exactly the launches it exists for -- with every unit
    // test still green, because the shape of the two surviving arms is
    // unchanged.
    [Fact]
    public void NoEarlyReturn_SitsBetweenTheLivenessArm_AndTheParseArm()
    {
        var open = App.Method("OpenWindowFromLaunch");

        var livenessIf = open.Call("AnyWindowHasToastSurface")
            .Ancestors().OfType<IfStatementSyntax>().First();
        var parse = open.CallEndingWith("ProtocolLaunch.ParseUri");

        var early = open.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Where(r => r.SpanStart > livenessIf.Span.End && r.Span.End < parse.SpanStart)
            .ToList();
        Assert.Empty(early);
    }

    [Fact]
    public void App_ExposesTheRelay_AsAProcessLifetimeStatic()
    {
        var property = App.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "ProtocolActivations");

        Assert.True(
            property.Modifiers.Any(SyntaxKind.StaticKeyword),
            "the relay must be a process-lifetime static, like ToastActivations: a forwarded URI can arrive at any point in the app's life");
        Assert.Contains("ProtocolActivationRelay", property.Type.ToString());
    }

    [Fact]
    public void Teardown_ResetsTheProtocolRelay()
    {
        var teardown = App.Method("OnAnyWindowClosedInternal");

        // Same reason as the toast relay's reset: the relay outlives the
        // objects that subscribe to it, and a handler left attached roots
        // them for the life of the process.
        var toast = teardown.Call("ToastActivations.Reset");
        var protocol = teardown.Call("ProtocolActivations.Reset");
        var host = teardown.Call("_bootstrapHost?.Dispose");

        Assert.True(protocol.SpanStart > toast.SpanStart);
        Assert.True(protocol.SpanStart < host.SpanStart);
    }
}
