const std = @import("std");
const com = @import("com.zig");
const dxgi = @import("dxgi.zig");
const d3d11 = @import("d3d11.zig");

// Verify COM GUID byte layout matches the Windows SDK definitions.
// GUIDs are stored as { u32, u16, u16, [8]u8 } in little-endian.

test "IUnknown GUID has no definition" {
    // IUnknown doesn't define its own IID in our bindings (it's accessed
    // via derived interfaces), so verify the base struct layout instead.
    try std.testing.expectEqual(@sizeOf(com.GUID), 16);
    try std.testing.expectEqual(@alignOf(com.GUID), 4);
}

test "HRESULT helpers" {
    try std.testing.expect(com.SUCCEEDED(com.S_OK));
    try std.testing.expect(!com.FAILED(com.S_OK));
    try std.testing.expect(com.FAILED(com.E_FAIL));
    try std.testing.expect(!com.SUCCEEDED(com.E_FAIL));
    try std.testing.expect(com.FAILED(com.E_NOINTERFACE));
    try std.testing.expect(com.FAILED(com.DXGI_ERROR_DEVICE_REMOVED));
}

test "Reserved type is function pointer sized" {
    try std.testing.expectEqual(@sizeOf(com.Reserved), @sizeOf(*anyopaque));
}

// Verify struct sizes match the C ABI (these are extern structs that
// cross the COM boundary, so size mismatches cause runtime crashes).

test "DXGI_SWAP_CHAIN_DESC1 size" {
    // DXGI_SWAP_CHAIN_DESC1 is 48 bytes on 64-bit Windows.
    try std.testing.expectEqual(@sizeOf(dxgi.DXGI_SWAP_CHAIN_DESC1), 48);
}

test "DXGI_SAMPLE_DESC size" {
    try std.testing.expectEqual(@sizeOf(dxgi.DXGI_SAMPLE_DESC), 8);
}

test "D3D11_VIEWPORT size" {
    // D3D11_VIEWPORT is 6 floats = 24 bytes.
    try std.testing.expectEqual(@sizeOf(d3d11.D3D11_VIEWPORT), 24);
}

test "D3D11_BUFFER_DESC size" {
    // D3D11_BUFFER_DESC is 24 bytes (6 u32 fields).
    try std.testing.expectEqual(@sizeOf(d3d11.D3D11_BUFFER_DESC), 24);
}

test "D3D11_INPUT_ELEMENT_DESC size" {
    // D3D11_INPUT_ELEMENT_DESC: ptr + u32 + enum + enum + u32 + u32 + enum = 32 bytes on 64-bit.
    const expected: usize = if (@sizeOf(*anyopaque) == 8) 32 else 24;
    try std.testing.expectEqual(@sizeOf(d3d11.D3D11_INPUT_ELEMENT_DESC), expected);
}

test "D3D11_MAPPED_SUBRESOURCE size" {
    // D3D11_MAPPED_SUBRESOURCE: ptr + u32 + u32.
    const expected: usize = if (@sizeOf(*anyopaque) == 8) 16 else 12;
    try std.testing.expectEqual(@sizeOf(d3d11.D3D11_MAPPED_SUBRESOURCE), expected);
}

test "D3D11_TEXTURE2D_DESC size" {
    // D3D11_TEXTURE2D_DESC: 4 u32s + DXGI_FORMAT(u32) + DXGI_SAMPLE_DESC(8) + D3D11_USAGE(u32) + 3 u32s = 44 bytes.
    try std.testing.expectEqual(@sizeOf(d3d11.D3D11_TEXTURE2D_DESC), 44);
}

test "D3D11_SHADER_RESOURCE_VIEW_DESC size" {
    // Format(4) + ViewDimension(4) + union(16) = 24 bytes.
    try std.testing.expectEqual(@sizeOf(d3d11.D3D11_SHADER_RESOURCE_VIEW_DESC), 24);
}

test "D3D11_SAMPLER_DESC size" {
    // Filter(4) + 3 AddressMode(4 each) + MipLODBias(4) + MaxAnisotropy(4) + ComparisonFunc(4) + BorderColor(16) + MinLOD(4) + MaxLOD(4) = 52 bytes.
    try std.testing.expectEqual(@sizeOf(d3d11.D3D11_SAMPLER_DESC), 52);
}

test "D3D11_BOX size" {
    // 6 u32s = 24 bytes.
    try std.testing.expectEqual(@sizeOf(d3d11.D3D11_BOX), 24);
}

test "D3D11_BLEND_DESC size" {
    // AlphaToCoverageEnable(4) + IndependentBlendEnable(4) + 8 * D3D11_RENDER_TARGET_BLEND_DESC(32) = 264 bytes.
    try std.testing.expectEqual(@sizeOf(d3d11.D3D11_BLEND_DESC), 264);
}

test "D3D11_RENDER_TARGET_BLEND_DESC size" {
    // BlendEnable(4) + SrcBlend(4) + DestBlend(4) + BlendOp(4) + SrcBlendAlpha(4)
    // + DestBlendAlpha(4) + BlendOpAlpha(4) + RenderTargetWriteMask(1) + padding(3) = 32 bytes.
    try std.testing.expectEqual(@sizeOf(d3d11.D3D11_RENDER_TARGET_BLEND_DESC), 32);
}

// Verify vtable pointer layout - COM objects are a single pointer to a vtable.

test "IDXGIDevice is a single vtable pointer" {
    try std.testing.expectEqual(@sizeOf(dxgi.IDXGIDevice), @sizeOf(*anyopaque));
}

test "IDXGISwapChain1 is a single vtable pointer" {
    try std.testing.expectEqual(@sizeOf(dxgi.IDXGISwapChain1), @sizeOf(*anyopaque));
}

test "ID3D11Device is a single vtable pointer" {
    try std.testing.expectEqual(@sizeOf(d3d11.ID3D11Device), @sizeOf(*anyopaque));
}

test "ID3D11DeviceContext is a single vtable pointer" {
    try std.testing.expectEqual(@sizeOf(d3d11.ID3D11DeviceContext), @sizeOf(*anyopaque));
}

// Verify GUID constants are the right values (cross-referenced with
// Windows SDK headers).

test "IDXGIDevice IID" {
    const iid = dxgi.IDXGIDevice.IID;
    try std.testing.expectEqual(iid.data1, 0x54ec77fa);
    try std.testing.expectEqual(iid.data2, 0x1377);
    try std.testing.expectEqual(iid.data3, 0x44e6);
    try std.testing.expectEqualSlices(u8, &iid.data4, &[_]u8{ 0x8c, 0x32, 0x88, 0xfd, 0x5f, 0x44, 0xc8, 0x4c });
}

test "IDXGIFactory2 IID" {
    const iid = dxgi.IDXGIFactory2.IID;
    try std.testing.expectEqual(iid.data1, 0x50c83a1c);
    try std.testing.expectEqual(iid.data2, 0xe072);
    try std.testing.expectEqual(iid.data3, 0x4c48);
    try std.testing.expectEqualSlices(u8, &iid.data4, &[_]u8{ 0x87, 0xb0, 0x36, 0x30, 0xfa, 0x36, 0xa6, 0xd0 });
}

test "ISwapChainPanelNative IID" {
    const iid = dxgi.ISwapChainPanelNative.IID;
    try std.testing.expectEqual(iid.data1, 0xf92f19d2);
    try std.testing.expectEqual(iid.data2, 0x3ade);
    try std.testing.expectEqual(iid.data3, 0x45a6);
    try std.testing.expectEqualSlices(u8, &iid.data4, &[_]u8{ 0xa2, 0x0c, 0xf6, 0xf1, 0xea, 0x90, 0x55, 0x4b });
}

test "ID3D11Texture2D IID" {
    const iid = d3d11.ID3D11Texture2D.IID;
    try std.testing.expectEqual(iid.data1, 0x6f15aaf2);
    try std.testing.expectEqual(iid.data2, 0xd208);
    try std.testing.expectEqual(iid.data3, 0x4e89);
}

test "Buffer Options has device and context fields" {
    const BufferOpts = @import("buffer.zig").Options;
    try std.testing.expect(@hasField(BufferOpts, "device"));
    try std.testing.expect(@hasField(BufferOpts, "context"));
    try std.testing.expect(@hasField(BufferOpts, "bind_flags"));
}

test "Buffer type instantiation compiles" {
    const buffer_mod = @import("buffer.zig");
    _ = buffer_mod.Buffer(f32);
    _ = buffer_mod.Buffer(extern struct { x: f32, y: f32 });
    _ = buffer_mod.Buffer(u8);
}

test "Texture Options has expected fields" {
    const Texture = @import("Texture.zig");
    try std.testing.expect(@hasField(Texture.Options, "device"));
    try std.testing.expect(@hasField(Texture.Options, "context"));
    try std.testing.expect(@hasField(Texture.Options, "format"));
}

test "Sampler Options has device field" {
    const Sampler = @import("Sampler.zig");
    try std.testing.expect(@hasField(Sampler.Options, "device"));
}
