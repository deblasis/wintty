using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

public class KeyNamesTests
{
    [Fact]
    public void Table_HasExpectedCount()
    {
        Assert.Equal(175, KeyNames.Count);
    }

    [Theory]
    [InlineData(0, "unidentified")]
    [InlineData(20, "key_a")]
    [InlineData(58, "enter")]
    [InlineData(78, "arrow_up")]
    [InlineData(121, "f1")]
    [InlineData(174, "paste")]
    public void Ordinal_MapsToName(int ordinal, string expected)
    {
        Assert.Equal(expected, KeyNames.NameOf(ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(175)]
    [InlineData(99999)]
    public void OutOfRange_ReturnsNull(int ordinal)
    {
        Assert.Null(KeyNames.NameOf(ordinal));
    }
}
