using System;
using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests.Panes;

public sealed class UndoPolicyTests
{
    [Fact]
    public void Default_IsEnabledWithUpstreamFiveSeconds()
    {
        Assert.True(UndoPolicy.Default.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(5), UndoPolicy.Default.Window);
    }

    [Fact]
    public void Disabled_IsNotEnabled()
    {
        Assert.False(UndoPolicy.Disabled.Enabled);
    }

    [Theory]
    [InlineData(5000)]  // the upstream default, read back verbatim
    [InlineData(1)]     // smallest honored value
    [InlineData(30000)] // a user-raised window
    public void FromConfigMilliseconds_Positive_EnabledWithThatWindow(int ms)
    {
        var policy = UndoPolicy.FromConfigMilliseconds(ms);
        Assert.True(policy.Enabled);
        Assert.Equal(TimeSpan.FromMilliseconds(ms), policy.Window);
    }

    [Fact]
    public void FromConfigMilliseconds_Zero_Disables()
    {
        // Faithful to upstream: `undo-timeout = 0` disables undo entirely.
        var policy = UndoPolicy.FromConfigMilliseconds(0);
        Assert.False(policy.Enabled);
        Assert.Equal(UndoPolicy.Disabled, policy);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void FromConfigMilliseconds_Negative_FallsBackToDefault(int ms)
    {
        // Negative is unreachable from a valid (unsigned, clamped) Duration;
        // a corrupt/defensive value defaults to the safe 5s rather than
        // silently disabling undo.
        Assert.Equal(UndoPolicy.Default, UndoPolicy.FromConfigMilliseconds(ms));
    }
}
