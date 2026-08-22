using System;

namespace Ghostty.Core.Interop;

// The plain C enums from include/ghostty.h that cross the boundary as ints:
// platform and surface context, clipboard, mouse and key input, colour scheme,
// and the point addressing used by the inspector and selection APIs.
//
// They live in Ghostty.Core rather than beside the P/Invokes in the WinUI
// project for one reason: Ghostty.Tests cannot reference that project, so
// nothing could check them. These are positions in enums upstream edits, and
// an insertion renumbers every later member without breaking a build --
// exactly what shifted twenty action tags and misrouted first_render into the
// custom-shader handler. GhosttyActionTagHeaderParityTests now reads each of
// them out of the header.
//
// GhosttyInputKey, GhosttyPoint and the rest of the P/Invoke surface stay in
// Ghostty/Interop/NativeMethods.cs; only the enums moved.

// Macos and Ios rather than MacOS and IOS: the header spells them
// GHOSTTY_PLATFORM_MACOS and GHOSTTY_PLATFORM_IOS, and the parity check maps a
// managed name to a C one by breaking at capitals. Neither is referenced
// anywhere on Windows; only Windows is.
internal enum GhosttyPlatform
{
    Invalid = 0,
    Macos = 1,
    Ios = 2,
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
}

internal enum GhosttyClipboardRequest
{
    Paste = 0,
    Osc52Read = 1,
    Osc52Write = 2,
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
