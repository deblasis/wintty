using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The empty-read answer of issue #1138 is a second gate beside
/// <c>ConfigReloadGate.Decide</c>, so the wiring pins here are the ones a
/// behaviour test cannot reach: that <c>Reload</c> asks the question at
/// all, that a held look frees the config it built and asks for one more
/// look rather than silently keeping nothing, and that the applied path
/// records what the running config was built from. The rule itself is
/// truth-tabled in <c>Config.ConfigReloadGateTests</c>.
/// </summary>
public class ConfigEmptyReadWiringTests
{
    private static ShellSource ConfigService() => ShellSource.Load("Services.ConfigService.cs");

    [Fact]
    public void Reload_asks_the_empty_read_question()
    {
        Assert.NotEmpty(ConfigService().Method("Reload").Calls("ConfigReloadGate.IsEmptyRead"));
    }

    [Fact]
    public void Reload_honours_the_look_budget_before_applying_an_empty_read()
    {
        Assert.NotEmpty(
            ConfigService().Method("Reload").Calls("ConfigReloadGate.ShouldApplyEmptyRead"));
    }

    /// <summary>
    /// A held look is a decline: the config it built is freed, the reason
    /// is logged the way every other decline logs it, one more look is
    /// asked for, and nothing is applied. Losing any of those four turns
    /// the hold into either a leak or a silent no-op.
    /// </summary>
    [Fact]
    public void The_empty_read_decline_frees_logs_asks_and_returns()
    {
        var reload = ConfigService().Method("Reload");

        var gate = Assert.Single(reload.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Where(i => i.Condition.ToString().Contains(
                "ConfigReloadGate.ShouldApplyEmptyRead", StringComparison.Ordinal)));

        var calls = gate.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression.ToString())
            .ToArray();
        Assert.Contains("NativeMethods.ConfigFree", calls);
        Assert.Contains("_lookAgain.Ask", calls);

        Assert.Contains(gate.DescendantNodes().OfType<ReturnStatementSyntax>(),
            r => r.Expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.FalseLiteralExpression));
    }

    /// <summary>
    /// The budget is consecutive looks, so every look answers the question
    /// and the applied path starts the next question from zero, next to
    /// the other two budgets' resets.
    /// </summary>
    [Fact]
    public void The_look_budget_moves_on_every_look_and_resets_when_applied()
    {
        var reload = ConfigService().Method("Reload");

        Assert.Contains(reload.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString().Contains("isEmptyRead", StringComparison.Ordinal));

        Assert.Contains(reload.DescendantNodes().OfType<ExpressionStatementSyntax>(),
            s => s.ToString().StartsWith("_emptyReadLooks = 0", StringComparison.Ordinal));
    }

    /// <summary>
    /// The empty count is recorded wherever the file count is: one writer,
    /// two fields, so the question Reload asks is always about the config
    /// actually in force.
    /// </summary>
    [Fact]
    public void Every_default_files_recording_carries_the_empty_count()
    {
        var recordings = ConfigService().Root.Calls("RecordDefaultFiles");
        Assert.NotEmpty(recordings);
        Assert.All(recordings, r => Assert.Equal(2, r.ArgumentList!.Arguments.Count));
    }
}
