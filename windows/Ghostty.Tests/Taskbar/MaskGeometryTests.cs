using System;
using Ghostty.Core.Taskbar;
using Xunit;

namespace Ghostty.Tests.Taskbar;

/// <summary>
/// The mask stride decides how long a buffer the attention overlay hands
/// to CreateBitmap. Short by one byte and GDI reads past a pinned array,
/// which is a worse fault than the undefined bits this arithmetic was
/// extracted to replace, so it is checked by running it rather than by
/// reading it.
/// </summary>
public class MaskGeometryTests
{
    // The widths SM_CXSMICON returns at 100/125/150/200 percent, plus the
    // boundaries either side of a WORD where the rounding actually decides
    // something, plus 1 where the byte count is smaller than the alignment.
    [Theory]
    [InlineData(1, 2)]
    [InlineData(8, 2)]
    [InlineData(9, 2)]
    [InlineData(15, 2)]
    [InlineData(16, 2)]
    [InlineData(17, 4)]
    [InlineData(20, 4)]
    [InlineData(24, 4)]
    [InlineData(25, 4)]
    [InlineData(32, 4)]
    [InlineData(33, 6)]
    [InlineData(48, 6)]
    [InlineData(64, 8)]
    public void KnownWidthsGetTheirDocumentedStride(int width, int expected)
        => Assert.Equal(expected, MaskGeometry.WordAlignedStride(width));

    /// <summary>
    /// A non-positive width is refused rather than answered with 0.
    ///
    /// This is the precondition the caller's `if (w &lt;= 0) w = 16;` clamp
    /// satisfies, and it is checked here rather than described in prose,
    /// because a 0 stride makes a zero-length buffer, a zero-length buffer
    /// pins to a null pointer, and a null pointer is exactly the undefined
    /// mask this whole change exists to remove. Losing the clamp has to
    /// fail loudly, not quietly come back round to the original defect.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-16)]
    [InlineData(int.MinValue)]
    public void ANonPositiveWidthIsRefused(int width)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => MaskGeometry.WordAlignedStride(width));

    /// <summary>
    /// The invariant itself, derived a different way from the expression
    /// under test: take the bytes the pixels need and round that up to an
    /// even number. A sweep rather than a spot check, because the failure
    /// this guards against is an off-by-one at one width, not a wholesale
    /// wrong formula.
    /// </summary>
    [Fact]
    public void EveryWidthCoversItsPixelsOnAWordBoundary()
    {
        for (int width = 1; width <= 256; width++)
        {
            int stride = MaskGeometry.WordAlignedStride(width);
            int needed = (width + 7) / 8;

            Assert.True(
                stride >= needed,
                $"width {width}: stride {stride} is shorter than the {needed} bytes its pixels occupy, "
                    + "so GDI would read past the end of the buffer");
            Assert.True(
                stride % 2 == 0,
                $"width {width}: stride {stride} is not on a WORD boundary");
            Assert.True(
                stride == needed + (needed % 2),
                $"width {width}: stride {stride} is not {needed} rounded up to an even byte count");
        }
    }
}
