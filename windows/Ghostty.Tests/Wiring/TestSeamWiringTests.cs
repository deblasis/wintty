using System.Collections.Generic;
using System.Linq;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The seam's gates ARE its safety, so they are what these pin.
///
/// The seam is a named pipe that drives the app's real handlers, and one of
/// its ops (send-text) hands arbitrary bytes to a live shell. Whatever can
/// talk to the pipe can therefore run commands as the user. Four things keep
/// that from being a hole, and each has a test here: the build gate (a
/// shipping binary has no seam at all), the session token (the pipe's name is
/// a secret, not an address), the pipe's ACL (this user and nobody else), and
/// send-text's own second opt-in.
///
/// What they can catch: the build gate deleted or widened to Release, the
/// token gate inverted or slackened back to a fixed name, the ACL flag
/// dropped, send-text ungated, the request reader losing its length cap, the
/// server started unconditionally, the window closing without the server
/// dying, and a command bypassing the UI-thread marshal.
///
/// What they cannot catch: whether a command really drives the same handlers
/// the pointer path does. That is what the seam acceptance script proves
/// against the running app. The reader's cap is likewise a wiring claim here
/// and a behavioural one only against a live pipe -- the shell assembly
/// cannot be loaded into this host, so nothing here executes seam code.
/// </summary>
public class TestSeamWiringTests
{
    /// <summary>
    /// Assert that <paramref name="span"/> sits inside this file's one
    /// <c>#if TESTSEAM</c> region.
    ///
    /// This is the guard that says a shipping build carries none of it.
    /// ShellSource parses with TESTSEAM defined -- it must, or every scan
    /// below would be reading an empty file -- so the code is visible here
    /// and the directives survive as trivia. Reading the directives back is
    /// the only way to check, from a parse that sees the region, that the
    /// region exists at all.
    /// </summary>
    private static void AssertInsideTheBuildGate(SyntaxNode root, TextSpan span, string what)
    {
        var directives = root.DescendantTrivia()
            .Where(t => t.IsKind(SyntaxKind.IfDirectiveTrivia)
                        || t.IsKind(SyntaxKind.EndIfDirectiveTrivia)
                        || t.IsKind(SyntaxKind.ElseDirectiveTrivia)
                        || t.IsKind(SyntaxKind.ElifDirectiveTrivia))
            .OrderBy(t => t.SpanStart)
            .ToList();

        var opens = directives
            .Where(t => t.IsKind(SyntaxKind.IfDirectiveTrivia)
                        && ((IfDirectiveTriviaSyntax)t.GetStructure()!)
                            .Condition.ToString() == "TESTSEAM")
            .ToList();
        Assert.True(
            opens.Count == 1,
            $"expected exactly one '#if TESTSEAM' region, found {opens.Count}. "
            + "The seam's absence from a shipping build is what that region is for.");

        // Walk to the #endif that closes it, counting nesting, so an unrelated
        // conditional inside the region cannot be mistaken for the close.
        var open = opens[0];
        var depth = 0;
        TextSpan? close = null;
        foreach (var t in directives.Where(t => t.SpanStart >= open.SpanStart))
        {
            if (t.IsKind(SyntaxKind.IfDirectiveTrivia)) depth++;
            else if (t.IsKind(SyntaxKind.EndIfDirectiveTrivia))
            {
                depth--;
                if (depth == 0) { close = t.Span; break; }
            }
            else if (depth == 1)
            {
                // An #else or #elif would leave a branch this parse cannot
                // see, and ShellSource.Load already refuses that; catching it
                // here names the reason.
                Assert.Fail($"the '#if TESTSEAM' region has an {t.Kind()}, which "
                    + "splits it into a branch the wiring scans cannot read");
            }
        }
        Assert.True(close is not null, "the '#if TESTSEAM' region is never closed");
        Assert.True(
            span.Start > open.Span.End && span.End < close!.Value.Start,
            $"{what} is outside the '#if TESTSEAM' region, so a shipping build "
            + "would still carry it");
    }

    private static void CtorCallsTestSeamStart()
    {
        var window = ShellSource.Load("MainWindow.xaml.cs");
        var calls = window.Root.Calls("Testing.TestSeam.Start");
        Assert.True(
            calls.Count == 1,
            $"expected exactly one TestSeam.Start call in MainWindow.xaml.cs, " +
            $"found {calls.Count}");

        var ctor = window.Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Where(c => c.Identifier.ValueText == "MainWindow"
                        && c.Span.Contains(calls[0].Span))
            .ToList();
        Assert.True(
            ctor.Count == 1,
            "TestSeam.Start is not called from exactly one MainWindow constructor");

        // The call site is inside the build gate too. Compiling the seam out
        // while leaving the call behind does not build, but leaving the call
        // OUTSIDE the gate while the seam stays in is exactly how the gate
        // gets quietly reverted.
        AssertInsideTheBuildGate(
            window.Root, calls[0].Span, "the TestSeam.Start call");
    }

    [Fact]
    public void TheSeamIsGated_OnTheBuild_AndOnASessionToken()
    {
        CtorCallsTestSeamStart();

        var source = ShellSource.Load("Testing.TestSeam.cs");
        var start = source.Method("Start");

        // Everything the seam does lives behind the build gate. Start is the
        // door, so pinning Start pins the rest.
        AssertInsideTheBuildGate(source.Root, start.Span, "TestSeam.Start");

        // Two env reads, one per variable, both named through their consts so
        // a rename cannot split the gate from the name it reads.
        var reads = start.Calls("Environment.GetEnvironmentVariable");
        Assert.True(
            reads.Count == 2,
            $"expected exactly two env-var reads in TestSeam.Start (the session "
            + $"token and the send-text opt-in), found {reads.Count}");
        Assert.Equal(
            new[] { "EnvVar", "InputEnvVar" },
            reads.Select(r => r.ArgumentList.Arguments[0].ToString()).OrderBy(s => s).ToArray());

        foreach (var (constName, spelling) in new[]
        {
            ("EnvVar", "WINTTY_TEST_SEAM"),
            ("InputEnvVar", "WINTTY_TEST_SEAM_INPUT"),
        })
        {
            var field = source.Field(constName).Variable;
            Assert.True(
                field.Initializer is not null
                && field.Initializer.Value.ToString().Contains(spelling, StringComparison.Ordinal),
                $"the seam's {constName} const no longer names {spelling}");
        }

        // The gate is the FIRST thing Start does, and it demands a session
        // token rather than a magic word. A fixed opt-in value would put the
        // pipe back at a name anything on the box can guess and squat.
        var guard = start.Body!.Statements.OfType<IfStatementSyntax>().FirstOrDefault();
        Assert.True(guard is not null, "TestSeam.Start has no gate guard");
        var call = Assert.IsType<InvocationExpressionSyntax>(
            Assert.IsType<PrefixUnaryExpressionSyntax>(guard!.Condition).Operand);
        Assert.Equal("IsSessionToken", call.CalleeText());

        // And the token really is a token: an exact length and a closed
        // alphabet. "at least N" or an open alphabet would let "1" back in
        // and would also stop guaranteeing the value is safe to paste into a
        // pipe name.
        var predicate = source.Method("IsSessionToken").ToString();
        Assert.Contains("TokenLength", predicate, StringComparison.Ordinal);
        Assert.Contains("!=", predicate, StringComparison.Ordinal);
        Assert.Contains("IsAsciiHexDigit", predicate, StringComparison.Ordinal);
        Assert.Equal(
            "32", source.Field("TokenLength").Variable.Initializer!.Value.ToString());

        // The name is built from the token, so it cannot be a fixed const any
        // more. A literal pipe name reappearing here is the regression.
        var pipeName = source.Field("_pipeName");
        Assert.Null(pipeName.Variable.Initializer);
        var assignment = Assert.Single(start.AssignsTo("_pipeName"));
        Assert.Contains("PipeNamePrefix", assignment.Right.ToString(), StringComparison.Ordinal);
        Assert.Contains("sessionToken", assignment.Right.ToString(), StringComparison.Ordinal);

        // And the gate has to run before the server does: nothing may spawn
        // a pipe from Start ahead of it.
        var guardEnd = guard.Span.End;
        var server = start.Calls("ServeAsync");
        Assert.True(
            server.Count == 1 && server[0].Span.Start > guardEnd,
            "TestSeam.Start reaches the pipe server before the opt-in gate");
    }

    [Fact]
    public void ThePipeLifecycle_IsBoundedByTheWindow()
    {
        var source = ShellSource.Load("Testing.TestSeam.cs");

        // One server loop, and it is the only place a pipe exists.
        var serve = source.Method("ServeAsync");
        var creations = serve.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(c => c.Type.ToString().Contains("NamedPipeServerStream"))
            .ToList();
        Assert.True(
            creations.Count == 1,
            "expected exactly one NamedPipeServerStream creation, in ServeAsync");

        // The ACL. Without CurrentUserOnly the DACL Windows puts on an
        // unsecured pipe grants Everyone and ANONYMOUS LOGON generic read,
        // which is enough for another account on the box (or an authenticated
        // SMB peer, since named pipes answer on \\host\pipe\) to take the one
        // server instance and hold it. ThePipeAclAdmitsThisUserAlone proves
        // the flag still means that; this proves it is still passed.
        //
        // Matched as a syntax node rather than as text: a substring search
        // over the argument would be satisfied by
        // `PipeOptions.Asynchronous /* | PipeOptions.CurrentUserOnly */`,
        // which is the removal it exists to catch.
        var options = creations[0].ArgumentList!.Arguments.Last().Expression;
        var flags = options.DescendantNodesAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .Select(m => m.ToString())
            .ToList();
        Assert.Contains("PipeOptions.CurrentUserOnly", flags);
        Assert.Contains("PipeOptions.Asynchronous", flags);

        // The name comes off the field the token built, never a literal.
        Assert.Equal("_pipeName!", creations[0].ArgumentList!.Arguments[0].ToString());
        Assert.True(
            serve.Calls("pipe.WaitForConnectionAsync").Count == 1,
            "the server loop no longer waits for exactly one connection per pipe");
        Assert.True(
            serve.Calls("pipe.Dispose").Count == 1,
            "the server no longer disposes the pipe between connections");

        // A name owned by another opted-in instance STOPS the server: one
        // seam per machine. The creation-failure catch must return -- a
        // continue here recreates the same refused pipe as fast as the
        // loop can spin -- and the connection-level catch must NOT return,
        // because a client hanging up is not the server's death. Both
        // catches declare Exception and discriminate in the filter.
        var catches = serve.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.CatchClauseSyntax>()
            .Where(c => c.Filter is not null
                        && c.Filter!.FilterExpression.ToString().Contains("IOException"))
            .ToList();
        var creation = catches.Single(c => c.Filter!.FilterExpression.ToString()
            .Contains("UnauthorizedAccessException"));
        Assert.Contains(
            creation.Block.Statements,
            s => s is Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax);
        var wait = serve.Calls("pipe.WaitForConnectionAsync").Single();
        Assert.True(
            creation.Span.End <= wait.Span.Start,
            "the name-taken refusal must guard the pipe creation, before the "
            + "connection wait it must never reach");
        var serving = catches.Single(c => c.Filter!.FilterExpression.ToString()
            .Contains("ObjectDisposedException"));
        Assert.DoesNotContain(
            serving.Block.Statements,
            s => s is Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax);

        // Start subscribes the window's close to the server's cancellation,
        // so a closed window cannot leave a listening pipe behind.
        var start = source.Method("Start");
        Assert.True(
            start.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax
                .MemberAccessExpressionSyntax>().Count(m =>
                    m.Name.Identifier.ValueText == "Closed") == 1,
            "TestSeam.Start no longer subscribes exactly one window.Closed");
        Assert.True(
            start.Calls("Task.Run").Count == 1,
            "TestSeam.Start no longer runs the server as exactly one background task");

        // The marshal is the whole fidelity story: every command funnels
        // through the one dispatcher hop, and the drag handoff runs below
        // the drag tick's priority.
        var marshal = source.Method("RunOnUiThreadAsync");
        Assert.True(
            marshal.Calls("window.DispatcherQueue.TryEnqueue").Count == 1,
            "the UI marshal is no longer exactly one TryEnqueue");
        var execute = source.Method("ExecuteAsync");
        Assert.True(
            execute.Calls("RunOnUiThreadAsync").Count == 1,
            "commands no longer funnel through the single UI marshal");
    }

    /// <summary>
    /// send-text is the op that is not "drive the UI": it hands bytes to a
    /// live shell, which is running commands as the user. It carries its own
    /// opt-in so that reaching the pipe is not by itself a shell, and the
    /// refusal has to be the FIRST thing the case does -- a check after the
    /// text has already been dispatched is not a gate.
    /// </summary>
    [Fact]
    public void SendText_IsBehindItsOwnSecondOptIn()
    {
        var source = ShellSource.Load("Testing.TestSeam.cs");
        var sendText = source.Case("ExecuteOnUiThreadAsync", "send-text");

        var statements = sendText.Statements
            .OfType<BlockSyntax>().SelectMany(b => b.Statements)
            .Concat(sendText.Statements.Where(s => s is not BlockSyntax))
            .ToList();
        var first = Assert.IsType<IfStatementSyntax>(statements.First());
        Assert.Equal(
            "!_inputAllowed",
            first.Condition.ToString());
        Assert.Contains(
            "InputEnvVar",
            first.Statement.ToString(),
            StringComparison.Ordinal);

        // The flag it reads is set once, from the second env var, and nowhere
        // else: a second writer is how a gate ends up open by default.
        var writes = source.Root.AssignsTo("_inputAllowed").ToList();
        var write = Assert.Single(writes);
        var read = Assert.IsType<InvocationExpressionSyntax>(write.Right is BinaryExpressionSyntax b
            ? b.Left
            : write.Right);
        Assert.Equal("Environment.GetEnvironmentVariable", read.CalleeText());
        Assert.Equal("InputEnvVar", read.Arg(0));

        // And nothing else in the seam consults it, so the gate is exactly one
        // op wide and cannot have quietly become the gate for everything.
        var mentions = source.Root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Count(i => i.Identifier.ValueText == "_inputAllowed");
        Assert.Equal(2, mentions); // the one write, the one read
    }

    /// <summary>
    /// The request reader has a ceiling.
    ///
    /// StreamReader.ReadLineAsync does not: it buffers until a newline
    /// arrives, so a client that opens the pipe and streams bytes without one
    /// walks the whole terminal out of memory without ever dispatching an op.
    /// The cap has to sit under the reader, which means the reader cannot be
    /// a StreamReader.
    /// </summary>
    [Fact]
    public void Requests_AreLengthCapped_BeforeAnythingParsesThem()
    {
        var source = ShellSource.Load("Testing.TestSeam.cs");
        var serve = source.Method("ServeConnectionAsync");

        var reader = Assert.Single(
            serve.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
            c => c.Type.ToString() == "BoundedLineReader");
        Assert.Equal("MaxRequestBytes", reader.ArgumentList!.Arguments[1].ToString());

        // No StreamReader over the pipe anywhere in the seam: the unbounded
        // reader coming back is the regression, and it comes back by someone
        // reaching for the obvious type.
        Assert.DoesNotContain(
            source.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
            c => c.Type.ToString() == "StreamReader");

        // The cap is a real number, not a sentinel that disables it.
        var cap = source.Field("MaxRequestBytes").Variable.Initializer!.Value.ToString();
        Assert.Equal("64 * 1024", cap);

        // An overlong line ends the connection rather than being trimmed and
        // parsed: the bytes after the cap are of unknown shape, and treating
        // whatever follows as the next request turns a length bug into a
        // parsing bug.
        var tooLong = Assert.Single(
            serve.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString().Contains("LineStatus.TooLong", StringComparison.Ordinal));
        Assert.Contains(
            tooLong.Statement.DescendantNodesAndSelf(),
            n => n is ReturnStatementSyntax);
    }

    /// <summary>
    /// The build gate: a shipping Release defines no TESTSEAM, so every seam
    /// op compiles to nothing and a binary a user installs has no pipe to
    /// reach. The `#if` regions the other tests pin are only as good as the
    /// property that decides the symbol, and that property lives in MSBuild
    /// where no C# scan can see it.
    ///
    /// Read as XML rather than as text, so a commented-out property or a
    /// condition moved into an attribute cannot pass on a substring.
    /// </summary>
    [Fact]
    public void TheSeam_IsCompiledOutOfShippingBuilds()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(
            "Ghostty.Tests.Build.Directory.Build.targets")!;
        var doc = System.Xml.Linq.XDocument.Load(stream);

        var enabled = Assert.Single(
            doc.Descendants(), e => e.Name.LocalName == "TestSeamEnabled");
        var condition = enabled.Attribute("Condition")?.Value;
        Assert.False(
            string.IsNullOrWhiteSpace(condition),
            "TestSeamEnabled has no Condition, so every build defines TESTSEAM "
            + "and the seam ships");
        // Debug, or an explicit opt-in. Anything else -- notably a condition
        // that also admits Release -- is the gate gone.
        Assert.Contains("'$(Configuration)' == 'Debug'", condition!, StringComparison.Ordinal);
        Assert.Contains("'$(TestSeam)' == 'true'", condition!, StringComparison.Ordinal);
        Assert.DoesNotContain("Release", condition!, StringComparison.Ordinal);

        // And the symbol is defined only when that property says so.
        //
        // Selected by CONDITION, not by value. Selecting on "the value
        // mentions TESTSEAM" matched a second element the moment
        // TESTSEAM_OPTIN arrived, and the file legitimately has more than one
        // constant in that family; the gate this test is about is the one
        // keyed off TestSeamEnabled.
        var define = Assert.Single(
            doc.Descendants(),
            e => e.Name.LocalName == "DefineConstants"
                 && (e.Attribute("Condition")?.Value
                        .Contains("TestSeamEnabled", StringComparison.Ordinal) ?? false));
        Assert.Equal(
            "'$(TestSeamEnabled)' == 'true'",
            define.Attribute("Condition")?.Value);
        Assert.Contains("TESTSEAM", define.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// What CurrentUserOnly actually produces, asked of the kernel rather
    /// than of the documentation.
    ///
    /// This is the one test here that runs rather than reads. It stands up a
    /// pipe with the seam's own options and reads back the DACL Windows put
    /// on it, because the finding that started this work was that the DEFAULT
    /// DACL grants Everyone and ANONYMOUS LOGON generic read -- which no
    /// amount of reading the constructor would have told anyone. If a future
    /// runtime changes what the flag means, the flag being present in the
    /// source stops being evidence and only this notices.
    /// </summary>
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ThePipeAclAdmitsThisUserAlone()
    {
        // xunit 2.9 has no runtime skip. The whole suite is Windows-only in
        // practice, so this guard is for a hypothetical host rather than a
        // configuration anyone runs; on Windows it never returns early.
        if (!OperatingSystem.IsWindows()) return;

        var world = new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.WorldSid, null);
        var anonymous = new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.AnonymousSid, null);
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;

        // The unsecured pipe first, so this test is an oracle rather than an
        // assertion that could pass because the probe returned nothing useful.
        // This is the finding, reproduced: Everyone and ANONYMOUS LOGON in the
        // DACL of a pipe created exactly the way the seam used to create one.
        var unsecured = GrantedSids(System.IO.Pipes.PipeOptions.Asynchronous);
        Assert.Contains(world, unsecured);
        Assert.Contains(anonymous, unsecured);

        // And the same constructor with the flag the seam now passes.
        var secured = GrantedSids(
            System.IO.Pipes.PipeOptions.Asynchronous
            | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        Assert.DoesNotContain(world, secured);
        Assert.DoesNotContain(anonymous, secured);
        Assert.Equal(new[] { me }, secured);
    }

    /// <summary>
    /// The SIDs an access-allowed ACE names on a pipe created with these
    /// options, read back off the handle.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<System.Security.Principal.SecurityIdentifier> GrantedSids(
        System.IO.Pipes.PipeOptions options)
    {
        var name = "wintty-seam-acl-" + System.Guid.NewGuid().ToString("N");
        using var pipe = new System.IO.Pipes.NamedPipeServerStream(
            name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, options);

        var descriptor = new System.Security.AccessControl.RawSecurityDescriptor(
            PipeSecurityProbe.Sddl(pipe.SafePipeHandle));
        Assert.NotNull(descriptor.DiscretionaryAcl);
        return descriptor.DiscretionaryAcl!
            .OfType<System.Security.AccessControl.CommonAce>()
            .Where(a => a.AceType == System.Security.AccessControl.AceType.AccessAllowed)
            .Select(a => a.SecurityIdentifier)
            .ToList();
    }

    /// <summary>
    /// The fuzz suite's gesture commands are only honest while they drive
    /// the strip's REAL pointer handlers: each op routes to its one strip
    /// walker, select goes through the manager's own activation, and the
    /// shared walk feeds DragMove under the seam's pointer id with the
    /// Low-priority handoff per tick -- never a second implementation of
    /// the grammar.
    /// </summary>
    [Fact]
    public void TheGestureOps_DriveTheStripsRealHandlers()
    {
        var seam = ShellSource.Load("Testing.TestSeam.cs");
        var dispatch = seam.Method("ExecuteOnUiThreadAsync");
        Assert.Single(dispatch.Calls("strip.TestSeamDragPacedAsync"));
        Assert.Single(dispatch.Calls("strip.TestSeamDragZoneAsync"));
        Assert.Single(dispatch.Calls("strip.TestSeamDragToHeaderAsync"));
        Assert.Single(dispatch.Calls("manager.Activate"));

        var strip = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");
        var walk = strip.Method("SeamWalkAsync");
        var move = walk.Calls("DragMove").Single();
        Assert.Contains("TestSeamPointerId", move.ToString());
        Assert.Single(walk.Calls("Testing.TestSeam.WaitForLowPriorityAsync"));
        // Wall-clock pacing is the walker's own optional tick, for the
        // filming driver; the settle handoff above stays unconditional.
        Assert.Contains("Task.Delay(tickDelayMs)", walk.ToString());
        foreach (var name in new[]
        {
            "TestSeamDragPacedAsync", "TestSeamDragZoneAsync", "TestSeamDragToHeaderAsync",
        })
        {
            var walker = strip.Method(name);
            Assert.Single(walker.Calls("DragPress"));
            Assert.Single(walker.Calls("DragRelease"));
        }
    }

    /// <summary>
    /// Every walker that must cross aims with the machine's own numbers,
    /// at every site: Evaluate's inequality is strict (center PLUS the
    /// token), so a walker aiming AT a slot center stalls one token short
    /// of its final commit -- the exact regression the base walker
    /// shipped. The base and paced walkers must overshoot past the center
    /// in the travel direction by TabStripMotion.CrossingHysteresisPx (a
    /// literal would fall silently behind a token change), the zone walk
    /// overshoots by the same token, and the header walk re-reads the
    /// header's live center every tick, because crossings churn the list
    /// under the walk.
    /// </summary>
    [Fact]
    public void TheBoundaryAndHeaderWalks_AimWithTheMachinesOwnNumbers()
    {
        var strip = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");
        foreach (var name in new[]
        {
            "TestSeamDragAsync", "TestSeamDragPacedAsync", "TestSeamDragZoneAsync",
        })
        {
            Assert.Contains(
                "TabStripMotion.CrossingHysteresisPx",
                strip.Method(name).ToString());
        }
        // The slot walkers must aim PAST the center in the travel
        // direction, not merely mention the token somewhere.
        foreach (var name in new[] { "TestSeamDragAsync", "TestSeamDragPacedAsync" })
        {
            Assert.Contains("Math.Sign(to - from)", strip.Method(name).ToString());
        }

        var header = strip.Method("TestSeamDragToHeaderAsync");
        var headerWalk = header.Calls("SeamWalkAsync").Single();
        Assert.Contains("HeaderCenterY(group)", headerWalk.ToString());
    }

    /// <summary>
    /// The filming driver aligns frames to the paced walk's own clock, so
    /// the commit timestamp must come from the manager index moving --
    /// gesture truth, not a schedule -- and the drag response must carry
    /// it out, along with the release stamp.
    /// </summary>
    [Fact]
    public void ThePacedWalk_TimestampsTheCommit_AndTheResponseCarriesIt()
    {
        var strip = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");
        var paced = strip.Method("TestSeamDragPacedAsync").ToString();
        Assert.Contains("outcome.ReleaseMs = clock.ElapsedMilliseconds", paced);

        // The commit stamp must be taken INSIDE the walk closure, where
        // it lands on the tick the manager index moved -- the earliest
        // honest reading. The post-walk fallback alone stamps LATE,
        // which SHRINKS the measured gap and breaks the oracle's
        // "a flattering gap is impossible" polarity.
        var walked = strip.Method("TestSeamDragPacedAsync").DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax>()
            .Single(f => f.Identifier.ValueText == "Walked").ToString();
        Assert.Contains("_manager.IndexOf(tab) != from", walked);
        Assert.Contains("outcome.CommitMs = clock.ElapsedMilliseconds", walked);

        var seam = ShellSource.Load("Testing.TestSeam.cs");
        var response = seam.Method("DragJson").ToString();
        Assert.Contains("\"commitMs\"", response);
        Assert.Contains("\"releaseMs\"", response);

        // And the state block names the active tab through the manager's
        // own index, for the guard scenario that asserts the fold moved
        // nothing.
        var state = seam.Method("WriteState");
        var active = state.Calls("manager.IndexOf").Single();
        Assert.Equal("manager.ActiveTab", active.ArgumentList.Arguments[0].ToString());
    }
}
