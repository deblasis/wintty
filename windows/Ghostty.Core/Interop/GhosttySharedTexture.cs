using System;
using System.Runtime.InteropServices;

namespace Ghostty.Core.Interop;

// Mirrors the nested `struct { bool enabled; uint32_t width; uint32_t height; }`
// inside ghostty_platform_windows_s. Used when constructing the platform
// config for shared-texture surface mode.
//
// Uses byte instead of bool for Enabled to keep the struct blittable and
// forward-compatible with DisableRuntimeMarshalling / NativeAOT.
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttySharedTextureConfig
{
    public byte Enabled;
    public uint Width;
    public uint Height;

    public bool IsEnabled => Enabled != 0;
}

// Mirrors ghostty_surface_shared_texture_s -- the atomic snapshot returned
// by ghostty_surface_shared_texture(). Field order is ABI-critical.
//
// ResourceHandle and FenceHandle are NT HANDLEs owned by the native side.
// Do NOT call CloseHandle on either of them. The consumer should open its
// own resources via ID3D12Device::OpenSharedHandle and Release() those
// when done; re-open whenever Version changes (resize / device recovery).
[StructLayout(LayoutKind.Sequential)]
internal struct GhosttySharedTextureSnapshot
{
    public IntPtr ResourceHandle;
    public IntPtr FenceHandle;
    public ulong FenceValue;
    public uint Width;
    public uint Height;
    public ulong Version;
}
