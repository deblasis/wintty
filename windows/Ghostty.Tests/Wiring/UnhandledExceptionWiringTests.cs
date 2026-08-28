using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That an unhandled exception on a thread that is not the main one still
/// gets reported, on the CLI as well as in the GUI (#442).
///
/// Why this needs a guard at all: nothing about the failure is loud. An
/// unhandled exception off the main thread does not unwind to
/// <c>Main</c>'s catch, and under NativeAOT its fail-fast bypasses
/// <c>SetUnhandledExceptionFilter</c>, so sentry does not see it either.
/// Measured before the fix, `wintty +crash managed-unhandled` on a published
/// build exited 0xC0000409 having written no log and no envelope. Deleting
/// the registration these tests guard restores that silence, and every other
/// test in the suite still passes.
///
/// The pair is what matters, not either half. Program registers so the CLI
/// is covered; App unregisters so the GUI keeps producing exactly one
/// artifact. Registering without the handoff double-reports every GUI crash;
/// the handoff without the registration is what we already had.
///
/// What this cannot see: whether the runtime actually raises the event
/// before dying on a given runtime and configuration. That is only
/// observable by killing a published build, which the crash matrix does.
/// </summary>
public class UnhandledExceptionWiringTests
{
    private const string Subscription = "AppDomain.CurrentDomain.UnhandledException";

    /// <summary>
    /// Every `X += ...` or `X -= ...` under a node, in source order, paired
    /// with the spelling of what is being subscribed to.
    /// </summary>
    private static List<(string Target, SyntaxKind Kind, int Position)> Subscriptions(SyntaxNode node) =>
        node.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                     || a.IsKind(SyntaxKind.SubtractAssignmentExpression))
            .Select(a => (Target: a.Left.ToString(), Kind: a.Kind(), Position: a.SpanStart))
            .OrderBy(x => x.Position)
            .ToList();

    [Fact]
    public void Main_subscribes_the_fatal_handler_before_it_starts_the_thread()
    {
        var program = ShellSource.Load("Program.cs");
        var main = program.Method("Main");

        var subscribe = Subscriptions(main)
            .Where(s => s.Target == Subscription && s.Kind == SyntaxKind.AddAssignmentExpression)
            .ToList();
        Assert.True(
            subscribe.Count == 1,
            $"expected exactly one '{Subscription} +=' in Main, found {subscribe.Count}");

        // Before the thread runs, not merely somewhere in the method. The
        // whole point is to cover MainImpl, so a registration that happened
        // after main.Start() would leave the run it exists for unguarded and
        // would still satisfy a presence-only assertion.
        var start = main.Call("main.Start");
        Assert.True(
            subscribe[0].Position < start.SpanStart,
            "the handler is registered after the main thread starts, so the run it exists to cover is unguarded");
    }

    [Fact]
    public void The_handler_reports_both_shapes_of_unhandled_throw()
    {
        var program = ShellSource.Load("Program.cs");
        var handler = program.Field("FatalHandler").Variable;

        // Two, not one: ExceptionObject is typed object and is not required
        // to hold an Exception. A handler that reports only the Exception
        // branch drops exactly the throws nothing else can describe, and
        // reads as complete.
        var reports = handler.Calls("ReportFatal");
        Assert.True(
            reports.Count == 2,
            $"expected FatalHandler to report on both the Exception and non-Exception paths, found {reports.Count} ReportFatal call(s)");
    }

    [Fact]
    public void The_handoff_unsubscribes_and_does_not_re_subscribe()
    {
        var program = ShellSource.Load("Program.cs");
        var handoff = program.Method("HandOffUnhandledReporting");

        var subs = Subscriptions(handoff).Where(s => s.Target == Subscription).ToList();
        Assert.True(subs.Count == 1, $"expected one '{Subscription}' subscription change, found {subs.Count}");
        Assert.Equal(SyntaxKind.SubtractAssignmentExpression, subs[0].Kind);
    }

    [Fact]
    public void App_stands_Program_down_only_after_installing_its_own_handlers()
    {
        var app = ShellSource.Load("App.xaml.cs");

        var handoff = app.Root.Call("Program.HandOffUnhandledReporting");

        // Every handler App installs for an unhandled throw, whichever event
        // carries it.
        var installs = Subscriptions(app.Root)
            .Where(s => s.Kind == SyntaxKind.AddAssignmentExpression
                     && (s.Target.Contains("Unhandled", StringComparison.Ordinal)
                      || s.Target.Contains("UnobservedTask", StringComparison.Ordinal)))
            .ToList();
        Assert.True(installs.Count >= 2, $"expected App to install its own handlers, found {installs.Count}");

        // Last, and the ordering is the safety property rather than tidiness.
        // Standing Program down first opens a window in which neither
        // reporter is registered, and a crash in that window is reported by
        // nobody: the exact condition #442 is about, reintroduced in the
        // handoff meant to close it. Overlapping instead costs a duplicate
        // log, which is the direction to err.
        var last = installs.Max(i => i.Position);
        Assert.True(
            handoff.SpanStart > last,
            "App hands Program off before installing its own handlers, leaving a window where neither reports");
    }
}
