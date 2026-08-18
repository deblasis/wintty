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
    public void ForwardedArgv_RoundTripsThroughArgv()
    {
        var argv = ToastActivation.ForwardedArgv(
            ["wintty.exe"], new ToastActivation("abc-123"));

        Assert.Equal(["wintty.exe", "--toast-surface=abc-123"], argv);
        Assert.Equal("abc-123", ToastActivation.FromForwardedArgs(argv).SurfaceKey);
    }

    [Fact]
    public void ForwardedArgv_NoActivation_AppendsNothing()
    {
        var argv = ToastActivation.ForwardedArgv(
            ["wintty.exe", "--jumplist-action=new-tab"], ToastActivation.None);

        Assert.Equal(["wintty.exe", "--jumplist-action=new-tab"], argv);
        Assert.False(ToastActivation.FromForwardedArgs(argv).HasSurface);
    }

    // The trailing slot is reserved for the forwarder, so a marker the user
    // typed there is dropped: otherwise a command line fabricates a click, the
    // primary focuses nothing, and the real launch is eaten.
    [Fact]
    public void ForwardedArgv_DropsATrailingMarkerTheUserTyped()
    {
        var argv = ToastActivation.ForwardedArgv(
            ["wintty.exe", "-e", "sometool", "--toast-surface=typed"],
            ToastActivation.None);

        Assert.Equal(["wintty.exe", "-e", "sometool"], argv);
        Assert.False(ToastActivation.FromForwardedArgs(argv).HasSurface);
    }

    // Anywhere else it is the user's own argument. It cannot be mistaken for a
    // forwarded one, and deleting it would hand the primary a command line the
    // user never typed.
    [Fact]
    public void ForwardedArgv_KeepsAMarkerThatIsNotTrailing()
    {
        var argv = ToastActivation.ForwardedArgv(
            ["wintty.exe", "--toast-surface=typed", "-e", "sometool"],
            ToastActivation.None);

        Assert.Equal(["wintty.exe", "--toast-surface=typed", "-e", "sometool"], argv);
        Assert.False(ToastActivation.FromForwardedArgs(argv).HasSurface);
    }

    [Fact]
    public void ForwardedArgv_RealActivationReplacesATrailingMarker()
    {
        var argv = ToastActivation.ForwardedArgv(
            ["wintty.exe", "-e", "sometool", "--toast-surface=typed"],
            new ToastActivation("real"));

        Assert.Equal(["wintty.exe", "-e", "sometool", "--toast-surface=real"], argv);
        Assert.Equal("real", ToastActivation.FromForwardedArgs(argv).SurfaceKey);
    }

    // Only the final element counts: that is the one position the forwarder
    // controls, because it appends there after stripping.
    [Fact]
    public void FromForwardedArgs_MarkerNotInFinalPosition_IsIgnored()
    {
        var activation = ToastActivation.FromForwardedArgs(
            ["wintty.exe", "--toast-surface=typed", "-e", "sometool"]);

        Assert.False(activation.HasSurface);
        Assert.Null(activation.SurfaceKey);
    }

    [Fact]
    public void FromForwardedArgs_NoMarker_IsNone()
        => Assert.False(ToastActivation
            .FromForwardedArgs(["wintty.exe", "--jumplist-action=new-tab"]).HasSurface);

    [Fact]
    public void FromForwardedArgs_NullOrEmptyArgs_IsNone()
    {
        Assert.False(ToastActivation.FromForwardedArgs(null).HasSurface);
        Assert.False(ToastActivation.FromForwardedArgs([]).HasSurface);
    }

    [Fact]
    public void FromForwardedArgs_EmptyValue_IsNone()
    {
        var activation = ToastActivation.FromForwardedArgs(["wintty.exe", "--toast-surface="]);

        Assert.False(activation.HasSurface);
        Assert.Null(activation.SurfaceKey);
    }
}
