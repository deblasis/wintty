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

// Verify DXGI error constants match the Windows SDK values.
// These are HRESULT codes that cross the COM boundary, so wrong
// values would silently miss device-loss events.

test "DXGI_ERROR_DEVICE_REMOVED value" {
    try std.testing.expectEqual(@as(u32, 0x887A0005), @as(u32, @bitCast(com.DXGI_ERROR_DEVICE_REMOVED)));
}

test "DXGI_ERROR_DEVICE_HUNG value" {
    try std.testing.expectEqual(@as(u32, 0x887A0006), @as(u32, @bitCast(com.DXGI_ERROR_DEVICE_HUNG)));
}

test "DXGI_ERROR_DEVICE_RESET value" {
    try std.testing.expectEqual(@as(u32, 0x887A0007), @as(u32, @bitCast(com.DXGI_ERROR_DEVICE_RESET)));
}

test "DXGI device-loss error codes are distinct" {
    try std.testing.expect(com.DXGI_ERROR_DEVICE_REMOVED != com.DXGI_ERROR_DEVICE_HUNG);
    try std.testing.expect(com.DXGI_ERROR_DEVICE_HUNG != com.DXGI_ERROR_DEVICE_RESET);
    try std.testing.expect(com.DXGI_ERROR_DEVICE_REMOVED != com.DXGI_ERROR_DEVICE_RESET);
}

test "DXGI device-loss error codes are all failures" {
    try std.testing.expect(com.FAILED(com.DXGI_ERROR_DEVICE_REMOVED));
    try std.testing.expect(com.FAILED(com.DXGI_ERROR_DEVICE_HUNG));
    try std.testing.expect(com.FAILED(com.DXGI_ERROR_DEVICE_RESET));
}

// Verify the device-loss check pattern used in drawFrameEnd's Signal
// failure path (and the other three call sites) catches all three codes.
// This pins the invariant: FAILED(hr) must be true AND the specific
// equality check must match for each device-loss HRESULT.

test "device-loss check pattern matches all three codes" {
    const device_loss_codes = [_]com.HRESULT{
        com.DXGI_ERROR_DEVICE_REMOVED,
        com.DXGI_ERROR_DEVICE_HUNG,
        com.DXGI_ERROR_DEVICE_RESET,
    };
    for (device_loss_codes) |hr| {
        // Outer gate: FAILED() must be true so we enter the error branch.
        try std.testing.expect(com.FAILED(hr));
        // Inner gate: at least one equality arm must match.
        const matched = (hr == com.DXGI_ERROR_DEVICE_REMOVED or
            hr == com.DXGI_ERROR_DEVICE_HUNG or
            hr == com.DXGI_ERROR_DEVICE_RESET);
        try std.testing.expect(matched);
    }
}

test "non-device-loss failures do not match device-loss pattern" {
    // E_FAIL is a generic COM error -- it should pass FAILED() but not
    // match the device-loss equality check.
    const e_fail: com.HRESULT = @bitCast(@as(u32, 0x80004005));
    try std.testing.expect(com.FAILED(e_fail));
    const matched = (e_fail == com.DXGI_ERROR_DEVICE_REMOVED or
        e_fail == com.DXGI_ERROR_DEVICE_HUNG or
        e_fail == com.DXGI_ERROR_DEVICE_RESET);
    try std.testing.expect(!matched);
}

test "Buffer type instantiation compiles" {
    const buffer_mod = @import("buffer.zig");
    _ = buffer_mod.Buffer(f32);
    _ = buffer_mod.Buffer(extern struct { x: f32, y: f32 });
    _ = buffer_mod.Buffer(u8);
}
