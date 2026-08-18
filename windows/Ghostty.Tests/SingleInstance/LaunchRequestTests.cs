using System.Collections.Generic;
using Ghostty.Core.Activation;
using Ghostty.Core.SingleInstance;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

public sealed class LaunchRequestTests
{
    [Fact]
    public void RoundTrips_SimpleArgs()
    {
        var req = new LaunchRequest(@"C:\Users\me\proj", ["wintty", "--flag", "value"]);
        Assert.True(LaunchRequest.TryParse(req.Serialize(), out var back));
        Assert.Equal(req.WorkingDirectory, back!.WorkingDirectory);
        Assert.Equal(req.Args, back.Args);
    }

    [Fact]
    public void RoundTrips_EmptyArgs()
    {
        var req = new LaunchRequest(@"C:\dir", []);
        Assert.True(LaunchRequest.TryParse(req.Serialize(), out var back));
        Assert.Equal(@"C:\dir", back!.WorkingDirectory);
        Assert.Empty(back.Args);
    }

    [Fact]
    public void RoundTrips_ArgsWithNewlinesColonsSpacesUnicode()
    {
        var req = new LaunchRequest(
            "/tmp/some dir:weird\nname",
            ["a b", "x:y", "line1\nline2", "naïve 中文", ""]);
        Assert.True(LaunchRequest.TryParse(req.Serialize(), out var back));
        Assert.Equal(req.WorkingDirectory, back!.WorkingDirectory);
        Assert.Equal(req.Args, back.Args);
    }

    // Defect-4 wire: the toast activation rides as one more argv entry, so it
    // survives Serialize/TryParse unchanged and a primary that predates it
    // simply does not recognize the argument.
    [Fact]
    public void RoundTrips_ForwardedToastActivation()
    {
        var req = new LaunchRequest(
            @"C:\dir",
            ["wintty.exe", ToastActivation.ForwardedArg("abc-123")]);

        Assert.True(LaunchRequest.TryParse(req.Serialize(), out var back));
        Assert.Equal("abc-123", ToastActivation.FromForwardedArgs(back!.Args).SurfaceKey);
    }

    // The other half of the upgrade window: an older secondary forwards argv
    // with no activation in it. The payload must parse and read as a plain
    // launch rather than as a surface nobody can find.
    [Fact]
    public void RoundTrips_WithoutActivation_ReadsAsPlainLaunch()
    {
        var req = new LaunchRequest(@"C:\dir", ["wintty.exe", "--jumplist-action=new-tab"]);

        Assert.True(LaunchRequest.TryParse(req.Serialize(), out var back));
        Assert.False(ToastActivation.FromForwardedArgs(back!.Args).HasSurface);
    }

    // A field a future build adds the same way must not cost the launch: an
    // unrecognized argument round-trips and is ignored, leaving a plain launch.
    [Fact]
    public void RoundTrips_UnknownArgument_IsIgnoredNotRejected()
    {
        var req = new LaunchRequest(@"C:\dir", ["wintty.exe", "--something-a-newer-build-sends=7"]);

        Assert.True(LaunchRequest.TryParse(req.Serialize(), out var back));
        Assert.Equal(req.Args, back!.Args);
        Assert.False(ToastActivation.FromForwardedArgs(back.Args).HasSurface);
    }

    [Theory]
    [InlineData("")]
    [InlineData("V1")]
    [InlineData("V2\n3:abc")]              // wrong version
    [InlineData("V1\n9:ab")]               // declared length exceeds bytes
    [InlineData("V1\n3:abcX")]             // trailing garbage after a field
    [InlineData("V1\nnotanumber:abc")]     // non-numeric length prefix
    [InlineData("V1\n3:cwd")]              // missing arg-count field
    public void TryParse_Malformed_ReturnsFalse(string s)
    {
        Assert.False(LaunchRequest.TryParse(s, out var back));
        Assert.Null(back);
    }
}
