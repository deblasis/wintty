//! Integration tests for DX11 GPU resource types.
//!
//! These tests create a real D3D11 device (headless, no window/swap chain)
//! and exercise Buffer, Texture, and Sampler create/use/destroy cycles.
//! They only run on Windows -- on other platforms they're skipped.
const std = @import("std");
const builtin = @import("builtin");
const d3d11 = @import("d3d11.zig");
const dxgi = @import("dxgi.zig");
const com = @import("com.zig");
const buffer_mod = @import("buffer.zig");
const Texture = @import("Texture.zig");
const Sampler = @import("Sampler.zig");
const Pipeline = @import("Pipeline.zig");
const RenderPass = @import("RenderPass.zig");
const Device = @import("device.zig").Device;

const Buffer = buffer_mod.Buffer;

/// Create a D3D11 device for testing. Returns null on non-Windows or if
/// device creation fails (e.g. no GPU in CI).
fn createTestDevice() ?struct { device: *d3d11.ID3D11Device, context: *d3d11.ID3D11DeviceContext } {
    if (comptime builtin.os.tag != .windows) return null;

    var device: ?*d3d11.ID3D11Device = null;
    var context: ?*d3d11.ID3D11DeviceContext = null;
    const feature_levels = [_]d3d11.D3D_FEATURE_LEVEL{.@"11_0"};

    const hr = d3d11.D3D11CreateDevice(
        null,
        .HARDWARE,
        null,
        d3d11.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        &feature_levels,
        feature_levels.len,
        d3d11.D3D11_SDK_VERSION,
        &device,
        null,
        &context,
    );

    if (com.FAILED(hr)) return null;
    if (device == null or context == null) return null;

    return .{ .device = device.?, .context = context.? };
}

test "Buffer: create, sync, deinit" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    const TestFloat = buffer_mod.Buffer(f32);
    var buf = try TestFloat.init(.{
        .device = dev.device,
        .context = dev.context,
        .usage = .dynamic,
        .bind_flags = d3d11.D3D11_BIND_VERTEX_BUFFER,
    }, 64);
    defer buf.deinit();

    // Sync some data.
    const data = [_]f32{ 1.0, 2.0, 3.0, 4.0 };
    try buf.sync(&data);
}

test "Buffer: sync triggers realloc when data exceeds capacity" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    const TestU32 = buffer_mod.Buffer(u32);
    var buf = try TestU32.init(.{
        .device = dev.device,
        .context = dev.context,
        .usage = .dynamic,
        .bind_flags = d3d11.D3D11_BIND_VERTEX_BUFFER,
    }, 4); // Start small.
    defer buf.deinit();

    // Sync data that exceeds capacity -- should realloc.
    var big_data: [100]u32 = undefined;
    for (&big_data, 0..) |*v, i| v.* = @intCast(i);
    try buf.sync(&big_data);

    // After realloc, capacity should be >= 100.
    try std.testing.expect(buf.len >= 100);
}

test "Buffer: syncFromArrayLists concatenates correctly" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    const TestU32 = buffer_mod.Buffer(u32);
    var buf = try TestU32.init(.{
        .device = dev.device,
        .context = dev.context,
        .usage = .dynamic,
        .bind_flags = d3d11.D3D11_BIND_VERTEX_BUFFER,
    }, 64);
    defer buf.deinit();

    // Create two ArrayLists.
    var list1 = std.ArrayListUnmanaged(u32){};
    defer list1.deinit(std.testing.allocator);
    try list1.appendSlice(std.testing.allocator, &.{ 1, 2, 3 });

    var list2 = std.ArrayListUnmanaged(u32){};
    defer list2.deinit(std.testing.allocator);
    try list2.appendSlice(std.testing.allocator, &.{ 4, 5 });

    const total = try buf.syncFromArrayLists(&.{ list1, list2 });
    try std.testing.expectEqual(total, 5);
}

test "Buffer: map and unmap" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    const TestF32 = buffer_mod.Buffer(f32);
    var buf = try TestF32.init(.{
        .device = dev.device,
        .context = dev.context,
        .usage = .dynamic,
        .bind_flags = d3d11.D3D11_BIND_VERTEX_BUFFER,
    }, 16);
    defer buf.deinit();

    const slice = try buf.map(4);
    slice[0] = 1.0;
    slice[1] = 2.0;
    slice[2] = 3.0;
    slice[3] = 4.0;
    buf.unmap();
}

test "Buffer: resize" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    const TestU8 = buffer_mod.Buffer(u8);
    var buf = try TestU8.init(.{
        .device = dev.device,
        .context = dev.context,
        .usage = .dynamic,
        .bind_flags = d3d11.D3D11_BIND_VERTEX_BUFFER,
    }, 16);
    defer buf.deinit();

    try buf.resize(1024);
    try std.testing.expectEqual(buf.len, 1024);
}

test "Buffer: constant buffer init" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    // 16 bytes = 1 float4, valid constant buffer size.
    const Uniforms = extern struct { x: f32, y: f32, z: f32, w: f32 };
    const TestCB = buffer_mod.Buffer(Uniforms);
    var buf = try TestCB.init(.{
        .device = dev.device,
        .context = dev.context,
        .usage = .dynamic,
        .bind_flags = d3d11.D3D11_BIND_CONSTANT_BUFFER,
    }, 1);
    defer buf.deinit();

    try buf.sync(&.{Uniforms{ .x = 1.0, .y = 2.0, .z = 3.0, .w = 4.0 }});
}

test "Texture: create with initial data and deinit" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    // 4x4 R8_UNORM texture (16 bytes).
    var data: [16]u8 = undefined;
    for (&data, 0..) |*v, i| v.* = @intCast(i);

    const tex = try Texture.init(
        .{
            .device = dev.device,
            .context = dev.context,
            .format = .R8_UNORM,
        },
        4,
        4,
        &data,
    );
    defer tex.deinit();

    try std.testing.expectEqual(tex.width, 4);
    try std.testing.expectEqual(tex.height, 4);
    try std.testing.expectEqual(tex.bpp, 1);
}

test "Texture: create BGRA and replaceRegion" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    // 8x8 B8G8R8A8_UNORM texture (256 bytes).
    const tex = try Texture.init(
        .{
            .device = dev.device,
            .context = dev.context,
            .format = .B8G8R8A8_UNORM,
        },
        8,
        8,
        null, // No initial data.
    );
    defer tex.deinit();

    try std.testing.expectEqual(tex.bpp, 4);

    // Replace a 2x2 sub-region.
    const region_data = [_]u8{0xFF} ** (2 * 2 * 4);
    try tex.replaceRegion(1, 1, 2, 2, &region_data);
}

test "Texture: create without initial data" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    const tex = try Texture.init(
        .{
            .device = dev.device,
            .context = dev.context,
            .format = .R8_UNORM,
        },
        32,
        32,
        null,
    );
    defer tex.deinit();

    try std.testing.expectEqual(tex.width, 32);
    try std.testing.expectEqual(tex.height, 32);
}

test "Sampler: create and deinit" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    const sampler = try Sampler.init(.{
        .device = dev.device,
    });
    defer sampler.deinit();
}

test "Pipeline: default init and deinit" {
    var pipeline = Pipeline{};
    pipeline.deinit();
}

test "Pipeline: deinit on empty pipeline is safe to call" {
    var pipeline = Pipeline{};
    pipeline.deinit();
}

test "RenderPass: begin and complete with no device" {
    var pass = RenderPass.begin(null, null, null, .{ .attachments = &.{} });
    pass.complete();
}

test "RenderPass: step with empty pipeline is no-op" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    var pass = RenderPass.begin(dev.context, dev.device, null, .{ .attachments = &.{} });
    defer pass.complete();

    pass.step(.{
        .pipeline = .{},
        .draw = .{ .type = .triangle, .vertex_count = 3 },
    });
}

test "RenderPass: step with zero instance count is no-op" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    var pass = RenderPass.begin(dev.context, dev.device, null, .{ .attachments = &.{} });
    defer pass.complete();

    pass.step(.{
        .pipeline = .{},
        .draw = .{ .type = .triangle_strip, .vertex_count = 4, .instance_count = 0 },
    });
}

test "BlendState: premultiplied alpha config" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    // Create a blend state the same way Device.init does.
    var blend_desc = d3d11.D3D11_BLEND_DESC{};
    blend_desc.RenderTarget[0] = .{
        .BlendEnable = 1,
        .SrcBlend = .ONE,
        .DestBlend = .INV_SRC_ALPHA,
        .BlendOp = .ADD,
        .SrcBlendAlpha = .ONE,
        .DestBlendAlpha = .INV_SRC_ALPHA,
        .BlendOpAlpha = .ADD,
        .RenderTargetWriteMask = 0x0F,
    };

    var state: ?*d3d11.ID3D11BlendState = null;
    const hr = dev.device.CreateBlendState(&blend_desc, &state);
    try std.testing.expect(!com.FAILED(hr));
    try std.testing.expect(state != null);
    defer _ = state.?.Release();

    // Read back the descriptor and verify the blend factors.
    // Guards against someone changing SrcBlend/DestBlend (PR #75 regression).
    var readback = d3d11.D3D11_BLEND_DESC{};
    state.?.GetDesc(&readback);

    const rt0 = readback.RenderTarget[0];
    try std.testing.expectEqual(rt0.BlendEnable, 1);
    try std.testing.expectEqual(rt0.SrcBlend, .ONE);
    try std.testing.expectEqual(rt0.DestBlend, .INV_SRC_ALPHA);
    try std.testing.expectEqual(rt0.BlendOp, .ADD);
    try std.testing.expectEqual(rt0.SrcBlendAlpha, .ONE);
    try std.testing.expectEqual(rt0.DestBlendAlpha, .INV_SRC_ALPHA);
    try std.testing.expectEqual(rt0.BlendOpAlpha, .ADD);
    try std.testing.expectEqual(rt0.RenderTargetWriteMask, 0x0F);
}

test "SwapChain: HWND surface uses SCALING_NONE" {
    if (comptime builtin.os.tag != .windows) return;

    // Create a hidden window for the swap chain.
    const HWND = dxgi.HWND;
    const HINSTANCE = std.os.windows.HINSTANCE;
    const WNDCLASSEXW = extern struct {
        cbSize: u32 = @sizeOf(@This()),
        style: u32 = 0,
        lpfnWndProc: *const fn (HWND, u32, usize, isize) callconv(.winapi) isize,
        cbClsExtra: i32 = 0,
        cbWndExtra: i32 = 0,
        hInstance: ?HINSTANCE = null,
        hIcon: ?*anyopaque = null,
        hCursor: ?*anyopaque = null,
        hbrBackground: ?*anyopaque = null,
        lpszMenuName: ?[*:0]const u16 = null,
        lpszClassName: [*:0]const u16,
        hIconSm: ?*anyopaque = null,
    };

    const user32 = struct {
        extern "user32" fn RegisterClassExW(*const WNDCLASSEXW) callconv(.winapi) u16;
        extern "user32" fn CreateWindowExW(
            u32, [*:0]const u16, ?[*:0]const u16,
            u32, i32, i32, i32, i32,
            ?HWND, ?*anyopaque, ?HINSTANCE, ?*anyopaque,
        ) callconv(.winapi) ?HWND;
        extern "user32" fn DestroyWindow(HWND) callconv(.winapi) i32;
        fn defWindowProc(_: HWND, _: u32, _: usize, _: isize) callconv(.winapi) isize {
            return 0;
        }
    };

    const class_name = std.unicode.utf8ToUtf16LeStringLiteral("GhosttyTestClass");
    const wc = WNDCLASSEXW{ .lpfnWndProc = user32.defWindowProc, .lpszClassName = class_name };
    _ = user32.RegisterClassExW(&wc);

    const hwnd = user32.CreateWindowExW(
        0, class_name, null, 0, // WS_OVERLAPPED
        0, 0, 100, 100, null, null, null, null,
    ) orelse return; // Skip if window creation fails (e.g. headless CI).
    defer _ = user32.DestroyWindow(hwnd);

    // Init the full Device with an HWND surface.
    var device = Device.init(.{ .hwnd = hwnd }, 100, 100) catch return;
    defer device.deinit();

    // Query the swap chain descriptor and verify scaling.
    // Guards against regression to STRETCH (PR #76).
    var desc: dxgi.DXGI_SWAP_CHAIN_DESC1 = undefined;
    const hr = device.swap_chain.GetDesc1(&desc);
    try std.testing.expect(!com.FAILED(hr));
    try std.testing.expectEqual(desc.Scaling, .NONE);
}
