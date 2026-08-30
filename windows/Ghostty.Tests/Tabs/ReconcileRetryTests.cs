using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The reconcile rebuild's executor. MUXC's item collections refuse writes
/// while one of their own modifications is open, and with virtualized
/// hosts that state spans frames -- so the executor retries the rebuild
/// across dispatcher yields, each attempt re-reading manager truth at run
/// time. Executed here against throwing attempts: only the foreign-frame
/// refusal (0x8000FFFF) is retried, the budget is real, and a landed
/// rebuild reports exactly once.
/// </summary>
public class ReconcileRetryTests
{
    private static COMException ForeignFrame() =>
        new("Cannot complete a collection modification while another modification is in progress.",
            unchecked((int)0x8000FFFF));

    [Fact]
    public void A_clean_write_lands_once_on_the_first_attempt()
    {
        var attempts = 0;
        var landed = 0;
        ReconcileRetry.Rebuild(
            "test rebuild",
            () => attempts++,
            () => landed++,
            _ => { },
            next => next());

        Assert.Equal(1, attempts);
        Assert.Equal(1, landed);
    }

    [Fact]
    public void The_foreign_frame_refusal_is_retried_until_it_lands()
    {
        var attempts = 0;
        var landed = 0;
        var messages = new List<string>();
        ReconcileRetry.Rebuild(
            "test rebuild",
            () =>
            {
                attempts++;
                if (attempts < 3) throw ForeignFrame();
            },
            () => landed++,
            messages.Add,
            next => next());

        Assert.Equal(3, attempts);
        Assert.Equal(1, landed);
        // Two refusals deferred, then the landed trace: the whole story in
        // order.
        Assert.Equal(3, messages.Count);
        Assert.Equal(2, messages.Count(m => m.Contains("deferred", StringComparison.Ordinal)));
        Assert.Equal(1, messages.Count(m => m.Contains("landed on attempt", StringComparison.Ordinal)));
    }

    [Fact]
    public void Each_attempt_re_reads_state_rather_than_carrying_it()
    {
        // The point of the retry: a refused attempt carries nothing. The
        // closure here reads `truth` AT RUN TIME -- the first attempt sees
        // the stale shape and refuses, the caller mutates the truth (what
        // the real manager does between passes), and the second attempt
        // observes the fresh shape.
        var truth = "stale";
        var seen = new List<string>();
        ReconcileRetry.Rebuild(
            "test rebuild",
            () =>
            {
                seen.Add(truth);
                if (seen.Count == 1) throw ForeignFrame();
            },
            () => { },
            _ => { },
            // The yield IS the world moving between attempts: the caller
            // mutates state across it, and the next attempt must observe
            // the fresh shape, never the one it carried in.
            next =>
            {
                truth = "fresh";
                next();
            });

        Assert.Equal(new[] { "stale", "fresh" }, seen);
    }

    [Fact]
    public void The_budget_running_out_propagates_the_refusal()
    {
        var attempts = 0;
        var landed = 0;
        var ex = Assert.Throws<COMException>(() =>
            ReconcileRetry.Rebuild(
                "test rebuild",
                () => { attempts++; throw ForeignFrame(); },
                () => landed++,
                _ => { },
                next => next()));

        Assert.Equal(unchecked((int)0x8000FFFF), ex.HResult);
        Assert.Equal(8, attempts);
        Assert.Equal(0, landed);
    }

    [Fact]
    public void Genuine_skew_is_never_swallowed_or_deferred()
    {
        var attempts = 0;
        var landed = 0;
        var ex = Assert.Throws<COMException>(() =>
            ReconcileRetry.Rebuild(
                "test rebuild",
                () => { attempts++; throw new COMException("genuine", unchecked((int)0x80004005)); },
                () => landed++,
                _ => { },
                next => next()));

        Assert.Equal(unchecked((int)0x80004005), ex.HResult);
        Assert.Equal(1, attempts);
        Assert.Equal(0, landed);
    }
}
