const std = @import("std");
const com = @import("com.zig");
const dxgi = @import("dxgi.zig");

// Verify struct sizes match the C ABI (these are extern structs that
// cross the COM boundary, so size mismatches cause runtime crashes).

test "DXGI_SWAP_CHAIN_DESC1 size" {
    // DXGI_SWAP_CHAIN_DESC1 is 48 bytes on 64-bit Windows.
    try std.testing.expectEqual(@sizeOf(dxgi.DXGI_SWAP_CHAIN_DESC1), 48);
}

test "DXGI_SAMPLE_DESC size" {
    try std.testing.expectEqual(@sizeOf(dxgi.DXGI_SAMPLE_DESC), 8);
}

// Verify vtable pointer layout - COM objects are a single pointer to a vtable.

test "IDXGIDevice is a single vtable pointer" {
    try std.testing.expectEqual(@sizeOf(dxgi.IDXGIDevice), @sizeOf(*anyopaque));
}

test "IDXGISwapChain1 is a single vtable pointer" {
    try std.testing.expectEqual(@sizeOf(dxgi.IDXGISwapChain1), @sizeOf(*anyopaque));
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

test "Buffer type instantiation compiles" {
    const buffer_mod = @import("buffer.zig");
    _ = buffer_mod.Buffer(f32);
    _ = buffer_mod.Buffer(extern struct { x: f32, y: f32 });
    _ = buffer_mod.Buffer(u8);
}
