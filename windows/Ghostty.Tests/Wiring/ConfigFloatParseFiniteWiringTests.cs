using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The config boundary's float parse is the pure
/// <c>WindowsOnlyKeyParsers.ParseFloatClamped</c>, not a local
/// TryParse-and-clamp. The distinction is load-bearing: "nan" and "infinity"
/// parse cleanly as floats, NaN survives Math.Clamp by definition, and the
/// value used to flow on to BackdropGround.Estimate, which mixed every
/// channel to NaN and truncated the ground to 0x000000 -- white tab ink over
/// a light desktop, with nothing signalling why. The parser's behaviour is
/// unit-tested on the real function in Core; this pins the routing, because
/// a test project that cannot load the shell assembly can otherwise only
/// hope the call site still points at the safe parser.
/// </summary>
public sealed class ConfigFloatParseFiniteWiringTests
{
    [Fact]
    public void ParseFloat_DelegatesToThePureFiniteSafeParser()
    {
        var body = ShellSource.Load("Services.ConfigService.cs").Method("ParseFloat");

        var call = Assert.Single(body.CallsEndingWith("ParseFloatClamped"));
        Assert.Equal("WindowsOnlyKeyParsers.ParseFloatClamped", call.CalleeText());

        // The old inline arithmetic must not come back alongside the
        // delegation: a TryParse here is the NaN hole reopened one clamp at
        // a time.
        Assert.Empty(body.CallsEndingWith("TryParse"));
    }
}
