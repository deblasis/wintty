using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The vanish shape issue #1150 was about is already pinned in
/// <c>ConfigWatcherWiringTests</c>: the vanished handler reloads and records
/// nothing of its own, and the protocol's accept records nothing either.
/// What those pins used to read is the call text inside the accept lambda,
/// so a walk past them lives here: since wintty#1158 the accept is a named
/// handler (the census reads a body, not a lambda), it causes the log and
/// the notice and nothing else, and the count still has exactly two
/// recorders, neither of them handed an assumed constant.
/// </summary>
public class VanishCountWiringTests
{
    private static ShellSource ConfigService() => ShellSource.Load("Services.ConfigService.cs");

    private static MethodDeclarationSyntax AcceptHandler()
    {
        var creation = Assert.Single(ConfigService().Root
            .DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>(),
            o => o.Type.ToString() == "ConfigVanishProtocol");

        var onAccept = Assert.Single(creation.ArgumentList!.Arguments,
            a => a.NameColon?.Name.ToString() == "onAccept");

        var name = Assert.IsType<IdentifierNameSyntax>(onAccept.Expression)
            .Identifier.ValueText;
        return ConfigService().Method(name);
    }

    /// <summary>
    /// The accept causes exactly the account of it, the notice, and one
    /// self-heal look. The shipped accept had a fourth effect - it was the
    /// key that carried an absence past the gate and applied a defaults
    /// config - and a second effect growing back beside these three is
    /// that defect returning through a side door.
    /// </summary>
    [Fact]
    public void The_vanish_accept_logs_raises_the_notice_and_schedules_one_look()
    {
        var handler = AcceptHandler();

        var calls = handler.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(c => c.Expression.ToString())
            .ToList();
        Assert.Equal(3, calls.Count);
        Assert.Contains("StaticLoggers.ConfigService.LogConfigFileVanished", calls);
        Assert.Contains("_missingNoticeSource.OnDeletionConfirmed", calls);
        Assert.Contains("_lookAgain.Schedule", calls);

        // No count write, no config push, no gate reach: the notice source
        // is the only thing an accept moves.
        Assert.DoesNotContain(calls, c => c.Contains("RecordDefaultFiles", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.Contains("AppUpdateConfig", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.Contains("Reload", StringComparison.Ordinal));
    }

    [Fact]
    public void The_count_has_exactly_two_recorders_neither_an_assumed_constant()
    {
        // The constructor's seed and the applied reload: adding a site is
        // how a second writer arrives without touching the writer the
        // one-writer census watches. Matched by the name the callee ends
        // with, so a this.-spelled recorder is still counted.
        var recordings = ConfigService().Root
            .DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith(
                "RecordDefaultFiles", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, recordings.Count());

        // A recorded constant is an assumption, and the assumption is the
        // defect: the count exists so the gate never guesses. Both sites
        // pass the load's own reading, the seed through the
        // created-if-absent arithmetic.
        foreach (var record in recordings)
        {
            Assert.False(int.TryParse(record.Arg(0), out _),
                "RecordDefaultFiles called with an assumed constant: " + record.Arg(0));
        }
    }
}
