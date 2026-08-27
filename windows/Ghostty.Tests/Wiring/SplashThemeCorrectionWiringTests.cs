using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The splash paints before any config exists, so its first colour is a
/// guess, and the shell corrects it once the theme has resolved. Every part
/// of that is timing and pixels, which no test host can see: these guards
/// prove the pieces are still joined the way the fix left them.
///
/// What each one is defending against is a change that still compiles, still
/// runs, and only shows up as a splash in last session's colour or a splash
/// with nothing drawn on it.
/// </summary>
public class SplashThemeCorrectionWiringTests
{
    private static ShellSource Splash() => ShellSource.Load("Shell.SplashWindow.cs");

    /// <summary>
    /// The resolved background reaches the splash from where config is
    /// built, which is the earliest point in the process that knows it.
    /// Moving this call later is the mutation that matters: further down
    /// <c>OnLaunched</c> the window is already being constructed, and the
    /// correction would land under a frame instead of before one.
    /// </summary>
    [Fact]
    public void TheResolvedBackground_IsHandedOver_AsSoonAsConfigExists()
    {
        var launched = ShellSource.Load("App.xaml.cs").Method("OnLaunched");
        var adopt = launched.Call("Ghostty.Shell.SplashWindow.AdoptBackground");

        // The terminal's colour, not the desktop's and not a constant.
        Assert.Equal("_configService.BackgroundColor", adopt.Arg(0));

        // Statement order inside the method body, so "as soon as" is checked
        // rather than merely "somewhere in here".
        var statements = launched.Body!.Statements;
        var configBuilt = statements.TakeWhile(
            s => !s.ToString().Contains("new ConfigService(")).Count();
        var handedOver = statements.TakeWhile(
            s => !s.ToString().Contains("SplashWindow.AdoptBackground")).Count();
        var firstWindow = statements.TakeWhile(
            s => !s.ToString().Contains("new MainWindow(")).Count();

        // TakeWhile returns the whole list when it never matches, which would
        // make both comparisons below true by accident and pin nothing.
        Assert.True(firstWindow < statements.Count, "no window is built in this method");
        Assert.True(handedOver < statements.Count, "the hand-over is not a statement of this method");

        Assert.True(configBuilt < handedOver, "the colour is read before config resolves it");
        Assert.True(
            handedOver < firstWindow,
            "the correction is published after the first window is built, which is too "
                + "late for it to finish before a frame is revealed");
    }

    /// <summary>
    /// Nothing moves when the guess was right. Deleting the comparison
    /// leaves a cross-dissolve from a colour to itself on every launch:
    /// invisible, and several frames of startup spent producing it.
    /// </summary>
    [Fact]
    public void TheCorrection_IsSkipped_WhenTheColourAlreadyMatches()
    {
        var apply = Splash().Method("ApplyResolvedBackground");

        var guard = apply.Body!.Statements
            .OfType<IfStatementSyntax>()
            .Single(s => s.Condition.ToString() == "resolved == _background");
        Assert.IsType<ReturnStatementSyntax>(guard.Statement);

        // And it guards the transition rather than sitting after it.
        var recolour = apply.Call("Recolour");
        Assert.True(
            guard.Span.End < recolour.Span.Start,
            "the equality check does not precede the transition it is meant to skip");
    }

    /// <summary>
    /// The composed bitmap is keyed on the background as well as on the size
    /// and the scale. Without it a recolour asks for a rebuild, gets the
    /// bitmap that is already there, and dissolves one frame into a copy of
    /// itself.
    /// </summary>
    [Fact]
    public void TheComposedBitmap_IsInvalidatedByAColourChange()
    {
        var ensure = Splash().Method("EnsureSurface");

        var reuse = ensure.Body!.Statements
            .OfType<IfStatementSyntax>()
            .Single(s => s.Condition.ToString().Contains("_surfaceDib != 0"));

        var terms = reuse.Condition.DescendantNodesAndSelf()
            .OfType<BinaryExpressionSyntax>()
            .Select(b => b.ToString())
            .ToList();
        Assert.Contains("_surfaceBackground == background", terms);
    }

    /// <summary>
    /// The bitmap for the new size is composed before the window is moved to
    /// it. A layered window keeps its old surface across a resize, so doing
    /// this the other way round leaves the grown area empty and the black
    /// window the splash is covering showing through it for the length of a
    /// full compose.
    /// </summary>
    [Fact]
    public void TheSurface_IsComposed_BeforeTheWindowIsResized()
    {
        var follow = Splash().Method("FollowTrackedWindow");

        var compose = follow.Call("EnsureSurface");
        var move = follow.Call("SetWindowPos");
        Assert.True(
            compose.Span.End < move.Span.Start,
            "the resize happens before the bitmap for it exists");

        // Only on a resize: a plain move keeps the surface it has, and
        // composing on every tracked frame would rebuild through a drag.
        var onResize = compose.Ancestors().OfType<IfStatementSyntax>().First();
        Assert.Equal("resized", onResize.Condition.ToString());
    }

    /// <summary>
    /// The transition holds the splash thread, so it owes the same
    /// re-assertion of topmost that the loop it was called from does.
    /// </summary>
    /// <remarks>
    /// This is not theoretical. Stretching the transition to four seconds and
    /// watching it showed the main window come up over the splash and stay
    /// there for the rest of it: WinUI shows and activates its window
    /// somewhere inside exactly this stretch, and a loop that does not nudge
    /// spends its whole length behind the window whose colour it is
    /// correcting.
    /// </remarks>
    [Fact]
    public void TheTransition_KeepsTheSplashOnTop()
    {
        var splash = Splash();

        var loop = splash.Method("Recolour")
            .DescendantNodes().OfType<WhileStatementSyntax>().Single();
        Assert.Single(loop.Calls("NudgeTopmostIfDue"));

        // And the loop it hands off from still does it too.
        Assert.Single(splash.Method("PumpUntilDismissed").Calls("NudgeTopmostIfDue"));

        // One schedule between them rather than one each. A per-loop clock
        // would be a local, and there would be no field here to find.
        var (_, field) = splash.Field("_nextTopmostNudge");
        Assert.Contains("static", field.Modifiers.ToString());
    }

    /// <summary>
    /// How the transition ends decides what the reveal uncovers.
    /// </summary>
    /// <remarks>
    /// A dismissal means the app frame is ready and the fade is next, so the
    /// loop has to leave and land on the target: the window underneath is
    /// that colour, and fading out the half-blended guess is the flash this
    /// whole thing exists to remove. <c>HideNow</c> is the opposite case --
    /// the process is ending, its caller is on the join, and there is no
    /// frame left to match -- so that one abandons.
    ///
    /// Both are one keyword, and swapping either compiles.
    /// </remarks>
    [Fact]
    public void TheTransition_FinishesOnADismissal_AndAbandonsOnAnExit()
    {
        var recolour = Splash().Method("Recolour");
        var loop = recolour.DescendantNodes().OfType<WhileStatementSyntax>().Single();

        var exits = loop.Statement.DescendantNodesAndSelf().OfType<IfStatementSyntax>().ToList();

        var onDismissal = exits.Single(s => s.Condition.ToString() == "_dismissed.IsSet");
        Assert.IsType<BreakStatementSyntax>(onDismissal.Statement);

        var onExit = exits.Single(s => s.Condition.ToString().Contains("_skipFade"));
        Assert.IsType<ReturnStatementSyntax>(onExit.Statement);

        // The break lands here, so the frame left on screen is the target.
        var landing = recolour.Body!.Statements.OfType<ExpressionStatementSyntax>()
            .Where(s => s.Expression is InvocationExpressionSyntax i && i.CalleeText() == "Blend")
            .ToList();
        Assert.Single(landing);
        Assert.True(
            landing[0].Span.Start > loop.Span.End,
            "nothing writes the target frame after the loop, so an early exit leaves a "
                + "half-blended one on screen");
    }
}
