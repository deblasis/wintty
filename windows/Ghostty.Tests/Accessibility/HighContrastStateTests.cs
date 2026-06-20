using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public sealed class HighContrastStateTests
{
    [Theory]
    [InlineData(true, false, true)]   // OS HC on, not opted out -> apply
    [InlineData(true, true, false)]   // OS HC on, opted out -> don't apply
    [InlineData(false, false, false)] // OS HC off -> don't apply
    [InlineData(false, true, false)]  // OS HC off + opted out -> don't apply
    public void ShouldApply_IsOsHighContrastAndNotOptedOut(
        bool osHighContrast, bool userOptOut, bool expected)
    {
        Assert.Equal(expected, HighContrastState.ShouldApply(osHighContrast, userOptOut));
    }
}
