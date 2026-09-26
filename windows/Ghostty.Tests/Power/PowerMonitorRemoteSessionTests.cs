using System;
using System.Linq;
using Ghostty.Core.Power;
using Ghostty.Tests.Wiring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Power;

/// <summary>
/// The RDP bit is already tracked on the production monitor (polled from
/// GetSystemMetrics(SM_REMOTESESSION)) and folded into the composite
/// low-power answer, but the interface never exposed it on its own, so a
/// caller that needs to tell transport state apart from low power had no
/// way to ask. These guard the exposure: one through the fake seam, the way
/// every IPowerStateMonitor consumer sees the interface; one on the real
/// monitor's source, which cannot be loaded into this plain net10.0 test
/// host (WinRT + CsWin32 surface), so what a guard there can reach is its
/// parse.
/// </summary>
public sealed class PowerMonitorRemoteSessionTests
{
    /// <summary>
    /// The interface member exists and reads through it, not only on the
    /// concrete fake. Asserted through an <c>IPowerStateMonitor</c>-typed
    /// reference: a member that landed on the fake alone would not compile
    /// here, which is the failure this test exists to catch.
    /// </summary>
    [Fact]
    public void The_seam_exposes_the_remote_session_bit()
    {
        IPowerStateMonitor monitor = new FakePowerStateMonitor();

        // A monitor that has observed nothing reports a local session.
        Assert.False(monitor.IsRemoteSession);

        ((FakePowerStateMonitor)monitor).IsRemoteSession = true;
        Assert.True(monitor.IsRemoteSession);
    }

    /// <summary>
    /// The real monitor's answer is the polled field and nothing else. The
    /// field is the one both writers feed from
    /// GetSystemMetrics(SM_REMOTESESSION): the Start() seed and the
    /// OnSessionChanged() re-poll. Asserted on the nodes rather than the
    /// words -- a second derivation here could disagree with the composite
    /// resolution that reads the same field, and an inverted comparison
    /// compiles and looks like a tidy-up.
    /// </summary>
    [Fact]
    public void The_real_monitor_answers_from_the_polled_session_field()
    {
        var source = ShellSource.Load("Power.WindowsPowerStateMonitor.cs");

        var property = Assert.Single(
            source.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.ValueText == "IsRemoteSession");
        Assert.Equal(
            "_remoteSession",
            property.ExpressionBody?.Expression?.ToString());

        var writes = source.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "_remoteSession")
            .ToList();
        Assert.NotEmpty(writes);
        foreach (var write in writes)
        {
            var comparison = Assert.IsType<BinaryExpressionSyntax>(write.Right);
            Assert.True(
                comparison.IsKind(SyntaxKind.NotEqualsExpression),
                $"the poll reads `{comparison}`; an equality there reports every "
                    + "local session as remote.");

            var metric = Assert.IsType<InvocationExpressionSyntax>(comparison.Left);
            Assert.True(
                metric.Expression.ToString().EndsWith(
                    "GetSystemMetrics", StringComparison.Ordinal),
                $"the poll calls `{metric.Expression}`, not GetSystemMetrics.");
            Assert.True(
                metric.ArgumentList.Arguments.Count == 1
                    && metric.ArgumentList.Arguments[0].ToString().Contains(
                        "SM_REMOTESESSION", StringComparison.Ordinal),
                "the metric call must ask for SM_REMOTESESSION");
        }
    }
}
