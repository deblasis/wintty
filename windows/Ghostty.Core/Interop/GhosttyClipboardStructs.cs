using System;
using System.Runtime.InteropServices;

namespace Ghostty.Core.Interop;

// Both this assembly and the WinUI one set [assembly: DisableRuntimeMarshalling],
// under which Marshal.SizeOf / OffsetOf / PtrToStructure / StructureToPtr throw
// NotSupportedException for types declared here. So the structs below are
// declarations that the header-parity test can measure, NOT things production
// code hands to Marshal. Readers and writers use the explicit offsets beside
// each one.
//
// GhosttyClipboardLayout ties the two together: the offsets are asserted
// against these structs in the test assembly (which has no such attribute), and
// the structs are asserted against include/ghostty.h. Neither link alone is
// enough -- offsets agreeing with a managed struct that has itself drifted from
// the header is exactly the failure this pair is here to prevent.

// The clipboard payload structs from include/ghostty.h.
//
// These live here rather than beside the P/Invokes in the WinUI project for
// the same reason the enums do: Ghostty.Tests cannot reference that project,
// so nothing there can be pinned. GhosttyStructHeaderParityTests computes the
// C layout from the header and compares it against these.
//
// That matters more here than anywhere else in the interop surface. A
// clipboard struct that drifts does not fail a build on either side -- both
// halves compile against their own idea of the layout -- and the first symptom
// is a callback reading the wrong bytes.

// ghostty_clipboard_content_s
//   const char *mime;
//   const char *data;
//   size_t len;
//
// `data` is NOT a C string. The header calls the contents binary-safe and
// explicitly not necessarily null-terminated, so `len` is the only correct
// way to bound a read. See ClipboardContentMarshaller.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyClipboardContent
{
    public IntPtr Mime;     // const char*
    public IntPtr Data;     // const char*, binary, bounded by Len
    public UIntPtr Len;
}

// ghostty_clipboard_complete_s
//   const ghostty_clipboard_content_s *contents;
//   size_t contents_len;
//   const char *const *available;
//   size_t available_len;
//   bool confirmed;
//   bool remember;
//
// The payload for ghostty_surface_complete_clipboard_request. `confirmed` and
// `remember` used to be trailing call arguments; folding them in here is what
// makes a denial inexpressible as a completion, hence
// ghostty_surface_deny_clipboard_request.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyClipboardComplete
{
    public IntPtr Contents;         // const ghostty_clipboard_content_s*
    public UIntPtr ContentsLen;
    public IntPtr Available;        // const char* const*
    public UIntPtr AvailableLen;
    public byte Confirmed;
    public byte Remember;
}

// ghostty_clipboard_confirm_s
//   const ghostty_clipboard_content_s *contents;
//   size_t contents_len;
//   const char *const *available;
//   size_t available_len;
//   const char *name;
//   bool can_remember;
//
// What the permission prompt is given: the would-be completion contents plus
// what to tell the user. `name` identifies the requesting party and
// `can_remember` says whether a session grant may be offered.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyClipboardConfirm
{
    public IntPtr Contents;         // const ghostty_clipboard_content_s*
    public UIntPtr ContentsLen;
    public IntPtr Available;        // const char* const*
    public UIntPtr AvailableLen;
    public IntPtr Name;             // const char*
    public byte CanRemember;
}

/// <summary>
/// Byte offsets and sizes for the clipboard structs, for code that cannot use
/// Marshal.OffsetOf. x64/ARM64: every field is pointer-sized and
/// pointer-aligned except the trailing bools, and the total rounds up to 8.
/// </summary>
internal static class GhosttyClipboardLayout
{
    public const int ContentMime = 0;
    public const int ContentData = 8;
    public const int ContentLen = 16;
    public const int ContentSize = 24;

    public const int CompleteContents = 0;
    public const int CompleteContentsLen = 8;
    public const int CompleteAvailable = 16;
    public const int CompleteAvailableLen = 24;
    public const int CompleteConfirmed = 32;
    public const int CompleteRemember = 33;
    public const int CompleteSize = 40;

    public const int ConfirmContents = 0;
    public const int ConfirmContentsLen = 8;
    public const int ConfirmAvailable = 16;
    public const int ConfirmAvailableLen = 24;
    public const int ConfirmName = 32;
    public const int ConfirmCanRemember = 40;
    public const int ConfirmSize = 48;
}
