using System.Runtime.InteropServices;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Interop;

// Pins ghostty_qt_size_s ABI shape (FFI with libghostty).
public class QuickTerminalSizeCLayoutTests
{
    [Fact]
    public void QuickTerminalSizeC_Size_Is_16_Bytes()
    {
        // Two ghostty_qt_size_one_s structs end-to-end.
        Assert.Equal(16, Marshal.SizeOf<QuickTerminalSizeC>());
    }

    [Fact]
    public void QuickTerminalSizeOneC_Size_Is_8_Bytes()
    {
        // c_int tag (4) + union { f32 | u32 } (4).
        Assert.Equal(8, Marshal.SizeOf<QuickTerminalSizeOneC>());
    }

    [Fact]
    public void QuickTerminalSizeC_Primary_Secondary_Offsets()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<QuickTerminalSizeC>(nameof(QuickTerminalSizeC.Primary)));
        Assert.Equal(8, (int)Marshal.OffsetOf<QuickTerminalSizeC>(nameof(QuickTerminalSizeC.Secondary)));
    }

    [Fact]
    public void QuickTerminalSizeOneC_Tag_Value_Offsets()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<QuickTerminalSizeOneC>(nameof(QuickTerminalSizeOneC.Tag)));
        Assert.Equal(4, (int)Marshal.OffsetOf<QuickTerminalSizeOneC>(nameof(QuickTerminalSizeOneC.Value)));
    }

    // int (not enum) parameter: xUnit needs public test class, internal enum
    // can't leak through [InlineData]. Same pattern as ActionTag_Ordinal_*.
    [Theory]
    [InlineData((int)QuickTerminalSizeTag.None, 0)]
    [InlineData((int)QuickTerminalSizeTag.Percentage, 1)]
    [InlineData((int)QuickTerminalSizeTag.Pixels, 2)]
    public void QuickTerminalSizeTag_Ordinal_Matches_Upstream(int tag, int expected)
    {
        Assert.Equal(expected, tag);
    }
}
