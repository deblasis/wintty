using Ghostty.Core.Shell;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// Unit tests for <see cref="LaunchIconPolicy"/>, which decides when the
/// cold-start launch icon leaves the screen. The interesting behaviour is
/// all in the edges: a surface that renders faster than the minimum
/// on-screen time, a surface that never renders at all, and the two
/// orderings of ready-vs-watchdog.
/// </summary>
public sealed class LaunchIconPolicyTests
{
    [Fact]
    public void Ready_after_the_minimum_fades_immediately()
    {
        var policy = new LaunchIconPolicy();

        var decision = policy.Ready(elapsedMs: LaunchIconPolicy.MinVisibleMs + 1);

        Assert.Equal(LaunchIconOutcome.FadeNow, decision.Outcome);
        Assert.Equal(0, decision.DelayMs);
    }

    [Fact]
    public void Ready_exactly_at_the_minimum_fades_immediately()
    {
        var policy = new LaunchIconPolicy();

        var decision = policy.Ready(elapsedMs: LaunchIconPolicy.MinVisibleMs);

        Assert.Equal(LaunchIconOutcome.FadeNow, decision.Outcome);
    }

    [Fact]
    public void Ready_before_the_minimum_defers_by_the_remainder()
    {
        var policy = new LaunchIconPolicy();

        var decision = policy.Ready(elapsedMs: 120);

        Assert.Equal(LaunchIconOutcome.FadeAfter, decision.Outcome);
        Assert.Equal(LaunchIconPolicy.MinVisibleMs - 120, decision.DelayMs);
    }

    [Fact]
    public void Negative_elapsed_is_clamped_to_the_full_minimum()
    {
        var policy = new LaunchIconPolicy();

        var decision = policy.Ready(elapsedMs: -50);

        Assert.Equal(LaunchIconOutcome.FadeAfter, decision.Outcome);
        Assert.Equal(LaunchIconPolicy.MinVisibleMs, decision.DelayMs);
    }

    [Fact]
    public void Second_ready_is_ignored()
    {
        var policy = new LaunchIconPolicy();
        policy.Ready(elapsedMs: 50);

        var decision = policy.Ready(elapsedMs: 60);

        Assert.Equal(LaunchIconOutcome.Ignore, decision.Outcome);
    }
}
