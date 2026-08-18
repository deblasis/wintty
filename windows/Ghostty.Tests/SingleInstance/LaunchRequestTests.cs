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

    // The toast activation rides as one more argv entry, so it survives
    // Serialize/TryParse unchanged and a primary that predates it simply does
    // not recognize the argument.
    //
    // Each case below carries its own positive control: a bare
    // "reads as no activation" assertion would pass just as well against a
    // parser that never recognizes anything.
    [Fact]
    public void RoundTrips_ForwardedToastActivation()
    {
        var argv = ToastActivation.ForwardedArgv(
            ["wintty.exe"], new ToastActivation("abc-123"));
        var req = new LaunchRequest(@"C:\dir", argv);

        Assert.True(LaunchRequest.TryParse(req.Serialize(), out var back));
        Assert.Equal(argv, back!.Args);
        Assert.Equal("abc-123", ToastActivation.FromForwardedArgs(back.Args).SurfaceKey);
    }

    // The other half of the upgrade window: an older secondary forwards argv
    // with no activation in it. The payload must parse and read as a plain
    // launch rather than as a surface nobody can find -- while the same
    // vector WITH a marker still reads as one.
    [Fact]
    public void RoundTrips_WithoutActivation_ReadsAsPlainLaunch()
    {
        string[] plain = ["wintty.exe", "--jumplist-action=new-tab"];
        var req = new LaunchRequest(@"C:\dir", plain);

        Assert.True(LaunchRequest.TryParse(req.Serialize(), out var back));
        Assert.Equal(plain, back!.Args);
        Assert.False(ToastActivation.FromForwardedArgs(back.Args).HasSurface);

        // Positive control over the identical vector.
        var withMarker = new LaunchRequest(
            @"C:\dir", ToastActivation.ForwardedArgv(plain, new ToastActivation("abc-123")));
        Assert.True(LaunchRequest.TryParse(withMarker.Serialize(), out var markedBack));
        Assert.Equal("abc-123", ToastActivation.FromForwardedArgs(markedBack!.Args).SurfaceKey);
    }

    // An argument a future build adds the same way must not cost the launch:
    // it round-trips verbatim, reads as no activation, and does not stop a
    // real marker appended after it from being seen.
    [Fact]
    public void RoundTrips_UnknownArgument_IsIgnoredNotRejected()
    {
        string[] unknown = ["wintty.exe", "--something-a-newer-build-sends=7"];
        var req = new LaunchRequest(@"C:\dir", unknown);

        Assert.True(LaunchRequest.TryParse(req.Serialize(), out var back));
        Assert.Equal(unknown, back!.Args);
        Assert.False(ToastActivation.FromForwardedArgs(back.Args).HasSurface);

        // Positive control: the unknown argument does not shadow a real one.
        var alsoActivated = new LaunchRequest(
            @"C:\dir", ToastActivation.ForwardedArgv(unknown, new ToastActivation("abc-123")));
        Assert.True(LaunchRequest.TryParse(alsoActivated.Serialize(), out var bothBack));
        Assert.Equal(
            ["wintty.exe", "--something-a-newer-build-sends=7", "--toast-surface=abc-123"],
            bothBack!.Args);
        Assert.Equal("abc-123", ToastActivation.FromForwardedArgs(bothBack.Args).SurfaceKey);
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
