using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

public class KeyNamesTests
{
    [Fact]
    public void Table_HasExpectedCount()
    {
        // Mirrors input.Key (enum c_int) in src/input/key.zig: 176 members as of
        // windows@11976dcda, including the @"fn" keyword member at ordinal 146.
        // If Zig adds/removes a key this fails -> re-extract the table.
        Assert.Equal(176, KeyNames.Count);
    }

    [Theory]
    [InlineData(0, "unidentified")]
    [InlineData(20, "key_a")]
    [InlineData(58, "enter")]
    [InlineData(78, "arrow_up")]
    [InlineData(121, "f1")]
    [InlineData(146, "fn")]
    [InlineData(175, "paste")]
    public void Ordinal_MapsToName(int ordinal, string expected)
    {
        Assert.Equal(expected, KeyNames.NameOf(ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(176)]
    [InlineData(99999)]
    public void OutOfRange_ReturnsNull(int ordinal)
    {
        Assert.Null(KeyNames.NameOf(ordinal));
    }
}
