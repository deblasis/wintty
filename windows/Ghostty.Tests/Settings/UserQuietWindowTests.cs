using System;
using Ghostty.Core.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// The quiet window that pauses the preview's autoplay while the user
/// types. Every user keystroke re-arms it; autoplay waits until it
/// expires. Time comes from an injected clock so the spans are exact,
/// not sleeps.
/// </summary>
public class UserQuietWindowTests
{
    private static readonly DateTime Start = new(2026, 8, 25, 12, 0, 0);

    [Fact]
    public void NeverArmedMeansExpired()
    {
        // Autoplay must run freely until the first user keystroke.
        var window = new UserQuietWindow(() => Start, TimeSpan.FromSeconds(10));
        Assert.True(window.Expired);
    }

    [Fact]
    public void ArmedHoldsForExactlyTheQuietSpan()
    {
        var now = Start;
        var window = new UserQuietWindow(() => now, TimeSpan.FromSeconds(10));
        window.Arm();
        Assert.False(window.Expired);
        now = Start.Add(TimeSpan.FromSeconds(9.999));
        Assert.False(window.Expired);
        now = Start.Add(TimeSpan.FromSeconds(10));
        Assert.True(window.Expired);
    }

    [Fact]
    public void EveryArmMovesTheDeadlineToNow()
    {
        // Steady typing holds the demo off indefinitely: each keystroke
        // restarts the span rather than accumulating it.
        var now = Start;
        var window = new UserQuietWindow(() => now, TimeSpan.FromSeconds(10));
        window.Arm();
        now = Start.Add(TimeSpan.FromSeconds(5));
        window.Arm();
        now = Start.Add(TimeSpan.FromSeconds(14));
        Assert.False(window.Expired);
        now = Start.Add(TimeSpan.FromSeconds(15));
        Assert.True(window.Expired);
    }
}
