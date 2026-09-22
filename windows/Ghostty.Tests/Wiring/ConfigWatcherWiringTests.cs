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
    /// beside it, on the same budget.
    /// </summary>
    [Fact]
    public void A_shrunk_count_decline_asks_for_one_more_look_on_the_same_budget()
    {
        var (_, guard) = ReloadGuard();

        var ask = Assert.Single(guard.Statement.Calls("ConfigReloadGate.ShouldConfirmShrink"));
        Assert.Equal("defaultFiles", ask.Arg(0));
        Assert.Equal("defaultFilesFound", ask.Arg(1));
        Assert.Equal("_defaultFilesFound", ask.Arg(2));
        Assert.Equal("_declinedReloadRetries", ask.Arg(3));
        Assert.Equal("MaxDeclinedReloadRetries", ask.Arg(4));

        // Beside ShouldRetry, not instead of it: each covers a different
        // decline, and replacing one with the other would stop asking
        // about locked files or about deletions outright.
        var askIf = ask.Ancestors().OfType<IfStatementSyntax>().First();
        var or = Assert.IsType<BinaryExpressionSyntax>(askIf.Condition);
        Assert.Equal(SyntaxKind.LogicalOrExpression, or.Kind());
        Assert.Equal(
            "ConfigReloadGate.ShouldRetry",
            Assert.IsType<InvocationExpressionSyntax>(or.Left).Expression.ToString());
        Assert.Equal(ask.Span, or.Right.Span);
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
        Assert.Equal("_declinedReloadRetries", heal.Arg(3));
        Assert.Equal("MaxDeclinedReloadRetries", heal.Arg(4));

        // The decline's cost only runs when the shrink is NOT confirmed
        // as a deletion; the heal is the fall-through past that branch.
        var inner = heal.Ancestors().OfType<IfStatementSyntax>().First();
        var not = Assert.IsType<PrefixUnaryExpressionSyntax>(inner.Condition);
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
    /// The budget ends when a reload applies, not when a decline stretch
    /// does. Without the reset, one exhausted budget suppresses the
    /// resettles and the one-per-stretch gave-up warning for every later
    /// stretch of the session.
    /// </summary>
    [Fact]
    public void An_applied_reload_resets_the_retry_budget()
    {
        var reset = Assert.Single(ConfigService().Method("Reload")
            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_declinedReloadRetries"));

        Assert.Equal("0", reset.Right.ToString());
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

        var vanished = source.Method("OnConfigFileVanished");
        Assert.Equal("0", Assert.Single(vanished.Calls("RecordDefaultFiles")).Arg(0));

        // Nothing is rebuilt and nothing is pushed: a deletion keeps the
        // running config, it does not replace it with pure defaults.
        Assert.Empty(vanished.Calls("Reload"));
        Assert.Empty(vanished.Calls("NativeMethods.AppUpdateConfig"));
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
