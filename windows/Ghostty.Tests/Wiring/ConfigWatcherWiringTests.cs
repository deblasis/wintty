using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The config service watches its file through
/// <c>Ghostty.Core.Config.ConfigFileWatcher</c>, whose behaviour against real
/// atomic saves is pinned in <c>Config.ConfigFileWatcherTests</c>. These pin
/// the wiring: that the shell does not grow a second, raw watcher beside it
/// (the raw one is what reloaded into the gap of a swap), that the reload
/// runs on the UI thread inside the watcher's delivery (where the file check
/// sits), that a watcher which fails to start is freed, and that teardown
/// still stops it before the app is freed.
/// </summary>
public class ConfigWatcherWiringTests
{
    private static ShellSource ConfigService() => ShellSource.Load("Services.ConfigService.cs");

    private static ObjectCreationExpressionSyntax[] Creations(Microsoft.CodeAnalysis.SyntaxNode node) =>
        node.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().ToArray();

    private static string[] CreatedTypes(Microsoft.CodeAnalysis.SyntaxNode node) =>
        Creations(node).Select(o => o.Type.ToString()).ToArray();

    [Fact]
    public void StartWatcher_builds_the_atomic_save_aware_watcher()
    {
        var created = CreatedTypes(ConfigService().Method("StartWatcher"));

        Assert.Contains("ConfigFileWatcher", created);
    }

    [Fact]
    public void ConfigService_never_builds_a_raw_FileSystemWatcher()
    {
        var created = CreatedTypes(ConfigService().Root);

        Assert.DoesNotContain(created, t => t.EndsWith("FileSystemWatcher", System.StringComparison.Ordinal));
    }

    [Fact]
    public void The_reload_is_posted_to_the_UI_thread_and_runs_inside_the_delivery()
    {
        var source = ConfigService();

        // The watcher's delivery is what checks the file is present, so the
        // reload must be called from it directly: a second hop would put a
        // dispatcher turn back between the check and the load.
        var settled = source.Method("OnConfigFileSettled");
        Assert.NotEmpty(settled.Calls("Reload"));
        Assert.Empty(settled.Calls("_dispatcher.TryEnqueue"));

        var post = Creations(source.Method("StartWatcher"))
            .Single(o => o.Type.ToString() == "ConfigFileWatcher")
            .ArgumentList!.Arguments
            .Single(a => a.NameColon?.Name.Identifier.ValueText == "post");
        Assert.NotEmpty(post.Calls("_dispatcher.TryEnqueue"));
    }

    [Fact]
    public void The_watcher_timer_logs_through_a_real_logger()
    {
        var timer = Creations(ConfigService().Method("StartWatcher"))
            .Single(o => o.Type.ToString() == "SystemSchedulerTimer");

        Assert.Equal("StaticLoggers.ConfigWatcherTimer",
            timer.ArgumentList!.Arguments.Single().Expression.ToString());
    }

    [Fact]
    public void StartWatcher_frees_the_watcher_when_Start_refuses_or_throws()
    {
        var finallies = ConfigService().Method("StartWatcher")
            .DescendantNodes().OfType<FinallyClauseSyntax>();

        Assert.Contains(finallies, f => f.Calls("watcher.Dispose").Count > 0);
    }

    /// <summary>
    /// Pins behaviour that predates the watcher class (the raw watcher was
    /// stopped here too): kept so the move to ConfigFileWatcher cannot drop
    /// the stop, which is what cancels a pending settle before AppFree.
    /// </summary>
    [Fact]
    public void BeginShutdown_stops_the_watcher()
    {
        var shutdown = ConfigService().Method("BeginShutdown");

        Assert.NotEmpty(shutdown.Calls("StopWatcher"));
    }

    /// <summary>
    /// The half of issue #676 the watcher cannot close. Its delivery checks
    /// the file is present and then loads, and those are two steps: the file
    /// can go between them, because that is exactly the moment an editor's
    /// atomic save is passing through. libghostty answered "no config file
    /// anywhere" by writing its starter template at the config path, so the
    /// loser of that race had the template dropped on top of the save.
    ///
    /// Only the loader can close it, which it does by not creating anything:
    /// ghostty_config_load_default_files reads, and writing the starter file
    /// is its own call. The behaviour is tested in zig, against the real
    /// entry point, in Config.zig. This pins the one thing that is a call
    /// site rather than behaviour: creating belongs to startup alone.
    /// </summary>
    [Fact]
    public void Only_the_constructor_may_create_a_config_file()
    {
        var source = ConfigService();

        var creating = source.Root.Calls("NativeMethods.ConfigCreateDefaultFile");
        Assert.Single(creating);
        Assert.NotNull(creating[0].Ancestors()
            .OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == "ConfigService"));
    }

    /// <summary>
    /// And only on a first run, and never under --no-config.
    /// </summary>
    /// <remarks>
    /// The flag has to mean nothing reads AND nothing writes the config
    /// file, and the starter template is a write: it is how such a run still
    /// left a config file behind on a fresh root. CliAliases sets NoConfig
    /// for either spelling, so this half covers them both, and the path
    /// resolution and the seed write in the same constructor branch on the
    /// same flag. libghostty refuses its create under the flag as well:
    /// that half holds for callers of the export that never see this
    /// shell's command line.
    ///
    /// Neither half has a test that fails without it otherwise: no C# test
    /// drives this constructor, and the libghostty half is behind a flag
    /// that is always empty under a zig test.
    /// </remarks>
    [Fact]
    public void The_created_config_file_is_gated_on_a_first_run_and_on_the_flag()
    {
        static IEnumerable<string> AndOperands(ExpressionSyntax expression) =>
            expression is BinaryExpressionSyntax b
                && b.Kind() == SyntaxKind.LogicalAndExpression
                    ? AndOperands(b.Left).Concat(AndOperands(b.Right))
                    : new[] { expression.ToString().Trim() };

        var create = Assert.Single(ConfigService().Root.Calls("NativeMethods.ConfigCreateDefaultFile"));
        var chain = create.Ancestors().OfType<BinaryExpressionSyntax>()
            .Last(b => b.Kind() == SyntaxKind.LogicalAndExpression);

        var operands = AndOperands(chain).ToList();
        Assert.Contains("!_noConfig", operands);
        Assert.Contains("defaultFiles == ConfigFilesFound.Absent", operands);
    }

    /// <summary>
    /// The one call to the gate in <c>Reload</c>, and the <c>if</c> built
    /// around it.
    /// </summary>
    private static (InvocationExpressionSyntax Decide, IfStatementSyntax Guard) ReloadGuard()
    {
        var decide = Assert.Single(ConfigService().Method("Reload").Calls("ConfigReloadGate.Decide"));
        return (decide, decide.Ancestors().OfType<IfStatementSyntax>().First());
    }

    /// <summary>
    /// Assert that <paramref name="guard"/> refuses on
    /// <paramref name="decision"/> and nothing else.
    /// </summary>
    /// <remarks>
    /// The assertion the rest of this file cannot make. <c>Calls()</c>
    /// resolves the callee and stops there, so every other claim about this
    /// guard is just as true of <c>== ConfigReloadDecision.Apply</c>: the
    /// then-block would still free what it built, still return false, still
    /// resettle, and the app would be one where no reload ever applies.
    /// Nothing else in the suite sees it either, because
    /// <c>ConfigReloadGateTests</c> tests the rule and not its call site and
    /// no test drives <c>Reload</c> end to end.
    /// </remarks>
    private static void AssertRefusesOn(IfStatementSyntax guard, string decision)
    {
        var condition = Assert.IsType<BinaryExpressionSyntax>(guard.Condition);
        Assert.Equal(SyntaxKind.EqualsExpression, condition.Kind());
        Assert.Equal(decision, condition.Right.ToString());
    }

    /// <summary>
    /// The reload decision is behaviour, and it is tested as behaviour in
    /// <c>Config.ConfigReloadGateTests</c>. What a wiring test can add is
    /// that Reload asks the gate rather than growing a second copy of the
    /// rule, that it asks with the right operands, and that a decline frees
    /// what it built and reports false: callers read true as "expect the
    /// ConfigChanged echo".
    /// </summary>
    [Fact]
    public void A_reload_defers_to_the_gate_and_a_decline_costs_nothing()
    {
        var (decide, guard) = ReloadGuard();

        AssertRefusesOn(guard, "ConfigReloadDecision.Decline");
        Assert.Equal(decide.Span, ((BinaryExpressionSyntax)guard.Condition).Left.Span);

        Assert.NotEmpty(guard.Statement.Calls("NativeMethods.ConfigFree"));
        Assert.Contains(
            guard.Statement.DescendantNodes().OfType<ReturnStatementSyntax>(),
            r => r.Expression?.ToString() == "false");
        Assert.NotEmpty(guard.Statement.Calls("_watcher?.Resettle"));
    }

    /// <summary>
    /// And with the counts the right way round: the load that just happened,
    /// then the one the session is running on. Swapped, the guard refuses
    /// every reload that finds MORE config files than before and waves
    /// through the one case it exists for, a file going away mid save.
    /// </summary>
    [Fact]
    public void The_gate_is_asked_about_this_load_against_the_session()
    {
        var (decide, _) = ReloadGuard();

        Assert.Equal("defaultFiles", decide.Arg(0));
        Assert.Equal("defaultFilesFound", decide.Arg(1));
        Assert.Equal("_defaultFilesFound", decide.Arg(2));
    }

    /// <summary>
    /// A count shrink is either a save mid swap or a file gone for good,
    /// and an ask for one more look is what tells them apart: the save's
    /// completing rename answers it, a deletion answers nothing. Locked
    /// files ask through ShouldRetry on their own; the shrink asks here,
    /// beside it, on its own budget.
    /// </summary>
    [Fact]
    public void A_shrunk_count_decline_asks_for_one_more_look_on_its_own_budget()
    {
        var (_, guard) = ReloadGuard();

        var ask = Assert.Single(guard.Statement.Calls("ConfigReloadGate.ShouldConfirmShrink"));
        Assert.Equal("defaultFiles", ask.Arg(0));
        Assert.Equal("defaultFilesFound", ask.Arg(1));
        Assert.Equal("_defaultFilesFound", ask.Arg(2));
        Assert.Equal("_shrinkConfirms", ask.Arg(3));
        Assert.Equal("MaxShrinkConfirms", ask.Arg(4));

        // An else-if on the chain ShouldRetry heads, not a condition of its
        // own and not folded into one: each covers a different decline, and
        // replacing one with the other would stop asking about locked files
        // or about deletions outright.
        var askIf = ask.Ancestors().OfType<IfStatementSyntax>().First();
        Assert.Equal(ask.Span, askIf.Condition.Span);
        var elseClause = Assert.IsType<ElseClauseSyntax>(askIf.Parent);
        var head = Assert.IsType<IfStatementSyntax>(elseClause.Parent);
        Assert.Equal(
            "ConfigReloadGate.ShouldRetry",
            Assert.IsType<InvocationExpressionSyntax>(head.Condition).Expression.ToString());

        // The ask spends the shrink budget, not the locked-file one: one
        // counter carrying both meanings is what let a gave-up unreadable
        // stretch skip these asks.
        var spend = Assert.Single(askIf.Statement.DescendantNodes()
            .OfType<PostfixUnaryExpressionSyntax>()
            .Where(p => p.OperatorToken.Kind() == SyntaxKind.PlusPlusToken)
            .Where(p => p.Operand.ToString() == "_shrinkConfirms"));
        Assert.Empty(askIf.Statement.DescendantNodes()
            .OfType<PostfixUnaryExpressionSyntax>()
            .Where(p => p.Operand.ToString() == "_declinedReloadRetries"));
    }

    /// <summary>
    /// The shrink confirmations and the locked-file retries are separate
    /// budgets, because they count different stretches: asks about a file
    /// that would not open, and asks about a file that went away. The
    /// deletion guard reads the shrink budget alone, so a spent unreadable
    /// budget cannot make the first shrink decline apply as a deletion
    /// with zero confirming asks.
    /// </summary>
    [Fact]
    public void Shrink_confirmations_spend_their_own_budget()
    {
        var (_, guard) = ReloadGuard();

        var retry = Assert.Single(guard.Statement.Calls("ConfigReloadGate.ShouldRetry"));
        Assert.Equal("_declinedReloadRetries", retry.Arg(1));
        Assert.Equal("MaxDeclinedReloadRetries", retry.Arg(2));

        var heal = Assert.Single(guard.Statement.Calls("ConfigReloadGate.IsPersistentShrink"));
        Assert.Equal("_shrinkConfirms", heal.Arg(3));
        Assert.Equal("MaxShrinkConfirms", heal.Arg(4));
    }

    /// <summary>
    /// A shrink that survives the whole ask budget is a deletion of a
    /// layered file the watcher does not watch, and believing the disk is
    /// the only way the session recovers: no event is coming for that
    /// file, so a refusal here is permanent. The config in hand was built
    /// from every file that does exist, so applying it is the user's
    /// configuration as it now stands.
    /// </summary>
    [Fact]
    public void A_shrink_that_outlives_the_asks_is_applied_as_a_deletion()
    {
        var (_, guard) = ReloadGuard();

        var heal = Assert.Single(guard.Statement.Calls("ConfigReloadGate.IsPersistentShrink"));
        Assert.Equal("defaultFiles", heal.Arg(0));
        Assert.Equal("defaultFilesFound", heal.Arg(1));
        Assert.Equal("_defaultFilesFound", heal.Arg(2));
        Assert.Equal("_shrinkConfirms", heal.Arg(3));
        Assert.Equal("MaxShrinkConfirms", heal.Arg(4));

        // The decline's cost only runs when neither proof landed: not a
        // confirmed vanish of the watched file, and not a shrink that
        // outlived its asks. Both operands are decomposed rather than
        // matched as text, because a condition that merely CONTAINS the
        // heal is also satisfied by one that inverts it.
        var inner = heal.Ancestors().OfType<IfStatementSyntax>().First();
        var both = Assert.IsType<BinaryExpressionSyntax>(inner.Condition);
        Assert.Equal(SyntaxKind.LogicalAndExpression, both.Kind());

        var vanish = Assert.IsType<PrefixUnaryExpressionSyntax>(both.Left);
        Assert.Equal(SyntaxKind.ExclamationToken, vanish.OperatorToken.Kind());
        Assert.Equal("vanishConfirmed", vanish.Operand.ToString());

        var not = Assert.IsType<PrefixUnaryExpressionSyntax>(both.Right);
        Assert.Equal(SyntaxKind.ExclamationToken, not.OperatorToken.Kind());
        Assert.Equal(heal.Span, not.Operand.Span);
        Assert.NotEmpty(inner.Statement.Calls("NativeMethods.ConfigFree"));
        Assert.Contains(
            inner.Statement.DescendantNodes().OfType<ReturnStatementSyntax>(),
            r => r.Expression?.ToString() == "false");

        // The heal writes the count nowhere: the applied path below
        // records the lower count, so the fall-through stays inside the
        // one-writer rule. All it adds is the account of what happened.
        Assert.NotEmpty(guard.Statement.Calls(
            "StaticLoggers.ConfigService.LogReloadDefaultFilesShrunk"));
        Assert.Empty(guard.Statement.Calls("RecordDefaultFiles"));
    }

    /// <summary>
    /// The count the gate reads has exactly one writer, so the session and
    /// the gate cannot start disagreeing about what the session is running
    /// on. Deriving it a second way is how that begins.
    /// </summary>
    [Fact]
    public void The_session_default_file_count_has_one_writer()
    {
        var assignment = Assert.Single(ConfigService().Root
            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_defaultFilesFound"));

        Assert.Equal(
            "RecordDefaultFiles",
            assignment.Ancestors().OfType<MethodDeclarationSyntax>().First()
                .Identifier.ValueText);
    }

    /// <summary>
    /// And an applied reload records what that load found, not a fresh
    /// reading of the disk: the two would be taken at different instants,
    /// which is the whole hazard this is about.
    /// </summary>
    [Fact]
    public void An_applied_reload_records_the_count_it_applied()
    {
        var record = Assert.Single(ConfigService().Method("Reload").Calls("RecordDefaultFiles"));

        Assert.Equal("defaultFilesFound", record.Arg(0));
    }

    /// <summary>
    /// The budget is spent on asks the watcher took, not on declines. It
    /// drops an ask while this service suppresses its own writes, and there
    /// is no watcher at all under --no-config, so counting a dropped one
    /// stops the retrying with nothing having been tried.
    /// </summary>
    [Fact]
    public void The_retry_budget_only_counts_an_ask_the_watcher_took()
    {
        var reload = ConfigService().Method("Reload");

        var retry = Assert.Single(reload.DescendantNodes()
            .OfType<PostfixUnaryExpressionSyntax>()
            .Where(p => p.Operand.ToString() == "_declinedReloadRetries")
            .Where(p => p.Ancestors().OfType<IfStatementSyntax>().First()
                .Condition.Calls("_watcher?.Resettle").Count > 0));

        var condition = Assert.IsType<BinaryExpressionSyntax>(
            retry.Ancestors().OfType<IfStatementSyntax>().First().Condition);
        Assert.Equal(SyntaxKind.EqualsExpression, condition.Kind());
        Assert.Equal("true", condition.Right.ToString());
    }

    /// <summary>
    /// Both budgets end when a reload applies, not when a decline stretch
    /// does. Without the reset, one exhausted budget suppresses the
    /// resettles and the one-per-stretch gave-up warning for every later
    /// stretch of the session.
    /// </summary>
    [Fact]
    public void An_applied_reload_resets_both_budgets()
    {
        var resets = ConfigService().Method("Reload")
            .DescendantNodes().OfType<AssignmentExpressionSyntax>();

        var retry = Assert.Single(resets
            .Where(a => a.Left.ToString() == "_declinedReloadRetries"));
        Assert.Equal("0", retry.Right.ToString());

        var confirms = Assert.Single(resets
            .Where(a => a.Left.ToString() == "_shrinkConfirms"));
        Assert.Equal("0", confirms.Right.ToString());
    }

    /// <summary>
    /// Lifting the write suppression frees both ask budgets, not only an
    /// applied reload. Our own write ends whatever stretch the budgets were
    /// counting, but its events were swallowed, so no delivery reloads on
    /// it and the reload a caller makes afterwards can still decline. A
    /// budget left spent makes the next stretch of unreadability arrive at
    /// the cap and log gave-up with no ask having been tried in it.
    /// </summary>
    /// <remarks>
    /// Only the LIFT frees them. Resetting on the bracket's opening half
    /// would spend the budgets' meaning while a decline inside the bracket
    /// is still trying to spend the budgets themselves.
    /// </remarks>
    [Fact]
    public void Lifting_the_write_suppression_frees_both_ask_budgets()
    {
        var guard = Assert.Single(ConfigService().Method("SuppressWatcher")
            .DescendantNodes().OfType<IfStatementSyntax>());

        Assert.Equal("!suppress", guard.Condition.ToString());

        var resets = guard.Statement.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() is "_declinedReloadRetries" or "_shrinkConfirms")
            .ToArray();

        Assert.Equal(2, resets.Length);
        Assert.All(resets, a => Assert.Equal("0", a.Right.ToString()));

        // The vanish question is not theirs to close: its stretches open and
        // close on load verdicts, and a write is not one.
        Assert.Empty(guard.Statement.Calls("_vanishProtocol.Observed"));
    }

    /// <summary>
    /// The session count is pinned at zero under --no-config, and this
    /// half is the pin. Deleting it lets a save gap refuse the High
    /// Contrast and OS-scheme reloads such a launch lives on, with no
    /// watcher to ever ask again.
    /// </summary>
    [Fact]
    public void The_session_count_is_pinned_at_zero_under_no_config()
    {
        var assignment = Assert.Single(ConfigService().Method("RecordDefaultFiles")
            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_defaultFilesFound"));

        var conditional = Assert.IsType<ConditionalExpressionSyntax>(assignment.Right);
        Assert.Equal("_noConfig", conditional.Condition.ToString());
        Assert.Equal("0", conditional.WhenTrue.ToString());
        Assert.Equal("found", conditional.WhenFalse.ToString());
    }

    /// <summary>
    /// The gave-up warning is the only account of a stretch of
    /// unreadability that outlived its asks, so the call is pinned, not
    /// just the branch it sits in.
    /// </summary>
    [Fact]
    public void A_stretch_that_outlives_its_asks_is_reported()
    {
        Assert.NotEmpty(ConfigService().Method("Reload")
            .Calls("StaticLoggers.ConfigService.LogReloadGaveUp"));
    }

    /// <summary>
    /// The preview gate is asked with the counts the same way round as
    /// the reload's: the load that just happened, then the session.
    /// Swapped, the first preview after a config file appeared is
    /// refused, which reads as a palette browse that does nothing.
    /// </summary>
    [Fact]
    public void The_preview_gate_is_asked_about_this_load_against_the_session()
    {
        var decide = Assert.Single(
            ConfigService().Method("PreviewTheme").Calls("ConfigReloadGate.Decide"));

        Assert.Equal("previewFiles", decide.Arg(0));
        Assert.Equal("previewFilesFound", decide.Arg(1));
        Assert.Equal("_defaultFilesFound", decide.Arg(2));
    }

    /// <summary>
    /// The one <c>ConfigVanishProtocol</c> construction in the service: the
    /// wiring whose behaviour is driven, against a real watcher, in
    /// <c>Config.ConfigVanishProtocolTests</c>.
    /// </summary>
    private static ObjectCreationExpressionSyntax ProtocolCreation() =>
        Assert.Single(Creations(ConfigService().Root)
            .Where(o => o.Type.ToString() == "ConfigVanishProtocol"));

    /// <summary>One named argument of that construction.</summary>
    private static ArgumentSyntax ProtocolArgument(string name) =>
        ProtocolCreation().ArgumentList!.Arguments
            .Single(a => a.NameColon?.Name.Identifier.ValueText == name);

    /// <summary>
    /// A config file that stays gone is reported by the watcher, and the
    /// session stops claiming to be running on one.
    /// </summary>
    /// <remarks>
    /// Without this the refusal is permanent: the count only ever rose, so a
    /// session whose config file is deleted declines every later reload for
    /// the life of the process, and High Contrast reaches the terminal only
    /// through the config a reload builds.
    /// </remarks>
    [Fact]
    public void A_vanished_config_file_is_reported_and_clears_the_session_count()
    {
        var source = ConfigService();

        var onVanished = Creations(source.Method("StartWatcher"))
            .Single(o => o.Type.ToString() == "ConfigFileWatcher")
            .ArgumentList!.Arguments
            .Single(a => a.NameColon?.Name.Identifier.ValueText == "onVanished");
        Assert.Equal("OnConfigFileVanished", onVanished.Expression.ToString());

        // The report is not the evidence, so the handler goes and gets some:
        // it reloads, and the load's verdict is what the protocol runs on.
        // Concluding here instead is what left a session with no watcher
        // unable to conclude at all (wintty#1155).
        var vanished = source.Method("OnConfigFileVanished");
        Assert.Single(vanished.Calls("Reload"));

        // And the handler keeps nothing of its own: no count, no push. The
        // count falls on the applied path, through its one writer.
        Assert.Empty(vanished.Calls("RecordDefaultFiles"));
        Assert.Empty(vanished.Calls("NativeMethods.AppUpdateConfig"));

        // The accept writes no count either. Lowering it there would write
        // it from a second place AND write it before the gate reads it, so
        // the decision would turn on call order that nothing pins.
        Assert.Empty(ProtocolArgument("onAccept").Calls("RecordDefaultFiles"));
    }

    /// <summary>
    /// And it asks before it believes. One vanished report is what an
    /// ordinary atomic save produces, so the count moves only on the far
    /// side of a confirmation, never on the report itself.
    /// </summary>
    /// <remarks>
    /// <para>Read what this can and cannot say. Nothing executes this file:
    /// Ghostty.Tests holds no reference to the shell project, so every
    /// assertion here is over source read with Roslyn, and source ORDER is
    /// not control flow. The decision, the asks and the accept live behind
    /// <c>ConfigVanishProtocol</c> exactly because a test of this kind
    /// could not see them set wrong: a budget of zero restores the defect
    /// and passes everything here. <c>ConfigVanishProtocolTests</c> drives
    /// the wiring against a real watcher. This only says the handler
    /// delegates to it and that nothing beside the delegation acts.</para>
    ///
    /// <para>The proof that the report fires mid save is
    /// <c>ConfigFileWatcherTests.The_file_check_runs_in_the_delivery_not_on_the_timer</c>
    /// (issue #1146).</para>
    /// </remarks>
    [Fact]
    public void A_vanished_config_file_is_confirmed_before_the_count_moves()
    {
        var source = ConfigService();

        // The decision is asked for rather than reimplemented here, and it
        // is asked with a LOAD's verdict rather than with the watcher's
        // existence check, which is a proxy a dangling symlink defeats.
        var reload = source.Method("Reload");
        var observed = Assert.Single(reload.Calls("_vanishProtocol.Observed"));
        Assert.Equal("defaultFiles", observed.Arg(0));

        // Nowhere else. A second caller feeding it something other than a
        // verdict is the shape this is against.
        Assert.Empty(source.Method("OnConfigFileVanished")
            .Calls("_vanishProtocol.Observed"));

        // The accept's only effect is the account of it. The count moves on
        // the applied path, which a proven vanish reaches by being carried
        // past the gate, not by the accept reaching over and writing it.
        var onAccept = ProtocolArgument("onAccept");
        Assert.NotEmpty(onAccept.Calls(
            "StaticLoggers.ConfigService.LogConfigFileVanished"));
        Assert.Empty(onAccept.Calls("RecordDefaultFiles"));
    }

    /// <summary>
    /// The ask goes through the watcher's own Resettle, which is what keeps
    /// a real deletion moving: a deleted file raises no further filesystem
    /// events, so without this nothing schedules the next look.
    /// </summary>
    /// <remarks>
    /// This is a LIVENESS pin and not a correctness one, and the difference
    /// matters enough to say. A constant ask still concludes, because the
    /// answer is deliberately not consulted: gating on it is what made a
    /// deletion unprovable with no watcher, which is the default
    /// (wintty#1155). What a constant loses is the sooner look, so the
    /// question waits for whatever reload happens to come next. No
    /// behavioural test can see that difference, which is the reason to
    /// assert the shape here.
    /// </remarks>
    [Fact]
    public void The_vanish_ask_goes_through_the_watchers_own_resettle()
    {
        Assert.NotEmpty(ProtocolArgument("ask")
            .Expression.Calls("_watcher?.Resettle"));
    }

    /// <summary>
    /// When the watcher takes no ask, which with auto-reload-config off is
    /// always, the protocol's look-again is what reloads at the floor. It is
    /// the service's own one-shot timer, posted to the UI thread, and it
    /// reloads.
    /// </summary>
    /// <remarks>
    /// Liveness again, like the ask above: without it a deletion still
    /// concludes, on the next reload somebody else causes, and the reload
    /// that opened the question is declined and lost. That is the second
    /// High Contrast toggle a deleted config file used to cost
    /// (wintty#1155). The protocol's half is driven in
    /// <c>Config.ConfigVanishProtocolTests</c>; this pins the service's.
    /// </remarks>
    [Fact]
    public void The_vanish_look_again_is_a_reload_on_the_UI_thread_at_the_asked_delay()
    {
        var source = ConfigService();

        Assert.Equal("ScheduleVanishRecheck",
            ProtocolArgument("lookAgainAfter").Expression.ToString());

        var schedule = Assert.Single(
            source.Method("ScheduleVanishRecheck").Calls("_vanishRecheck.Schedule"));
        Assert.Equal("delay", schedule.Arg(0));

        var callback = Assert.Single(source.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_vanishRecheck.Callback"));
        var post = Assert.Single(callback.Right.Calls("_dispatcher.TryEnqueue"));
        Assert.Equal("OnVanishRecheck", post.Arg(0));

        Assert.Single(source.Method("OnVanishRecheck").Calls("Reload"));
    }

    /// <summary>
    /// And teardown stops it, beside the watcher: a look that fired after
    /// AppFree would push a config into a freed app, which is issue #208.
    /// Reload's own fence would catch it, but the fence is the last line,
    /// not the plan.
    /// </summary>
    [Fact]
    public void BeginShutdown_cancels_the_vanish_look_again()
    {
        Assert.NotEmpty(ConfigService().Method("BeginShutdown")
            .Calls("_vanishRecheck.Cancel"));
    }

    /// <summary>
    /// A High Contrast request whose reload declined is not put back. It
    /// stays wanted, so the reload the vanish question schedules carries it,
    /// and the running config's palette moves only on an applied reload.
    /// </summary>
    /// <remarks>
    /// Putting it back was the other half of the second toggle: the look
    /// the question scheduled rebuilt without the palette the user had just
    /// asked for. The rule itself is <c>HighContrastOverrideLatch</c>'s, and
    /// <c>Accessibility.HighContrastOverrideLatchTests</c> drives it.
    /// </remarks>
    [Fact]
    public void A_declined_high_contrast_request_stays_wanted()
    {
        var source = ConfigService();

        var set = source.Method("SetHighContrastOverride");
        Assert.Single(set.Calls("_highContrast.Request"));
        Assert.Single(set.Calls("Reload"));
        Assert.Empty(set.DescendantNodes().OfType<AssignmentExpressionSyntax>());

        // Applied is marked with what THIS reload built, on the applied
        // path, after the config was pushed.
        var reload = source.Method("Reload");
        var built = Assert.Single(reload.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.ValueText == "highContrastBuilt"));
        Assert.Equal("_highContrast.Wanted", built.Initializer!.Value.ToString());

        var mark = Assert.Single(reload.Calls("_highContrast.MarkApplied"));
        Assert.Equal("highContrastBuilt", mark.Arg(0));
        var push = Assert.Single(reload.Calls("NativeMethods.AppUpdateConfig"));
        Assert.Same(
            push.Ancestors().OfType<BlockSyntax>().First(),
            mark.Ancestors().OfType<BlockSyntax>().First());
        Assert.True(push.Span.End < mark.Span.Start,
            "the palette is marked applied before the config carrying it was pushed");
        Assert.True(built.Span.End < Assert.Single(reload.Calls("BuildLiveConfig")).Span.Start,
            "the palette is read after the config was built");
    }

    /// <summary>
    /// The question is asked about THIS session's count, read at report time
    /// through the field its one writer owns.
    /// </summary>
    /// <remarks>
    /// A constant here has a session that never had a config file confirming
    /// the deletion of one. Reading it eagerly is the other half: the service
    /// is constructed before the first config is ever loaded, so a value
    /// captured then is zero for the life of the process and no deletion is
    /// ever confirmed.
    /// </remarks>
    [Fact]
    public void The_question_is_asked_about_the_sessions_own_count()
    {
        var lambda = Assert.IsType<ParenthesizedLambdaExpressionSyntax>(
            ProtocolArgument("sessionDefaultFilesFound").Expression);

        Assert.Equal("_defaultFilesFound", lambda.Body!.ToString());
    }

    /// <summary>
    /// Every load's verdict reaches the question, and it reaches it before
    /// the gate decides anything.
    /// </summary>
    /// <remarks>
    /// Unconditional is the point. An earlier shape asked only on an Absent
    /// branch, which tells the protocol nothing the argument does not
    /// already carry and, worse, never delivers the verdict that ENDS a
    /// stretch: a file coming back was then invisible to the question, and
    /// a stretch opened by one save could be concluded by another an hour
    /// later. The one exception is the test below this: a verdict that
    /// disagrees with its own count is not a verdict about presence at all.
    /// Shape only, like everything else over this file; the behaviour is
    /// driven in <c>Config.ConfigVanishProtocolTests</c>.
    /// </remarks>
    [Fact]
    public void Every_load_verdict_reaches_the_question_before_the_gate()
    {
        var reload = ConfigService().Method("Reload");

        var observed = Assert.Single(reload.Calls("_vanishProtocol.Observed"));
        Assert.Equal("defaultFiles", observed.Arg(0));
        Assert.Empty(observed.Ancestors().OfType<IfStatementSyntax>());

        // Before the gate, whose decision a proven vanish has to be able to
        // overrule. These are statements of one block, which is the one
        // place source order in this file IS execution order.
        var decide = Assert.Single(reload.Calls("ConfigReloadGate.Decide"));
        Assert.Same(
            observed.Ancestors().OfType<BlockSyntax>().First(),
            decide.Ancestors().OfType<BlockSyntax>().First());
        Assert.True(
            observed.Span.End < decide.Span.Start,
            "the gate decides before the verdict has reached the question");
    }

    /// <summary>
    /// A verdict its own count contradicts never reaches the vanish
    /// question: the gate refuses the reload on it, and a confirmation built
    /// on it would apply a defaults config straight past that refusal. The
    /// stretch is left as it was rather than answered, which is the safe
    /// direction for evidence that cannot be read.
    /// </summary>
    [Fact]
    public void A_verdict_that_disagrees_with_its_count_is_not_vanish_evidence()
    {
        var observed = Assert.Single(ConfigService().Method("Reload")
            .Calls("_vanishProtocol.Observed"));

        // The gate's own agreement check, short-circuited in front of the
        // call, with this load's verdict and count. Decomposed rather than
        // matched as text, because a substring assertion here would accept
        // the check sitting beside the call instead of guarding it.
        var and = Assert.IsType<BinaryExpressionSyntax>(observed.Parent);
        Assert.Equal(SyntaxKind.LogicalAndExpression, and.Kind());
        Assert.Equal(observed.Span, and.Right.Span);

        var agree = Assert.IsType<InvocationExpressionSyntax>(and.Left);
        Assert.Equal("ConfigReloadGate.VerdictAndCountAgree", agree.Expression.ToString());
        Assert.Equal("defaultFiles", agree.ArgumentList.Arguments[0].ToString());
        Assert.Equal("defaultFilesFound", agree.ArgumentList.Arguments[1].ToString());
    }

    /// <summary>
    /// The settle answers nothing itself: it reloads, and the load's verdict
    /// is the answer.
    /// </summary>
    /// <remarks>
    /// A settle knows only that the path existed when it looked. A dangling
    /// symlink satisfies that and fails to open, so ending the question here
    /// cleared the stretch that the same settle's reload then reopened, and
    /// it oscillated without ever concluding. The verdict distinguishes
    /// present from readable and is the same evidence wherever the reload
    /// came from, which is what lets a host with no watcher conclude at all
    /// (wintty#1155).
    /// </remarks>
    [Fact]
    public void The_settle_reloads_rather_than_answering_the_question_itself()
    {
        var settled = ConfigService().Method("OnConfigFileSettled");

        Assert.Single(settled.Calls("Reload"));
        Assert.Empty(settled.Calls("_vanishProtocol.Observed"));
    }

    /// <summary>
    /// A palette preview takes the same gate, with the same polarity, and a
    /// refusal takes back the overlay file it had already written. Nothing
    /// later removes it: RevertThemePreview only runs for a preview that was
    /// shown.
    /// </summary>
    [Fact]
    public void A_refused_preview_cleans_up_after_itself()
    {
        var preview = ConfigService().Method("PreviewTheme");

        var decide = Assert.Single(preview.Calls("ConfigReloadGate.Decide"));
        var guard = decide.Ancestors().OfType<IfStatementSyntax>().First();

        AssertRefusesOn(guard, "ConfigReloadDecision.Decline");
        Assert.NotEmpty(guard.Statement.Calls("NativeMethods.ConfigFree"));
        Assert.NotEmpty(guard.Statement.Calls("DeleteThemePreviewOverlay"));

        // Its own line, not the reload's. Sharing one made a palette browse
        // report "Keeping the running config" for something that never asked
        // to change it, which reads in a log as a reload that happened.
        Assert.NotEmpty(guard.Statement.Calls(
            "StaticLoggers.ConfigService.LogThemePreviewKeptRunningConfig"));
        Assert.Empty(guard.Statement.Calls(
            "StaticLoggers.ConfigService.LogReloadKeptRunningConfig"));
    }
}
