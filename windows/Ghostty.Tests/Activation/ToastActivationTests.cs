using System.Collections.Generic;
using Ghostty.Core.Activation;
using Xunit;

namespace Ghostty.Tests.Activation;

public sealed class ToastActivationTests
{
    [Fact]
    public void FromNotificationArguments_ReadsSurface()
    {
        var activation = ToastActivation.FromNotificationArguments(
            new Dictionary<string, string> { ["surface"] = "abc-123" });

        Assert.True(activation.HasSurface);
        Assert.Equal("abc-123", activation.SurfaceKey);
    }

    [Fact]
    public void FromNotificationArguments_IgnoresUnknownKeys()
    {
        var activation = ToastActivation.FromNotificationArguments(
            new Dictionary<string, string>
            {
                ["surface"] = "abc-123",
                ["somethingANewerBuildAdded"] = "whatever",
            });

        Assert.Equal("abc-123", activation.SurfaceKey);
    }

    [Fact]
    public void FromNotificationArguments_NullBag_IsNone()
        => Assert.False(ToastActivation.FromNotificationArguments(null).HasSurface);

    // A toast raised before the argument existed activates the app with an
    // empty bag. That must read as a plain activation, not as a surface named
    // "".
    [Fact]
    public void FromNotificationArguments_MissingKey_IsNone()
    {
        var activation = ToastActivation.FromNotificationArguments(
            new Dictionary<string, string> { ["other"] = "x" });

        Assert.False(activation.HasSurface);
        Assert.Null(activation.SurfaceKey);
    }

    [Fact]
    public void FromNotificationArguments_EmptyValue_IsNone()
    {
        var activation = ToastActivation.FromNotificationArguments(
            new Dictionary<string, string> { ["surface"] = "" });

        Assert.False(activation.HasSurface);
        Assert.Null(activation.SurfaceKey);
    }

    [Fact]
    public void ForwardedArg_RoundTripsThroughArgv()
    {
        var arg = ToastActivation.ForwardedArg("abc-123");
        var activation = ToastActivation.FromForwardedArgs(["wintty.exe", arg]);

        Assert.Equal("abc-123", activation.SurfaceKey);
    }

    [Fact]
    public void FromForwardedArgs_NoMarker_IsNone()
        => Assert.False(ToastActivation
            .FromForwardedArgs(["wintty.exe", "--jumplist-action=new-tab"]).HasSurface);

    [Fact]
    public void FromForwardedArgs_NullArgs_IsNone()
        => Assert.False(ToastActivation.FromForwardedArgs(null).HasSurface);

    [Fact]
    public void FromForwardedArgs_EmptyValue_IsNone()
    {
        var activation = ToastActivation.FromForwardedArgs(["wintty.exe", "--toast-surface="]);

        Assert.False(activation.HasSurface);
        Assert.Null(activation.SurfaceKey);
    }

    [Fact]
    public void FromForwardedArgs_LastMarkerWins()
    {
        var activation = ToastActivation.FromForwardedArgs(
        [
            "wintty.exe",
            ToastActivation.ForwardedArg("typed-by-the-user"),
            ToastActivation.ForwardedArg("appended-by-the-forwarder"),
        ]);

        Assert.Equal("appended-by-the-forwarder", activation.SurfaceKey);
    }
}
