using System.Collections.Generic;
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
