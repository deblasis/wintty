using System;
using Ghostty.Core.Activation;
using Xunit;

namespace Ghostty.Tests.Activation;

public sealed class ProtocolLaunchTests
{
    [Fact]
    public void ParseUri_FindsValueAfterFlag()
    {
        var uri = ProtocolLaunch.ParseUri(["wintty.exe", "--uri", "wintty://open/x"]);
        Assert.Equal(new Uri("wintty://open/x"), uri);
    }

    [Fact]
    public void ParseUri_NoFlag_ReturnsNull()
        => Assert.Null(ProtocolLaunch.ParseUri(["wintty.exe", "--jumplist-action=new-tab"]));

    [Fact]
    public void ParseUri_NullArgs_ReturnsNull()
        => Assert.Null(ProtocolLaunch.ParseUri(null));

    [Fact]
    public void ParseUri_FlagWithNothingAfterIt_ReturnsNull()
        => Assert.Null(ProtocolLaunch.ParseUri(["wintty.exe", "--uri"]));

    [Fact]
    public void ParseUri_RelativeValue_IsRejected()
        => Assert.Null(ProtocolLaunch.ParseUri(["wintty.exe", "--uri", "not/absolute"]));

    [Fact]
    public void ParseUri_SkipsUnparseablePair_AndTakesTheNextGoodOne()
    {
        var uri = ProtocolLaunch.ParseUri(
            ["wintty.exe", "--uri", "not/absolute", "--uri", "wintty://second"]);
        Assert.Equal(new Uri("wintty://second"), uri);
    }

    // The rule the restructured probe exists to preserve: argv is a fallback
    // for a launch the packaged path could not describe, never an override.
    [Fact]
    public void Resolve_ProtocolActivation_BeatsArgv()
    {
        var resolved = ProtocolLaunch.Resolve(
            new Uri("wintty://from-winrt"),
            ["wintty.exe", "--uri", "wintty://from-argv"]);
        Assert.Equal(new Uri("wintty://from-winrt"), resolved);
    }

    // Null protocolUri is both "the probe found no protocol activation" and
    // "the probe threw". The scan must cover the second, which is the whole
    // point of hoisting it out of the try block.
    [Fact]
    public void Resolve_NoProtocolActivation_FallsBackToArgv()
    {
        var resolved = ProtocolLaunch.Resolve(
            null, ["wintty.exe", "--uri", "wintty://from-argv"]);
        Assert.Equal(new Uri("wintty://from-argv"), resolved);
    }

    [Fact]
    public void Resolve_NeitherSource_ReturnsNull()
        => Assert.Null(ProtocolLaunch.Resolve(null, ["wintty.exe"]));
}
