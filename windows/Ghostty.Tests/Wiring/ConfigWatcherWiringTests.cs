using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The config service watches its file through
/// <c>Ghostty.Core.Config.ConfigFileWatcher</c>, whose behaviour against real
/// atomic saves is pinned in <c>Config.ConfigFileWatcherTests</c>. These pin
/// the wiring: that the shell does not grow a second, raw watcher beside it
/// (the raw one is what reloaded into the gap of a swap), and that teardown
/// still stops it before the app is freed.
/// </summary>
public class ConfigWatcherWiringTests
{
    private static ShellSource ConfigService() => ShellSource.Load("Services.ConfigService.cs");

    private static string[] CreatedTypes(Microsoft.CodeAnalysis.SyntaxNode node) =>
        node.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Select(o => o.Type.ToString())
            .ToArray();

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
    public void BeginShutdown_stops_the_watcher()
    {
        var shutdown = ConfigService().Method("BeginShutdown");

        Assert.NotEmpty(shutdown.Calls("StopWatcher"));
    }
}
