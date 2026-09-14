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
}
