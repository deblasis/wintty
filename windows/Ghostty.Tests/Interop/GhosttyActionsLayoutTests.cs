using System.Runtime.InteropServices;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Interop;

// Pins ghostty_action_* ordinals and struct layouts (FFI ABI with include/ghostty.h).
public class GhosttyActionsLayoutTests
{
    // int (not enum) parameter: xUnit needs public test class, internal enum can't leak.
    [Theory]
    [InlineData((int)GhosttyActionTag.Scrollbar, 26)]
    [InlineData((int)GhosttyActionTag.SetTitle, 32)]
    [InlineData((int)GhosttyActionTag.CloseWindow, 49)]
    [InlineData((int)GhosttyActionTag.RingBell, 50)]
    [InlineData((int)GhosttyActionTag.ProgressReport, 56)]
    public void ActionTag_Ordinal_Matches_Upstream(int tag, int expected)
    {
        Assert.Equal(expected, tag);
    }

    [Theory]
    [InlineData((int)GhosttyProgressState.Remove, 0)]
    [InlineData((int)GhosttyProgressState.Set, 1)]
    [InlineData((int)GhosttyProgressState.Error, 2)]
    [InlineData((int)GhosttyProgressState.Indeterminate, 3)]
    [InlineData((int)GhosttyProgressState.Pause, 4)]
    public void ProgressState_Ordinal_Matches_Upstream(int state, int expected)
    {
        Assert.Equal(expected, state);
    }

    [Fact]
    public void ScrollbarStruct_Size_Is_24_Bytes()
    {
        // { uint64 total; uint64 offset; uint64 len; } -> 3 * 8 = 24
        Assert.Equal(24, Marshal.SizeOf<GhosttyActionScrollbar>());
    }

    [Fact]
    public void ScrollbarStruct_Field_Offsets_Match_C_Layout()
    {
        // GhosttyHost reads this struct at (actionPtr + 8) via
        // Unsafe.ReadUnaligned, so the three fields MUST sit at
        // +0/+8/+16 within the struct itself.
        Assert.Equal(0,  (int)Marshal.OffsetOf<GhosttyActionScrollbar>(nameof(GhosttyActionScrollbar.Total)));
        Assert.Equal(8,  (int)Marshal.OffsetOf<GhosttyActionScrollbar>(nameof(GhosttyActionScrollbar.Offset)));
        Assert.Equal(16, (int)Marshal.OffsetOf<GhosttyActionScrollbar>(nameof(GhosttyActionScrollbar.Len)));
    }

    [Fact]
    public void ProgressReportStruct_Size_Is_8_Bytes()
    {
        // { int32 state; sbyte progress; } + 3 bytes of trailing
        // alignment padding on x64 -> 8. Pinning total size catches
        // a future field reorder that only shuffles trailing padding.
        Assert.Equal(8, Marshal.SizeOf<GhosttyActionProgressReport>());
    }

    [Fact]
    public void ProgressReportStruct_Field_Offsets_Match_C_Layout()
    {
        // ghostty_action_progress_report_s is read at +8/+12 inside
        // the action union. The struct itself sits at +0/+4 with the
        // sbyte right after the int32 (no packing tricks on x64).
        Assert.Equal(0, (int)Marshal.OffsetOf<GhosttyActionProgressReport>(nameof(GhosttyActionProgressReport.State)));
        Assert.Equal(4, (int)Marshal.OffsetOf<GhosttyActionProgressReport>(nameof(GhosttyActionProgressReport.Progress)));
    }
}
