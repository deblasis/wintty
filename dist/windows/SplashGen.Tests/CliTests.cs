using Xunit;

namespace Ghostty.SplashGen.Tests;

public class CliTests
{
    [Fact]
    public void SeedAndOutputAreEnough()
    {
        var options = Cli.Parse(["--seed", "17", "--out", @"C:\sheet.png"]);

        Assert.Equal(17, options.Seed);
        Assert.Equal(@"C:\sheet.png", options.OutputPath);
        Assert.Equal(Cli.DefaultSheetPixels, options.SheetPixels);
        Assert.Equal(Cli.DefaultGridCells, options.GridCells);
        Assert.Null(options.MotifDirectory);
    }

    [Fact]
    public void MissingSeedIsRejected()
        => Assert.Throws<ArgumentException>(() => Cli.Parse(["--out", "sheet.png"]));

    [Fact]
    public void MissingOutputIsRejected()
        => Assert.Throws<ArgumentException>(() => Cli.Parse(["--seed", "1"]));

    /// <summary>
    /// A misspelled option has to be an error. Silently ignoring one would
    /// write a sheet from a command line that says it wrote a different
    /// one, and the sheet is an asset somebody commits.
    /// </summary>
    [Fact]
    public void UnknownArgumentsAreRejected()
        => Assert.Throws<ArgumentException>(
            () => Cli.Parse(["--seed", "1", "--out", "sheet.png", "--grd", "6"]));

    [Theory]
    [InlineData("--seed", "not-a-number")]
    [InlineData("--size", "0")]
    [InlineData("--grid", "-1")]
    public void BadValuesAreRejected(string option, string value)
        => Assert.Throws<ArgumentException>(
            () => Cli.Parse(["--seed", "1", "--out", "sheet.png", option, value]));

    [Fact]
    public void TrailingOptionWithoutAValueIsRejected()
        => Assert.Throws<ArgumentException>(
            () => Cli.Parse(["--seed", "1", "--out", "sheet.png", "--grid"]));
}
