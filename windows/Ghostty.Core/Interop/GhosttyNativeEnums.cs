using System;

namespace Ghostty.Core.Interop;

// The plain C enums from include/ghostty.h that cross the boundary as ints:
// platform and surface context, clipboard, mouse and key input, colour scheme,
// and the point addressing used by the inspector and selection APIs.
//
// They live in Ghostty.Core rather than beside the P/Invokes in the WinUI
// project for one reason: Ghostty.Tests cannot reference that project, so
// nothing could check them. These are positions in enums edited elsewhere, and
// an insertion renumbers every later member without breaking a build --
// exactly what shifted twenty action tags and misrouted first_render into the
// custom-shader handler. GhosttyActionTagHeaderParityTests now reads each of
// them out of the header.
//
// GhosttyInputKey, GhosttyPoint and the rest of the P/Invoke surface stay in
// Ghostty/Interop/NativeMethods.cs; only the enums moved.

// Only Windows is ever passed; the rest exist to hold the ordinals.
internal enum GhosttyPlatform
{
    Invalid = 0,
    MacOS = 1,
    IOS = 2,
    Windows = 3,
}

internal enum GhosttySurfaceContext
{
    Window = 0,
    Tab = 1,
    Split = 2,
}

internal enum GhosttyClipboard
{
    Standard = 0,
    Selection = 1,
    Primary = 2,
}

internal enum GhosttyClipboardRequest
{
    Paste = 0,
    Osc52Read = 1,
    Osc52Write = 2,
    KittyRead = 3,
    KittyWrite = 4,
    List = 5,
}

// ghostty_clipboard_read_result_e. The read callback answers with this
// rather than a bool so libghostty can tell "the clipboard held nothing"
// apart from "this runtime cannot serve that request" -- the mode 5522
// report is gated on the difference, so a flat false would advertise the
// wrong capability to every program that asks.
internal enum GhosttyClipboardReadResult
{
    Started = 0,
    Unavailable = 1,
    Unsupported = 2,
}

internal enum GhosttyMouseState
{
    Release = 0,
    Press = 1,
}

internal enum GhosttyMouseButton
{
    Unknown = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
    Four = 4,
    Five = 5,
    Six = 6,
    Seven = 7,
    Eight = 8,
    Nine = 9,
    Ten = 10,
    Eleven = 11,
}

internal enum GhosttyColorScheme
{
    Light = 0,
    Dark = 1,
}

[Flags]
internal enum GhosttyMods
{
    None = 0,
    Shift = 1 << 0,
    Ctrl = 1 << 1,
    Alt = 1 << 2,
    Super = 1 << 3,
    Caps = 1 << 4,
    Num = 1 << 5,
    ShiftRight = 1 << 6,
    CtrlRight = 1 << 7,
    AltRight = 1 << 8,
    SuperRight = 1 << 9,
}

internal enum GhosttyInputAction
{
    Release = 0,
    Press = 1,
    Repeat = 2,
}

// Mirrors ghostty_point_tag_e.
// ghostty_input_trigger_tag_e: which kind of trigger a keybind step carries.
// Four files under Input/ had their own `private const int TagPhysical = 0`
// copies of this; they now derive from here, so the values have one source and
// the header check can see it. An insertion upstream would otherwise decode
// every physical trigger as unicode and render a garbage glyph in the keybind
// editor, with nothing reporting an error.
internal enum GhosttyTriggerTag { Physical = 0, Unicode = 1, CatchAll = 2 }

internal enum GhosttyPointTag
{
    Active = 0,
    Viewport = 1,
    Screen = 2,
    Surface = 3,
}

// Mirrors ghostty_point_coord_e.
internal enum GhosttyPointCoord
{
    Exact = 0,
    TopLeft = 1,
    BottomRight = 2,
}
