using System;
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

    // Compared by name rather than by the enum: Edition is internal, so a
    // public [Theory] signature cannot name it.
    [Theory]
    [InlineData("none", "None")]
    [InlineData("pro", "Pro")]
    [InlineData("enterprise", "Enterprise")]
    [InlineData("legacy", "Legacy")]
    [InlineData("oss", "Oss")]
    public void ParsesEachEdition(string value, string expected)
    {
        var options = Cli.Parse(new[] { "--channel", "stable", "--out", "out", "--edition", value });
        Assert.Equal(expected, options.Edition.ToString());
    }

    // The default is what every build in this repo gets, and Cli.cs states
    // it as a requirement: an invocation with no --edition has to keep
    // producing the unmarked mark it produced before editions existed.
    [Fact]
    public void EditionDefaultsToNoneWhenAbsent()
    {
        var options = Cli.Parse(new[] { "--channel", "stable", "--out", "out" });
        Assert.Equal("None", options.Edition.ToString());
    }

    // A typo must not fall back to the flagship mark: that would ship one
    // flavour's icon under another flavour's name, silently.
    [Fact]
    public void UnknownEditionThrows()
    {
        Assert.Throws<ArgumentException>(() =>
            Cli.Parse(new[] { "--channel", "stable", "--out", "out", "--edition", "platinum" }));
    }

    [Fact]
    public void EditionWithoutValueThrows()
    {
        Assert.Throws<ArgumentException>(() =>
            Cli.Parse(new[] { "--channel", "stable", "--out", "out", "--edition" }));
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
