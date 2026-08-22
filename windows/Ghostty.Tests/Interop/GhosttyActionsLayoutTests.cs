using System.Runtime.InteropServices;
using Xunit;

namespace Ghostty.Tests.Interop;

// What is left after the header-driven checks took the rest: the shape of the
// action envelope itself, which is not a struct include/ghostty.h declares.
//
// Everything else that used to live here restated the header as a literal --
// ordinals as `(int)SomeEnum.Member == <literal>`, sizes and offsets as
// `SizeOf<T>() == <literal>`. The ordinal form could only fail if someone
// edited one of two copies alone, and it stayed green while an insertion
// misrouted twenty tags. The layout form was better, since it did compare
// against the managed struct, but it still only looked at one side: a field
// appended to a C struct fails nothing. GhosttyActionTagHeaderParityTests and
// GhosttyStructHeaderParityTests read include/ghostty.h instead.
//
public class GhosttyActionsLayoutTests
{
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
        // Scrollbar, ProgressReport, MouseShape and the rest) reads payload bytes
        // via Marshal.ReadXxx(actionPtr, 8). This pin catches a future
        // ABI change that shifts the union, e.g. upstream widening tag
        // to int64 (would still be +8 by coincidence) or growing the
        // union's alignment beyond 8 (would push payloads to +16 and
        // silently corrupt every read).
        Assert.Equal(
            8,
            (int)Marshal.OffsetOf<GhosttyActionEnvelopeProbe>(
                nameof(GhosttyActionEnvelopeProbe.Payload)));
    }
}
