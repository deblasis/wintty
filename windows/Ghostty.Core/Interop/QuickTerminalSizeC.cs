using System.Runtime.InteropServices;
using Ghostty.Core.Hosting;

namespace Ghostty.Core.Interop;

// Mirrors the ghostty_qt_size_s C ABI from
// src/config/Config.zig:9484-9517.
//
// Layout:
//   { tag: c_int; value: union { f32, u32 } }   per axis = 8 bytes
//   { primary: per-axis; secondary: per-axis }  total    = 16 bytes
//
// QuickTerminalSizeCLayoutTests in Ghostty.Tests pins size +
// offsets at build time; an upstream layout drift will trip
// the FFI suite there before runtime.

internal enum QuickTerminalSizeTag : int
{
    None = 0,
    Percentage = 1,
    Pixels = 2,
}

[StructLayout(LayoutKind.Explicit)]
internal struct QuickTerminalSizeValueC
{
    [FieldOffset(0)] public float Percentage;
    [FieldOffset(0)] public uint Pixels;
}

[StructLayout(LayoutKind.Sequential)]
internal struct QuickTerminalSizeOneC
{
    public QuickTerminalSizeTag Tag;
    public QuickTerminalSizeValueC Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct QuickTerminalSizeC
{
    public QuickTerminalSizeOneC Primary;
    public QuickTerminalSizeOneC Secondary;

    public QuickTerminalSize ToManaged() => new(
        ToDimension(Primary),
        ToDimension(Secondary));

    private static Dimension? ToDimension(QuickTerminalSizeOneC one) => one.Tag switch
    {
        QuickTerminalSizeTag.Percentage => Dimension.Percentage(one.Value.Percentage),
        QuickTerminalSizeTag.Pixels     => Dimension.Pixels(one.Value.Pixels),
        _                                => null,
    };
}
