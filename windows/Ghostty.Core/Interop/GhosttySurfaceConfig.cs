using System;
using System.Runtime.InteropServices;

namespace Ghostty.Core.Interop;

// ghostty_surface_config_s and the platform structs it carries. They live in
// Ghostty.Core rather than beside the P/Invokes in the WinUI project so the
// test project can check them against include/ghostty.h
// (GhosttyStructHeaderParityTests). The same struct is declared three times,
// in the header, in src/apprt/embedded.zig (Surface.Options) and here, and a
// field added in a different place on one side compiles cleanly and reads the
// wrong bytes at run time. The parity test pins this side to the header, and
// a comptime block in embedded.zig pins the Zig side to the same offsets.

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyPlatformWindows
{
    public IntPtr Hwnd;                        // null for composition mode
    public IntPtr SwapChainPanel;              // ISwapChainPanelNative*
    public GhosttySharedTextureConfig SharedTexture;
}

// ghostty_platform_u: explicit layout so all three variants share memory.
// The Windows variant is the widest, so it sets the size.
[StructLayout(LayoutKind.Explicit)]
internal struct GhosttyPlatformUnion
{
    [FieldOffset(0)] public IntPtr MacosNsView;     // macos variant
    [FieldOffset(0)] public IntPtr IosUiView;       // ios variant
    [FieldOffset(0)] public GhosttyPlatformWindows Windows;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttySurfaceConfig
{
    public GhosttyPlatform PlatformTag;
    public GhosttyPlatformUnion Platform;
    public IntPtr Userdata;
    public double ScaleFactor;
    public float FontSize;
    public IntPtr WorkingDirectory; // const char*
    public IntPtr Command;          // const char*
    public IntPtr EnvVars;          // ghostty_env_var_s*
    public UIntPtr EnvVarCount;
    public IntPtr InitialInput;     // const char*
    // C99 _Bool on the C side; byte on the managed side.
    public byte WaitAfterCommand;
    public GhosttySurfaceContext Context;
    // Per-surface custom shader override (const char*). Must stay allocated
    // until the surface is freed, like the other string fields above.
    public IntPtr CustomShader;
    // Initial size in pixels (uint32_t width/height). Both non-zero: the pty
    // starts at the grid this size holds. Zero: a placeholder until the
    // first SurfaceSetSize.
    public uint Width;
    public uint Height;
}
