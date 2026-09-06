using Ghostty.Core.Diagnostics;
using Xunit;

namespace Ghostty.Tests.Diagnostics;

public class HangDumpTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("triage")]
    [InlineData("TRIAGE")]
    [InlineData("bogus")]
    public void UnsetOrInvalidLandsOnTriage(string? raw)
    {
        // Allowed-string keys fall back silently; a typo that quietly
        // captured full memory would be the surprising direction.
        Assert.Equal(HangDumpMode.Triage, HangDump.Parse(raw));
    }

    [Theory]
    [InlineData("full")]
    [InlineData("Full")]
    [InlineData("  FULL  ")]
    public void FullIsOptIn(string raw)
    {
        Assert.Equal(HangDumpMode.Full, HangDump.Parse(raw));
    }

    [Fact]
    public void AllowedAndDefaultStayAnchored()
    {
        Assert.Equal(new[] { "triage", "full" }, HangDump.Allowed);
        Assert.Equal("triage", HangDump.Default);
    }

    [Fact]
    public void ConfigValueRoundTripsTheConfigSpellings()
    {
        Assert.Equal("triage", HangDump.ConfigValue(HangDumpMode.Triage));
        Assert.Equal("full", HangDump.ConfigValue(HangDumpMode.Full));
    }
}
