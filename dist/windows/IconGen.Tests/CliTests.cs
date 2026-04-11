using Xunit;

namespace Ghostty.IconGen.Tests;

public class CliTests
{
    [Fact]
    public void ParsesStableChannelAndOutputDir()
    {
        var options = Cli.Parse(new[] { "--channel", "stable", "--out", "C:\\tmp\\x" });
        Assert.Equal(Channel.Stable, options.Channel);
        Assert.Equal("C:\\tmp\\x", options.OutputDir);
    }

    [Fact]
    public void ParsesNightlyChannel()
    {
        var options = Cli.Parse(new[] { "--channel", "nightly", "--out", "out" });
        Assert.Equal(Channel.Nightly, options.Channel);
    }

    [Fact]
    public void UnknownChannelThrows()
    {
        Assert.Throws<ArgumentException>(
            () => Cli.Parse(new[] { "--channel", "banana", "--out", "out" }));
    }

    [Fact]
    public void MissingOutThrows()
    {
        Assert.Throws<ArgumentException>(
            () => Cli.Parse(new[] { "--channel", "stable" }));
    }
}
