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
    var pass = RenderPass.begin(null, null, .{ .attachments = &.{} });
    pass.complete();
}

test "RenderPass: step with empty pipeline is no-op" {
    const dev = createTestDevice() orelse return;
    defer _ = dev.device.Release();
    defer _ = dev.context.Release();

    var pass = RenderPass.begin(dev.context, dev.device, .{ .attachments = &.{} });
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

    var pass = RenderPass.begin(dev.context, dev.device, .{ .attachments = &.{} });
    defer pass.complete();

    pass.step(.{
        .pipeline = .{},
        .draw = .{ .type = .triangle_strip, .vertex_count = 4, .instance_count = 0 },
    });
}
