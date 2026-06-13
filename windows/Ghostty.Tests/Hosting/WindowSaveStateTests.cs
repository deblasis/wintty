using Ghostty.Core.Hosting;
using Xunit;

namespace Ghostty.Tests.Hosting;

public class WindowSaveStateTests
{
    [Theory]
    [InlineData("default", WindowSaveState.Default)]
    [InlineData("never", WindowSaveState.Never)]
    [InlineData("always", WindowSaveState.Always)]
    [InlineData("DEFAULT", WindowSaveState.Default)]
    [InlineData("  always  ", WindowSaveState.Always)]
    [InlineData("", WindowSaveState.Default)]
    [InlineData(null, WindowSaveState.Default)]
    [InlineData("garbage", WindowSaveState.Default)]
    public void Parse_MapsTagsAndFallsBackToDefault(string? raw, WindowSaveState expected)
    {
        Assert.Equal(expected, WindowSaveStateExtensions.Parse(raw));
    }
}
