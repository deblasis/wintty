using System;
using Ghostty.Core.SingleInstance;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

/// <summary>
/// The role table and the warm rule, without an OS mutex in sight. The
/// election's own theory over the same property lives in the Windows test
/// project; this one covers the half a real election cannot reach, which is
/// what a warm probe answers and what happens when it answers badly.
/// </summary>
public sealed class LaunchSplashDecisionTests
{
    [Theory]
    [InlineData(SingleInstanceRole.Disabled, true)]
    [InlineData(SingleInstanceRole.Primary, true)]
    [InlineData(SingleInstanceRole.Failed, true)]
    [InlineData(SingleInstanceRole.Secondary, false)]
    public void WithNoProbeInstalled_TheAnswerIsTheRoleTableAlone(
        SingleInstanceRole role, bool expected)
    {
        Assert.Equal(expected, LaunchSplashDecision.ShouldShow(role, warmProbe: null));
    }

    // The probe is the only thing that can suppress a splash on a launch that
    // opens a window here, so a probe that says "warm" is a promise this type
    // has to honour exactly: a splash that flashes over an already-warm attach
    // is the defect this seam exists to remove.
    [Fact]
    public void AWarmAnswer_SuppressesTheSplash_OnALaunchThatWouldShowOne()
    {
        Assert.False(LaunchSplashDecision.ShouldShow(
            SingleInstanceRole.Primary, warmProbe: () => true));
        Assert.False(LaunchSplashDecision.ShouldShow(
            SingleInstanceRole.Disabled, warmProbe: () => true));
    }

    [Fact]
    public void AColdAnswer_KeepsTheSplash()
    {
        Assert.True(LaunchSplashDecision.ShouldShow(
            SingleInstanceRole.Primary, warmProbe: () => false));
    }

    // A secondary forwards and exits, and the splash it would put up is
    // full-size, opaque and topmost over the primary's window. No warm answer
    // changes that, because the window being covered was never this process's.
    [Fact]
    public void ASecondary_NeverShowsASplash_EvenWhenNothingIsWarm()
    {
        Assert.False(LaunchSplashDecision.ShouldShow(
            SingleInstanceRole.Secondary, warmProbe: () => false));
    }

    // Fail towards the splash. It comes down on its own once there is real
    // content, so a probe that cannot answer costs a flash; the opposite
    // failure, suppressing a splash behind a probe that throws, uncovers a
    // black window with no way back.
    [Fact]
    public void AThrowingProbe_IsReadAsCold()
    {
        Assert.True(LaunchSplashDecision.ShouldShow(
            SingleInstanceRole.Primary, warmProbe: () => throw new InvalidOperationException()));
    }

    // The probe is asked at most once per launch and the answer is used for
    // that launch alone, so a probe with a side effect cannot be silently
    // promoted into something polled.
    [Fact]
    public void TheProbeIsAskedOncePerDecision()
    {
        var asked = 0;

        LaunchSplashDecision.ShouldShow(
            SingleInstanceRole.Primary, warmProbe: () => { asked++; return false; });

        Assert.Equal(1, asked);
    }
}
