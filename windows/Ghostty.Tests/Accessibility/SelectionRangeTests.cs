using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public class SelectionRangeTests
{
    [Fact]
    public void FromOffsets_WithinBounds_IsExact()
    {
        Assert.Equal(new TextSpan(2, 5), SelectionRange.FromOffsets(2, 3, docLength: 10));
    }

    [Fact]
    public void FromOffsets_StartBeyondLength_ClampsToDegenerateAtEnd()
    {
        Assert.Equal(new TextSpan(10, 10), SelectionRange.FromOffsets(99, 4, docLength: 10));
    }

    [Fact]
    public void FromOffsets_LengthOverflowsDoc_ClampsEnd()
    {
        Assert.Equal(new TextSpan(8, 10), SelectionRange.FromOffsets(8, 50, docLength: 10));
    }

    [Fact]
    public void FromOffsets_ZeroLength_IsDegenerate()
    {
        Assert.Equal(new TextSpan(3, 3), SelectionRange.FromOffsets(3, 0, docLength: 10));
    }
}
