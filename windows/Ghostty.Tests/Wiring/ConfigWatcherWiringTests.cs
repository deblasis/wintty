using System.Linq;
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
    /// The reload decision is behaviour, and it is tested as behaviour in
    /// <c>Config.ConfigReloadGateTests</c>. What a wiring test can add is
    /// that Reload asks the gate rather than growing a second copy of the
    /// rule, and that a decline frees what it built and reports false:
    /// callers read true as "expect the ConfigChanged echo".
    /// </summary>
    [Fact]
    public void A_reload_defers_to_the_gate_and_a_decline_costs_nothing()
    {
        var reload = ConfigService().Method("Reload");

        var decide = Assert.Single(reload.Calls("ConfigReloadGate.Decide"));
        var guard = decide.Ancestors().OfType<IfStatementSyntax>().First();

        Assert.NotEmpty(guard.Statement.Calls("NativeMethods.ConfigFree"));
        Assert.Contains(
            guard.Statement.DescendantNodes().OfType<ReturnStatementSyntax>(),
            r => r.Expression?.ToString() == "false");
        Assert.NotEmpty(guard.Statement.Calls("_watcher?.Resettle"));
    }

    /// <summary>
    /// And the flag the gate reads moves only with an applied config, from
    /// the gate's own answer. Deriving it a second way here is how the two
    /// start disagreeing about what counts as a config file.
    /// </summary>
    [Fact]
    public void The_session_config_file_flag_comes_from_the_gate()
    {
        var assignments = ConfigService().Method("Reload")
            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_configFilePresent")
            .ToList();

        var assignment = Assert.Single(assignments);
        Assert.Equal(
            "ConfigReloadGate.HasConfigFileAfterApply",
            Assert.IsType<InvocationExpressionSyntax>(assignment.Right).CalleeText());
    }
}
