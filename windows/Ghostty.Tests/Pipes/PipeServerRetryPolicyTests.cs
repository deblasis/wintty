using System;
using Ghostty.Core.Pipes;
using Xunit;

namespace Ghostty.Tests.Pipes;

public class PipeServerRetryPolicyTests
{
    [Fact]
    public void ServerCreationFailure_StandsDown_NeverRetries()
    {
        // The disk-fill bug: a server-creation IOException ("All pipe
        // instances are busy") was treated like a client disconnect and
        // retried immediately, busy-looping forever. Creation failure must
        // stand the loop down instead.
        var policy = new PipeServerRetryPolicy();

        var decision = policy.Decide(PipeLoopOutcome.ServerCreationFailed);

        Assert.Equal(PipeLoopDecision.StandDown, decision);
    }

    [Fact]
    public void Cancellation_Stops()
    {
        var policy = new PipeServerRetryPolicy();
        Assert.Equal(PipeLoopDecision.Stop, policy.Decide(PipeLoopOutcome.Cancelled));
    }

    [Fact]
    public void SingleSessionFault_RetriesAfterBackoff()
    {
        var policy = new PipeServerRetryPolicy();
        Assert.Equal(PipeLoopDecision.RetryAfterBackoff, policy.Decide(PipeLoopOutcome.SessionFaulted));
    }

    [Fact]
    public void SessionEnded_RetriesImmediately()
    {
        var policy = new PipeServerRetryPolicy();
        Assert.Equal(PipeLoopDecision.RetryImmediately, policy.Decide(PipeLoopOutcome.SessionEnded));
    }

    [Fact]
    public void ConsecutiveSessionFaults_EventuallyStandDown()
    {
        // Even the in-session error path must be bounded: a pipe that keeps
        // faulting on every reconnect should give up rather than retry (with
        // backoff) forever.
        var policy = new PipeServerRetryPolicy(maxConsecutiveFaults: 3);

        Assert.Equal(PipeLoopDecision.RetryAfterBackoff, policy.Decide(PipeLoopOutcome.SessionFaulted)); // 1
        Assert.Equal(PipeLoopDecision.RetryAfterBackoff, policy.Decide(PipeLoopOutcome.SessionFaulted)); // 2
        Assert.Equal(PipeLoopDecision.StandDown, policy.Decide(PipeLoopOutcome.SessionFaulted));         // 3rd -> give up
    }

    [Fact]
    public void SuccessfulSession_ResetsFaultCount()
    {
        var policy = new PipeServerRetryPolicy(maxConsecutiveFaults: 2);

        Assert.Equal(PipeLoopDecision.RetryAfterBackoff, policy.Decide(PipeLoopOutcome.SessionFaulted)); // fault 1
        Assert.Equal(PipeLoopDecision.RetryImmediately, policy.Decide(PipeLoopOutcome.SessionEnded));    // resets counter
        // Back to fault 1, not 2 -- so this still backs off rather than standing down.
        Assert.Equal(PipeLoopDecision.RetryAfterBackoff, policy.Decide(PipeLoopOutcome.SessionFaulted));
    }

    [Fact]
    public void Backoff_IsPositive()
    {
        var policy = new PipeServerRetryPolicy();
        Assert.True(policy.Backoff > TimeSpan.Zero);
    }
}
