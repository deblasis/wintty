using System;
using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests.Panes;

public sealed class UndoTimeoutTests
{
    [Fact]
    public void Default_MatchesUpstreamFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), UndoTimeout.Default);
    }

    [Theory]
    [InlineData(5000)]  // the upstream default, read back verbatim
    [InlineData(1)]     // smallest honored value
    [InlineData(30000)] // a user-raised window
    public void FromMilliseconds_PositiveValue_HonoredVerbatim(int ms)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(ms), UndoTimeout.FromMilliseconds(ms));
    }

    [Theory]
    [InlineData(0)]            // upstream's "disable" sentinel; this fork falls back instead
    [InlineData(-1)]
    [InlineData(int.MinValue)] // a wrapped native value can't slip through > 0
    public void FromMilliseconds_NonPositive_FallsBackToDefault(int ms)
    {
        // Intentional divergence from upstream's 0 = disable: a non-positive
        // window is treated as "unset" and resolves to the 5s default rather
        // than a half-disabled state. See UndoTimeout.FromMilliseconds docs.
        Assert.Equal(UndoTimeout.Default, UndoTimeout.FromMilliseconds(ms));
    }
}
