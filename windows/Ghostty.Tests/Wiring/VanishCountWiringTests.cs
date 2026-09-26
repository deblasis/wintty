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
/// What those pins read is the call text inside the accept lambda, so two
/// walks past them live here: an accept that records through a helper of
/// its own leaves no <c>RecordDefaultFiles</c> text inside the lambda to
/// match, and a count write from anywhere new is a second writer the
/// one-writer census never sees. Between the base pins and these two, the
/// accept calls the logger and nothing else, and the count has exactly two
/// recorders, neither of them handed an assumed constant.
/// </summary>
public class VanishCountWiringTests
{
    private static ShellSource ConfigService() => ShellSource.Load("Services.ConfigService.cs");

    private static InvocationExpressionSyntax[] AcceptCalls()
    {
        var creation = Assert.Single(ConfigService().Root
            .DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>(),
            o => o.Type.ToString() == "ConfigVanishProtocol");

        var onAccept = Assert.Single(creation.ArgumentList!.Arguments,
            a => a.NameColon?.Name.ToString() == "onAccept");

        return onAccept.Expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .ToArray();
    }

    [Fact]
    public void The_vanish_accept_calls_only_the_logger()
    {
        var call = Assert.Single(AcceptCalls());

        Assert.Equal(
            "StaticLoggers.ConfigService.LogConfigFileVanished",
            call.Expression.ToString());
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
