using System.Runtime.InteropServices;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Interop;

// Pins ghostty_action_* ordinals and struct layouts (FFI ABI with include/ghostty.h).
public class GhosttyActionsLayoutTests
{
    // int (not enum) parameter: xUnit needs public test class, internal enum can't leak.
    [Theory]
    [InlineData((int)GhosttyActionTag.NewTab, 2)]
    [InlineData((int)GhosttyActionTag.CloseTab, 3)]
    [InlineData((int)GhosttyActionTag.NewSplit, 4)]
    [InlineData((int)GhosttyActionTag.ToggleFullscreen, 7)]
    [InlineData((int)GhosttyActionTag.ToggleQuickTerminal, 10)]
    [InlineData((int)GhosttyActionTag.MoveTab, 14)]
    [InlineData((int)GhosttyActionTag.GotoTab, 15)]
    [InlineData((int)GhosttyActionTag.GotoSplit, 16)]
    [InlineData((int)GhosttyActionTag.ResizeSplit, 18)]
    [InlineData((int)GhosttyActionTag.EqualizeSplits, 19)]
    [InlineData((int)GhosttyActionTag.ToggleSplitZoom, 20)]
    [InlineData((int)GhosttyActionTag.Scrollbar, 26)]
    [InlineData((int)GhosttyActionTag.SetTitle, 32)]
    [InlineData((int)GhosttyActionTag.MouseShape, 36)]
    [InlineData((int)GhosttyActionTag.MouseVisibility, 37)]
    [InlineData((int)GhosttyActionTag.MouseOverLink, 38)]
    [InlineData((int)GhosttyActionTag.DesktopNotification, 31)]
    [InlineData((int)GhosttyActionTag.ShowChildExited, 55)]
    [InlineData((int)GhosttyActionTag.ConfigChange, 48)]
    [InlineData((int)GhosttyActionTag.CloseWindow, 49)]
    [InlineData((int)GhosttyActionTag.RingBell, 50)]
    [InlineData((int)GhosttyActionTag.ProgressReport, 56)]
    [InlineData((int)GhosttyActionTag.StartSearch, 59)]
    [InlineData((int)GhosttyActionTag.EndSearch, 60)]
    [InlineData((int)GhosttyActionTag.SearchTotal, 61)]
    [InlineData((int)GhosttyActionTag.SearchSelected, 62)]
    [InlineData((int)GhosttyActionTag.PromptReady, 65)]
    [InlineData((int)GhosttyActionTag.FirstRender, 66)]
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

    // Probe struct modelling the C ABI of `ghostty_action_s`:
    //   { ghostty_action_tag_e tag; ghostty_action_u action; }
    //
    // The union contains members with pointers (e.g. `const char* title`,
    // `const char* url`), so on x64 its natural alignment is 8 bytes.
    // The tag is `c_int` (4 bytes), and sequential layout inserts 4
    // bytes of padding to align the union to +8. The `long` field below
    // has the same 8-byte alignment as the real union, so its computed
    // offset matches what `OnAction` sees at runtime.
    [StructLayout(LayoutKind.Sequential)]
    private struct GhosttyActionEnvelopeProbe
    {
        public int Tag;
        public long Payload;
    }

    [Fact]
    public void ActionStruct_Payload_Starts_At_Offset_8()
    {
        // Every dispatched case in GhosttyHost.OnAction (SetTitle,
        // Scrollbar, ProgressReport, MouseShape, …) reads payload bytes
        // via Marshal.ReadXxx(actionPtr, 8). This pin catches a future
        // ABI change that shifts the union — e.g. upstream widening tag
        // to int64 (would still be +8 by coincidence) or growing the
        // union's alignment beyond 8 (would push payloads to +16 and
        // silently corrupt every read).
        Assert.Equal(
            8,
            (int)Marshal.OffsetOf<GhosttyActionEnvelopeProbe>(
                nameof(GhosttyActionEnvelopeProbe.Payload)));
    }

    [Fact]
    public void MouseOverLinkStruct_Size_Is_16_Bytes()
    {
        // { const char* url; size_t len; } on x64 = 8 + 8 = 16
        Assert.Equal(16, Marshal.SizeOf<GhosttyActionMouseOverLink>());
    }

    [Fact]
    public void MouseOverLinkStruct_Field_Offsets_Match_C_Layout()
    {
        // Read at (actionPtr + 8); Url at struct offset 0, Len at 8.
        Assert.Equal(0, (int)Marshal.OffsetOf<GhosttyActionMouseOverLink>(nameof(GhosttyActionMouseOverLink.Url)));
        Assert.Equal(8, (int)Marshal.OffsetOf<GhosttyActionMouseOverLink>(nameof(GhosttyActionMouseOverLink.Len)));
    }

    [Fact]
    public void StartSearchStruct_Size_Is_8_Bytes()
    {
        // { const char* needle; } on x64 = 8.
        Assert.Equal(8, Marshal.SizeOf<GhosttyActionStartSearch>());
        Assert.Equal(0, (int)Marshal.OffsetOf<GhosttyActionStartSearch>(nameof(GhosttyActionStartSearch.Needle)));
    }

    [Fact]
    public void SearchTotalStruct_Size_Is_8_Bytes()
    {
        // { ssize_t total; } on x64 = 8.
        Assert.Equal(8, Marshal.SizeOf<GhosttyActionSearchTotal>());
        Assert.Equal(0, (int)Marshal.OffsetOf<GhosttyActionSearchTotal>(nameof(GhosttyActionSearchTotal.Total)));
    }

    [Fact]
    public void SearchSelectedStruct_Size_Is_8_Bytes()
    {
        // { ssize_t selected; } on x64 = 8.
        Assert.Equal(8, Marshal.SizeOf<GhosttyActionSearchSelected>());
        Assert.Equal(0, (int)Marshal.OffsetOf<GhosttyActionSearchSelected>(nameof(GhosttyActionSearchSelected.Selected)));
    }

    [Fact]
    public void ResizeSplitStruct_HasExpectedLayout()
    {
        Assert.Equal(8, Marshal.SizeOf<GhosttyActionResizeSplit>());
        Assert.Equal(0, (int)Marshal.OffsetOf<GhosttyActionResizeSplit>(nameof(GhosttyActionResizeSplit.Amount)));
        Assert.Equal(4, (int)Marshal.OffsetOf<GhosttyActionResizeSplit>(nameof(GhosttyActionResizeSplit.Direction)));
    }

    [Fact]
    public void SplitDirectionEnum_MatchesHeader()
    {
        Assert.Equal(0, (int)GhosttySplitDirection.Right);
        Assert.Equal(1, (int)GhosttySplitDirection.Down);
        Assert.Equal(2, (int)GhosttySplitDirection.Left);
        Assert.Equal(3, (int)GhosttySplitDirection.Up);
    }

    [Fact]
    public void GotoTabSentinels_MatchHeader()
    {
        Assert.Equal(-1, (int)GhosttyGotoTab.Previous);
        Assert.Equal(-2, (int)GhosttyGotoTab.Next);
        Assert.Equal(-3, (int)GhosttyGotoTab.Last);
    }

    [Fact]
    public void GotoSplitEnum_MatchesHeader()
    {
        Assert.Equal(0, (int)GhosttyGotoSplit.Previous);
        Assert.Equal(1, (int)GhosttyGotoSplit.Next);
        Assert.Equal(2, (int)GhosttyGotoSplit.Up);
        Assert.Equal(3, (int)GhosttyGotoSplit.Left);
        Assert.Equal(4, (int)GhosttyGotoSplit.Down);
        Assert.Equal(5, (int)GhosttyGotoSplit.Right);
    }

    [Fact]
    public void ResizeSplitDirectionEnum_MatchesHeader()
    {
        Assert.Equal(0, (int)GhosttyResizeSplitDirection.Up);
        Assert.Equal(1, (int)GhosttyResizeSplitDirection.Down);
        Assert.Equal(2, (int)GhosttyResizeSplitDirection.Left);
        Assert.Equal(3, (int)GhosttyResizeSplitDirection.Right);
    }

    [Fact]
    public void MoveTabStruct_Size_Is_8_Bytes()
    {
        Assert.Equal(8, Marshal.SizeOf<GhosttyActionMoveTab>());
    }

    [Fact]
    public void ChildExitedStruct_Size_Is_16_Bytes()
    {
        // ghostty_surface_message_childexited_s:
        //   { uint32 exit_code; uint64 runtime_ms; }
        // exit_code@0, 4 bytes pad, runtime_ms@8 -> 16 total on x64.
        Assert.Equal(16, Marshal.SizeOf<GhosttyChildExited>());
    }

    [Fact]
    public void ChildExitedStruct_Field_Offsets_Match_C_Layout()
    {
        // GhosttyHost reads this struct at (actionPtr + 8) via
        // Unsafe.ReadUnaligned, so the fields MUST sit at +0/+8.
        Assert.Equal(0, (int)Marshal.OffsetOf<GhosttyChildExited>(nameof(GhosttyChildExited.ExitCode)));
        Assert.Equal(8, (int)Marshal.OffsetOf<GhosttyChildExited>(nameof(GhosttyChildExited.RuntimeMs)));
    }
}
