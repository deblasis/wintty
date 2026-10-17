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

    // Adversarial argv, pinned to what System.Uri actually does, because the
    // contract is "argv is untrusted input": a value that cannot parse costs
    // nothing, and everything that CAN parse is handed on untouched. Scheme-
    // agnostic acceptance is deliberate: the warm path runs the same scan the
    // cold path runs (Resolve's argv half), and the downstream router is the
    // half that validates the scheme and decodes the action. Making ParseUri
    // scheme-aware here would silently diverge warm from cold.
    public static TheoryData<string, bool, string?> AdversarialValues => new()
    {
        // A NUL in the authority is rejected outright: unparseable, so null
        // and nothing else in the argv is affected.
        { "wintty://upda\0te", false, null },
        // A trailing newline (bare CR/LF or the pair) is FOLDED by System.Uri,
        // not rejected: the URI parses to its clean form. The router's own
        // action decode is the half that would have to care.
        { "wintty://update\r\n", true, "wintty://update/" },
        { "wintty://update\n", true, "wintty://update/" },
        // Percent-encoded control characters in the query are legal
        // percent-encoding and pass through exactly as written.
        { "wintty://update?action=%00%0d%0a", true, "wintty://update/?action=%00%0d%0a" },
        // An empty authority parses; the router sees an empty host and its
        // action decode decides.
        { "wintty://?action=install-restart", true, "wintty:///?action=install-restart" },
        // A foreign scheme is accepted on purpose: warm must match cold, and
        // cold hands any absolute URI the shell registered to the router.
        { "https://example.com/x", true, "https://example.com/x" },
    };

    [Theory]
    [MemberData(nameof(AdversarialValues))]
    public void ParseUri_AdversarialValues_PinTheUntrustedArgvContract(
        string value, bool parses, string? expected)
    {
        var uri = ProtocolLaunch.ParseUri(["wintty.exe", "--uri", value]);

        if (parses)
            Assert.Equal(new Uri(expected!), uri);
        else
            Assert.Null(uri);
    }
}
