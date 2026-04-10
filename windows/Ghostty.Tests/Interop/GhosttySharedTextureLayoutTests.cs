using System;
using System.Runtime.InteropServices;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Interop;

// Pins the struct layouts of the DX12 shared-texture interop types
// against the C ABI defined in include/ghostty.h.  If the C header
// changes (field reorder, new field, type change), these tests fail
// before a runtime ABI mismatch can cause data corruption.
//
// The Snapshot assertions assume x64 pointer size (IntPtr = 8 bytes).
// On non-x64 runtimes those tests are skipped via early return.
public class GhosttySharedTextureLayoutTests
{
    // -- GhosttySharedTextureConfig -----------------------------------
    // Mirrors the anonymous nested struct inside ghostty_platform_windows_s:
    //   struct { bool enabled; uint32_t width; uint32_t height; }
    // Total: 1 (byte) + 3 (padding) + 4 + 4 = 12 bytes.

    [Fact]
    public void SharedTextureConfig_Size_Is_12_Bytes()
    {
        Assert.Equal(12, Marshal.SizeOf<GhosttySharedTextureConfig>());
    }

    [Fact]
    public void SharedTextureConfig_Field_Offsets_Match_C_Layout()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<GhosttySharedTextureConfig>(
            nameof(GhosttySharedTextureConfig.Enabled)));
        Assert.Equal(4, (int)Marshal.OffsetOf<GhosttySharedTextureConfig>(
            nameof(GhosttySharedTextureConfig.Width)));
        Assert.Equal(8, (int)Marshal.OffsetOf<GhosttySharedTextureConfig>(
            nameof(GhosttySharedTextureConfig.Height)));
    }

    // -- GhosttySharedTextureSnapshot ---------------------------------
    // Mirrors ghostty_surface_shared_texture_s:
    //   void* resource_handle;    offset  0
    //   void* fence_handle;       offset  8
    //   uint64_t fence_value;     offset 16
    //   uint32_t width;           offset 24
    //   uint32_t height;          offset 28
    //   uint64_t version;         offset 32
    //   total = 40 bytes on x64.

    [Fact]
    public void SharedTextureSnapshot_Size_Is_40_Bytes()
    {
        if (IntPtr.Size != 8) return; // x64 only
        Assert.Equal(40, Marshal.SizeOf<GhosttySharedTextureSnapshot>());
    }

    [Fact]
    public void SharedTextureSnapshot_Field_Offsets_Match_C_Layout()
    {
        if (IntPtr.Size != 8) return; // x64 only
        Assert.Equal(0, (int)Marshal.OffsetOf<GhosttySharedTextureSnapshot>(
            nameof(GhosttySharedTextureSnapshot.ResourceHandle)));
        Assert.Equal(8, (int)Marshal.OffsetOf<GhosttySharedTextureSnapshot>(
            nameof(GhosttySharedTextureSnapshot.FenceHandle)));
        Assert.Equal(16, (int)Marshal.OffsetOf<GhosttySharedTextureSnapshot>(
            nameof(GhosttySharedTextureSnapshot.FenceValue)));
        Assert.Equal(24, (int)Marshal.OffsetOf<GhosttySharedTextureSnapshot>(
            nameof(GhosttySharedTextureSnapshot.Width)));
        Assert.Equal(28, (int)Marshal.OffsetOf<GhosttySharedTextureSnapshot>(
            nameof(GhosttySharedTextureSnapshot.Height)));
        Assert.Equal(32, (int)Marshal.OffsetOf<GhosttySharedTextureSnapshot>(
            nameof(GhosttySharedTextureSnapshot.Version)));
    }
}
